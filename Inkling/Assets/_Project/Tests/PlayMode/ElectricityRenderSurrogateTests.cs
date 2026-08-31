using System.Runtime.InteropServices;
using System.Collections;
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
    /// True Metal render identity (M1). This file was the "electricity/dust surrogate" lock: before M1 the
    /// ParticleToColor B channel = electricity lifetime proxy + glitter*0.3, and metal had no render path.
    /// M1 binds the render pipeline (ParticleToColor B, ParticleChannelSplat _Channels2.w, and the gradient
    /// shader) to the real iparticle.metal field, so electricity/glitter NO LONGER stand in for metal.
    /// These tests are RED under M0 behavior (metal invisible; B was the electricity surrogate) and GREEN
    /// under M1.
    /// </summary>
    public class ElectricityRenderSurrogateTests
    {
        [Test]
        public void Iparticle_HasDedicatedMetalField()
        {
            Assert.IsNotNull(typeof(iparticle).GetField("metal"),
                "iparticle must have a dedicated 'metal' field (index 10).");
        }

        // M1 outcome #1: Metal.asset exists as a real InkTypeDef at index 10 with an ACTIVE tolerance and a
        // key/debug color distinct from BlackBody, Steam and Ice. RED if the asset is missing/misconfigured.
        [Test]
        public void MetalAsset_HasDistinctIdentity_Index10_ActiveTolerance()
        {
#if UNITY_EDITOR
            var metal = AssetDatabase.LoadAssetAtPath<InkTypeDef>("Assets/_Project/Inks/Metal.asset");
            Assert.IsNotNull(metal, "Metal.asset must exist under Assets/_Project/Inks.");
            Assert.AreEqual(InkTypeId.Metal, metal.inkType, "Metal.asset inkType must be Metal (index 10).");
            Assert.Greater(metal.colorMatchTolerance, 0f, "M1 Metal must have an active (>0) color-match tolerance.");

            var bb = AssetDatabase.LoadAssetAtPath<InkTypeDef>("Assets/_Project/Inks/BlackBody.asset");
            var steam = AssetDatabase.LoadAssetAtPath<InkTypeDef>("Assets/_Project/Inks/Steam.asset");
            var ice = AssetDatabase.LoadAssetAtPath<InkTypeDef>("Assets/_Project/Inks/Ice.asset");
            Assert.Greater(ColorDist(metal.inputKeyColor, bb.inputKeyColor), 0.2f, "Metal key color must be distinct from BlackBody.");
            Assert.Greater(ColorDist(metal.inputKeyColor, steam.inputKeyColor), 0.2f, "Metal key color must be distinct from Steam.");
            Assert.Greater(ColorDist(metal.inputKeyColor, ice.inputKeyColor), 0.2f, "Metal key color must be distinct from Ice.");
#endif
        }

        // M1 RED-capable: B channel is TRUE metal. Under M0 ParticleToColor ignored p.metal, so B would be 0
        // here and this fails; under M1 B == p.metal * brightness.
        [UnityTest]
        public IEnumerator ParticleToColor_BlueChannel_IsTrueMetal()
        {
#if UNITY_EDITOR
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Rendering/ParticleToColor.compute");
            Assert.IsNotNull(cs, "ParticleToColor.compute should load");
            int kernel = cs.FindKernel("ParticleToColor");

            const int res = 1;
            // Only metal set; everything else 0 so R and G stay 0 and B isolates true metal.
            var particles = new iparticle[res * res];
            particles[0].metal = IFloatTestValue.FromFloat(0.7f);

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
                Assert.That(c.b, Is.EqualTo(0.7f).Within(2e-2f),
                    "B channel must be TRUE metal (p.metal=0.7 * brightness=1).");
                Assert.That(c.r, Is.EqualTo(0f).Within(1e-3f), "metal must not bleed into R (fire).");
                Assert.That(c.g, Is.EqualTo(0f).Within(1e-3f), "metal must not bleed into G (water).");
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

        // M1 RED-capable: electricity + glitter must NO LONGER drive the metal/B channel. Under M0 this exact
        // input produced B = 0.66 (the surrogate); under M1 (B = true metal, none present) B must be 0.
        [UnityTest]
        public IEnumerator ParticleToColor_ElectricityAndGlitter_NoLongerDriveMetalChannel()
        {
#if UNITY_EDITOR
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Rendering/ParticleToColor.compute");
            Assert.IsNotNull(cs, "ParticleToColor.compute should load");
            int kernel = cs.FindKernel("ParticleToColor");

            const int res = 1;
            // The exact former-surrogate input (B was 0.66 in M0). No metal present.
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
                Assert.That(c.b, Is.EqualTo(0f).Within(1e-3f),
                    "electricity+glitter must NOT drive B now that B is true metal (was 0.66 in the M0 surrogate).");
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

        // M1 RED-capable: the channel splat packs true metal into _Channels2.w (was a reserved 0 in M0),
        // while electricity still occupies _Channels2.x/y.
        [UnityTest]
        public IEnumerator ChannelSplat_PacksMetalIntoChannels2W_NotReserved()
        {
#if UNITY_EDITOR
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Rendering/ParticleChannelSplat.compute");
            Assert.IsNotNull(cs, "ParticleChannelSplat.compute should load");
            int kernel = cs.FindKernel("ChannelSplat");

            const int res = 1;
            var particles = new iparticle[res * res];
            particles[0].metal = IFloatTestValue.FromFloat(0.5f);
            particles[0].electricitySeeded = IFloatTestValue.FromFloat(0.25f);
            particles[0].electricityGrown = IFloatTestValue.FromFloat(0.75f);

            var buffer = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            var ch0 = NewRT(res);
            var ch1 = NewRT(res);
            var ch2 = NewRT(res);
            var heat = NewRFloat(res);
            try
            {
                buffer.SetData(particles);
                cs.SetInt("_Resolution", res);
                cs.SetBuffer(kernel, "_ParticlesRead", buffer);
                cs.SetTexture(kernel, "_HeatRead", heat);
                cs.SetTexture(kernel, "_Channels0", ch0);
                cs.SetTexture(kernel, "_Channels1", ch1);
                cs.SetTexture(kernel, "_Channels2", ch2);
                cs.Dispatch(kernel, 1, 1, 1);
                yield return null;

                Color c2 = ReadPixel(ch2);
                Assert.That(c2.a, Is.EqualTo(0.5f).Within(1e-3f),
                    "M1: _Channels2.w must carry true metal (0.5), not a reserved 0.");
                Assert.That(c2.r, Is.EqualTo(0.25f).Within(1e-3f),
                    "electricitySeeded must still occupy _Channels2.x.");
                Assert.That(c2.g, Is.EqualTo(0.75f).Within(1e-3f),
                    "electricityGrown must still occupy _Channels2.y.");
            }
            finally
            {
                buffer.Release();
                ch0.Release();
                ch1.Release();
                ch2.Release();
                heat.Release();
            }
#else
            yield break;
#endif
        }

#if UNITY_EDITOR
        private static float ColorDist(Color a, Color b)
        {
            float dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
            return Mathf.Sqrt(dr * dr + dg * dg + db * db);
        }

        private static RenderTexture NewRT(int res)
        {
            var rt = new RenderTexture(res, res, 0, RenderTextureFormat.ARGBFloat) { enableRandomWrite = true };
            rt.Create();
            return rt;
        }

        private static RenderTexture NewRFloat(int res)
        {
            var rt = new RenderTexture(res, res, 0, RenderTextureFormat.RFloat) { enableRandomWrite = true };
            rt.Create();
            return rt;
        }

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
