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
            // CP8g: one-shot chill as Water -> Ice forms. Defaults to 0 so every pre-CP8g test keeps its
            // original heat numbers; the CP8g tests opt in explicitly.
            public float freezeHeatCost = 0f;
            // CP8a: this is the CLAMP FLOOR (minTemperature), not the neutral/room temperature.
            public float minTemp = 0f, maxHeat = 1f;
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
            freezeHeatCost = tp.freezeHeatCost,
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
                cs.SetFloat("_MinTemperature", tp.minTemp);
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
        // CP8i: dispatches the REAL DiffuseHeat kernel (InkTools Fluids.compute) so a test can compose
        // conduction with ThermalInteractions exactly as FluidSolver.Step does: heat transport first,
        // then the thermal pass reads the freshly-diffused heat. `obstacle` is bound because ice
        // actsAsObstacle — and CP8d deliberately made conduction IGNORE that mask, so heat still enters
        // solid ice. Binding it with the ice cell masked is what proves that end of the contract.
        /// <summary>
        /// CP8q: extended with the CP8l/CP8o parameters this helper predates — `dt` (conduction is a
        /// PER-SECOND rate converted by 1-exp(-rate*dt)), `diffusionSolid`, and the ice-concentration
        /// thermal-solid threshold with its particle buffer.
        ///
        /// Setting them EVERY call is mandatory, not tidiness: compute uniforms and buffers persist
        /// between dispatches, so without this a prior test's threshold leaks in and the kernel can read
        /// an unbound particle buffer — the same stale-uniform class CP8 has now hit three times.
        /// Defaults keep every pre-CP8q caller byte-identical (threshold 0 => geometry-mask path only).
        /// </summary>
        private static float[] DispatchDiffuseHeat(float[] heat, float[] obstacle, int res,
            float diffusion, float minTemp, float maxHeat,
            float dt = 1f, float diffusionSolid = -1f, float iceThermalThreshold = 0f, iparticle[] particles = null)
        {
            if (diffusionSolid < 0f) diffusionSolid = diffusion;
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.inktools.sim/Compute/Fluids.compute");
            Assert.IsNotNull(cs, "Fluids.compute should load");
            int kernel = cs.FindKernel("DiffuseHeat");

            var hr = MakeHeatRT(res, heat);
            var hw = MakeHeatRT(res, new float[res * res]);

            var obs = new RenderTexture(res, res, 0, RenderTextureFormat.RFloat) { enableRandomWrite = true };
            obs.Create();
            var obsTex = new Texture2D(res, res, TextureFormat.RGBAFloat, false);

            // Always bind a particle buffer so the kernel can never read an unbound SRV.
            var parts = particles ?? new iparticle[res * res];
            var partBuf = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            partBuf.SetData(parts);

            var prev = RenderTexture.active;
            try
            {
                for (int y = 0; y < res; y++)
                    for (int x = 0; x < res; x++)
                        obsTex.SetPixel(x, y, new Color(obstacle[y * res + x], 0f, 0f, 0f));
                obsTex.Apply();
                Graphics.Blit(obsTex, obs);
                RenderTexture.active = prev;   // Blit leaves `active` set; releasing it later would warn.

                cs.SetVector("_SimulationSize", new Vector2(res, res));
                cs.SetFloat("_ThermalDiffusion", diffusion);
                cs.SetFloat("_ThermalDiffusionSolid", diffusionSolid);
                cs.SetFloat("_ThermalSolidThresholdIce", iceThermalThreshold);   // ALWAYS set — no leak
                cs.SetFloat("_FrameDeltaTime", dt);
                cs.SetFloat("_MinTemperature", minTemp);
                cs.SetFloat("_MaxHeat", maxHeat);
                cs.SetTexture(kernel, "_HeatRead", hr);
                cs.SetTexture(kernel, "_HeatWrite", hw);
                cs.SetTexture(kernel, "_ObstacleRead", obs);
                cs.SetBuffer(kernel, "_ParticlesRead", partBuf);                 // ALWAYS bound
                int g = Mathf.CeilToInt(res / 8f);
                cs.Dispatch(kernel, g, g, 1);
                return ReadHeatAll(hw, res);
            }
            finally
            {
                RenderTexture.active = null;
                hr.Release(); hw.Release(); obs.Release(); partBuf.Release();
                Object.DestroyImmediate(hr); Object.DestroyImmediate(hw);
                Object.DestroyImmediate(obs); Object.DestroyImmediate(obsTex);
            }
        }

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

        // CP8h: shipped condensation is GENTLE. Lake: "the steam should dissipate into water at a lesser
        // rate. Only a little bit of water should form from cooling steam." Cold steam must still
        // condense — the THRESHOLD is untouched — but it sheds only ~15% of itself per second instead of
        // collapsing wholesale, so steam lingers and drizzles rather than vanishing into a puddle.
        //
        // freezeRate = 0 isolates condensation: at heat 0.1 the condensed water would otherwise freeze
        // to ice in the same pass (CP7c cold cascade), which would eat the water we are measuring.
        //
        // The exact numbers are the point — a `water > 0` assertion would pass just as happily on the
        // old full-collapse behaviour and would not pin what we actually changed.
        [UnityTest]
        public IEnumerator Condense_GentleRate_OnlyALittleWaterForms()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].steam = 1f;
            var tp = new TP { condenseRate = 0.15f, freezeRate = 0f };
            var outp = Run(p, new[] { 0.1f }, 1, tp, out _);   // below condense(0.2)
            yield return null;

            Assert.That(outp[0].water, Is.EqualTo(0.15f).Within(3e-2f),
                "Only a LITTLE water forms from cooling steam (rate 0.15), not a wholesale collapse");
            Assert.That(outp[0].steam, Is.EqualTo(0.85f).Within(3e-2f),
                "…and most of the steam survives the step to keep drifting");
            Assert.That(outp[0].steam + outp[0].water, Is.EqualTo(1f).Within(3e-2f),
                "steam + water conserved");
            Assert.That(outp[0].ice, Is.EqualTo(0f).Within(1e-3f),
                "Freezing is disabled here, so the new water must stay liquid");
