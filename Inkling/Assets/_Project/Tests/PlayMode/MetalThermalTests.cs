using System.Runtime.InteropServices;
using System.Collections;
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
    /// M3b RED tests: thin/sub-obstacle true Metal must conduct heat at the generic SOLID rate (like thin
    /// ice), via a concentration-gated thermal-solid path in DiffuseHeat (_ThermalSolidThresholdMetal, 0.1).
    ///
    /// Baseline (M3a): Heat.hlsl DiffuseHeat classifies thermal-solid as `iceThermalSolid || IsObstacle>0.5`.
    /// Metal below the 0.5 flow-obstacle threshold does NOT mark IsObstacle and has no ice-style concentration
    /// path, so it conducts at the FLUID rate. The RED driver below asserts SOLID-rate conduction for
    /// p.metal=0.2 with obstacle mask 0 -> fails today, passes once M3b adds the metal thermal-solid branch.
    ///
    /// Model math (DiffuseHeat): center' = lerp(center, avg4, ConductionBlend(rate, dt)),
    /// ConductionBlend = saturate(1 - exp(-rate*dt)), solidRate = max(_ThermalDiffusionSolid, _ThermalDiffusion).
    /// On a 3x3 grid with center=1 and all neighbors=0, avg4=0 so center' = exp(-rate*dt).
    ///
    /// Non-goals honored: touches DiffuseHeat classification only in the PLAN; these are tests. No AdvectHeat,
    /// no _HeatObstacleMode, no permeability, no layout/render/electricity. BlackBody stays distinct from Metal.
    /// </summary>
    public class MetalThermalTests
    {
#if UNITY_EDITOR
        private const int Res = 3;
        private const float Dt = 0.1f;
        private const float FluidRate = 1f;
        private const float SolidRate = 20f; // max(SolidRate, FluidRate) = 20

        // center=1, neighbors=0 -> center' = exp(-rate*dt)
        private static float ExpectedFluid => Mathf.Exp(-FluidRate * Dt);          // ~0.9048
        private static float ExpectedSolid => Mathf.Exp(-Mathf.Max(SolidRate, FluidRate) * Dt); // ~0.1353

        private static ComputeShader LoadFluids()
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.inktools.sim/Compute/Fluids.compute");
            Assert.IsNotNull(cs, "Fluids.compute should load");
            return cs;
        }

        // Dispatch DiffuseHeat once on a 3x3 grid (center heat 1, neighbors 0) and return the center cell.
        // obstacleSeed is a 9-length mask (index y*Res+x); pass all-0 for "no obstacle".
        private static float DispatchDiffuseHeatCenter(ComputeShader cs, iparticle[] particles, float[] obstacleSeed)
        {
            int kernel = cs.FindKernel("DiffuseHeat");

            var heatSeed = new float[Res * Res]; // all 0 except center
            heatSeed[1 * Res + 1] = 1f;

            var heatRead = MakeSeededRFloat(heatSeed);
            var heatWrite = MakeClearedRFloat();
            var obstacle = MakeSeededRFloat(obstacleSeed);
            var buffer = new ComputeBuffer(Res * Res, Marshal.SizeOf<iparticle>());
            try
            {
                buffer.SetData(particles);

                // Reset EVERY uniform this kernel path can read, so no stale shared-asset state leaks in.
                cs.SetVector("_SimulationSize", new Vector2(Res, Res));
                cs.SetFloat("_FrameDeltaTime", Dt);
                cs.SetFloat("_ThermalDiffusion", FluidRate);
                cs.SetFloat("_ThermalDiffusionSolid", SolidRate);
                cs.SetFloat("_ThermalSolidThresholdIce", 0f);    // ice path OFF (isolate metal/obstacle)
                cs.SetFloat("_ThermalSolidThresholdMetal", 0.1f); // no-op until M3b declares it; set for cleanliness
                cs.SetFloat("_MinTemperature", 0f);
                cs.SetFloat("_MaxHeat", 1f);
                cs.SetFloat("_AmbientTemperature", 0f);

                cs.SetBuffer(kernel, "_ParticlesRead", buffer);
                cs.SetTexture(kernel, "_HeatRead", heatRead);
                cs.SetTexture(kernel, "_HeatWrite", heatWrite);
                cs.SetTexture(kernel, "_ObstacleRead", obstacle);
                cs.Dispatch(kernel, 1, 1, 1);

                return ReadR(heatWrite, 1, 1);
            }
            finally
            {
                RenderTexture.active = null;
                buffer.Release();
                heatRead.Release(); Object.DestroyImmediate(heatRead);
                heatWrite.Release(); Object.DestroyImmediate(heatWrite);
                obstacle.Release(); Object.DestroyImmediate(obstacle);
            }
        }

        private static RenderTexture MakeClearedRFloat()
        {
            var rt = new RenderTexture(Res, Res, 0, RenderTextureFormat.RFloat) { enableRandomWrite = true };
            rt.Create();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
            return rt;
        }

        private static RenderTexture MakeSeededRFloat(float[] seed)
        {
            var rt = new RenderTexture(Res, Res, 0, RenderTextureFormat.RFloat) { enableRandomWrite = true };
            rt.Create();
            var tex = new Texture2D(Res, Res, TextureFormat.RGBAFloat, false);
            var prev = RenderTexture.active;
            try
            {
                for (int y = 0; y < Res; y++)
                    for (int x = 0; x < Res; x++)
                        tex.SetPixel(x, y, new Color(seed[y * Res + x], 0f, 0f, 0f));
                tex.Apply();
                Graphics.Blit(tex, rt);
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
            }
            return rt;
        }

        private static float ReadR(RenderTexture rt, int x, int y)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Res, Res, TextureFormat.RGBAFloat, false);
            try
            {
                tex.ReadPixels(new Rect(0, 0, Res, Res), 0, 0);
                tex.Apply();
                return tex.GetPixel(x, y).r;
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
            }
        }
