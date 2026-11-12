using UnityEngine;

namespace WallInk
{
    /// <summary>Shared calibration state and helpers for the stylus providers.</summary>
    public abstract class StylusProviderBase : IStylusInputProvider
    {
        protected readonly IXRDevice Device;

        protected StylusProviderBase(IXRDevice device, Handedness hand)
        {
            Device = device;
            Hand = hand;
        }

        public abstract StylusSource Source { get; }
        public Handedness Hand { get; set; }
        public abstract string DisplayName { get; }
        public abstract string AnalogMeaning { get; }
        public abstract string DrawControlHint { get; }
        public abstract bool TipIsEstimated { get; }
        public abstract bool IsAvailable { get; }
        public Vector3 LocalTipOffset { get; set; }
        public float SurfaceOffsetMm { get; set; }
        public abstract bool SupportsPivotCalibration { get; }

        public virtual void Tick(float deltaTime) { }
        public abstract StylusSample GetSample();
        public abstract bool TryGetReferencePose(out Vector3 position, out Quaternion rotation);

        protected Handedness OtherHand => Hand == Handedness.Left ? Handedness.Right : Handedness.Left;

        protected void Stamp(ref StylusSample s)
        {
            if (Device.TryGetTrackingTime(out double t))
            {
                s.timestamp = t;
                s.timestampFromTracking = true;
            }
            else
            {
                s.timestamp = Time.unscaledTimeAsDouble;
                s.timestampFromTracking = false;
            }
        }

        protected static StylusSample Invalid(string reason)
        {
            return new StylusSample { tracked = false, invalidReason = reason, tipRotation = new Quaternion(0, 0, 0, 1) };
        }

        /// <summary>Distance-based pinch with hysteresis between two joints. Returns false when either joint is unavailable.</summary>
        protected bool PinchHeld(Handedness hand, HandJoint a, HandJoint b, bool wasHeld, float enterMm, float exitMm, out float distanceMm)
        {
            distanceMm = float.MaxValue;
            if (!Device.IsHandTracked(hand)) return false;
            if (!Device.TryGetHandJoint(hand, a, out Vector3 pa, out _) || !Device.TryGetHandJoint(hand, b, out Vector3 pb, out _)) return false;
            distanceMm = (pa - pb).magnitude * 1000f;
            return wasHeld ? distanceMm < exitMm : distanceMm < enterMm;
        }
    }

    /// <summary>
    /// Baseline input: a tracked controller with a virtual tip at a calibrated offset, and the
    /// index trigger as the explicit draw control.
    ///
    /// The default tip is simply a point 6 cm ahead of the controller origin. It is a virtual
    /// point, not a physical feature of the controller, until the user runs tip calibration
    /// with the part of the controller they intend to touch the surface with.
    /// </summary>
    public sealed class ControllerStylusInputProvider : StylusProviderBase
    {
        public static readonly Vector3 DefaultTipOffset = new Vector3(0f, 0f, 0.06f);
        public float triggerPress = 0.55f;
        public float triggerRelease = 0.35f;
        bool _held;

        public ControllerStylusInputProvider(IXRDevice device, Handedness hand) : base(device, hand)
        {
            LocalTipOffset = DefaultTipOffset;
        }

        public override StylusSource Source => StylusSource.Controller;
        public override string DisplayName => "Controller";
        public override string AnalogMeaning => "index trigger pull 0..1 (not pressure)";
        public override string DrawControlHint => "hold the index trigger";
        public override bool TipIsEstimated => false;
        public override bool IsAvailable => Device.IsControllerTracked(Hand);
        public override bool SupportsPivotCalibration => true;

        public override StylusSample GetSample()
        {
            if (!Device.IsControllerTracked(Hand) || !Device.TryGetControllerPose(Hand, out Vector3 p, out Quaternion r))
            {
                _held = false;
                return Invalid("Controller not tracked");
            }
            float trigger = Device.GetTrigger(Hand);
            _held = _held ? trigger > triggerRelease : trigger > triggerPress;
            var s = new StylusSample
            {
                tracked = true,
                confidence = 1f,
                tipPosition = p + r * LocalTipOffset,
                tipRotation = r,
                drawHeld = _held,
                analog = trigger,
            };
            Stamp(ref s);
            return s;
        }

        public override bool TryGetReferencePose(out Vector3 position, out Quaternion rotation)
        {
            return Device.TryGetControllerPose(Hand, out position, out rotation);
        }
    }

    public enum HandDrawGesture
    {
        /// <summary>Thumb to middle finger. Leaves the index fingertip (the writing point) undisturbed.</summary>
        MiddlePinch,
        /// <summary>Thumb to index finger. Familiar, but pinching moves the index tip and so the ink.</summary>
        IndexPinch,
        /// <summary>Thumb to index finger of the OTHER hand. The writing hand stays completely still.</summary>
        OtherHandPinch,
    }

