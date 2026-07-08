using System.Collections;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.PlayMode
{
    /// <summary>
    /// CP5 tests for the LOCAL heat-driven phase-change pass (ThermalInteractions.compute):
    /// disabled pass-through, melt/boil/condense gating + conservation, per-threshold budget,
    /// the Codex same-pass boil→condense hazard, cascade, heat/ink caps, local-only, and clamps.
    /// </summary>
    public class ThermalInteractionsTests
    {
#if UNITY_EDITOR
        // Tunables with the shader's default values; individual tests override as needed.
        private class TP
        {
            public int enable = 1;
            public float dt = 1f;
            public float condenseT = 0.2f, meltT = 0.4f, boilT = 0.7f;
            public float meltRate = 1f, boilRate = 1f, condenseRate = 1f;
            public float meltCost = 0.5f, boilCost = 0.5f, condenseRelease = 0f;
            public float ambient = 0f, maxHeat = 1f;
            // CP7b fuel-like fire. Sources default OFF so all pre-CP7b phase tests are unaffected.
            public int enableHeatSources = 0;
            public float fireEmissionRate = 1f;
            public float fireFuelCost = 0f;
        }

        private static RenderTexture MakeHeatRT(int res, float[] seed)
        {
            var rt = new RenderTexture(res, res, 0, RenderTextureFormat.RHalf) { enableRandomWrite = true };
            rt.Create();
            var t = new Texture2D(res, res, TextureFormat.RGBAFloat, false);
            try
            {
                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                        t.SetPixel(x, y, new Color(seed[y * res + x], 0f, 0f, 0f));
                t.Apply();
                Graphics.Blit(t, rt);
            }
            finally { Object.DestroyImmediate(t); }
            return rt;
        }

        private static float[] ReadHeatAll(RenderTexture rt, int res)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(res, res, TextureFormat.RGBAFloat, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
                tex.Apply();
                var outp = new float[res * res];
                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                        outp[y * res + x] = tex.GetPixel(x, y).r;
                return outp;
            }
            finally { RenderTexture.active = prev; Object.DestroyImmediate(tex); }
        }

        private static iparticle[] Run(iparticle[] particles, float[] heat, int res, TP tp, out float[] heatOut)
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/SimulationLOD0/ThermalInteractions.compute");
            Assert.IsNotNull(cs, "ThermalInteractions.compute should load");
            int kernel = cs.FindKernel("ThermalInteractions");

            int count = res * res;
            var readBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());
            var writeBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());
            var heatRead = MakeHeatRT(res, heat);
            var heatWrite = MakeHeatRT(res, new float[count]);
            try
            {
                readBuf.SetData(particles);
                writeBuf.SetData(particles);

                cs.SetInt("_Resolution", res);
                cs.SetFloat("_FrameDeltaTime", tp.dt);
                cs.SetInt("_EnableThermalInteractions", tp.enable);
                cs.SetFloat("_CondenseThreshold", tp.condenseT);
                cs.SetFloat("_MeltThreshold", tp.meltT);
                cs.SetFloat("_BoilThreshold", tp.boilT);
                cs.SetFloat("_MeltRate", tp.meltRate);
                cs.SetFloat("_BoilRate", tp.boilRate);
                cs.SetFloat("_CondenseRate", tp.condenseRate);
                cs.SetFloat("_MeltHeatCost", tp.meltCost);
                cs.SetFloat("_BoilHeatCost", tp.boilCost);
                cs.SetFloat("_CondenseHeatRelease", tp.condenseRelease);
                cs.SetFloat("_AmbientTemperature", tp.ambient);
                cs.SetFloat("_MaxHeat", tp.maxHeat);
                cs.SetInt("_EnableHeatSources", tp.enableHeatSources);
                cs.SetFloat("_FireHeatEmissionRate", tp.fireEmissionRate);
                cs.SetFloat("_FireHeatFuelCost", tp.fireFuelCost);

                cs.SetBuffer(kernel, "_ParticlesRead", readBuf);
                cs.SetBuffer(kernel, "_ParticlesWrite", writeBuf);
                cs.SetTexture(kernel, "_HeatRead", heatRead);
                cs.SetTexture(kernel, "_HeatWrite", heatWrite);

                int g = Mathf.CeilToInt(res / 8f);
                cs.Dispatch(kernel, g, g, 1);

                var outData = new iparticle[count];
                writeBuf.GetData(outData);
                heatOut = ReadHeatAll(heatWrite, res);
                return outData;
            }
            finally
            {
                readBuf.Release();
                writeBuf.Release();
                heatRead.Release();
                heatWrite.Release();
                Object.DestroyImmediate(heatRead);
                Object.DestroyImmediate(heatWrite);
            }
        }