#else
            yield break;
#endif
        }

        // 5. Condense only when pre-reaction heat < condenseThreshold: steam -> water, conserved.
        // LEGACY mechanics fixture: TP.condenseRate defaults to 1f (full rate), which is what the CP7
        // numeric baselines pin. CP8h's gentle shipped rate lives in Cp8Defaults, not in TP.
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
        // pass. The fixture uses condense 0.3 <= boil 0.5, i.e. a VALID water<->steam inverse cycle.
        // (The original fixture had condense 0.5 > boil 0.4 — a genuine cycle violation — so the baker
        // rejects it and the pass goes inert. "Invalid rules are inert" is covered by InvalidRuleSet_IsInert.)
        //
        // Boil converts all the water and draws heat down to EXACTLY the boil threshold — never below
        // it, because the heat-budget cap gives conv <= excess/heatCost. Since the cycle invariant
        // guarantees condense <= boil, heat can therefore never fall back into the condense band within
        // a pass. Cold-before-hot ordering is belt-and-braces on top of that.
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

        // 10b. CP8a: the kernel's clamp FLOOR is _MinTemperature — NOT the neutral/room temperature.
        // This is the single most consequential CP8a invariant: if neutral (0.5) were the floor, nothing
        // could ever be colder than room temperature and ice could NEVER form. The CPU oracle covers this,
        // and the runtime wiring is pinned by a source assertion — but neither proves the KERNEL honours
        // it, so this closes that gap directly on the GPU.
        //
        // An empty-but-valid rule set is used deliberately: no transitions and no sources fire, so the
        // final clamp is the only thing under test.
        [UnityTest]
        public IEnumerator MinTemperature_ClampsBelowMin_ButDoesNotClampToNeutral()
        {
#if UNITY_EDITOR
            var emptyValidRules = CustomRules(null, null);
            Assume.That(emptyValidRules.IsValid, "an empty rule set must still be valid, so the pass runs");

            var tp = new TP { enableHeatSources = 0, minTemp = 0.1f, maxHeat = 1f };

            // (a) Below the floor => clamps UP to minTemperature.
            var below = new iparticle[1];
            RunRules(below, new[] { -0.2f }, 1, tp, emptyValidRules, out float[] belowOut);
            yield return null;
            Assert.That(belowOut[0], Is.EqualTo(0.1f).Within(2e-2f),
                "A temperature below the floor must clamp UP to minTemperature");

            // (b) Above the floor but BELOW neutral (0.5) => must stay put. If the kernel were still
            // clamping to neutral/ambient, this would be dragged up to 0.5 and the test would fail.
            var subNeutral = new iparticle[1];
            RunRules(subNeutral, new[] { 0.25f }, 1, tp, emptyValidRules, out float[] subOut);
            yield return null;
            Assert.That(subOut[0], Is.EqualTo(0.25f).Within(2e-2f),
                "A sub-neutral temperature must NOT be clamped up to neutral (0.5) — ice could never form");
            Assert.That(subOut[0], Is.LessThan(0.5f - 2e-2f),
                "Sub-neutral must remain sub-neutral: neutral is the relaxation target, never the clamp floor");
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

        // ── CP8k: cold fire GOES OUT, through the REAL kernel ───────────────────────────────
        // Proves the SINK sentinel (toField < 0) survives the GPU round trip: the struct is uploaded
        // with toField = -1 and the kernel must remove the source ink WITHOUT crediting any destination.
        // A bug here would either write ink into a garbage channel or silently do nothing at all, so
        // this is the parity test that matters most for the new sentinel.
        [UnityTest]
        public IEnumerator ShippedRules_ColdFire_IsExtinguished_AndBecomesNothing()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].fire = 1f;
            ThermalRuleSet shipped = ThermalRuleBaker.Bake(null, ThermalDefaults.Cp8Defaults);
            Assume.That(shipped.IsValid, "Shipped CP8 defaults must bake cleanly: " + shipped.Error);

            // Sources OFF so the fire cannot reheat its own cell — we are testing the sink in isolation.
            var tp = new TP { minTemp = 0f, maxHeat = 1f, enableHeatSources = 0 };
            var outp = RunRules(p, new[] { 0.5f }, 1, tp, shipped, out float[] heatOut);  // 0.5 < sink 0.6
            yield return null;

            Assert.That(outp[0].fire, Is.LessThan(0.05f),
                "Cold fire must go out rapidly through the real kernel");

            // The sink's defining property: the fire became NOTHING. Lake ruled out smoke/puddles.
            Assert.That(outp[0].steam, Is.EqualTo(0f).Within(1e-2f), "Dying fire must not mint steam/smoke");
            Assert.That(outp[0].water, Is.EqualTo(0f).Within(1e-2f), "…nor water");
            Assert.That(outp[0].ice, Is.EqualTo(0f).Within(1e-2f), "…nor anything else");

            Assert.That(heatOut[0], Is.EqualTo(0.5f).Within(2e-2f),
                "Extinguishing is heat-neutral — it must not chill the cell, or the sink becomes just " +
                "another term in the heat ratchet CP8k exists to remove");
