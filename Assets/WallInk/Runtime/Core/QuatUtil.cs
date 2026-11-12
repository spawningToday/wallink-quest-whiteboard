using UnityEngine;

namespace WallInk
{
    /// <summary>
    /// Managed quaternion helpers. The core assembly avoids Unity's native-backed quaternion
    /// functions so that its logic can be unit-tested outside the Unity player as well.
    /// </summary>
    public static class QuatUtil
    {
        /// <summary>Inverse of a unit quaternion (its conjugate).</summary>
        public static Quaternion Inverse(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        /// <summary>
        /// Rotation whose local +Z is <paramref name="forward"/> and whose local +Y is as close
        /// to <paramref name="up"/> as possible (same contract as Quaternion.LookRotation).
        /// </summary>
        public static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            Vector3 z = forward.normalized;
            Vector3 x = Vector3.Cross(up, z);
            if (x.sqrMagnitude < 1e-12f)
            {
                // up is parallel to forward: pick any perpendicular.
                x = Vector3.Cross(Mathf.Abs(z.y) < 0.99f ? Vector3.up : Vector3.right, z);
            }
            x.Normalize();
            Vector3 y = Vector3.Cross(z, x);
            return FromBasis(x, y, z);
        }

        /// <summary>Quaternion from an orthonormal basis given as the rotated X, Y, Z axes (matrix columns).</summary>
        public static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z)
        {
            float m00 = x.x, m01 = y.x, m02 = z.x;
            float m10 = x.y, m11 = y.y, m12 = z.y;
            float m20 = x.z, m21 = y.z, m22 = z.z;
            float trace = m00 + m11 + m22;
            Quaternion q;
            if (trace > 0f)
            {
                float s = Mathf.Sqrt(trace + 1f) * 2f;
                q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                float s = Mathf.Sqrt(1f + m00 - m11 - m22) * 2f;
                q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                float s = Mathf.Sqrt(1f + m11 - m00 - m22) * 2f;
                q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                float s = Mathf.Sqrt(1f + m22 - m00 - m11) * 2f;
                q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s);
            }
            return Normalize(q);
        }

        public static Quaternion AngleAxis(float degrees, Vector3 axis)
        {
            axis.Normalize();
            float h = degrees * Mathf.Deg2Rad * 0.5f;
            float s = Mathf.Sin(h);
            return new Quaternion(axis.x * s, axis.y * s, axis.z * s, Mathf.Cos(h));
        }

        public static Quaternion Normalize(Quaternion q)
        {
            float m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (m < 1e-12f) return new Quaternion(0, 0, 0, 1);
            return new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m);
        }

        /// <summary>Angle in degrees between two rotations.</summary>
        public static float Angle(Quaternion a, Quaternion b)
        {
            float dot = Mathf.Abs(a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w);
            return dot >= 1f ? 0f : Mathf.Acos(dot) * 2f * Mathf.Rad2Deg;
        }
    }
}
