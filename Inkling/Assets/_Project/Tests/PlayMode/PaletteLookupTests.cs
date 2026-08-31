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
            Vector4[] palette = new Vector4[11]; // Count-sized to match _InkKeyColors[11]
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

        // RED for Codex M0 blocker: an inactive (tolerance 0) palette slot must NOT match even on an EXACT
        // color hit. Proves the M0 Metal slot (Count-sized palette, _NumActiveInks=11, Metal tolerance 0) is
        // not color-stampable and does not fall back to BlackBody/Ice. The exact hit is produced via the
        // artist-override path (matchColor = _OverrideColor.rgb), NOT RGBA32 texture sampling, so matchColor
        // equals the palette value bit-for-bit and dist is truly 0. Before the shader `tolerance <= 0` skip,
        // that hit passed `dist <= tolerance*1.732` (0 <= 0) and wrote p.metal; after the skip it writes nothing.
        [UnityTest]
        public IEnumerator StampParticles_InactiveMetalSlot_ExactColorMatch_DoesNotWriteMetal()
        {
#if UNITY_EDITOR
            var stamp = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/SimulationLOD0/StampParticlesCompute.compute");
            Assert.IsNotNull(stamp, "StampParticlesCompute not found");
            int kernel = stamp.FindKernel("StampParticles");

            var particles = new iparticle[1];
            using var buffer = new ComputeBuffer(1, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            // The EXACT M0 Metal placeholder color. Fed via _OverrideColor (below), so the shader's matchColor
            // is this value verbatim — no RGBA32 quantization of 0.65 that would make dist slightly > 0 and
            // let the test pass regardless of the fix.
            var metalColor = new Vector4(0.6f, 0.6f, 0.65f, 1f);

            // Texture supplies ONLY alpha coverage; its RGB is irrelevant because _UseOverride=1 sources the
            // match hue from _OverrideColor.
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            tex.SetPixel(0, 0, new Color(1f, 1f, 1f, 1f));
            tex.Apply();

            // Count-sized palette (11). Metal slot (index 10) carries the EXACT override color but tolerance
            // 0 = INACTIVE; all other slots left zero/tolerance 0 (also inactive).
            Vector4[] palette = new Vector4[11];
            palette[(int)InkTypeId.Metal] = new Vector4(metalColor.x, metalColor.y, metalColor.z, 0f);

            stamp.SetBuffer(kernel, "_ParticlesRW", buffer);
            stamp.SetTexture(kernel, "_StampTex", tex);
            stamp.SetVector("_StampCenter", new Vector4(0.5f, 0.5f, 0f, 0f));
            stamp.SetVector("_StampSize", new Vector4(1f, 1f, 0f, 0f));
            stamp.SetFloat("_AlphaThreshold", 0f);
            stamp.SetFloat("_DensityMul", 5f);
            stamp.SetFloat("_UseOverride", 1f);                // exact-match path: matchColor = _OverrideColor.rgb
            stamp.SetVector("_OverrideColor", metalColor);     // exact metal hue == the palette slot color
            stamp.SetVector("_Resolution", new Vector2(1, 1));
            stamp.SetVectorArray("_InkKeyColors", palette);
            stamp.SetInt("_NumActiveInks", 11);   // Count active; Metal slot present but tolerance 0
            stamp.SetInt("_UsePaletteLookup", 1);

            stamp.Dispatch(kernel, 1, 1, 1);
            yield return null;

            buffer.GetData(particles);
            Assert.That((float)particles[0].metal, Is.EqualTo(0f).Within(1e-4f),
                "An exact match against the tolerance-0 (inactive) Metal slot must NOT write p.metal (M0: Metal not color-stampable).");
            Assert.That((float)particles[0].blackBody, Is.EqualTo(0f).Within(1e-4f),
                "Inactive Metal match must not fall back to BlackBody.");
            Assert.That((float)particles[0].ice, Is.EqualTo(0f).Within(1e-4f),
                "Inactive Metal match must not fall back to Ice.");
#else
            yield break;
#endif
        }
    }
}