    /// <summary>
    /// Bare-hand input. The writing point is the tracked index fingertip joint itself (never
    /// the pointing ray). A sample is valid only while the hand is tracked with high
    /// confidence; near a wall the fingertip is often occluded from the headset cameras, in
    /// which case the sample is reported untracked and the stroke ends.
    /// </summary>
    public sealed class HandStylusInputProvider : StylusProviderBase
    {
        public HandDrawGesture gesture = HandDrawGesture.MiddlePinch;
        public float pinchEnterMm = 18f;
        public float pinchExitMm = 32f;
        public float minConfidence = 0.9f;
        bool _held;

        public HandStylusInputProvider(IXRDevice device, Handedness hand) : base(device, hand) { }

        public override StylusSource Source => StylusSource.Hand;
        public override string DisplayName => "Fingertip";
        public override string AnalogMeaning => "pinch closeness 0..1 (not pressure)";
        public override bool TipIsEstimated => false;
        public override bool IsAvailable => Device.IsHandTracked(Hand);
        public override bool SupportsPivotCalibration => false;

        public override string DrawControlHint
        {
            get
            {
                switch (gesture)
                {
                    case HandDrawGesture.IndexPinch: return "pinch thumb to index finger";
                    case HandDrawGesture.OtherHandPinch: return "pinch thumb and index of your other hand";
                    default: return "pinch thumb to middle finger";
                }
            }
        }

        public override StylusSample GetSample()
        {
            if (!Device.IsHandTracked(Hand))
            {
                _held = false;
                return Invalid("Hand not tracked");
            }
            float conf = Device.GetHandConfidence(Hand);
            if (conf < minConfidence)
            {
                _held = false;
                return Invalid("Hand tracking confidence is low (fingertip may be hidden from the cameras)");
            }
            if (!Device.TryGetHandJoint(Hand, HandJoint.IndexTip, out Vector3 p, out Quaternion r))
            {
                _held = false;
                return Invalid("Index fingertip joint unavailable");
            }

            float dist;
            switch (gesture)
            {
                case HandDrawGesture.IndexPinch:
                    _held = PinchHeld(Hand, HandJoint.ThumbTip, HandJoint.IndexTip, _held, pinchEnterMm, pinchExitMm, out dist);
                    break;
                case HandDrawGesture.OtherHandPinch:
                    _held = PinchHeld(OtherHand, HandJoint.ThumbTip, HandJoint.IndexTip, _held, pinchEnterMm, pinchExitMm, out dist);
                    break;
                default:
                    _held = PinchHeld(Hand, HandJoint.ThumbTip, HandJoint.MiddleTip, _held, pinchEnterMm, pinchExitMm, out dist);
                    break;
            }

            var s = new StylusSample
            {
                tracked = true,
                confidence = conf,
                tipPosition = p + r * LocalTipOffset,
                tipRotation = r,
                drawHeld = _held,
                analog = Mathf.Clamp01(1f - dist / 60f),
            };
            Stamp(ref s);
            return s;
        }

        public override bool TryGetReferencePose(out Vector3 position, out Quaternion rotation)
        {
            return Device.TryGetHandJoint(Hand, HandJoint.IndexTip, out position, out rotation);
        }
    }

    /// <summary>
    /// EXPERIMENTAL: an ordinary pen or pencil held in the tracked hand.
    ///
    /// Nothing here tracks the pen. The tip is ESTIMATED as a fixed offset in a "grip frame"
    /// built from three hand landmarks, so it is only as good as the hand tracking and only
    /// valid while the grip stays as it was during calibration:
    ///
    ///   G = midpoint of thumb tip and index tip (where a tripod grip pinches the barrel)
    ///   K = index finger proximal joint (the barrel rests against this knuckle)
    ///   +Z = from K toward G (roughly along the barrel toward the nib)
    ///   +Y = from index tip toward thumb tip, made perpendicular to Z
    ///   tip = G + frame * LocalTipOffset
    ///
    /// Assumptions: a tripod or similar grip with thumb and index on the barrel, both visible
    /// to the headset. A pen in the hand hides parts of the fingers from the cameras, so these
    /// landmarks are noisier than with an empty hand, and motion the tracker does not observe
    /// cannot be recovered by calibration. Contact and pressure are not measured.
    ///
    /// If the landmark distances drift from their calibrated values the grip has probably
    /// changed, and the provider reports the sample as untracked until recalibrated.
    /// </summary>
    public sealed class CalibratedPenInputProvider : StylusProviderBase
    {
        public static readonly Vector3 DefaultTipOffset = new Vector3(0f, 0f, 0.025f);

        public HandDrawGesture gesture = HandDrawGesture.OtherHandPinch;
        public float pinchEnterMm = 18f;
        public float pinchExitMm = 32f;
        public float minConfidence = 0.9f;

