using System;
using UnityEngine;

namespace WallInk.MetaXR
{
    /// <summary>
    /// IXRDevice on top of the Meta XR Core SDK (OVRCameraRig, OVRInput, OVRPlugin hand state).
    /// This is the only place, together with the scanner and anchor adapters in this folder,
    /// that touches Meta SDK types.
    ///
    /// Verified against com.meta.xr.sdk.core 209.0.0 source:
    ///  - controller poses from OVRInput are local to OVRCameraRig.trackingSpace;
    ///  - with the OpenXR hand skeleton, OVRPlugin.HandState.BonePositions / BoneRotations hold
    ///    tracking-space joint poses indexed by OVRPlugin.BoneId.XRHand_*, converted to Unity
    ///    axes with FromFlippedZVector3f / FromFlippedZQuatf (the same conversion OVRSkeleton uses);
    ///  - OVRManager updates OVRInput each frame, so an OVRManager must be in the scene
    ///    (it is part of the OVRCameraRig prefab).
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class MetaXRDevice : MonoBehaviour, IXRDevice
    {
        [Tooltip("Found automatically when left empty.")]
        public OVRCameraRig rig;

        OVRPlugin.HandState _leftState = new OVRPlugin.HandState();
        OVRPlugin.HandState _rightState = new OVRPlugin.HandState();
        bool _leftValid, _rightValid;
        int _handFrame = -1;
        bool _warnedSkeleton;
        bool _recenterHooked;
        readonly float[] _hapticEnd = new float[2];

        public string Description => "Meta XR Core SDK (OVR) on OpenXR";
        public event Action<string> TrackingDisrupted;
        public event Action<string> InputInterrupted;

        void Awake()
        {
            if (rig == null) rig = FindAnyObjectByType<OVRCameraRig>();
            if (rig == null) Debug.LogError("WallInk: no OVRCameraRig in the scene. Run WallInk > Setup Project and Scene.");
        }

        void OnEnable()
        {
            OVRManager.TrackingOriginChangePending += OnTrackingOriginChange;
            OVRManager.TrackingLost += OnTrackingLost;
            OVRManager.TrackingAcquired += OnTrackingAcquired;
            OVRManager.HMDMounted += OnHmdMounted;
            OVRManager.HMDUnmounted += OnHmdUnmounted;
            OVRManager.InputFocusLost += OnInputFocusLost;
        }

        void OnDisable()
        {
            OVRManager.TrackingOriginChangePending -= OnTrackingOriginChange;
            OVRManager.TrackingLost -= OnTrackingLost;
            OVRManager.TrackingAcquired -= OnTrackingAcquired;
            OVRManager.HMDMounted -= OnHmdMounted;
            OVRManager.HMDUnmounted -= OnHmdUnmounted;
            OVRManager.InputFocusLost -= OnInputFocusLost;
            if (_recenterHooked && OVRManager.display != null) OVRManager.display.RecenteredPose -= OnRecentered;
            _recenterHooked = false;
        }

        void Update()
        {
            // OVRManager.display is created during OVRManager initialisation, which may be after our OnEnable.
            if (!_recenterHooked && OVRManager.display != null)
            {
                OVRManager.display.RecenteredPose += OnRecentered;
                _recenterHooked = true;
            }
            for (int i = 0; i < 2; i++)
            {
                if (_hapticEnd[i] > 0f && Time.unscaledTime >= _hapticEnd[i])
                {
                    _hapticEnd[i] = 0f;
                    OVRInput.SetControllerVibration(0f, 0f, Touch((Handedness)i));
                }
            }
        }

        void OnRecentered() => TrackingDisrupted?.Invoke("view recentered");
        void OnTrackingOriginChange(OVRManager.TrackingOrigin origin, OVRPose? pose) => TrackingDisrupted?.Invoke("tracking origin changed");
        void OnTrackingLost() => TrackingDisrupted?.Invoke("headset tracking lost");
        void OnTrackingAcquired() => TrackingDisrupted?.Invoke("headset tracking recovered");
        void OnHmdMounted() => TrackingDisrupted?.Invoke("headset put back on");
        void OnHmdUnmounted() => InputInterrupted?.Invoke("Headset removed");
        void OnInputFocusLost() => InputInterrupted?.Invoke("System menu opened");

        static OVRInput.Controller Touch(Handedness hand) =>
            hand == Handedness.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

        // ------------------------------------------------------------------ head

        public bool TryGetHeadPose(out Vector3 position, out Quaternion rotation)
        {
            if (rig == null || rig.centerEyeAnchor == null)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return false;
            }
            position = rig.centerEyeAnchor.position;
            rotation = rig.centerEyeAnchor.rotation;
            return true;
        }

        // ------------------------------------------------------------------ controllers

        public bool IsControllerTracked(Handedness hand)
        {
            OVRInput.Controller c = Touch(hand);
            return rig != null && OVRInput.IsControllerConnected(c) &&
                   OVRInput.GetControllerPositionTracked(c) && OVRInput.GetControllerOrientationTracked(c);
        }