#else
            yield break;
#endif
        }

        [UnityTest]
        public IEnumerator ShippedRules_HotFire_SurvivesTheSink()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].fire = 1f;
            ThermalRuleSet shipped = ThermalRuleBaker.Bake(null, ThermalDefaults.Cp8Defaults);
            Assume.That(shipped.IsValid, shipped.Error);

            var tp = new TP { minTemp = 0f, maxHeat = 1f, enableHeatSources = 0 };
            var outp = RunRules(p, new[] { 0.9f }, 1, tp, shipped, out _);   // above the 0.6 sink threshold
            yield return null;

            Assert.That(outp[0].fire, Is.EqualTo(1f).Within(2e-2f),
                "Fire in a genuinely hot cell must survive — the sink culls fire that drifted somewhere " +
                "COLD, not a healthy flame (which heats its own cell above the threshold anyway)");
#else
            yield break;
#endif
        }

        // ── CP8q: THE RED TARGET — obstacle-strength ice must actually MELT under fire contact ──
        //
        // Lake: "when the Ice value is high enough to make an obstacle, the heat still doesn't advect
        // into the Ice, so the temperature never rises high enough to trigger a melting of the ice."
        //
        // CP8q-fix (CKPT-085): the first version of this test lived in HeatLayerTests, composed only
        // AdvectHeat + DiffuseHeat, and asserted HEAT ONLY — while its comment claimed it asserted ice
        // mass. That was an overclaim and it did not measure Lake's bug at all: "temperature never rises
        // high enough to TRIGGER MELTING" is a statement about ice mass, so the test must dispatch
        // ThermalInteractions and watch ice fall / water rise. It now does.
        //
        // Composes the real order over many frames, at obstacle strength (ice = 1.0, mask set):
        //     AdvectHeat (runtime-clipped velocity)  ->  DiffuseHeat  ->  ThermalInteractions
        // against the SHIPPED CP8 rule set, and asserts raw ice/water/heat with conservation — so a
        // visual or thermometer-only false positive is impossible.
        [UnityTest]
        public IEnumerator ObstacleStrengthIce_UnderFireContact_ActuallyMelts_NotJustWarms()
        {
#if UNITY_EDITOR
            const int res = 5;
            int FIRE = 2 * res + 1;    // (1,2) sustained flame
            int FACE = 2 * res + 2;    // (2,2) ice face touching the flame

            ThermalRuleSet shipped = ThermalRuleBaker.Bake(null, ThermalDefaults.Cp8Defaults);
            Assume.That(shipped.IsValid, "Shipped CP8 defaults must bake cleanly: " + shipped.Error);

            // A 3-deep ice wall at OBSTACLE STRENGTH (1.0, well above Ice.obstacleThreshold 0.5), cold,
            // with a flame on its left face. Obstacle mask set for the wall, exactly as InkToObstacles
            // would set it at runtime — we are NOT lowering the obstacle threshold to make this pass.
            var p = new iparticle[res * res];
            var heat = new float[res * res];
            var obstacle = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 0.5f;      // neutral room
            for (int y = 0; y < res; y++)
                for (int x = 2; x <= 4; x++)
                {
                    int i = y * res + x;
                    p[i].ice = 1f;                                     // obstacle-strength ice
                    heat[i] = 0f;                                      // …and genuinely cold
                    obstacle[i] = 1f;
                }
            heat[FIRE] = 1f;

            float iceStart = p[FACE].ice;
            float waterStart = p[FACE].water;
            Assume.That(iceStart, Is.EqualTo(1f).Within(1e-3f), "face starts as solid ice");

            var tp = new TP { minTemp = 0f, maxHeat = 1f, enableHeatSources = 0 };
            float[] h = heat;
            float faceHeatPeak = 0f;

            for (int frame = 0; frame < 240; frame++)   // 4 simulated seconds
            {
                h[FIRE] = 1f;   // a burning fire cell holds itself at max via its own emission

                // This test drives CONDUCTION only, deliberately — it is a lower-level check that the
                // conduction+melt chain works, NOT the CP8q runtime proof.
                //
                // SUPERSEDED NOTE: an earlier version of this comment claimed conduction is "the ONLY path
                // into a solid". That was true of the old code but is no longer the design. Lake: "I do
                // want the heat to advect through the obstacle ice." CP8q added a pre-boundary velocity
                // snapshot so heat advects into solids too; see FluidSolver + Heat.hlsl. Advection is not
                // exercised here, which is why this test alone cannot answer Lake's question.
                h = DispatchDiffuseHeat(h, obstacle, res, diffusion: 2f, minTemp: 0f, maxHeat: 1f,
                    dt: 1f / 60f, diffusionSolid: 12f, iceThermalThreshold: 0.1f, particles: p);

                // …then the phase pass, which is what turns heat into actual MELTING.
                p = RunRules(p, h, res, tp, shipped, out float[] heatOut);
                h = heatOut;

                if (h[FACE] > faceHeatPeak) faceHeatPeak = h[FACE];
            }
            yield return null;

            float iceEnd = p[FACE].ice, waterEnd = p[FACE].water;

            Assert.That(faceHeatPeak, Is.GreaterThan(0.15f),
                $"The ice face must warm past the melt threshold under sustained fire contact. " +
                $"Peak was {faceHeatPeak:0.0000}. If this fails, heat never reaches obstacle-strength ice " +
                "at all — Lake's observation — and conduction across the fluid/solid face is the culprit.");

            Assert.That(iceEnd, Is.LessThan(iceStart - 0.01f),
                $"THE ACTUAL BUG: obstacle-strength ice must LOSE MASS, not merely get warm. " +
                $"ice {iceStart:0.###} -> {iceEnd:0.###}. Warming without melting is exactly what Lake " +
                "reports seeing.");

            Assert.That(waterEnd, Is.GreaterThan(waterStart + 0.01f),
                $"…and the melted ice must become water. water {waterStart:0.###} -> {waterEnd:0.###}");

            Assert.That(iceEnd + waterEnd, Is.EqualTo(iceStart + waterStart).Within(5e-2f),
                "ice + water must be conserved through melting — no minting, no loss");
#else
            yield break;
#endif
        }

        // ── CP8i: ice INTENSITY is coupled to TEMPERATURE ───────────────────────────────────
        // Lake: "there should be a relation between the ice's intensity and its temperature. The
        // dissipation of the ice should correspond to the diffusion of heat into the ice."
        //
        // These two tests compose the REAL kernels in the same order FluidSolver.Step runs them —
        // DiffuseHeat, then ThermalInteractions — and pin both halves of that statement:
        //   * heat conducted INTO an ice cell melts it, and the melt is paid for out of that heat;
        //   * with no heat flowing in, the ice does not melt at all.
        // Ice's independent time-based fade was removed in CP8i (Ice.asset dissipationHalfLife
        // 45 -> 120000, matching the other structural/obstacle inks), so melt is now the ONLY route by
        // which ice loses intensity. That asset policy is pinned separately in HeatLayerTests.

        [UnityTest]
        public IEnumerator HeatDiffusedIntoIce_MeltsIt_AndPaysForItInHeat()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;   // centre: a cold cell of solid ice

            var p = new iparticle[res * res];
            p[C].ice = 1f;

            // Centre starts at the floor; every neighbour is hot. Ice actsAsObstacle, so the centre is
            // masked — CP8d's conduction-ignores-obstacles rule is what lets the heat reach it at all.
            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 1f;
            heat[C] = 0f;

            var obstacle = new float[res * res];
            obstacle[C] = 1f;

            // 1. Conduction. centre = lerp(0, avg(4 hot cardinals) = 1, blend) where CP8l made the blend
            //    dt-normalized: ConductionBlend(rate, dt) = 1 - exp(-rate*dt). With dt=1 a rate of ln(2)
            //    ~= 0.6931 gives blend = 0.5, so the centre still lands on 0.5 — above melt(0.4) so the
            //    downstream melt chain (0.8/0.2, heat->0.4) is unchanged. (Pre-CP8l this was a per-frame
            //    lerp where rate 0.5 meant blend 0.5 directly; 0.5 now yields 1-exp(-0.5)=0.393, below
            //    the melt threshold, which is why the old expectation went stale.)
            float[] diffused = DispatchDiffuseHeat(heat, obstacle, res, diffusion: 0.6931f,
                minTemp: 0f, maxHeat: 1f);
            yield return null;

            Assert.That(diffused[C], Is.EqualTo(0.5f).Within(3e-2f),
                "Heat must CONDUCT INTO the solid ice cell (CP8d) — otherwise ice is a perfect insulator " +
                "and could never melt from its surroundings");

            // 2. The thermal pass reads that freshly-diffused heat.
            //    0.5 > melt(0.4). excess = 0.1, meltCost 0.5 => conv capped at excess/cost = 0.2.
            var outp = Run(p, diffused, res, new TP(), out float[] heatOut);
            yield return null;

            Assert.That(outp[C].ice, Is.EqualTo(0.8f).Within(3e-2f),
                "Ice loses intensity by MELTING — bounded by the heat available above the melt threshold");
            Assert.That(outp[C].water, Is.EqualTo(0.2f).Within(3e-2f), "…turning into exactly that much water");
            Assert.That(outp[C].ice + outp[C].water, Is.EqualTo(1f).Within(3e-2f), "ice + water conserved");

            // THE COUPLING. The melt is paid for out of the heat that arrived: heat drops by
            // conv * meltHeatCost = 0.2 * 0.5 = 0.1, landing exactly back on the melt threshold. This is
            // why ice loss TRACKS heat inflow — melt is capped by excess/heatCost, so it consumes the
            // incoming heat and then stalls until conduction delivers more.
            Assert.That(heatOut[C], Is.LessThan(diffused[C] - 2e-2f),
                "Melting must CONSUME the heat that melted it (latent heat), not melt for free");
            Assert.That(heatOut[C], Is.EqualTo(0.4f).Within(3e-2f),
                "Heat is drawn down to exactly the melt threshold — the heat budget is spent, melt stalls");
