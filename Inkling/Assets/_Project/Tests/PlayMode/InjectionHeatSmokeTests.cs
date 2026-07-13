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
    /// Runs on a throwaway GameObject — the active scene is never touched.
    /// </summary>
    public class InjectionHeatSmokeTests
    {
#if UNITY_EDITOR
        private const int Res = 32;
        private const float Neutral = 0.5f, MinTemp = 0f, MaxTemp = 1f;
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
            SetPrivate(driver, "forceRadius", 3f);              // default 56 would blanket a 32px grid
            SetPrivate(driver, "simulationUpdateRate", 60);     // int; keeps LateUpdate from driving display
            SetPrivate(driver, "neutralTemperature", Neutral);
            SetPrivate(driver, "minTemperature", MinTemp);
            SetPrivate(driver, "maxHeat", MaxTemp);

            // Isolate the stamp: no continuous sources, no phase changes, no conduction — so whatever we
            // read at a texel is the stamp itself, not something the solver did afterwards.
            SetPrivate(driver, "enableHeatSources", false);
            SetPrivate(driver, "enableThermalInteractions", false);
            SetPrivate(driver, "thermalDiffusion", 0f);

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

            // Three well-separated injections (radius 3, separations >> 3 so the stamps cannot overlap).
            const int ix = 6, iy = 6;      // Ice   -> min
            const int fx = 25, fy = 25;    // Fire  -> max
            const int px = 6, py = 25;     // PlantSeeded -> not a thermal ink: heat must be untouched

            driver.InjectDensity(Uv(ix, iy), Color.white, (int)InkTypeId.Ice);
            driver.InjectDensity(Uv(fx, fy), Color.white, (int)InkTypeId.Fire);
            driver.InjectDensity(Uv(px, py), Color.white, (int)InkTypeId.PlantSeeded);

            driver.StepSimulation();
            yield return null;

            float[] heat = ReadHeat(ctx);

            AssertStamped(heat, ix, iy, MinTemp,
                $"[{(batched ? "batched" : "fallback")}] Ice injection must stamp the MINIMUM temperature");
            Assert.That(heat[iy * Res + ix], Is.LessThan(Neutral - Tol),
                "…and it must be genuinely SUB-NEUTRAL — this is the behaviour the user reported missing");

            AssertStamped(heat, fx, fy, MaxTemp,
                $"[{(batched ? "batched" : "fallback")}] Fire injection must stamp the MAXIMUM temperature");
            Assert.That(heat[fy * Res + fx], Is.GreaterThan(Neutral + Tol), "…and be genuinely above neutral");

            AssertStamped(heat, px, py, Neutral,
                $"[{(batched ? "batched" : "fallback")}] A non-thermal ink must leave the heat field untouched");
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
    }
}
