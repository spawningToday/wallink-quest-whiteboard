using System;
using UnityEngine;

namespace WallInk
{
    /// <summary>
    /// Desktop stand-in for a headset so board-space logic, tools and UI can be exercised in
    /// the Unity editor with a mouse. It is used automatically when no real XR device
    /// component is present in the scene.
    ///
    ///   Mouse move ........ aims the pointer ray; the simulated tip hovers 3 cm off the board
    ///   Left button ....... trigger (explicit draw control, palette click)
    ///   Right button/Space  push the tip onto the surface (contact-like writing)
    ///   P ................. pinch gesture (hand and pen draw control)
    ///   L (hold) .......... simulate tracking loss
    ///   R ................. simulate a recenter / tracking-origin change
    ///   W A S D, Q E ...... move and turn the camera;  arrow keys = thumbstick
    ///   Z / X / M ......... primary / secondary / menu buttons
    ///
    /// Works with either Active Input Handling setting (Input System package or legacy Input Manager).
    /// </summary>
    public sealed class SimulatedXRDevice : MonoBehaviour, IXRDevice
    {
        public float hoverHeight = 0.03f;
        public float freeDistance = 0.6f;

        /// <summary>Supplies the current board plane so the simulated tip can ride on it.</summary>
        public Func<BoardFrame?> BoardProvider;

        Camera _camera;
        bool _inputBroken;
        Vector3 _tip;
        Quaternion _rot = new Quaternion(0, 0, 0, 1);
        Vector3 _rayOrigin, _rayDir = Vector3.forward;
        bool _lost, _pinch, _contact;
        float _trigger;
        float _yaw;
        bool _prevRecenter;

        public string Description => "Editor simulation (mouse)";
        public event Action<string> TrackingDisrupted;
#pragma warning disable 0067 // never raised by the simulation
        public event Action<string> InputInterrupted;
#pragma warning restore 0067

        void Awake()
        {
            _camera = Camera.main;
            if (_camera == null)
            {
                var go = new GameObject("Sim Camera") { tag = "MainCamera" };
                _camera = go.AddComponent<Camera>();
                _camera.transform.position = new Vector3(0f, 1.5f, 0f);
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.32f, 0.34f, 0.38f, 1f);
                _camera.nearClipPlane = 0.05f;
            }
        }

        void Update()
        {
            if (_inputBroken) return;
            try
            {
                ReadInput();
            }
            catch (InvalidOperationException)
            {
                _inputBroken = true;
                Debug.LogWarning("WallInk simulation could not read keyboard/mouse input. Check Project Settings > Player > Active Input Handling.");
            }
        }

        void ReadInput()
        {
            float dt = Time.unscaledDeltaTime;
            Transform ct = _camera.transform;
            Vector3 move = Vector3.zero;
            if (SimInput.Key(SimKey.W)) move += Vector3.forward;
            if (SimInput.Key(SimKey.S)) move += Vector3.back;
            if (SimInput.Key(SimKey.A)) move += Vector3.left;
            if (SimInput.Key(SimKey.D)) move += Vector3.right;
            if (SimInput.Key(SimKey.Q)) _yaw -= 60f * dt;
            if (SimInput.Key(SimKey.E)) _yaw += 60f * dt;
            ct.rotation = QuatUtil.AngleAxis(_yaw, Vector3.up);
            ct.position += ct.rotation * move * dt;

            _lost = SimInput.Key(SimKey.L);
            _pinch = SimInput.Key(SimKey.P);
            _contact = SimInput.Mouse(1) || SimInput.Key(SimKey.Space);
            _trigger = SimInput.Mouse(0) ? 1f : 0f;
            bool recenter = SimInput.Key(SimKey.R);
            if (recenter && !_prevRecenter) TrackingDisrupted?.Invoke("Simulated recenter");
            _prevRecenter = recenter;

            Ray ray = _camera.ScreenPointToRay(SimInput.MousePosition);
            _rayOrigin = ray.origin;
            _rayDir = ray.direction;
            _rot = QuatUtil.LookRotation(_rayDir, Vector3.up);

            _tip = ray.origin + ray.direction * freeDistance;
            BoardFrame? bf = BoardProvider != null ? BoardProvider() : null;
            if (bf.HasValue)
            {
                BoardFrame f = bf.Value;
                Vector3 n = f.OutwardNormal;
                float denom = Vector3.Dot(ray.direction, n);
                if (denom < -1e-4f)
                {
                    float t = Vector3.Dot(f.origin - ray.origin, n) / denom;
                    if (t > 0f) _tip = ray.origin + ray.direction * t + n * (_contact ? 0f : hoverHeight);
                }
            }
        }

        public bool TryGetHeadPose(out Vector3 position, out Quaternion rotation)
        {
            position = _camera.transform.position;
            rotation = _camera.transform.rotation;
            return true;
        }

        public bool IsControllerTracked(Handedness hand) => !_lost;

        public bool TryGetControllerPose(Handedness hand, out Vector3 position, out Quaternion rotation)
        {
            rotation = _rot;
            // Place the controller so that its default virtual tip lands on the simulated tip.
            position = _tip - _rot * ControllerStylusInputProvider.DefaultTipOffset;
            return !_lost;
        }

        public bool TryGetPointerRay(Handedness hand, out Vector3 origin, out Vector3 direction)
        {
            origin = _rayOrigin;
            direction = _rayDir;
            return !_lost && hand == Handedness.Right;
        }

        public float GetTrigger(Handedness hand) => _trigger;
        public float GetGrip(Handedness hand) => 0f;