#else
            yield break;
#endif
        }

        // The other half of Lake's statement, and the one the old behaviour got wrong: with NO heat
        // flowing in, cold ice must simply persist. It must not melt, and (post-CP8i) it has no
        // independent time fade either, so its intensity is stable.
        [UnityTest]
        public IEnumerator ColdIce_WithNoHeatInflow_DoesNotMelt()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;

            var p = new iparticle[res * res];
            p[C].ice = 1f;

            var heat = new float[res * res];          // whole grid at the floor: nothing to conduct in
            var obstacle = new float[res * res];
            obstacle[C] = 1f;

            float[] diffused = DispatchDiffuseHeat(heat, obstacle, res, diffusion: 0.5f,
                minTemp: 0f, maxHeat: 1f);
            yield return null;

            Assert.That(diffused[C], Is.EqualTo(0f).Within(2e-2f), "No warm neighbours => no heat arrives");

            var outp = Run(p, diffused, res, new TP(), out float[] heatOut);
            yield return null;

            Assert.That(outp[C].ice, Is.EqualTo(1f).Within(2e-2f),
                "Cold ice must PERSIST — with no heat arriving there is no thermal reason for it to go");
            Assert.That(outp[C].water, Is.EqualTo(0f).Within(2e-2f), "…and none of it becomes water");
            Assert.That(heatOut[C], Is.EqualTo(0f).Within(2e-2f),
                "…and it does not keep pulling heat down either (CP8g: no continuous cold source)");
