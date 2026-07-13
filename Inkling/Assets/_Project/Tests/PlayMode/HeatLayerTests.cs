using System.Collections;
using System.Runtime.InteropServices;
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
    /// CP1 functional tests for the separate scalar heat layer (heat is its OWN ping-pong RT,
    /// NOT an iparticle channel). Covers allocation/format, the two-sided ClearAll lifecycle,
    /// and the AdvectHeat kernel's decay-toward-ambient behaviour (retention 1 preserves,
    /// retention 0 cools to ambient).
    /// </summary>
    public class HeatLayerTests
    {
        // Reads the .r channel of the center texel of a (single-channel) RenderTexture.
        private static float ReadCenterR(RenderTexture rt)
        {
            int cx = rt.width / 2;
            int cy = rt.height / 2;
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex.GetPixel(cx, cy).r;
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
            }
        }

        // Reads a full RGBA pixel at (x,y) of a RenderTexture.
        private static Color ReadPixelColor(RenderTexture rt, int x, int y)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex.GetPixel(x, y);
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
            }
        }

        private static void FillRT(RenderTexture rt, Color color)
        {
            var prev = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                GL.Clear(true, true, color);
            }
            finally
            {
                RenderTexture.active = prev;
            }
        }

#if UNITY_EDITOR
        // Dispatches AddHeatSources on a 1-cell grid and returns the resulting center heat.
        // Uses the raw iparticle float layout (stride 56); fire is field index 0.
        // minTemp defaults to 0 so the original CP3 expectations (no-fire cell stays at 0) hold.
        private static float DispatchAddHeatSources(float fire, float heat0, int enable, float rate, float dt,
            float maxHeat, float minTemp = 0f)
        {
            const int res = 1;
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.inktools.sim/Compute/Fluids.compute");
            Assert.IsNotNull(cs, "Fluids.compute should load");
            int kernel = cs.FindKernel("AddHeatSources");

            var particle = new float[14];
            particle[0] = fire; // fire = iparticle field index 0

            var buf = new ComputeBuffer(res * res, 56);
            var hr = new RenderTexture(res, res, 0, RenderTextureFormat.RHalf) { enableRandomWrite = true }; hr.Create();
            var hw = new RenderTexture(res, res, 0, RenderTextureFormat.RHalf) { enableRandomWrite = true }; hw.Create();
            try
            {
                buf.SetData(particle);
                FillRT(hr, new Color(heat0, 0f, 0f, 0f));
                FillRT(hw, Color.clear);

                cs.SetVector("_SimulationSize", new Vector2(res, res));
                cs.SetFloat("_FrameDeltaTime", dt);
                cs.SetInt("_EnableHeatSources", enable);
                cs.SetFloat("_FireHeatEmissionRate", rate);
                // Upload the FULL clamp range, never just the ceiling. Compute-shader uniforms persist
                // on the shared ComputeShader asset between dispatches, so omitting _MinTemperature made
                // this helper inherit whatever floor a previously-run test had set (the CP8b stamp tests
                // set 0.1), silently clamping the no-fire case up off zero. Always set both bounds.
                cs.SetFloat("_MinTemperature", minTemp);
                cs.SetFloat("_MaxHeat", maxHeat);
                cs.SetBuffer(kernel, "_ParticlesRead", buf);
                cs.SetTexture(kernel, "_HeatRead", hr);
                cs.SetTexture(kernel, "_HeatWrite", hw);
                cs.Dispatch(kernel, 1, 1, 1);

                return ReadCenterR(hw);
            }
            finally
            {
                // Never release an RT that is still bound as the active render target.
                RenderTexture.active = null;
                buf.Release();
                hr.Release();
                hw.Release();
                Object.DestroyImmediate(hr);
                Object.DestroyImmediate(hw);
            }
        }

        // Creates an RT seeded from a per-cell array (index y*res+x) in the .r channel.
        private static RenderTexture MakeSeededRT(int res, RenderTextureFormat fmt, float[] seed)
        {
            var rt = new RenderTexture(res, res, 0, fmt) { enableRandomWrite = true };
            rt.Create();
            var t = new Texture2D(res, res, TextureFormat.RGBAFloat, false);

            // Graphics.Blit SETS RenderTexture.active to its destination and LEAVES it set. If we don't
            // restore it, `active` stays pointing at this RT — ReadAllR then captures that stale value
            // as its "previous" target and dutifully restores it, so the RT is still active when the
            // caller Release()s it. That is what produced the repeated
            // "Releasing render texture that is set to be RenderTexture.active!" warnings.
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

        // Reads the .r channel of every cell into a per-cell array (index y*res+x).
        private static float[] ReadAllR(RenderTexture rt, int res)
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

        // Dispatch AdvectHeat over a grid with an obstacle mask; returns the resulting heat grid.
        // CP8a: transport now clamps every write to [minTemp, maxHeat]. `ambient` is the NEUTRAL
        // relaxation target and is deliberately NOT the floor — sub-neutral temperatures are valid.
        private static float[] DispatchAdvectHeatGrid(float[] heat, float[] obstacle, Vector2 vel,
            int res, float dt, float dissipation, float ambient,
            float minTemp = 0f, float maxHeat = 1f)
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.inktools.sim/Compute/Fluids.compute");
            int kernel = cs.FindKernel("AdvectHeat");

            var hr = MakeSeededRT(res, RenderTextureFormat.RHalf, heat);
            var hw = MakeSeededRT(res, RenderTextureFormat.RHalf, new float[res * res]);
            var obs = MakeSeededRT(res, RenderTextureFormat.RFloat, obstacle);
            var velRT = new RenderTexture(res, res, 0, RenderTextureFormat.RGHalf) { enableRandomWrite = true };
            velRT.Create();
            try
            {
                FillRT(velRT, new Color(vel.x, vel.y, 0f, 0f));
                cs.SetVector("_SimulationSize", new Vector2(res, res));
                cs.SetFloat("_FrameDeltaTime", dt);
                cs.SetFloat("_ThermalDissipation", dissipation);
                cs.SetFloat("_AmbientTemperature", ambient);
                cs.SetFloat("_MinTemperature", minTemp);
                cs.SetFloat("_MaxHeat", maxHeat);
                cs.SetTexture(kernel, "_VelocityRead", velRT);
                cs.SetTexture(kernel, "_HeatRead", hr);
                cs.SetTexture(kernel, "_HeatWrite", hw);
                cs.SetTexture(kernel, "_ObstacleRead", obs);
                int g = Mathf.CeilToInt(res / 8f);
                cs.Dispatch(kernel, g, g, 1);
                return ReadAllR(hw, res);
            }
            finally
            {
                // Never release an RT that is still bound as the active render target.
                RenderTexture.active = null;
                hr.Release(); hw.Release(); obs.Release(); velRT.Release();
                Object.DestroyImmediate(hr); Object.DestroyImmediate(hw);
                Object.DestroyImmediate(obs); Object.DestroyImmediate(velRT);
            }
        }

        // Dispatch DiffuseHeat over a grid with an obstacle mask; returns the resulting heat grid.
        private static float[] DispatchDiffuseHeatGrid(float[] heat, float[] obstacle, int res, float diffusion,
            float minTemp = 0f, float maxHeat = 1f)
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.inktools.sim/Compute/Fluids.compute");
            int kernel = cs.FindKernel("DiffuseHeat");

            var hr = MakeSeededRT(res, RenderTextureFormat.RHalf, heat);
            var hw = MakeSeededRT(res, RenderTextureFormat.RHalf, new float[res * res]);
            var obs = MakeSeededRT(res, RenderTextureFormat.RFloat, obstacle);
            try
            {
                cs.SetVector("_SimulationSize", new Vector2(res, res));
                cs.SetFloat("_ThermalDiffusion", diffusion);
                cs.SetFloat("_MinTemperature", minTemp);
                cs.SetFloat("_MaxHeat", maxHeat);
                cs.SetTexture(kernel, "_HeatRead", hr);
                cs.SetTexture(kernel, "_HeatWrite", hw);
                cs.SetTexture(kernel, "_ObstacleRead", obs);
                int g = Mathf.CeilToInt(res / 8f);
                cs.Dispatch(kernel, g, g, 1);
                return ReadAllR(hw, res);
            }
            finally
            {
                // Never release an RT that is still bound as the active render target.
                RenderTexture.active = null;
                hr.Release(); hw.Release(); obs.Release();
                Object.DestroyImmediate(hr); Object.DestroyImmediate(hw); Object.DestroyImmediate(obs);
            }
        }