        public Vector2 GetThumbstick(Handedness hand)
        {
            if (_inputBroken) return Vector2.zero;
            float x = (SimInput.Key(SimKey.Right) ? 1f : 0f) - (SimInput.Key(SimKey.Left) ? 1f : 0f);
            float y = (SimInput.Key(SimKey.Up) ? 1f : 0f) - (SimInput.Key(SimKey.Down) ? 1f : 0f);
            return new Vector2(x, y);
        }

        public bool GetButton(Handedness hand, XRButton button)
        {
            if (_inputBroken) return false;
            switch (button)
            {
                case XRButton.Primary: return SimInput.Key(SimKey.Z);
                case XRButton.Secondary: return SimInput.Key(SimKey.X);
                case XRButton.Menu: return SimInput.Key(SimKey.M);
                default: return false;
            }
        }

        public void Haptic(Handedness hand, float amplitude, float seconds) { }

        public bool IsHandTracked(Handedness hand) => !_lost;
        public float GetHandConfidence(Handedness hand) => _lost ? 0f : 1f;

        public bool TryGetHandJoint(Handedness hand, HandJoint joint, out Vector3 position, out Quaternion rotation)
        {
            rotation = _rot;
            Vector3 right = _rot * Vector3.right, up = _rot * Vector3.up;
            // The writing hand's index tip is the simulated tip; the other hand floats beside it.
            Vector3 baseTip = _tip + (hand == Handedness.Left ? right * -0.25f : Vector3.zero);
            Vector3 middle = baseTip + right * 0.025f - up * 0.01f;
            switch (joint)
            {
                case HandJoint.IndexTip: position = baseTip; break;
                case HandJoint.MiddleTip: position = middle; break;
                case HandJoint.ThumbTip:
                    // P pinches thumb to middle on the right hand and thumb to index on the left.
                    if (_pinch) position = hand == Handedness.Left ? baseTip + up * 0.004f : middle + up * 0.004f;
                    else position = baseTip + right * 0.02f - up * 0.045f;
                    break;
                case HandJoint.IndexProximal: position = baseTip - _rayDir * 0.08f + up * 0.02f; break;
                default: position = baseTip - _rayDir * 0.16f; break;
            }
            return !_lost;
        }

        public bool TryGetTrackingTime(out double seconds)
        {
            seconds = 0;
            return false;
        }
    }

    internal enum SimKey { W, A, S, D, Q, E, L, P, R, Z, X, M, Space, Left, Right, Up, Down }

    /// <summary>Keyboard and mouse access for the simulation under whichever input backend the project enables.</summary>
    internal static class SimInput
    {
#if ENABLE_INPUT_SYSTEM
        static UnityEngine.InputSystem.Key Map(SimKey k)
        {
            switch (k)
            {
                case SimKey.W: return UnityEngine.InputSystem.Key.W;
                case SimKey.A: return UnityEngine.InputSystem.Key.A;
                case SimKey.S: return UnityEngine.InputSystem.Key.S;
                case SimKey.D: return UnityEngine.InputSystem.Key.D;
                case SimKey.Q: return UnityEngine.InputSystem.Key.Q;
                case SimKey.E: return UnityEngine.InputSystem.Key.E;
                case SimKey.L: return UnityEngine.InputSystem.Key.L;
                case SimKey.P: return UnityEngine.InputSystem.Key.P;
                case SimKey.R: return UnityEngine.InputSystem.Key.R;
                case SimKey.Z: return UnityEngine.InputSystem.Key.Z;
                case SimKey.X: return UnityEngine.InputSystem.Key.X;
                case SimKey.M: return UnityEngine.InputSystem.Key.M;
                case SimKey.Left: return UnityEngine.InputSystem.Key.LeftArrow;
                case SimKey.Right: return UnityEngine.InputSystem.Key.RightArrow;
                case SimKey.Up: return UnityEngine.InputSystem.Key.UpArrow;
                case SimKey.Down: return UnityEngine.InputSystem.Key.DownArrow;
                default: return UnityEngine.InputSystem.Key.Space;
            }
        }

        public static bool Key(SimKey k)
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            return kb != null && kb[Map(k)].isPressed;
        }

        public static bool Mouse(int button)
        {
            var m = UnityEngine.InputSystem.Mouse.current;
            if (m == null) return false;
            return button == 0 ? m.leftButton.isPressed : m.rightButton.isPressed;
        }

        public static Vector3 MousePosition
        {
            get
            {
                var m = UnityEngine.InputSystem.Mouse.current;
                if (m == null) return Vector3.zero;
                Vector2 p = m.position.ReadValue();
                return new Vector3(p.x, p.y, 0f);
            }
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        static KeyCode Map(SimKey k)
        {
            switch (k)
            {
                case SimKey.W: return KeyCode.W;
                case SimKey.A: return KeyCode.A;
                case SimKey.S: return KeyCode.S;
                case SimKey.D: return KeyCode.D;
                case SimKey.Q: return KeyCode.Q;
                case SimKey.E: return KeyCode.E;
                case SimKey.L: return KeyCode.L;
                case SimKey.P: return KeyCode.P;
                case SimKey.R: return KeyCode.R;
                case SimKey.Z: return KeyCode.Z;
                case SimKey.X: return KeyCode.X;
                case SimKey.M: return KeyCode.M;
                case SimKey.Left: return KeyCode.LeftArrow;
                case SimKey.Right: return KeyCode.RightArrow;
                case SimKey.Up: return KeyCode.UpArrow;
                case SimKey.Down: return KeyCode.DownArrow;
                default: return KeyCode.Space;
            }
        }

        public static bool Key(SimKey k) => Input.GetKey(Map(k));
        public static bool Mouse(int button) => Input.GetMouseButton(button);
        public static Vector3 MousePosition => Input.mousePosition;
#else
        public static bool Key(SimKey k) => false;
        public static bool Mouse(int button) => false;
        public static Vector3 MousePosition => Vector3.zero;
#endif
    }
}
