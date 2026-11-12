using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace WallInk
{
    public enum DiagnosticMode { None, Targets, Jitter }

    /// <summary>
    /// Accuracy-check mode. It measures what the headset can measure about itself and is
    /// explicit about what it cannot:
    ///
    ///  MEASURED  - repeatability: scatter of repeated indications of the same target (mm);
    ///            - mean offset from each DISPLAYED target (includes how well the user aimed);
    ///            - scatter of the reported height at contact (plane + tip noise along the normal);
    ///            - jitter of a tip held still; tracking dropouts; frame-time statistics.
    ///  NOT MEASURED - absolute accuracy against the physical wall. The targets are virtual,
    ///            so a board that is 5 mm off the wall still scores perfectly here. Physical
    ///            alignment is judged by eye against the overlay (outline, centre cross, grid)
    ///            and corrected with the Board adjustments.
    ///            - end-to-end latency (motion to photons); only the frame time and the
    ///            smoothing filter's theoretical lag are reported.
    /// </summary>
    public sealed class AccuracyDiagnostics
    {
        public const int Repeats = 3;
        public const float AcceptRadius = 0.06f;
        public const float JitterSeconds = 4f;

        // Provisional engineering targets (see README). They are goals to test against on a
        // device, not results.
        public const float TargetRepeatabilityControllerMm = 2f;
        public const float TargetRepeatabilityHandMm = 4f;
        public const float TargetRepeatabilityPenMm = 6f;
        public const float TargetJitterMm = 1f;

        public DiagnosticMode Active { get; private set; }
        public string Prompt { get; private set; } = string.Empty;
        public string LastReport { get; private set; } = string.Empty;
        public string LastSummary { get; private set; } = string.Empty;
        public string LastReportPath { get; private set; }
        public float Progress { get; private set; }
        public event Action<DiagnosticMode> Completed;

        readonly List<Vector2> _targets = new List<Vector2>();
        readonly List<PointStats> _stats = new List<PointStats>();
        readonly PointStats _jitter = new PointStats();
        int _index;
        float _time;
        int _dropoutsAtStart;
        float _untrackedAtStart;

        DrawingManager _drawing;
        WhiteboardSurface _board;
        BoardMarkers _markers;

        /// <summary>Context lines (plane fit, tip calibration, settings) supplied by the app for the report.</summary>
        public Func<string> ContextProvider;

        public void Init(DrawingManager drawing, WhiteboardSurface board, BoardMarkers markers)
        {
            _drawing = drawing;
            _board = board;
            _markers = markers;
            _drawing.TipDown += OnTipDown;
        }

        public int CurrentTargetIndex => _targets.Count == 0 ? 0 : _index % _targets.Count;

        public bool Begin(DiagnosticMode mode)
        {
            if (_board == null || !_board.WritingAllowed)
            {
                LastSummary = "Confirm the board alignment first (Board > Confirm).";
                return false;
            }
            Active = mode;
            _time = 0f;
            _index = 0;
            Progress = 0f;
            _dropoutsAtStart = _drawing.DropoutCount;
            _untrackedAtStart = _drawing.UntrackedSeconds;
            _drawing.CaptureOnly = true;

            if (mode == DiagnosticMode.Targets)
            {
                Vector2 s = _board.size;
                _targets.Clear();
                _stats.Clear();
                _targets.Add(Vector2.zero);
                _targets.Add(new Vector2(-0.3f * s.x, 0.3f * s.y));
                _targets.Add(new Vector2(0.3f * s.x, 0.3f * s.y));
                _targets.Add(new Vector2(0.3f * s.x, -0.3f * s.y));
                _targets.Add(new Vector2(-0.3f * s.x, -0.3f * s.y));
                for (int i = 0; i < _targets.Count; i++) _stats.Add(new PointStats());
                DrawTargets();
                Prompt = TargetPrompt();
            }
            else
            {
                _jitter.Reset();
                _markers.Hide();
                Prompt = "JITTER TEST: hold the tip still on (or just above) the board for 4 seconds.";
            }
            return true;
        }

        public void Cancel()
        {
            if (Active == DiagnosticMode.None) return;
            Active = DiagnosticMode.None;
            _drawing.CaptureOnly = false;
            _markers.Hide();
            Prompt = string.Empty;
            LastSummary = "Accuracy check cancelled.";
        }

        string TargetPrompt()
        {
            int total = _targets.Count * Repeats;
            string how = _drawing.capture.mode == ActivationMode.ContactLike
                ? "touch the tip to"
                : "put the tip on the cross and " + _drawing.Provider.DrawControlHint + " at";
            return $"ACCURACY CHECK {_index + 1}/{total}: {how} the highlighted cross. Lift away between touches.";
        }

        void OnTipDown(Vector3 boardPoint)
        {
            if (Active != DiagnosticMode.Targets) return;
            int t = CurrentTargetIndex;
            var p = new Vector2(boardPoint.x, boardPoint.y);
            if ((p - _targets[t]).magnitude > AcceptRadius)
            {
                Prompt = "Too far from the highlighted cross; not counted. " + TargetPrompt();
                return;
            }
            _stats[t].Add(boardPoint);
            _index++;
            Progress = (float)_index / (_targets.Count * Repeats);
            if (_index >= _targets.Count * Repeats)
            {
                FinishTargets();
            }
            else
            {
                DrawTargets();
                Prompt = TargetPrompt();
            }
        }

        public void Tick(float dt)
        {
            if (Active != DiagnosticMode.Jitter) return;
            _time += dt;
            Progress = Mathf.Clamp01(_time / JitterSeconds);
            if (_time > 0.5f && _drawing.HasBoardPoint && _drawing.LastSample.tracked)
                _jitter.Add(_drawing.LastBoardPoint);
            if (_time >= JitterSeconds) FinishJitter();
        }

        void DrawTargets()
        {
            int current = CurrentTargetIndex;
            _markers.Begin();
            for (int i = 0; i < _targets.Count; i++)
            {
                bool hot = i == current;
                _markers.Cross(_targets[i], hot ? 0.012f : 0.006f,
                    hot ? new Color32(255, 120, 0, 255) : new Color32(120, 120, 120, 200), hot ? 0.001f : 0.0006f);
            }
            _markers.End();
        }

        float TargetForSource()
        {
            switch (_drawing.Provider.Source)
            {
                case StylusSource.Hand: return TargetRepeatabilityHandMm;
                case StylusSource.EstimatedPen: return TargetRepeatabilityPenMm;
                default: return TargetRepeatabilityControllerMm;
            }
        }

        void FinishTargets()
        {
            var sb = new StringBuilder(1024);
            Header(sb, "Target repeatability check");
            float sumRms = 0f, worstRms = 0f, sumOffset = 0f, sumHeightStd = 0f;
            for (int i = 0; i < _targets.Count; i++)
            {
                PointStats st = _stats[i];
                float rms = st.InPlaneRms * 1000f;
                Vector2 off = st.OffsetFrom(_targets[i]) * 1000f;
                float hStd = st.HeightStd * 1000f;
                sumRms += rms; worstRms = Mathf.Max(worstRms, rms); sumOffset += off.magnitude; sumHeightStd += hStd;
                sb.AppendLine($"  target {i + 1} at ({_targets[i].x * 1000f:0}, {_targets[i].y * 1000f:0}) mm: " +
                              $"repeatability {rms:0.0} mm RMS (max {st.InPlaneMax * 1000f:0.0}), " +
                              $"mean offset ({off.x:+0.0;-0.0}, {off.y:+0.0;-0.0}) mm, height {st.Mean.z * 1000f:+0.0;-0.0} mm, height scatter {hStd:0.0} mm");
            }
            int n = _targets.Count;
            float meanRms = sumRms / n, target = TargetForSource();
            sb.AppendLine();
            sb.AppendLine($"Mean repeatability: {meanRms:0.0} mm RMS (worst target {worstRms:0.0} mm). Provisional target for this input: <= {target:0.0} mm.");
            sb.AppendLine($"Mean offset from displayed targets: {sumOffset / n:0.0} mm (includes aiming error).");
            sb.AppendLine($"Mean height scatter at activation: {sumHeightStd / n:0.0} mm.");
            Footer(sb);

            LastSummary = $"Repeatability {meanRms:0.0} mm RMS (target <= {target:0.0}), offset from targets {sumOffset / n:0.0} mm. " +
                          "Repeatability is not absolute accuracy: check the overlay against the real surface.";
            Complete(sb, DiagnosticMode.Targets);
        }

        void FinishJitter()
        {
            var sb = new StringBuilder(512);
            Header(sb, "Still-tip jitter test");
            float rms = _jitter.InPlaneRms * 1000f;
            sb.AppendLine($"Samples: {_jitter.Count} over {JitterSeconds - 0.5f:0.0} s");
            sb.AppendLine($"In-plane jitter: {rms:0.00} mm RMS (max {_jitter.InPlaneMax * 1000f:0.00} mm). Provisional target: <= {TargetJitterMm:0.0} mm.");
            sb.AppendLine($"Height jitter: {_jitter.HeightStd * 1000f:0.00} mm (std). Mean height {_jitter.Mean.z * 1000f:+0.0;-0.0} mm.");
            sb.AppendLine("Includes hand tremor as well as tracking noise; rest your hand against the surface for a tracking-dominated figure.");
            Footer(sb);
            LastSummary = _jitter.Count < 10
                ? "Jitter test: too few valid samples (tracking was not valid)."
                : $"Still-tip jitter {rms:0.00} mm RMS in plane, {_jitter.HeightStd * 1000f:0.00} mm in height (includes hand tremor).";
            Complete(sb, DiagnosticMode.Jitter);
        }

        void Header(StringBuilder sb, string title)
        {
            sb.AppendLine("WallInk accuracy report - " + title);
            sb.AppendLine("UTC " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine($"Input: {_drawing.Provider.DisplayName}, {_drawing.Provider.Hand} hand, tip {(_drawing.Provider.TipIsEstimated ? "ESTIMATED (not tracked)" : "from tracked pose")}");
            sb.AppendLine($"Activation: {_drawing.capture.mode}; board {_board.size.x * 100f:0} x {_board.size.y * 100f:0} cm");
            if (ContextProvider != null) sb.AppendLine(ContextProvider());
            sb.AppendLine();
        }

        void Footer(StringBuilder sb)
        {
            _drawing.GetFrameStats(out float avg, out float max);
            sb.AppendLine();
            sb.AppendLine($"Tracking dropouts during the test: {_drawing.DropoutCount - _dropoutsAtStart} ({_drawing.UntrackedSeconds - _untrackedAtStart:0.00} s untracked).");
            sb.AppendLine($"Frame time over the last 120 frames: mean {avg:0.0} ms, worst {max:0.0} ms.");
            sb.AppendLine($"Sample timestamps: {(_drawing.LastSample.timestampFromTracking ? "tracking system clock" : "frame clock (no tracking timestamp available)")}.");
            sb.AppendLine($"Smoothing lag (theoretical, from filter settings): {OneEuroFilter.EstimatedDelayMs(_drawing.build.minCutoffHz, _drawing.build.beta, 0.05f):0.0} ms at 5 cm/s, " +
                          $"{OneEuroFilter.EstimatedDelayMs(_drawing.build.minCutoffHz, _drawing.build.beta, 0.3f):0.0} ms at 30 cm/s.");
            sb.AppendLine();
            sb.AppendLine("What this does not show: absolute alignment with the physical surface and end-to-end latency.");
            sb.AppendLine("The targets are virtual, so these figures describe consistency, not truth. Judge physical alignment");
            sb.AppendLine("by eye against the board outline, centre cross and grid, and correct it with Board > Realign.");
        }

        void Complete(StringBuilder sb, DiagnosticMode mode)
        {
            LastReport = sb.ToString();
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "wallink-accuracy-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(path, LastReport);
                LastReportPath = path;
            }
            catch (Exception e)
            {
                LastReportPath = null;
                Debug.LogWarning("WallInk: could not write accuracy report: " + e.Message);
            }
            Debug.Log(LastReport);
            Active = DiagnosticMode.None;
            _drawing.CaptureOnly = false;
            _markers.Hide();
            Prompt = string.Empty;
            Completed?.Invoke(mode);
        }
    }
}
