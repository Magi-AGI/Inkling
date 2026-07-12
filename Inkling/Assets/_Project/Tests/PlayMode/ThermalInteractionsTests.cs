using System.Collections;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Magi.InkTools.Simulation;
using Magi.Inkling.Systems.SimulationLOD0;

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
            public float freezeT = 0.2f, condenseT = 0.2f, meltT = 0.4f, boilT = 0.7f;
            public float meltRate = 1f, boilRate = 1f, condenseRate = 1f, freezeRate = 1f;
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

            // Graphics.Blit SETS RenderTexture.active to its destination and leaves it set. If we don't
            // restore it, `active` stays pointing at this RT — ReadHeatAll then captures that stale value
            // as its "previous" target and restores it, so the RT is still active when we Release() it,
            // which is what produced the repeated "Releasing render texture that is set to be
            // RenderTexture.active!" warnings.
            var prev = RenderTexture.active;
            try
            {
                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                        t.SetPixel(x, y, new Color(seed[y * res + x], 0f, 0f, 0f));
                t.Apply();
                Graphics.Blit(t, rt);
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(t);
            }
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

        // TP -> the CP7 defaults the baker turns into the default rule set. Every pre-CP7d test keeps
        // its original TP inputs and expected numbers, so they now serve as the DEFAULT-RULE PARITY
        // suite: identical results, but produced through the buffer-driven path.
        private static ThermalDefaults ToDefaults(TP tp) => new ThermalDefaults
        {
            fireHeatEmissionRate = tp.fireEmissionRate,
            fireHeatFuelCost = tp.fireFuelCost,
            condenseThreshold = tp.condenseT,
            condenseRate = tp.condenseRate,
            condenseHeatRelease = tp.condenseRelease,
            freezeThreshold = tp.freezeT,
            freezeRate = tp.freezeRate,
            meltThreshold = tp.meltT,
            meltRate = tp.meltRate,
            meltHeatCost = tp.meltCost,
            boilThreshold = tp.boilT,
            boilRate = tp.boilRate,
            boilHeatCost = tp.boilCost,
        };

        // Bakes the default rules from TP and dispatches through the buffer path.
        private static iparticle[] Run(iparticle[] particles, float[] heat, int res, TP tp, out float[] heatOut)
        {
            ThermalRuleSet rules = ThermalRuleBaker.Bake(null, ToDefaults(tp));
            return RunRules(particles, heat, res, tp, rules, out heatOut);
        }

        /// <summary>
        /// Dispatches ThermalInteractions with an EXPLICIT baked rule set uploaded as StructuredBuffers.
        /// This is the CP7d slice-2 path: no per-phase uniforms remain.
        /// </summary>
        /// <param name="forceValid">
        /// Overrides the `_ThermalRulesValid` flag independently of the rule set. This lets a test
        /// upload REAL populated buffers (non-zero counts) while telling the kernel the set is invalid,
        /// proving the pass is inert because of the flag — not merely because the buffers were empty.
        /// </param>
        private static iparticle[] RunRules(iparticle[] particles, float[] heat, int res, TP tp,
            ThermalRuleSet rules, out float[] heatOut, int? forceValid = null)
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/SimulationLOD0/ThermalInteractions.compute");
            Assert.IsNotNull(cs, "ThermalInteractions.compute should load");
            int kernel = cs.FindKernel("ThermalInteractions");

            int count = res * res;
            var readBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());
            var writeBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());

            // Fixed-capacity rule buffers, exactly like the runtime.
            var transitionBuf = new ComputeBuffer(
                ThermalRuleBaker.MaxTransitions, GpuThermalTransition.Stride, ComputeBufferType.Structured);
            var sourceBuf = new ComputeBuffer(
                ThermalRuleBaker.MaxSources, GpuThermalSource.Stride, ComputeBufferType.Structured);

            var heatRead = MakeHeatRT(res, heat);
            var heatWrite = MakeHeatRT(res, new float[count]);
            try
            {
                readBuf.SetData(particles);
                writeBuf.SetData(particles);

                var tScratch = new GpuThermalTransition[ThermalRuleBaker.MaxTransitions];
                var sScratch = new GpuThermalSource[ThermalRuleBaker.MaxSources];
                int tCount = ThermalRuleBaker.ToGpu(rules, tScratch);
                int sCount = ThermalRuleBaker.ToGpu(rules, sScratch);
                transitionBuf.SetData(tScratch);
                sourceBuf.SetData(sScratch);

                cs.SetInt("_Resolution", res);
                cs.SetFloat("_FrameDeltaTime", tp.dt);
                cs.SetInt("_EnableThermalInteractions", tp.enable);
                cs.SetInt("_EnableHeatSources", tp.enableHeatSources);
                cs.SetFloat("_AmbientTemperature", tp.ambient);
                cs.SetFloat("_MaxHeat", tp.maxHeat);

                cs.SetInt("_ThermalRulesValid", forceValid ?? (rules != null && rules.IsValid ? 1 : 0));
                cs.SetInt("_ThermalTransitionCount", tCount);
                cs.SetInt("_ThermalSourceCount", sCount);
                cs.SetBuffer(kernel, "_ThermalTransitions", transitionBuf);
                cs.SetBuffer(kernel, "_ThermalSources", sourceBuf);

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
                transitionBuf.Release();
                sourceBuf.Release();

                // Never release an RT that is still bound as the active render target.
                RenderTexture.active = null;
                heatRead.Release();
                heatWrite.Release();
                Object.DestroyImmediate(heatRead);
                Object.DestroyImmediate(heatWrite);
            }
        }

        // Builds a rule set directly from baked rules (bypassing AffinityGroup authoring) so custom
        // non-default fields can be exercised without touching any shipped asset.
        private static ThermalRuleSet CustomRules(
            BakedThermalTransition[] transitions, BakedThermalSource[] sources)
        {
            var rs = new ThermalRuleSet();
            if (transitions != null) rs.Transitions.AddRange(transitions);
            if (sources != null) rs.Sources.AddRange(sources);
            return rs;
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
            // freezeRate=0 isolates condensation: at heat 0.1 the condensed water would otherwise
            // freeze to ice in the same pass (CP7c). Freezing is covered by its own tests below.
            var cold = new iparticle[1]; cold[0].steam = 1f;
            var outCold = Run(cold, new[] { 0.1f }, 1, new TP { freezeRate = 0f }, out _);   // below condense(0.2)
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

        // 6. Codex hazard: newly-boiled steam must NOT condense (or condense-then-freeze) in the same
        // pass. Uses a VALID ladder (freeze/condense 0.3 <= melt 0.4 <= boil 0.5) — the old fixture
        // (condense 0.5 > boil 0.4) violated the ladder, so the baker now rightly rejects it and the
        // pass goes inert. "Invalid ladder is inert" is covered separately by InvalidRuleSet_IsInert.
        //
        // Boil converts all the water and draws heat down to EXACTLY the boil threshold — never below
        // it, because the heat-budget cap gives conv <= excess/heatCost. With the ladder guaranteeing
        // every cold threshold <= every hot threshold, heat can therefore never fall back into the
        // condense band within a pass. Cold-before-hot ordering is belt-and-braces on top of that.
        [UnityTest]
        public IEnumerator BoilDoesNotTriggerSamePassCondensation()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].water = 1f; // steam0 == 0
            var tp = new TP { freezeT = 0.3f, condenseT = 0.3f, meltT = 0.4f, boilT = 0.5f };
            var outp = Run(p, new[] { 1f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(outp[0].steam, Is.EqualTo(1f).Within(4e-2f),
                "Newly-boiled steam must remain steam (cold ran first, on steam0 = 0).");
            Assert.That(outp[0].water, Is.EqualTo(0f).Within(4e-2f), "All water boiled away");
            Assert.That(outp[0].ice, Is.EqualTo(0f).Within(1e-3f),
                "Newly-boiled steam must not condense-then-freeze in the same pass either (CP7c).");
            Assert.That(heatOut[0], Is.EqualTo(0.5f).Within(3e-2f),
                "Boil draws heat down TO its threshold, not below it.");
            Assert.That(heatOut[0], Is.GreaterThanOrEqualTo(tp.condenseT - 3e-2f),
                "…so heat can never re-enter the condense band within the pass.");
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
            // boilRate = 0 ISOLATES the melt. Without it, heat 1.0 sits above the default boil
            // threshold (0.7), so the freshly-melted water immediately boils away (correct hot-cascade
            // behaviour — see Cascade_HotIceMeltsThenBoils) and `water` would read 0, telling us nothing
            // about the ice cap. This is the assertion Hermes saw fail; the cascade is right, the
            // fixture was not isolating what it claimed to measure.
            var pInk = new iparticle[1]; pInk[0].ice = 0.05f;
            var outInk = Run(pInk, new[] { 1f }, 1,
                new TP { meltRate = 10f, meltCost = 0.1f, boilRate = 0f }, out _);
            Assert.That(outInk[0].water, Is.EqualTo(0.05f).Within(2e-2f), "Melt capped by available ice");
            Assert.That(outInk[0].ice, Is.EqualTo(0f).Within(1e-2f), "Ice fully consumed");
            Assert.That(outInk[0].steam, Is.EqualTo(0f).Within(1e-3f), "Boil disabled: melt is isolated");
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

        // ── CP7c: Water -> Ice freezing + cold steam cascade ────────────────────────────────

        // 18. Cold water freezes to ice; ice+water conserved; heat unchanged (no freeze cost/release).
        [UnityTest]
        public IEnumerator Freeze_WaterBelowThreshold_BecomesIce()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].water = 1f;
            var outp = Run(p, new[] { 0.1f }, 1, new TP(), out float[] heatOut);   // below freeze(0.2)
            yield return null;

            Assert.That(outp[0].ice, Is.EqualTo(1f).Within(3e-2f), "Cold water freezes to ice");
            Assert.That(outp[0].water, Is.EqualTo(0f).Within(3e-2f), "Water consumed by freezing");
            Assert.That(outp[0].steam, Is.EqualTo(0f).Within(1e-3f), "No steam involved");
            Assert.That(outp[0].water + outp[0].ice, Is.EqualTo(1f).Within(3e-2f), "water+ice conserved");
            Assert.That(heatOut[0], Is.EqualTo(0.1f).Within(2e-2f), "Freezing neither consumes nor releases heat in CP7c");
#else
            yield break;
#endif
        }

        // 19. Freezing is rate-limited: only freezeRate*dt of the water converts.
        [UnityTest]
        public IEnumerator Freeze_RateLimited_LeavesSomeWater()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].water = 1f;
            var outp = Run(p, new[] { 0.1f }, 1, new TP { freezeRate = 0.25f }, out _);
            yield return null;

            Assert.That(outp[0].ice, Is.EqualTo(0.25f).Within(3e-2f), "Only rate*dt of water freezes");
            Assert.That(outp[0].water, Is.EqualTo(0.75f).Within(3e-2f), "Remaining water stays liquid");