        public bool TryGetControllerPose(Handedness hand, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!IsControllerTracked(hand)) return false;
            OVRInput.Controller c = Touch(hand);
            Transform space = rig.trackingSpace;
            position = space.TransformPoint(OVRInput.GetLocalControllerPosition(c));
            rotation = space.rotation * OVRInput.GetLocalControllerRotation(c);
            return true;
        }

        public bool TryGetPointerRay(Handedness hand, out Vector3 origin, out Vector3 direction)
        {
            direction = Vector3.forward;
            if (!TryGetControllerPose(hand, out origin, out Quaternion rotation)) return false;
            direction = rotation * Vector3.forward;
            return true;
        }

        public float GetTrigger(Handedness hand) => OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, Touch(hand));
        public float GetGrip(Handedness hand) => OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, Touch(hand));
        public Vector2 GetThumbstick(Handedness hand) => OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, Touch(hand));

        public bool GetButton(Handedness hand, XRButton button)
        {
            OVRInput.Controller c = Touch(hand);
            switch (button)
            {
                case XRButton.Primary: return OVRInput.Get(OVRInput.Button.One, c);       // A (right) / X (left)
                case XRButton.Secondary: return OVRInput.Get(OVRInput.Button.Two, c);     // B (right) / Y (left)
                case XRButton.Menu: return hand == Handedness.Left && OVRInput.Get(OVRInput.Button.Start, c);
                case XRButton.ThumbstickClick: return OVRInput.Get(OVRInput.Button.PrimaryThumbstick, c);
                default: return false;
            }
        }

        public void Haptic(Handedness hand, float amplitude, float seconds)
        {
            if (!IsControllerTracked(hand)) return;
            OVRInput.SetControllerVibration(1f, Mathf.Clamp01(amplitude), Touch(hand));
            _hapticEnd[(int)hand] = Time.unscaledTime + Mathf.Max(seconds, 0.01f);
        }

        // ------------------------------------------------------------------ hands

        void RefreshHands()
        {
            if (_handFrame == Time.frameCount) return;
            _handFrame = Time.frameCount;
            _leftValid = _rightValid = false;
            if (rig == null) return;

            if (OVRPlugin.HandSkeletonVersion != OVRHandSkeletonVersion.OpenXR)
            {
                if (!_warnedSkeleton && OVRManager.OVRManagerinitialized)
                {
                    _warnedSkeleton = true;
                    Debug.LogError("WallInk: hand input needs the OpenXR hand skeleton. Set Meta > Runtime Settings > Hand Skeleton Version to OpenXR " +
                                   "(WallInk > Setup Project and Scene does this).");
                }
                return;
            }
            _leftValid = ReadHand(OVRPlugin.Hand.HandLeft, ref _leftState);
            _rightValid = ReadHand(OVRPlugin.Hand.HandRight, ref _rightState);
        }

        static bool ReadHand(OVRPlugin.Hand hand, ref OVRPlugin.HandState state)
        {
            if (!OVRPlugin.GetHandState(OVRPlugin.Step.Render, hand, ref state)) return false;
            if ((state.Status & OVRPlugin.HandStatus.HandTracked) == 0) return false;
            int needed = (int)OVRPlugin.BoneId.XRHand_MiddleTip + 1;
            return state.BonePositions != null && state.BoneRotations != null &&
                   state.BonePositions.Length >= needed && state.BoneRotations.Length >= needed;
        }

        public bool IsHandTracked(Handedness hand)
        {
            RefreshHands();
            return hand == Handedness.Left ? _leftValid : _rightValid;
        }

        public float GetHandConfidence(Handedness hand)
        {
            if (!IsHandTracked(hand)) return 0f;
            OVRPlugin.HandState s = hand == Handedness.Left ? _leftState : _rightState;
            bool high = s.HandConfidence == OVRPlugin.TrackingConfidence.High;
            // FingerConfidences is ordered thumb, index, middle, ring, pinky.
            if (high && s.FingerConfidences != null && s.FingerConfidences.Length > 1)
                high = s.FingerConfidences[1] == OVRPlugin.TrackingConfidence.High;
            return high ? 1f : 0.5f;
        }

        public bool TryGetHandJoint(Handedness hand, HandJoint joint, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!IsHandTracked(hand)) return false;
            OVRPlugin.HandState s = hand == Handedness.Left ? _leftState : _rightState;
            int i;
            switch (joint)
            {
                case HandJoint.ThumbTip: i = (int)OVRPlugin.BoneId.XRHand_ThumbTip; break;
                case HandJoint.IndexProximal: i = (int)OVRPlugin.BoneId.XRHand_IndexProximal; break;
                case HandJoint.IndexTip: i = (int)OVRPlugin.BoneId.XRHand_IndexTip; break;
                case HandJoint.MiddleTip: i = (int)OVRPlugin.BoneId.XRHand_MiddleTip; break;
                default: i = (int)OVRPlugin.BoneId.XRHand_Wrist; break;
            }
            Transform space = rig.trackingSpace;
            position = space.TransformPoint(s.BonePositions[i].FromFlippedZVector3f());
            rotation = space.rotation * s.BoneRotations[i].FromFlippedZQuatf();
            return true;
        }

        // ------------------------------------------------------------------ timing

        /// <summary>
        /// Time the runtime associates with this frame's render poses (the head pose state).
        /// It is a predicted display time, not a camera capture time, and is used only to
        /// order samples and detect stale ones.
        /// </summary>
        public bool TryGetTrackingTime(out double seconds)
        {
            seconds = OVRPlugin.GetNodePoseStateRaw(OVRPlugin.Node.Head, OVRPlugin.Step.Render).Time;
            return seconds > 0.0;
        }
    }
}
