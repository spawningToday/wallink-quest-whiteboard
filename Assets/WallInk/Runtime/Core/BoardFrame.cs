using UnityEngine;

namespace WallInk
{
    /// <summary>
    /// The writing board's coordinate frame, in metres.
    ///
    /// Board-local axes as seen by the writer facing the surface:
    ///   +X = right, +Y = up (along the board), +Z = INTO the surface (away from the writer).
    /// This is an ordinary left-handed Unity transform, so the outward normal (toward the
    /// writer) is local -Z.
    ///
    /// "Board coordinates" used by strokes and diagnostics are (x, y, height) where
    /// height = -localZ: positive in front of the surface, negative behind it.
    /// </summary>
    public struct BoardFrame
    {
        public Vector3 origin;      // board centre, world space
        public Quaternion rotation; // board-local -> world
        public Vector2 size;        // width (X) and height (Y) in metres

        public BoardFrame(Vector3 origin, Quaternion rotation, Vector2 size)
        {
            this.origin = origin;
            this.rotation = rotation;
            this.size = size;
        }

        public Vector3 Right => rotation * Vector3.right;
        public Vector3 Up => rotation * Vector3.up;

        /// <summary>Unit normal pointing from the surface toward the writer.</summary>
        public Vector3 OutwardNormal => rotation * Vector3.back;

        /// <summary>World point -> (x, y, height above surface), metres.</summary>
        public Vector3 WorldToBoard(Vector3 world)
        {
            Vector3 l = QuatUtil.Inverse(rotation) * (world - origin);
            return new Vector3(l.x, l.y, -l.z);
        }

        /// <summary>Board point (x, y) at a given height above the surface -> world.</summary>
        public Vector3 BoardToWorld(Vector2 p, float height = 0f)
        {
            return origin + rotation * new Vector3(p.x, p.y, -height);
        }

        public float SignedHeight(Vector3 world) => Vector3.Dot(world - origin, OutwardNormal);

        public bool Contains(Vector2 p, float margin = 0f)
        {
            return Mathf.Abs(p.x) <= size.x * 0.5f + margin && Mathf.Abs(p.y) <= size.y * 0.5f + margin;
        }

        /// <summary>
        /// Builds a frame for an arbitrary plane (wall, desk or tilted surface).
        /// "Up" on the board is world-up projected into the plane. When the plane is close to
        /// horizontal that projection vanishes, so the writer's view direction projected into
        /// the plane is used instead (the far edge of a desk becomes the top of the page).
        /// </summary>
        public static BoardFrame FromPlane(Vector3 pointOnPlane, Vector3 normalTowardWriter,
            Vector3 writerForward, Vector2 size)
        {
            Vector3 n = normalTowardWriter.normalized;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, n);
            if (up.magnitude < 0.2f) // plane within ~11.5 degrees of horizontal
            {
                up = Vector3.ProjectOnPlane(writerForward, n);
                if (up.magnitude < 1e-3f) up = Vector3.ProjectOnPlane(Vector3.forward, n);
                if (up.magnitude < 1e-3f) up = Vector3.ProjectOnPlane(Vector3.right, n);
            }
            return new BoardFrame(pointOnPlane, QuatUtil.LookRotation(-n, up.normalized), size);
        }
    }
}
