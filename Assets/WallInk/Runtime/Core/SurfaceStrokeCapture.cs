using System;
using UnityEngine;

namespace WallInk
{
    /// <summary>Contact inference with hysteresis from the tip's signed height above the board.</summary>
    public struct ContactDetector
    {
        public bool InContact { get; private set; }
        /// <summary>True when the last sample was further behind the plane than allowed.</summary>
        public bool Implausible { get; private set; }

        public void Reset()
        {
            InContact = false;
            Implausible = false;
        }

        /// <summary>
        /// heightMm is positive in front of the surface. Contact begins at or below enterMm and
        /// only ends above exitMm (exitMm &gt; enterMm), so tracking noise around a single
        /// threshold cannot chop a stroke into pieces.
        /// </summary>
        public bool Update(float heightMm, float enterMm, float exitMm, float maxPenetrationMm)
        {
            Implausible = heightMm < -maxPenetrationMm;
            if (Implausible)
            {
                InContact = false;
                return false;
            }
            if (!InContact)
            {
                if (heightMm <= enterMm) InContact = true;
            }
            else if (heightMm > exitMm)
            {
                InContact = false;
            }
            return InContact;
        }
    }

    public enum CaptureEvent { None, Begin, Continue, End }

    public enum CaptureEndReason
    {
        None,
        Released,      // contact lifted or trigger/gesture released
        TrackingLost,
        Implausible,   // tip far behind the surface
        Jump,          // pose jumped further than a hand can move in one frame
        Stale,         // too long since the previous accepted sample (hitch, pause)
        LeftBounds,
        Suppressed,    // pointer went onto UI
        Interrupted,   // recenter, pause, input switch, alignment uncertain...
    }

    public enum TipState
    {
        Untracked,
        Hover,       // tracked, not inking
        Drawing,
        NeedsRearm,  // must lift / release once before inking can resume
        OutOfBounds, // would ink, but the projected point is off the board
    }

    [Serializable]
    public class CaptureSettings
    {
        public ActivationMode mode = ActivationMode.Explicit;

        [Header("Contact-like mode (millimetres)")]
        public float contactEnterMm = 4f;
        public float contactExitMm = 10f;
        public float maxPenetrationMm = 40f;

        [Header("Explicit mode")]
        [Tooltip("Ink only when the tip is within this distance of the board plane (either side).")]
        public float explicitMaxDistanceMm = 120f;

        [Header("Plausibility")]
        [Tooltip("Largest accepted move between consecutive samples.")]
        public float maxJumpMm = 70f;
        [Tooltip("Moves faster than this (and longer than 10 mm) are treated as tracking glitches.")]
        public float maxSpeedMps = 6f;
        [Tooltip("A sample arriving later than this after the previous one never connects to it.")]
        public float maxGapSeconds = 0.2f;
    }

    public struct CaptureInput
    {
        public bool tracked;
        /// <summary>(x, y, height) in board coordinates, metres; surface offset already removed from height.</summary>
        public Vector3 boardPoint;
        public bool explicitHeld;
        public double time;
        /// <summary>True while the pointer is on UI or the app otherwise forbids inking.</summary>
        public bool suppressed;
    }

    public struct CaptureOutput
    {
        public CaptureEvent evt;
        public CaptureEndReason endReason;
        public Vector2 point;
        public TipState state;
        public float heightMm;
        public bool inBounds;
    }

    /// <summary>
    /// Decides, frame by frame, whether the tip is inking on the board. Pure logic with no
    /// Unity scene dependencies, so every rule below is unit-tested:
    ///  - a stroke ends on tracking loss, implausible depth, a pose jump or a stale sample;
    ///  - after such an interruption the user must deliberately lift / release once ("re-arm")
    ///    before inking resumes, so a recovered pose never connects to an old point;
    ///  - a tap that begins and ends on consecutive frames still yields a Begin (a dot).
    /// </summary>
    public sealed class SurfaceStrokeCapture
    {
        ContactDetector _contact;
        bool _drawing;
        bool _needsRearm;
        Vector2 _lastPoint;
        double _lastTime;

