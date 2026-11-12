using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    public enum DrawTool { Pen, Line, Eraser }
    public enum DrawSpace { Surface, Air }

    /// <summary>
    /// Turns stylus samples into ink. It knows nothing about any XR SDK: it reads an
    /// <see cref="IStylusInputProvider"/>, runs the board capture rules, builds strokes and
    /// hands them to the history and renderers.
    /// </summary>
    public sealed class DrawingManager : MonoBehaviour
    {
        public CaptureSettings capture = new CaptureSettings();
        public StrokeBuildSettings build = new StrokeBuildSettings();

        public DrawTool tool = DrawTool.Pen;
        public DrawSpace space = DrawSpace.Surface;
        public Color32 color = new Color32(20, 20, 24, 255);
        [Tooltip("Constant stroke width in millimetres.")]
        public float widthMm = 1.5f;
        public float eraserRadiusMm = 8f;
        [Tooltip("Straight-line tool: snap to the board's horizontal/vertical when within this angle. 0 disables.")]
        public float lineSnapDegrees = 4f;
        [Tooltip("Air mode: a move larger than this between frames is treated as a tracking glitch (metres).")]
        public float airMaxJump = 0.15f;

        public const float MinWidthMm = 0.5f;
        public const float MaxWidthMm = 12f;

        IXRDevice _device;
        WhiteboardSurface _board;
        ProceduralStrokeRenderer _boardRenderer;
        ProceduralStrokeRenderer _airRenderer;
        Transform _airRoot;

        readonly SurfaceStrokeCapture _capture = new SurfaceStrokeCapture();
        readonly StrokeBuilder _builder = new StrokeBuilder();
        readonly List<StrokeData> _eraseHits = new List<StrokeData>();
        int _eraseGroup;
        Vector3 _lineStart;

        bool _airDrawing;
        bool _airRearm;
        Vector3 _airLast;
        double _airLastTime;

        MeshRenderer _tipMarker;
        MeshRenderer _boardCursor;

        bool _wasTracked;
        readonly float[] _frameMs = new float[120];
        int _frameIndex;

        public StrokeHistory History { get; } = new StrokeHistory();
        public IStylusInputProvider Provider { get; private set; }

        /// <summary>Set every frame by the app: true while the pointer is on UI or writing is otherwise not wanted.</summary>
        public bool Suppressed { get; set; }
        /// <summary>Diagnostics: report tip-down events but create no ink.</summary>
        public bool CaptureOnly { get; set; }
        /// <summary>Raised when a stroke would begin on the board. Argument: (x, y, height) in board coordinates.</summary>
        public event Action<Vector3> TipDown;

        public StylusSample LastSample { get; private set; }
        public Vector3 LastBoardPoint { get; private set; }
        public bool HasBoardPoint { get; private set; }
        public TipState TipState { get; private set; }
        public CaptureEndReason LastEndReason => _capture.LastEndReason;
        public string Notice { get; private set; } = string.Empty;

        public int DropoutCount { get; private set; }
        public float UntrackedSeconds { get; private set; }

        public void Init(IXRDevice device, WhiteboardSurface board, ProceduralStrokeRenderer boardRenderer,
            ProceduralStrokeRenderer airRenderer, Transform airRoot)
        {
            _device = device;
            _board = board;
            _boardRenderer = boardRenderer;
            _airRenderer = airRenderer;
            _airRoot = airRoot;

            History.VisibilityChanged += s => RendererFor(s).RefreshStroke(s);
            History.Discarded += s => RendererFor(s).RemoveStroke(s);

            _tipMarker = InkAssets.NewRenderer("Tip Marker", transform, InkAssets.Disc, InkAssets.InkMaterial, InkAssets.SortCursor + 1);
            _boardCursor = InkAssets.NewRenderer("Board Cursor", board.transform, InkAssets.Disc, InkAssets.InkMaterial, InkAssets.SortCursor);
            _tipMarker.enabled = false;
            _boardCursor.enabled = false;
        }

        ProceduralStrokeRenderer RendererFor(StrokeData s) => s.space == StrokeSpace.Board ? _boardRenderer : _airRenderer;

        public void SetProvider(IStylusInputProvider provider)
        {
            Interrupt("Input changed");
            Provider = provider;
        }

        /// <summary>
        /// Ends any stroke in progress and requires a deliberate lift / release before inking
        /// resumes. Used for recenter, pause, input or hand switches and board moves.
        /// </summary>
        public void Interrupt(string reason)
        {
            _capture.Interrupt();
            FinishStroke();
            _airDrawing = false;
            _airRearm = true;
            if (!string.IsNullOrEmpty(reason)) Notice = reason;
        }

        public void ResetDiagnostics()
        {
            DropoutCount = 0;
            UntrackedSeconds = 0f;
        }

        public void GetFrameStats(out float averageMs, out float maxMs)
        {
            float sum = 0f, max = 0f;
            for (int i = 0; i < _frameMs.Length; i++)
            {
                sum += _frameMs[i];
                if (_frameMs[i] > max) max = _frameMs[i];
            }
            averageMs = sum / _frameMs.Length;
            maxMs = max;
        }

        public void Undo() { Interrupt(null); History.Undo(); }
        public void Redo() { Interrupt(null); History.Redo(); }

        public int ClearCurrentSpace()
        {
            Interrupt(null);
            return History.Clear(space == DrawSpace.Surface ? StrokeSpace.Board : StrokeSpace.Air);
        }

        /// <summary>Called once per frame by the app, after UI input has been processed.</summary>
        public void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            _frameMs[_frameIndex] = dt * 1000f;
            _frameIndex = (_frameIndex + 1) % _frameMs.Length;

            if (Provider == null)
            {
                TipState = TipState.Untracked;
                HideCursors();
                return;
            }

            Provider.Tick(dt);
            StylusSample s = Provider.GetSample();
            LastSample = s;

            if (!s.tracked)
            {
                UntrackedSeconds += dt;
                if (_wasTracked) DropoutCount++;
            }
            _wasTracked = s.tracked;

            if (space == DrawSpace.Surface) TickSurface(s);
            else TickAir(s);

            UpdateCursors(s);
        }

        // ---------------------------------------------------------------- surface writing

        void TickSurface(StylusSample s)
        {
            HasBoardPoint = false;
            if (_board == null || !_board.Exists)
            {
                TipState = s.tracked ? TipState.Hover : TipState.Untracked;
                return;
            }

            BoardFrame frame = _board.Frame;
            Vector3 bp = default;
            if (s.tracked)
            {
                bp = frame.WorldToBoard(s.tipPosition);
                // Surface-offset calibration: the height this provider reports when the tip
                // physically rests on the surface is subtracted, so 0 means "touching".
                bp.z -= Provider.SurfaceOffsetMm * 0.001f;
                LastBoardPoint = bp;
                HasBoardPoint = true;
            }

            if (!_board.WritingAllowed)
            {
                // Alignment is not confirmed (or became uncertain): never ink.
                if (_capture.Interrupt()) FinishStroke();
                TipState = s.tracked ? TipState.Hover : TipState.Untracked;
                return;
            }

            var input = new CaptureInput
            {
                tracked = s.tracked,
                boardPoint = bp,
                explicitHeld = s.drawHeld,
                time = s.timestamp,
                suppressed = Suppressed,
            };
            CaptureOutput o = _capture.Step(input, frame.size, capture);
            TipState = o.state;

            switch (o.evt)
            {
                case CaptureEvent.Begin:
                    Notice = string.Empty;
                    TipDown?.Invoke(bp);
                    if (CaptureOnly) break;
                    if (tool == DrawTool.Eraser)
                    {
                        _eraseGroup = History.NewEraseGroup();
                        EraseAt(new Vector3(bp.x, bp.y, 0f), StrokeSpace.Board);
                    }
                    else if (!_boardRenderer.HasBudget)
                    {
                        Notice = "Ink budget reached. Erase or clear to keep writing.";
                    }
                    else
                    {
                        BeginStroke(StrokeSpace.Board, bp, s, tool == DrawTool.Line);
                        _device.Haptic(Provider.Hand, 0.15f, 0.01f);
                    }
                    break;

                case CaptureEvent.Continue:
                    if (CaptureOnly) break;
                    if (tool == DrawTool.Eraser)
                    {
                        EraseAt(new Vector3(bp.x, bp.y, 0f), StrokeSpace.Board);
                    }
                    else if (_builder.IsActive)
                    {
                        Vector3 p = _builder.Stroke.isStraightLine ? SnapLine(_lineStart, bp) : bp;
                        bool room = _builder.Add(p, s.timestamp, s.confidence);
                        _boardRenderer.UpdateLive(_builder.Stroke);
                        if (!room)
                        {
                            // Point budget reached: commit and continue seamlessly in a new stroke
                            // from the same, current, valid position.
                            bool line = _builder.Stroke.isStraightLine;
                            FinishStroke();
                            if (_boardRenderer.HasBudget) BeginStroke(StrokeSpace.Board, bp, s, line);
                        }
                    }
                    break;

                case CaptureEvent.End:
                    FinishStroke();
                    if (o.endReason != CaptureEndReason.Released && o.endReason != CaptureEndReason.LeftBounds)
                        Notice = DescribeEnd(o.endReason, s);
                    break;
            }
        }

        static string DescribeEnd(CaptureEndReason r, StylusSample s)
        {
            switch (r)
            {
                case CaptureEndReason.TrackingLost:
                    return "Stroke ended: " + (string.IsNullOrEmpty(s.invalidReason) ? "tracking lost" : s.invalidReason) + ". Lift / release, then continue.";
                case CaptureEndReason.Implausible: return "Stroke ended: tip estimate went far behind the surface. Lift, then continue.";
                case CaptureEndReason.Jump: return "Stroke ended: tracking jumped. Lift / release, then continue.";
                case CaptureEndReason.Stale: return "Stroke ended: frame hitch. Lift / release, then continue.";
                default: return string.Empty;
            }
        }

        Vector3 SnapLine(Vector3 start, Vector3 p)
        {
            if (lineSnapDegrees <= 0f) return p;
            float dx = Mathf.Abs(p.x - start.x), dy = Mathf.Abs(p.y - start.y);
            if (dx < 1e-6f && dy < 1e-6f) return p;
            float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg; // 0 = horizontal, 90 = vertical
            if (angle < lineSnapDegrees) p.y = start.y;
            else if (angle > 90f - lineSnapDegrees) p.x = start.x;
            return p;
        }

        // ---------------------------------------------------------------- air drawing

        void TickAir(StylusSample s)
        {
            HasBoardPoint = false;
            if (!s.tracked)
            {
                EndAir();
                _airRearm = true;
                TipState = TipState.Untracked;
                return;
            }
            if (Suppressed)
            {
                EndAir();
                if (s.drawHeld) _airRearm = true;
                TipState = _airRearm ? TipState.NeedsRearm : TipState.Hover;
                return;
            }
            if (_airRearm)
            {
                if (!s.drawHeld) _airRearm = false;
                TipState = _airRearm ? TipState.NeedsRearm : TipState.Hover;
                return;
            }
            if (!s.drawHeld)
            {
                EndAir();
                TipState = TipState.Hover;
                return;
            }

            Vector3 lp = _airRoot.InverseTransformPoint(s.tipPosition);
            if (!_airDrawing)
            {
                _airDrawing = true;
                Notice = string.Empty;
                if (tool == DrawTool.Eraser)
                {
                    _eraseGroup = History.NewEraseGroup();
                    EraseAt(lp, StrokeSpace.Air);
                }
                else if (!_airRenderer.HasBudget)
                {
                    Notice = "Ink budget reached. Erase or clear to keep drawing.";
                }
                else
                {
                    BeginStroke(StrokeSpace.Air, lp, s, tool == DrawTool.Line);
                    // Face the ribbon toward the viewer at the start of the stroke.
                    if (_device.TryGetHeadPose(out Vector3 head, out _))
                        _builder.Stroke.ribbonNormal = _airRoot.InverseTransformDirection((head - s.tipPosition).normalized);
                }
            }
            else
            {
                bool jump = (lp - _airLast).magnitude > airMaxJump;
                bool stale = s.timestamp - _airLastTime > capture.maxGapSeconds;
                if (jump || stale)
                {
                    EndAir();
                    _airRearm = true;
                    Notice = "Stroke ended: tracking jumped. Release, then continue.";
                    TipState = TipState.NeedsRearm;
                    return;
                }
                if (tool == DrawTool.Eraser)
                {
                    EraseAt(lp, StrokeSpace.Air);
                }
                else if (_builder.IsActive)
                {
                    bool room = _builder.Add(lp, s.timestamp, s.confidence);
                    _airRenderer.UpdateLive(_builder.Stroke);
                    if (!room)
                    {
                        Vector3 normal = _builder.Stroke.ribbonNormal;
                        bool line = _builder.Stroke.isStraightLine;
                        FinishStroke();
                        if (_airRenderer.HasBudget)
                        {
                            BeginStroke(StrokeSpace.Air, lp, s, line);
                            _builder.Stroke.ribbonNormal = normal;
                        }
                    }
                }
            }
            _airLast = lp;
            _airLastTime = s.timestamp;
            TipState = TipState.Drawing;
        }

        void EndAir()
        {
            if (!_airDrawing) return;
            _airDrawing = false;
            FinishStroke();
        }

        // ---------------------------------------------------------------- strokes

        void BeginStroke(StrokeSpace strokeSpace, Vector3 point, StylusSample s, bool line)
        {
            var stroke = new StrokeData
            {
                id = History.NextStrokeId(),
                space = strokeSpace,
                color = color,
                width = Mathf.Clamp(widthMm, MinWidthMm, MaxWidthMm) * 0.001f,
                source = Provider.Source,
            };
            _lineStart = point;
            _builder.Begin(stroke, build, point, s.timestamp, s.confidence, line);
            RendererFor(stroke).UpdateLive(stroke);
        }

        void FinishStroke()
        {
            if (!_builder.IsActive) return;
            StrokeData s = _builder.End();
            ProceduralStrokeRenderer r = RendererFor(s);
            r.ClearLive();
            r.AddStroke(s);
            History.AddStroke(s);
        }

        void EraseAt(Vector3 point, StrokeSpace strokeSpace)
        {
            float radius = eraserRadiusMm * 0.001f * (strokeSpace == StrokeSpace.Air ? 2f : 1f);
            _eraseHits.Clear();
            IReadOnlyList<StrokeData> all = History.Strokes;
            for (int i = 0; i < all.Count; i++)
            {
                StrokeData s = all[i];
                if (s.visible && s.space == strokeSpace && StrokeHitTest.Hits(s, point, radius)) _eraseHits.Add(s);
            }
            if (_eraseHits.Count > 0) History.Erase(_eraseHits, _eraseGroup);
        }

        // ---------------------------------------------------------------- cursors

        void HideCursors()
        {
            if (_tipMarker != null) _tipMarker.enabled = false;
            if (_boardCursor != null) _boardCursor.enabled = false;
        }

        /// <summary>
        /// Two cursors: a marker at the (possibly estimated) tip itself and, in surface mode, a
        /// cursor at its projection onto the board. The gap between the real tip, the marker
        /// and the ink is exactly the alignment error the user can see and correct.
        /// </summary>
        void UpdateCursors(StylusSample s)
        {
            if (!s.tracked)
            {
                HideCursors();
                return;
            }

            Color stateColor;
            switch (TipState)
            {
                case TipState.Drawing: stateColor = tool == DrawTool.Eraser ? new Color(1f, 0.4f, 0.6f, 0.9f) : (Color)color; break;
                case TipState.NeedsRearm: stateColor = new Color(1f, 0.55f, 0.1f, 0.9f); break;
                case TipState.OutOfBounds: stateColor = new Color(0.9f, 0.2f, 0.2f, 0.9f); break;
                default: stateColor = new Color(0.2f, 0.6f, 1f, 0.75f); break;
            }

            _tipMarker.enabled = true;
            Transform tm = _tipMarker.transform;
            tm.position = s.tipPosition;
            if (_device.TryGetHeadPose(out Vector3 head, out _) && (head - s.tipPosition).sqrMagnitude > 1e-6f)
                tm.rotation = QuatUtil.LookRotation(s.tipPosition - head, Vector3.up);
            float markerSize = Provider.TipIsEstimated ? 0.006f : 0.004f;
            tm.localScale = new Vector3(markerSize, markerSize, markerSize);
            InkAssets.SetTint(_tipMarker, Provider.TipIsEstimated ? new Color(1f, 0.6f, 0f, 0.9f) : stateColor);

            bool showBoard = space == DrawSpace.Surface && HasBoardPoint && _board.Exists &&
                             Mathf.Abs(LastBoardPoint.z) < 0.3f && _board.Frame.Contains(new Vector2(LastBoardPoint.x, LastBoardPoint.y), 0.02f);
            _boardCursor.enabled = showBoard;
            if (showBoard)
            {
                float d = tool == DrawTool.Eraser ? eraserRadiusMm * 0.002f : Mathf.Max(widthMm * 0.001f, 0.002f);
                // The hover cursor grows with distance from the surface, shrinking to the ink
                // width at contact: a simple depth cue for contact-like writing.
                if (TipState != TipState.Drawing) d += Mathf.Clamp(Mathf.Abs(LastBoardPoint.z), 0f, 0.05f) * 0.4f;
                Transform bc = _boardCursor.transform;
                bc.localPosition = new Vector3(LastBoardPoint.x, LastBoardPoint.y, -0.001f);
                bc.localRotation = new Quaternion(0, 0, 0, 1);
                bc.localScale = new Vector3(d, d, d);
                if (TipState != TipState.Drawing) stateColor.a *= 0.6f;
                InkAssets.SetTint(_boardCursor, stateColor);
            }
        }
    }
}