#else
            yield break;
#endif
        }

        // 20. Cold cascade: steam condenses to water, and that RUNNING water freezes in the same
        // dispatch (proves freeze consumes post-condensation water, not water0).
        [UnityTest]
        public IEnumerator Freeze_SteamCondensesThenFreezes_WhenCold()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].steam = 1f;
            var outp = Run(p, new[] { 0.1f }, 1, new TP { condenseRate = 1f, freezeRate = 1f }, out _);
            yield return null;

            Assert.That(outp[0].steam, Is.EqualTo(0f).Within(3e-2f), "Steam fully condensed");
            Assert.That(outp[0].water, Is.EqualTo(0f).Within(3e-2f), "Condensed water then froze");
            Assert.That(outp[0].ice, Is.EqualTo(1f).Within(5e-2f), "steam -> water -> ice cascade in one dispatch");
            Assert.That(outp[0].steam + outp[0].water + outp[0].ice, Is.EqualTo(1f).Within(5e-2f),
                "Total steam+water+ice mass conserved through the cold cascade");
#else
            yield break;
#endif
        }

        // 21. Local-only: warm local water does not freeze just because a NEIGHBOR cell is cold.
        [UnityTest]
        public IEnumerator Freeze_LocalOnly_NeighborColdDoesNotFreezeWarmLocalWater()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;   // center: warm water
            int N = 1 * res + 0;   // (0,1) cold neighbor

            var p = new iparticle[res * res];
            p[C].water = 1f;
            var heat = new float[res * res];
            heat[C] = 0.5f;   // warm: above freeze(0.2), below boil(0.7)
            heat[N] = 0f;     // neighbor is cold

            var outp = Run(p, heat, res, new TP(), out _);
            yield return null;

            Assert.That(outp[C].water, Is.EqualTo(1f).Within(3e-2f), "Warm local water must not freeze from neighbor cold");
            Assert.That(outp[C].ice, Is.EqualTo(0f).Within(1e-3f), "No ice created without LOCAL cold");
            Assert.That(outp[C].steam, Is.EqualTo(0f).Within(1e-3f), "No boil below the boil threshold");
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

        // 22. Runtime wiring guard (CP7d slice 2): the direct shader tests above can all pass while the
        // RUNTIME path never bakes/uploads the rules — freezing would then silently no-op in play (the
        // exact bug class caught in CP7c). Pin that FluidSolver bakes once and uploads the buffers,
        // and that the ladder is still sanitized when building the defaults.
        [Test]
        public void FluidSolver_BakesAndUploadsThermalRuleBuffers()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Scripts/Systems/SimulationLOD0/Core/FluidSolver.cs";
            string src = System.IO.File.ReadAllText(path);

            StringAssert.Contains("ThermalRuleBaker.Bake(ctx.AffinityGroups", src,
                "FluidSolver must bake ONCE across all active groups (not per group)");
            StringAssert.Contains("\"_ThermalTransitions\"", src, "must bind the transition buffer");
            StringAssert.Contains("\"_ThermalTransitionCount\"", src, "must upload the transition count");
            StringAssert.Contains("\"_ThermalSources\"", src, "must bind the source buffer");
            StringAssert.Contains("\"_ThermalSourceCount\"", src, "must upload the source count");
            StringAssert.Contains("\"_ThermalRulesValid\"", src, "must pass the validity flag to the shader");

            // The default rule set must still be built from the sanitized ladder.
            StringAssert.Contains("Mathf.Max(0f, ctx.FreezeThreshold)", src,
                "freezeT must be the floor of the sanitized thermal ladder");
            StringAssert.Contains("Mathf.Max(freezeT, ctx.CondenseThreshold)", src,
                "condenseT must be sanitized to >= freezeT (freeze <= condense <= melt <= boil)");

            // The old hardcoded per-phase uniforms must be GONE from the runtime path.
            Assert.IsFalse(src.Contains("\"_MeltThreshold\""),
                "Hardcoded per-phase uniforms must be replaced by the buffer-driven rule set");
            Assert.IsFalse(src.Contains("\"_FireHeatFuelCost\""),
                "Fuel cost is now carried by the baked source buffer, not a uniform");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // 23. GPU struct strides must match the HLSL layout exactly, or every rule is garbage.
        [Test]
        public void GpuThermalStructs_MatchDeclaredStrides()
        {
#if UNITY_EDITOR
            Assert.AreEqual(GpuThermalTransition.Stride, Marshal.SizeOf<GpuThermalTransition>(),
                "GpuThermalTransition must be 32 bytes to match the HLSL struct");
            Assert.AreEqual(GpuThermalSource.Stride, Marshal.SizeOf<GpuThermalSource>(),
                "GpuThermalSource must be 16 bytes to match the HLSL struct");
#else
            Assert.Ignore("Editor-only");
#endif
        }

        // 24. A buffered source on a NON-default field emits heat and burns only for heat actually added.
        // PlantSeeded(0.5) @ rate 1, fuelCost 2, dt 1 => rawEmission 0.5, fuel cap 0.5/2 = 0.25,
        // headroom 1 => heatAdded 0.25, burn 0.25*2 = 0.5 => PlantSeeded 0.
        [UnityTest]
        public IEnumerator CustomBufferedSource_OnNonDefaultField_EmitsAndBurns()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].plantSeeded = 0.5f;
            var rules = CustomRules(null, new[]
            {
                new BakedThermalSource
                {
                    field = (int)InkTypeId.PlantSeeded, heatEmissionRate = 1f, fuelCost = 2f
                }
            });

            var tp = new TP { enableHeatSources = 1, maxHeat = 1f };
            var outp = RunRules(p, new[] { 0f }, 1, tp, rules, out float[] heatOut);
            yield return null;

            Assert.That(heatOut[0], Is.EqualTo(0.25f).Within(2e-2f), "Heat capped by fuel/fuelCost");
            Assert.That(outp[0].plantSeeded, Is.EqualTo(0f).Within(2e-2f), "PlantSeeded fully burned");
            Assert.That(outp[0].fire, Is.EqualTo(0f).Within(1e-3f), "Fire is not a source in this rule set");