#endif

        // 1. Allocate creates ctx.Heat as an RHalf ping-pong buffer.
        [UnityTest]
        public IEnumerator Allocate_CreatesHeatLayer_AsRHalf()
        {
            var ctx = new SimulationContext { Resolution = 32 };
            var resources = new SimulationResources();
            try
            {
                resources.Allocate(ctx);
                yield return null;

                Assert.IsNotNull(ctx.Heat, "Heat ping-pong buffer should be allocated");
                Assert.IsNotNull(ctx.Heat.Read, "Heat.Read RT should exist");
                Assert.IsNotNull(ctx.Heat.Write, "Heat.Write RT should exist");
                Assert.AreEqual(RenderTextureFormat.RHalf, ctx.Heat.Read.format, "Heat should be RHalf");
                Assert.AreEqual(RenderTextureFormat.RHalf, ctx.Heat.Format, "Heat ping-pong format should be RHalf");
            }
            finally
            {
                resources.Dispose(ctx);
            }
        }

        // 2. PingPongRenderTexture.Clear(Color) zeroes BOTH Read and Write sides.
        [UnityTest]
        public IEnumerator HeatClear_ClearsBothPingPongSides()
        {
            var ctx = new SimulationContext { Resolution = 32 };
            var resources = new SimulationResources();
            try
            {
                resources.Allocate(ctx);
                yield return null;

                // Seed DIFFERENT non-zero values on each side to prove both are cleared.
                FillRT(ctx.Heat.Read, new Color(0.7f, 0f, 0f, 0f));
                FillRT(ctx.Heat.Write, new Color(0.3f, 0f, 0f, 0f));
                Assert.Greater(ReadCenterR(ctx.Heat.Read), 0.5f, "Read side should be seeded non-zero");
                Assert.Greater(ReadCenterR(ctx.Heat.Write), 0.2f, "Write side should be seeded non-zero");

                ctx.Heat.Clear(Color.clear);

                Assert.That(ReadCenterR(ctx.Heat.Read), Is.EqualTo(0f).Within(1e-3f), "Read side must be cleared to 0");
                Assert.That(ReadCenterR(ctx.Heat.Write), Is.EqualTo(0f).Within(1e-3f), "Write side must be cleared to 0");
            }
            finally
            {
                resources.Dispose(ctx);
            }
        }

        // 3. FluidSolver.ClearAll() clears both heat ping-pong sides.
        // CP8a: ClearAll must init the heat field to the NEUTRAL (room) temperature, not zero.
        // Clearing to 0 puts every cell below freezeThreshold, so the first frame after a reset would
        // flash-freeze all water before the end-of-pass clamp could rescue it.
        //
        // The neutral value used here is deliberately NON-DEFAULT (0.42, not 0.5) so this test cannot
        // pass against a hardcoded constant — it proves ClearAll reads the configured neutral.
        [UnityTest]
        public IEnumerator FluidSolverClearAll_InitsBothHeatSidesToNeutral()
        {
#if UNITY_EDITOR
            const float customNeutral = 0.42f;
            var ctx = new SimulationContext
            {
                Resolution = 32,
                NeutralTemperature = customNeutral,
                MinTemperature = 0f,
                MaxHeat = 1f,
            };
            var resources = new SimulationResources();
            try
            {
                resources.Allocate(ctx);
                yield return null;

                ctx.FluidCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/com.inktools.sim/Compute/Fluids.compute");
                Assert.IsNotNull(ctx.FluidCompute, "Fluids.compute should load from the com.inktools.sim package");

                var opQueue = new OperationQueue(ctx);
                var solver = new FluidSolver(ctx, opQueue);
                bool kernelsOk = solver.InitializeKernels();
                Assert.IsTrue(kernelsOk, "Fluid kernels should initialize");

                // Seed both heat sides with values that are neither zero nor the neutral.
                FillRT(ctx.Heat.Read, new Color(0.9f, 0f, 0f, 0f));
                FillRT(ctx.Heat.Write, new Color(0.1f, 0f, 0f, 0f));

                solver.ClearAll();
                yield return null;

                Assert.That(ReadCenterR(ctx.Heat.Read), Is.EqualTo(customNeutral).Within(3e-3f),
                    "ClearAll must init Heat.Read to the configured neutral temperature (not 0)");
                Assert.That(ReadCenterR(ctx.Heat.Write), Is.EqualTo(customNeutral).Within(3e-3f),
                    "ClearAll must init Heat.Write to the configured neutral temperature (not 0)");
            }
            finally
            {
                resources.Dispose(ctx);
            }
#else
            yield break;
#endif
        }

        // 4. AdvectHeat: zero velocity + retention 1 preserves heat; retention 0 decays to ambient (0).
        [UnityTest]
        public IEnumerator AdvectHeat_PreservesWithRetention1_DecaysWithRetention0()
        {
#if UNITY_EDITOR
            const int res = 4;
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.inktools.sim/Compute/Fluids.compute");
            Assert.IsNotNull(cs, "Fluids.compute should load");
            int kernel = cs.FindKernel("AdvectHeat");

            RenderTexture MakeR(RenderTextureFormat fmt)
            {
                var rt = new RenderTexture(res, res, 0, fmt) { enableRandomWrite = true };
                rt.Create();
                return rt;
            }

            var velocity = MakeR(RenderTextureFormat.RGHalf);   // zero velocity
            var heatRead = MakeR(RenderTextureFormat.RHalf);
            var heatWrite = MakeR(RenderTextureFormat.RHalf);
            var obstacle = MakeR(RenderTextureFormat.RFloat);   // no obstacles (CP4: AdvectHeat reads it)
            try
            {
                FillRT(velocity, Color.clear);
                FillRT(heatWrite, Color.clear);
                FillRT(obstacle, Color.clear);
                // Seed center texel (res/2, res/2) = 0.5 on the read side.
                FillRT(heatRead, Color.clear);
                var prev = RenderTexture.active;
                var seed = new Texture2D(res, res, TextureFormat.RGBAFloat, false);
                try
                {
                    for (int y = 0; y < res; y++)
                        for (int x = 0; x < res; x++)
                            seed.SetPixel(x, y, Color.clear);
                    seed.SetPixel(res / 2, res / 2, new Color(0.5f, 0f, 0f, 0f));
                    seed.Apply();
                    Graphics.Blit(seed, heatRead);
                }
                finally
                {
                    RenderTexture.active = prev;
                    Object.DestroyImmediate(seed);
                }

                cs.SetVector("_SimulationSize", new Vector2(res, res));
                cs.SetFloat("_FrameDeltaTime", 0.016f);
                cs.SetFloat("_AmbientTemperature", 0f);

                int groups = Mathf.CeilToInt(res / 8f);

                // --- retention = 1 -> heat preserved ---
                cs.SetFloat("_ThermalDissipation", 1f);
                cs.SetTexture(kernel, "_VelocityRead", velocity);
                cs.SetTexture(kernel, "_HeatRead", heatRead);
                cs.SetTexture(kernel, "_HeatWrite", heatWrite);
                cs.SetTexture(kernel, "_ObstacleRead", obstacle);
                cs.Dispatch(kernel, groups, groups, 1);
                yield return null;

                float preserved = ReadCenterR(heatWrite);
                Assert.That(preserved, Is.EqualTo(0.5f).Within(2e-2f),
                    "Zero velocity + retention 1 should preserve center heat ~0.5");

                // --- retention = 0 -> heat decays to ambient (0) ---
                cs.SetFloat("_ThermalDissipation", 0f);
                cs.SetTexture(kernel, "_VelocityRead", velocity);
                cs.SetTexture(kernel, "_HeatRead", heatRead);
                cs.SetTexture(kernel, "_HeatWrite", heatWrite);
                cs.SetTexture(kernel, "_ObstacleRead", obstacle);
                cs.Dispatch(kernel, groups, groups, 1);
                yield return null;

                float decayed = ReadCenterR(heatWrite);
                Assert.That(decayed, Is.EqualTo(0f).Within(1e-3f),
                    "Retention 0 should decay center heat to ambient (0)");
            }
            finally
            {
                // Never release an RT that is still bound as the active render target.
                RenderTexture.active = null;
                velocity.Release();
                heatRead.Release();
                heatWrite.Release();
                obstacle.Release();
                Object.DestroyImmediate(velocity);
                Object.DestroyImmediate(heatRead);
                Object.DestroyImmediate(heatWrite);
                Object.DestroyImmediate(obstacle);
            }
#else
            yield break;
#endif
        }

        // 5. ParticleChannelSplat packs heat into _Channels2.z while electricity stays in x/y and w=0.
        [UnityTest]
        public IEnumerator ChannelSplat_PacksHeatIntoChannels2Z()
        {
#if UNITY_EDITOR
            const int res = 1;
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Rendering/ParticleChannelSplat.compute");
            Assert.IsNotNull(cs, "ParticleChannelSplat.compute should load");
            int kernel = cs.FindKernel("ChannelSplat");

            var particles = new iparticle[res * res];
            particles[0].electricitySeeded = 0.3f;
            particles[0].electricityGrown = 0.6f;

            var buf = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            var heat = new RenderTexture(res, res, 0, RenderTextureFormat.RHalf) { enableRandomWrite = true };
            heat.Create();

            RenderTexture MakeCh()
            {
                var rt = new RenderTexture(res, res, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true };
                rt.Create();
                return rt;
            }
            var ch0 = MakeCh();
            var ch1 = MakeCh();
            var ch2 = MakeCh();
            try
            {
                buf.SetData(particles);
                FillRT(heat, new Color(0.5f, 0f, 0f, 0f)); // seed heat = 0.5 everywhere (res 1)

                cs.SetInt("_Resolution", res);
                cs.SetBuffer(kernel, "_ParticlesRead", buf);
                cs.SetTexture(kernel, "_HeatRead", heat);
                cs.SetTexture(kernel, "_Channels0", ch0);
                cs.SetTexture(kernel, "_Channels1", ch1);
                cs.SetTexture(kernel, "_Channels2", ch2);
                cs.Dispatch(kernel, 1, 1, 1);
                yield return null;

                Color c2 = ReadPixelColor(ch2, 0, 0);
                Assert.That(c2.r, Is.EqualTo(0.3f).Within(2e-2f), "Channels2.x should carry electricitySeeded");
                Assert.That(c2.g, Is.EqualTo(0.6f).Within(2e-2f), "Channels2.y should carry electricityGrown");
                Assert.That(c2.b, Is.EqualTo(0.5f).Within(2e-2f), "Channels2.z should carry heat");
                Assert.That(c2.a, Is.EqualTo(0f).Within(1e-3f), "Channels2.w is reserved and must stay 0");
            }
            finally
            {
                // Never release an RT that is still bound as the active render target.
                RenderTexture.active = null;
                buf.Release();
                heat.Release();
                ch0.Release();
                ch1.Release();
                ch2.Release();
                Object.DestroyImmediate(heat);
                Object.DestroyImmediate(ch0);
                Object.DestroyImmediate(ch1);
                Object.DestroyImmediate(ch2);
            }
#else
            yield break;
#endif
        }

        // 6. The gradient shader exposes a Heat debug mode (source-level assertion; a full rendered
        // shader test is brittle, so verify the keyword/enum exist and compile clean via the Editor).
        [Test]
        public void GradientShader_HasHeatDebugMode()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Scripts/Systems/Rendering/InkGradientRenderer.shader";
            string src = System.IO.File.ReadAllText(path);
            StringAssert.Contains("_DEBUGMODE_HEAT", src,
                "Heat debug keyword must be in the _DebugMode multi_compile list");
            StringAssert.Contains("PlantBoth, Heat", src,
                "Heat must be the last entry in the _DebugMode KeywordEnum");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // 7. AddHeatSources: fire emits heat, non-fire stays inert, output clamps to _MaxHeat, and
        // a disabled gate passes existing heat through unchanged.
        [UnityTest]
        public IEnumerator AddHeatSources_FireEmits_NonFireInert_ClampsAndGates()
        {
#if UNITY_EDITOR
            // Fire cell: 0 + fire(0.5) * rate(1) * dt(1) = 0.5
            float fired = DispatchAddHeatSources(0.5f, 0f, 1, 1f, 1f, 1f);
            Assert.That(fired, Is.EqualTo(0.5f).Within(2e-2f), "Fire should emit heat (~0.5)");

            // Non-fire cell must not heat itself.
            float noFire = DispatchAddHeatSources(0f, 0f, 1, 1f, 1f, 1f);
            Assert.That(noFire, Is.EqualTo(0f).Within(1e-3f), "Non-fire cell must stay at 0");

            // Clamp: 0 + 1 * 10 * 1 = 10, clamped to _MaxHeat = 0.3
            float clamped = DispatchAddHeatSources(1f, 0f, 1, 10f, 1f, 0.3f);
            Assert.That(clamped, Is.EqualTo(0.3f).Within(2e-2f), "Heat must clamp to _MaxHeat");

            // Disabled: seeded heat 0.25 passes through unchanged despite fire present.
            float disabled = DispatchAddHeatSources(1f, 0.25f, 0, 5f, 1f, 1f);
            Assert.That(disabled, Is.EqualTo(0.25f).Within(2e-2f), "Disabled sources must pass heat through");

            // CP8a floor: the kernel clamps on EVERY path, so a below-floor value is lifted to
            // _MinTemperature — even with no fire and sources enabled.
            float lifted = DispatchAddHeatSources(0f, 0f, 1, 1f, 1f, 1f, minTemp: 0.2f);
            Assert.That(lifted, Is.EqualTo(0.2f).Within(2e-2f),
                "AddHeatSources must clamp up to _MinTemperature on the no-fire path too");

            // UNIFORM-LEAK GUARD: compute-shader uniforms persist on the shared ComputeShader asset
            // between dispatches. Re-running the no-fire case with an explicit floor of 0 must return 0,
            // NOT the 0.2 floor left behind by the dispatch above. This is exactly the leak that made
            // this test fail once the CP8b stamp tests (which set _MinTemperature = 0.1) ran before it.
            float afterLeak = DispatchAddHeatSources(0f, 0f, 1, 1f, 1f, 1f, minTemp: 0f);
            Assert.That(afterLeak, Is.EqualTo(0f).Within(1e-3f),
                "Helper must upload _MinTemperature every dispatch — a stale floor from a prior test must not leak in");

            yield return null;
#else
            yield break;
#endif
        }

        // ── CP8b: injection heat stamping ────────────────────────────────────────────────────

