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
        private static float DispatchAddHeatSources(float fire, float heat0, int enable, float rate, float dt, float maxHeat)
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
                cs.SetFloat("_MaxHeat", maxHeat);
                cs.SetBuffer(kernel, "_ParticlesRead", buf);
                cs.SetTexture(kernel, "_HeatRead", hr);
                cs.SetTexture(kernel, "_HeatWrite", hw);
                cs.Dispatch(kernel, 1, 1, 1);

                return ReadCenterR(hw);
            }
            finally
            {
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
        private static float[] DispatchAdvectHeatGrid(float[] heat, float[] obstacle, Vector2 vel,
            int res, float dt, float dissipation, float ambient)
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
                hr.Release(); hw.Release(); obs.Release(); velRT.Release();
                Object.DestroyImmediate(hr); Object.DestroyImmediate(hw);
                Object.DestroyImmediate(obs); Object.DestroyImmediate(velRT);
            }
        }

        // Dispatch DiffuseHeat over a grid with an obstacle mask; returns the resulting heat grid.
        private static float[] DispatchDiffuseHeatGrid(float[] heat, float[] obstacle, int res, float diffusion)
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
                cs.SetTexture(kernel, "_HeatRead", hr);
                cs.SetTexture(kernel, "_HeatWrite", hw);
                cs.SetTexture(kernel, "_ObstacleRead", obs);
                int g = Mathf.CeilToInt(res / 8f);
                cs.Dispatch(kernel, g, g, 1);
                return ReadAllR(hw, res);
            }
            finally
            {
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
        [UnityTest]
        public IEnumerator FluidSolverClearAll_ClearsBothHeatSides()
        {
#if UNITY_EDITOR
            var ctx = new SimulationContext { Resolution = 32 };
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

                // Seed both heat sides non-zero, then ClearAll must zero both.
                FillRT(ctx.Heat.Read, new Color(0.6f, 0f, 0f, 0f));
                FillRT(ctx.Heat.Write, new Color(0.4f, 0f, 0f, 0f));

                solver.ClearAll();
                yield return null;

                Assert.That(ReadCenterR(ctx.Heat.Read), Is.EqualTo(0f).Within(1e-3f),
                    "ClearAll must clear Heat.Read");
                Assert.That(ReadCenterR(ctx.Heat.Write), Is.EqualTo(0f).Within(1e-3f),
                    "ClearAll must clear Heat.Write");
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

            yield return null;
#else
            yield break;
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
            foreach (var field in new[] { "thermalDissipationHalfLife", "thermalDiffusion",
                "ambientTemperature", "enableHeatSources", "fireHeatEmissionRate", "maxHeat" })
            {
                StringAssert.Contains(field, src, $"SimDriver should serialize {field}");
                StringAssert.Contains($"ctx.", src); // sanity: context copy block present
            }
            StringAssert.Contains("ctx.FireHeatEmissionRate = fireHeatEmissionRate", src,
                "SimDriver must copy thermal source fields into the context");
#else
            Assert.Ignore("Editor-only source assertion");
#endif
        }

        // 9. DiffuseHeat no-flux: an obstacle neighbor does not receive heat, and the heated cell
        // treats that obstacle neighbor as itself. A control run (no obstacle) proves the block is real.
        [UnityTest]
        public IEnumerator DiffuseHeat_ObstacleBlocksNeighborExchange()
        {
#if UNITY_EDITOR
            const int res = 3;
            int C = 1 * res + 1;   // (1,1) heated cell
            int R = 1 * res + 2;   // (2,1) right neighbor / obstacle site

            var heat = new float[res * res]; heat[C] = 1f;
            var wall = new float[res * res]; wall[R] = 1f;
            var open = new float[res * res];

            float[] blocked = DispatchDiffuseHeatGrid(heat, wall, res, 1f);
            Assert.That(blocked[R], Is.EqualTo(0f).Within(2e-2f),
                "Obstacle cell must not receive heat from its neighbor (no-flux).");
            Assert.That(blocked[C], Is.EqualTo(0.25f).Within(3e-2f),
                "Heated cell treats the obstacle neighbor as itself (retains ~0.25).");

            float[] control = DispatchDiffuseHeatGrid(heat, open, res, 1f);
            Assert.That(control[R], Is.EqualTo(0.25f).Within(3e-2f),
                "Without an obstacle the neighbor DOES receive heat (~0.25) — proves the block is real.");

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