        /// <summary>Grip signature captured at calibration, millimetres. Zero = not calibrated.</summary>
        public float calibratedSpreadMm;
        public float calibratedLengthMm;
        public float gripToleranceMm = 14f;
        public float gripChangeSeconds = 0.25f;

        bool _held;
        float _gripBadTime;
        float _dt;

        public CalibratedPenInputProvider(IXRDevice device, Handedness hand) : base(device, hand)
        {
            LocalTipOffset = DefaultTipOffset;
        }

        public override StylusSource Source => StylusSource.EstimatedPen;
        public override string DisplayName => "Pen (estimated tip)";
        public override string AnalogMeaning => "pinch closeness of the other hand 0..1 (not pressure)";
        public override string DrawControlHint => "pinch thumb and index of your other hand";
        public override bool TipIsEstimated => true;
        public override bool IsAvailable => Device.IsHandTracked(Hand);
        public override bool SupportsPivotCalibration => true;
        public bool IsCalibrated => calibratedLengthMm > 0f;
        public bool GripChanged { get; private set; }

        public override void Tick(float deltaTime) { _dt = deltaTime; }

        bool TryGetGrip(out Vector3 g, out Quaternion rot, out float spreadMm, out float lengthMm)
        {
            g = default; rot = new Quaternion(0, 0, 0, 1); spreadMm = 0f; lengthMm = 0f;
            if (!Device.TryGetHandJoint(Hand, HandJoint.ThumbTip, out Vector3 thumb, out _)) return false;
            if (!Device.TryGetHandJoint(Hand, HandJoint.IndexTip, out Vector3 index, out _)) return false;
            if (!Device.TryGetHandJoint(Hand, HandJoint.IndexProximal, out Vector3 knuckle, out _)) return false;

            g = (thumb + index) * 0.5f;
            Vector3 z = g - knuckle;
            Vector3 across = thumb - index;
            lengthMm = z.magnitude * 1000f;
            spreadMm = across.magnitude * 1000f;
            if (z.sqrMagnitude < 1e-6f) return false;
            z.Normalize();
            Vector3 x = Vector3.Cross(across, z);
            if (x.sqrMagnitude < 1e-8f) return false; // thumb-index direction parallel to the barrel: frame undefined
            x.Normalize();
            Vector3 y = Vector3.Cross(z, x);
            rot = QuatUtil.FromBasis(x, y, z);
            return true;
        }

        /// <summary>Stores the current grip as the calibrated one. Call when a calibration succeeds.</summary>
        public bool CaptureGripSignature()
        {
            if (!TryGetGrip(out _, out _, out float spread, out float length)) return false;
            calibratedSpreadMm = spread;
            calibratedLengthMm = length;
            _gripBadTime = 0f;
            GripChanged = false;
            return true;
        }

        public override StylusSample GetSample()
        {
            if (!Device.IsHandTracked(Hand))
            {
                _held = false;
                return Invalid("Hand not tracked");
            }
            float conf = Device.GetHandConfidence(Hand);
            if (conf < minConfidence)
            {
                _held = false;
                return Invalid("Hand tracking confidence is low (the pen may be hiding your fingers)");
            }
            if (!TryGetGrip(out Vector3 g, out Quaternion rot, out float spread, out float length))
            {
                _held = false;
                return Invalid("Thumb, index tip or index knuckle not available");
            }

            if (IsCalibrated)
            {
                bool off = Mathf.Abs(spread - calibratedSpreadMm) > gripToleranceMm ||
                           Mathf.Abs(length - calibratedLengthMm) > gripToleranceMm;
                _gripBadTime = off ? _gripBadTime + _dt : 0f;
                GripChanged = _gripBadTime > gripChangeSeconds;
                if (GripChanged)
                {
                    _held = false;
                    return Invalid("Grip changed since calibration. Re-grip as before or recalibrate the pen.");
                }
            }

            _held = PinchHeld(gesture == HandDrawGesture.OtherHandPinch ? OtherHand : Hand,
                HandJoint.ThumbTip, gesture == HandDrawGesture.MiddlePinch ? HandJoint.MiddleTip : HandJoint.IndexTip,
                _held, pinchEnterMm, pinchExitMm, out float dist);
            // Pinching thumb to index on the pen hand is the grip itself, so it cannot be a draw control.
            if (gesture == HandDrawGesture.IndexPinch) _held = false;

            var s = new StylusSample
            {
                tracked = true,
                confidence = conf,
                tipPosition = g + rot * LocalTipOffset,
                tipRotation = rot,
                drawHeld = _held,
                analog = Mathf.Clamp01(1f - dist / 60f),
            };
            Stamp(ref s);
            return s;
        }

        public override bool TryGetReferencePose(out Vector3 position, out Quaternion rotation)
        {
            return TryGetGrip(out position, out rotation, out _, out _);
        }
    }
}