        public bool IsDrawing => _drawing;
        public bool NeedsRearm => _needsRearm;
        public CaptureEndReason LastEndReason { get; private set; }

        public void Reset()
        {
            _contact.Reset();
            _drawing = false;
            _needsRearm = false;
            LastEndReason = CaptureEndReason.None;
        }

        /// <summary>
        /// External interruption (recenter, pause, input switch, board moved...). Returns true
        /// if a stroke was in progress and must be finalized by the caller.
        /// </summary>
        public bool Interrupt(CaptureEndReason reason = CaptureEndReason.Interrupted)
        {
            bool was = _drawing;
            _drawing = false;
            _needsRearm = true;
            _contact.Reset();
            if (was) LastEndReason = reason;
            return was;
        }

        public CaptureOutput Step(in CaptureInput input, Vector2 boardSize, CaptureSettings s)
        {
            var o = new CaptureOutput
            {
                point = new Vector2(input.boardPoint.x, input.boardPoint.y),
                heightMm = input.boardPoint.z * 1000f,
            };

            if (!input.tracked)
            {
                _contact.Reset();
                o.state = TipState.Untracked;
                if (_drawing) End(ref o, CaptureEndReason.TrackingLost);
                // Any tracking gap demands a deliberate restart, even if no stroke was active:
                // the pose that comes back may already be "in contact" by accident.
                _needsRearm = true;
                return o;
            }

            o.inBounds = Mathf.Abs(o.point.x) <= boardSize.x * 0.5f && Mathf.Abs(o.point.y) <= boardSize.y * 0.5f;

            bool active;
            if (s.mode == ActivationMode.ContactLike)
            {
                active = _contact.Update(o.heightMm, s.contactEnterMm, s.contactExitMm, s.maxPenetrationMm);
                if (_contact.Implausible)
                {
                    if (_drawing) End(ref o, CaptureEndReason.Implausible);
                    _needsRearm = true;
                    o.state = TipState.NeedsRearm;
                    return o;
                }
            }
            else
            {
                active = input.explicitHeld && Mathf.Abs(o.heightMm) <= s.explicitMaxDistanceMm;
            }

            if (input.suppressed)
            {
                if (_drawing) End(ref o, CaptureEndReason.Suppressed);
                // An activation that started on UI must not turn into ink when it slides off.
                if (active) _needsRearm = true;
                o.state = _needsRearm ? TipState.NeedsRearm : TipState.Hover;
                return o;
            }

            if (_needsRearm)
            {
                if (!active) _needsRearm = false;
                o.state = _needsRearm ? TipState.NeedsRearm : TipState.Hover;
                return o;
            }

            if (!active)
            {
                if (_drawing) End(ref o, CaptureEndReason.Released);
                o.state = TipState.Hover;
                return o;
            }

            if (!o.inBounds)
            {
                // Leaving the board ends the stroke. Coming back while still active starts a
                // separate stroke at the entry point; nothing is drawn across the gap.
                if (_drawing) End(ref o, CaptureEndReason.LeftBounds);
                o.state = TipState.OutOfBounds;
                return o;
            }

            if (_drawing)
            {
                float distMm = (o.point - _lastPoint).magnitude * 1000f;
                double dt = input.time - _lastTime;
                bool stale = dt > s.maxGapSeconds;
                bool jump = distMm > s.maxJumpMm ||
                            (dt > 1e-6 && distMm > 10f && distMm / 1000f / dt > s.maxSpeedMps);
                if (stale || jump)
                {
                    End(ref o, stale ? CaptureEndReason.Stale : CaptureEndReason.Jump);
                    _needsRearm = true;
                    o.state = TipState.NeedsRearm;
                    return o;
                }
                o.evt = CaptureEvent.Continue;
            }
            else
            {
                _drawing = true;
                o.evt = CaptureEvent.Begin;
            }

            _lastPoint = o.point;
            _lastTime = input.time;
            o.state = TipState.Drawing;
            return o;
        }

        void End(ref CaptureOutput o, CaptureEndReason reason)
        {
            _drawing = false;
            o.evt = CaptureEvent.End;
            o.endReason = reason;
            LastEndReason = reason;
        }
    }
}
