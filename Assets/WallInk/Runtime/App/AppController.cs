using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WallInk
{
    public enum AppStage
    {
        Welcome,   // passthrough, no board yet
        Placing,   // choosing / defining the surface
        Adjusting, // board previewed; fine-tune and confirm
        Ready,     // board confirmed; calibrate, check accuracy, write
    }

    /// <summary>
    /// Application hub. Builds the runtime objects, owns the create-whiteboard flow and wires
    /// input, placement, calibration, diagnostics, persistence and the palette together.
    /// It talks to the XR platform only through IXRDevice / ISurfaceScanner / IBoardAnchor,
    /// which it discovers in the scene; with none present it falls back to editor simulation.
    /// </summary>
    [DefaultExecutionOrder(100)] // after the XR SDK has refreshed its input for the frame
    public sealed class AppController : MonoBehaviour
    {
        public static readonly Color32[] Palette =
        {
            new Color32(20, 20, 24, 255),    // black
            new Color32(30, 80, 200, 255),   // blue
            new Color32(210, 40, 40, 255),   // red
            new Color32(20, 140, 70, 255),   // green
            new Color32(240, 130, 20, 255),  // orange
            new Color32(130, 60, 180, 255),  // purple
            new Color32(250, 250, 250, 255), // white
            new Color32(250, 210, 40, 255),  // yellow
        };

        static readonly float[] StepMm = { 1f, 5f, 20f };
        static readonly float[] StepDeg = { 0.2f, 1f, 3f };
        static readonly float[] StepSizeCm = { 1f, 5f, 10f };
        static readonly string[] StepNames = { "fine", "medium", "coarse" };
        static readonly float[] OpacitySteps = { 0.2f, 0.35f, 0.6f, 0.92f };

        public IXRDevice Device { get; private set; }
        public WhiteboardSurface Board { get; private set; }
        public DrawingManager Drawing { get; private set; }
        public SurfacePlacementController Placement { get; } = new SurfacePlacementController();
        public TipCalibrationController Calibration { get; } = new TipCalibrationController();
        public AccuracyDiagnostics Diagnostics { get; } = new AccuracyDiagnostics();
        public DrawingPaletteController PaletteUi { get; private set; }

        public AppStage Stage { get; private set; } = AppStage.Welcome;
        public string Guide { get; private set; } = string.Empty;
        public string Message { get; private set; } = string.Empty;
        public Handedness DominantHand { get; private set; } = Handedness.Right;
        public int StepIndex { get; private set; }
        public int Smoothing { get; private set; } = 1;
        public bool WritingGuide { get; private set; }
        public bool PaletteOnRight => _settings.paletteOnRight;
        public bool ChoosingPlacement { get; private set; }
        public bool ScannerAvailable => _scanner != null;
        public bool CanLaunchRoomSetup => _scanner != null && _scanner.CanLaunchRoomSetup;
        public bool AnchorSupported => _anchor != null;
        public string StepName => StepNames[StepIndex];

        ISurfaceScanner _scanner;
        IBoardAnchor _anchor;
        SavedSettings _settings;
        ControllerStylusInputProvider _controller;
        HandStylusInputProvider _hand;
        CalibratedPenInputProvider _pen;
        ProceduralStrokeRenderer _boardRenderer;
        ProceduralStrokeRenderer _airRenderer;
        BoardMarkers _markers;
        readonly List<MeshRenderer> _pointMarkers = new List<MeshRenderer>();

        // Anchor bookkeeping
        bool _anchored;
        bool _anchorLost;
        bool _movedSinceAnchor;
        Vector3 _anchorLocalPos;
        Quaternion _anchorLocalRot = new Quaternion(0, 0, 0, 1);

        // Restore point for cancelling a placement
        bool _hadBoardBeforePlacement;
        BoardFrame _frameBeforePlacement;
        AlignmentState _alignmentBeforePlacement;

        float _unavailableTime;
        bool _autoBoardDone;
        float _startupTime;
        bool _quickMove;
        readonly StringBuilder _sb = new StringBuilder(512);

        public IStylusInputProvider Provider => Drawing.Provider;
        public HandDrawGesture HandGesture => _hand.gesture;

        // ------------------------------------------------------------------ setup

        void Awake()
        {
            Device = FindService<IXRDevice>();
            if (Device == null)
            {
                var sim = gameObject.AddComponent<SimulatedXRDevice>();
                sim.BoardProvider = () => Board != null && Board.Exists ? Board.Frame : (BoardFrame?)null;
                Device = sim;
            }
            _scanner = FindService<ISurfaceScanner>();
            _anchor = FindService<IBoardAnchor>();

            Board = new GameObject("Whiteboard").AddComponent<WhiteboardSurface>();
            Board.Init();
            _boardRenderer = Board.StrokeRoot.gameObject.AddComponent<ProceduralStrokeRenderer>();
            _boardRenderer.Init(InkAssets.InkMaterial, InkAssets.SortInk);

            var air = new GameObject("Air Canvas");
            _airRenderer = air.AddComponent<ProceduralStrokeRenderer>();
            _airRenderer.planeOffset = 0f;
            _airRenderer.Init(InkAssets.AirInkMaterial, InkAssets.SortAirInk);

            Drawing = gameObject.AddComponent<DrawingManager>();
            Drawing.Init(Device, Board, _boardRenderer, _airRenderer, air.transform);

            _markers = new BoardMarkers(Board.transform);
            Placement.Init(Device, _scanner);
            Diagnostics.Init(Drawing, Board, _markers);
            Diagnostics.ContextProvider = DescribeCalibration;
            Diagnostics.Completed += OnDiagnosticsCompleted;
            Calibration.Completed += OnCalibrationCompleted;
            Device.TrackingDisrupted += OnTrackingDisrupted;
            Device.InputInterrupted += reason => Drawing.Interrupt(reason);

            _settings = BoardPersistence.ReadSettings();
            DominantHand = (Handedness)Mathf.Clamp(_settings.dominantHand, 0, 1);
            _controller = new ControllerStylusInputProvider(Device, DominantHand);
            _hand = new HandStylusInputProvider(Device, DominantHand) { gesture = (HandDrawGesture)Mathf.Clamp(_settings.handGesture, 0, 2) };
            _pen = new CalibratedPenInputProvider(Device, DominantHand);
            LoadCalibration(_controller);
            LoadCalibration(_hand);
            LoadCalibration(_pen);

            Drawing.widthMm = Mathf.Clamp(_settings.widthMm, DrawingManager.MinWidthMm, DrawingManager.MaxWidthMm);
            Drawing.capture.mode = (ActivationMode)Mathf.Clamp(_settings.activationMode, 0, 1);
            Drawing.capture.contactEnterMm = _settings.contactEnterMm;
            Drawing.capture.contactExitMm = Mathf.Max(_settings.contactExitMm, _settings.contactEnterMm + 2f);
            ApplySmoothing(Mathf.Clamp(_settings.smoothing, 0, 2));
            Drawing.SetProvider(ProviderFor((StylusSource)Mathf.Clamp(_settings.inputSource, 0, 2)));

            PaletteUi = gameObject.AddComponent<DrawingPaletteController>();
            PaletteUi.Init(this);

            Board.backingOpacity = 0.92f;
            Guide = "Starting... a whiteboard will appear in front of you.";
            Message = "Device: " + Device.Description;
        }

        static T FindService<T>() where T : class
        {
            MonoBehaviour[] all = FindObjectsByType<MonoBehaviour>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] is T t && all[i].isActiveAndEnabled) return t;
            return null;
        }

        IStylusInputProvider ProviderFor(StylusSource source)
        {
            switch (source)
            {
                case StylusSource.Hand: return _hand;
                case StylusSource.EstimatedPen: return _pen;
                default: return _controller;
            }
        }

        // ------------------------------------------------------------------ frame loop

        void Update()
        {
            float dt = Time.unscaledDeltaTime;

            // Digital-whiteboard start: as soon as head tracking is live, put a board in front
            // of the user, ready to write on. No placement or calibration is required first.
            if (!_autoBoardDone)
            {
                _startupTime += dt;
                if (_startupTime > 0.75f && Device.TryGetHeadPose(out Vector3 hp, out _) && hp.sqrMagnitude > 1e-4f)
                {
                    _autoBoardDone = true;
                    if (!Board.Exists) BringBoardToMe();
                }
            }

            PaletteUi.Tick(dt);

            bool busy = Placement.Active != PlacementMethod.None || Calibration.Active != CalibrationMode.None ||
                        Diagnostics.Active == DiagnosticMode.Jitter;
            Drawing.Suppressed = PaletteUi.PointerOverUi || busy;
            Drawing.Tick();
            StylusSample sample = Drawing.LastSample;

            if (Placement.Active != PlacementMethod.None)
            {
                bool confirmed = Placement.Tick(dt, Provider, sample, PaletteUi.PointerOverUi);
                BoardFrame preview = Placement.Preview;
                if (_quickMove) preview.size = _frameBeforePlacement.size; // moving keeps the board's size
                if (confirmed)
                {
                    ApplyPlacement(preview);
                    if (_quickMove)
                    {
                        _quickMove = false;
                        ConfirmBoard(true);
                    }
                }
                else if (Placement.HasPreview) ShowPreview(preview);
                UpdatePointMarkers();
            }

            Calibration.Tick(dt, Provider, Board, sample);
            Diagnostics.Tick(dt);
            UpdateAnchor();

            // Input availability hint (never switch automatically: switching must be deliberate).
            _unavailableTime = Provider.IsAvailable ? 0f : _unavailableTime + dt;
        }

        // ------------------------------------------------------------------ placement

        /// <summary>
        /// Places the whiteboard upright half a metre in front of the user, facing them, and
        /// makes it writable at once. Existing ink comes along (it is stored on the board).
        /// </summary>
        public void BringBoardToMe()
        {
            if (!Device.TryGetHeadPose(out Vector3 head, out Quaternion headRot))
            {
                Message = "Head tracking is not available yet.";
                return;
            }
            CancelProcedures();
            Placement.Cancel();
            HidePointMarkers();
            ChoosingPlacement = false;
            _quickMove = false;
            Drawing.Interrupt(null);

            Vector3 forward = headRot * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = Vector3.forward;
            forward.Normalize();
            Vector2 size = Board.Exists ? Board.size : new Vector2(1.0f, 0.65f);
            Vector3 center = head + forward * 0.5f + Vector3.down * 0.1f;

            ReleaseAnchor();
            Board.SetFrame(BoardFrame.FromPlane(center, -forward, forward, size));
            _movedSinceAnchor = true;
            ConfirmBoard(true);
        }

        /// <summary>Carry the board on the pointer and drop it with the draw control; size and ink are kept.</summary>
        public void MoveBoard()
        {
            if (!Board.Exists) { BringBoardToMe(); return; }
            StartPlacement(PlacementMethod.Pointer);
            _quickMove = Placement.Active == PlacementMethod.Pointer;
            Guide = "Point where the board should go (thumbstick up/down changes distance), then press the draw control to drop it.";
        }

        public void BeginCreateWhiteboard()
        {
            ChoosingPlacement = true;
            Guide = "Choose how to place the board: 'Detected' uses the headset's room scan (approximate), " +
                    "'3+ points' fits a plane to points you touch (most precise), 'Pointer' drops a board where you aim.";
        }

        public void StartPlacement(PlacementMethod method)
        {
            ChoosingPlacement = false;
            CancelProcedures();
            Drawing.Interrupt(null);
            _hadBoardBeforePlacement = Board.Exists;
            _frameBeforePlacement = Board.Frame;
            _alignmentBeforePlacement = Board.alignment;
            Placement.Begin(method);
            if (Placement.Active != PlacementMethod.None) Stage = AppStage.Placing;
            Guide = Placement.Prompt;
        }

        public void CancelPlacement()
        {
            ChoosingPlacement = false;
            _quickMove = false;
            if (Placement.Active == PlacementMethod.None) return;
            Placement.Cancel();
            HidePointMarkers();
            if (_hadBoardBeforePlacement)
            {
                Board.SetFrame(_frameBeforePlacement);
                Board.SetAlignment(_alignmentBeforePlacement);
                Stage = Board.WritingAllowed ? AppStage.Ready : AppStage.Adjusting;
            }
            else
            {
                Board.SetAlignment(AlignmentState.None);
                Stage = AppStage.Welcome;
            }
            Guide = "Placement cancelled.";
        }

        public void FinishPoints()
        {
            if (Placement.TryFinishPoints(out BoardFrame frame)) ApplyPlacement(frame);
            else Guide = Placement.Prompt;
        }

        public void RemoveLastPoint()
        {
            Placement.RemoveLastPoint();
            if (!Placement.HasPreview && !_hadBoardBeforePlacement) Board.SetAlignment(AlignmentState.None);
            UpdatePointMarkers();
        }

        public void LaunchRoomSetup()
        {
            if (_scanner != null && _scanner.CanLaunchRoomSetup) _scanner.LaunchRoomSetup();
        }

        void ShowPreview(BoardFrame frame)
        {
            Board.SetFrame(frame);
            if (Board.alignment != AlignmentState.Preview) Board.SetAlignment(AlignmentState.Preview);
        }

        void ApplyPlacement(BoardFrame frame)
        {
            HidePointMarkers();
            ReleaseAnchor();
            Board.SetFrame(frame);
            Board.SetAlignment(AlignmentState.Preview);
            Stage = AppStage.Adjusting;
            _movedSinceAnchor = true;
            Guide = "Compare the blue outline, centre cross and arrow with the real surface. Use the Board adjustments " +
                    "(depth is the important one) until the outline lies ON the surface, then press Confirm.";
            PaletteUi.ShowPage(DrawingPaletteController.PageBoard);
        }

        void UpdatePointMarkers()
        {
            if (Placement.Active != PlacementMethod.Points)
            {
                HidePointMarkers();
                return;
            }
            Guide = Placement.Prompt;
            Device.TryGetHeadPose(out Vector3 head, out _);
            for (int i = 0; i < Placement.Points.Count; i++)
            {
                if (i >= _pointMarkers.Count)
                {
                    MeshRenderer mr = InkAssets.NewRenderer("Point Marker", transform, InkAssets.Disc, InkAssets.InkMaterial, InkAssets.SortCursor);
                    InkAssets.SetTint(mr, new Color(1f, 0.5f, 0f, 1f));
                    mr.transform.localScale = Vector3.one * 0.012f;
                    _pointMarkers.Add(mr);
                }
                Transform t = _pointMarkers[i].transform;
                t.position = Placement.Points[i];
                if ((head - t.position).sqrMagnitude > 1e-6f) t.rotation = QuatUtil.LookRotation(t.position - head, Vector3.up);
                _pointMarkers[i].enabled = true;
            }
            for (int i = Placement.Points.Count; i < _pointMarkers.Count; i++) _pointMarkers[i].enabled = false;
            if (!Placement.HasPreview && !_hadBoardBeforePlacement && Board.Exists) Board.SetAlignment(AlignmentState.None);
        }

        void HidePointMarkers()
        {
            for (int i = 0; i < _pointMarkers.Count; i++) _pointMarkers[i].enabled = false;
        }

        // ------------------------------------------------------------------ adjust / confirm

        /// <summary>"Realign Board": pause writing and re-enter adjustment. Existing ink stays attached to the board.</summary>
        public void Realign()
        {
            if (!Board.Exists) { Guide = "No board yet. Press 'Create Whiteboard'."; return; }
            CancelProcedures();
            Drawing.Interrupt("Realigning board");
            Board.SetAlignment(AlignmentState.Preview);
            Stage = AppStage.Adjusting;
            Guide = "Realign: nudge the board until the outline lies on the real surface, then Confirm. Your ink moves with the board.";
        }

        bool EnsureAdjusting()
        {
            if (!Board.Exists) return false;
            if (Board.alignment == AlignmentState.Confirmed) Realign();
            else if (Stage != AppStage.Adjusting && Placement.Active == PlacementMethod.None) Stage = AppStage.Adjusting;
            _movedSinceAnchor = true;
            return Placement.Active == PlacementMethod.None;
        }

        /// <summary>dir: x = right, y = up, z = toward the writer; scaled by the current step.</summary>
        public void NudgeBoard(Vector3 dir)
        {
            if (!EnsureAdjusting()) return;
            Board.Nudge(dir * (StepMm[StepIndex] * 0.001f));
        }

        public void RotateBoard(float pitch, float yaw, float roll)
        {
            if (!EnsureAdjusting()) return;
            float d = StepDeg[StepIndex];
            Board.RotateLocal(pitch * d, yaw * d, roll * d);
        }

        public void ResizeBoard(float w, float h)
        {
            if (!EnsureAdjusting()) return;
            float d = StepSizeCm[StepIndex] * 0.01f;
            Board.Resize(w * d, h * d);
        }

        public void CycleStep() => StepIndex = (StepIndex + 1) % StepMm.Length;

        public void ConfirmBoard() => ConfirmBoard(false);

        /// <param name="quick">True for the instant whiteboard: go straight to drawing instead of the calibration steps.</param>
        public void ConfirmBoard(bool quick)
        {
            if (!Board.Exists || Placement.Active != PlacementMethod.None) return;
            if (Board.alignment == AlignmentState.Uncertain && _anchored && _anchor != null && _anchor.State == AnchorState.NotLocalized)
            {
                Guide = "The board's anchor is still not located by the headset. Look around the area, or use Realign.";
                return;
            }
            Board.SetAlignment(AlignmentState.Confirmed);
            Stage = AppStage.Ready;
            Drawing.Interrupt(null);

            if (_anchor != null && (!_anchored || _movedSinceAnchor || _anchor.State != AnchorState.Tracked))
            {
                ReleaseAnchor();
                _anchor.Create(Board.transform.position, Board.transform.rotation);
                _anchored = true;
                _anchorLocalPos = Vector3.zero;
                _anchorLocalRot = new Quaternion(0, 0, 0, 1);
            }
            _movedSinceAnchor = false;
            _anchorLost = false;

            Guide = "Board confirmed" + (_anchor == null ? " (no spatial anchor here: held by headset tracking only)" : "") +
                    ". Next, on the Calib tab: 'Touch offset'" + (Provider.SupportsPivotCalibration ? " and 'Tip calibration'" : "") +
                    ", then 'Accuracy check'. Or go to Draw and write.";
            if (quick)
            {
                Guide = "Whiteboard ready. Bring the tip close to the board and " + Provider.DrawControlHint + " to write. " +
                        "To pin it to a real wall or desk instead, use the Board tab.";
                PaletteUi.ShowPage(DrawingPaletteController.PageDraw);
            }
            else
            {
                PaletteUi.ShowPage(DrawingPaletteController.PageCalib);
            }
        }

        public void RemoveBoard()
        {
            CancelProcedures();
            Placement.Cancel();
            Drawing.Interrupt(null);
            Drawing.History.Clear(StrokeSpace.Board);
            ReleaseAnchor();
            Board.SetAlignment(AlignmentState.None);
            Stage = AppStage.Welcome;
            Guide = "Board removed (Undo restores its ink once a board exists again).";
        }

        public void CycleOpacity()
        {
            int i = 0;
            for (int k = 0; k < OpacitySteps.Length; k++)
                if (Mathf.Abs(OpacitySteps[k] - Board.backingOpacity) < 0.01f) i = k;
            Board.backingOpacity = OpacitySteps[(i + 1) % OpacitySteps.Length];
            Board.Refresh();
        }

        public void CycleGrid()
        {
            Board.grid = (GridMode)(((int)Board.grid + 1) % 3);
            Board.Refresh();
        }

        // ------------------------------------------------------------------ anchoring & tracking stability

        void ReleaseAnchor()
        {
            if (_anchor != null && _anchored) _anchor.Release();
            _anchored = false;
            _anchorLost = false;
        }

        /// <summary>
        /// Keeps the confirmed board on its spatial anchor. Small corrections are applied
        /// continuously (that is what holds the board on the wall as the headset refines its
        /// map). A large correction ends any stroke in progress first, so the board never
        /// jumps silently under the pen.
        /// </summary>
        void UpdateAnchor()
        {
            if (_anchor == null || !_anchored) return;
            switch (_anchor.State)
            {
                case AnchorState.Tracked:
                    if (!_anchor.TryGetPose(out Vector3 ap, out Quaternion ar)) break;
                    if (_anchorLost)
                    {
                        _anchorLost = false;
                        if (Board.alignment == AlignmentState.Uncertain)
                        {
                            Board.transform.SetPositionAndRotation(ap + ar * _anchorLocalPos, QuatUtil.Normalize(ar * _anchorLocalRot));
                            Board.SetAlignment(AlignmentState.NeedsValidation);
                            Guide = "The board's anchor was found again. Check the outline against the surface, then Confirm (or Realign).";
                        }
                    }
                    if (Board.alignment != AlignmentState.Confirmed) break;
                    Vector3 target = ap + ar * _anchorLocalPos;
                    Quaternion targetRot = QuatUtil.Normalize(ar * _anchorLocalRot);
                    float dist = (target - Board.transform.position).magnitude;
                    float angle = QuatUtil.Angle(targetRot, Board.transform.rotation);
                    if (dist < 1e-5f && angle < 1e-3f) break;
                    if ((dist > 0.004f || angle > 0.4f) && Drawing.TipState == TipState.Drawing)
                        Drawing.Interrupt($"Board re-localized by {dist * 1000f:0} mm: stroke ended. Lift / release, then continue.");
                    Board.transform.SetPositionAndRotation(target, targetRot);
                    break;

                case AnchorState.NotLocalized:
                    if (_anchorLost) break;
                    _anchorLost = true;
                    Drawing.Interrupt("Board anchor not located: writing paused");
                    if (Board.alignment == AlignmentState.Confirmed) Board.SetAlignment(AlignmentState.Uncertain);
                    Stage = AppStage.Adjusting;
                    Guide = "The headset cannot currently locate the board's anchor, so alignment is uncertain and writing is paused. " +
                            "Look around the area to help it relocalize, or use Realign.";
                    break;

                case AnchorState.Failed:
                case AnchorState.Unsupported:
                    _anchored = false;
                    Message = "Spatial anchor unavailable (" + _anchor.Message + "). The board is held by headset tracking only; " +
                              "use Realign if it drifts.";
                    break;
            }
        }

        void OnTrackingDisrupted(string reason)
        {
            Drawing.Interrupt("Tracking changed (" + reason + ")");
            if (!Board.Exists) return;
            bool anchorOk = _anchored && _anchor != null && _anchor.State == AnchorState.Tracked;
            if (anchorOk)
            {
                Message = "Tracking changed (" + reason + "). The board follows its spatial anchor; glance at the outline to check it.";
                return;
            }
            // Without an anchor the board is defined in tracking-space coordinates, which a
            // recenter or relocalization can shift relative to the room.
            if (Board.alignment == AlignmentState.Confirmed)
            {
                Board.SetAlignment(AlignmentState.Uncertain);
                Stage = AppStage.Adjusting;
            }
            Guide = "Tracking changed (" + reason + "). Writing is paused: check that the red outline still lies on the surface, " +
                    "then Confirm, or adjust it first.";
            PaletteUi.ShowPage(DrawingPaletteController.PageBoard);
        }

        void OnApplicationPause(bool paused)
        {
            if (Drawing == null) return;
            if (paused) Drawing.Interrupt("App paused");
            else OnTrackingDisrupted("app resumed");
        }

        void OnApplicationFocus(bool focus)
        {
            if (Drawing != null && !focus) Drawing.Interrupt("App lost focus");
        }

        // ------------------------------------------------------------------ input

        public void SetInput(StylusSource source)
        {
            CancelProcedures();
            IStylusInputProvider p = ProviderFor(source);
            Drawing.SetProvider(p);
            _settings.inputSource = (int)source;
            SaveSettings();
            Message = p.TipIsEstimated
                ? "Pen input is EXPERIMENTAL. The tip is estimated from your hand, not tracked. Run 'Pen quick calibrate' on the Calib tab, and again whenever you re-grip."
                : "Input: " + p.DisplayName + ". Draw control: " + p.DrawControlHint + ".";
        }

        public void ToggleDominantHand()
        {
            CancelProcedures();
            Drawing.Interrupt("Hand changed");
            StoreCalibration(_controller); StoreCalibration(_hand); StoreCalibration(_pen);
            DominantHand = DominantHand == Handedness.Right ? Handedness.Left : Handedness.Right;
            _controller.Hand = _hand.Hand = _pen.Hand = DominantHand;
            LoadCalibration(_controller); LoadCalibration(_hand); LoadCalibration(_pen);
            _settings.dominantHand = (int)DominantHand;
            SaveSettings();
            Message = "Dominant hand: " + DominantHand + ". Calibration is stored per hand.";
        }

        public void CycleHandGesture()
        {
            _hand.gesture = (HandDrawGesture)(((int)_hand.gesture + 1) % 3);
            _settings.handGesture = (int)_hand.gesture;
            SaveSettings();
            Drawing.Interrupt(null);
            Message = "Fingertip draw gesture: " + _hand.DrawControlHint + ".";
        }

        public void ToggleActivation()
        {
            Drawing.Interrupt(null);
            Drawing.capture.mode = Drawing.capture.mode == ActivationMode.ContactLike ? ActivationMode.Explicit : ActivationMode.ContactLike;
            _settings.activationMode = (int)Drawing.capture.mode;
            SaveSettings();
            Message = Drawing.capture.mode == ActivationMode.ContactLike
                ? "Contact-like: ink when the tip is within the contact distance of the board. Run 'Touch offset' first."
                : "Explicit: ink only while you " + Provider.DrawControlHint + "; the tip is projected onto the board.";
        }

        public void ToggleSpace()
        {
            Drawing.Interrupt(null);
            Drawing.space = Drawing.space == DrawSpace.Surface ? DrawSpace.Air : DrawSpace.Surface;
            Message = Drawing.space == DrawSpace.Air
                ? "Air mode: freehand 3D strokes while you " + Provider.DrawControlHint + "."
                : "Surface mode: writing on the board.";
        }

        public void CycleSmoothing()
        {
            ApplySmoothing((Smoothing + 1) % 3);
            _settings.smoothing = Smoothing;
            SaveSettings();
        }

        void ApplySmoothing(int level)
        {
            Smoothing = level;
            switch (level)
            {
                case 0: Drawing.build.minCutoffHz = 0f; Drawing.build.beta = 0f; break;
                case 2: Drawing.build.minCutoffHz = 3f; Drawing.build.beta = 90f; break;
                default: Drawing.build.minCutoffHz = 6f; Drawing.build.beta = 150f; break;
            }
        }

        public string SmoothingName => Smoothing == 0 ? "off" : Smoothing == 1 ? "light" : "medium";

        public void ChangeWidth(float deltaMm)
        {
            Drawing.widthMm = Mathf.Clamp(Mathf.Round((Drawing.widthMm + deltaMm) * 2f) / 2f, DrawingManager.MinWidthMm, DrawingManager.MaxWidthMm);
            _settings.widthMm = Drawing.widthMm;
            SaveSettings();
        }

        public void TogglePaletteSide()
        {
            _settings.paletteOnRight = !_settings.paletteOnRight;
            SaveSettings();
        }

        // ------------------------------------------------------------------ calibration & diagnostics

        public void StartCalibration(CalibrationMode mode)
        {
            CancelProcedures();
            Drawing.Interrupt(null);
            Calibration.Begin(mode, Provider, Board);
            Guide = Calibration.Active != CalibrationMode.None ? Calibration.Prompt : Calibration.LastResult;
            RefreshMarkers();
        }

        public void ResetCalibration()
        {
            CancelProcedures();
            Provider.SurfaceOffsetMm = 0f;
            if (Provider is ControllerStylusInputProvider) Provider.LocalTipOffset = ControllerStylusInputProvider.DefaultTipOffset;
            else if (Provider is CalibratedPenInputProvider pen)
            {
                pen.LocalTipOffset = CalibratedPenInputProvider.DefaultTipOffset;
                pen.calibratedLengthMm = pen.calibratedSpreadMm = 0f;
            }
            else Provider.LocalTipOffset = Vector3.zero;
            StoreCalibration(Provider);
            SaveSettings();
            Message = "Calibration reset for " + Provider.DisplayName + " (" + Provider.Hand + ").";
        }

        public void AdjustSurfaceOffset(float deltaMm)
        {
            Provider.SurfaceOffsetMm = Mathf.Clamp(Provider.SurfaceOffsetMm + deltaMm, -60f, 60f);
            StoreCalibration(Provider);
            SaveSettings();
        }

        public void AdjustContact(float enterDelta, float exitDelta)
        {
            CaptureSettings c = Drawing.capture;
            c.contactEnterMm = Mathf.Clamp(c.contactEnterMm + enterDelta, 0f, 30f);
            c.contactExitMm = Mathf.Clamp(c.contactExitMm + exitDelta, c.contactEnterMm + 2f, 60f);
            _settings.contactEnterMm = c.contactEnterMm;
            _settings.contactExitMm = c.contactExitMm;
            SaveSettings();
        }

        public void StartDiagnostics(DiagnosticMode mode)
        {
            CancelProcedures();
            Drawing.Interrupt(null);
            if (Drawing.space != DrawSpace.Surface) Drawing.space = DrawSpace.Surface;
            Guide = Diagnostics.Begin(mode) ? Diagnostics.Prompt : Diagnostics.LastSummary;
        }

        public void ToggleWritingGuide()
        {
            WritingGuide = !WritingGuide;
            RefreshMarkers();
            if (WritingGuide)
                Guide = "Writing exercise: write '2+3=5' and 'x' with a superscript on each line (20, 10 and 5 mm). " +
                        "Watch whether the ink appears under the tip and whether small symbols stay readable.";
        }

        public void CancelProcedures()
        {
            Calibration.Cancel();
            Diagnostics.Cancel();
            RefreshMarkers();
        }

        void OnCalibrationCompleted(CalibrationMode mode, bool ok)
        {
            Guide = Calibration.LastResult;
            if (ok)
            {
                StoreCalibration(Provider);
                SaveSettings();
                Drawing.Interrupt(null);
            }
            RefreshMarkers();
        }

        void OnDiagnosticsCompleted(DiagnosticMode mode)
        {
            Guide = Diagnostics.LastSummary;
            RefreshMarkers();
            PaletteUi.ShowPage(DrawingPaletteController.PageInfo);
        }

        void RefreshMarkers()
        {
            if (Diagnostics.Active == DiagnosticMode.Targets) return; // diagnostics draws its own targets
            if (!Board.Exists) { _markers.Hide(); return; }
            if (Calibration.Active == CalibrationMode.PenQuick)
            {
                _markers.Begin();
                _markers.Cross(Calibration.PenTarget, 0.015f, new Color32(255, 120, 0, 255), 0.001f);
                _markers.End();
            }
            else if (WritingGuide)
            {
                // Three ruled lines for a handwriting-size exercise: 20, 10 and 5 mm tall.
                var c = new Color32(90, 110, 140, 220);
                float x0 = -Board.size.x * 0.4f, x1 = x0 + Mathf.Min(0.2f, Board.size.x * 0.6f);
                float y = Board.size.y * 0.35f;
                _markers.Begin();
                float[] heights = { 0.02f, 0.01f, 0.005f };
                for (int i = 0; i < heights.Length; i++)
                {
                    _markers.Box(new Vector2(x0, y - heights[i]), new Vector2(x1, y), c);
                    y -= heights[i] + 0.012f;
                }
                _markers.End();
            }
            else
            {
                _markers.Hide();
            }
        }

        // ------------------------------------------------------------------ persistence

        void StoreCalibration(IStylusInputProvider p)
        {
            SavedInputCalibration c = FindCalibration(p, true);
            c.tipOffset = p.LocalTipOffset;
            c.surfaceOffsetMm = p.SurfaceOffsetMm;
            if (p is CalibratedPenInputProvider pen)
            {
                c.gripSpreadMm = pen.calibratedSpreadMm;
                c.gripLengthMm = pen.calibratedLengthMm;
            }
        }

        void LoadCalibration(IStylusInputProvider p)
        {
            SavedInputCalibration c = FindCalibration(p, false);
            var pen = p as CalibratedPenInputProvider;
            if (c == null)
            {
                p.SurfaceOffsetMm = 0f;
                if (p is ControllerStylusInputProvider) p.LocalTipOffset = ControllerStylusInputProvider.DefaultTipOffset;
                else if (pen != null) { pen.LocalTipOffset = CalibratedPenInputProvider.DefaultTipOffset; pen.calibratedSpreadMm = pen.calibratedLengthMm = 0f; }
                else p.LocalTipOffset = Vector3.zero;
                return;
            }
            p.LocalTipOffset = c.tipOffset;
            p.SurfaceOffsetMm = c.surfaceOffsetMm;
            if (pen != null) { pen.calibratedSpreadMm = c.gripSpreadMm; pen.calibratedLengthMm = c.gripLengthMm; }
        }

        SavedInputCalibration FindCalibration(IStylusInputProvider p, bool create)
        {
            for (int i = 0; i < _settings.calibrations.Count; i++)
            {
                SavedInputCalibration c = _settings.calibrations[i];
                if (c.source == (int)p.Source && c.hand == (int)p.Hand) return c;
            }
            if (!create) return null;
            var n = new SavedInputCalibration { source = (int)p.Source, hand = (int)p.Hand };
            _settings.calibrations.Add(n);
            return n;
        }

        void SaveSettings() => BoardPersistence.WriteSettings(_settings);

        public void SaveBoard()
        {
            if (!Board.Exists) { Message = "Nothing to save: no board."; return; }
            SavedBoard sb = BoardPersistence.Capture(Board, Drawing.History);
            if (_anchor != null && _anchored && _anchor.State == AnchorState.Tracked && Board.alignment == AlignmentState.Confirmed)
            {
                Message = "Saving board and anchor...";
                _anchor.Save(ok =>
                {
                    if (ok && !string.IsNullOrEmpty(_anchor.PersistentId))
                    {
                        sb.anchorId = _anchor.PersistentId;
                        sb.anchorLocalPosition = _anchorLocalPos;
                        sb.anchorLocalRotation = _anchorLocalRot;
                    }
                    WriteBoard(sb, ok);
                });
            }
            else
            {
                WriteBoard(sb, false);
            }
        }

        void WriteBoard(SavedBoard sb, bool anchorSaved)
        {
            if (!BoardPersistence.Write(sb, out string error)) { Message = "Save failed: " + error; return; }
            Message = $"Saved {sb.strokes.Count} strokes" + (anchorSaved
                ? " with a spatial anchor."
                : ". No anchor was saved, so next session the board position will only be a rough guess and must be realigned.");
        }

        public void LoadBoard()
        {
            SavedBoard sb = BoardPersistence.Read(out string error);
            if (sb == null) { Message = error; return; }
            CancelProcedures();
            Placement.Cancel();
            HidePointMarkers();
            Drawing.Interrupt(null);
            ReleaseAnchor();

            Drawing.History.Reset();
            List<StrokeData> strokes = BoardPersistence.ToStrokes(sb);
            for (int i = 0; i < strokes.Count; i++)
            {
                Drawing.History.AddRestored(strokes[i]);
                _boardRenderer.AddStroke(strokes[i]);
            }
            Board.backingOpacity = sb.backingOpacity;
            Board.grid = (GridMode)Mathf.Clamp(sb.grid, 0, 2);
            Board.SetFrame(new BoardFrame(sb.worldPosition, QuatUtil.Normalize(sb.worldRotation), sb.size));
            Board.SetAlignment(AlignmentState.NeedsValidation);
            Stage = AppStage.Adjusting;
            _movedSinceAnchor = false;
            PaletteUi.ShowPage(DrawingPaletteController.PageBoard);

            if (_anchor != null && !string.IsNullOrEmpty(sb.anchorId))
            {
                Guide = "Restoring the board's spatial anchor...";
                _anchor.Load(sb.anchorId, ok =>
                {
                    if (ok && _anchor.TryGetPose(out Vector3 ap, out Quaternion ar))
                    {
                        _anchored = true;
                        _anchorLocalPos = sb.anchorLocalPosition;
                        _anchorLocalRot = QuatUtil.Normalize(sb.anchorLocalRotation);
                        Board.transform.SetPositionAndRotation(ap + ar * _anchorLocalPos, QuatUtil.Normalize(ar * _anchorLocalRot));
                        Guide = $"Restored {strokes.Count} strokes at the saved anchor. Writing stays off until you check the orange " +
                                "outline against the real surface and press Confirm (or adjust it first).";
                    }
                    else
                    {
                        _movedSinceAnchor = true;
                        Guide = $"Restored {strokes.Count} strokes, but the saved anchor could not be located ({_anchor.Message}). " +
                                "The board position is only a guess: realign it (or Create Whiteboard again; the ink follows), then Confirm.";
                    }
                });
            }
            else
            {
                _movedSinceAnchor = true;
                Guide = $"Restored {strokes.Count} strokes without an anchor. The position is only a guess from the last session: " +
                        "realign the board (or Create Whiteboard again; the ink follows), then Confirm.";
            }
        }

        // ------------------------------------------------------------------ status text

        string DescribeCalibration()
        {
            Vector3 o = Provider.LocalTipOffset * 1000f;
            string s = $"Tip offset ({o.x:0.0}, {o.y:0.0}, {o.z:0.0}) mm; surface offset {Provider.SurfaceOffsetMm:0.0} mm; " +
                       $"contact enter {Drawing.capture.contactEnterMm:0} / exit {Drawing.capture.contactExitMm:0} mm; smoothing {SmoothingName}";
            if (Calibration.HasPivotResult && Calibration.LastPivot.success)
                s += $"\nLast tip calibration consistency: {Calibration.LastPivot.rmsResidual * 1000f:0.0} mm RMS";
            if (Placement.HasFit && Placement.LastFit.success && Placement.LastFit.residualIsMeaningful)
                s += $"\nLast plane fit: {Placement.LastFit.sampleCount} points, residual {Placement.LastFit.rmsResidual * 1000f:0.0} mm RMS (max {Placement.LastFit.maxResidual * 1000f:0.0})";
            return s;
        }

        /// <summary>Short always-visible status (palette header).</summary>
        public string BuildStatus()
        {
            _sb.Length = 0;
            StylusSample s = Drawing.LastSample;
            _sb.Append(Provider.DisplayName).Append(" (").Append(Provider.Hand == Handedness.Right ? "R" : "L").Append("): ");
            if (!s.tracked) _sb.Append("NOT TRACKED");
            else
            {
                switch (Drawing.TipState)
                {
                    case TipState.Drawing: _sb.Append("inking"); break;
                    case TipState.NeedsRearm: _sb.Append("lift / release to re-arm"); break;
                    case TipState.OutOfBounds: _sb.Append("off the board"); break;
                    default: _sb.Append("ready"); break;
                }
                if (Drawing.space == DrawSpace.Surface && Drawing.HasBoardPoint)
                    _sb.Append(", ").Append(Mathf.RoundToInt(Drawing.LastBoardPoint.z * 1000f)).Append(" mm from board");
            }
            _sb.Append('\n').Append("Board: ");
            switch (Board.alignment)
            {
                case AlignmentState.None: _sb.Append("none"); break;
                case AlignmentState.Preview: _sb.Append("adjusting (not confirmed)"); break;
                case AlignmentState.NeedsValidation: _sb.Append("restored - check and confirm"); break;
                case AlignmentState.Uncertain: _sb.Append("ALIGNMENT UNCERTAIN - writing paused"); break;
                default: _sb.Append("confirmed"); break;
            }
            if (Board.Exists)
            {
                _sb.Append(", anchor ");
                if (_anchor == null) _sb.Append("n/a");
                else if (!_anchored) _sb.Append("none");
                else _sb.Append(_anchor.State.ToString().ToLowerInvariant());
            }
            if (_unavailableTime > 1.5f)
                _sb.Append('\n').Append(Provider.DisplayName).Append(" is not available. Pick another input on the Input tab if you switched.");
            else if (!s.tracked && !string.IsNullOrEmpty(s.invalidReason))
                _sb.Append('\n').Append(s.invalidReason);
            else if (!string.IsNullOrEmpty(Drawing.Notice))
                _sb.Append('\n').Append(Drawing.Notice);
            return _sb.ToString();
        }

        /// <summary>Detailed diagnostics (Info tab).</summary>
        public string BuildInfo()
        {
            Drawing.GetFrameStats(out float avg, out float max);
            _sb.Length = 0;
            _sb.Append("Strokes: ").Append(Drawing.History.VisibleCount).Append(" visible (board ")
               .Append(_boardRenderer.StrokeCount).Append('/').Append(_boardRenderer.maxStrokes).Append(", air ").Append(_airRenderer.StrokeCount).Append(")\n");
            _sb.Append("Vertices: ").Append(_boardRenderer.TotalVertexCount + _airRenderer.TotalVertexCount).Append(" of ")
               .Append(_boardRenderer.maxTotalVertices).Append(" in ").Append(_boardRenderer.ChunkCount + _airRenderer.ChunkCount).Append(" chunks\n");
            _sb.Append("Frame: ").Append(avg.ToString("0.0")).Append(" ms mean, ").Append(max.ToString("0.0")).Append(" ms worst (last 120)\n");
            _sb.Append("Tracking dropouts: ").Append(Drawing.DropoutCount).Append(" (").Append(Drawing.UntrackedSeconds.ToString("0.0")).Append(" s)\n");
            _sb.Append(DescribeCalibration()).Append('\n');
            if (!string.IsNullOrEmpty(Diagnostics.LastSummary)) _sb.Append(Diagnostics.LastSummary).Append('\n');
            if (!string.IsNullOrEmpty(Diagnostics.LastReportPath)) _sb.Append("Report file: ").Append(Diagnostics.LastReportPath).Append('\n');
            if (!string.IsNullOrEmpty(Message)) _sb.Append(Message);
            return _sb.ToString();
        }

        public string AnchorDescription => _anchor == null ? "not available" : _anchored ? _anchor.State.ToString() : "none";
    }
}
