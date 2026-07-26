using NUnit.Framework;
using UnityEngine;
using Magi.Inkling.Systems.Brush;
using Magi.Inkling.Systems.SimulationLOD0;   // CP8w: SimulationContext.ColdSourceInkIndex
using Magi.Inkling.Tests.Helpers;

namespace Magi.Inkling.Tests.PlayMode
{
    /// <summary>
    /// CP8p: the right-mouse continuous directional emitters Lake asked for.
    ///
    /// These run against StubSimulationWriter — no scene, no GPU, no SimDriver — because
    /// DirectionalEmitterController deliberately owns no input handling and no simulation lookup. That
    /// separation is what makes the gesture logic (the part with real edge cases) testable at all.
    /// </summary>
    public class DirectionalEmitterTests
    {
        private GameObject go;
        private DirectionalEmitterController ctrl;
        private StubSimulationWriter writer;

        [SetUp]
        public void SetUp()
        {
            go = new GameObject("CP8p_Emitters");
            ctrl = go.AddComponent<DirectionalEmitterController>();
            writer = go.AddComponent<StubSimulationWriter>();
            ctrl.SetWriter(writer);
        }

        [TearDown]
        public void TearDown()
        {
            if (go != null) Object.DestroyImmediate(go);
        }

        // Lake: "if the space is empty, create an emitter with impulse in the direction of the mouse
        // movement, as per the left mouse, just CONTINUOUS." So one drag must leave behind something that
        // keeps emitting AND keeps pushing, every frame, with no further input. Fire is passed explicitly
        // here because CP8s made the ink a caller-supplied selection rather than a fixed default.
        [Test]
        public void RmbDragInEmptySpace_CreatesContinuousEmitter_PushingAlongTheDrag()
        {
            bool created = ctrl.ApplyDragGesture(new Vector2(0.25f, 0.5f), new Vector2(0.45f, 0.5f), 0);

            Assert.IsTrue(created, "A drag through empty space must CREATE an emitter");
            Assert.AreEqual(1, ctrl.Count);

            writer.Reset();
            ctrl.Tick();
            ctrl.Tick();   // continuous: a second frame with NO further input must emit again

            Assert.AreEqual(2, writer.DensityCalls, "Emitter must inject density EVERY frame (continuous)");
            Assert.AreEqual(2, writer.ForceCalls, "…and inject force every frame too");
            Assert.AreEqual(0, writer.LastDensityInkTypeIndex, "Emitted ink must be Fire (index 0)");

            // Rightward drag => rightward push. This is the directional half of the feature and the thing
            // that actually drives fire INTO the ice formation.
            Assert.That(writer.LastForce.x, Is.GreaterThan(0f), "Force must point along the drag (+x)");
            Assert.That(Mathf.Abs(writer.LastForce.y), Is.LessThan(1e-4f), "…with no spurious vertical push");
            Assert.That(writer.LastDensityPosition.x, Is.EqualTo(0.25f).Within(1e-4f),
                "Emitter sits where the drag BEGAN, so it pushes across the gap toward the target");
        }

        // Lake: "Where that path would cross an existing emitter, remove that emitter." Removal must WIN —
        // a delete gesture that also spawned a fresh emitter on the same spot would be unusable.
        [Test]
        public void RmbDragCrossingAnExistingEmitter_RemovesIt_AndDoesNotCreateADuplicate()
        {
            ctrl.ApplyDragGesture(new Vector2(0.5f, 0.5f), new Vector2(0.7f, 0.5f), 0);
            Assume.That(ctrl.Count, Is.EqualTo(1));

            // Drag straight THROUGH the existing emitter at (0.5, 0.5).
            bool created = ctrl.ApplyDragGesture(new Vector2(0.5f, 0.4f), new Vector2(0.5f, 0.6f), 0);

            Assert.IsFalse(created, "A drag crossing an emitter is a DELETE gesture, not a create");
            Assert.AreEqual(0, ctrl.Count, "…and the crossed emitter must be gone");

            writer.Reset();
            ctrl.Tick();
            Assert.AreEqual(0, writer.DensityCalls, "A removed emitter must stop emitting entirely");
        }