#else
            yield break;
#endif
        }

        // 25. A buffered HOT transition on NON-default fields converts per the heat budget.
        // PlantSeeded(1) -> PlantGrown, thr 0.4, rate 1, heatCost 0.5, heat 1 =>
        // excess 0.6, cap 0.6/0.5 = 1.2, rate cap 1 => conv 1 => PlantGrown 1, heat 1 - 0.5 = 0.5.
        [UnityTest]
        public IEnumerator CustomBufferedHotTransition_OnNonDefaultFields_Converts()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].plantSeeded = 1f;
            var rules = CustomRules(new[]
            {
                new BakedThermalTransition
                {
                    fromField = (int)InkTypeId.PlantSeeded,
                    toField = (int)InkTypeId.PlantGrown,
                    regime = ThermalRegime.Hot,
                    threshold = 0.4f, rate = 1f, heatCost = 0.5f, heatRelease = 0f
                }
            }, null);

            var tp = new TP { enableHeatSources = 0, maxHeat = 1f };
            var outp = RunRules(p, new[] { 1f }, 1, tp, rules, out float[] heatOut);
            yield return null;

            Assert.That(outp[0].plantGrown, Is.EqualTo(1f).Within(3e-2f), "PlantSeeded converted to PlantGrown");
            Assert.That(outp[0].plantSeeded, Is.EqualTo(0f).Within(3e-2f), "Source consumed");
            Assert.That(heatOut[0], Is.EqualTo(0.5f).Within(3e-2f), "Heat drawn by conv * heatCost");
            Assert.That(outp[0].water, Is.EqualTo(0f).Within(1e-3f), "Default rules are NOT applied");