#else
            yield break;
#endif
        }

        // ── CP8g: ice is a cold source WHEN IT FORMS ────────────────────────────────────────
        // Lake: "Ice should be a cold source, but only when it forms, whether by painting or growing."
        // Painted ice is handled by the CP8b injection stamp (min temperature). GROWN ice — water
        // freezing — cools through the cold transition's heatCost. The defining property is that the
        // cooling scales with the amount CONVERTED, so it is a formation event, not a standing emitter.

        // Full conversion with cost 1: 0.1 - (1.0 * 1.0) = -0.9, which must clamp to the floor.
        [UnityTest]
        public IEnumerator Freeze_WithHeatCost_ChillsAsIceForms()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].water = 1f;
            var tp = new TP { freezeHeatCost = 1f, minTemp = 0f };
            var outp = Run(p, new[] { 0.1f }, 1, tp, out float[] heatOut);   // below freeze(0.2)
            yield return null;

            Assert.That(outp[0].ice, Is.EqualTo(1f).Within(3e-2f), "Cold water freezes to ice");
            Assert.That(outp[0].water, Is.EqualTo(0f).Within(3e-2f), "Water consumed by freezing");
            Assert.That(heatOut[0], Is.EqualTo(0f).Within(2e-2f),
                "Forming ice must CHILL the cell — 0.1 minus a full unit of cost clamps to the floor");
            Assert.That(heatOut[0], Is.LessThan(0.1f - 2e-2f),
                "…and it must genuinely be colder than it started, not merely unchanged");