        // The crossing test must use distance to the drag SEGMENT, not to its endpoints — otherwise a
        // fast drag that flies straight past an emitter (neither endpoint near it) would fail to delete.
        [Test]
        public void DragPassingOverAnEmitter_RemovesIt_EvenWhenBothEndpointsAreFarAway()
        {
            ctrl.ApplyDragGesture(new Vector2(0.5f, 0.5f), new Vector2(0.6f, 0.5f), 0);
            Assume.That(ctrl.Count, Is.EqualTo(1));

            // Both endpoints are far from (0.5,0.5), but the path sweeps directly over it.
            bool created = ctrl.ApplyDragGesture(new Vector2(0.1f, 0.5f), new Vector2(0.9f, 0.5f), 0);

            Assert.IsFalse(created, "Path crosses the emitter, so this deletes rather than creates");
            Assert.AreEqual(0, ctrl.Count);
        }

        [Test]
        public void DragMissingAllEmitters_CreatesAnother_AndBothKeepEmitting()
        {
            ctrl.ApplyDragGesture(new Vector2(0.2f, 0.2f), new Vector2(0.3f, 0.2f), 0);
            ctrl.ApplyDragGesture(new Vector2(0.2f, 0.8f), new Vector2(0.3f, 0.8f), 0);   // well clear

            Assert.AreEqual(2, ctrl.Count, "A drag that crosses nothing must add a second emitter");

            writer.Reset();
            ctrl.Tick();
            Assert.AreEqual(2, writer.DensityCalls, "Both emitters emit each frame");
        }

        // A click with no movement has no direction to push, so it must not create a degenerate,
        // zero-force emitter that silently does nothing forever.
        [Test]
        public void RmbClickWithoutDragging_CreatesNothing()
        {
            bool created = ctrl.ApplyDragGesture(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), 0);

            Assert.IsFalse(created, "A zero-length drag has no direction and must not create an emitter");
            Assert.AreEqual(0, ctrl.Count);
        }

        // ── CP8p-fix (Codex): Fire-vs-Ice harness invariants ────────────────────────────────
        // Source-level guards. The harness itself needs a live sim to run, but these three properties are
        // exactly the ones whose violation makes its OUTPUT lie, so they are worth pinning cheaply.

