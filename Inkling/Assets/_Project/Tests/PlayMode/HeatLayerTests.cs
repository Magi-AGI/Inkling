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
        // CP8z: heatObstacleMode defaults to 1 (LEGACY advective) so every pre-CP8z AdvectHeat GPU test —
        // all of which were written to exercise the CP8q permeability path — keeps its original meaning.
        // Strict-mode tests pass 0 explicitly.
        private static float[] DispatchAdvectHeatGrid(float[] heat, float[] obstacle, Vector2 vel,
            int res, float dt, float dissipation, float ambient,
            float minTemp = 0f, float maxHeat = 1f, float solidPermeability = 0f, int heatObstacleMode = 1)
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
                // CP8q: ALWAYS set — uniforms persist between dispatches, so leaving this unset would
                // inherit whatever a prior test left behind (the stale-uniform class CP8 keeps hitting).
                cs.SetFloat("_ThermalSolidPermeability", solidPermeability);
                cs.SetInt("_HeatObstacleMode", heatObstacleMode);   // CP8z: 0 strict, 1 legacy advective
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
        // CP8l: `diffusion`/`diffusionSolid` are now conduction rates PER SECOND, and the kernel converts
        // each to a per-frame blend via 1 - exp(-rate*dt) — so `dt` is load-bearing and must be supplied.
        //
        // CP8o-fix (Codex): DiffuseHeat reads `_ParticlesRead` whenever `_ThermalSolidThresholdIce > 0`,
        // and compute uniforms/buffers PERSIST across dispatches. So this helper now ALWAYS sets the
        // threshold and ALWAYS binds a particle buffer, seeded with `iceConc` everywhere — otherwise a
        // prior test's threshold could leak in and the kernel would read a stale/unbound buffer (the exact
        // stale-uniform class CP8 keeps hitting). Default `iceThermalThreshold = 0` keeps every existing
        // caller on the pure geometry-mask path, byte-identical to before.
        private static float[] DispatchDiffuseHeatGrid(float[] heat, float[] obstacle, int res, float diffusion,
            float minTemp = 0f, float maxHeat = 1f, float diffusionSolid = -1f, float dt = 1f,
            float iceThermalThreshold = 0f, float iceConc = 0f)
        {
            if (diffusionSolid < 0f) diffusionSolid = diffusion;   // default: solids behave like fluid

            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.inktools.sim/Compute/Fluids.compute");
            int kernel = cs.FindKernel("DiffuseHeat");

            var hr = MakeSeededRT(res, RenderTextureFormat.RHalf, heat);
            var hw = MakeSeededRT(res, RenderTextureFormat.RHalf, new float[res * res]);
            var obs = MakeSeededRT(res, RenderTextureFormat.RFloat, obstacle);

            // Always bind a real particle buffer so the kernel never reads an unbound SRV, regardless of
            // whether ice-concentration classification is active this call.
            var parts = new iparticle[res * res];
            for (int i = 0; i < parts.Length; i++) parts[i].ice = iceConc;
            var partBuf = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            partBuf.SetData(parts);
            try
            {
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
                return ReadAllR(hw, res);
            }
            finally
            {
                // Never release an RT that is still bound as the active render target.
                RenderTexture.active = null;
                hr.Release(); hw.Release(); obs.Release(); partBuf.Release();
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
                // ClampTemperature (CP8a) clamps every heat write to [_MinTemperature, _MaxHeat]. These are
                // PERSISTENT compute uniforms — if this test does not set them, they leak from whatever test
                // ran before, and the retention-0 decay-to-0 case clamps UP to a stale floor (observed
                // 0.0999755859 = a leftover _MinTemperature ~0.1). Set the intended range so the clamp is inert.
                cs.SetFloat("_MinTemperature", 0f);
                cs.SetFloat("_MaxHeat", 1f);

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

        // CP8f: Steam is born HOT — strictly between Water (neutral) and Fire (max). The band is the
        // point: below the condense threshold, freshly painted steam would collapse straight back into
        // water; at max it would read as fire-hot.
        [UnityTest]
        public IEnumerator StampInjectionHeat_Steam_StampsHotBetweenWaterAndFire()
        {
#if UNITY_EDITOR
            const int res = 8;
            const float minTemp = 0f, maxHeat = 1f, neutral = 0.5f, steam = 0.75f;

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = neutral;   // world at room temperature

            float[] outp = DispatchStampInjectionHeat(heat, res,
                centerPixel: new Vector2(4f, 4f), radius: 3f, target: steam,
                minTemp: minTemp, maxHeat: maxHeat);
            yield return null;

            int centre = 4 * res + 4;
            Assert.That(outp[centre], Is.EqualTo(steam).Within(2e-2f),
                "Steam injection must stamp its hot default temperature at the centre");
            Assert.That(outp[centre], Is.GreaterThan(neutral + 2e-2f),
                "…genuinely hotter than water/room temperature");
            Assert.That(outp[centre], Is.LessThan(maxHeat - 2e-2f),
                "…but cooler than fire");
            Assert.That(outp[0], Is.EqualTo(neutral).Within(2e-2f),
                "A cell outside the injection radius must pass through unchanged");
#else
            yield break;
#endif
        }

        // CP8k. Lake: "All inks (including plant) should be a neutral temperature except for fire, ice,
        // and steam." Exactly three inks carry a characteristic temperature; everything else is room
        // temperature. Crucially, non-thermal inks must now STAMP neutral rather than returning false
        // and leaving heat untouched — "untouched" meant painting plant over a frozen patch preserved
        // the cold, letting ink and temperature drift apart.
        [Test]
        public void Context_OnlyFireIceSteam_HaveCharacteristicTemperatures_EverythingElseIsNeutral()
        {
            var ctx = new SimulationContext();
            float neutral = ctx.SanitizedNeutralTemperature;

            // The three special inks.
            ctx.TryGetInjectionTemperature((int)InkTypeId.Fire, out float fire);
            ctx.TryGetInjectionTemperature((int)InkTypeId.Ice, out float ice);
            ctx.TryGetInjectionTemperature((int)InkTypeId.Steam, out float steam);
            Assert.That(fire, Is.EqualTo(ctx.SanitizedMaxTemperature).Within(1e-4f), "Fire is the ceiling");
            Assert.That(ice, Is.EqualTo(ctx.SanitizedMinTemperature).Within(1e-4f), "Ice is the floor");
            Assert.That(steam, Is.GreaterThan(neutral), "Steam is hot");
            Assert.That(steam, Is.LessThan(fire), "…but cooler than fire");

            // EVERY other ink is room temperature — and must return true, so the stamp actually runs.
            var neutralInks = new[]
            {
                InkTypeId.Water, InkTypeId.PlantSeeded, InkTypeId.PlantGrown, InkTypeId.Glitter,
                InkTypeId.BlackBody, InkTypeId.ElectricitySeeded, InkTypeId.ElectricityGrown,
            };
            foreach (var ink in neutralInks)
            {
                Assert.IsTrue(ctx.TryGetInjectionTemperature((int)ink, out float t),
                    $"{ink} must STAMP its temperature, not leave the heat field untouched — otherwise " +
                    "painting it over a frozen patch silently preserves the cold");
                Assert.That(t, Is.EqualTo(neutral).Within(1e-4f), $"{ink} must arrive at room temperature");
            }

            // Out-of-range is the only false case, and it must NOT hand back the floor: a caller that
            // ignores the bool would otherwise freeze the cell.
            Assert.IsFalse(ctx.TryGetInjectionTemperature(-1, out float bad), "Out-of-range index");
            Assert.That(bad, Is.EqualTo(neutral).Within(1e-4f),
                "Even the rejected path must fall back to NEUTRAL, never the freezing floor");
            Assert.IsFalse(ctx.TryGetInjectionTemperature((int)InkTypeId.Count, out _), "Count is not an ink");
        }

        // The context is what actually chooses that number at runtime, and it must sit inside the band
        // that keeps steam alive: above condense (or it instantly re-condenses) and below boil.
        [Test]
        public void Context_SteamInjectionTemperature_IsHot_AndSurvivesCondensation()
        {
            var ctx = new SimulationContext();

            Assert.IsTrue(ctx.TryGetInjectionTemperature((int)InkTypeId.Steam, out float steam),
                "Steam must be a typed-injection ink, or painting steam stamps no heat at all");
            Assert.That(steam, Is.EqualTo(0.75f).Within(1e-4f));

            ctx.TryGetInjectionTemperature((int)InkTypeId.Water, out float water);
            ctx.TryGetInjectionTemperature((int)InkTypeId.Fire, out float fire);
            Assert.That(steam, Is.GreaterThan(water), "Steam must be hotter than water");
            Assert.That(steam, Is.LessThan(fire), "…and cooler than fire");

            Assert.That(steam, Is.GreaterThan(ctx.CondenseThreshold),
                "Steam must be born ABOVE the condense threshold, or painted steam collapses to water at once");
            Assert.That(steam, Is.LessThan(ctx.BoilThreshold), "…and below the boil threshold");

            // The clamp must hold even if someone authors a nonsense value: steam colder than water or
            // hotter than fire is never a valid state.
            ctx.SteamInjectionTemperature = -5f;
            Assert.That(ctx.SanitizedSteamInjectionTemperature, Is.EqualTo(ctx.SanitizedNeutralTemperature).Within(1e-4f),
                "A sub-neutral steam temperature must clamp up to neutral");
            ctx.SteamInjectionTemperature = 5f;
            Assert.That(ctx.SanitizedSteamInjectionTemperature, Is.EqualTo(ctx.SanitizedMaxTemperature).Within(1e-4f),
                "A super-max steam temperature must clamp down to max");
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

            // CP8 neutral layout: freeze == melt < neutral < condense <= boil.
            StringAssert.Contains("neutralTemperature: 0.5", src);
            StringAssert.Contains("freezeThreshold: 0.15", src);

            // CP8j: melt sits ON the freeze point, so ice above freezing simply melts. The old 0.35 left
            // a 0.15..0.35 dead band where ice was warmer than freezing and still would not melt — ice
            // whose appearance was divorced from its temperature, which is what Lake reported.
            StringAssert.Contains("meltThreshold: 0.15", src,
                "Ice above the freezing point must melt — no dead band between freeze and melt");

            StringAssert.Contains("condenseThreshold: 0.65", src,
                "Condense must sit ABOVE neutral, or steam never condenses at room temperature");
            StringAssert.Contains("boilThreshold: 0.85", src);

            // CP8k: the heat RATCHET fix. Every thermal transition removes heat and none returns it, so
            // with a 1000s relaxation half-life (0.0003 heat/sec restored) the field could only ever get
            // colder. The thermostat must actually work, and freezing must not be a 1.0-unit refrigerator.
            StringAssert.Contains("thermalDissipationHalfLife: 60", src,
                "Relaxation toward NEUTRAL is the thermostat, not a leak — at 1000 it was effectively off");
            // CP8u lowered this 0.2 -> 0.1 ("make ice formation a bit slower" + less formation chill).
            // The golden tracks the live scene value; the ratchet reasoning (never 1.0) still holds at 0.1.
            StringAssert.Contains("freezeHeatCost: 0.1", src,
                "Freezing keeps a one-shot chill, but a small one — at 1.0 a freeze/thaw cycle destroyed " +
                "1.5 units of heat and dragged the whole field to frozen (CP8u trimmed it further to 0.1)");

            // CP8k: cold fire goes out (removed outright, not converted into smoke or a puddle).
            // CP8l LOWERED the threshold from 0.85: a plant cell beside a max-heat fire settles at 0.625,
            // so a 0.85 sink was EXTINGUISHING fire as it spread into plant, before it could establish and
            // heat its own cell. Fire was strangling itself. 0.6 is below that 0.625 but still above room
            // temperature (0.5), so fire adrift in the cold still dies.
            StringAssert.Contains("fireSinkThreshold: 0.6", src,
                "The sink must sit below what a fire-adjacent cell reaches, or fire cannot spread at all");
            StringAssert.Contains("fireSinkRate: 4", src, "…and goes out rapidly when it is genuinely cold");

            // CP8l: conduction is now a PER-SECOND rate (dt-normalised), and solids conduct faster.
            StringAssert.Contains("thermalDiffusion: 2", src, "Conduction rate per second in open fluid");
            // CP8aa/CP8ab raised this 12 -> 30 -> 60 (the melt-through-obstacle-ice retune; 60 is the top
            // of the useful range). Under CP8z's strict model conduction is once again the ONLY way heat
            // enters a solid, so this rate carries the whole ingress — hence it must exceed the fluid rate.
            StringAssert.Contains("thermalDiffusionSolid: 60", src,
                "Heat must travel MORE readily through solids than fluid — under strict conduction (CP8z) " +
                "it is the only way heat enters a solid, so the solid rate must exceed the fluid rate");
            StringAssert.Contains("fireHeatEmissionRate: 4", src,
                "Fire must out-produce conduction in its own cell, or it cannot hold its temperature");

            // CP8l: heat-only plant ignition must be REACHABLE. At 0.98 it never fired — a plant cell
            // beside a max-heat fire converges to 0.625 and simply cannot get there.
            StringAssert.Contains("plantIgnitionThreshold: 0.75", src,
                "Plant ignition must be reachable by conduction, yet well above ambient (0.5)");

            // CP8h: the THRESHOLD says steam condenses at room temperature; the RATE says it does so
            // gently. Both are needed — a gentle rate with the wrong threshold would still be wrong.
            StringAssert.Contains("condenseRate: 0.15", src,
                "Cooling steam should shed only a little water per step, not collapse wholesale");

            // Conduction must be ON, or fire/ice cannot influence the temperature around them at all.
            // (CP8l pins the actual rates — fluid 2/s, solid 6/s — in the block above.)
            Assert.IsFalse(src.Contains("thermalDiffusion: 0\n") || src.Contains("thermalDiffusion: 0\r\n"),
                "thermalDiffusion must be non-zero so heat actually conducts");
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
        // CP8o — THE DECOUPLING. Two thresholds now govern ice, and the whole point is they are SEPARATE:
        //
        //   FLOW  : Ice.obstacleThreshold (0.5)   — at/above this, ice blocks fluid VELOCITY.
        //   HEAT  : thermalSolidThresholdIce (0.1) — at/above this, ice conducts at the SOLID rate.
        //
        // and the brush paints at densityAmount (0.3). The required ordering is:
        //
        //           thermalSolidThreshold (0.1)  <=  densityAmount (0.3)  <  obstacleThreshold (0.5)
        //
        // so a normal painted stroke CONDUCTS heat (clears the heat threshold) but does NOT dam fluid
        // (stays below the flow threshold). CP8n could not have both — it used one threshold for both jobs,
        // so making ice conductive (lowering to 0.15) also made thin ice a flow obstacle, which Lake
        // rejected. Pinning the ORDERING, not literals, is deliberate: it survives retuning densityAmount
        // and is exactly the invariant that was missing when this drifted before.
        [Test]
        public void IceThresholds_HeatConductsButFlowDoesNotBlock_AtBrushDensity()
        {
#if UNITY_EDITOR
            string ice = System.IO.File.ReadAllText("Assets/_Project/Inks/Ice.asset");
            string scene = System.IO.File.ReadAllText("Assets/_Project/Scenes/Main.unity");

            float Read(string src, string key)
            {
                var m = System.Text.RegularExpressions.Regex.Match(src, key + @":\s*([0-9.]+)");
                Assert.IsTrue(m.Success, $"could not read {key}");
                return float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            float flowThreshold = Read(ice, "obstacleThreshold");
            float heatThreshold = Read(scene, "thermalSolidThresholdIce");
            float density = Read(scene, "densityAmount");

            // Lake's explicit ask: keep the flow threshold HIGH so thin ice does not dam fluid.
            Assert.That(flowThreshold, Is.EqualTo(0.5f).Within(1e-5f),
                "Ice velocity/flow obstacle threshold must stay 0.5 — thin ice must NOT block fluid");

            Assert.That(heatThreshold, Is.LessThanOrEqualTo(density),
                $"thermalSolidThresholdIce ({heatThreshold}) must be REACHABLE by a brush stroke at " +
                $"densityAmount ({density}), or painted ice never conducts at the solid rate — the CP8n bug");

            Assert.That(density, Is.LessThan(flowThreshold),
                $"brush density ({density}) must stay BELOW the flow threshold ({flowThreshold}), or a " +
                "normal stroke dams fluid — the coupling Lake rejected");

            Assert.That(heatThreshold, Is.LessThan(flowThreshold),
                "the HEAT threshold must sit below the FLOW threshold — that gap is the decoupling itself");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // CP8o: the wiring that makes the decoupling real. DiffuseHeat must classify thermal-solid from the
        // ICE CONCENTRATION (its own threshold), not solely from IsObstacle (the velocity mask). If it
        // regressed to IsObstacle-only, painted ice below the 0.5 flow threshold would silently stop
        // conducting again.
        [Test]
        public void DiffuseHeat_ClassifiesThermalSolid_FromIceConcentration_NotOnlyObstacleMask()
        {
#if UNITY_EDITOR
            const string heat = "Packages/com.inktools.sim/Compute/Include/Heat.hlsl";
            string src = System.IO.File.Exists(heat)
                ? System.IO.File.ReadAllText(heat)
                : System.IO.File.ReadAllText(
                    "../InkTools/InkTools/Assets/_Project/Scripts/Simulation/Compute/Include/Heat.hlsl");

            StringAssert.Contains("_ThermalSolidThresholdIce", src,
                "DiffuseHeat must read the ice concentration against its own thermal threshold");
            StringAssert.Contains("_ParticlesRead[pidx].ice", src,
                "…by reading the particle buffer directly, decoupled from the velocity obstacle mask");
            StringAssert.Contains("iceThermalSolid || (IsObstacle(id.xy) > 0.5)", src,
                "…OR-ed with the geometry mask so walls still conduct, but ice no longer DEPENDS on it");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // CP8i. Lake: "there should be a relation between the ice's intensity and its temperature. The
        // dissipation of the ice should correspond to the diffusion of heat into the ice."
        //
        // Ice carried a generic, time-based concentration fade (dissipationHalfLife 45 => the particle
        // kernel applies pow(0.5^(1/45), dt), i.e. ~1.5% lost EVERY SECOND regardless of temperature).
        // That is precisely the arbitrary, non-thermal dissipation Lake is objecting to: ice at absolute
        // zero, with no heat anywhere near it, still quietly evaporated.
        //
        // The fix is to make ice PERSISTENT and let heat be the only thing that removes it — melt is
        // capped by excess/meltHeatCost, so ice loss is metered by how much heat conducts in
        // (HeatDiffusedIntoIce_MeltsIt_AndPaysForItInHeat pins that composition). 120000 is not an
        // arbitrary number: it is the value the project's OTHER structural/obstacle inks
        // (PlantSeeded, PlantGrown) already use for "does not fade on its own".
        [Test]
        public void IceAsset_IsThermallyPersistent_NotTimeFaded()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Inks/Ice.asset";
            string src = System.IO.File.ReadAllText(path);

            var m = System.Text.RegularExpressions.Regex.Match(src, @"dissipationHalfLife:\s*([0-9.eE+-]+)");
            Assert.IsTrue(m.Success, "Ice.asset must declare dissipationHalfLife");
            float halfLife = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            Assert.That(halfLife, Is.GreaterThanOrEqualTo(1000f),
                "Ice must not fade on a timer — its intensity is coupled to TEMPERATURE, and heat-driven " +
                "melt must be the only route by which it loses intensity. A short half-life (it was 45) " +
                "makes cold ice evaporate for no thermal reason.");

            // Guard the reasoning, not just the number: whatever "persistent" means for the structural
            // inks, ice must be at least as persistent. If Plant's value is ever retuned, this still holds.
            string plant = System.IO.File.ReadAllText("Assets/_Project/Inks/PlantGrown.asset");
            var pm = System.Text.RegularExpressions.Regex.Match(plant, @"dissipationHalfLife:\s*([0-9.eE+-]+)");
            Assert.IsTrue(pm.Success, "PlantGrown.asset must declare dissipationHalfLife");
            float plantHalfLife = float.Parse(pm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            Assert.That(halfLife, Is.GreaterThanOrEqualTo(plantHalfLife),
                "Ice is a structural/obstacle ink like Plant and must be at least as persistent");

            // CP8i (Codex note): viscosity is the OTHER non-thermal route by which ice intensity can
            // change. `ParticleSimulation.hlsl` does `p.ice = lerp(p.ice, NEIGHBOR_AVG(ice), _ViscosityIce)`
            // — with no dt, so it runs per FRAME. It is mass-CONSERVING (a blur, not a sink), so it is
            // not "dissipation" in the fading sense. But it still softens the blob's RIM, and because
            // `obstacleThreshold` is 0.5, rim cells blurred below that cutoff silently stop counting as
            // obstacles: the ice's collision footprint erodes and fluid starts leaking through its edge,
            // with no heat involved anywhere. That is a temperature-independent change to ice's intensity
            // AND to the threshold Lake hand-tuned, so it is out.
            //
            // 0 is also what the engine itself defaults Ice to — see FluidSolver's
            // `GetInkProp(InkTypeId.Ice, d => d.viscosity, 0.0f)`.
            var vm = System.Text.RegularExpressions.Regex.Match(src, @"viscosity:\s*([0-9.eE+-]+)");
            Assert.IsTrue(vm.Success, "Ice.asset must declare viscosity");
            float viscosity = float.Parse(vm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            Assert.That(viscosity, Is.EqualTo(0f).Within(1e-6f),
                "Ice must not blur/spread on its own. Ice is a SOLID: advectionWeight is already 0 so it " +
                "does not ride the fluid, and viscosity must be 0 too so it does not seep sideways either. " +
                "Heat-driven melt is the only permitted route by which an ice cell's intensity may change.");

            // Belt and braces on the same intent: ice must not be carried by the fluid.
            StringAssert.Contains("advectionWeight: 0", src, "Ice is a solid and must not advect");
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

        // CP8l. There are THREE spellings of every thermal knob — SimDriver's `[SerializeField] private
        // float x = v`, SimulationContext's `public float X = v`, and Main.unity's `x: v` — and CP8l
        // drifted apart on the first one for FIVE fields at once (plus `thermalDiffusionSolid`, which
        // SimDriver was missing entirely, so its scene line was inert and the knob was un-tunable).
        // Nothing pinned the SimDriver initializers, so review caught it instead of tests.
        //
        // This asserts EQUALITY between the two C# surfaces rather than hardcoding literals, so it keeps
        // working across retunes and automatically covers any field added later. The scene is the third
        // surface and is pinned separately by MainScene_IsInThermalSteamMode_WithNeutralLayout.
        [Test]
        public void SimDriver_SerializedDefaults_MatchSimulationContextDefaults()
        {
#if UNITY_EDITOR
            string driver = System.IO.File.ReadAllText(
                "Assets/_Project/Scripts/Systems/SimulationLOD0/SimDriver.cs");
            string context = System.IO.File.ReadAllText(
                "Assets/_Project/Scripts/Systems/SimulationLOD0/Core/SimulationContext.cs");

            // driverField -> contextField. Every CP8l value that drifted, plus its neighbours.
            var pairs = new (string driverField, string contextField)[]
            {
                ("thermalDissipationHalfLife", "ThermalDissipationHalfLife"),
                ("thermalDiffusion",           "ThermalDiffusion"),
                ("thermalDiffusionSolid",      "ThermalDiffusionSolid"),
                ("fireHeatEmissionRate",       "FireHeatEmissionRate"),
                ("meltHeatCost",               "MeltHeatCost"),
                ("freezeHeatCost",             "FreezeHeatCost"),
                ("fireSinkThreshold",          "FireSinkThreshold"),
                ("fireSinkRate",               "FireSinkRate"),
                ("plantIgnitionThreshold",     "PlantIgnitionThreshold"),
                ("neutralTemperature",         "NeutralTemperature"),
                ("minTemperature",             "MinTemperature"),
                ("steamInjectionTemperature",  "SteamInjectionTemperature"),
                ("condenseRate",               "CondenseRate"),
                ("freezeThreshold",            "FreezeThreshold"),
                ("meltThreshold",              "MeltThreshold"),
            };

            float Parse(string src, string pattern, string what)
            {
                var m = System.Text.RegularExpressions.Regex.Match(src, pattern);
                Assert.IsTrue(m.Success, $"Could not find {what} — was it renamed or removed?");
                return float.Parse(m.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            foreach (var (d, c) in pairs)
            {
                float dv = Parse(driver, $@"private\s+float\s+{d}\s*=\s*(-?[0-9.]+)f",
                    $"SimDriver serialized field '{d}'");
                float cv = Parse(context, $@"public\s+float\s+{c}\s*=\s*(-?[0-9.]+)f",
                    $"SimulationContext field '{c}'");

                Assert.That(dv, Is.EqualTo(cv).Within(1e-5f),
                    $"DRIFT: SimDriver.{d} = {dv} but SimulationContext.{c} = {cv}. These seed the same " +
                    "knob and must agree — a fresh SimDriver uses the SimDriver initializer, so a stale " +
                    "one silently ships different physics than the context (and than the tests) assume.");
            }

            // And the new field must actually reach the context, or its inspector value is inert — which
            // is exactly what happened to thermalDiffusionSolid: the scene had the line, SimDriver had no
            // field, so the value never made it anywhere.
            StringAssert.Contains("ctx.ThermalDiffusionSolid = thermalDiffusionSolid", driver,
                "SimDriver must copy the solid conduction rate into the context, or the knob does nothing");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // 9. CP8d BEHAVIOUR CHANGE: an obstacle cell CAN absorb heat by conduction.
        //
        // This test previously asserted the opposite (obstacle cells were no-flux and received no heat).
        // That made ink obstacles — plant and ice — PERFECT INSULATORS: fire beside a plant could never
        // warm it, so heat-driven ignition was impossible and dense ice could not be melted from outside.
        // Conduction is transport THROUGH MATTER, and ink obstacles ARE matter. Ice conducts heat; that
        // is why a flame melts it.
        //
        // CP8l UPDATE — the mask's role has since inverted, so read the assertions below carefully:
        // DiffuseHeat is no longer obstacle-BLIND. It reads the mask as a CONDUCTIVITY SELECTOR, choosing
        // _ThermalDiffusionSolid over _ThermalDiffusion. It still never BLOCKS exchange, which is what
        // this test is really about. The equality assertions hold here only because the helper defaults
        // `diffusionSolid` to the same value as `diffusion` when unspecified — i.e. this test pins "a
        // solid is not a barrier", NOT "a solid conducts identically to fluid". The latter is false, and
        // DiffuseHeat_ConductsFasterThroughSolidsThanFluid above is what pins the difference.
        //
        // (AdvectHeat no longer reads the mask at all — see the test below, now inverted for CP8k.)
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

            // A solid is not a BARRIER. With the solid and fluid conduction rates held equal (the helper's
            // default when `diffusionSolid` is unspecified), masking a cell as solid must change nothing —
            // proving the mask no longer gates exchange, only selects a rate.
            float[] control = DispatchDiffuseHeatGrid(heat, open, res, 1f);
            Assert.That(withObstacle[R], Is.EqualTo(control[R]).Within(2e-2f),
                "At equal rates, the obstacle result must equal the open result — the mask must not BLOCK " +
                "conduction, only choose its rate (CP8l)");
            Assert.That(withObstacle[C], Is.EqualTo(control[C]).Within(2e-2f),
                "…including for the heated cell, which genuinely loses heat into its solid neighbour");

            yield return null;
#else
            yield break;
#endif
        }

        // ── CP8l: conduction is dt-normalised, and solids conduct BETTER than fluid ──────────

        // THE CP8l BUG. DiffuseHeat was the ONLY heat term in the file not multiplied by _FrameDeltaTime:
        // it applied a flat blend once per FRAME. At 60fps a 0.2 blend/frame is an effective ~12/sec,
        // which beat fire's own dt-normalised emission SIX TO ONE — fire could not hold its own
        // temperature and every hot spot smeared away before it could melt ice or ignite plant. It was
        // also frame-rate dependent, so the whole thermal model behaved differently on a slow machine.
        //
        // Halving dt must now halve the conduction, not leave it unchanged. That is the whole assertion.
        [UnityTest]
        public IEnumerator DiffuseHeat_IsDtNormalised_NotPerFrame()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 1f;
            heat[C] = 0f;                       // one cold cell surrounded by hot ones
            var open = new float[res * res];    // no obstacles

            float[] full = DispatchDiffuseHeatGrid(heat, open, res, diffusion: 4f, dt: 1f / 60f);
            float[] half = DispatchDiffuseHeatGrid(heat, open, res, diffusion: 4f, dt: 1f / 120f);
            yield return null;

            Assert.That(full[C], Is.GreaterThan(0f), "Sanity: conduction warms the cold cell");
            Assert.That(half[C], Is.LessThan(full[C] - 1e-3f),
                "HALF the timestep must conduct roughly HALF as much. If dt is ignored, both runs land " +
                "on the same value and conduction is frame-rate dependent — the CP8l bug.");
#else
            yield break;
#endif
        }

        // CP8o — THE CORE BEHAVIOUR, on the GPU. Painted ice (concentration 0.3) with the obstacle mask
        // ZERO must conduct at the SOLID rate, purely because its ice clears the thermal threshold (0.1) —
        // NOT because it is a velocity obstacle (it isn't; mask is 0). This is the whole decoupling: heat
        // conductivity from ice concentration, flow-blocking from the obstacle mask, independently.
        [UnityTest]
        public IEnumerator DiffuseHeat_PaintedIce_ConductsAtSolidRate_WithNoObstacleMask()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;                       // cold centre cell we will heat by conduction

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 1f;
            heat[C] = 0f;
            var noObstacle = new float[res * res];     // mask ALL zero — nothing is a velocity obstacle

            // Ice present (0.3) with threshold 0.1 => thermal-solid by CONCENTRATION => solid rate.
            float[] asIce = DispatchDiffuseHeatGrid(heat, noObstacle, res,
                diffusion: 2f, diffusionSolid: 12f, dt: 1f / 60f,
                iceThermalThreshold: 0.1f, iceConc: 0.3f);

            // Same cell, same everything, but threshold 0 => ice classification OFF => fluid rate.
            float[] asFluid = DispatchDiffuseHeatGrid(heat, noObstacle, res,
                diffusion: 2f, diffusionSolid: 12f, dt: 1f / 60f,
                iceThermalThreshold: 0f, iceConc: 0.3f);
            yield return null;

            Assert.That(asIce[C], Is.GreaterThan(asFluid[C] + 1e-3f),
                "Painted ice (0.3) with a ZERO obstacle mask must conduct FASTER than plain fluid — solid " +
                "rate selected by ice CONCENTRATION, not by the velocity obstacle mask. This is the CP8o " +
                "decoupling working on the GPU: it would fail if DiffuseHeat still keyed solely on IsObstacle.");
#else
            yield break;
#endif
        }

        // CP8o-fix (Codex): the stale-uniform / unbound-buffer guard. Ice BELOW the threshold must get the
        // fluid rate even if a PRIOR dispatch left the threshold high — the helper sets it every call, so
        // classification cannot leak between tests, and the kernel never reads a buffer it shouldn't act on.
        [UnityTest]
        public IEnumerator DiffuseHeat_IceBelowThermalThreshold_UsesFluidRate_NoLeak()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 1f;
            heat[C] = 0f;
            var noObstacle = new float[res * res];

            // Prime the persistent uniform HIGH via one dispatch that DOES classify ice as solid…
            DispatchDiffuseHeatGrid(heat, noObstacle, res, 2f, diffusionSolid: 12f, dt: 1f / 60f,
                iceThermalThreshold: 0.1f, iceConc: 0.3f);

            // …then a dispatch with ice 0.05 (BELOW 0.1). It must land on the fluid rate — equal to the
            // pure-fluid control — proving the prior high threshold did not leak in.
            float[] belowThr = DispatchDiffuseHeatGrid(heat, noObstacle, res, 2f, diffusionSolid: 12f,
                dt: 1f / 60f, iceThermalThreshold: 0.1f, iceConc: 0.05f);
            float[] fluid = DispatchDiffuseHeatGrid(heat, noObstacle, res, 2f, diffusionSolid: 12f,
                dt: 1f / 60f, iceThermalThreshold: 0f, iceConc: 0f);
            yield return null;

            Assert.That(belowThr[C], Is.EqualTo(fluid[C]).Within(2e-2f),
                "Ice below the thermal threshold must conduct at the FLUID rate — the persistent uniform " +
                "from the prior solid dispatch must not leak, and no stale classification may apply.");
#else
            yield break;
#endif
        }

        // ── CP8q: heat must reach obstacle-STRENGTH ice (Lake's observation) ───────────────
        //
        // Lake: "when the Ice value is high enough to make an obstacle, the heat still doesn't advect
        // into the Ice, so the temperature never rises high enough to trigger a melting of the ice ...
        // Actually, I do want the heat to advect through the obstacle ice."
        //
        // SCOPE, STATED HONESTLY (Codex CKPT-093): this test exercises the HLSL AdvectHeat KERNEL and its
        // _ThermalSolidPermeability path ONLY. It does NOT exercise the C# VelocityThermal wiring and it
        // does NOT exercise full SimDriver composition — those are covered by
        // FluidSolver_AdvectsHeatWithPreBoundaryVelocity_NotTheClippedField (source) and by the
        // obstacle-strength FireIceScenario run (runtime, still unexecuted).
        //
        // It supplies an explicit PRE-BOUNDARY velocity field: unclipped inward flow on the fire-facing
        // side ONLY, with the solid's own velocity and every non-contact neighbour set to ZERO. That
        // isolation is deliberate — the previous version of this test fed a clipped field in which the
        // obstacle cell could borrow velocity from downstream/irrelevant neighbours and pass without ever
        // proving heat came from the fire side. Here, permeability 0 has nothing to borrow and MUST fail;
        // permeability 1 borrows the fire-facing inflow and MUST succeed. That contrast is the assertion.
        [UnityTest]
        public IEnumerator AdvectHeat_ThermalPermeability_CarriesHeatFromTheFireFacingSideOnly()
        {
#if UNITY_EDITOR
            const int res = 3;
            int HOT = 1 * res + 0;     // (0,1) fire-facing fluid, to the LEFT of the ice
            int ICE = 1 * res + 1;     // (1,1) obstacle-strength ice
            int DOWNSTREAM = 1 * res + 2;  // (2,1) fluid beyond the ice — must contribute NOTHING

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 0.5f;   // neutral room
            heat[HOT] = 1f;                                          // the only hot cell
            heat[ICE] = 0f;                                          // ice is cold

            var obstacle = new float[res * res];
            obstacle[ICE] = 1f;                                      // ice >= 0.5 => solid

            // PRE-BOUNDARY field: the fire-facing cell retains its inward (+x) flow, which the obstacle
            // boundary would normally have clipped away. Everything else — including the solid itself and
            // the downstream cell — is zero, so there is exactly ONE possible source of borrowed velocity.
            var vel = new Vector2[res * res];
            vel[HOT] = new Vector2(2f, 0f);
            Assume.That(vel[ICE], Is.EqualTo(Vector2.zero), "the solid carries no velocity of its own");
            Assume.That(vel[DOWNSTREAM], Is.EqualTo(Vector2.zero), "downstream must not be borrowable");

            float before = heat[ICE];

            // permeability 0: the solid keeps its own (zero) velocity => no advection => no heat.
            float[] withoutPermeability = DispatchAdvectHeatGridWithField(heat, obstacle, vel, res,
                dt: 1f / 60f, dissipation: 1f, ambient: 0.5f, solidPermeability: 0f);

            // permeability 1: the solid borrows the fire-facing inflow => back-traces toward HOT => warms.
            float[] withPermeability = DispatchAdvectHeatGridWithField(heat, obstacle, vel, res,
                dt: 1f / 60f, dissipation: 1f, ambient: 0.5f, solidPermeability: 1f);
            yield return null;

            Assert.That(withoutPermeability[ICE], Is.LessThan(before + 1e-3f),
                "CONTROL: with permeability 0 the solid keeps its own zero velocity, so advection can " +
                "deliver nothing. If this ever rises, the test is passing for some reason other than the " +
                "mechanism under test and proves nothing.");

            Assert.That(withPermeability[ICE], Is.GreaterThan(withoutPermeability[ICE] + 1e-3f),
                $"Heat must ADVECT INTO obstacle-strength ice from the FIRE-FACING side once the solid may " +
                $"borrow the unclipped inflow. perm0 {withoutPermeability[ICE]:0.0000} -> " +
                $"perm1 {withPermeability[ICE]:0.0000}. Blocking MASS (velocity) must not also block " +
                "ENERGY, or obstacle ice is a perfect thermal barrier that can never melt.");
#else
            yield break;
#endif
        }

        // ── CP8z: STRICT conduction-only AdvectHeat (the new default model) ──────────────────────────
        //
        // Lake: "we may have made a mistake in allowing advection through obstacles when what we really
        // needed was conduction." These prove AdvectHeat carries NO heat through a solid in strict mode.
        // Conduction (DiffuseHeat) is untouched and still warms solids — that path is tested separately.

        // A solid cell must NOT advect heat into itself in strict mode. This is a CONTRAST test: the
        // identical fire-facing setup is run under BOTH models, and the assertion is that legacy (borrow
        // + permeability) DOES warm the ice while strict does NOT. Asserting only "strict stays cold"
        // would pass even if the whole kernel were broken, so the legacy leg proves the setup can deliver
        // heat — which is exactly what strict must then refuse.
        [UnityTest]
        public IEnumerator AdvectHeat_Strict_DoesNotCarryHeatIntoASolid_ButLegacyDoes()
        {
#if UNITY_EDITOR
            const int res = 3;
            int HOT = 1 * res + 0;   // (0,1) fire-facing fluid, left of the ice
            int ICE = 1 * res + 1;   // (1,1) obstacle-strength ice

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 0.5f;
            heat[HOT] = 1f;
            heat[ICE] = 0f;

            var obstacle = new float[res * res];
            obstacle[ICE] = 1f;

            // Fire-facing inflow the solid can borrow in legacy mode. dt = 1 (not 1/60) so the back-trace
            // travels a real fraction of a cell and the contrast is large and unambiguous.
            var vel = new Vector2[res * res];
            vel[HOT] = new Vector2(2f, 0f);

            float before = heat[ICE];

            float[] legacy = DispatchAdvectHeatGridWithField(heat, obstacle, vel, res,
                dt: 1f, dissipation: 1f, ambient: 0.5f, solidPermeability: 1f, heatObstacleMode: 1);
            float[] strict = DispatchAdvectHeatGridWithField(heat, obstacle, vel, res,
                dt: 1f, dissipation: 1f, ambient: 0.5f, solidPermeability: 1f, heatObstacleMode: 0);
            yield return null;

            Assert.That(legacy[ICE], Is.GreaterThan(before + 1e-2f),
                "SETUP PROOF: legacy advective mode must warm the ice (borrow + permeability) — otherwise " +
                "the scenario delivers no heat and the strict assertion below would be vacuous.");
            Assert.That(strict[ICE], Is.LessThan(before + 1e-3f),
                "STRICT mode must not advect heat into a solid — conduction (DiffuseHeat) is the only " +
                "ingress. If the early-out were removed this would rise toward the legacy value.");
            Assert.That(strict[ICE], Is.LessThan(legacy[ICE] - 1e-2f),
                "…and strict must be strictly colder than legacy at the ice face.");
#else
            yield break;
#endif
        }

        // Advective punch-through must be ABSENT. HOT | WALL | COLD in a row, flow pushing COLD's
        // back-trace LEFTWARD across the wall toward HOT. Sign and magnitude matter and both were wrong
        // in the first draft: prevUV = uv - (vel/simSize)*dt, so a POSITIVE velocity moves the back-trace
        // left (toward lower x), and dt must be large enough to span a whole cell or the path never
        // reaches the wall. With vel +2 and dt 1 the back-trace of COLD (x=2) lands on HOT (x=0), crossing
        // the wall at x=1 — so the no-flux march MUST fire. Contrast against legacy proves it: legacy has
        // no march and samples straight across the wall (warms), strict blocks (stays cold).
        [UnityTest]
        public IEnumerator AdvectHeat_Strict_NoPunchThroughAcrossAThinWall_ButLegacyPunchesThrough()
        {
#if UNITY_EDITOR
            const int res = 3;
            int HOT  = 1 * res + 0;   // (0,1) hot fluid
            int WALL = 1 * res + 1;   // (1,1) one-cell solid
            int COLD = 1 * res + 2;   // (2,1) cold fluid beyond the wall

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 0.5f;
            heat[HOT] = 1f;
            heat[COLD] = 0f;

            var obstacle = new float[res * res];
            obstacle[WALL] = 1f;

            // Positive (+x) flow => COLD back-traces toward x=0 (HOT), path crosses the wall at x=1.
            var vel = new Vector2[res * res];
            for (int i = 0; i < vel.Length; i++) vel[i] = new Vector2(2f, 0f);

            float before = heat[COLD];

            float[] legacy = DispatchAdvectHeatGridWithField(heat, obstacle, vel, res,
                dt: 1f, dissipation: 1f, ambient: 0.5f, solidPermeability: 1f, heatObstacleMode: 1);
            float[] strict = DispatchAdvectHeatGridWithField(heat, obstacle, vel, res,
                dt: 1f, dissipation: 1f, ambient: 0.5f, solidPermeability: 1f, heatObstacleMode: 0);
            yield return null;

            Assert.That(legacy[COLD], Is.GreaterThan(before + 1e-2f),
                "SETUP PROOF: legacy mode has no no-flux march, so COLD samples straight across the wall " +
                "toward HOT and warms. If this fails, the back-trace never reached the wall and the test " +
                "proves nothing about the strict path.");
            Assert.That(strict[COLD], Is.LessThan(before + 1e-3f),
                "STRICT mode must refuse the same back-trace because it crosses the wall (no-flux) — the " +
                "downstream cell stays cold. This is the assertion that would fail if the march were removed.");
            Assert.That(strict[COLD], Is.LessThan(legacy[COLD] - 1e-2f),
                "…and strict must be strictly colder than legacy downstream of the wall.");
#else
            yield break;
#endif
        }

        // ── CP8aa: the conduction retune, and the guard that it stays CONDUCTION ────────────────────
        //
        // Strict Fire-vs-Ice melted only 12.6% of the obstacle wall (vs legacy's 93.2%) because the
        // wall's average temperature plateaued at 0.1486 — just BELOW meltThreshold 0.15 — so the bulk
        // never crossed the threshold and only the surface skin melted. The conduction-only remedy is a
        // higher solid rate (thermalDiffusionSolid 12 -> 30). This proves that lever actually moves more
        // energy into a solid, IN DiffuseHeat.
        //
        // Pairs with AdvectHeat_Strict_* above: together they say "more heat may enter a solid, but ONLY
        // by conduction". If a future change tried to buy melt performance by reopening advection, those
        // tests fail; if it tried by weakening conduction, this one fails.
        [UnityTest]
        public IEnumerator DiffuseHeat_HigherSolidRate_ConductsMoreIntoASolid()
        {
#if UNITY_EDITOR
            const int res = 3;
            int HOT   = 1 * res + 0;   // (0,1) hot fluid neighbour
            int SOLID = 1 * res + 1;   // (1,1) the solid receiving conduction

            var heat = new float[res * res];   // everything cold except the one hot neighbour
            heat[HOT] = 1f;

            var obstacle = new float[res * res];
            obstacle[SOLID] = 1f;

            // Same dt and geometry; ONLY the solid conduction rate differs.
            float[] oldRate = DispatchDiffuseHeatGrid(heat, obstacle, res, diffusion: 2f,
                diffusionSolid: 12f, dt: 1f / 60f);
            float[] newRate = DispatchDiffuseHeatGrid(heat, obstacle, res, diffusion: 2f,
                diffusionSolid: 30f, dt: 1f / 60f);
            yield return null;

            Assert.That(oldRate[SOLID], Is.GreaterThan(0f),
                "SANITY: conduction must deliver SOME heat into the solid even at the old rate — " +
                "DiffuseHeat is never a barrier.");
            Assert.That(newRate[SOLID], Is.GreaterThan(oldRate[SOLID] + 1e-3f),
                $"CP8aa: raising thermalDiffusionSolid must increase conduction into a solid. " +
                $"12 -> {oldRate[SOLID]:0.0000}, 30 -> {newRate[SOLID]:0.0000}. If this fails, the retune " +
                "cannot help strict-mode melting and the Fire-vs-Ice result will not move.");

            // Bounded, not runaway: the kernel blends toward the neighbour AVERAGE, so a solid can never
            // exceed the hottest neighbour no matter how high the rate goes. This is why the retune is
            // numerically safe at any magnitude.
            Assert.That(newRate[SOLID], Is.LessThanOrEqualTo(1f + 1e-4f),
                "conduction is a convex blend toward the neighbour average — it must never overshoot.");
#else
            yield break;
#endif
        }

        // NOTE (CP8q-fix, CKPT-085): the RED TARGET for Lake's bug now lives in
        // ThermalInteractionsTests.ObstacleStrengthIce_UnderFireContact_ActuallyMelts_NotJustWarms.
        //
        // A heat-only version originally sat here: it asserted `faceHeat > meltThreshold` while its
        // comment claimed it asserted ice mass. That was an overclaim, and it did not measure the reported
        // bug — "the temperature never rises high enough to TRIGGER MELTING" is a claim about ICE MASS, so
        // the test must dispatch ThermalInteractions and watch ice fall / water rise. It was MOVED (not
        // duplicated) so there is exactly one authoritative target, living where the rule-dispatch helper
        // RunRules already exists.

#if UNITY_EDITOR
        /// <summary>
        /// Reproduces ApplyObstacleBoundary's velocity treatment on the CPU: zero inside solids, and clip
        /// any fluid velocity component pointing INTO an adjacent solid. Using this (rather than a uniform
        /// velocity everywhere) is what makes the CP8q tests honest about runtime conditions.
        /// </summary>
        private static Vector2[] ClipVelocityLikeRuntime(Vector2 flow, float[] obstacle, int res)
        {
            var v = new Vector2[res * res];
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int i = y * res + x;
                    if (obstacle[i] > 0.5f) { v[i] = Vector2.zero; continue; }   // solid: no motion at all

                    Vector2 f = flow;
                    int right = y * res + Mathf.Min(x + 1, res - 1);
                    int left = y * res + Mathf.Max(x - 1, 0);
                    int up = Mathf.Min(y + 1, res - 1) * res + x;
                    int down = Mathf.Max(y - 1, 0) * res + x;
                    if (obstacle[right] > 0.5f) f.x = Mathf.Min(f.x, 0f);        // no flow into solid
                    if (obstacle[left] > 0.5f) f.x = Mathf.Max(f.x, 0f);
                    if (obstacle[up] > 0.5f) f.y = Mathf.Min(f.y, 0f);
                    if (obstacle[down] > 0.5f) f.y = Mathf.Max(f.y, 0f);
                    v[i] = f;
                }
            }
            return v;
        }

        /// <summary>AdvectHeat with an explicit PER-CELL velocity field (not one uniform vector).</summary>
        private static float[] DispatchAdvectHeatGridWithField(float[] heat, float[] obstacle, Vector2[] vel,
            int res, float dt, float dissipation, float ambient, float minTemp = 0f, float maxHeat = 1f,
            float solidPermeability = 1f, int heatObstacleMode = 1)   // CP8z: default legacy, see other harness
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.inktools.sim/Compute/Fluids.compute");
            int kernel = cs.FindKernel("AdvectHeat");

            var hr = MakeSeededRT(res, RenderTextureFormat.RHalf, heat);
            var hw = MakeSeededRT(res, RenderTextureFormat.RHalf, new float[res * res]);
            var obs = MakeSeededRT(res, RenderTextureFormat.RFloat, obstacle);

            var velRT = new RenderTexture(res, res, 0, RenderTextureFormat.RGHalf) { enableRandomWrite = true };
            velRT.Create();
            var velTex = new Texture2D(res, res, TextureFormat.RGBAFloat, false);
            var prevActive = RenderTexture.active;
            try
            {
                var px = new Color[res * res];
                for (int i = 0; i < px.Length; i++) px[i] = new Color(vel[i].x, vel[i].y, 0f, 0f);
                velTex.SetPixels(px);
                velTex.Apply();
                Graphics.Blit(velTex, velRT);
                RenderTexture.active = prevActive;

                cs.SetVector("_SimulationSize", new Vector2(res, res));
                cs.SetFloat("_FrameDeltaTime", dt);
                cs.SetFloat("_ThermalDissipation", dissipation);
                cs.SetFloat("_AmbientTemperature", ambient);
                cs.SetFloat("_MinTemperature", minTemp);
                cs.SetFloat("_MaxHeat", maxHeat);
                // CP8q: ALWAYS set — uniforms persist between dispatches, so leaving this unset would
                // inherit whatever a prior test left behind (the stale-uniform class CP8 keeps hitting).
                cs.SetFloat("_ThermalSolidPermeability", solidPermeability);
                cs.SetInt("_HeatObstacleMode", heatObstacleMode);   // CP8z: 0 strict, 1 legacy advective
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
                RenderTexture.active = null;
                hr.Release(); hw.Release(); obs.Release(); velRT.Release();
                Object.DestroyImmediate(hr); Object.DestroyImmediate(hw); Object.DestroyImmediate(obs);
                Object.DestroyImmediate(velRT); Object.DestroyImmediate(velTex);
            }
        }
#endif

        // CP8z: pins the MODE-DEPENDENT heat-velocity wiring, which no GPU test in this file can observe.
        //
        // The obstacle-heat model lives in FluidSolver's binding, not a kernel. In the DEFAULT strict mode
        // AdvectHeat binds the CLIPPED ctx.Velocity.Read — heat does not advect through solids at all, so
        // there is deliberately no pre-boundary field to punch it through. The legacy CP8q advective path
        // (VelocityThermal snapshot) survives only for _HeatObstacleMode == 1, so the snapshot + ClearAll
        // zeroing infrastructure below is retained and still asserted — but it must be gated on the mode.
        // The runtime proof is the strict-vs-legacy FireIceScenario A/B.
        [Test]
        public void FluidSolver_BindsHeatVelocityPerObstacleMode_StrictUsesClippedField()
        {
#if UNITY_EDITOR
            string src = System.IO.File.ReadAllText(
                "Assets/_Project/Scripts/Systems/SimulationLOD0/Core/FluidSolver.cs");

            // The mode uniform must be uploaded every dispatch, or a stale value carries over (the
            // recurring persistent-uniform trap in this subsystem).
            StringAssert.Contains("SetInt(\"_HeatObstacleMode\"", src,
                "FluidSolver must upload _HeatObstacleMode so the shader knows which model to run.");

            // The binding is mode-conditional: legacy (mode 1) may use the pre-boundary snapshot; strict
            // (the default) must fall back to the clipped ctx.Velocity.Read, so heat has no unclipped
            // field to ride through a solid.
            StringAssert.Contains("legacyAdvectiveHeat && ctx.VelocityThermal != null) ? ctx.VelocityThermal : ctx.Velocity.Read", src,
                "AdvectHeat must bind the pre-boundary snapshot ONLY in legacy advective mode; strict mode " +
                "must bind the clipped ctx.Velocity.Read so heat cannot advect through a solid.");
            StringAssert.Contains("ctx.HeatObstacleMode == 1", src,
                "the legacy-advective branch must be keyed on HeatObstacleMode == 1, not always-on.");

            // The VelocityThermal snapshot infrastructure is RETAINED (legacy mode needs it). CP8q
            // (CKPT-094): taken with Graphics.CopyTexture, not Blit — a straight GPU copy, no render pass.
            const string Snapshot = "Graphics.CopyTexture(ctx.Velocity.Read, ctx.VelocityThermal)";

            StringAssert.Contains(Snapshot, src,
                "The pre-boundary snapshot must remain for legacy-advective mode, taken BEFORE " +
                "ApplyObstacleBoundary clips velocity — the only point an unclipped field exists.");

            // Determinism guard (CKPT-093 blocker): VelocityThermal must be zeroed alongside the velocity
            // buffers in ClearAll, so its first use after allocation or a mid-run reset advects nothing
            // rather than whatever the RT happened to contain.
            //
            // CKPT-095: this is a SCOPED check, not a bare Contains. Targeting the RT alone
            // (`RenderTexture.active = ctx.VelocityThermal;`) does not prove anything was zeroed, and a
            // bare `Contains("GL.Clear(...)")` is just as useless in the other direction — that exact
            // string appears THREE times in FluidSolver.cs (obstacles, creature buffer, reaction impulse),
            // so it would still pass with the VelocityThermal clear deleted. Same weak-anchor trap as the
            // ordering guard fixed in CKPT-094. So: find the VelocityThermal target, then require an
            // actual clear BETWEEN it and the active-state restore that closes the block.
            const string ThermalTarget = "RenderTexture.active = ctx.VelocityThermal;";
            const string ActiveRestore = "RenderTexture.active = prevRT;";

            int target = src.IndexOf(ThermalTarget, System.StringComparison.Ordinal);
            Assert.That(target, Is.GreaterThan(0),
                "ClearAll must target VelocityThermal so its first use after reset is deterministic");

            int restore = src.IndexOf(ActiveRestore, target, System.StringComparison.Ordinal);
            Assert.That(restore, Is.GreaterThan(target),
                "the VelocityThermal clear block must restore the previous active RenderTexture");

            string clearBlock = src.Substring(target, restore - target);
            StringAssert.Contains("GL.Clear(", clearBlock,
                "VelocityThermal must actually be ZEROED — merely setting it as the active RenderTexture " +
                "proves nothing. Without a real clear, the snapshot's first use after allocation or a " +
                "mid-run reset advects whatever the RT happened to contain, which is exactly the " +
                "non-determinism this guard exists to prevent.");

            // Ordering guard: the snapshot must precede the boundary dispatch, or it captures clipped data
            // and the whole fix silently becomes a no-op.
            //
            // CKPT-094: the anchor is the DISPATCH call, not a texture binding. This guard previously
            // anchored on `_VelocityWrite", ctx.Velocity.Write`, which occurs EIGHT times in this file
            // (advection, diffusion, vorticity, pressure, …) — IndexOf matched the first of those, far
            // above the snapshot, so the assertion was comparing against an unrelated kernel and had never
            // actually verified the ordering it claimed to. `Dispatch(ctx.FluidKernelApplyObstacleBoundary`
            // appears only at the three real boundary dispatches, so the first of those is the correct
            // "first clip" marker.
            const string BoundaryDispatch = "Dispatch(ctx.FluidKernelApplyObstacleBoundary";

            int snapshot = src.IndexOf(Snapshot, System.StringComparison.Ordinal);
            int boundary = src.IndexOf(BoundaryDispatch, System.StringComparison.Ordinal);
            Assert.That(snapshot, Is.GreaterThan(0), "snapshot call not found");
            Assert.That(boundary, Is.GreaterThan(0), "ApplyObstacleBoundary dispatch not found");
            Assert.That(snapshot, Is.LessThan(boundary),
                $"The snapshot (index {snapshot}) must come BEFORE the first ApplyObstacleBoundary " +
                $"dispatch (index {boundary}), or it captures already-clipped velocity and the entire " +
                "pre-boundary fix silently becomes a no-op.");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // CP8m REGRESSION GUARD — this is the bug that ate a whole playtest, so it gets a test.
        //
        // An unset compute-shader float is ZERO. `_ThermalDiffusionSolid` is uploaded by FluidSolver
        // (C#), while Heat.hlsl (HLSL) compiles SEPARATELY. When the C# assembly failed to compile,
        // Unity silently kept running the last good build — which never uploaded the new uniform — while
        // the shader happily read it. Every obstacle cell got conduction rate 0, so ICE BECAME A PERFECT
        // INSULATOR: strictly worse than the CP4 behaviour three checkpoints were spent removing, and it
        // failed silently. Lake played that build and reported "I still don't see the heat advecting into
        // the ice", which was entirely correct.
        //
        // The kernel now takes max(solid, fluid). "Solids conduct at least as readily as fluid" IS the
        // design intent, so encoding it in the shader — rather than trusting the host to upload a sane
        // value — makes an unset/zero solid rate degrade gracefully to the fluid rate instead of
        // silently sealing heat out of every solid.
        [UnityTest]
        public IEnumerator DiffuseHeat_SolidRateNeverConductsSlowerThanFluid_EvenIfUnset()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 1f;
            heat[C] = 0f;
            var solid = new float[res * res]; solid[C] = 1f;

            // solidRate 0 == "the uniform was never uploaded". The cell must STILL take up heat.
            float[] unset = DispatchDiffuseHeatGrid(heat, solid, res,
                diffusion: 2f, diffusionSolid: 0f, dt: 1f / 60f);
            yield return null;

            Assert.That(unset[C], Is.GreaterThan(1e-3f),
                "A solid cell with an UNSET (zero) solid conduction rate must fall back to the fluid " +
                "rate, NOT become a perfect insulator. This exact failure — shader compiled, C# did " +
                "not, uniform defaulted to 0 — made ice unheatable for an entire playtest.");

            float[] fluid = DispatchDiffuseHeatGrid(heat, new float[res * res], res,
                diffusion: 2f, dt: 1f / 60f);
            Assert.That(unset[C], Is.EqualTo(fluid[C]).Within(2e-2f),
                "…degrading to exactly the fluid rate");
#else
            yield break;
#endif
        }

        // CP8m (Codex note 3). The kernel picks its per-cell rate itself, so the DISPATCH GUARD only needs
        // to know whether any conduction is wanted at all — and it must therefore consider BOTH rates.
        // Guarding on the fluid rate alone meant an "only solids conduct" config (thermalDiffusion = 0,
        // thermalDiffusionSolid > 0) would skip the dispatch entirely and conduct nothing ANYWHERE,
        // including in the very solids it was enabling. Same failure shape as the bug that cost a
        // playtest: not an error, just a heat path that silently does nothing.
        [Test]
        public void FluidSolver_DispatchesDiffuseHeat_IfEitherConductionRateIsPositive()
        {
#if UNITY_EDITOR
            const string path = "Assets/_Project/Scripts/Systems/SimulationLOD0/Core/FluidSolver.cs";
            string src = System.IO.File.ReadAllText(path);

            StringAssert.Contains("ctx.ThermalDiffusion > 0f || ctx.ThermalDiffusionSolid > 0f", src,
                "The DiffuseHeat dispatch must run when EITHER conduction rate is positive. Gating on the " +
                "fluid rate alone silently disables solid conduction too — so 'only solids conduct' would " +
                "conduct nothing at all, failing quietly rather than loudly.");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // Lake: "Heat should travel even more readily through solids than it does in the open fluids."
        // Physically right — ice and rock conduct better than the fluid around them — and it is the ONLY
        // lever for heating a solid, because ApplyObstacles ZEROES the velocity inside every obstacle
        // cell, so advection's back-trace there is a no-op and can never carry heat in. Conduction is not
        // merely the better path into a solid; it is the ONLY path.
        [UnityTest]
        public IEnumerator DiffuseHeat_ConductsFasterThroughSolidsThanFluid()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;

            var heat = new float[res * res];
            for (int i = 0; i < heat.Length; i++) heat[i] = 1f;
            heat[C] = 0f;                       // the cold cell we will heat, once as fluid, once as solid

            var open = new float[res * res];
            var solid = new float[res * res]; solid[C] = 1f;

            // Same fluid rate in both runs; the ONLY difference is whether the centre is masked solid.
            float[] asFluid = DispatchDiffuseHeatGrid(heat, open, res,
                diffusion: 2f, diffusionSolid: 6f, dt: 1f / 60f);
            float[] asSolid = DispatchDiffuseHeatGrid(heat, solid, res,
                diffusion: 2f, diffusionSolid: 6f, dt: 1f / 60f);
            yield return null;

            Assert.That(asSolid[C], Is.GreaterThan(asFluid[C] + 1e-3f),
                "A SOLID cell must take up heat FASTER than the same cell would as open fluid — not " +
                "merely at the same rate. This is what lets a block of ice heat THROUGH rather than " +
                "only skinning at its surface, so it keeps melting without a constant fire stream.");
#else
            yield break;
#endif
        }

        // 10. CP8k — INVERTED. This test used to assert the OPPOSITE: that a back-trace crossing a solid
        // must NOT carry heat, and that an obstacle cell never pulls heat in. That no-flux rule was the
        // bug Lake reported as "not enough heat advecting into obstacles", and it is the same modelling
        // error CP8d already fixed for conduction, merely left behind in advection.
        //
        // An ink obstacle is MATTER, not vacuum. Ice and plant are solid ink sitting IN the fluid, and
        // hot fluid flowing against them must be able to deposit heat into them — that is how a flame
        // held against ice melts it. Sealing obstacle cells off from advection left conduction as the
        // only way in, and conduction alone could not outpace the melt drawing heat back out, so ice
        // sat frozen forever. Heat must now flow into and across obstacle cells.
        [UnityTest]
        public IEnumerator AdvectHeat_CarriesHeatIntoObstacleCells()
        {
#if UNITY_EDITOR
            const int res = 3;
            int SRC = 1 * res + 0;   // (0,1) hot fluid
            int SOLID = 1 * res + 1; // (1,1) the ice/plant cell, masked as an obstacle
            int DST = 1 * res + 2;   // (2,1) beyond it; velocity (2,0) back-traces toward (0,1)

            var heat = new float[res * res]; heat[SRC] = 1f;
            var solid = new float[res * res]; solid[SOLID] = 1f;
            var open = new float[res * res];
            var vel = new Vector2(2f, 0f);

            // dissipation 1.0 => retention 1.0 => no relaxation, so we measure transport alone.
            float[] withSolid = DispatchAdvectHeatGrid(heat, solid, vel, res, 1f, 1f, 0f);

            // The kernel must not GATE on the mask. This test hands it a non-zero velocity everywhere,
            // including inside the solid, so the obstacle cell's back-trace reaches the hot source and it
            // warms — previously it early-outed and stayed at 0 forever.
            //
            // Scope note: this proves the KERNEL has no obstacle special-casing left. It does not by
            // itself prove heat reaches ice in the live solver, because it supplies its own velocity.
            //
            // SUPERSEDED (CP8q): the CP8m version of this note went further and asserted that heat can
            // NEVER advect into real ice, "and nothing can, because inside a solid there is no fluid
            // motion" — concluding conduction was the only way in. That treated an implementation
            // consequence as a law of nature, and Lake has since set the design intent explicitly: "I do
            // want the heat to advect through the obstacle ice." The zeroed velocity is a MASS boundary
            // condition; heat is energy and need not inherit it. CP8q snapshots velocity BEFORE
            // ApplyObstacleBoundary clips it and advects heat with that.
            Assert.That(withSolid[SOLID], Is.GreaterThan(0.5f),
                "With a non-zero velocity supplied, an obstacle cell must not be gated out of advection — " +
                "the kernel must contain no obstacle special-case at all");

            // …and heat must no longer be forbidden from crossing it.
            Assert.That(withSolid[DST], Is.GreaterThan(0.5f),
                "Heat must not be blocked from advecting ACROSS an ink obstacle either");

            // Control: identical to the no-obstacle case, proving the mask no longer gates heat at all.
            float[] control = DispatchAdvectHeatGrid(heat, open, vel, res, 1f, 1f, 0f);
            Assert.That(withSolid[DST], Is.EqualTo(control[DST]).Within(3e-2f),
                "With the no-flux rule gone, the obstacle mask must have NO effect on heat advection");

            yield return null;
#else
            yield break;
#endif
        }
    }
}
