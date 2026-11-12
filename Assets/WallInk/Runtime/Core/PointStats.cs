using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    /// <summary>
    /// Statistics over repeated indications of one target, in board coordinates
    /// (x, y in the board plane; z = height above the calibrated plane), metres.
    ///
    /// Repeatability (spread around the samples' own mean) measures tracking noise plus hand
    /// steadiness. It does NOT measure absolute accuracy: a tip that is consistently 5 mm off
    /// has excellent repeatability.
    /// </summary>
    public sealed class PointStats
    {
        readonly List<Vector3> _samples = new List<Vector3>();

        public int Count => _samples.Count;
        public void Reset() => _samples.Clear();
        public void Add(Vector3 boardPoint) => _samples.Add(boardPoint);

        public Vector3 Mean
        {
            get
            {
                if (_samples.Count == 0) return Vector3.zero;
                Vector3 sum = Vector3.zero;
                for (int i = 0; i < _samples.Count; i++) sum += _samples[i];
                return sum / _samples.Count;
            }
        }

        /// <summary>RMS in-plane distance of the samples from their own mean.</summary>
        public float InPlaneRms
        {
            get
            {
                if (_samples.Count < 2) return 0f;
                Vector3 mean = Mean;
                float sum = 0f;
                for (int i = 0; i < _samples.Count; i++)
                {
                    float dx = _samples[i].x - mean.x, dy = _samples[i].y - mean.y;
                    sum += dx * dx + dy * dy;
                }
                return Mathf.Sqrt(sum / _samples.Count);
            }
        }

        /// <summary>Largest in-plane distance of any sample from the mean.</summary>
        public float InPlaneMax
        {
            get
            {
                Vector3 mean = Mean;
                float max = 0f;
                for (int i = 0; i < _samples.Count; i++)
                {
                    float dx = _samples[i].x - mean.x, dy = _samples[i].y - mean.y;
                    max = Mathf.Max(max, Mathf.Sqrt(dx * dx + dy * dy));
                }
                return max;
            }
        }

        /// <summary>Standard deviation of height above the plane.</summary>
        public float HeightStd
        {
            get
            {
                if (_samples.Count < 2) return 0f;
                float mean = Mean.z, sum = 0f;
                for (int i = 0; i < _samples.Count; i++)
                {
                    float d = _samples[i].z - mean;
                    sum += d * d;
                }
                return Mathf.Sqrt(sum / _samples.Count);
            }
        }

        /// <summary>In-plane offset of the mean from a displayed target. Includes the user's aim, not just tracking.</summary>
        public Vector2 OffsetFrom(Vector2 target)
        {
            Vector3 m = Mean;
            return new Vector2(m.x - target.x, m.y - target.y);
        }

        public static float Median(List<float> values)
        {
            if (values.Count == 0) return 0f;
            values.Sort();
            int mid = values.Count / 2;
            return (values.Count & 1) == 1 ? values[mid] : 0.5f * (values[mid - 1] + values[mid]);
        }
    }
}