#endif

        // RED driver: thin metal (0.2, below the 0.5 obstacle threshold, obstacle mask 0) must conduct at the
        // SOLID rate. Baseline has no metal thermal-solid path -> conducts at FLUID rate -> assertion fails.
        [UnityTest]
        public IEnumerator DiffuseHeat_ThinMetal_ConductsAtSolidRate()
        {
#if UNITY_EDITOR
            var cs = LoadFluids();
            var particles = new iparticle[Res * Res];
            particles[1 * Res + 1].metal = IFloatTestValue.FromFloat(0.2f);

            float center = DispatchDiffuseHeatCenter(cs, particles, new float[Res * Res]); // obstacle all 0
            yield return null;

            Assert.That(center, Is.EqualTo(ExpectedSolid).Within(1.5e-2f),
                $"Thin metal (0.2) >= _ThermalSolidThresholdMetal (0.1) with obstacle mask 0 must conduct at the " +
                $"SOLID rate ({ExpectedSolid:F4}), not the fluid rate ({ExpectedFluid:F4}). RED until M3b adds the metal thermal-solid branch.");
#else
            yield break;
#endif
        }

        // Boundary guard: metal below its thermal threshold conducts at the fluid rate. Green before and after.
        [UnityTest]
        public IEnumerator DiffuseHeat_MetalBelowThermalThreshold_UsesFluidRate()
        {
#if UNITY_EDITOR
            var cs = LoadFluids();
            var particles = new iparticle[Res * Res];
            particles[1 * Res + 1].metal = IFloatTestValue.FromFloat(0.05f); // < 0.1

            float center = DispatchDiffuseHeatCenter(cs, particles, new float[Res * Res]);
            yield return null;

            Assert.That(center, Is.EqualTo(ExpectedFluid).Within(1.5e-2f),
                $"Metal (0.05) < _ThermalSolidThresholdMetal (0.1) must conduct at the FLUID rate ({ExpectedFluid:F4}).");
#else
            yield break;
#endif
        }

        // Independence guard: BlackBody is NOT a metal thermal-solid. Green before and after (proves true Metal
        // does not revive the old BlackBody surrogate).
        [UnityTest]
        public IEnumerator DiffuseHeat_BlackBody_IsNotMetalThermalSolid()
        {
#if UNITY_EDITOR
            var cs = LoadFluids();
            var particles = new iparticle[Res * Res];
            particles[1 * Res + 1].blackBody = IFloatTestValue.FromFloat(0.6f); // metal stays 0

            float center = DispatchDiffuseHeatCenter(cs, particles, new float[Res * Res]);
            yield return null;

            Assert.That(center, Is.EqualTo(ExpectedFluid).Within(1.5e-2f),
                $"A BlackBody-only cell (no metal, no obstacle) must conduct at the FLUID rate ({ExpectedFluid:F4}); " +
                $"BlackBody must not be treated as a metal thermal-solid.");
#else
            yield break;
#endif
        }

        // Characterization (GREEN on M3a baseline): dense/obstacle metal already conducts at the solid rate via
        // the existing IsObstacle path. Seed the obstacle mask=1 at the center to characterize that path.
        [UnityTest]
        public IEnumerator DiffuseHeat_ObstacleMetal_AlreadyConductsAtSolidRate()
        {
#if UNITY_EDITOR
            var cs = LoadFluids();
            var particles = new iparticle[Res * Res];
            particles[1 * Res + 1].metal = IFloatTestValue.FromFloat(0.6f);

            var obstacleSeed = new float[Res * Res];
            obstacleSeed[1 * Res + 1] = 1f; // dense metal marks IsObstacle (characterizes M3a's existing path)

            float center = DispatchDiffuseHeatCenter(cs, particles, obstacleSeed);
            yield return null;

            Assert.That(center, Is.EqualTo(ExpectedSolid).Within(1.5e-2f),
                $"An obstacle-mask cell (dense metal) already conducts at the SOLID rate ({ExpectedSolid:F4}) via IsObstacle.");
#else
            yield break;
#endif
        }
    }
}