#if UNITY_EDITOR
        // Dispatch StampInjectionHeat: writes `target` into the heat field with the injection's own
        // gaussian falloff, clamped to [minTemp, maxHeat]. Cells outside the radius pass through.
        private static float[] DispatchStampInjectionHeat(float[] heat, int res,
            Vector2 centerPixel, float radius, float target, float minTemp, float maxHeat)
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.inktools.sim/Compute/Fluids.compute");
            Assert.IsNotNull(cs, "Fluids.compute should load");
            int kernel = cs.FindKernel("StampInjectionHeat");

            var hr = MakeSeededRT(res, RenderTextureFormat.RHalf, heat);
            var hw = MakeSeededRT(res, RenderTextureFormat.RHalf, new float[res * res]);
            try
            {
                cs.SetVector("_SimulationSize", new Vector2(res, res));
                cs.SetVector("_ForcePosition", centerPixel);
                cs.SetFloat("_ForceRadius", radius);
                cs.SetFloat("_InjectionTargetHeat", target);
                cs.SetFloat("_MinTemperature", minTemp);
                cs.SetFloat("_MaxHeat", maxHeat);
                cs.SetTexture(kernel, "_HeatRead", hr);
                cs.SetTexture(kernel, "_HeatWrite", hw);
                int g = Mathf.CeilToInt(res / 8f);
                cs.Dispatch(kernel, g, g, 1);
                return ReadAllR(hw, res);
            }
            finally
            {
                RenderTexture.active = null;
                hr.Release(); hw.Release();
                Object.DestroyImmediate(hr); Object.DestroyImmediate(hw);
            }
        }
