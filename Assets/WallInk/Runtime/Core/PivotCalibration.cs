using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    public struct PivotCalibrationResult
    {
        public bool success;
        public string message;
        public Vector3 localTipOffset; // tip position expressed in the tracked reference frame
        public Vector3 pivotWorld;     // estimated fixed point the tip rested on
        public float rmsResidual;      // metres: how far the solved tip wandered from the pivot across samples
        public float angularSpreadDeg; // largest rotation between any sample and the first
        public int sampleCount;
    }

    /// <summary>
    /// Pivot calibration. The user rests the writing tip on one fixed spot and rocks the tracked
    /// object (a controller, or a hand holding a pen) through different orientations.
    ///
    /// For every sample i with tracked pose (p_i, R_i), the unknown local tip offset t and the
    /// unknown fixed point c satisfy    R_i * t + p_i = c.
    /// Stacking all samples gives the linear least-squares system  [R_i  -I] [t; c] = -p_i,
    /// which is solved here through its 6x6 normal equations.
    ///
    /// The result is only as good as the tracked poses. It removes a constant offset, not noise,
    /// and it is meaningless if the tip slid or (for a pen) the grip changed during capture.
    /// The RMS residual reports how consistent the samples were, not absolute accuracy.
    /// </summary>
    public static class PivotCalibration
    {
        public const int MinSamples = 8;
        public const float MinAngularSpreadDeg = 25f;

        public static PivotCalibrationResult Solve(IList<Vector3> positions, IList<Quaternion> rotations)
        {
            var r = new PivotCalibrationResult { sampleCount = positions.Count };
            if (positions.Count != rotations.Count || positions.Count < MinSamples)
            {
                r.message = "Not enough samples. Keep the tip on one spot and rock the handle for a few seconds.";
                return r;
            }

            int n = positions.Count;
            float maxAngle = 0f;
            for (int i = 1; i < n; i++)
                maxAngle = Mathf.Max(maxAngle, QuatUtil.Angle(rotations[0], rotations[i]));
            r.angularSpreadDeg = maxAngle;
            if (maxAngle < MinAngularSpreadDeg)
            {
                r.message = "Too little rotation. Tilt the handle more widely, in several directions, while the tip stays put.";
                return r;
            }

            var ata = new double[6, 6];
            var atb = new double[6];
            var row = new double[3, 6];
            for (int i = 0; i < n; i++)
            {
                Matrix4x4 m = Matrix4x4.Rotate(rotations[i]);
                for (int a = 0; a < 3; a++)
                {
                    for (int b = 0; b < 3; b++)
                    {
                        row[a, b] = m[a, b];
                        row[a, b + 3] = a == b ? -1 : 0;
                    }
                }
                Vector3 p = positions[i];
                for (int a = 0; a < 3; a++)
                {
                    double rhs = -p[a];
                    for (int j = 0; j < 6; j++)
                    {
                        atb[j] += row[a, j] * rhs;
                        for (int k = 0; k < 6; k++) ata[j, k] += row[a, j] * row[a, k];
                    }
                }
            }

            var x = new double[6];
            if (!SolveLinear(ata, atb, x, 6, 1e-4 * n))
            {
                r.message = "Calibration was ill-conditioned. Rock the handle about two different axes and retry.";
                return r;
            }

            r.localTipOffset = new Vector3((float)x[0], (float)x[1], (float)x[2]);
            r.pivotWorld = new Vector3((float)x[3], (float)x[4], (float)x[5]);

            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 tip = positions[i] + rotations[i] * r.localTipOffset;
                sum += (tip - r.pivotWorld).sqrMagnitude;
            }
            r.rmsResidual = (float)Math.Sqrt(sum / n);
            r.success = true;
            r.message = "OK";
            return r;
        }

        /// <summary>
        /// Gaussian elimination with partial pivoting; destroys a and b. Fails when a pivot is
        /// below minPivot, which means the captured rotations did not constrain one unknown
        /// (for example, rocking about a single axis leaves the offset along that axis free).
        /// </summary>
        static bool SolveLinear(double[,] a, double[] b, double[] x, int n, double minPivot)
        {
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                double best = Math.Abs(a[col, col]);
                for (int rIdx = col + 1; rIdx < n; rIdx++)
                {
                    double val = Math.Abs(a[rIdx, col]);
                    if (val > best) { best = val; pivot = rIdx; }
                }
                if (best < minPivot) return false;
                if (pivot != col)
                {
                    for (int k = 0; k < n; k++) { double t = a[col, k]; a[col, k] = a[pivot, k]; a[pivot, k] = t; }
                    double tb = b[col]; b[col] = b[pivot]; b[pivot] = tb;
                }
                for (int rIdx = col + 1; rIdx < n; rIdx++)
                {
                    double f = a[rIdx, col] / a[col, col];
                    if (f == 0) continue;
                    for (int k = col; k < n; k++) a[rIdx, k] -= f * a[col, k];
                    b[rIdx] -= f * b[col];
                }
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double s = b[i];
                for (int k = i + 1; k < n; k++) s -= a[i, k] * x[k];
                x[i] = s / a[i, i];
            }
            return true;
        }
    }
}