#endif

        // 1. Disabled => exact pass-through of every field and heat.
        [UnityTest]
        public IEnumerator Disabled_PassesThroughAllFieldsAndHeat()
        {
#if UNITY_EDITOR
            var p = new iparticle[1];
            p[0].fire = 0.3f; p[0].ice = 0.5f; p[0].water = 0.2f; p[0].steam = 0.1f;
            p[0].red = 0.7f; p[0].blue = 0.9f; p[0].plantGrown = 0.15f;
            var heat = new[] { 0.5f };

            var outp = Run(p, heat, 1, new TP { enable = 0 }, out float[] heatOut);
            yield return null;

            Assert.That(outp[0].fire, Is.EqualTo(0.3f).Within(2e-2f));
            Assert.That(outp[0].ice, Is.EqualTo(0.5f).Within(2e-2f));
            Assert.That(outp[0].water, Is.EqualTo(0.2f).Within(2e-2f));
            Assert.That(outp[0].steam, Is.EqualTo(0.1f).Within(2e-2f));
            Assert.That(outp[0].red, Is.EqualTo(0.7f).Within(2e-2f));
            Assert.That(outp[0].blue, Is.EqualTo(0.9f).Within(2e-2f));
            Assert.That(outp[0].plantGrown, Is.EqualTo(0.15f).Within(2e-2f));
            Assert.That(heatOut[0], Is.EqualTo(0.5f).Within(2e-2f), "Heat must pass through unchanged when disabled");
#else
            yield break;
#endif
        }

        // 2. Melt only above threshold: consumes ice+heat, produces water, ice+water conserved.
        [UnityTest]
        public IEnumerator Melt_ConsumesIceAndHeat_ProducesWater_Conserves()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].ice = 1f;
            var outp = Run(p, new[] { 0.6f }, 1, new TP(), out float[] heatOut);   // between melt(0.4) and boil(0.7)
            yield return null;

            Assert.That(outp[0].water, Is.GreaterThan(0f), "Water produced from melt");
            Assert.That(outp[0].ice, Is.LessThan(1f), "Ice consumed");
            Assert.That(outp[0].steam, Is.EqualTo(0f).Within(1e-3f), "No boil in the melt band");
            Assert.That(outp[0].ice + outp[0].water, Is.EqualTo(1f).Within(3e-2f), "ice+water conserved");
            Assert.That(heatOut[0], Is.LessThan(0.6f), "Heat consumed by melting");

            // No melt at/below threshold.
            var p2 = new iparticle[1]; p2[0].ice = 1f;
            var out2 = Run(p2, new[] { 0.4f }, 1, new TP(), out _);
            Assert.That(out2[0].water, Is.EqualTo(0f).Within(1e-3f), "No melt at threshold");
            Assert.That(out2[0].ice, Is.EqualTo(1f).Within(2e-2f), "Ice untouched at threshold");

            // No melt without ice.
            var p3 = new iparticle[1];
            var out3 = Run(p3, new[] { 1f }, 1, new TP(), out _);
            Assert.That(out3[0].water, Is.EqualTo(0f).Within(1e-3f), "No water without ice");
#else
            yield break;
#endif
        }

        // 3. Boil only above threshold: consumes water+heat, produces steam, water+steam conserved.
        [UnityTest]
        public IEnumerator Boil_ConsumesWaterAndHeat_ProducesSteam_Conserves()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].water = 1f;
            var outp = Run(p, new[] { 1f }, 1, new TP(), out float[] heatOut);   // above boil(0.7)
            yield return null;

            Assert.That(outp[0].steam, Is.GreaterThan(0f), "Steam produced from boil");
            Assert.That(outp[0].water, Is.LessThan(1f), "Water consumed");
            Assert.That(outp[0].water + outp[0].steam, Is.EqualTo(1f).Within(3e-2f), "water+steam conserved");
            Assert.That(heatOut[0], Is.LessThan(1f), "Heat consumed by boiling");
#else
            yield break;
#endif
        }

        // 4. Per-threshold budget: heat between melt and boil melts but does NOT boil.
        [UnityTest]
        public IEnumerator PerThresholdBudget_MeltsButDoesNotBoil()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].ice = 1f; p[0].water = 1f;
            var outp = Run(p, new[] { 0.5f }, 1, new TP(), out _);   // melt(0.4) < 0.5 < boil(0.7)
            yield return null;

            Assert.That(outp[0].water, Is.GreaterThan(1f), "Water increased (melt added to existing water)");
            Assert.That(outp[0].ice, Is.LessThan(1f), "Ice consumed by melt");
            Assert.That(outp[0].steam, Is.EqualTo(0f).Within(1e-3f), "No boil: heat is below the boil threshold");