#endif

        // Ice must stamp the FLOOR — this is the user-reported bug: painting ice never dropped the
        // temperature below the baseline. The centre must land exactly on minTemperature (sub-neutral),
        // and a far cell must be untouched.
        [UnityTest]
        public IEnumerator StampInjectionHeat_Ice_StampsMinTemperature_SubNeutral()
        {
#if UNITY_EDITOR
            const int res = 8;
            const float minTemp = 0.1f, maxHeat = 0.9f, neutral = 0.5f;

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = neutral;   // world at room temperature

            float[] outp = DispatchStampInjectionHeat(heat, res,
                centerPixel: new Vector2(4f, 4f), radius: 3f, target: minTemp,
                minTemp: minTemp, maxHeat: maxHeat);
            yield return null;

            int centre = 4 * res + 4;
            Assert.That(outp[centre], Is.EqualTo(minTemp).Within(2e-2f),
                "Ice injection must stamp the MINIMUM temperature at the centre");
            Assert.That(outp[centre], Is.LessThan(neutral - 2e-2f),
                "…which is genuinely sub-neutral — that is the whole point of the fix");
            Assert.That(outp[0], Is.EqualTo(neutral).Within(2e-2f),
                "A cell outside the injection radius must pass through unchanged");
#else
            yield break;
#endif
        }

        [UnityTest]
        public IEnumerator StampInjectionHeat_Fire_StampsMaxTemperature()
        {
#if UNITY_EDITOR
            const int res = 8;
            const float minTemp = 0.1f, maxHeat = 0.9f, neutral = 0.5f;

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = neutral;

            float[] outp = DispatchStampInjectionHeat(heat, res,
                centerPixel: new Vector2(4f, 4f), radius: 3f, target: maxHeat,
                minTemp: minTemp, maxHeat: maxHeat);
            yield return null;

            Assert.That(outp[4 * res + 4], Is.EqualTo(maxHeat).Within(2e-2f),
                "Fire injection must stamp the MAXIMUM temperature at the centre");
            Assert.That(outp[0], Is.EqualTo(neutral).Within(2e-2f), "Outside the radius: unchanged");
#else
            yield break;
#endif
        }

        [UnityTest]
        public IEnumerator StampInjectionHeat_Water_StampsNeutral_AndClampsOutOfRangeTarget()
        {
#if UNITY_EDITOR
            const int res = 8;
            const float minTemp = 0.1f, maxHeat = 0.9f, neutral = 0.5f;

            // Start the world COLD, then paint water: it must be pulled up to the neutral baseline.
            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = minTemp;

            float[] outp = DispatchStampInjectionHeat(heat, res,
                centerPixel: new Vector2(4f, 4f), radius: 3f, target: neutral,
                minTemp: minTemp, maxHeat: maxHeat);
            yield return null;

            Assert.That(outp[4 * res + 4], Is.EqualTo(neutral).Within(2e-2f),
                "Water injection must stamp the NEUTRAL baseline at the centre");
            Assert.That(outp[0], Is.EqualTo(minTemp).Within(2e-2f), "Outside the radius: unchanged");

            // An out-of-range target must still be clamped into [min, max].
            float[] clamped = DispatchStampInjectionHeat(heat, res,
                centerPixel: new Vector2(4f, 4f), radius: 3f, target: 5f,
                minTemp: minTemp, maxHeat: maxHeat);
            Assert.That(clamped[4 * res + 4], Is.EqualTo(maxHeat).Within(2e-2f),
                "An out-of-range stamp target must clamp to maxHeat");
#else
            yield break;
#endif
        }

        // ── CP8a: heat TRANSPORT must keep the field inside [minTemp, maxHeat] ───────────────
        // With thermal interactions DISABLED, these transport kernels are the ONLY writers of the heat
        // field — nothing downstream clamps. Pre-fix they wrote unclamped, so an out-of-range value
        // (from a seed, a stale buffer, or a knob change) would persist forever.
        //
        // The floor is minTemp, NOT the neutral: a valid sub-neutral temperature must survive transport
        // untouched, or ice could never form.

        [UnityTest]
        public IEnumerator AdvectHeat_ClampsToRange_ButPreservesSubNeutral()
        {
#if UNITY_EDITOR
            const int res = 3;
            const float minTemp = 0.1f, maxHeat = 0.9f, neutral = 0.5f;

            // Zero velocity + retention 1 => transport is a pure copy, so ONLY the clamp can change a value.
            var heat = new float[res * res];
            heat[0] = -0.5f;   // below min  => must clamp UP to 0.1
            heat[1] = 1.5f;    // above max  => must clamp DOWN to 0.9
            heat[2] = 0.25f;   // sub-neutral but IN range => must be preserved exactly
            var open = new float[res * res];

            float[] outp = DispatchAdvectHeatGrid(heat, open, Vector2.zero, res,
                dt: 1f, dissipation: 1f, ambient: neutral, minTemp: minTemp, maxHeat: maxHeat);
            yield return null;

            Assert.That(outp[0], Is.EqualTo(minTemp).Within(2e-2f), "Below-min must clamp up to minTemperature");
            Assert.That(outp[1], Is.EqualTo(maxHeat).Within(2e-2f), "Above-max must clamp down to maxHeat");
            Assert.That(outp[2], Is.EqualTo(0.25f).Within(2e-2f),
                "A valid sub-neutral temperature must survive transport (neutral is NOT the floor)");
            Assert.That(outp[2], Is.LessThan(neutral - 2e-2f), "…and must remain sub-neutral");
#else
            yield break;
#endif
        }

        [UnityTest]
        public IEnumerator DiffuseHeat_ClampsToRange_ButPreservesSubNeutral()
        {
#if UNITY_EDITOR
            const int res = 3;
            const float minTemp = 0.1f, maxHeat = 0.9f, neutral = 0.5f;

            // diffusion = 0 => output is a pure copy of the centre, so ONLY the clamp can change a value.
            var heat = new float[res * res];
            heat[0] = -0.5f;   // below min
            heat[1] = 1.5f;    // above max
            heat[2] = 0.25f;   // sub-neutral, in range
            var open = new float[res * res];

            float[] outp = DispatchDiffuseHeatGrid(heat, open, res,
                diffusion: 0f, minTemp: minTemp, maxHeat: maxHeat);
            yield return null;

            Assert.That(outp[0], Is.EqualTo(minTemp).Within(2e-2f), "Below-min must clamp up to minTemperature");
            Assert.That(outp[1], Is.EqualTo(maxHeat).Within(2e-2f), "Above-max must clamp down to maxHeat");
            Assert.That(outp[2], Is.EqualTo(0.25f).Within(2e-2f),
                "A valid sub-neutral temperature must survive diffusion (neutral is NOT the floor)");
            Assert.That(outp[2], Is.LessThan(neutral - 2e-2f), "…and must remain sub-neutral");
#else
            yield break;
#endif
        }

        // CP8b: the kernel above can be perfect while the runtime never dispatches it — the CP7c bug
        // class. Pin that OperationQueue stamps injection heat on BOTH injection paths (batched and
        // fallback), uploads the clamp bounds itself (ProcessPending runs BEFORE FluidSolver.Step's
        // SetConstants, so the uniforms would otherwise be stale/zero), and swaps the heat ping-pong.
        [Test]
        public void OperationQueue_StampsInjectionHeat_OnBothInjectionPaths()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Scripts/Systems/SimulationLOD0/Core/OperationQueue.cs";
            string src = System.IO.File.ReadAllText(path);

            StringAssert.Contains("StampInjectionHeat", src,
                "OperationQueue must dispatch the injection heat stamp");
            StringAssert.Contains("TryGetInjectionTemperature", src,
                "Ink -> temperature mapping must come from the context (Fire=max, Water=neutral, Ice=min)");
            StringAssert.Contains("\"_InjectionTargetHeat\"", src, "must upload the stamp target");
            StringAssert.Contains("\"_MinTemperature\"", src,
                "queue must upload the clamp floor itself — SetConstants has not run yet");
            StringAssert.Contains("ctx.Heat.Swap()", src,
                "heat ping-pong must be swapped so the next pass reads the stamped temperature");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // CP8b: fire must NOT regain a free continuous heat source. CP7b/CP7d deliberately skip the
        // legacy AddHeatSources pass when thermal interactions own fire->heat emission (with fuel cost).
        [Test]
        public void CP8b_DoesNotRevive_FreeContinuousFireHeat()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Scripts/Systems/SimulationLOD0/Core/FluidSolver.cs";
            string src = System.IO.File.ReadAllText(path);
            StringAssert.Contains("!ctx.EnableThermalInteractions", src,
                "The AddHeatSources double-source guard must remain: injection stamping is a one-shot " +
                "initial condition, NOT a revival of free per-frame fire heat");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // CP8d: the shipped scene must actually be in thermal steam mode, with the CP8 NEUTRAL layout.
        // The prior playtest config had condenseThreshold 0.2 with neutral 0.5 — meaning steam needed to
        // be BELOW 0.2 to condense, so at room temperature steam never condensed and accumulated forever.
        // Steam mode was wired but physically dead. These assertions pin the corrected layout.
        [Test]
        public void MainScene_IsInThermalSteamMode_WithNeutralLayout()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Scenes/Main.unity";
            string src = System.IO.File.ReadAllText(path);

            StringAssert.Contains("enableThermalInteractions: 1", src, "Thermal (CP5/CP8) mode must be ON");
            StringAssert.Contains("useInkInteractions: 1", src, "Organic ink interactions stay ON");

            // Legacy adjacency ThermalGroup must be out of the active list; Organic groups stay.
            Assert.IsFalse(src.Contains("guid: bb5975a015651cc47aab80f9ac703167"),
                "Legacy ThermalGroup must be removed from the scene's affinityGroups (asset itself is kept on disk)");
            StringAssert.Contains("guid: da76efa99d9a5cf4aadca3f811d21554", src, "OrganicGroup must remain");
            StringAssert.Contains("guid: 3892ae7ecffb33f4cad3ec4e410eee4c", src, "OrganicGroup2 must remain");

            // CP8e: the contact-quench group must be ACTIVE. Fire+Water annihilation lives in the
            // pairwise product matrix, not the thermal pass, and neither Organic group can express it
            // (OrganicGroup has Fire/Water but no Steam slot; OrganicGroup2 has Steam but no Fire/Water).
            // Without this group in the scene there is no quench at all and fire spreads through water.
            StringAssert.Contains("guid: 7f3c1a9e5b204d64a8e1c6f0d29b4e73", src,
                "ContactReactionsGroup must be in the scene's affinityGroups, or Fire+Water never quenches");

            // CP8 neutral layout: freeze <= melt < neutral < condense <= boil.
            StringAssert.Contains("neutralTemperature: 0.5", src);
            StringAssert.Contains("freezeThreshold: 0.15", src);
            StringAssert.Contains("meltThreshold: 0.35", src);
            StringAssert.Contains("condenseThreshold: 0.65", src,
                "Condense must sit ABOVE neutral, or steam never condenses at room temperature");
            StringAssert.Contains("boilThreshold: 0.85", src);

            // Conduction must be ON, or fire/ice cannot influence the temperature around them at all.
            // CP8e raised this from 0.05, which read as almost no conduction on screen.
            Assert.IsFalse(src.Contains("thermalDiffusion: 0\n") || src.Contains("thermalDiffusion: 0\r\n"),
                "thermalDiffusion must be non-zero so heat actually conducts");
            StringAssert.Contains("thermalDiffusion: 0.2", src);

            // CP8e: spontaneous heat-only plant combustion must be a near-max, rare event. Everyday
            // fire spread is the legacy Fire x Plant CONTACT reaction, which this does not gate.
            StringAssert.Contains("plantIgnitionThreshold: 0.98", src,
                "Heat-only plant ignition must be near max heat, not merely 'hot'");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

