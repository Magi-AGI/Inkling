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
    /// M3a RED tests: painted true Metal (iparticle.metal, index 10) above its obstacle threshold must mark
    /// the ink obstacle mask via InkToObstacles, so metal blocks fluid flow like a solid wall.
    ///
    /// RED on the M2 baseline: Obstacles.hlsl declares _ObstacleThresholdFire.._ObstacleThresholdIce only and
    /// InkToObstacles enumerates fire..ice — there is NO _ObstacleThresholdMetal uniform and NO p.metal branch,
    /// so metal never marks the mask. GREEN once M3a adds the uniform + branch (InkTools) and the FluidSolver
    /// upload (Inkling). BlackBody obstacle behavior stays independent (must not become a metal surrogate).
    ///
    /// NOTE: SetFloat("_ObstacleThresholdMetal", ...) is a harmless no-op until the uniform exists. The
    /// BlackBody-independence test is GREEN on the current baseline and doubles as a harness-correctness proof:
    /// if it is RED too, the dispatch setup is broken rather than the metal wiring being merely absent.
    /// </summary>
    public class MetalObstacleTests
    {
#if UNITY_EDITOR
        private const string FluidsPath = "Packages/com.inktools.sim/Compute/Fluids.compute";

        private static ComputeShader LoadFluids()
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(FluidsPath);
            Assert.IsNotNull(cs, "Fluids.compute should load");
            return cs;
        }

        // Disable EVERY per-ink obstacle threshold so a dispatch can't inherit stale state (compute uniforms
        // persist on the shared ComputeShader asset between dispatches). Callers re-enable only what they test.
        private static void DisableAllObstacleThresholds(ComputeShader cs)
        {
            cs.SetFloat("_ObstacleThresholdFire", 0f);
            cs.SetFloat("_ObstacleThresholdWater", 0f);
            cs.SetFloat("_ObstacleThresholdPlantSeeded", 0f);
            cs.SetFloat("_ObstacleThresholdPlantGrown", 0f);
            cs.SetFloat("_ObstacleThresholdSteam", 0f);
            cs.SetFloat("_ObstacleThresholdGlitter", 0f);
            cs.SetFloat("_ObstacleThresholdBlackBody", 0f);
            cs.SetFloat("_ObstacleThresholdElectricitySeeded", 0f);
            cs.SetFloat("_ObstacleThresholdElectricityGrown", 0f);
            cs.SetFloat("_ObstacleThresholdIce", 0f);
            cs.SetFloat("_ObstacleThresholdMetal", 0f); // no-op until M3a declares it; keeps state clean
        }

        // Dispatch InkToObstacles on a 1x1 grid with the given particle; return the obstacle-mask value.
        private static float DispatchInkToObstacles(ComputeShader cs, iparticle p)
        {
            const int res = 1;
            int kernel = cs.FindKernel("InkToObstacles");

            var particles = new iparticle[res * res];
            particles[0] = p;

            var buffer = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            var obstacleWrite = NewClearedObstacleRT(res);
            try
            {
                buffer.SetData(particles);
                // InkToObstacles only reads _SimParams.simulationSize (via INIT_PARAMS) and writes 1.0
                // additively; other INIT_PARAMS uniforms are unused by this kernel.
                cs.SetVector("_SimulationSize", new Vector2(res, res));
                cs.SetBuffer(kernel, "_ParticlesRead", buffer);
                cs.SetTexture(kernel, "_ObstacleWrite", obstacleWrite);
                cs.Dispatch(kernel, 1, 1, 1);
                return ReadCenterR(obstacleWrite);
            }
            finally
            {
                RenderTexture.active = null;
                buffer.Release();
                obstacleWrite.Release();
                Object.DestroyImmediate(obstacleWrite);
            }
        }

        // RFloat, random-write, cleared to 0 — InkToObstacles is additive (only writes 1.0, never clears),
        // so the mask MUST start at 0 for the negative assertions to be meaningful.
        private static RenderTexture NewClearedObstacleRT(int res)
        {
            var rt = new RenderTexture(res, res, 0, RenderTextureFormat.RFloat) { enableRandomWrite = true };
            rt.Create();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
            return rt;
        }

        private static float ReadCenterR(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
            try
            {
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex.GetPixel(0, 0).r;
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
            }
        }
#endif

        // RED on baseline: no p.metal branch exists, so metal never marks the mask -> returns 0, assertion fails.
        [UnityTest]
        public IEnumerator InkToObstacles_MetalAboveThreshold_MarksObstacle()
        {
#if UNITY_EDITOR
            var cs = LoadFluids();
            DisableAllObstacleThresholds(cs);
            cs.SetFloat("_ObstacleThresholdMetal", 0.5f);

            var p = new iparticle();
            p.metal = IFloatTestValue.FromFloat(0.6f);

            float mask = DispatchInkToObstacles(cs, p);
            yield return null;
            Assert.That(mask, Is.EqualTo(1f).Within(1e-4f),
                "Metal (0.6) >= _ObstacleThresholdMetal (0.5) must mark the ink obstacle mask (1.0). " +
                "RED until M3a adds the Obstacles.hlsl metal branch + FluidSolver upload.");
#else
            yield break;
#endif
        }

        // Threshold-boundary guard: below the metal threshold, the mask must stay 0.
        [UnityTest]
        public IEnumerator InkToObstacles_MetalBelowThreshold_DoesNotMarkObstacle()
        {
#if UNITY_EDITOR
            var cs = LoadFluids();
            DisableAllObstacleThresholds(cs);
            cs.SetFloat("_ObstacleThresholdMetal", 0.5f);

            var p = new iparticle();
            p.metal = IFloatTestValue.FromFloat(0.1f);

            float mask = DispatchInkToObstacles(cs, p);
            yield return null;
            Assert.That(mask, Is.EqualTo(0f).Within(1e-4f),
                "Metal (0.1) < _ObstacleThresholdMetal (0.5) must NOT mark the obstacle mask.");
#else
            yield break;
#endif
        }

        // Independence guard (GREEN on baseline; also proves the harness works): a BlackBody-only cell marks
        // via its OWN threshold with Metal disabled — BlackBody must never depend on or become Metal.
        [UnityTest]
        public IEnumerator InkToObstacles_BlackBodyThreshold_RemainsIndependentFromMetal()
        {
#if UNITY_EDITOR
            var cs = LoadFluids();
            DisableAllObstacleThresholds(cs);
            cs.SetFloat("_ObstacleThresholdMetal", 0f);        // Metal OFF
            cs.SetFloat("_ObstacleThresholdBlackBody", 0.5f);  // BlackBody ON

            var p = new iparticle();
            p.blackBody = IFloatTestValue.FromFloat(0.6f);

            float mask = DispatchInkToObstacles(cs, p);
            yield return null;
            Assert.That(mask, Is.EqualTo(1f).Within(1e-4f),
                "BlackBody (0.6) >= _ObstacleThresholdBlackBody (0.5) must mark the mask regardless of Metal, " +
                "proving BlackBody obstacle behavior is independent of Metal.");
#else
            yield break;
#endif
        }
    }
}