        // Obstacles.hlsl:UpdateObstacles stamps a built-in circular geometry obstacle at the sim CENTRE
        // with radius 0.1 UV — spanning UV [0.4, 0.6] on both axes. If the fire lane or the ice wall
        // overlaps it, `fireReachesIce` / `thinIceIsObstacle` / breakthrough all conflate GEOMETRY
        // blockage with ICE behaviour, i.e. the harness misdiagnoses the exact CP8o question it exists
        // to answer. The lane must stay clear of that circle.
        [Test]
        public void FireIceLane_AvoidsTheBuiltInCentreGeometryObstacle()
        {
#if UNITY_EDITOR
            string src = System.IO.File.ReadAllText("Assets/_Project/Scripts/Dev/FireIceScenario.cs");

            float Read(string key)
            {
                var m = System.Text.RegularExpressions.Regex.Match(src, key + @"\s*=\s*([0-9.]+)f");
                Assert.IsTrue(m.Success, $"could not read {key} from FireIceScenario.cs");
                return float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            // Centre obstacle occupies UV [0.4, 0.6] on both axes.
            const float ObsLo = 0.4f, ObsHi = 0.6f;

            float iceY0 = Read("IceY0"), iceY1 = Read("IceY1");
            bool wallClearsCircle = iceY1 < ObsLo || iceY0 > ObsHi;

            Assert.IsTrue(wallClearsCircle,
                $"Ice wall spans y {iceY0}..{iceY1}, which overlaps the built-in centre geometry obstacle " +
                $"(UV {ObsLo}..{ObsHi}). Geometry blockage would be reported as ice behaviour.");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // The wall must be seeded from a ZERO array, not by reading and patching the live particle buffer.
        // Patching inherited whatever was already in the sim (startup ink, creature injectors, leftover
        // user painting) into every channel and every cell outside the wall — contamination that lands
        // straight in the metrics.
        [Test]
        public void FireIceHarness_SeedsFromCleanArrays_NotTheLiveBuffer()
        {
#if UNITY_EDITOR
            string src = System.IO.File.ReadAllText("Assets/_Project/Scripts/Dev/FireIceScenario.cs");

            StringAssert.Contains("new iparticle[count]", src,
                "The wall must be seeded from a zero-initialised particle array");
            Assert.IsFalse(src.Contains("ParticlesBuffer[ctx.ParticleReadIndex].GetData(all)"),
                "REGRESSION: seeding must NOT read the live particle buffer as its baseline — prior ink " +
                "and velocity would contaminate the fire-vs-ice metrics");

            // Velocity must be cleared too, or leftover flow pushes the fire and invalidates the impulse
            // metrics; and both ping-pong sides must be written or the first swap loses the seed.
            StringAssert.Contains("ClearRT(ctx.Velocity?.Read)", src, "velocity must be zeroed before Scenario A");
            StringAssert.Contains("for (int b = 0; b < ctx.ParticlesBuffer.Length; b++)", src,
                "both particle ping-pong sides must be seeded");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // Impulse efficacy must be numerically inspectable, not only visible in a velocity PNG.
        [Test]
        public void FireIceMetricSchema_IncludesRegionalVelocityFields()
        {
#if UNITY_EDITOR
            string src = System.IO.File.ReadAllText("Assets/_Project/Scripts/Dev/FireIceScenario.cs");
            foreach (var field in new[] { "velAvg", "velMax", "velAvgX" })
                StringAssert.Contains("\\\"" + field + "\\\":", src,
                    $"metrics JSON must expose {field} so directional impulse efficacy is a number");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // ── CP8s: emitters use the CURRENTLY SELECTED ink, not always Fire ──────────────────
        //
        // Lake: "It would also be nice if the new right click and drag emitters were tied to whatever ink
        // was the currently selected ink, instead of just fire."
        //
        // Semantics are SNAPSHOT-AT-CREATION: an emitter keeps the ink it was made with. Changing the
        // selection afterwards must not retroactively repaint existing emitters — otherwise you could
        // never build a scene with a fire emitter AND a water emitter running at once, which is the whole
        // point of having persistent emitters.
        [Test]
        public void RmbDrag_CreatesEmitterUsingTheSelectedInk_NotAlwaysFire()
        {
            const int water = 1;
            bool created = ctrl.ApplyDragGesture(new Vector2(0.2f, 0.5f), new Vector2(0.4f, 0.5f), water);

            Assert.IsTrue(created);
            writer.Reset();
            ctrl.Tick();

            Assert.AreEqual(water, writer.LastDensityInkTypeIndex,
                "The emitter must emit the SELECTED ink, not the hardcoded Fire default");
            Assert.AreEqual(new Color(0f, 0f, 1f, 1f), writer.LastDensityColor,
                "…and carry that ink's key colour (Water = blue), matching left-click painting");
        }

        [Test]
        public void EmittersSnapshotTheirInk_AtCreation_NotGlobally()
        {
            ctrl.ApplyDragGesture(new Vector2(0.2f, 0.2f), new Vector2(0.35f, 0.2f), 0);   // Fire
            ctrl.ApplyDragGesture(new Vector2(0.2f, 0.8f), new Vector2(0.35f, 0.8f), 9);   // Ice, well clear
            Assume.That(ctrl.Count, Is.EqualTo(2));

            writer.Reset();
            ctrl.Tick();

            var inks = new System.Collections.Generic.List<int>();
            foreach (var d in writer.DensityHistory) inks.Add(d.index);

            CollectionAssert.Contains(inks, 0, "the first emitter must still emit Fire");
            CollectionAssert.Contains(inks, 9, "the second must emit Ice — selection is snapshot, not shared");
        }

        // Deletion must stay ink-agnostic: dragging through an emitter removes it whatever ink you happen
        // to have selected. Otherwise you could not clean up an emitter without first re-selecting its ink.
        [Test]
        public void RmbDragCrossingAnEmitter_RemovesIt_RegardlessOfSelectedInk()
        {
            ctrl.ApplyDragGesture(new Vector2(0.5f, 0.5f), new Vector2(0.65f, 0.5f), 0);   // Fire emitter
            Assume.That(ctrl.Count, Is.EqualTo(1));

            // Cross it while a completely different ink is selected.
            bool created = ctrl.ApplyDragGesture(new Vector2(0.5f, 0.4f), new Vector2(0.5f, 0.6f), 9);

            Assert.IsFalse(created, "crossing an emitter deletes, whatever ink is selected");
            Assert.AreEqual(0, ctrl.Count);
        }

        [Test]
        public void SelectedInk_IsClampedToValidRange_MatchingSimDriver()
        {
            ctrl.ApplyDragGesture(new Vector2(0.2f, 0.3f), new Vector2(0.35f, 0.3f), 99);
            ctrl.ApplyDragGesture(new Vector2(0.2f, 0.7f), new Vector2(0.35f, 0.7f), -5);
            Assume.That(ctrl.Count, Is.EqualTo(2));

            writer.Reset();
            ctrl.Tick();

            foreach (var d in writer.DensityHistory)
                Assert.That(d.index, Is.InRange(0, SimulationContext.ColdSourceInkIndex),
                    "Emitter ink must clamp to the same range SimDriver.CurrentInkType does. CP8w " +
                    "widened that to include ColdAir (10), so 99 now clamps to 10, not 9 — an index " +
                    "past ColdAir would address a nonexistent selection.");
        }

        // CP8w: a ColdAir emitter is a continuous COOLER, not a jet. It must not push, or it would stir
        // the velocity field it exists to observe and you could not separate freezing from advection.
        [Test]
        public void ColdAirEmitter_InjectsButAppliesNoForce()
        {
            ctrl.ApplyDragGesture(new Vector2(0.3f, 0.5f), new Vector2(0.5f, 0.5f),
                SimulationContext.ColdSourceInkIndex);
            Assume.That(ctrl.Count, Is.EqualTo(1), "precondition: the ColdAir emitter was created");

            writer.Reset();
            ctrl.Tick();
            ctrl.Tick();

            Assert.AreEqual(2, writer.DensityCalls,
                "ColdAir emitters must still tick every frame — that continuity is the cooling");
            Assert.AreEqual(0, writer.ForceCalls,
                "…but must apply NO directional force, unlike every normal ink emitter");
            Assert.AreEqual(SimulationContext.ColdSourceInkIndex, writer.LastDensityInkTypeIndex,
                "and the index must survive as ColdAir rather than being clamped to Ice");
        }

        // Guard the contrast: normal inks keep CP8p force semantics. Without this, deleting the force
        // call entirely would still pass the test above.
        [Test]
        public void NormalInkEmitter_StillAppliesForce()
        {
            ctrl.ApplyDragGesture(new Vector2(0.3f, 0.2f), new Vector2(0.5f, 0.2f), 0);   // Fire
            Assume.That(ctrl.Count, Is.EqualTo(1));

            writer.Reset();
            ctrl.Tick();

            Assert.AreEqual(1, writer.ForceCalls,
                "normal ink emitters must keep pushing — CP8w only suppressed force for ColdAir");
        }

        // The input layer must source the same selection left-click painting uses, or the two paths drift.
        [Test]
        public void BrushInputController_PassesSimDriverCurrentInkType_ToEmitterCreation()
        {
#if UNITY_EDITOR
            string path = System.IO.Path.Combine(Application.dataPath,
                "_Project/Scripts/Systems/Brush/BrushInputController.cs");
            Assume.That(System.IO.File.Exists(path), "source file not found: " + path);
            string src = System.IO.File.ReadAllText(path);
            StringAssert.Contains("emitters.ApplyDragGesture(rmbStartUv, endUv,", src,
                "RMB release must pass a selected ink to the emitter controller");
            StringAssert.Contains("simDriver != null ? simDriver.CurrentInkType : 0", src,
                "…sourced from SimDriver.CurrentInkType, the same selection left-click painting reads");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        [Test]
        public void SegmentDistance_MeasuresToThePath_NotJustEndpoints()
        {
            // Point sits on the midpoint of the segment => distance 0, even though it is 0.5 from each end.
            float d = DirectionalEmitterController.SegmentDistance(
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f));
            Assert.That(d, Is.EqualTo(0f).Within(1e-5f));

            // Perpendicular offset is measured correctly.
            float d2 = DirectionalEmitterController.SegmentDistance(
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0.25f));
            Assert.That(d2, Is.EqualTo(0.25f).Within(1e-5f));

            // Beyond the end, it clamps to the endpoint rather than extending the infinite line.
            float d3 = DirectionalEmitterController.SegmentDistance(
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(2f, 0f));
            Assert.That(d3, Is.EqualTo(1f).Within(1e-5f));
        }
    }
}