#if UNITY_EDITOR
        // Reads "  eRC: <value>" out of an AffinityGroup YAML matrix block. Row = output slot,
        // column = pair index — the convention documented on AffinityGroup.productMatrix.
        private static float ReadMatrixCell(string yaml, string blockKey, int row, int col)
        {
            int block = yaml.IndexOf(blockKey + ":", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(block, 0, $"'{blockKey}' block missing from asset");

            var m = System.Text.RegularExpressions.Regex.Match(
                yaml.Substring(block), $@"e{row}{col}:\s*(-?[0-9.eE+-]+)");
            Assert.IsTrue(m.Success, $"{blockKey}.e{row}{col} missing");
            return float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
#endif

        // CP8e: Fire + Water annihilate ON CONTACT into Steam, so fire cannot spread freely through
        // water. This is CONTACT CHEMISTRY, not a phase change — it lives in the pairwise product
        // matrix and fires at ANY temperature, including well below boilThreshold. It could not be
        // added to either existing active group: productMatrix coefficients are per-slot WITHIN a
        // group, and OrganicGroup's slots have no Steam while OrganicGroup2's have no Fire/Water.
        [Test]
        public void ContactReactionsGroup_QuenchesFireAndWaterIntoSteam_Conservingly()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Inks/ContactReactionsGroup.asset";
            Assert.IsTrue(System.IO.File.Exists(path), "ContactReactionsGroup.asset must exist");
            string src = System.IO.File.ReadAllText(path);

            // Slot ORDER is load-bearing: col0 is the pair (slot0 x slot1), so Fire and Water must be
            // slots 0 and 1, and Steam must be a slot at all for the reaction to have anywhere to go.
            int fire  = src.IndexOf("b95f0ee9596be374186a08a2bcba1023", System.StringComparison.Ordinal);
            int water = src.IndexOf("2a479c9de68b35042b619687310e729a", System.StringComparison.Ordinal);
            int steam = src.IndexOf("84ea5c909487b764c934fad7c76e218d", System.StringComparison.Ordinal);
            int ice   = src.IndexOf("4d5cac36951b253469623e9d3dcf56dd", System.StringComparison.Ordinal);
            Assert.GreaterOrEqual(fire, 0, "slot 0 must be Fire");
            Assert.Greater(water, fire, "slot 1 must be Water (after Fire)");
            Assert.Greater(steam, water, "slot 2 must be Steam (after Water)");
            Assert.Greater(ice, steam, "slot 3 must be Ice (after Steam)");

            // Column 0 = pair 0x1 = Fire x Water. Row = output slot.
            float dFire  = ReadMatrixCell(src, "productMatrix", 0, 0);
            float dWater = ReadMatrixCell(src, "productMatrix", 1, 0);
            float dSteam = ReadMatrixCell(src, "productMatrix", 2, 0);
            float dIce   = ReadMatrixCell(src, "productMatrix", 3, 0);

            Assert.Less(dFire, 0f, "Quench must CONSUME fire — that is the whole point");
            Assert.Less(dWater, 0f, "Quench must CONSUME water");
            Assert.Greater(dSteam, 0f, "Quench must PRODUCE steam");
            Assert.AreEqual(0f, dIce, 1e-5f, "Quench must not touch ice");

            // CONSERVATION: a zero column-sum is what makes this event mass-conserving under
            // ApplyLimitedReactionEvent. A positive sum would MINT mass every step at a fire/water front.
            Assert.AreEqual(0f, dFire + dWater + dSteam + dIce, 1e-5f,
                "Fire x Water column must have zero sum, or the quench mints mass");

            // Every OTHER pair column must be inert: this group exists only to quench. In particular no
            // Water x Ice contact freezing yet — freezing stays thermal (cold conducting out of ice).
            for (int col = 1; col < 4; col++)
                for (int row = 0; row < 4; row++)
                    Assert.AreEqual(0f, ReadMatrixCell(src, "productMatrix", row, col), 1e-5f,
                        $"productMatrix column {col} must be zero — this group only quenches");

            StringAssert.Contains("productCol4: {x: 0, y: 0, z: 0, w: 0}", src, "pair 1x3 (Water x Ice) must be inert");
            StringAssert.Contains("productCol5: {x: 0, y: 0, z: 0, w: 0}", src, "pair 2x3 (Steam x Ice) must be inert");

            // No reaction MOTION in this slice — prove the chemistry before adding impulse.
            for (int col = 0; col < 4; col++)
                for (int row = 0; row < 4; row++)
                    Assert.AreEqual(0f, ReadMatrixCell(src, "reactionImpulseMatrix", row, col), 1e-5f,
                        "ContactReactionsGroup must not add reaction motion yet");
            StringAssert.Contains("reactionImpulseCol4: {x: 0, y: 0, z: 0, w: 0}", src);
            StringAssert.Contains("reactionImpulseCol5: {x: 0, y: 0, z: 0, w: 0}", src);

            // Thermal arrays MUST stay empty. ThermalRuleBaker replaces defaults per-category the moment
            // ANY active group authors a transition/source — authoring here would silently drop the
            // built-in condense/freeze/melt/boil/ignition defaults for the whole sim.
            StringAssert.Contains("thermalTransitions: []", src,
                "Authoring thermal rules here would replace the global default transitions");
            StringAssert.Contains("thermalSources: []", src,
                "Authoring thermal sources here would replace the global default sources");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // CP8b: lower the ice obstacle threshold so ice and inks overlap more.
        // Pins the AUTHORED ice obstacle threshold.
        //
        // NOTE (CP8d): CP8b committed 0.15 (lower = ice blocks fluid at lower concentration = more
        // ink/ice overlap). Lake's playtest raised it to 0.5, which is the opposite direction. That was
        // very likely a workaround for ice acting as a perfect heat INSULATOR — a high threshold means
        // fewer cells count as obstacles, so less heat got blocked. CP8d removes that root cause
        // (conduction now ignores obstacles entirely), so the workaround may no longer be needed and
        // 0.15 may be preferable again. Preserving Lake's playtest value pending confirmation.
        [Test]
        public void IceAsset_ObstacleThreshold_IsAuthoredValue()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Inks/Ice.asset";
            string src = System.IO.File.ReadAllText(path);
            StringAssert.Contains("obstacleThreshold: 0.5", src,
                "Ice obstacle threshold is the playtest-authored 0.5 (was 0.15 in CP8b) — see note above");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // 8. SimDriver serializes the CP3 thermal fields (source-level assertion — the fields are
        // private [SerializeField], so verify their declarations exist and copy into the context).
        [Test]
        public void SimDriver_SerializesThermalFields()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Scripts/Systems/SimulationLOD0/SimDriver.cs";
            string src = System.IO.File.ReadAllText(path);
            // CP8a: `ambientTemperature` is split into `neutralTemperature` (relaxation target /
            // room temperature) and `minTemperature` (the clamp floor). They must NOT be the same
            // value, or nothing can get colder than room temperature and ice can never form.
            foreach (var field in new[] { "thermalDissipationHalfLife", "thermalDiffusion",
                "neutralTemperature", "minTemperature", "enableHeatSources", "fireHeatEmissionRate", "maxHeat" })
            {
                StringAssert.Contains(field, src, $"SimDriver should serialize {field}");
                StringAssert.Contains($"ctx.", src); // sanity: context copy block present
            }
            Assert.IsFalse(src.Contains("ambientTemperature"),
                "CP8a: ambientTemperature must be replaced by neutralTemperature + minTemperature");
            StringAssert.Contains("ctx.FireHeatEmissionRate = fireHeatEmissionRate", src,
                "SimDriver must copy thermal source fields into the context");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // 9. CP8d BEHAVIOUR CHANGE: conduction now IGNORES obstacles.
        //
        // This test previously asserted the opposite (obstacle cells were no-flux and received no heat).
        // That made ink obstacles — plant and ice — PERFECT INSULATORS: fire beside a plant could never
        // warm it, so heat-driven ignition was impossible and dense ice could not be melted from outside.
        //
        // Advection and conduction are different physics. Advection is transport BY THE FLUID, so no flow
        // through a solid => no advective transport (AdvectHeat keeps its no-flux mask — see the test
        // below, which still passes). Conduction is transport THROUGH MATTER, and ink obstacles ARE
        // matter. Ice conducts heat; that is why a flame melts it.
        [UnityTest]
        public IEnumerator DiffuseHeat_ConductsIntoObstacleCells()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;   // (1,1) heated cell
            int R = 1 * res + 2;   // (2,1) obstacle cell beside it

            var heat = new float[res * res]; heat[C] = 1f;
            var wall = new float[res * res]; wall[R] = 1f;
            var open = new float[res * res];

            float[] withObstacle = DispatchDiffuseHeatGrid(heat, wall, res, 1f);

            Assert.That(withObstacle[R], Is.GreaterThan(2e-2f),
                "An obstacle cell MUST now absorb heat by conduction — otherwise plant/ice are perfect " +
                "insulators and can never be ignited or melted from outside.");

            // Conduction is now obstacle-blind, so the obstacle case must match the open case exactly.
            float[] control = DispatchDiffuseHeatGrid(heat, open, res, 1f);
            Assert.That(withObstacle[R], Is.EqualTo(control[R]).Within(2e-2f),
                "Conduction ignores the obstacle mask entirely: the obstacle result must equal the open result");
            Assert.That(withObstacle[C], Is.EqualTo(control[C]).Within(2e-2f),
                "…including for the heated cell, which now genuinely loses heat into its solid neighbour");

            yield return null;
#else
            yield break;
#endif
        }

        // 10. AdvectHeat no-flux: a velocity back-trace that crosses a solid must not jump heat
        // across it. A control run (no obstacle) confirms the same back-trace would transfer heat.
        [UnityTest]
        public IEnumerator AdvectHeat_ObstacleBlocksBacktrace()
        {
#if UNITY_EDITOR
            const int res = 3;
            int SRC = 1 * res + 0;   // (0,1) source heat behind the wall
            int WALL = 1 * res + 1;  // (1,1) obstacle between source and target
            int DST = 1 * res + 2;   // (2,1) target; velocity (2,0) back-traces it toward (0,1)

            var heat = new float[res * res]; heat[SRC] = 1f;
            var wall = new float[res * res]; wall[WALL] = 1f;
            var open = new float[res * res];
            var vel = new Vector2(2f, 0f);

            float[] blocked = DispatchAdvectHeatGrid(heat, wall, vel, res, 1f, 1f, 0f);
            Assert.That(blocked[DST], Is.EqualTo(0f).Within(3e-2f),
                "Back-trace across a solid must fall back to current heat (no jump).");

            float[] control = DispatchAdvectHeatGrid(heat, open, vel, res, 1f, 1f, 0f);
            Assert.That(control[DST], Is.EqualTo(1f).Within(5e-2f),
                "Without an obstacle the back-trace transfers source heat (~1.0) — proves the block is real.");

            yield return null;
#else
            yield break;
#endif
        }
    }
}
