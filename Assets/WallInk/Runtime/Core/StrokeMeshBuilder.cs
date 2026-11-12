using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    /// <summary>Reusable CPU-side mesh arrays. Lists keep their capacity between uses, so steady-state drawing does not allocate.</summary>
    public sealed class MeshBuffers
    {
        public readonly List<Vector3> vertices = new List<Vector3>(1024);
        public readonly List<Color32> colors = new List<Color32>(1024);
        public readonly List<Vector2> uvs = new List<Vector2>(1024);
        public readonly List<int> indices = new List<int>(4096);
        public Vector3 boundsMin;
        public Vector3 boundsMax;

        public int VertexCount => vertices.Count;

        public void Clear()
        {
            vertices.Clear();
            colors.Clear();
            uvs.Clear();
            indices.Clear();
            boundsMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            boundsMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        }

        public int Add(Vector3 p, Color32 c, Vector2 uv)
        {
            vertices.Add(p);
            colors.Add(c);
            uvs.Add(uv);
            boundsMin = Vector3.Min(boundsMin, p);
            boundsMax = Vector3.Max(boundsMax, p);
            return vertices.Count - 1;
        }

        public void Quad(int a, int b, int c, int d)
        {
            indices.Add(a); indices.Add(b); indices.Add(c);
            indices.Add(b); indices.Add(d); indices.Add(c);
        }
    }

    /// <summary>
    /// Converts a stroke polyline into a constant-width triangle ribbon.
    ///
    ///  - Board strokes lie flat in the board plane (ribbon normal = board normal).
    ///  - Air strokes use a ribbon normal carried along the curve by parallel transport, so the
    ///    ribbon does not flip or twist abruptly as the direction changes.
    ///  - Gentle turns are mitred with shared vertices. Sharp turns (over 60 degrees) end the
    ///    ribbon section, add a round join disc and start a new section, which keeps corners of
    ///    small symbols crisp without long miter spikes.
    ///  - Round caps at both ends; a single-point stroke is a disc (a dot).
    ///
    /// UV.x runs -1..+1 across the ribbon (0 at disc centres, 1 at their rim) for edge
    /// anti-aliasing in the shader. UV.y is arc length in metres.
    /// The ink shader is drawn without culling and overlapping pieces of one stroke share one
    /// colour, so overlaps between quads and discs are invisible.
    /// </summary>
    public static class StrokeMeshBuilder
    {
        public const int DiscSegments = 12;
        const float MinPointSeparation = 1e-5f; // 10 micrometres: anything closer is a duplicate
        const float MiterCosLimit = 0.5f;       // turns sharper than 60 degrees get a round join

        static readonly List<Vector3> s_points = new List<Vector3>(256);

        /// <summary>Upper bound on vertices produced for a stroke with the given point count.</summary>
        public static int MaxVertexCount(int pointCount)
        {
            return pointCount * (4 + DiscSegments + 1) + 2 * (DiscSegments + 1);
        }

        /// <param name="planeOffset">Board strokes only: distance to lift the ink off the board toward the writer, metres (prevents z-fighting with the backing).</param>
        public static void Append(MeshBuffers b, StrokeData s, float planeOffset)
        {
            var src = s.points;
            if (src.Count == 0) return;

            bool board = s.space == StrokeSpace.Board;
            Vector3 lift = board ? new Vector3(0f, 0f, -planeOffset) : Vector3.zero;

            // Remove consecutive duplicates; they have no direction and would produce NaN normals.
            s_points.Clear();
            s_points.Add(src[0] + lift);
            for (int i = 1; i < src.Count; i++)
            {
                Vector3 p = src[i] + lift;
                if ((p - s_points[s_points.Count - 1]).sqrMagnitude >= MinPointSeparation * MinPointSeparation)
                    s_points.Add(p);
            }

            float hw = Mathf.Max(s.width * 0.5f, 1e-5f);
            Color32 col = s.color;
            int m = s_points.Count;
            Vector3 n = board ? Vector3.back : s.ribbonNormal;

            if (m == 1)
            {
                if (!board) n = SafeNormal(n, Vector3.forward);
                Disc(b, s_points[0], n, hw, col, 0f);
                return;
            }

            Vector3 d = (s_points[1] - s_points[0]).normalized;
            if (!board) n = SafeNormal(n, d);
            Vector3 side = Vector3.Cross(n, d).normalized;
            float arc = 0f;

            Disc(b, s_points[0], n, hw, col, 0f);
            int a = b.Add(s_points[0] - side * hw, col, new Vector2(-1f, 0f));
            int c = b.Add(s_points[0] + side * hw, col, new Vector2(1f, 0f));

            for (int i = 1; i < m; i++)
            {
                Vector3 p = s_points[i];
                arc += (p - s_points[i - 1]).magnitude;

                if (i == m - 1)
                {
                    int e = b.Add(p - side * hw, col, new Vector2(-1f, arc));
                    int f = b.Add(p + side * hw, col, new Vector2(1f, arc));
                    b.Quad(a, c, e, f);
                    Disc(b, p, n, hw, col, arc);
                    break;
                }

                Vector3 dNext = (s_points[i + 1] - p).normalized;
                Vector3 nNext = board ? n : Transport(n, dNext);
                Vector3 sideNext = Vector3.Cross(nNext, dNext).normalized;
                float cos = Vector3.Dot(d, dNext);

                if (cos > MiterCosLimit)
                {
                    // Miter: offset along the bisector, lengthened so the ribbon keeps its width.
                    Vector3 mdir = (side + sideNext).normalized;
                    float len = hw / Mathf.Max(0.5f, Vector3.Dot(mdir, sideNext));
                    int e = b.Add(p - mdir * len, col, new Vector2(-1f, arc));
                    int f = b.Add(p + mdir * len, col, new Vector2(1f, arc));
                    b.Quad(a, c, e, f);
                    a = e; c = f;
                }
                else
                {
                    int e = b.Add(p - side * hw, col, new Vector2(-1f, arc));
                    int f = b.Add(p + side * hw, col, new Vector2(1f, arc));
                    b.Quad(a, c, e, f);
                    Disc(b, p, nNext, hw, col, arc);
                    a = b.Add(p - sideNext * hw, col, new Vector2(-1f, arc));
                    c = b.Add(p + sideNext * hw, col, new Vector2(1f, arc));
                }

                d = dNext;
                n = nNext;
                side = sideNext;
            }
        }

        /// <summary>Parallel transport: remove the component of the normal along the new direction.</summary>
        static Vector3 Transport(Vector3 n, Vector3 dir)
        {
            Vector3 t = n - dir * Vector3.Dot(n, dir);
            return t.sqrMagnitude < 1e-8f ? SafeNormal(n, dir) : t.normalized;
        }

        /// <summary>A unit vector perpendicular to dir, as close to the preferred normal as possible.</summary>
        static Vector3 SafeNormal(Vector3 preferred, Vector3 dir)
        {
            Vector3 t = preferred - dir * Vector3.Dot(preferred, dir);
            if (t.sqrMagnitude > 1e-8f) return t.normalized;
            t = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right);
            return t.normalized;
        }

        static void Disc(MeshBuffers b, Vector3 center, Vector3 normal, float radius, Color32 col, float arc)
        {
            Vector3 u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(normal, u);
            int ci = b.Add(center, col, new Vector2(0f, arc));
            int first = b.VertexCount;
            for (int k = 0; k < DiscSegments; k++)
            {
                float ang = k * (2f * Mathf.PI / DiscSegments);
                Vector3 p = center + (u * Mathf.Cos(ang) + v * Mathf.Sin(ang)) * radius;
                b.Add(p, col, new Vector2(1f, arc));
            }
            for (int k = 0; k < DiscSegments; k++)
            {
                b.indices.Add(ci);
                b.indices.Add(first + k);
                b.indices.Add(first + (k + 1) % DiscSegments);
            }
        }
    }

    public static class StrokeHitTest
    {
        /// <summary>True when the point is within radius of the stroke's ink (centre line plus half width).</summary>
        public static bool Hits(StrokeData s, Vector3 p, float radius)
        {
            float r = radius + s.width * 0.5f;
            if (p.x < s.boundsMin.x - r || p.x > s.boundsMax.x + r ||
                p.y < s.boundsMin.y - r || p.y > s.boundsMax.y + r ||
                p.z < s.boundsMin.z - r || p.z > s.boundsMax.z + r)
                return false;

            var pts = s.points;
            float r2 = r * r;
            if (pts.Count == 1) return (pts[0] - p).sqrMagnitude <= r2;
            for (int i = 1; i < pts.Count; i++)
            {
                if (SqrDistanceToSegment(p, pts[i - 1], pts[i]) <= r2) return true;
            }
            return false;
        }

        public static float SqrDistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len2 = ab.sqrMagnitude;
            if (len2 < 1e-12f) return (p - a).sqrMagnitude;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2);
            return (a + ab * t - p).sqrMagnitude;
        }
    }
}
