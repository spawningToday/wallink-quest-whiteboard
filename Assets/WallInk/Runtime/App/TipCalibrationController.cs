using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    public enum CalibrationMode
    {
        None,
        /// <summary>Tip calibration: rock the handle about a fixed tip to solve the tip offset.</summary>
        Pivot,
        /// <summary>Surface-offset calibration: rest the tip on the board to measure its reported height at contact.</summary>
        Touch,
        /// <summary>Pen quick calibration: hold the pen tip on a displayed cross to solve the grip-relative offset.</summary>
        PenQuick,
    }

    /// <summary>
    /// Input-tip calibration, kept separate from surface (board) calibration:
    ///
    ///  - Pivot solves WHERE the tip is relative to the tracked reference (controller body or
    ///    pen grip frame). It does not use the board at all.
    ///  - Touch measures the residual height the calibrated tip reports while resting on the
    ///    board, and stores it as a per-input surface offset. It absorbs small tip and plane
    ///    errors along the normal only; it cannot fix in-plane offsets.
    ///  - PenQuick assumes the board is already aligned and uses a known point on it.
    /// </summary>
    public sealed class TipCalibrationController
    {
        public const float PivotMaxRmsMm = 8f;
        public const float TouchMaxOffsetMm = 60f;
        public const float PenMaxOffsetM = 0.12f;

        public CalibrationMode Active { get; private set; }
        public string Prompt { get; private set; } = string.Empty;
        public string LastResult { get; private set; } = string.Empty;
        public float Progress { get; private set; }
        public bool HasPivotResult { get; private set; }
        public PivotCalibrationResult LastPivot { get; private set; }
        public float LastTouchStdMm { get; private set; } = -1f;
        public float LastPenSpreadMm { get; private set; } = -1f;
        /// <summary>Board point the pen tip must rest on during pen quick calibration.</summary>
        public Vector2 PenTarget { get; private set; }

        /// <summary>Raised when a procedure finishes. Argument: success.</summary>
        public event Action<CalibrationMode, bool> Completed;

        readonly List<Vector3> _positions = new List<Vector3>(700);
        readonly List<Quaternion> _rotations = new List<Quaternion>(700);
        readonly List<float> _heights = new List<float>(300);
        float _time;
        float _prepare;
        float _duration;

        public void Begin(CalibrationMode mode, IStylusInputProvider provider, WhiteboardSurface board)
        {
            Active = CalibrationMode.None;
            _positions.Clear();
            _rotations.Clear();
            _heights.Clear();
            _time = 0f;
            Progress = 0f;

            if (provider == null)
            {
                LastResult = "No input selected.";
                return;
            }
            if (mode == CalibrationMode.Pivot && !provider.SupportsPivotCalibration)
            {
                LastResult = "Tip calibration is not needed for the fingertip: the tracked joint is the tip. Use Touch offset instead.";
                return;
            }
            if ((mode == CalibrationMode.Touch || mode == CalibrationMode.PenQuick) && (board == null || !board.Exists))
            {
                LastResult = "Place the board first. This step measures against the board.";
                return;
            }
            if (mode == CalibrationMode.PenQuick && !(provider is CalibratedPenInputProvider))
            {
                LastResult = "Pen quick calibration applies to the Pen input only.";
                return;
            }

            Active = mode;
            switch (mode)
            {
                case CalibrationMode.Pivot:
                    _prepare = 2.5f; _duration = 6f;
                    Prompt = "TIP CALIBRATION: rest the tip on one fixed spot (a dent or pencil mark helps). When capture starts, " +
                             "keep the tip there and slowly rock the handle in a wide cone, tilting in every direction.";
                    break;
                case CalibrationMode.Touch:
                    _prepare = 2f; _duration = 1.5f;
                    Prompt = "TOUCH OFFSET: rest the tip on the board surface, as if writing, and hold still.";
                    break;
                case CalibrationMode.PenQuick:
                    _prepare = 2.5f; _duration = 1.2f;
                    PenTarget = Vector2.zero;
                    Prompt = "PEN CALIBRATION: hold the pen in your normal writing grip, put its tip on the orange cross and hold still.";
                    break;
            }
        }

        public void Cancel()
        {
            if (Active == CalibrationMode.None) return;
            Active = CalibrationMode.None;
            LastResult = "Calibration cancelled.";
        }

        public void Tick(float dt, IStylusInputProvider provider, WhiteboardSurface board, StylusSample s)
        {
            if (Active == CalibrationMode.None) return;
            _time += dt;
            if (_time < _prepare)
            {
                Progress = 0f;
                return;
            }
            Progress = Mathf.Clamp01((_time - _prepare) / _duration);

            if (provider.TryGetReferencePose(out Vector3 p, out Quaternion r))
            {
                if (Active == CalibrationMode.Pivot && _positions.Count < 700)
                {
                    _positions.Add(p);
                    _rotations.Add(r);
                }
                else if (Active == CalibrationMode.Touch && s.tracked)
                {
                    _heights.Add(board.Frame.SignedHeight(s.tipPosition) * 1000f);
                }
                else if (Active == CalibrationMode.PenQuick && s.tracked)
                {
                    // The tip is known to be at the target, so the offset in the grip frame is
                    // the target position expressed in that frame.
                    Vector3 target = board.Frame.BoardToWorld(PenTarget);
                    _positions.Add(QuatUtil.Inverse(r) * (target - p));
                }
            }

            if (_time >= _prepare + _duration) Finish(provider);
        }

        void Finish(IStylusInputProvider provider)
        {
            CalibrationMode mode = Active;
            Active = CalibrationMode.None;
            bool ok = false;

            if (mode == CalibrationMode.Pivot)
            {
                PivotCalibrationResult res = PivotCalibration.Solve(_positions, _rotations);
                LastPivot = res;
                HasPivotResult = true;
                float rmsMm = res.rmsResidual * 1000f;
                if (!res.success)
                {
                    LastResult = "Tip calibration failed: " + res.message;
                }
                else if (rmsMm > PivotMaxRmsMm || res.localTipOffset.magnitude > 0.3f)
                {
                    LastResult = $"Tip calibration rejected: samples disagreed by {rmsMm:0.0} mm RMS. The tip probably slid" +
                                 (provider.TipIsEstimated ? " or the grip shifted" : "") + ". Previous calibration kept.";
                }
                else
                {
                    provider.LocalTipOffset = res.localTipOffset;
                    (provider as CalibratedPenInputProvider)?.CaptureGripSignature();
                    ok = true;
                    LastResult = $"Tip calibrated. Consistency {rmsMm:0.0} mm RMS over {res.sampleCount} samples, " +
                                 $"{res.angularSpreadDeg:0} deg of rotation. (Consistency is not absolute accuracy.)";
                }
            }
            else if (mode == CalibrationMode.Touch)
            {
                if (_heights.Count < 20)
                {
                    LastResult = "Touch offset failed: tracking was not valid long enough. Keep the tip visible to the headset.";
                }
                else
                {
                    float mean = 0f;
                    for (int i = 0; i < _heights.Count; i++) mean += _heights[i];
                    mean /= _heights.Count;
                    float var = 0f;
                    for (int i = 0; i < _heights.Count; i++) var += (_heights[i] - mean) * (_heights[i] - mean);
                    LastTouchStdMm = Mathf.Sqrt(var / _heights.Count);
                    float median = PointStats.Median(_heights);
                    if (Mathf.Abs(median) > TouchMaxOffsetMm)
                    {
                        LastResult = $"Touch offset rejected: the tip was {median:0} mm from the board plane. " +
                                     "That is too far to be a contact offset; realign the board (Board > Realign) and retry.";
                    }
                    else
                    {
                        provider.SurfaceOffsetMm = median;
                        ok = true;
                        LastResult = $"Touch offset set to {median:0.0} mm (scatter {LastTouchStdMm:0.0} mm while held still).";
                    }
                }
            }
            else if (mode == CalibrationMode.PenQuick)
            {
                var pen = (CalibratedPenInputProvider)provider;
                if (_positions.Count < 20)
                {
                    LastResult = "Pen calibration failed: thumb, index tip and knuckle must stay visible to the headset.";
                }
                else
                {
                    Vector3 mean = Vector3.zero;
                    for (int i = 0; i < _positions.Count; i++) mean += _positions[i];
                    mean /= _positions.Count;
                    float sum = 0f;
                    for (int i = 0; i < _positions.Count; i++) sum += (_positions[i] - mean).sqrMagnitude;
                    LastPenSpreadMm = Mathf.Sqrt(sum / _positions.Count) * 1000f;
                    if (mean.magnitude > PenMaxOffsetM)
                    {
                        LastResult = $"Pen calibration rejected: the grip was {mean.magnitude * 100f:0} cm from the cross. Put the pen tip on the cross.";
                    }
                    else
                    {
                        pen.LocalTipOffset = mean;
                        pen.SurfaceOffsetMm = 0f;
                        pen.CaptureGripSignature();
                        ok = true;
                        LastResult = $"Pen tip estimated {mean.magnitude * 1000f:0} mm from the grip point (scatter {LastPenSpreadMm:0.0} mm). " +
                                     "This is an estimate: recalibrate whenever you re-grip the pen.";
                    }
                }
            }

            Prompt = string.Empty;
            Completed?.Invoke(mode, ok);
        }
    }
}
