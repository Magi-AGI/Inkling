using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Magi.Inkling.Systems.SimulationLOD0;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.PlayMode
{
    /// <summary>
    /// CP8c: end-to-end smoke for injection heat stamping through the REAL runtime path:
    ///
    ///   SimDriver.InjectDensity -> OperationQueue.EnqueueDensityInjection -> ProcessPending
    ///     -> StampInjectionHeat -> ctx.Heat.Read
    ///
    /// CP8b GPU-verified the kernel and source-asserted the queue wiring, but never proved their
    /// COMPOSITION. This closes that gap: it drives a live SimDriver and reads the heat RT back.
    ///
    /// Typed mapping under test: Fire = max, Steam = hot (between water and fire, CP8f),
    /// Water = neutral, Ice = min. Every other VALID ink stamps NEUTRAL (room temperature) — CP8k made
    /// injection stamp a temperature for all inks; only an out-of-range index leaves the heat field alone.
    ///
    /// Runs on a throwaway GameObject — the active scene is never touched.
    /// </summary>
    public class InjectionHeatSmokeTests
    {
#if UNITY_EDITOR
        private const int Res = 32;
        private const float Neutral = 0.5f, MinTemp = 0f, MaxTemp = 1f;
        private const float SteamTemp = 0.75f;   // CP8f: hot, between Water (neutral) and Fire (max)
        private const float Tol = 6e-2f;

        // UVs chosen to land EXACTLY on a texel centre (pixel i centre is at i+0.5), so the injection's
        // gaussian falloff is 1.0 there and the stamp hits its target exactly rather than a blend.
        private static Vector2 Uv(int px, int py) => new Vector2((px + 0.5f) / Res, (py + 0.5f) / Res);

        private GameObject go;

        [TearDown]
        public void TearDown()
        {
            if (go != null) Object.DestroyImmediate(go);
            go = null;
        }

        private static void SetPrivate(object target, string field, object value)
        {
            FieldInfo f = typeof(SimDriver).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(f, $"SimDriver private field '{field}' not found — did it get renamed?");
            f.SetValue(target, value);
        }

        private static SimulationContext ContextOf(SimDriver driver)
        {
            FieldInfo f = typeof(SimDriver).GetField("ctx", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(f, "SimDriver private field 'ctx' not found");
            var ctx = (SimulationContext)f.GetValue(driver);
            Assert.IsNotNull(ctx, "SimDriver.ctx is null — Start() did not run?");
            return ctx;
        }

        /// <summary>Reads the .r channel of every cell of the heat RT (index y*Res + x).</summary>
        private static float[] ReadHeat(SimulationContext ctx)
        {
            RenderTexture rt = ctx.Heat.Read;
            var prev = RenderTexture.active;
            var tex = new Texture2D(Res, Res, TextureFormat.RGBAFloat, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Res, Res), 0, 0);
                tex.Apply();
                var outp = new float[Res * Res];
                for (int y = 0; y < Res; y++)
                    for (int x = 0; x < Res; x++)
                        outp[y * Res + x] = tex.GetPixel(x, y).r;
                return outp;
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
            }
        }

        /// <summary>
        /// Creates a fresh SimDriver configured for a deterministic, isolated heat read, lets Start()
        /// run, then drains and resets so the startup injections cannot contaminate the assertions.
        /// </summary>
        private IEnumerator CreateDriver(bool batched, System.Action<SimDriver> onReady)
        {
            go = new GameObject("CP8c_SimDriver");
            var driver = go.AddComponent<SimDriver>();

            // Configure BEFORE Start() runs (it fires at the end of this frame).
            SetPrivate(driver, "resolution", Res);
            // CP9c: radius 6, not 3. Injection compares a texel CORNER (pos = id.xy) to the injection's
            // texel-CENTRE position (uv*res = n+0.5), so the sampled cell sits ~0.707px off the Gaussian
            // peak. At radius 3 that attenuates the stamp to ~0.80 (fire read 0.899, not 1.0); radius 6
            // gives falloff ~0.95 so the read lands within tolerance of the target. Still << the 19px
            // separation between the four quadrant injections, so their stamps cannot overlap. (In
            // production forceRadius is ~56, where the half-texel offset is negligible — this only bit the
            // unrealistically small unit-test radius.)
            SetPrivate(driver, "forceRadius", 6f);
            SetPrivate(driver, "simulationUpdateRate", 60);     // int; keeps LateUpdate from driving display
            SetPrivate(driver, "neutralTemperature", Neutral);
            SetPrivate(driver, "minTemperature", MinTemp);
            SetPrivate(driver, "maxHeat", MaxTemp);
            SetPrivate(driver, "steamInjectionTemperature", SteamTemp);

            // Isolate the stamp: no continuous sources, no phase changes, no conduction — so whatever we
            // read at a texel is the stamp itself, not something the solver did afterwards.
            SetPrivate(driver, "enableHeatSources", false);
            SetPrivate(driver, "enableThermalInteractions", false);
            SetPrivate(driver, "thermalDiffusion", 0f);
            // CP9c: thermalDiffusion=0 only disables FLUID conduction. An injected ICE cell becomes a
            // thermal-SOLID (obstacle), which DiffuseHeat conducts at thermalDiffusionSolid — 60 by default
            // (CP8ab). Left on, it warmed the stamped ice cell back toward its neutral surroundings (ice
            // read 0.172 instead of the floor). Zero it too so the stamp is genuinely isolated for both
            // fluid and solid cells.
            SetPrivate(driver, "thermalDiffusionSolid", 0f);

            SetPrivate(driver, "useBatchedDensityInjection", batched);
            if (batched)
            {
                var batchedCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Assets/_Project/Scripts/Systems/SimulationLOD0/BatchedInjection.compute");
                Assert.IsNotNull(batchedCompute, "BatchedInjection.compute should load");
                SetPrivate(driver, "batchedInjectionCompute", batchedCompute);
            }

            driver.ExternalStepControl = true;   // we drive stepping ourselves

            yield return null;                   // Start() runs: allocate, ClearAll, queue 3 startup injections

            SimulationContext ctx = ContextOf(driver);
            Assert.IsNotNull(ctx.FluidCompute, "SimDriver could not resolve Fluids.compute");
            Assert.IsNotNull(ctx.Heat, "Heat layer was not allocated");

            // Start() enqueues three Fire injections (ink 0), which now stamp MAX heat. Drain them
            // FIRST, then reset — resetting first would leave them pending in the queue and they would
            // fire into our measurement step.
            driver.StepSimulation();
            driver.ResetSimulation();            // ClearAll -> heat field back to neutral everywhere
            yield return null;

            float[] baseline = ReadHeat(ctx);
            Assert.That(baseline[0], Is.EqualTo(Neutral).Within(Tol),
                "After reset the heat field must sit at the neutral baseline (CP8a)");

            onReady(driver);
        }

        private static void AssertStamped(float[] heat, int px, int py, float expected, string what)
        {
            Assert.That(heat[py * Res + px], Is.EqualTo(expected).Within(Tol), what);
        }

        private IEnumerator RunTypedInjectionSmoke(bool batched)
        {
            SimDriver driver = null;
            yield return CreateDriver(batched, d => driver = d);
            SimulationContext ctx = ContextOf(driver);

            // Four well-separated injections, one per quadrant (radius 6, separations >> 6 so the stamps
            // cannot overlap — an overlap would silently blend two targets and make the reads meaningless).
            const int ix = 6, iy = 6;      // Ice   -> min
            const int fx = 25, fy = 25;    // Fire  -> max
            const int sx = 25, sy = 6;     // Steam -> hot, between water and fire (CP8f)
            const int px = 6, py = 25;     // PlantSeeded -> no characteristic temperature: stamps NEUTRAL

            driver.InjectDensity(Uv(ix, iy), Color.white, (int)InkTypeId.Ice);
            driver.InjectDensity(Uv(fx, fy), Color.white, (int)InkTypeId.Fire);
            driver.InjectDensity(Uv(sx, sy), Color.white, (int)InkTypeId.Steam);
            driver.InjectDensity(Uv(px, py), Color.white, (int)InkTypeId.PlantSeeded);

            driver.StepSimulation();
            yield return null;

            float[] heat = ReadHeat(ctx);
            string path = batched ? "batched" : "fallback";

            AssertStamped(heat, ix, iy, MinTemp,
                $"[{path}] Ice injection must stamp the MINIMUM temperature");
            Assert.That(heat[iy * Res + ix], Is.LessThan(Neutral - Tol),
                "…and it must be genuinely SUB-NEUTRAL — this is the behaviour the user reported missing");

            AssertStamped(heat, fx, fy, MaxTemp,
                $"[{path}] Fire injection must stamp the MAXIMUM temperature");
            Assert.That(heat[fy * Res + fx], Is.GreaterThan(Neutral + Tol), "…and be genuinely above neutral");

            // CP8f: Steam is born HOT — but strictly BETWEEN water and fire. The two-sided bound is the
            // real assertion: equal to neutral would mean the typed stamp never ran, and equal to max
            // would mean steam is indistinguishable from fire.
            AssertStamped(heat, sx, sy, SteamTemp,
                $"[{path}] Steam injection must stamp its hot default temperature");
            Assert.That(heat[sy * Res + sx], Is.GreaterThan(Neutral + Tol),
                "…genuinely hotter than water/room temperature");
            Assert.That(heat[sy * Res + sx], Is.LessThan(MaxTemp - Tol),
                "…but genuinely cooler than fire");

            // CP8k: a non-thermal ink stamps NEUTRAL (it no longer leaves heat untouched). Against a
            // neutral background those two are indistinguishable, so this assertion alone is weak —
            // InjectDensity_Plant_ResetsColdCellToNeutral below is the one that actually proves it.
            AssertStamped(heat, px, py, Neutral,
                $"[{path}] A non-thermal ink (PlantSeeded) must arrive at room temperature");
        }
#endif

        // The DEFAULT runtime path (batched density injection).
        [UnityTest]
        public IEnumerator InjectDensity_BatchedPath_StampsTypedTemperatures()
        {
#if UNITY_EDITOR
            yield return RunTypedInjectionSmoke(batched: true);
#else
            yield break;
#endif
        }

        // The fallback path (batched injection disabled) — CP8b wires the stamp into BOTH branches.
        [UnityTest]
        public IEnumerator InjectDensity_FallbackPath_StampsTypedTemperatures()
        {
#if UNITY_EDITOR
            yield return RunTypedInjectionSmoke(batched: false);
#else
            yield break;
#endif
        }

        // CP8k. Lake: "All inks (including plant) should be a neutral temperature except for fire, ice,
        // and steam." Plant used to leave the heat field UNTOUCHED, which quietly made painting plant a
        // way to PRESERVE stale cold — paint plant over a frozen patch and it stayed frozen, so the ink
        // and the temperature drifted apart. Freeze the cell with Ice first, then prove Plant actively
        // pulls it back up to room temperature. Asserting this against a neutral background would be
        // vacuous (untouched and neutral-stamped look identical), which is the whole point of the setup.
        [UnityTest]
        public IEnumerator InjectDensity_Plant_ResetsColdCellToNeutral()
        {
#if UNITY_EDITOR
            SimDriver driver = null;
            yield return CreateDriver(batched: true, d => driver = d);
            SimulationContext ctx = ContextOf(driver);

            const int cx = 16, cy = 16;

            // 1. Ice -> the cell becomes genuinely cold (the floor).
            driver.InjectDensity(Uv(cx, cy), Color.white, (int)InkTypeId.Ice);
            driver.StepSimulation();
            yield return null;

            float cold = ReadHeat(ctx)[cy * Res + cx];
            Assert.That(cold, Is.EqualTo(MinTemp).Within(Tol), "Ice should have chilled the cell to the floor");

            // 2. Plant over the SAME cell -> it must be pulled back up to room temperature.
            driver.InjectDensity(Uv(cx, cy), Color.white, (int)InkTypeId.PlantSeeded);
            driver.StepSimulation();
            yield return null;

            float afterPlant = ReadHeat(ctx)[cy * Res + cx];
            Assert.That(afterPlant, Is.EqualTo(Neutral).Within(Tol),
                "Plant must stamp the NEUTRAL baseline, actively warming a frozen cell back to room temperature");
            Assert.That(afterPlant, Is.GreaterThan(cold + Tol), "…i.e. it genuinely moved off the cold value");
#else
            yield break;
#endif
        }

        // Water stamps the NEUTRAL baseline. Asserting that against a neutral background would be
        // vacuous, so heat the cell with Fire first and prove Water actively pulls it back to neutral.
        [UnityTest]
        public IEnumerator InjectDensity_Water_ResetsHotCellToNeutral()
        {
#if UNITY_EDITOR
            SimDriver driver = null;
            yield return CreateDriver(batched: true, d => driver = d);
            SimulationContext ctx = ContextOf(driver);

            const int wx = 16, wy = 16;

            // 1. Fire -> the cell becomes hot.
            driver.InjectDensity(Uv(wx, wy), Color.white, (int)InkTypeId.Fire);
            driver.StepSimulation();
            yield return null;

            float hot = ReadHeat(ctx)[wy * Res + wx];
            Assert.That(hot, Is.EqualTo(MaxTemp).Within(Tol), "Fire should have heated the cell to max");

            // 2. Water over the SAME cell -> it must be pulled back down to the neutral baseline.
            driver.InjectDensity(Uv(wx, wy), Color.white, (int)InkTypeId.Water);
            driver.StepSimulation();
            yield return null;

            float afterWater = ReadHeat(ctx)[wy * Res + wx];
            Assert.That(afterWater, Is.EqualTo(Neutral).Within(Tol),
                "Water injection must stamp the NEUTRAL baseline, actively cooling a hot cell");
            Assert.That(afterWater, Is.LessThan(hot - Tol), "…i.e. it genuinely moved off the hot value");
#else
            yield break;
#endif
        }

        // ── CP8w: ColdAir, the temperature-only probe ───────────────────────────────────────────────
        //
        // Lake: "since ice is the only way to lower the temperature, we can't determine whether water
        // will freeze on its own. Let's make a new ink for cold air ... without inserting ice."
        //
        // The whole value of ColdAir rests on it being cold WITHOUT being ice. Testing only that it
        // chills would be near-vacuous — injecting Ice chills too, and would pass such a test while
        // completely defeating the purpose. So the mass assertion below is the load-bearing one.

        [UnityTest]
        public IEnumerator ColdAir_ChillsTheCell_ToTheFloor()
        {
#if UNITY_EDITOR
            SimDriver driver = null;
            yield return CreateDriver(batched: true, d => driver = d);
            SimulationContext ctx = ContextOf(driver);

            const int cx = 10, cy = 20;

            // Warm the cell with Fire first, so "cold" is a measured MOVE rather than the background.
            driver.InjectDensity(Uv(cx, cy), Color.white, (int)InkTypeId.Fire);
            driver.StepSimulation();
            yield return null;
            float hot = ReadHeat(ctx)[cy * Res + cx];
            Assume.That(hot, Is.EqualTo(MaxTemp).Within(Tol), "precondition: Fire heated the cell");

            driver.InjectDensity(Uv(cx, cy), Color.white, SimulationContext.ColdSourceInkIndex);
            driver.StepSimulation();
            yield return null;

            float cold = ReadHeat(ctx)[cy * Res + cx];
            Assert.That(cold, Is.EqualTo(MinTemp).Within(Tol),
                "ColdAir must drive the cell to the temperature floor");
            Assert.That(cold, Is.LessThan(Neutral - Tol),
                "…genuinely sub-neutral, which is the whole point: a cold source that is not Ice");
#else
            yield break;
#endif
        }

        /// <summary>
        /// THE test for CP8w. ColdAir must lower heat while adding NO ice mass — otherwise it is just
        /// Ice with extra steps, and Lake still could not tell whether water froze on its own.
        /// Also guards the specific failure mode in SimDriver.InjectDensity: without the ColdAir intercept,
        /// the injection clamp (0..Count-1) would silently pin ColdAir (index == InkTypeId.Count) to the last
        /// real ink (Metal, index 10). That bug would pass a heat-only assertion.
        /// </summary>
        [UnityTest]
        public IEnumerator ColdAir_AddsNoIceMass_AndNoOtherInk()
        {
#if UNITY_EDITOR
            SimDriver driver = null;
            yield return CreateDriver(batched: true, d => driver = d);
            SimulationContext ctx = ContextOf(driver);

            const int cx = 12, cy = 12;

            float[] before = ReadInkChannels(driver, cx, cy);

            driver.InjectDensity(Uv(cx, cy), Color.white, SimulationContext.ColdSourceInkIndex);
            driver.StepSimulation();
            yield return null;

            // It really did cool — so the injection was not simply dropped on the floor.
            float cold = ReadHeat(ctx)[cy * Res + cx];
            Assume.That(cold, Is.LessThan(Neutral - Tol), "precondition: ColdAir actually cooled the cell");

            float[] after = ReadInkChannels(driver, cx, cy);

            Assert.That(after[(int)InkTypeId.Ice], Is.EqualTo(before[(int)InkTypeId.Ice]).Within(1e-5f),
                "ColdAir must NOT add ice mass — if this fails, ColdAir is being clamped to a real ink instead of heat-only");

            for (int i = 0; i < (int)InkTypeId.Count; i++)
                Assert.That(after[i], Is.EqualTo(before[i]).Within(1e-5f),
                    $"ColdAir must add no mass to ANY channel; channel {(InkTypeId)i} changed");
#else
            yield break;
#endif
        }

        /// <summary>
        /// Selection plumbing: CurrentInkType must accept ColdAir. Before CP8w the setter clamped to
        /// 0..Count-1, which would have silently pinned any ColdAir selection to the last real ink (Metal).
        /// </summary>
        [Test]
        public void CurrentInkType_AcceptsColdAir_AndStillClampsAbove()
        {
#if UNITY_EDITOR
            var host = new GameObject("CP8w_Selection");
            try
            {
                var driver = host.AddComponent<SimDriver>();

                driver.CurrentInkType = SimulationContext.ColdSourceInkIndex;
                Assert.AreEqual(SimulationContext.ColdSourceInkIndex, driver.CurrentInkType,
                    "ColdAir must be selectable, not clamped down to Ice");

                driver.CurrentInkType = 99;
                Assert.AreEqual(SimulationContext.ColdSourceInkIndex, driver.CurrentInkType,
                    "out-of-range still clamps to the top of the valid range");

                driver.CurrentInkType = -5;
                Assert.AreEqual(0, driver.CurrentInkType, "negative still clamps to 0");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
#endif
        }

#if UNITY_EDITOR
        /// <summary>Reads all eleven ink mass channels (0..Count-1, incl. Metal) of the particle at (px, py).</summary>
        private static float[] ReadInkChannels(SimDriver driver, int px, int py)
        {
            var buffer = driver.GetParticleBuffer();
            Assert.IsNotNull(buffer, "particle buffer must exist to prove ColdAir adds no mass");

            var all = new iparticle[Res * Res];
            buffer.GetData(all);
            iparticle p = all[py * Res + px];

            // Explicit float[] so half-mode fields (Unity.Mathematics.half) convert implicitly to float per
            // element; in the default float mode this is identity. Avoids new[] inferring half[] (CS0029).
            return new float[]
            {
                p.fire, p.water, p.plantSeeded, p.plantGrown, p.steam,
                p.glitter, p.blackBody, p.electricitySeeded, p.electricityGrown, p.ice,
                p.metal
            };
        }
#endif
    }
}