#else
            yield break;
#endif
        }

        // ── Negative-source underflow regressions (Codex blocker) ────────────────────────────
        // A transition source that has underflowed below 0 must not produce a NEGATIVE conversion.
        // Unclamped, conv < 0 would (a) drain the DESTINATION ink and (b) for hot transitions INVERT
        // the heat budget (`heat -= conv*cost` ADDS heat), minting energy.
        //
        // The source magnitude is -0.1 (NOT -0.01) so every pre-fix deviation is 0.05..0.1 — i.e.
        // 2.5x..5x the 0.02 tolerance. Each expected value below is annotated with what the PRE-FIX
        // kernel produced, and every one of those is outside tolerance, so these tests genuinely FAIL
        // on the broken kernel rather than passing vacuously.

        // 27a. COLD: negative source must not drain the destination or invert the heat release.
        // Isolated to a freeze-only rule so the negative water reaches the freeze source directly
        // (under the default rules, condense's destination-write clamps water to 0 first, which would
        // mask the bug entirely).
        //   PRE-FIX: conv = -0.1  =>  ice 0.4 -> 0.3,  heat 0.1 -> 0.05
        //   POST-FIX: conv = 0    =>  ice 0.4,         heat 0.1,   water clamped to 0
        [UnityTest]
        public IEnumerator NegativeSource_ColdTransition_DoesNotDrainDestinationOrHeat()
        {
#if UNITY_EDITOR
            var p = new iparticle[1];
            p[0].water = -0.1f;   // underflowed source
            p[0].ice = 0.4f;      // destination starts non-zero: an unclamped conv would drain it

            var rules = CustomRules(new[]
            {
                new BakedThermalTransition
                {
                    fromField = (int)InkTypeId.Water, toField = (int)InkTypeId.Ice,
                    regime = ThermalRegime.Cold, threshold = 0.2f, rate = 1f, heatRelease = 0.5f
                }
            }, null);

            var tp = new TP { enableHeatSources = 0, maxHeat = 1f };
            var outp = RunRules(p, new[] { 0.1f }, 1, tp, rules, out float[] heatOut);
            yield return null;

            Assert.That(outp[0].ice, Is.EqualTo(0.4f).Within(2e-2f),
                "Destination must NOT be drained by a negative source (pre-fix: 0.3)");
            Assert.That(outp[0].water, Is.EqualTo(0f).Within(2e-2f), "Underflowed source clamps to 0");
            Assert.That(heatOut[0], Is.EqualTo(0.1f).Within(2e-2f),
                "Heat release must not be inverted into a heat LOSS (pre-fix: 0.05)");
#else
            yield break;
#endif
        }

        // 27b. HOT (default rules): negative ice in the melt band must not drain water or MINT heat.
        //   PRE-FIX: conv = -0.1  =>  water 0.3 -> 0.2,  heat 0.6 -> 0.65 (energy created)
        //   POST-FIX: conv = 0    =>  water 0.3,         heat 0.6,        ice clamped to 0
        // heat 0.6 sits above melt (0.4) and below boil (0.7), so melt fires and boil does not.
        [UnityTest]
        public IEnumerator NegativeSource_HotTransition_DoesNotDrainDestinationOrMintHeat()
        {
#if UNITY_EDITOR
            var p = new iparticle[1];
            p[0].ice = -0.1f;     // underflowed melt source
            p[0].water = 0.3f;    // melt destination, non-zero so a drain is observable

            var outp = Run(p, new[] { 0.6f }, 1, new TP(), out float[] heatOut);
            yield return null;

            Assert.That(outp[0].water, Is.EqualTo(0.3f).Within(2e-2f),
                "Destination must NOT be drained by a negative source (pre-fix: 0.2)");
            Assert.That(outp[0].ice, Is.EqualTo(0f).Within(2e-2f), "Underflowed source clamps to 0");
            Assert.That(heatOut[0], Is.EqualTo(0.6f).Within(2e-2f),
                "Heat must NOT be minted by a negative conversion (pre-fix: 0.65)");
            Assert.That(outp[0].steam, Is.EqualTo(0f).Within(1e-3f), "Below boil threshold: no boil");
#else
            yield break;
#endif
        }

        // 27c. Custom buffered transition on NON-default fields, negative source.
        // maxHeat = 2 so the pre-fix heat gain (1.0 -> 1.05) is observable rather than clamped away.
        //   PRE-FIX: conv = -0.1  =>  plantGrown 0.5 -> 0.4,  heat 1.0 -> 1.05
        //   POST-FIX: conv = 0    =>  plantGrown 0.5,         heat 1.0
        [UnityTest]
        public IEnumerator NegativeSource_CustomBufferedTransition_DoesNotDrainOrMint()
        {
#if UNITY_EDITOR
            var p = new iparticle[1];
            p[0].plantSeeded = -0.1f;   // underflowed source
            p[0].plantGrown = 0.5f;     // destination, non-zero

            var rules = CustomRules(new[]
            {
                new BakedThermalTransition
                {
                    fromField = (int)InkTypeId.PlantSeeded,
                    toField = (int)InkTypeId.PlantGrown,
                    regime = ThermalRegime.Hot,
                    threshold = 0.4f, rate = 1f, heatCost = 0.5f
                }
            }, null);

            var tp = new TP { enableHeatSources = 0, maxHeat = 2f };
            var outp = RunRules(p, new[] { 1f }, 1, tp, rules, out float[] heatOut);
            yield return null;

            Assert.That(outp[0].plantGrown, Is.EqualTo(0.5f).Within(2e-2f),
                "Destination must NOT be drained (pre-fix: 0.4)");
            Assert.That(outp[0].plantSeeded, Is.EqualTo(0f).Within(2e-2f), "Underflowed source clamps to 0");
            Assert.That(heatOut[0], Is.EqualTo(1f).Within(2e-2f),
                "Heat must NOT be minted (pre-fix: 1.05)");
#else
            yield break;
#endif
        }

        // 26. An INVALID rule set is inert: particles and heat pass through unchanged even though the
        // buffers still contain values. Never partially applied.
        [UnityTest]
        public IEnumerator InvalidRuleSet_IsInert_PassThrough()
        {
#if UNITY_EDITOR
            var p = new iparticle[1];
            p[0].ice = 0.5f; p[0].water = 0.3f; p[0].fire = 0.2f;

            // A VALID rule set (so the buffers are genuinely populated and the counts are non-zero),
            // dispatched with the validity flag forced to 0. If the kernel honoured the buffers instead
            // of the flag, this ice WOULD melt and this fire WOULD burn — so the assertions below prove
            // inertness comes from the flag, not from empty buffers.
            var rules = CustomRules(new[]
            {
                new BakedThermalTransition
                {
                    fromField = (int)InkTypeId.Ice, toField = (int)InkTypeId.Water,
                    regime = ThermalRegime.Hot, threshold = 0.1f, rate = 1f, heatCost = 0.1f
                }
            }, new[]
            {
                new BakedThermalSource
                {
                    field = (int)InkTypeId.Fire, heatEmissionRate = 1f, fuelCost = 1f
                }
            });
            Assume.That(rules.IsValid, "fixture must be valid so the buffers are populated");

            var tp = new TP { enableHeatSources = 1, maxHeat = 1f };
            var outp = RunRules(p, new[] { 0.9f }, 1, tp, rules, out float[] heatOut, forceValid: 0);
            yield return null;

            Assert.That(outp[0].ice, Is.EqualTo(0.5f).Within(2e-2f), "Ice unchanged");
            Assert.That(outp[0].water, Is.EqualTo(0.3f).Within(2e-2f), "Water unchanged");
            Assert.That(outp[0].fire, Is.EqualTo(0.2f).Within(2e-2f), "Fire unchanged (no emission)");
            Assert.That(heatOut[0], Is.EqualTo(0.9f).Within(2e-2f), "Heat unchanged");
#else
            yield break;
#endif
        }
    }
}
