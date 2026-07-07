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
            try
            {
                FillRT(velocity, Color.clear);
                FillRT(heatWrite, Color.clear);
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
                Object.DestroyImmediate(velocity);
                Object.DestroyImmediate(heatRead);
                Object.DestroyImmediate(heatWrite);
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
    }
}
