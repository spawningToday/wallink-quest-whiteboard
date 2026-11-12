using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace WallInk.Tests
{
    public class ContactDetectorTests
    {
        [Test]
        public void HysteresisKeepsContactThroughNoise()
        {
            var c = new ContactDetector();
            Assert.That(c.Update(12f, 4f, 10f, 40f), Is.False);
            Assert.That(c.Update(5f, 4f, 10f, 40f), Is.False, "between thresholds while hovering: no contact yet");
            Assert.That(c.Update(3f, 4f, 10f, 40f), Is.True);
            Assert.That(c.Update(7f, 4f, 10f, 40f), Is.True, "noise above the enter threshold must not break contact");
            Assert.That(c.Update(9.5f, 4f, 10f, 40f), Is.True);
            Assert.That(c.Update(10.5f, 4f, 10f, 40f), Is.False);
            Assert.That(c.Update(6f, 4f, 10f, 40f), Is.False, "must come back below the enter threshold to re-contact");
        }

        [Test]
        public void ExcessivePenetrationIsImplausible()
        {
            var c = new ContactDetector();
            c.Update(0f, 4f, 10f, 40f);
            Assert.That(c.Update(-60f, 4f, 10f, 40f), Is.False);
            Assert.That(c.Implausible, Is.True);
            Assert.That(c.Update(-10f, 4f, 10f, 40f), Is.True, "moderate penetration (tip estimate slightly behind the wall) still counts");
        }
    }

    public class SurfaceStrokeCaptureTests
    {
        static readonly Vector2 Size = new Vector2(1f, 0.6f);

        static CaptureInput In(float x, float y, float heightMm, double t, bool held = false, bool tracked = true, bool suppressed = false)
        {
            return new CaptureInput
            {
                tracked = tracked,
                boardPoint = new Vector3(x, y, heightMm * 0.001f),
                explicitHeld = held,
                time = t,
                suppressed = suppressed,
            };
        }

        static CaptureSettings Contact() => new CaptureSettings { mode = ActivationMode.ContactLike };
        static CaptureSettings Explicit() => new CaptureSettings { mode = ActivationMode.Explicit };

        [Test]
        public void ContactTapProducesBeginThenEnd()
        {
            var cap = new SurfaceStrokeCapture(); var s = Contact();
            Assert.That(cap.Step(In(0, 0, 20, 0.00), Size, s).evt, Is.EqualTo(CaptureEvent.None));
            Assert.That(cap.Step(In(0, 0, 2, 0.01), Size, s).evt, Is.EqualTo(CaptureEvent.Begin));
            CaptureOutput o = cap.Step(In(0, 0, 15, 0.02), Size, s);
            Assert.That(o.evt, Is.EqualTo(CaptureEvent.End));
            Assert.That(o.endReason, Is.EqualTo(CaptureEndReason.Released));
        }

        [Test]
        public void ExplicitModeIgnoresDepthNoiseButRequiresProximity()
        {
            var cap = new SurfaceStrokeCapture(); var s = Explicit();
            Assert.That(cap.Step(In(0, 0, 30, 0.00, held: false), Size, s).evt, Is.EqualTo(CaptureEvent.None));
            Assert.That(cap.Step(In(0, 0, 30, 0.01, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.Begin));
            Assert.That(cap.Step(In(0.002f, 0, -15, 0.02, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.Continue));
            Assert.That(cap.Step(In(0.004f, 0, 45, 0.03, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.Continue));
            Assert.That(cap.Step(In(0.004f, 0, 45, 0.04, held: false), Size, s).evt, Is.EqualTo(CaptureEvent.End));

            var far = new SurfaceStrokeCapture();
            Assert.That(far.Step(In(0, 0, 400, 0.0, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.None),
                "holding the trigger far from the board must not ink");
        }

        [Test]
        public void TrackingLossEndsStrokeAndRequiresRearm()
        {
            var cap = new SurfaceStrokeCapture(); var s = Explicit();
            cap.Step(In(0, 0, 5, 0.00, held: true), Size, s);
            cap.Step(In(0.001f, 0, 5, 0.01, held: true), Size, s);
            CaptureOutput lost = cap.Step(In(0, 0, 0, 0.02, held: true, tracked: false), Size, s);
            Assert.That(lost.evt, Is.EqualTo(CaptureEvent.End));
            Assert.That(lost.endReason, Is.EqualTo(CaptureEndReason.TrackingLost));

            // Tracking returns with the trigger still held: no ink, and nothing connects to the old point.
            CaptureOutput back = cap.Step(In(0.05f, 0.05f, 5, 0.03, held: true), Size, s);
            Assert.That(back.evt, Is.EqualTo(CaptureEvent.None));
            Assert.That(back.state, Is.EqualTo(TipState.NeedsRearm));
            Assert.That(cap.Step(In(0.05f, 0.05f, 5, 0.04, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.None));

            // Deliberate release, then press again: a new stroke begins.
            Assert.That(cap.Step(In(0.05f, 0.05f, 5, 0.05, held: false), Size, s).state, Is.EqualTo(TipState.Hover));
            Assert.That(cap.Step(In(0.05f, 0.05f, 5, 0.06, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.Begin));
        }

        [Test]
        public void TrackingLossWhileIdleStillRequiresRearmInContactMode()
        {
            var cap = new SurfaceStrokeCapture(); var s = Contact();
            cap.Step(In(0, 0, 50, 0.00), Size, s);
            cap.Step(In(0, 0, 0, 0.01, tracked: false), Size, s);
            // The pose reappears already "touching": must not ink until lifted once.
            Assert.That(cap.Step(In(0, 0, 1, 0.02), Size, s).evt, Is.EqualTo(CaptureEvent.None));
            Assert.That(cap.Step(In(0, 0, 30, 0.03), Size, s).state, Is.EqualTo(TipState.Hover));
            Assert.That(cap.Step(In(0, 0, 1, 0.04), Size, s).evt, Is.EqualTo(CaptureEvent.Begin));
        }

        [Test]
        public void PoseJumpEndsStrokeInsteadOfDrawingAConnectingLine()
        {
            var cap = new SurfaceStrokeCapture(); var s = Explicit();
            cap.Step(In(0, 0, 2, 0.000, held: true), Size, s);
            cap.Step(In(0.001f, 0, 2, 0.011, held: true), Size, s);
            CaptureOutput o = cap.Step(In(0.2f, 0.1f, 2, 0.022, held: true), Size, s);
            Assert.That(o.evt, Is.EqualTo(CaptureEvent.End));
            Assert.That(o.endReason, Is.EqualTo(CaptureEndReason.Jump));
            Assert.That(cap.NeedsRearm, Is.True);
        }

        [Test]
        public void FastButPlausibleStrokeIsNotRejected()
        {
            var cap = new SurfaceStrokeCapture(); var s = Explicit();
            cap.Step(In(-0.3f, 0, 2, 0.0, held: true), Size, s);
            // 1.5 m/s at 90 Hz = 16.7 mm per frame: a quick underline, not a glitch.
            for (int i = 1; i < 20; i++)
            {
                CaptureOutput o = cap.Step(In(-0.3f + i * 0.0167f, 0, 2, i / 90.0, held: true), Size, s);
                Assert.That(o.evt, Is.EqualTo(CaptureEvent.Continue), "frame " + i);
            }
        }

        [Test]
        public void StaleSampleNeverConnects()
        {
            var cap = new SurfaceStrokeCapture(); var s = Explicit();
            cap.Step(In(0, 0, 2, 0.0, held: true), Size, s);
            CaptureOutput o = cap.Step(In(0.003f, 0, 2, 0.9, held: true), Size, s); // app hitched for 0.9 s
            Assert.That(o.evt, Is.EqualTo(CaptureEvent.End));
            Assert.That(o.endReason, Is.EqualTo(CaptureEndReason.Stale));
        }

        [Test]
        public void LeavingBoundsEndsAndReentryStartsASeparateStroke()
        {
            var cap = new SurfaceStrokeCapture(); var s = Explicit();
            cap.Step(In(0.49f, 0, 2, 0.00, held: true), Size, s);
            CaptureOutput o = cap.Step(In(0.51f, 0, 2, 0.01, held: true), Size, s);
            Assert.That(o.evt, Is.EqualTo(CaptureEvent.End));
            Assert.That(o.endReason, Is.EqualTo(CaptureEndReason.LeftBounds));
            Assert.That(cap.Step(In(0.52f, 0, 2, 0.02, held: true), Size, s).state, Is.EqualTo(TipState.OutOfBounds));
            Assert.That(cap.Step(In(0.49f, 0.01f, 2, 0.03, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.Begin));
        }

        [Test]
        public void UiInteractionNeverCreatesInk()
        {
            var cap = new SurfaceStrokeCapture(); var s = Explicit();
            // Trigger pressed while pointing at the palette, then dragged onto the board.
            Assert.That(cap.Step(In(0, 0, 2, 0.00, held: true, suppressed: true), Size, s).evt, Is.EqualTo(CaptureEvent.None));
            Assert.That(cap.Step(In(0, 0, 2, 0.01, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.None));
            cap.Step(In(0, 0, 2, 0.02, held: false), Size, s);
            Assert.That(cap.Step(In(0, 0, 2, 0.03, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.Begin));
            // UI becoming active mid-stroke ends it.
            Assert.That(cap.Step(In(0, 0, 2, 0.04, held: true, suppressed: true), Size, s).endReason, Is.EqualTo(CaptureEndReason.Suppressed));
        }

        [Test]
        public void ImplausiblePenetrationEndsContactStroke()
        {
            var cap = new SurfaceStrokeCapture(); var s = Contact();
            cap.Step(In(0, 0, 20, 0.00), Size, s);
            cap.Step(In(0, 0, 1, 0.01), Size, s);
            CaptureOutput o = cap.Step(In(0, 0, -80, 0.02), Size, s);
            Assert.That(o.evt, Is.EqualTo(CaptureEvent.End));
            Assert.That(o.endReason, Is.EqualTo(CaptureEndReason.Implausible));
        }

        [Test]
        public void ExternalInterruptReportsActiveStrokeAndRearms()
        {
            var cap = new SurfaceStrokeCapture(); var s = Explicit();
            Assert.That(cap.Interrupt(), Is.False);
            cap.Step(In(0, 0, 2, 0.00, held: false), Size, s); // clears the re-arm
            cap.Step(In(0, 0, 2, 0.01, held: true), Size, s);
            Assert.That(cap.Interrupt(), Is.True);
            Assert.That(cap.IsDrawing, Is.False);
            Assert.That(cap.Step(In(0, 0, 2, 0.02, held: true), Size, s).evt, Is.EqualTo(CaptureEvent.None));
        }
    }

    public class StrokeBuilderTests
    {
        static StrokeData NewStroke() => new StrokeData { space = StrokeSpace.Board, width = 0.0015f, color = new Color32(0, 0, 0, 255) };

        [Test]
        public void TapBecomesASinglePointDot()
        {
            var b = new StrokeBuilder(); var set = new StrokeBuildSettings();
            b.Begin(NewStroke(), set, new Vector3(0.1f, 0.2f, 0.002f), 0.0, 1f, false);
            b.Add(new Vector3(0.10001f, 0.2f, 0.002f), 0.011, 1f);
            StrokeData s = b.End();
            Assert.That(s.points.Count, Is.EqualTo(1));
            Assert.That(s.points[0].z, Is.EqualTo(0f), "board strokes are stored in the plane");
            Assert.That(s.raw.Count, Is.EqualTo(2), "raw samples are kept, including height");
            Assert.That(s.raw[0].position.z, Is.EqualTo(0.002f));
        }

        [Test]
        public void OneMillimetreStrokeSurvives()
        {
            // A decimal point dragged slightly, or a tiny minus sign.
            var b = new StrokeBuilder(); var set = new StrokeBuildSettings();
            b.Begin(NewStroke(), set, new Vector3(0, 0, 0), 0.0, 1f, false);
            for (int i = 1; i <= 5; i++) b.Add(new Vector3(i * 0.0002f, 0, 0), i * 0.011, 1f);
            StrokeData s = b.End();
            Assert.That(s.points.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(s.points[s.points.Count - 1].x, Is.EqualTo(0.001f).Within(1e-6f), "stroke ends at the last raw tip position");
            Assert.That(s.PathLength(), Is.EqualTo(0.001f).Within(2e-4f));
        }

        [Test]
        public void SpacingLimitsPointCountWithoutLosingLength()
        {
            var b = new StrokeBuilder(); var set = new StrokeBuildSettings { spacingMm = 0.5f, minCutoffHz = 0f };
            b.Begin(NewStroke(), set, Vector3.zero, 0.0, 1f, false);
            for (int i = 1; i <= 1000; i++) b.Add(new Vector3(i * 0.00005f, 0, 0), i * 0.001, 1f); // 50 mm in 0.05 mm steps
            StrokeData s = b.End();
            Assert.That(s.points.Count, Is.InRange(95, 105));
            Assert.That(s.PathLength(), Is.EqualTo(0.05f).Within(1e-4f));
        }

        [Test]
        public void SharpCornerOfSmallGlyphIsPreserved()
        {
            // A 4 mm "L": down 4 mm, then right 4 mm, written at 5 cm/s sampled at 90 Hz.
            var b = new StrokeBuilder(); var set = new StrokeBuildSettings();
            float dt = 1f / 90f, step = 0.05f * dt;
            Vector3 p = new Vector3(0, 0.004f, 0);
            double t = 0;
            b.Begin(NewStroke(), set, p, t, 1f, false);
            while (p.y > 0f) { p.y = Mathf.Max(0f, p.y - step); t += dt; b.Add(p, t, 1f); }
            while (p.x < 0.004f) { p.x = Mathf.Min(0.004f, p.x + step); t += dt; b.Add(p, t, 1f); }
            StrokeData s = b.End();

            float best = float.MaxValue;
            foreach (Vector3 q in s.points) best = Mathf.Min(best, (q - Vector3.zero).magnitude);
            Assert.That(best, Is.LessThan(0.0004f), "corner rounded by more than 0.4 mm");
        }

        [Test]
        public void StraightLineToolKeepsTwoPoints()
        {
            var b = new StrokeBuilder(); var set = new StrokeBuildSettings();
            b.Begin(NewStroke(), set, Vector3.zero, 0.0, 1f, true);
            b.Add(new Vector3(0.01f, 0.003f, 0), 0.01, 1f);
            b.Add(new Vector3(0.05f, 0.001f, 0), 0.02, 1f);
            StrokeData s = b.End();
            Assert.That(s.points.Count, Is.EqualTo(2));
            Assert.That(s.points[1].x, Is.EqualTo(0.05f).Within(1e-6f));
            Assert.That(s.isStraightLine, Is.True);
        }

        [Test]
        public void PointBudgetIsReported()
        {
            var b = new StrokeBuilder(); var set = new StrokeBuildSettings { maxPointsPerStroke = 10, minCutoffHz = 0f };
            b.Begin(NewStroke(), set, Vector3.zero, 0.0, 1f, false);
            bool ok = true; int n = 0;
            while (ok && n < 100) { n++; ok = b.Add(new Vector3(n * 0.001f, 0, 0), n * 0.01, 1f); }
            Assert.That(ok, Is.False);
            Assert.That(b.End().points.Count, Is.LessThanOrEqualTo(11));
        }

        [Test]
        public void RebuildFromRawAppliesNewSmoothingAndKeepsRawData()
        {
            var b = new StrokeBuilder(); var set = new StrokeBuildSettings { minCutoffHz = 0f };
            b.Begin(NewStroke(), set, Vector3.zero, 0.0, 1f, false);
            uint seed = 5;
            for (int i = 1; i <= 200; i++)
            {
                seed = seed * 1664525u + 1013904223u;
                float jitter = ((seed >> 8) / 16777216f * 2f - 1f) * 0.0004f;
                b.Add(new Vector3(i * 0.0003f, jitter, 0), i / 90.0, 1f);
            }
            StrokeData s = b.End();
            int rawCount = s.raw.Count;
            float roughBefore = Roughness(s);
            StrokeBuilder.RebuildFromRaw(s, new StrokeBuildSettings { minCutoffHz = 2f, beta = 5f });
            Assert.That(s.raw.Count, Is.EqualTo(rawCount));
            Assert.That(Roughness(s), Is.LessThan(roughBefore * 0.7f));
        }

        static float Roughness(StrokeData s)
        {
            float sum = 0f;
            for (int i = 1; i < s.points.Count - 1; i++) sum += Mathf.Abs(s.points[i].y);
            return sum / Mathf.Max(1, s.points.Count - 2);
        }
    }

    public class StrokeHistoryTests
    {
        static StrokeData Make(StrokeHistory h, StrokeSpace space = StrokeSpace.Board)
        {
            var s = new StrokeData { id = h.NextStrokeId(), space = space, width = 0.002f };
            s.points.Add(Vector3.zero);
            h.AddStroke(s);
            return s;
        }

        [Test]
        public void UndoRedoDrawing()
        {
            var h = new StrokeHistory();
            StrokeData a = Make(h), b = Make(h);
            Assert.That(h.VisibleCount, Is.EqualTo(2));
            Assert.That(h.Undo(), Is.True);
            Assert.That(b.visible, Is.False);
            Assert.That(a.visible, Is.True);
            Assert.That(h.Redo(), Is.True);
            Assert.That(b.visible, Is.True);
            Assert.That(h.CanRedo, Is.False);
        }

        [Test]
        public void NewActionAfterUndoInvalidatesRedoAndDiscardsTheStroke()
        {
            var h = new StrokeHistory();
            var discarded = new List<StrokeData>();
            h.Discarded += discarded.Add;
            StrokeData a = Make(h), b = Make(h);
            h.Undo();
            Assert.That(h.CanRedo, Is.True);
            StrokeData c = Make(h);
            Assert.That(h.CanRedo, Is.False);
            Assert.That(h.Redo(), Is.False);
            Assert.That(discarded, Is.EquivalentTo(new[] { b }));
            Assert.That(h.Strokes.Count, Is.EqualTo(2));
            Assert.That(a.visible && c.visible, Is.True);
        }

        [Test]
        public void EraseIsUndoableAndGroupedPerGesture()
        {
            var h = new StrokeHistory();
            StrokeData a = Make(h), b = Make(h), c = Make(h);
            int changes = 0;
            h.VisibilityChanged += _ => changes++;

            int g = h.NewEraseGroup();
            h.Erase(new List<StrokeData> { a }, g);
            h.Erase(new List<StrokeData> { b }, g);     // same drag
            h.Erase(new List<StrokeData> { b }, g);     // already hidden: no-op
            Assert.That(h.VisibleCount, Is.EqualTo(1));
            Assert.That(changes, Is.EqualTo(2));

            h.Undo(); // one step restores the whole drag
            Assert.That(a.visible && b.visible && c.visible, Is.True);
            h.Redo();
            Assert.That(a.visible || b.visible, Is.False);

            int g2 = h.NewEraseGroup();
            h.Erase(new List<StrokeData> { c }, g2);    // separate gesture: separate step
            h.Undo();
            Assert.That(c.visible, Is.True);
            Assert.That(a.visible, Is.False);
        }

        [Test]
        public void ClearIsOneUndoableStepAndRespectsSpace()
        {
            var h = new StrokeHistory();
            StrokeData a = Make(h), b = Make(h), air = Make(h, StrokeSpace.Air);
            Assert.That(h.Clear(StrokeSpace.Board), Is.EqualTo(2));
            Assert.That(air.visible, Is.True);
            Assert.That(h.VisibleCount, Is.EqualTo(1));
            h.Undo();
            Assert.That(a.visible && b.visible, Is.True);
            h.Redo();
            Assert.That(a.visible || b.visible, Is.False);
            Assert.That(h.Clear(StrokeSpace.Board), Is.EqualTo(0), "clearing an empty board adds no undo step");
            h.Undo();
            Assert.That(a.visible && b.visible, Is.True);
        }

        [Test]
        public void MixedSequenceUndoesInReverseOrder()
        {
            var h = new StrokeHistory();
            StrokeData a = Make(h);
            h.Erase(new List<StrokeData> { a });
            StrokeData b = Make(h);
            h.Clear();
            Assert.That(h.VisibleCount, Is.EqualTo(0));
            h.Undo(); Assert.That(b.visible && !a.visible, Is.True);   // clear undone
            h.Undo(); Assert.That(!b.visible && !a.visible, Is.True);  // draw b undone
            h.Undo(); Assert.That(a.visible, Is.True);                 // erase a undone
            h.Undo(); Assert.That(a.visible, Is.False);                // draw a undone
            Assert.That(h.CanUndo, Is.False);
            h.Redo(); h.Redo(); h.Redo(); h.Redo();
            Assert.That(h.VisibleCount, Is.EqualTo(0));
            Assert.That(h.CanRedo, Is.False);
        }

        [Test]
        public void UndoDepthIsBoundedAndOldErasuresBecomePermanent()
        {
            var h = new StrokeHistory { maxUndoSteps = 3 };
            var discarded = new List<StrokeData>();
            h.Discarded += discarded.Add;
            StrokeData a = Make(h);
            h.Erase(new List<StrokeData> { a });
            Make(h); Make(h); Make(h);
            Assert.That(discarded, Does.Contain(a));
            int undone = 0;
            while (h.Undo()) undone++;
            Assert.That(undone, Is.EqualTo(3));
        }
    }

    public class StrokeMeshBuilderTests
    {
        static StrokeData Stroke(params Vector3[] pts)
        {
            var s = new StrokeData { space = StrokeSpace.Board, width = 0.002f, color = new Color32(10, 20, 30, 255) };
            s.points.AddRange(pts);
            s.RecomputeBounds();
            return s;
        }

        static void AssertValid(MeshBuffers b)
        {
            Assert.That(b.indices.Count % 3, Is.EqualTo(0));
            foreach (int i in b.indices) Assert.That(i, Is.InRange(0, b.VertexCount - 1));
            foreach (Vector3 v in b.vertices)
                Assert.That(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.x), Is.False);
            Assert.That(b.colors.Count, Is.EqualTo(b.VertexCount));
            Assert.That(b.uvs.Count, Is.EqualTo(b.VertexCount));
        }

        [Test]
        public void DotIsADiscOfTheStrokeWidth()
        {
            var b = new MeshBuffers(); b.Clear();
            StrokeMeshBuilder.Append(b, Stroke(new Vector3(0.1f, 0.1f, 0)), 0.0005f);
            AssertValid(b);
            Assert.That(b.VertexCount, Is.EqualTo(StrokeMeshBuilder.DiscSegments + 1));
            Assert.That(b.boundsMax.x - b.boundsMin.x, Is.EqualTo(0.002f).Within(1e-5f));
            Assert.That(b.boundsMin.z, Is.EqualTo(-0.0005f).Within(1e-7f), "ink is lifted toward the writer");
            Assert.That(b.boundsMax.z, Is.EqualTo(-0.0005f).Within(1e-7f), "board ink is flat");
        }

        [Test]
        public void HorizontalLineHasConstantPhysicalWidth()
        {
            var b = new MeshBuffers(); b.Clear();
            StrokeMeshBuilder.Append(b, Stroke(new Vector3(0, 0, 0), new Vector3(0.01f, 0, 0), new Vector3(0.02f, 0, 0)), 0f);
            AssertValid(b);
            Assert.That(b.boundsMax.y - b.boundsMin.y, Is.EqualTo(0.002f).Within(1e-5f));
            Assert.That(b.boundsMax.x - b.boundsMin.x, Is.EqualTo(0.022f).Within(1e-4f), "round caps extend half a width past each end");
        }

        [Test]
        public void DuplicateAndNearDuplicatePointsAreHarmless()
        {
            var p = new Vector3(0.05f, 0.05f, 0);
            var b = new MeshBuffers(); b.Clear();
            StrokeMeshBuilder.Append(b, Stroke(p, p, p + new Vector3(1e-7f, 0, 0), p), 0f);
            AssertValid(b);
            Assert.That(b.VertexCount, Is.EqualTo(StrokeMeshBuilder.DiscSegments + 1), "collapses to a dot");

            b.Clear();
            StrokeMeshBuilder.Append(b, Stroke(Vector3.zero, Vector3.zero, new Vector3(0.01f, 0, 0), new Vector3(0.01f, 0, 0)), 0f);
            AssertValid(b);
        }

        [Test]
        public void SharpTurnsAndReversalsStayBounded()
        {
            // A zig-zag with 180-degree reversals, like scribbling over a mistake.
            var b = new MeshBuffers(); b.Clear();
            StrokeMeshBuilder.Append(b, Stroke(
                new Vector3(0, 0, 0), new Vector3(0.01f, 0, 0), new Vector3(0, 0.0001f, 0),
                new Vector3(0.01f, 0.0002f, 0), new Vector3(0.01f, 0.01f, 0)), 0f);
            AssertValid(b);
            // No miter spike: nothing extends further than half a width beyond the points.
            Assert.That(b.boundsMin.x, Is.GreaterThan(-0.0011f));
            Assert.That(b.boundsMax.x, Is.LessThan(0.0111f));
            Assert.That(b.boundsMax.y, Is.LessThan(0.0111f));
        }

        [Test]
        public void VertexEstimateIsAnUpperBound()
        {
            var pts = new List<Vector3>();
            for (int i = 0; i < 200; i++) pts.Add(new Vector3(i * 0.001f, (i % 2) * 0.004f, 0)); // all sharp joins
            var b = new MeshBuffers(); b.Clear();
            StrokeMeshBuilder.Append(b, Stroke(pts.ToArray()), 0f);
            AssertValid(b);
            Assert.That(b.VertexCount, Is.LessThanOrEqualTo(StrokeMeshBuilder.MaxVertexCount(pts.Count)));
        }

        [Test]
        public void AirRibbonNormalDoesNotFlipAlongAHelix()
        {
            var s = new StrokeData { space = StrokeSpace.Air, width = 0.004f, ribbonNormal = Vector3.back };
            for (int i = 0; i < 120; i++)
            {
                float a = i * 0.15f;
                s.points.Add(new Vector3(Mathf.Cos(a) * 0.1f, i * 0.002f, Mathf.Sin(a) * 0.1f));
            }
            var b = new MeshBuffers(); b.Clear();
            StrokeMeshBuilder.Append(b, s, 0f);
            AssertValid(b);
            // Ribbon width stays constant: every vertex is within half a width (plus miter slack) of some point.
            foreach (Vector3 v in b.vertices)
            {
                float best = float.MaxValue;
                foreach (Vector3 p in s.points) best = Mathf.Min(best, (p - v).magnitude);
                Assert.That(best, Is.LessThan(0.0024f));
            }
        }

        [Test]
        public void EraserHitTestUsesInkWidthAndRadius()
        {
            StrokeData s = Stroke(new Vector3(0, 0, 0), new Vector3(0.05f, 0, 0));
            Assert.That(StrokeHitTest.Hits(s, new Vector3(0.025f, 0.004f, 0), 0.005f), Is.True);
            Assert.That(StrokeHitTest.Hits(s, new Vector3(0.025f, 0.007f, 0), 0.005f), Is.False);
            Assert.That(StrokeHitTest.Hits(s, new Vector3(0.058f, 0f, 0), 0.005f), Is.False);
            StrokeData dot = Stroke(new Vector3(0.1f, 0.1f, 0));
            Assert.That(StrokeHitTest.Hits(dot, new Vector3(0.103f, 0.1f, 0), 0.005f), Is.True);
        }
    }
}
