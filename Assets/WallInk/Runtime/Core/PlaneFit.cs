using System;
using System.Collections.Generic;
using UnityEngine;

namespace WallInk
{
    public enum PlaneFitFailure
    {
        None,
        TooFewPoints,
        PointsTooClose,
        Collinear,
        NotPlanar,
    }

    public struct PlaneFitResult
    {
        public bool success;
        public PlaneFitFailure failure;
        public Vector3 centroid;
        public Vector3 normal;          // unit; sign is arbitrary until oriented by the caller
        public float rmsResidual;       // metres, distance of the samples from the fitted plane
        public float maxResidual;       // metres
        public float spreadMajor;       // metres, RMS spread along the longest in-plane axis
        public float spreadMinor;       // metres, RMS spread along the shorter in-plane axis
        public int sampleCount;

        /// <summary>
        /// With exactly three points the plane passes through all of them, so the residual is
        /// zero by construction and says nothing about quality.
        /// </summary>
        public bool residualIsMeaningful;

        public string DescribeFailure()
        {
            switch (failure)
            {
                case PlaneFitFailure.TooFewPoints:
                    return "Need at least 3 points.";
                case PlaneFitFailure.PointsTooClose:
                    return "Points are too close together. Spread them at least 20 cm apart.";
                case PlaneFitFailure.Collinear:
                    return "Points are nearly in a straight line. Add a point well off that line (make a triangle, not a row).";
                case PlaneFitFailure.NotPlanar:
                    return "Points do not lie on one flat surface. Clear them and touch the surface again.";
                default:
                    return string.Empty;
            }
        }
    }

    /// <summary>
    /// Total-least-squares plane fit: the plane passes through the centroid and its normal is
    /// the eigenvector of the sample covariance matrix with the smallest eigenvalue (the
    /// direction in which the samples vary least).
    /// </summary>
    public static class PlaneFit
    {
        public const float DefaultMinMajorSpread = 0.06f;  // m RMS along the longest axis
        public const float DefaultMinMinorSpread = 0.03f;  // m RMS along the second axis
        public const float DefaultMaxFlatnessRatio = 0.2f; // residual RMS / minor spread

        public static PlaneFitResult Fit(IList<Vector3> points,
            float minMajorSpread = DefaultMinMajorSpread,
            float minMinorSpread = DefaultMinMinorSpread,
            float maxFlatnessRatio = DefaultMaxFlatnessRatio)
        {
            var r = new PlaneFitResult { sampleCount = points == null ? 0 : points.Count };
            if (r.sampleCount < 3)
            {
                r.failure = PlaneFitFailure.TooFewPoints;
                return r;
            }

            int n = r.sampleCount;
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++) { cx += points[i].x; cy += points[i].y; cz += points[i].z; }
            cx /= n; cy /= n; cz /= n;

            // Symmetric covariance matrix, accumulated in double to keep sub-millimetre detail.
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = points[i].x - cx, dy = points[i].y - cy, dz = points[i].z - cz;
                xx += dx * dx; xy += dx * dy; xz += dx * dz;
                yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }
            xx /= n; xy /= n; xz /= n; yy /= n; yz /= n; zz /= n;

            var a = new double[3, 3] { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
            var v = new double[3, 3];
            var w = new double[3];
            JacobiEigen(a, v, w);

            // Indices of the eigenvalues in descending order.
            int i0 = 0, i1 = 1, i2 = 2;
            if (w[i0] < w[i1]) { int t = i0; i0 = i1; i1 = t; }
            if (w[i1] < w[i2]) { int t = i1; i1 = i2; i2 = t; }
            if (w[i0] < w[i1]) { int t = i0; i0 = i1; i1 = t; }

            r.centroid = new Vector3((float)cx, (float)cy, (float)cz);
            r.spreadMajor = (float)Math.Sqrt(Math.Max(0, w[i0]));
            r.spreadMinor = (float)Math.Sqrt(Math.Max(0, w[i1]));
            r.normal = new Vector3((float)v[0, i2], (float)v[1, i2], (float)v[2, i2]).normalized;
            r.residualIsMeaningful = n > 3;

            double sum = 0, max = 0;
            for (int i = 0; i < n; i++)
            {
                double d = Math.Abs(Vector3.Dot(points[i] - r.centroid, r.normal));
                sum += d * d;
                if (d > max) max = d;
            }
            r.rmsResidual = (float)Math.Sqrt(sum / n);
            r.maxResidual = (float)max;

            if (r.spreadMajor < minMajorSpread) { r.failure = PlaneFitFailure.PointsTooClose; return r; }
            if (r.spreadMinor < minMinorSpread) { r.failure = PlaneFitFailure.Collinear; return r; }
            if (r.rmsResidual > maxFlatnessRatio * r.spreadMinor) { r.failure = PlaneFitFailure.NotPlanar; return r; }

            r.success = true;
            return r;
        }

        /// <summary>
        /// Cyclic Jacobi eigenvalue iteration for a symmetric 3x3 matrix.
        /// On return the eigenvalues are in w and the eigenvectors are the columns of v.
        /// </summary>
        static void JacobiEigen(double[,] a, double[,] v, double[] w)
        {
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    v[i, j] = i == j ? 1 : 0;

            for (int sweep = 0; sweep < 32; sweep++)
            {
                double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-20) break;
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-30) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = theta >= 0
                            ? 1 / (theta + Math.Sqrt(theta * theta + 1))
                            : 1 / (theta - Math.Sqrt(theta * theta + 1));
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        // A <- J^T A J with J the rotation in the (p, q) plane.
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
            w[0] = a[0, 0]; w[1] = a[1, 1]; w[2] = a[2, 2];
        }
    }
}
