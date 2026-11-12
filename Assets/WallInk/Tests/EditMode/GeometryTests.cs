using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace WallInk.Tests
{
    public class PlaneFitTests
    {
        // Deterministic pseudo-random numbers so the tests never flake.
        static float Rand(ref uint state)
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) / 16777216f * 2f - 1f; // -1..1
        }

        static List<Vector3> PlanePoints(Vector3 origin, Vector3 normal, int count, float extent, float noise, uint seed)
        {
            Vector3 n = normal.normalized;
            Vector3 u = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 v = Vector3.Cross(n, u);
            var pts = new List<Vector3>();
            uint s = seed;
            for (int i = 0; i < count; i++)
                pts.Add(origin + u * (Rand(ref s) * extent) + v * (Rand(ref s) * extent) + n * (Rand(ref s) * noise));
            return pts;
        }

        [Test]
        public void RecoversTiltedPlaneWithinNoise()
        {
            Vector3 normal = new Vector3(0.3f, 0.5f, -0.8f).normalized;
            var pts = PlanePoints(new Vector3(1f, 1.4f, 2f), normal, 40, 0.4f, 0.001f, 7);
            PlaneFitResult r = PlaneFit.Fit(pts);
            Assert.That(r.success, Is.True, r.DescribeFailure());
            float angle = Mathf.Acos(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(r.normal, normal)))) * Mathf.Rad2Deg;
            Assert.That(angle, Is.LessThan(0.3f));
            Assert.That(r.rmsResidual, Is.LessThan(0.0012f));
            Assert.That(r.residualIsMeaningful, Is.True);
        }

        [Test]
        public void VerticalWallAndHorizontalDeskBothFit()
        {
            PlaneFitResult wall = PlaneFit.Fit(PlanePoints(new Vector3(0, 1.5f, 2), Vector3.back, 12, 0.3f, 0f, 3));
            PlaneFitResult desk = PlaneFit.Fit(PlanePoints(new Vector3(0, 0.75f, 0.5f), Vector3.up, 12, 0.3f, 0f, 5));
            Assert.That(wall.success && desk.success, Is.True);
            Assert.That(Mathf.Abs(wall.normal.z), Is.GreaterThan(0.9999f));
            Assert.That(Mathf.Abs(desk.normal.y), Is.GreaterThan(0.9999f));
        }

        [Test]
        public void ThreePointsFitExactlyButResidualIsNotMeaningful()
        {
            var pts = new List<Vector3> { new Vector3(0, 0, 1), new Vector3(0.4f, 0, 1), new Vector3(0, 0.4f, 1) };
            PlaneFitResult r = PlaneFit.Fit(pts);
            Assert.That(r.success, Is.True);
            Assert.That(r.residualIsMeaningful, Is.False);
            Assert.That(r.rmsResidual, Is.LessThan(1e-5f));
        }

        [Test]
        public void RejectsTooFewPoints()
        {
            var pts = new List<Vector3> { Vector3.zero, Vector3.right };
            Assert.That(PlaneFit.Fit(pts).failure, Is.EqualTo(PlaneFitFailure.TooFewPoints));
        }

        [Test]
        public void RejectsCollinearPoints()
        {
            var pts = new List<Vector3>();
            for (int i = 0; i < 5; i++) pts.Add(new Vector3(i * 0.2f, 1f + i * 0.0005f, 2f));
            PlaneFitResult r = PlaneFit.Fit(pts);
            Assert.That(r.success, Is.False);
            Assert.That(r.failure, Is.EqualTo(PlaneFitFailure.Collinear));
            Assert.That(r.DescribeFailure(), Is.Not.Empty);
        }

        [Test]
        public void RejectsPointsTooCloseTogether()
        {
            var pts = new List<Vector3> { new Vector3(0, 0, 1), new Vector3(0.02f, 0, 1), new Vector3(0, 0.02f, 1) };
            Assert.That(PlaneFit.Fit(pts).failure, Is.EqualTo(PlaneFitFailure.PointsTooClose));
        }

        [Test]
        public void RejectsNonPlanarSamples()
        {
            // Samples spread over two perpendicular walls (a room corner).
            var pts = new List<Vector3>();
            for (int i = 0; i < 4; i++)
            {
                pts.Add(new Vector3(0.1f * i, 0.1f * i, 0f));
                pts.Add(new Vector3(0f, 0.1f * i, 0.1f * (i + 1)));
                pts.Add(new Vector3(0.1f * (i + 1), 0.3f - 0.1f * i, 0f));
                pts.Add(new Vector3(0f, 0.3f - 0.1f * i, 0.1f * (i + 1)));
            }
            PlaneFitResult r = PlaneFit.Fit(pts);
            Assert.That(r.success, Is.False);
            Assert.That(r.failure, Is.EqualTo(PlaneFitFailure.NotPlanar));
        }
    }

    public class BoardFrameTests
    {
        static void AssertClose(Vector3 a, Vector3 b, float tol = 1e-4f)
        {
            Assert.That((a - b).magnitude, Is.LessThan(tol), $"{a} vs {b}");
        }

        [Test]
        public void WallFrameHasXRightYUpFromTheWritersView()
        {
            // Writer at the origin looking along +Z at a wall 2 m away; wall normal faces the writer (-Z).
            BoardFrame f = BoardFrame.FromPlane(new Vector3(0, 1.5f, 2), Vector3.back, Vector3.forward, new Vector2(1.2f, 0.8f));
            AssertClose(f.Right, Vector3.right);
            AssertClose(f.Up, Vector3.up);
            AssertClose(f.OutwardNormal, Vector3.back);
            // A point 10 cm right, 5 cm up and 2 cm in front of the wall.
            Vector3 b = f.WorldToBoard(new Vector3(0.1f, 1.55f, 1.98f));
            AssertClose(b, new Vector3(0.1f, 0.05f, 0.02f));
        }

        [Test]
        public void DeskFrameUsesWriterForwardAsUp()
        {
            BoardFrame f = BoardFrame.FromPlane(new Vector3(0, 0.75f, 0.5f), Vector3.up, Vector3.forward, new Vector2(0.6f, 0.4f));
            AssertClose(f.OutwardNormal, Vector3.up);
            AssertClose(f.Up, Vector3.forward);  // far edge of the desk is the top of the page
            AssertClose(f.Right, Vector3.right);
            Assert.That(f.WorldToBoard(new Vector3(0, 0.76f, 0.5f)).z, Is.EqualTo(0.01f).Within(1e-5f));
        }

        [Test]
        public void TiltedSurfaceRoundTrips()
        {
            Vector3 n = new Vector3(0.2f, 0.6f, -0.77f).normalized; // drafting-table style tilt
            BoardFrame f = BoardFrame.FromPlane(new Vector3(0.3f, 1.1f, 1.2f), n, Vector3.forward, new Vector2(1f, 0.7f));
            AssertClose(f.OutwardNormal, n);
            Assert.That(Vector3.Dot(f.Up, Vector3.up), Is.GreaterThan(0f), "board up should point uphill");
            Assert.That(Mathf.Abs(Vector3.Dot(f.Right, f.Up)), Is.LessThan(1e-4f));

            var bp = new Vector2(0.123f, -0.2f);
            Vector3 w = f.BoardToWorld(bp, 0.015f);
            Vector3 back = f.WorldToBoard(w);
            AssertClose(back, new Vector3(bp.x, bp.y, 0.015f), 1e-5f);
            Assert.That(f.SignedHeight(w), Is.EqualTo(0.015f).Within(1e-5f));
        }

        [Test]
        public void StrokesStayPutInBoardSpaceWhenTheBoardIsRealigned()
        {
            var size = new Vector2(1f, 1f);
            BoardFrame a = BoardFrame.FromPlane(new Vector3(0, 1.5f, 2f), Vector3.back, Vector3.forward, size);
            BoardFrame b = BoardFrame.FromPlane(new Vector3(0.01f, 1.5f, 2.004f), new Vector3(0.02f, 0, -1f), Vector3.forward, size);
            var p = new Vector2(0.2f, 0.1f);
            // The same board-local point maps to the corrected plane, exactly on its surface.
            Assert.That(Mathf.Abs(b.SignedHeight(b.BoardToWorld(p))), Is.LessThan(1e-5f));
            Assert.That(Mathf.Abs(a.SignedHeight(a.BoardToWorld(p))), Is.LessThan(1e-5f));
            AssertClose(b.WorldToBoard(b.BoardToWorld(p)), new Vector3(p.x, p.y, 0), 1e-5f);
        }

        [Test]
        public void ContainsRespectsBounds()
        {
            var f = new BoardFrame(Vector3.zero, new Quaternion(0, 0, 0, 1), new Vector2(1f, 0.5f));
            Assert.That(f.Contains(new Vector2(0.49f, 0.24f)), Is.True);
            Assert.That(f.Contains(new Vector2(0.51f, 0f)), Is.False);
            Assert.That(f.Contains(new Vector2(0.51f, 0f), 0.02f), Is.True);
        }

        [Test]
        public void LookRotationMatchesAxes()
        {
            Quaternion q = QuatUtil.LookRotation(Vector3.right, Vector3.up);
            AssertClose(q * Vector3.forward, Vector3.right);
            AssertClose(q * Vector3.up, Vector3.up);
            AssertClose(QuatUtil.Inverse(q) * (q * new Vector3(1, 2, 3)), new Vector3(1, 2, 3));
            Assert.That(QuatUtil.Angle(q, QuatUtil.AngleAxis(90f, Vector3.up)), Is.LessThan(0.05f));
        }
    }

    public class PivotCalibrationTests
    {
        static void Synthesize(Vector3 offset, Vector3 pivot, float noise, bool singleAxis,
            List<Vector3> positions, List<Quaternion> rotations)
        {
            uint s = 11;
            for (int i = 0; i < 60; i++)
            {
                float a = -35f + 70f * i / 59f;
                float b = singleAxis ? 0f : 30f * Mathf.Sin(i * 0.7f);
                Quaternion r = QuatUtil.AngleAxis(a, Vector3.right) * QuatUtil.AngleAxis(b, Vector3.forward);
                s = s * 1664525u + 1013904223u; float nx = ((s >> 8) / 16777216f * 2f - 1f) * noise;
                s = s * 1664525u + 1013904223u; float ny = ((s >> 8) / 16777216f * 2f - 1f) * noise;
                s = s * 1664525u + 1013904223u; float nz = ((s >> 8) / 16777216f * 2f - 1f) * noise;
                // tip = p + R * offset = pivot  =>  p = pivot - R * offset
                positions.Add(pivot - r * offset + new Vector3(nx, ny, nz));
                rotations.Add(r);
            }
        }

        [Test]
        public void RecoversKnownTipOffset()
        {
            var offset = new Vector3(0.004f, -0.031f, 0.072f);
            var pivot = new Vector3(0.2f, 1.3f, 1.9f);
            var pos = new List<Vector3>(); var rot = new List<Quaternion>();
            Synthesize(offset, pivot, 0f, false, pos, rot);
            PivotCalibrationResult r = PivotCalibration.Solve(pos, rot);
            Assert.That(r.success, Is.True, r.message);
            Assert.That((r.localTipOffset - offset).magnitude, Is.LessThan(2e-4f));
            Assert.That((r.pivotWorld - pivot).magnitude, Is.LessThan(2e-4f));
            Assert.That(r.rmsResidual, Is.LessThan(2e-4f));
        }

        [Test]
        public void NoiseShowsUpInTheResidual()
        {
            var pos = new List<Vector3>(); var rot = new List<Quaternion>();
            Synthesize(new Vector3(0, -0.03f, 0.07f), new Vector3(0, 1, 2), 0.002f, false, pos, rot);
            PivotCalibrationResult r = PivotCalibration.Solve(pos, rot);
            Assert.That(r.success, Is.True, r.message);
            Assert.That(r.rmsResidual, Is.GreaterThan(0.0005f));
            Assert.That(r.rmsResidual, Is.LessThan(0.004f));
        }

        [Test]
        public void RejectsTooLittleRotation()
        {
            var pos = new List<Vector3>(); var rot = new List<Quaternion>();
            for (int i = 0; i < 30; i++)
            {
                Quaternion r = QuatUtil.AngleAxis(i * 0.2f, Vector3.right);
                rot.Add(r);
                pos.Add(new Vector3(0, 1, 2) - r * new Vector3(0, 0, 0.07f));
            }
            PivotCalibrationResult res = PivotCalibration.Solve(pos, rot);
            Assert.That(res.success, Is.False);
            Assert.That(res.message, Does.Contain("rotation"));
        }

        [Test]
        public void RejectsRockingAboutASingleAxis()
        {
            // Rotation about one axis leaves the offset component along that axis unobservable.
            var pos = new List<Vector3>(); var rot = new List<Quaternion>();
            Synthesize(new Vector3(0.01f, -0.03f, 0.07f), new Vector3(0, 1, 2), 0f, true, pos, rot);
            PivotCalibrationResult r = PivotCalibration.Solve(pos, rot);
            Assert.That(r.success, Is.False);
        }

        [Test]
        public void RejectsTooFewSamples()
        {
            var pos = new List<Vector3> { Vector3.zero };
            var rot = new List<Quaternion> { new Quaternion(0, 0, 0, 1) };
            Assert.That(PivotCalibration.Solve(pos, rot).success, Is.False);
        }
    }

    public class FilterAndStatsTests
    {
        [Test]
        public void DisabledFilterPassesThrough()
        {
            var f = new OneEuroFilter(0f, 0f);
            var p = new Vector3(1, 2, 3);
            f.Filter(Vector3.zero, 0.01f);
            Assert.That((f.Filter(p, 0.01f) - p).magnitude, Is.LessThan(1e-7f));
        }

        [Test]
        public void FilterReducesJitterOfAStillTip()
        {
            var f = new OneEuroFilter(6f, 150f);
            uint s = 3;
            float rawSum = 0f, filtSum = 0f;
            for (int i = 0; i < 400; i++)
            {
                s = s * 1664525u + 1013904223u;
                float n = ((s >> 8) / 16777216f * 2f - 1f) * 0.0005f; // +-0.5 mm jitter
                Vector3 o = f.Filter(new Vector3(n, 0, 0), 1f / 90f);
                if (i < 50) continue;
                rawSum += n * n; filtSum += o.x * o.x;
            }
            Assert.That(filtSum, Is.LessThan(rawSum * 0.5f));
        }

        [Test]
        public void FilterFollowsFastMotionClosely()
        {
            var f = new OneEuroFilter(6f, 150f);
            float dt = 1f / 90f, speed = 0.3f; // 30 cm/s pen stroke
            Vector3 o = Vector3.zero; float x = 0f;
            for (int i = 0; i < 90; i++) { x += speed * dt; o = f.Filter(new Vector3(x, 0, 0), dt); }
            // Steady-state lag = speed * tau. Must stay well under a millimetre-scale glyph feature.
            Assert.That(x - o.x, Is.LessThan(0.003f));
            Assert.That(OneEuroFilter.EstimatedDelayMs(6f, 150f, speed), Is.LessThan(10f));
        }

        [Test]
        public void PointStatsSeparateRepeatabilityFromOffset()
        {
            var st = new PointStats();
            // Consistently 5 mm to the right of the target with 1 mm scatter.
            st.Add(new Vector3(0.006f, 0f, 0.001f));
            st.Add(new Vector3(0.004f, 0f, 0.001f));
            st.Add(new Vector3(0.005f, 0.001f, 0.003f));
            st.Add(new Vector3(0.005f, -0.001f, -0.001f));
            Assert.That(st.Count, Is.EqualTo(4));
            Assert.That(st.OffsetFrom(Vector2.zero).x, Is.EqualTo(0.005f).Within(1e-5f));
            Assert.That(st.InPlaneRms, Is.EqualTo(0.001f).Within(1e-4f));
            Assert.That(st.InPlaneMax, Is.EqualTo(0.001f).Within(1e-4f));
            Assert.That(st.HeightStd, Is.GreaterThan(0.001f));
            Assert.That(PointStats.Median(new List<float> { 3f, 1f, 2f }), Is.EqualTo(2f));
            Assert.That(PointStats.Median(new List<float> { 4f, 1f, 2f, 3f }), Is.EqualTo(2.5f));
        }
    }
}