#else
            yield break;
#endif
        }

        // The cooling must be PROPORTIONAL to what converted, not a flat per-cell hit. 25% of the water
        // freezes at cost 0.2 => heat drops by 0.25*0.2 = 0.05, i.e. 0.1 -> 0.05. If the kernel applied
        // the cost unscaled, heat would land at 0.1 - 0.2 = 0 (clamped) and this would catch it.
        [UnityTest]
        public IEnumerator Freeze_RateLimitedHeatCost_CoolsByConvertedAmount()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].water = 1f;
            var tp = new TP { freezeRate = 0.25f, freezeHeatCost = 0.2f, minTemp = 0f };
            var outp = Run(p, new[] { 0.1f }, 1, tp, out float[] heatOut);
            yield return null;

            Assert.That(outp[0].ice, Is.EqualTo(0.25f).Within(3e-2f), "Only rate*dt of water freezes");
            Assert.That(heatOut[0], Is.EqualTo(0.05f).Within(2e-2f),
                "Cooling must scale with the CONVERTED amount: 0.1 - 0.25*0.2 = 0.05");
#else
            yield break;
#endif
        }

        // THE CORE CP8g GUARANTEE. A cell of settled ice, below the melt threshold, with no water left
        // to freeze, must NOT keep dragging its own temperature down. Ice is not a standing cold
        // emitter the way fire is a standing heat source — if it were, ice fields would run away to the
        // floor forever. conv == 0 => no cooling.
        // CP8j: asserted BELOW the freezing point (TP's freeze is 0.2), not in the old dead band. It used
        // to sit at heat 0.3 — above freezing yet below TP's melt of 0.4 — which passed, but only because
        // it was pinning the very gap Lake reported: ice loitering at a temperature that is not cold. The
        // no-continuous-cooling guarantee is real, so it stays; it just has to be stated where ice is
        // actually entitled to exist.
        [UnityTest]
        public IEnumerator ExistingIce_BelowFreezing_WithNoWater_DoesNotContinuouslyCool()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].ice = 1f;   // no water: nothing can freeze
            var tp = new TP { freezeHeatCost = 1f, minTemp = 0f };
            var outp = Run(p, new[] { 0.1f }, 1, tp, out float[] heatOut);   // below freeze(0.2): genuinely cold
            yield return null;

            Assert.That(outp[0].ice, Is.EqualTo(1f).Within(3e-2f), "Ice below the freezing point persists");
            Assert.That(outp[0].water, Is.EqualTo(0f).Within(3e-2f), "…and none of it melts");
            Assert.That(heatOut[0], Is.EqualTo(0.1f).Within(2e-2f),
                "Existing ice must NOT keep cooling — the chill happens at FORMATION, not continuously");
