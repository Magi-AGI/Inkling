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
    /// CP8r: EVAPORATIVE COOLING on the Fire+Water contact quench.
    ///
    /// Lake: "when plant catches fire, and we try to douse the flames with water, it's never enough to
    /// completely cancel out the fire." Root cause: this pass removed fire MASS but never HEAT, so a
    /// doused cell stayed pinned near max temperature — above plantIgnitionThreshold (0.75), which kept
    /// regenerating Fire from Plant, and above fireSinkThreshold (0.6), so surviving fire never guttered
    /// out. Vaporising water absorbs latent heat, which is physically how water extinguishes fire.
    ///
    /// These dispatch the REAL InkInteractions kernel. They guard the cooling path itself; the composed
    /// burning-plant dousing behaviour is a separate runtime concern and is NOT claimed here.
    /// </summary>
    public class QuenchCoolingTests
    {
#if UNITY_EDITOR
        private const int Res = 8;

        private static ComputeShader Load()
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/SimulationLOD0/InkInteractions.compute");
            Assert.IsNotNull(cs, "InkInteractions.compute should load");
            return cs;
        }

        /// <summary>ContactReactionsGroup layout: slots Fire, Water, Steam, Ice; col0 = -0.5/-0.5/+1.</summary>
        private static Matrix4x4 QuenchMatrix()
        {
            var m = Matrix4x4.zero;
            m.m00 = -0.5f;   // Fire consumed
            m.m10 = -0.5f;   // Water consumed
            m.m20 = 1.0f;    // Steam produced
            return m;
        }

        /// <summary>An OrganicGroup-style Fire x Plant burn: NOT a quench, must never cool.</summary>
        private static Matrix4x4 PlantBurnMatrix()
        {
            var m = Matrix4x4.zero;
            m.m01 = 0.025f;   // pair 0x2 column produces Fire
            m.m21 = -0.025f;  // …consuming Plant
            return m;
        }

        /// <summary>Runs one dispatch and returns the resulting particles + heat.</summary>
        private static void Dispatch(iparticle[] particles, float seedHeat, Matrix4x4 product,
            int[] inkIndices, float cooling, out iparticle[] outP, out float[] outHeat)
        {
            var cs = Load();
            int kernel = cs.FindKernel("InkInteractions");
            int count = Res * Res;

            var readBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());
            var writeBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());
            readBuf.SetData(particles);
            writeBuf.SetData(particles);

            var impulseRT = new RenderTexture(Res, Res, 0, RenderTextureFormat.RGFloat);
            impulseRT.enableRandomWrite = true; impulseRT.Create();

            var heatRead = MakeHeat(seedHeat);
            var heatWrite = MakeHeat(seedHeat);
            try
            {
                cs.SetInt("_Resolution", Res);
                cs.SetFloat("_DeltaTime", 1f / 60f);
                cs.SetInt("_DebugMode", 0);
                // CKPT-103: ComputeShader uniforms PERSIST across dispatches and across tests, so these
                // sticky flags are set explicitly rather than inherited from whatever ran last. The
                // conservation tests already do this; omitting it is the same stale-uniform trap CP8 has
                // hit repeatedly.
                cs.SetInt("_EnableBlackBodyClearing", 0);
                cs.SetInt("_AccumulateReactionImpulse", 0);
                cs.SetFloat("_BlackBodyThreshold", 0.5f);
                cs.SetFloat("_BlackBodyClearingRate", 0f);
                cs.SetInts("_InkIndices", inkIndices);
                cs.SetMatrix("_ProductMatrix", product);
                cs.SetVector("_ProductCol4", Vector4.zero);
                cs.SetVector("_ProductCol5", Vector4.zero);
                cs.SetFloats("_Weights", 1f, 0f, 0f);          // self-only: isolate the cell
                cs.SetFloat("_RateMultiplier", 10f);
                cs.SetVector("_InteractionThresholds", new Vector4(0.01f, 0.01f, 0.01f, 0.01f));
                cs.SetMatrix("_ReactionImpulseMatrix", Matrix4x4.zero);
                cs.SetVector("_ReactionImpulseCol4", Vector4.zero);
                cs.SetVector("_ReactionImpulseCol5", Vector4.zero);
                cs.SetFloat("_ReactionImpulseGain", 0f);
                cs.SetTexture(kernel, "_ReactionImpulseRW", impulseRT);

                cs.SetFloat("_QuenchCoolingPerUnit", cooling);
                cs.SetFloat("_MinTemperature", 0f);
                cs.SetFloat("_MaxHeat", 1f);
                cs.SetTexture(kernel, "_HeatRead", heatRead);
                cs.SetTexture(kernel, "_HeatWrite", heatWrite);

                cs.SetBuffer(kernel, "_ParticlesRead", readBuf);
                cs.SetBuffer(kernel, "_ParticlesWrite", writeBuf);
                int g = Mathf.CeilToInt(Res / 8f);
                cs.Dispatch(kernel, g, g, 1);

                outP = new iparticle[count];
                writeBuf.GetData(outP);
                outHeat = ReadHeat(heatWrite);
            }
            finally
            {
                RenderTexture.active = null;
                readBuf.Release(); writeBuf.Release();
                impulseRT.Release(); heatRead.Release(); heatWrite.Release();
                Object.DestroyImmediate(impulseRT);
                Object.DestroyImmediate(heatRead); Object.DestroyImmediate(heatWrite);
            }
        }

        private static RenderTexture MakeHeat(float value)
        {
            var rt = new RenderTexture(Res, Res, 0, RenderTextureFormat.RHalf) { enableRandomWrite = true };
            rt.Create();
            var tex = new Texture2D(Res, Res, TextureFormat.RGBAFloat, false);
            var prev = RenderTexture.active;
            try
            {
                var px = new Color[Res * Res];
                for (int i = 0; i < px.Length; i++) px[i] = new Color(value, 0f, 0f, 0f);
                tex.SetPixels(px); tex.Apply();
                Graphics.Blit(tex, rt);
            }
            finally { RenderTexture.active = prev; Object.DestroyImmediate(tex); }
            return rt;
        }

        private static float[] ReadHeat(RenderTexture rt)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(Res, Res, TextureFormat.RGBAFloat, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Res, Res), 0, 0);
                tex.Apply();
                var px = tex.GetPixels();
                var outp = new float[Res * Res];
                for (int i = 0; i < outp.Length; i++) outp[i] = px[i].r;
                return outp;
            }
            finally { RenderTexture.active = prev; Object.DestroyImmediate(tex); }
        }

        private static iparticle[] FireAndWater(float fire, float water)
        {
            var p = new iparticle[Res * Res];
            for (int i = 0; i < p.Length; i++) { p[i].fire = IFloatTestValue.FromFloat(fire); p[i].water = IFloatTestValue.FromFloat(water); }
            return p;
        }