#else
            yield break;
#endif
        }

        // 5. Condense only when pre-reaction heat < condenseThreshold: steam -> water, conserved.
        [UnityTest]
        public IEnumerator Condense_ColdSteamBecomesWater_WarmDoesNot()
        {
#if UNITY_EDITOR
            var cold = new iparticle[1]; cold[0].steam = 1f;
            var outCold = Run(cold, new[] { 0.1f }, 1, new TP(), out _);   // below condense(0.2)
            yield return null;
            Assert.That(outCold[0].water, Is.GreaterThan(0f), "Cold steam condenses to water");
            Assert.That(outCold[0].steam, Is.LessThan(1f), "Steam consumed");
            Assert.That(outCold[0].steam + outCold[0].water, Is.EqualTo(1f).Within(3e-2f), "steam+water conserved");

            var warm = new iparticle[1]; warm[0].steam = 1f;
            var outWarm = Run(warm, new[] { 0.5f }, 1, new TP(), out _);   // above condense
            Assert.That(outWarm[0].steam, Is.EqualTo(1f).Within(2e-2f), "Warm steam does not condense");
#else
            yield break;
#endif
        }

        // 6. Codex hazard: boiling lowers running heat below condenseThreshold, but the newly-boiled
        // steam must NOT condense this pass (condensation ran first on steam0 == 0).
        [UnityTest]
        public IEnumerator BoilDoesNotTriggerSamePassCondensation()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].water = 1f; // steam0 == 0
            // condenseT high (0.5) so post-boil heat (0.4) sits inside the condense range;
            // boilT low (0.4) so all water boils and drops heat to 0.4.
            var tp = new TP { condenseT = 0.5f, meltT = 0.5f, boilT = 0.4f };
            var outp = Run(p, new[] { 0.9f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(outp[0].steam, Is.EqualTo(1f).Within(4e-2f),
                "Newly-boiled steam must remain steam (condensation ran first on steam0=0).");
            Assert.That(outp[0].water, Is.EqualTo(0f).Within(4e-2f), "All water boiled away");
            Assert.That(heatOut[0], Is.LessThan(0.5f),
                "Sanity: final heat is below condenseThreshold, yet no condensation occurred.");
#else
            yield break;
#endif
        }

        // 7. Cascade: a very hot ice cell melts then boils in one dispatch when heat stays above boil.
        [UnityTest]
        public IEnumerator Cascade_HotIceMeltsThenBoils()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].ice = 1f;
            var tp = new TP { maxHeat = 3f };
            var outp = Run(p, new[] { 3f }, 1, tp, out _);
            yield return null;

            Assert.That(outp[0].ice, Is.EqualTo(0f).Within(4e-2f), "Ice fully melted");
            Assert.That(outp[0].steam, Is.GreaterThan(0.5f), "Cascade produced steam in the same dispatch");
            Assert.That(outp[0].ice + outp[0].water + outp[0].steam, Is.EqualTo(1f).Within(5e-2f),
                "Total ice+water+steam mass conserved through the cascade");
#else
            yield break;