#else
            yield break;
#endif
        }

        // CP8j through the REAL kernel, on the SHIPPED rules (not the TP fixture): with freeze == melt,
        // ice one notch above the freezing point melts, and pays for it in latent heat that lands the
        // cell back exactly ON the freezing point — never below it. Landing below would push the fresh
        // water back into the freeze band and churn ice<->water forever; landing exactly on it is what
        // makes this converge instead of oscillating, and is why melting ice cannot run away into an
        // ever-expanding pocket of cold.
        [UnityTest]
        public IEnumerator ShippedRules_IceAboveFreezing_Melts_AndSettlesAtTheFreezePoint()
        {
#if UNITY_EDITOR
            var p = new iparticle[1]; p[0].ice = 1f;
            ThermalRuleSet shipped = ThermalRuleBaker.Bake(null, ThermalDefaults.Cp8Defaults);
            Assume.That(shipped.IsValid, "Shipped CP8 defaults must bake cleanly: " + shipped.Error);

            var tp = new TP { minTemp = 0f, maxHeat = 1f, enableHeatSources = 0 };
            var outp = RunRules(p, new[] { 0.2f }, 1, tp, shipped, out float[] heatOut);  // above freeze(0.15)
            yield return null;

            // excess = 0.2 - 0.15 = 0.05; CP8ad meltHeatCost 0.10 => conv capped at 0.05/0.10 = 0.5.
            // The cost fell 0.5 -> 0.15 (CP8l) -> 0.10 (CP8ad), so the SAME deposited heat now melts 5x
            // more ice than the legacy 0.5 cost — ice keeps melting from heat already delivered rather
            // than needing a constant fire stream. The heat still settles on exactly the freeze point
            // regardless of cost; only the ice bought with it changed.
            Assert.That(outp[0].ice, Is.EqualTo(0.5f).Within(3e-2f),
                "Ice above the freezing point must MELT under the shipped rules");
            Assert.That(outp[0].water, Is.EqualTo(0.5f).Within(3e-2f), "…into water");
            Assert.That(outp[0].ice + outp[0].water, Is.EqualTo(1f).Within(3e-2f), "ice + water conserved");

            Assert.That(heatOut[0], Is.EqualTo(0.15f).Within(2e-2f),
                "Melting draws the cell back to EXACTLY the freezing point — the heat above freezing is " +
                "spent on the melt and no further");
            Assert.That(heatOut[0], Is.LessThan(0.2f - 1e-2f), "…so the cell genuinely cooled");
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
        // and that the defaults are still sanitized per inverse cycle when building them (CP8a).
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

            // CP8a runtime wiring: the clamp floor must be MIN temperature, and the heat field must be
            // initialised to NEUTRAL. Getting either wrong is silent — the kernel would be correct while
            // the runtime freezes the world (the CP7c bug class).
            StringAssert.Contains("\"_MinTemperature\"", src,
                "FluidSolver must upload the min-temperature clamp floor to the thermal kernel");
            Assert.IsFalse(src.Contains("tc.SetFloat(\"_AmbientTemperature\""),
                "CP8a: the thermal kernel must NOT be clamped to ambient/neutral — that would make room " +
                "temperature the coldest attainable state and ice could never form");
            StringAssert.Contains("SanitizedNeutral()", src,
                "Heat transport target and ClearAll init must both use the sanitized neutral temperature");

            // CP8a: the default rule set must be sanitized PER INVERSE CYCLE, not with the old global
            // "freeze <= condense <= melt <= boil" ladder.
            //   water <-> ice   cycle:  freeze <= melt
            //   water <-> steam cycle:  condense <= boil   (independent of the ice cycle)
            StringAssert.Contains("Mathf.Max(freezeT, ctx.MeltThreshold)", src,
                "meltT must be sanitized to >= freezeT (the water<->ice inverse cycle)");
            StringAssert.Contains("Mathf.Max(condenseT, ctx.BoilThreshold)", src,
                "boilT must be sanitized to >= condenseT (the water<->steam inverse cycle)");

            // condense must be seeded INDEPENDENTLY of the freeze/melt cycle. The room-temperature layout
            // requires condense (.65) ABOVE melt (.15 in shipped CP8j), so any coupling of condense to
            // freeze would clamp it back down and silently destroy water stability at neutral.
            StringAssert.Contains("Mathf.Max(0f, ctx.CondenseThreshold)", src,
                "condenseT must be seeded from 0, independent of the freeze/melt cycle");
            Assert.IsFalse(src.Contains("Mathf.Max(freezeT, ctx.CondenseThreshold)"),
                "CP8a REGRESSION: condenseT must NOT be coupled to freezeT — that reinstates the global " +
                "ladder and forces condense <= melt, which breaks room-temperature water stability");
            Assert.IsFalse(src.Contains("Mathf.Max(condenseT, ctx.MeltThreshold)"),
                "CP8a REGRESSION: meltT must NOT be coupled to condenseT — melt belongs to the ice cycle");

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

        // 27a. COLD: negative source must not drain the destination, nor invert the heat release, nor
        // (CP8g) invert the ice-formation COOLING into heating.
        // Isolated to a freeze-only rule so the negative water reaches the freeze source directly
        // (under the default rules, condense's destination-write clamps water to 0 first, which would
        // mask the bug entirely).
        //   PRE-FIX: conv = -0.1  =>  ice 0.4 -> 0.3,  heat 0.1 -> 0.1 + (-0.1*0.5) - (-0.1*0.2) = 0.07
        //   POST-FIX: conv = 0    =>  ice 0.4,         heat 0.1,   water clamped to 0
        //
        // heatCost (0.2) MUST DIFFER from heatRelease (0.5). With a negative conv the two terms have
        // opposite signs, so if they were equal they would cancel EXACTLY and the heat assertion would
        // pass on the broken kernel — a vacuous regression. The asymmetry is what gives the pre-fix
        // path an observable 0.03 deviation.
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
                    regime = ThermalRegime.Cold, threshold = 0.2f, rate = 1f,
                    heatRelease = 0.5f, heatCost = 0.2f
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
