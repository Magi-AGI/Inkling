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
    /// Electricity slice 1 — CHARACTERIZATION LOCK for the ParticleToColor "metal/electric" B channel.
    /// This asserts the CURRENT render SURROGATE behavior only: B = electricity lifetime proxy + glitter
    /// dust*0.3. It does NOT assert or imply a true metal ink — "metal" here is a render-channel label, and
    /// there is deliberately NO metal particle field (documented by the reflection assertion below). If a
    /// real metal ink is ever added, that is a separate iparticle layout migration, not this test.
    /// </summary>
    public class ElectricityRenderSurrogateTests
    {
        [Test]
        public void Iparticle_HasNoDedicatedMetalField()
        {
            // 'metal' is only a rendering-channel concept; the particle struct has no metal storage.
            Assert.IsNull(typeof(iparticle).GetField("metal"),
                "No dedicated metal particle field should exist; 'metal' is a ParticleToColor channel label only.");
        }

        [UnityTest]
        public IEnumerator ParticleToColor_BlueChannel_IsElectricityLifetimeProxyPlusDust()
        {
#if UNITY_EDITOR
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Rendering/ParticleToColor.compute");
            Assert.IsNotNull(cs, "ParticleToColor.compute should load");
            int kernel = cs.FindKernel("ParticleToColor");

            const int res = 1;
            // Only electricity + glitter set; fire/water/plant/steam left 0 so R and G stay 0 and B isolates
            // the surrogate. electricityTotal = 0.9 > 0, so contribution = (0.3+0.6)*saturate(0.6/0.9) = 0.6;
            // plus glitter dust 0.2*0.3 = 0.06  ->  expected B = 0.66.
            var particles = new iparticle[res * res];
            particles[0].electricitySeeded = IFloatTestValue.FromFloat(0.3f);
            particles[0].electricityGrown = IFloatTestValue.FromFloat(0.6f);
            particles[0].glitter = IFloatTestValue.FromFloat(0.2f);

            var buffer = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            var output = new RenderTexture(res, res, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true };
            output.Create();
            try
            {
                buffer.SetData(particles);
                cs.SetInt("_Resolution", res);
                cs.SetFloat("_GlobalBrightness", 1f);
                cs.SetBuffer(kernel, "_ParticlesRead", buffer);
                cs.SetTexture(kernel, "_Output", output);
                cs.Dispatch(kernel, 1, 1, 1);
                yield return null;

                Color c = ReadPixel(output);
                Assert.That(c.b, Is.EqualTo(0.66f).Within(2e-2f),
                    "B channel must be electricityGrown lifetime proxy (0.6) + glitter dust*0.3 (0.06) = 0.66.");
                Assert.That(c.r, Is.EqualTo(0f).Within(1e-3f),
                    "electricity/glitter must not bleed into the R (fire) channel.");
                Assert.That(c.g, Is.EqualTo(0f).Within(1e-3f),
                    "electricity/glitter must not bleed into the G (water) channel.");
            }
            finally
            {
                buffer.Release();
                output.Release();
            }
#else
            yield break;
#endif
        }

#if UNITY_EDITOR
        private static Color ReadPixel(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
            try
            {
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                return tex.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = prev;
                Object.DestroyImmediate(tex);
            }
        }
#endif
    }
}
