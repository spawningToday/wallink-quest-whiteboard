using System;
using UnityEngine;

namespace WallInk
{
    public enum StylusSource
    {
        Controller,    // tracked controller with a calibrated virtual tip
        Hand,          // bare index fingertip from hand tracking
        EstimatedPen,  // ordinary pen: tip ESTIMATED from hand landmarks, never measured
        Simulated,     // editor mouse simulation
    }

    public enum Handedness { Left, Right }

    public enum ActivationMode
    {
        /// <summary>Ink while the tip is inferred to be at the surface (distance to the calibrated plane).</summary>
        ContactLike,
        /// <summary>Ink only while a trigger or deliberate gesture is held; the tip is projected onto the board.</summary>
        Explicit,
    }

    /// <summary>One frame of writing-tip input in world space.</summary>
    public struct StylusSample
    {
        /// <summary>False when the pose must not be used this frame (lost, occluded, low confidence).</summary>
        public bool tracked;
        /// <summary>0..1. 1 when the SDK reports high confidence or offers nothing finer.</summary>
        public float confidence;
        public Vector3 tipPosition;
        public Quaternion tipRotation;
        /// <summary>Explicit draw control (trigger or gesture) after hysteresis.</summary>
        public bool drawHeld;
        /// <summary>Optional analog value; see <see cref="IStylusInputProvider.AnalogMeaning"/>. Never pressure.</summary>
        public float analog;
        /// <summary>Seconds. Comparable only between samples of the same provider.</summary>
        public double timestamp;
        /// <summary>True when the timestamp comes from the tracking system rather than the frame clock.</summary>
        public bool timestampFromTracking;
        /// <summary>Human-readable reason when tracked is false.</summary>
        public string invalidReason;
    }

    /// <summary>
    /// SDK-independent source of writing-tip samples. Drawing logic depends only on this, so a
    /// real tracked stylus can be added later as one more implementation.
    /// </summary>
    public interface IStylusInputProvider
    {
        StylusSource Source { get; }
        Handedness Hand { get; set; }
        string DisplayName { get; }
        /// <summary>What <see cref="StylusSample.analog"/> means, e.g. "trigger pull 0..1".</summary>
        string AnalogMeaning { get; }
        /// <summary>How the user holds the explicit draw control, for on-screen hints.</summary>
        string DrawControlHint { get; }
        /// <summary>True when the tip is an estimate rather than a tracked point.</summary>
        bool TipIsEstimated { get; }
        /// <summary>False when this input source cannot be used right now (e.g. controllers put down).</summary>
        bool IsAvailable { get; }

        /// <summary>Tip offset in the tracked reference frame, metres (tip calibration).</summary>
        Vector3 LocalTipOffset { get; set; }
        /// <summary>Measured tip height when the tip rests on the surface, millimetres (surface-offset calibration).</summary>
        float SurfaceOffsetMm { get; set; }
        bool SupportsPivotCalibration { get; }

        /// <summary>Called once per frame before sampling.</summary>
        void Tick(float deltaTime);
        StylusSample GetSample();
        /// <summary>Pose of the tracked reference frame that <see cref="LocalTipOffset"/> is relative to.</summary>
        bool TryGetReferencePose(out Vector3 position, out Quaternion rotation);
    }

    public enum XRButton
    {
        Primary,        // A on the right controller, X on the left
        Secondary,      // B on the right controller, Y on the left
        Menu,
        ThumbstickClick,
    }

    public enum HandJoint { Wrist, ThumbTip, IndexProximal, IndexTip, MiddleTip }

    /// <summary>
    /// The narrow slice of the XR platform the app needs. One implementation wraps the Meta XR
    /// SDK; another simulates a device in the editor. Poses are world space.
    /// </summary>
    public interface IXRDevice
    {
        string Description { get; }
        bool TryGetHeadPose(out Vector3 position, out Quaternion rotation);

        bool IsControllerTracked(Handedness hand);
        bool TryGetControllerPose(Handedness hand, out Vector3 position, out Quaternion rotation);
        /// <summary>Ray used to point at the palette and at surfaces from a distance.</summary>
        bool TryGetPointerRay(Handedness hand, out Vector3 origin, out Vector3 direction);
        float GetTrigger(Handedness hand);
        float GetGrip(Handedness hand);
        Vector2 GetThumbstick(Handedness hand);
        bool GetButton(Handedness hand, XRButton button);
        void Haptic(Handedness hand, float amplitude, float seconds);

        bool IsHandTracked(Handedness hand);
        /// <summary>1 = high confidence, about 0.5 = low confidence, 0 = not tracked.</summary>
        float GetHandConfidence(Handedness hand);
        bool TryGetHandJoint(Handedness hand, HandJoint joint, out Vector3 position, out Quaternion rotation);

        /// <summary>Tracking-system time for the current poses, if the platform exposes one.</summary>
        bool TryGetTrackingTime(out double seconds);

        /// <summary>Raised on recenter, tracking-origin change, focus loss, headset removal or tracking loss. The argument explains why.</summary>
        event Action<string> TrackingDisrupted;

        /// <summary>Raised when input was interrupted without the world shifting (system menu opened, focus lost). Strokes end; alignment is kept.</summary>
        event Action<string> InputInterrupted;
    }
}