#endif
        }

        // 8. Conversion is capped by the heat budget AND by available ink.
        [UnityTest]
        public IEnumerator Conversion_CappedByHeatBudget_AndByInk()
        {
#if UNITY_EDITOR
            // Heat-capped: big rate, tiny excess (0.1 over melt), cost 1 => melt <= 0.1.
            var pHeat = new iparticle[1]; pHeat[0].ice = 1f;
            var outHeat = Run(pHeat, new[] { 0.5f }, 1,
                new TP { meltRate = 10f, meltCost = 1f }, out _);
            yield return null;
            Assert.That(outHeat[0].water, Is.EqualTo(0.1f).Within(3e-2f), "Melt capped by heat budget (excess/cost)");

            // Ink-capped: tiny ice, huge excess/rate => melt <= ice.
            var pInk = new iparticle[1]; pInk[0].ice = 0.05f;
            var outInk = Run(pInk, new[] { 1f }, 1,
                new TP { meltRate = 10f, meltCost = 0.1f }, out _);
            Assert.That(outInk[0].water, Is.EqualTo(0.05f).Within(2e-2f), "Melt capped by available ice");
            Assert.That(outInk[0].ice, Is.EqualTo(0f).Within(1e-2f), "Ice fully consumed");
#else
            yield break;
#endif
        }

        // 9. Local-only: a cell with no local ice/heat is unchanged even when neighbors are hot/icy.
        [UnityTest]
        public IEnumerator LocalOnly_NeighborsDoNotCauseConversion()
        {
#if UNITY_EDITOR
            const int res = 3;
            var p = new iparticle[res * res];
            p[1 * res + 0].ice = 1f;   // (0,1) hot ice neighbor
            var heat = new float[res * res];
            heat[1 * res + 0] = 1f;    // heat only at the neighbor
            // center (1,1) has no ice, no water, no heat.

            var outp = Run(p, heat, res, new TP(), out float[] heatOut);
            yield return null;

            int C = 1 * res + 1;
            Assert.That(outp[C].ice, Is.EqualTo(0f).Within(1e-3f), "Center gains no ice");
            Assert.That(outp[C].water, Is.EqualTo(0f).Within(1e-3f), "Center produces no water (no neighbor sampling)");
            Assert.That(outp[C].steam, Is.EqualTo(0f).Within(1e-3f), "Center produces no steam");
            Assert.That(heatOut[C], Is.EqualTo(0f).Within(1e-3f), "Center heat unchanged");
#else
            yield break;
#endif
        }

        // 9b. Local-only (stronger): the center has LOCAL ice but ZERO local heat, while a neighbor is
        // hot. Neighbor heat must not drive the center's melt — the center's own heat gates conversion.
        [UnityTest]
        public IEnumerator LocalOnly_NeighborHeatDoesNotMeltLocalIce()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;   // center: local ice, no local heat
            int N = 1 * res + 0;   // (0,1) hot neighbor

            var p = new iparticle[res * res];
            p[C].ice = 1f;         // center HAS ice
            var heat = new float[res * res];
            heat[N] = 1f;          // heat only at the neighbor; center heat = 0

            var outp = Run(p, heat, res, new TP(), out float[] heatOut);
            yield return null;

            Assert.That(outp[C].ice, Is.EqualTo(1f).Within(2e-2f), "Center ice must not melt (no LOCAL heat)");
            Assert.That(outp[C].water, Is.EqualTo(0f).Within(1e-3f), "No water without local heat");
            Assert.That(outp[C].steam, Is.EqualTo(0f).Within(1e-3f), "No steam without local heat");
            Assert.That(heatOut[C], Is.EqualTo(0f).Within(1e-3f), "Center heat stays 0 (no neighbor heat leak)");
#else
            yield break;
