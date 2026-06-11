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
    public class PaletteLookupTests
    {
        [UnityTest]
        public IEnumerator StampParticles_RespectsPaletteWithHighDensityMultiplier()
        {
#if UNITY_EDITOR
            var stamp = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/SimulationLOD0/StampParticlesCompute.compute");
            Assert.IsNotNull(stamp, "StampParticlesCompute not found");

            int kernel = stamp.FindKernel("StampParticles");

            // 1x1 particle grid
            var particles = new iparticle[1];
            using var buffer = new ComputeBuffer(1, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            // 1x1 red stamp texture
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            tex.SetPixel(0, 0, new Color(1f, 0f, 0f, 1f));
            tex.Apply();

            // Palette: fire key color with tolerance
            Vector4[] palette = new Vector4[10];
            palette[0] = new Vector4(1f, 0.3f, 0f, 0.35f);

            stamp.SetBuffer(kernel, "_ParticlesRW", buffer);
            stamp.SetTexture(kernel, "_StampTex", tex);
            stamp.SetVector("_StampCenter", new Vector4(0.5f, 0.5f, 0f, 0f));
            stamp.SetVector("_StampSize", new Vector4(1f, 1f, 0f, 0f));
            stamp.SetFloat("_AlphaThreshold", 0f);
            stamp.SetFloat("_DensityMul", 5f); // previously caused mismatch
            stamp.SetFloat("_UseOverride", 0f);
            stamp.SetVector("_OverrideColor", Vector4.zero);
            stamp.SetVector("_Resolution", new Vector2(1, 1));
            stamp.SetVectorArray("_InkKeyColors", palette);
            stamp.SetInt("_NumActiveInks", 1);
            stamp.SetInt("_UsePaletteLookup", 1);

            stamp.Dispatch(kernel, 1, 1, 1);
            yield return null;

            buffer.GetData(particles);
            Assert.That(particles[0].fire, Is.EqualTo(5f).Within(0.001f),
                "Fire channel should receive stamp alpha scaled by density multiplier");
#else
            yield break;
#endif
        }
    }
}