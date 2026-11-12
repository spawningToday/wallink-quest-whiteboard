using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    public enum PlacementMethod
    {
        None,
        /// <summary>Pick a plane detected by the headset's room scan.</summary>
        Detected,
        /// <summary>Touch three or more well-separated points on the surface; a plane is fitted to them.</summary>
        Points,
        /// <summary>Hold the board at the end of the pointer ray and drop it.</summary>
        Pointer,
    }

    /// <summary>
    /// Surface calibration: establishes the board plane and bounds. It only ever produces a
    /// PREVIEW; the user confirms and fine-tunes it afterwards. The plane is fitted once, on
    /// request, and never refitted during writing, so existing ink cannot shift on its own.
    /// </summary>
    public sealed class SurfacePlacementController
    {
        public const int MaxPoints = 16;
        public static readonly Vector2 DefaultSize = new Vector2(1.2f, 0.8f);

        public PlacementMethod Active { get; private set; }
        public string Prompt { get; private set; } = string.Empty;
        public bool HasPreview { get; private set; }
        public BoardFrame Preview { get; private set; }
        public bool HasFit { get; private set; }
        public PlaneFitResult LastFit { get; private set; }
        public readonly List<Vector3> Points = new List<Vector3>();

        IXRDevice _device;
        ISurfaceScanner _scanner;
        bool _prevHeld = true; // require a fresh press after starting
        float _pointerDistance = 0.7f;

        public void Init(IXRDevice device, ISurfaceScanner scanner)
        {
            _device = device;
            _scanner = scanner;
        }

        public bool ScannerAvailable => _scanner != null;

        public void Begin(PlacementMethod method)
        {
            Active = method;
            HasPreview = false;
            HasFit = false;
            Points.Clear();
            _prevHeld = true;
            switch (method)
            {
                case PlacementMethod.Detected:
                    if (_scanner == null)
                    {
                        Prompt = "Automatic detection is not available here. Use '3+ points' or 'Pointer' placement.";
                        Active = PlacementMethod.None;
                    }
                    else
                    {
                        _scanner.RequestScan();
                        Prompt = "Looking for scanned surfaces...";
                    }
                    break;
                case PlacementMethod.Points:
                    Prompt = "Touch the tip to the surface and press the draw control at 3 or more points, at least 20 cm apart and not in a line. " +
                             "More points (5-8, spread over the area) give a better fit.";
                    break;
                case PlacementMethod.Pointer:
                    Prompt = "Point where the board should go. Thumbstick up/down changes distance. Press the draw control to drop it, then fine-tune.";
                    break;
            }
        }

        public void Cancel()
        {
            Active = PlacementMethod.None;
            HasPreview = false;
            Prompt = string.Empty;
        }

        public void RemoveLastPoint()
        {
            if (Points.Count == 0) return;
            Points.RemoveAt(Points.Count - 1);
            Refit(default, false);
        }

        /// <summary>Points mode: accept the current fit. Returns false (with an explanation in Prompt) if the fit is not usable.</summary>
        public bool TryFinishPoints(out BoardFrame frame)
        {
            frame = Preview;
            if (Active != PlacementMethod.Points) return false;
            if (!HasFit || !LastFit.success)
            {
                Prompt = (HasFit ? LastFit.DescribeFailure() : "Need at least 3 points.") + " Add more points, or remove the last one and retry.";
                return false;
            }
            Active = PlacementMethod.None;
            return true;
        }

        /// <summary>
        /// Advances placement. Returns true on the frame the user confirmed a placement
        /// (Detected and Pointer modes); the result is then in <see cref="Preview"/>.
        /// </summary>
        public bool Tick(float dt, IStylusInputProvider provider, StylusSample sample, bool pointerOverUi)
        {
            if (Active == PlacementMethod.None) return false;

            bool held = sample.tracked && sample.drawHeld && !pointerOverUi;
            bool pressed = held && !_prevHeld;
            _prevHeld = sample.tracked ? sample.drawHeld : _prevHeld;

            _device.TryGetHeadPose(out Vector3 head, out Quaternion headRot);
            Vector3 headForward = headRot * Vector3.forward;

            switch (Active)
            {
                case PlacementMethod.Points:
                    if (pressed && Points.Count < MaxPoints)
                    {
                        Points.Add(sample.tipPosition);
                        Refit(head, true);
                    }
                    return false;

                case PlacementMethod.Pointer:
                {
                    if (!TryGetAimRay(provider, sample, head, out Vector3 origin, out Vector3 dir))
                    {
                        HasPreview = false;
                        return false;
                    }
                    float stick = _device.GetThumbstick(provider.Hand).y;
                    if (Mathf.Abs(stick) > 0.2f) _pointerDistance = Mathf.Clamp(_pointerDistance + stick * dt * 0.6f, 0.25f, 4f);
                    Vector3 center = origin + dir * _pointerDistance;
                    // Face the board toward the user, upright.
                    Vector3 normal = head - center;
                    normal.y = 0f;
                    if (normal.sqrMagnitude < 1e-6f) normal = -headForward;
                    Preview = BoardFrame.FromPlane(center, normal, headForward, DefaultSize);
                    HasPreview = true;
                    if (pressed)
                    {
                        Active = PlacementMethod.None;
                        return true;
                    }
                    return false;
                }

                case PlacementMethod.Detected:
                    return TickDetected(provider, sample, head, headForward, pressed);
            }
            return false;
        }

        bool TickDetected(IStylusInputProvider provider, StylusSample sample, Vector3 head, Vector3 headForward, bool pressed)
        {
            switch (_scanner.State)
            {
                case SurfaceScanState.Loading:
                    Prompt = "Loading the room scan...";
                    HasPreview = false;
                    return false;
                case SurfaceScanState.Ready:
                    break;
                default:
                    Prompt = _scanner.StateMessage + " Use '3+ points' or 'Pointer' placement instead" +
                             (_scanner.CanLaunchRoomSetup ? ", or run Room Setup and try again." : ".");
                    HasPreview = false;
                    return false;
            }

            IReadOnlyList<SurfaceCandidate> list = _scanner.Candidates;
            if (list.Count == 0)
            {
                Prompt = "The room scan contains no usable flat surfaces. Use '3+ points' or 'Pointer' placement.";
                HasPreview = false;
                return false;
            }

            Prompt = $"{list.Count} detected surface(s). Point at a wall or table and press the draw control to select it. " +
                     "Detected planes are approximate (centimetres): you will fine-tune next.";
            if (!TryGetAimRay(provider, sample, head, out Vector3 origin, out Vector3 dir))
            {
                HasPreview = false;
                return false;
            }

            float best = float.MaxValue;
            HasPreview = false;
            for (int i = 0; i < list.Count; i++)
            {
                SurfaceCandidate c = list[i];
                var plane = new BoardFrame(c.center, c.rotation, c.size);
                Vector3 n = plane.OutwardNormal;
                float denom = Vector3.Dot(dir, n);
                if (denom > -1e-4f) continue; // facing away
                float t = Vector3.Dot(c.center - origin, n) / denom;
                if (t <= 0f || t >= best) continue;
                Vector3 hit = plane.WorldToBoard(origin + dir * t);
                if (!plane.Contains(new Vector2(hit.x, hit.y))) continue;

                best = t;
                // Fit a default-sized board inside the detected region, centred on the aim point.
                Vector2 size = new Vector2(Mathf.Min(DefaultSize.x, c.size.x), Mathf.Min(DefaultSize.y, c.size.y));
                float cx = Mathf.Clamp(hit.x, -(c.size.x - size.x) * 0.5f, (c.size.x - size.x) * 0.5f);
                float cy = Mathf.Clamp(hit.y, -(c.size.y - size.y) * 0.5f, (c.size.y - size.y) * 0.5f);
                Vector3 center = plane.BoardToWorld(new Vector2(cx, cy));
                Preview = BoardFrame.FromPlane(center, n, headForward, size);
                HasPreview = true;
            }

            if (HasPreview && pressed)
            {
                Active = PlacementMethod.None;
                return true;
            }
            return false;
        }

        /// <summary>Aim ray: the controller pointer when available, otherwise from the head through the tip (hands, pen).</summary>
        bool TryGetAimRay(IStylusInputProvider provider, StylusSample sample, Vector3 head, out Vector3 origin, out Vector3 dir)
        {
            if (provider.Source == StylusSource.Controller && _device.TryGetPointerRay(provider.Hand, out origin, out dir)) return true;
            origin = head;
            dir = Vector3.forward;
            if (!sample.tracked) return false;
            Vector3 d = sample.tipPosition - head;
            if (d.sqrMagnitude < 1e-4f) return false;
            dir = d.normalized;
            return true;
        }

        void Refit(Vector3 head, bool haveHead)
        {
            HasFit = Points.Count >= 3;
            HasPreview = false;
            if (!HasFit)
            {
                Prompt = $"{Points.Count} point(s) recorded. Need at least 3, well apart and not in a line.";
                return;
            }
            PlaneFitResult fit = PlaneFit.Fit(Points);
            LastFit = fit;
            if (!fit.success)
            {
                Prompt = $"{Points.Count} points: " + fit.DescribeFailure();
                return;
            }

            // The fit's normal sign is arbitrary: make it face the writer.
            Vector3 n = fit.normal;
            if (haveHead && Vector3.Dot(head - fit.centroid, n) < 0f) n = -n;
            else if (!haveHead && HasPreviewNormal(out Vector3 prev) && Vector3.Dot(prev, n) < 0f) n = -n;

            BoardFrame f = BoardFrame.FromPlane(fit.centroid, n, haveHead ? fit.centroid - head : Vector3.forward, DefaultSize);
            // Size the board to the touched region (never smaller than 40 x 30 cm).
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < Points.Count; i++)
            {
                Vector3 b = f.WorldToBoard(Points[i]);
                minX = Mathf.Min(minX, b.x); maxX = Mathf.Max(maxX, b.x);
                minY = Mathf.Min(minY, b.y); maxY = Mathf.Max(maxY, b.y);
            }
            Vector2 size = new Vector2(Mathf.Max(0.4f, maxX - minX), Mathf.Max(0.3f, maxY - minY));
            Vector3 center = f.BoardToWorld(new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f));
            Preview = new BoardFrame(center, f.rotation, size);
            _lastNormal = n;
            HasPreview = true;

            string residual = fit.residualIsMeaningful
                ? $"fit residual {fit.rmsResidual * 1000f:0.0} mm RMS (max {fit.maxResidual * 1000f:0.0} mm)"
                : "exactly 3 points: no residual to judge the fit by; add 2 or more for a quality check";
            Prompt = $"{Points.Count} points, {residual}. Add more points, or press Finish. " +
                     "The residual reflects how consistently the tip was tracked, not how well the plane matches the wall.";
        }

        Vector3 _lastNormal;
        bool HasPreviewNormal(out Vector3 n)
        {
            n = _lastNormal;
            return n.sqrMagnitude > 0.5f;
        }
    }
}