#endif
        }

        // 10. Clamp: latent-heat release cannot push heat past _MaxHeat.
        [UnityTest]
        public IEnumerator Clamp_HeatNeverExceedsMaxHeat()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].steam = 1f;
            // Cold steam condenses; a huge (test-only) release would blow past maxHeat without the clamp.
            var tp = new TP { condenseRelease = 100f, maxHeat = 1f };
            var outp = Run(p, new[] { 0.1f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(heatOut[0], Is.LessThanOrEqualTo(1f + 2e-2f), "Heat clamped to _MaxHeat despite large release");
            Assert.That(outp[0].steam, Is.GreaterThanOrEqualTo(0f), "Steam never negative");
            Assert.That(outp[0].water, Is.GreaterThanOrEqualTo(0f), "Water never negative");
#else
            yield break;
#endif
        }

        // ── CP7b: fuel-like fire (emission step inside ThermalInteractions) ─────────────────
        // All fuel tests keep ice/water/steam at zero so only fire + heat are observed.

        // 11. fuelCost = 0 => fire emits heat but is NOT consumed (add-only, pre-CP7b semantics).
        [UnityTest]
        public IEnumerator Fuel_ZeroCost_EmitsHeat_FireUnchanged()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].fire = 0.5f;
            var tp = new TP { enableHeatSources = 1, fireEmissionRate = 1f, fireFuelCost = 0f };
            var outp = Run(p, new[] { 0f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(heatOut[0], Is.EqualTo(0.5f).Within(2e-2f), "fire*rate*dt = 0.5 heat added");
            Assert.That(outp[0].fire, Is.EqualTo(0.5f).Within(2e-2f), "fuelCost=0 must not consume fire");
#else
            yield break;
#endif
        }

        // 12. Headroom-limited burn: heat clamps at _MaxHeat and fire is consumed only for the heat
        // ACTUALLY added (0.2 * cost 2 = 0.4), never for the raw emission.
        [UnityTest]
        public IEnumerator Fuel_HeadroomLimited_ConsumesOnlyForHeatAdded()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].fire = 1f;
            var tp = new TP { enableHeatSources = 1, fireEmissionRate = 100f, fireFuelCost = 2f, maxHeat = 1f };
            var outp = Run(p, new[] { 0.8f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(heatOut[0], Is.EqualTo(1f).Within(2e-2f), "Heat fills the 0.2 headroom to _MaxHeat");
            Assert.That(outp[0].fire, Is.EqualTo(0.6f).Within(3e-2f),
                "Only 0.2 heat was added, so only 0.2*2 = 0.4 fire is burned (no energy mint)");
#else
            yield break;
#endif
        }

        // 13. Fuel-limited burn: heat added is capped by fire/fuelCost; fire hits 0, never negative.
        [UnityTest]
        public IEnumerator Fuel_FuelLimited_CapsHeat_FireNotNegative()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].fire = 0.1f;
            var tp = new TP { enableHeatSources = 1, fireEmissionRate = 100f, fireFuelCost = 5f, maxHeat = 3f };
            var outp = Run(p, new[] { 0f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(heatOut[0], Is.EqualTo(0.02f).Within(5e-3f), "Heat capped by fire/fuelCost = 0.1/5 = 0.02");
            Assert.That(outp[0].fire, Is.EqualTo(0f).Within(5e-3f), "Fire fully burned");
            Assert.That(outp[0].fire, Is.GreaterThanOrEqualTo(0f), "Fire never negative");
#else
            yield break;
#endif
        }

        // 14. At _MaxHeat: zero headroom => no heat added AND no fire consumed.
        [UnityTest]
        public IEnumerator Fuel_AtMaxHeat_NoHeatAdded_NoFireConsumed()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].fire = 1f;
            var tp = new TP { enableHeatSources = 1, fireEmissionRate = 10f, fireFuelCost = 2f, maxHeat = 1f };
            var outp = Run(p, new[] { 1f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(heatOut[0], Is.EqualTo(1f).Within(2e-2f), "Heat stays at _MaxHeat");
            Assert.That(outp[0].fire, Is.EqualTo(1f).Within(2e-2f), "No headroom => no fuel burned");
#else
            yield break;
#endif
        }

        // 15. Heat sources disabled => no emission, no fuel burn, even with fire and fuelCost set.
        [UnityTest]
        public IEnumerator Fuel_SourcesDisabled_NoEmission_NoBurn()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].fire = 1f;
            var tp = new TP { enableHeatSources = 0, fireEmissionRate = 10f, fireFuelCost = 2f };
            var outp = Run(p, new[] { 0.3f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(heatOut[0], Is.EqualTo(0.3f).Within(2e-2f), "No heat added when sources disabled");
            Assert.That(outp[0].fire, Is.EqualTo(1f).Within(2e-2f), "No fire consumed when sources disabled");
#else
            yield break;
#endif
        }

        // 17. Robustness: an upstream numerical underflow (slightly negative fire) must not emit
        // negative heat, must not lower heat, and must clamp the output fire to 0.
        [UnityTest]
        public IEnumerator Fuel_NegativeFireUnderflow_NoNegativeEmission_ClampsToZero()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].fire = -0.01f;
            var tp = new TP { enableHeatSources = 1, fireEmissionRate = 100f, fireFuelCost = 2f, maxHeat = 1f };
            var outp = Run(p, new[] { 0.25f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(heatOut[0], Is.EqualTo(0.25f).Within(2e-2f),
                "Negative fire must not emit negative heat or lower the heat field");
            Assert.That(outp[0].fire, Is.GreaterThanOrEqualTo(0f), "Fire must never be negative on output");
            Assert.That(outp[0].fire, Is.EqualTo(0f).Within(1e-3f), "Underflowed fire is clamped to 0");
#else
            yield break;
#endif
        }

        // 16. Double-source guard: FluidSolver must not dispatch the InkTools AddHeatSources pass when
        // ThermalInteractions is enabled (which now owns fire->heat emission). Source assertion —
        // a full FluidSolver.Step() integration test would need a live sim + scene.
        [Test]
        public void FluidSolver_DoesNotDoubleSourceHeat_WhenThermalEnabled()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Scripts/Systems/SimulationLOD0/Core/FluidSolver.cs";
            string src = System.IO.File.ReadAllText(path);
            StringAssert.Contains("!ctx.EnableThermalInteractions", src,
                "AddHeatSources dispatch must be guarded by !ctx.EnableThermalInteractions to avoid double-sourcing heat");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }
    }
}