#endif

        // THE FIX. A real Fire+Water quench must remove heat, in addition to converting mass to Steam.
        [UnityTest]
        public IEnumerator Quench_WithCooling_RemovesHeat_AndStillConservesMass()
        {
#if UNITY_EDITOR
            var idx = new[] { (int)InkTypeId.Fire, (int)InkTypeId.Water, (int)InkTypeId.Steam, (int)InkTypeId.Ice };
            var p = FireAndWater(0.6f, 0.6f);
            const float seed = 1f;

            Dispatch(p, seed, QuenchMatrix(), idx, cooling: 1f, out var outP, out var outHeat);
            yield return null;

            Assert.That(outHeat[0], Is.LessThan(seed - 1e-3f),
                $"A real Fire+Water quench must COOL the cell (evaporative/latent heat). " +
                $"heat {seed} -> {outHeat[0]:0.0000}. Without this the cell stays hot enough to keep " +
                "re-igniting plant, which is exactly the dousing bug Lake reported.");

            Assert.That(outHeat[0], Is.GreaterThanOrEqualTo(0f), "cooling must clamp at _MinTemperature");

            // Mass semantics unchanged: fire and water consumed, steam produced, column conserving.
            Assert.That(outP[0].fire, Is.LessThan(0.6f), "quench consumes Fire");
            Assert.That(outP[0].water, Is.LessThan(0.6f), "quench consumes Water");
            Assert.That(outP[0].steam, Is.GreaterThan(0f), "quench produces Steam");
            Assert.That(outP[0].fire + outP[0].water + outP[0].steam,
                Is.EqualTo(1.2f).Within(5e-2f), "Fire+Water->Steam stays mass-conserving");
#else
            yield break;
#endif
        }

        // Cooling must never fire when no quench actually happened — the invariant that keeps it honest.
        [UnityTest]
        public IEnumerator NoQuenchOccurs_LeavesHeatUnchanged()
        {
#if UNITY_EDITOR
            var idx = new[] { (int)InkTypeId.Fire, (int)InkTypeId.Water, (int)InkTypeId.Steam, (int)InkTypeId.Ice };
            const float seed = 1f;

            // Fire present but NO water => the pair product is zero => nothing converts.
            Dispatch(FireAndWater(0.6f, 0f), seed, QuenchMatrix(), idx, cooling: 1f,
                out _, out var noWaterHeat);

            // Both reactants present but cooling disabled => pre-CP8r behaviour.
            Dispatch(FireAndWater(0.6f, 0.6f), seed, QuenchMatrix(), idx, cooling: 0f,
                out _, out var noCoolHeat);
            yield return null;

            Assert.That(noWaterHeat[0], Is.EqualTo(seed).Within(2e-2f),
                "No water => no quench => heat MUST be untouched. Cooling is keyed off the APPLIED " +
                "reaction scale, not raw adjacency, precisely so it cannot fire on a reaction that " +
                "never happened.");

            Assert.That(noCoolHeat[0], Is.EqualTo(seed).Within(2e-2f),
                "_QuenchCoolingPerUnit = 0 must be an exact pass-through (pre-CP8r behaviour)");
#else
            yield break;
#endif
        }

        // CKPT-103: the bug class the shader hardening closes. This group HAS Fire and Water in slots 0/1
        // — so it passes the naive "looks like a quench" shape — but its pair-0x1 column is ZERO, so no
        // steam is produced and nothing should cool. Cooling is deliberately forced ON to prove the
        // shader refuses on its own, independent of the host's scope gate. Before the hardening this
        // cooled from the raw pair product and would have chilled the cell for free.
        [UnityTest]
        public IEnumerator FireAndWaterPresent_ButZeroQuenchColumn_DoesNotCool()
        {
#if UNITY_EDITOR
            var idx = new[] { (int)InkTypeId.Fire, (int)InkTypeId.Water, (int)InkTypeId.Steam, (int)InkTypeId.Ice };
            const float seed = 1f;

            // Fire and Water both abundant (so products0.x is large), but col0 is all zero.
            Dispatch(FireAndWater(0.6f, 0.6f), seed, Matrix4x4.zero, idx, cooling: 1f,
                out var outP, out var outHeat);
            yield return null;

            Assert.That(outHeat[0], Is.EqualTo(seed).Within(2e-2f),
                "A ZERO pair-0x1 column produces no Steam, so it must produce no cooling — even with " +
                "Fire and Water present and cooling forced on. Cooling is derived from the steam the " +
                "column actually produced (event01.z), not from the raw pair product, so this is " +
                "guaranteed by the shader itself and not only by host scoping.");

            Assert.That(outP[0].steam, Is.EqualTo(0f).Within(1e-3f), "…and no steam was produced");
#else
            yield break;
#endif
        }

        // SCOPING. An OrganicGroup-style Fire x Plant burn must not chill the world even if a nonzero
        // cooling value ever reached it — the host scopes the value, and this pins that the wrong pair
        // column cannot produce cooling through the pair-0x1 path.
        [UnityTest]
        public IEnumerator NonQuenchReaction_DoesNotCoolHeat()
        {
#if UNITY_EDITOR
            var idx = new[] { (int)InkTypeId.Fire, (int)InkTypeId.Water,
                              (int)InkTypeId.PlantSeeded, (int)InkTypeId.PlantGrown };
            var p = new iparticle[Res * Res];
            for (int i = 0; i < p.Length; i++) { p[i].fire = IFloatTestValue.FromFloat(0.6f); p[i].plantSeeded = IFloatTestValue.FromFloat(0.6f); }
            const float seed = 1f;

            Dispatch(p, seed, PlantBurnMatrix(), idx, cooling: 1f, out var outP, out var outHeat);
            yield return null;

            Assert.That(outHeat[0], Is.EqualTo(seed).Within(2e-2f),
                "A Fire x Plant burn must NOT cool the cell. Cooling belongs to the Fire+Water quench " +
                "alone; chilling every reaction would suppress fire spread globally, which is the " +
                "opposite of the intended design.");

            Assert.That(outP[0].fire, Is.GreaterThan(0.6f), "…and plant burning still produces Fire");
#else
            yield break;
#endif
        }
    }
}
