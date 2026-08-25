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
    /// Verifies the event-level conservation fix in InkInteractions.compute (ckpt-015):
    /// a balanced reaction column must NOT mint mass at a front (product from neighborhood
    /// adjacency while the local cell lacks the consumed reactants), and a local balanced
    /// reaction must stay mass-conserving. Also checks that a negative-only sink never creates.
    /// </summary>
    public class InkInteractionsConservationTests
    {
#if UNITY_EDITOR
        // iparticle field indices (InkTypeId order): Fire=0, Water=1, PlantSeeded=2, PlantGrown=3, Steam=4, Ice=9.
        private const int FIRE = 0, WATER = 1, PLANT_SEEDED = 2, PLANT_GROWN = 3, STEAM = 4, ICE = 9;

        private static ComputeShader LoadInkInteractions()
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/SimulationLOD0/InkInteractions.compute");
            Assert.IsNotNull(cs, "InkInteractions.compute not found");
            return cs;
        }

        // Dispatch the InkInteractions kernel over a res*res grid for one affinity group.
        private static iparticle[] Dispatch(
            iparticle[] particles, int res, int[] inkIndices,
            Matrix4x4 productMatrix, Vector4 col4, Vector4 col5,
            Vector3 weights, float rate, float dt, Vector4 thresholds)
        {
            var cs = LoadInkInteractions();
            int kernel = cs.FindKernel("InkInteractions");

            int count = res * res;
            var readBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());
            var writeBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());
            // The kernel declares a _ReactionImpulseRW UAV; it must be bound even though we disable
            // accumulation. A tiny RGFloat UAV texture satisfies the binding.
            var impulseRT = new RenderTexture(res, res, 0, RenderTextureFormat.RGFloat);
            impulseRT.enableRandomWrite = true;
            impulseRT.Create();
            // CP8r (CKPT-102): the kernel now also declares _HeatRead/_HeatWrite, so they must be bound
            // on every dispatch for the same reason _ReactionImpulseRW is — Unity validates declared
            // resources at dispatch, not at the shader's uniform branch. Cooling stays disabled here
            // (_QuenchCoolingPerUnit = 0), so heat is untouched; these only satisfy the binding.
            var heatReadRT = new RenderTexture(res, res, 0, RenderTextureFormat.RHalf);
            heatReadRT.enableRandomWrite = true; heatReadRT.Create();
            var heatWriteRT = new RenderTexture(res, res, 0, RenderTextureFormat.RHalf);
            heatWriteRT.enableRandomWrite = true; heatWriteRT.Create();

            try
            {
                readBuf.SetData(particles);
                writeBuf.SetData(particles);

                cs.SetInt("_Resolution", res);
                cs.SetFloat("_DeltaTime", dt);
                cs.SetInt("_DebugMode", 0);
                cs.SetInt("_EnableBlackBodyClearing", 0);
                cs.SetFloat("_BlackBodyThreshold", 0.5f);
                cs.SetFloat("_BlackBodyClearingRate", 0f);

                cs.SetInts("_InkIndices", inkIndices);
                cs.SetMatrix("_ProductMatrix", productMatrix);
                cs.SetVector("_ProductCol4", col4);
                cs.SetVector("_ProductCol5", col5);
                cs.SetFloats("_Weights", weights.x, weights.y, weights.z);
                cs.SetFloat("_RateMultiplier", rate);
                cs.SetVector("_InteractionThresholds", thresholds);

                // Reaction impulse disabled for these tests (concentration-only).
                cs.SetInt("_AccumulateReactionImpulse", 0);
                cs.SetFloat("_ReactionImpulseGain", 0f);
                cs.SetTexture(kernel, "_ReactionImpulseRW", impulseRT);
                cs.SetTexture(kernel, "_HeatRead", heatReadRT);
                cs.SetTexture(kernel, "_HeatWrite", heatWriteRT);
                cs.SetFloat("_QuenchCoolingPerUnit", 0f);   // cooling OFF: conservation only
                cs.SetFloat("_MinTemperature", 0f);
                cs.SetFloat("_MaxHeat", 1f);

                cs.SetBuffer(kernel, "_ParticlesRead", readBuf);
                cs.SetBuffer(kernel, "_ParticlesWrite", writeBuf);

                int groups = Mathf.CeilToInt(res / 8f);
                cs.Dispatch(kernel, groups, groups, 1);

                var outData = new iparticle[count];
                writeBuf.GetData(outData);
                return outData;
            }
            finally
            {
                readBuf.Release();
                writeBuf.Release();
                impulseRT.Release();
                heatReadRT.Release();
                heatWriteRT.Release();
            }
        }

        // Dispatch with the reaction impulse ENABLED, and read back the accumulated impulse vector
        // (RG of _ReactionImpulseRW) at a given cell. Used to verify impulse gating by event scale.
        private static Vector2 DispatchReadImpulse(
            iparticle[] particles, int res, int[] inkIndices,
            Matrix4x4 productMatrix, Vector4 col4, Vector4 col5, Matrix4x4 impulseMatrix,
            Vector3 weights, float rate, float dt, Vector4 thresholds, int cellX, int cellY)
        {
            var cs = LoadInkInteractions();
            int kernel = cs.FindKernel("InkInteractions");

            int count = res * res;
            var readBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());
            var writeBuf = new ComputeBuffer(count, Marshal.SizeOf<iparticle>());
            var impulseRT = new RenderTexture(res, res, 0, RenderTextureFormat.RGFloat);
            impulseRT.enableRandomWrite = true;
            impulseRT.Create();
            // CP8r (CKPT-102): the kernel now also declares _HeatRead/_HeatWrite, so they must be bound
            // on every dispatch for the same reason _ReactionImpulseRW is — Unity validates declared
            // resources at dispatch, not at the shader's uniform branch. Cooling stays disabled here
            // (_QuenchCoolingPerUnit = 0), so heat is untouched; these only satisfy the binding.
            var heatReadRT = new RenderTexture(res, res, 0, RenderTextureFormat.RHalf);
            heatReadRT.enableRandomWrite = true; heatReadRT.Create();
            var heatWriteRT = new RenderTexture(res, res, 0, RenderTextureFormat.RHalf);
            heatWriteRT.enableRandomWrite = true; heatWriteRT.Create();

            try
            {
                readBuf.SetData(particles);
                writeBuf.SetData(particles);

                cs.SetInt("_Resolution", res);
                cs.SetFloat("_DeltaTime", dt);
                cs.SetInt("_DebugMode", 0);
                cs.SetInt("_EnableBlackBodyClearing", 0);
                cs.SetFloat("_BlackBodyThreshold", 0.5f);
                cs.SetFloat("_BlackBodyClearingRate", 0f);

                cs.SetInts("_InkIndices", inkIndices);
                cs.SetMatrix("_ProductMatrix", productMatrix);
                cs.SetVector("_ProductCol4", col4);
                cs.SetVector("_ProductCol5", col5);
                cs.SetFloats("_Weights", weights.x, weights.y, weights.z);
                cs.SetFloat("_RateMultiplier", rate);
                cs.SetVector("_InteractionThresholds", thresholds);

                cs.SetInt("_AccumulateReactionImpulse", 1);
                cs.SetFloat("_ReactionImpulseGain", 1f);
                cs.SetMatrix("_ReactionImpulseMatrix", impulseMatrix);
                cs.SetVector("_ReactionImpulseCol4", Vector4.zero);
                cs.SetVector("_ReactionImpulseCol5", Vector4.zero);
                cs.SetTexture(kernel, "_ReactionImpulseRW", impulseRT);
                cs.SetTexture(kernel, "_HeatRead", heatReadRT);
                cs.SetTexture(kernel, "_HeatWrite", heatWriteRT);
                cs.SetFloat("_QuenchCoolingPerUnit", 0f);   // cooling OFF: conservation only
                cs.SetFloat("_MinTemperature", 0f);
                cs.SetFloat("_MaxHeat", 1f);

                cs.SetBuffer(kernel, "_ParticlesRead", readBuf);
                cs.SetBuffer(kernel, "_ParticlesWrite", writeBuf);

                // The kernel accumulates into _ReactionImpulseRW with +=; a freshly-created RT is not
                // guaranteed zero on every backend, so clear it explicitly before dispatch.
                var prevClear = RenderTexture.active;
                RenderTexture.active = impulseRT;
                GL.Clear(true, true, Color.clear);
                RenderTexture.active = prevClear;

                int groups = Mathf.CeilToInt(res / 8f);
                cs.Dispatch(kernel, groups, groups, 1);

                var prev = RenderTexture.active;
                var tex = new Texture2D(res, res, TextureFormat.RGBAFloat, false);
                try
                {
                    RenderTexture.active = impulseRT;
                    tex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
                    tex.Apply();
                    Color c = tex.GetPixel(cellX, cellY);
                    return new Vector2(c.r, c.g);
                }
                finally
                {
                    RenderTexture.active = prev;   // restored even if ReadPixels/Apply throws
                    Object.DestroyImmediate(tex);  // temp Texture2D destroyed even on error
                }
            }
            finally
            {
                readBuf.Release();
                writeBuf.Release();
                impulseRT.Release();
                heatReadRT.Release();
                heatWriteRT.Release();
                Object.DestroyImmediate(impulseRT);
            }
        }

        private static float GetField(iparticle p, int fieldIndex)
        {
            switch (fieldIndex)
            {
                case FIRE: return p.fire;
                case WATER: return p.water;
                case PLANT_SEEDED: return p.plantSeeded;
                case PLANT_GROWN: return p.plantGrown;
                case STEAM: return p.steam;
                case ICE: return p.ice;
                default: return 0f;
            }
        }
#endif

        // REGRESSION: a balanced Fire+Ice->Water column must NOT create water in an empty center
        // cell whose neighbors hold fire and ice. Under the old per-channel clamp this minted water.
        [UnityTest]
        public IEnumerator FireIceToWater_DoesNotMintWaterAtEmptyFront()
        {
#if UNITY_EDITOR
            const int res = 3;
            var particles = new iparticle[res * res];
            int center = 1 * res + 1;
            int left = 1 * res + 0;
            int right = 1 * res + 2;
            particles[left].fire = IFloatTestValue.FromFloat(1f);   // neighbor supplies fire
            particles[right].ice = IFloatTestValue.FromFloat(1f);   // neighbor supplies ice
            // center has NO fire/ice/water/steam locally.

            // Thermal group slots [Fire, Water, Steam, Ice]; Fire+Ice is pair 0x3 (products0.z).
            // Column 2 coefficients: Fire -0.5, Water +1, Steam 0, Ice -0.5.
            var m = Matrix4x4.zero;
            m[0, 2] = -0.5f; // Fire
            m[1, 2] = 1.0f;  // Water
            m[3, 2] = -0.5f; // Ice

            var outData = Dispatch(
                particles, res, new[] { FIRE, WATER, STEAM, ICE },
                m, Vector4.zero, Vector4.zero,
                new Vector3(1f, 1f, 0.707f), 1f, 1f, Vector4.zero);

            yield return null;

            Assert.That(outData[center].water, Is.LessThan(1e-3f),
                "Center cell with no local fire/ice must not mint water from neighborhood reactants.");
            Assert.That(outData[center].fire, Is.LessThan(1e-3f), "Center fire should remain ~0.");
            Assert.That(outData[center].ice, Is.LessThan(1e-3f), "Center ice should remain ~0.");
#else
            yield break;
#endif
        }

        // A cell with LOCAL fire+ice should convert some to water and stay mass-conserving.
        [UnityTest]
        public IEnumerator FireIceToWater_LocalReaction_ConservesMass()
        {
#if UNITY_EDITOR
            const int res = 1;
            var particles = new iparticle[res * res];
            particles[0].fire = IFloatTestValue.FromFloat(0.5f);
            particles[0].ice = IFloatTestValue.FromFloat(0.5f);
            particles[0].water = IFloatTestValue.FromFloat(0f);

            var m = Matrix4x4.zero;
            m[0, 2] = -0.5f; // Fire
            m[1, 2] = 1.0f;  // Water
            m[3, 2] = -0.5f; // Ice

            float before = 0.5f + 0.5f + 0f; // fire + ice + water (steam 0)
            var outData = Dispatch(
                particles, res, new[] { FIRE, WATER, STEAM, ICE },
                m, Vector4.zero, Vector4.zero,
                new Vector3(1f, 1f, 0.707f), 1f, 1f, Vector4.zero);

            yield return null;

            float after = outData[0].fire + outData[0].ice + outData[0].water + outData[0].steam;
            Assert.That(outData[0].water, Is.GreaterThan(0f), "Water should be produced from local fire+ice.");
            Assert.That(outData[0].fire, Is.LessThan(0.5f), "Fire should be consumed.");
            Assert.That(outData[0].ice, Is.LessThan(0.5f), "Ice should be consumed.");
            Assert.That(after, Is.EqualTo(before).Within(1e-3f),
                "Balanced reaction must conserve local mass across [Fire,Water,Steam,Ice].");
#else
            yield break;
#endif
        }

        // IMPULSE GATING (ckpt-016): reaction motion must respect the same conservation limit as the
        // concentration reaction. At an adjacency-only front where the local cell cannot pay for the
        // reaction (event scale 0), the impulse must be zero even with a nonzero impulse coefficient.
        [UnityTest]
        public IEnumerator Impulse_NoMotionAtEmptyFront()
        {
#if UNITY_EDITOR
            const int res = 3;
            var particles = new iparticle[res * res];
            particles[1 * res + 0].fire = IFloatTestValue.FromFloat(1f);  // left neighbor supplies fire
            particles[1 * res + 2].ice = IFloatTestValue.FromFloat(1f);   // right neighbor supplies ice
            // center (1,1) has no local fire/ice.

            var m = Matrix4x4.zero; m[0, 2] = -0.5f; m[1, 2] = 1f; m[3, 2] = -0.5f; // Fire+Ice->Water (pair 0x3)
            var imp = Matrix4x4.zero; imp[0, 2] = 1f;                               // impulse coeff on pair 0x3

            Vector2 v = DispatchReadImpulse(
                particles, res, new[] { FIRE, WATER, STEAM, ICE },
                m, Vector4.zero, Vector4.zero, imp,
                new Vector3(1f, 1f, 0.707f), 1f, 1f, Vector4.zero, 1, 1);

            yield return null;
            Assert.That(v.magnitude, Is.LessThan(1e-4f),
                "No local reactants => event scale 0 => no reaction impulse (was phantom motion before).");
#else
            yield break;
#endif
        }

        // A payable local fire+ice front (with a gradient) must still produce a nonzero impulse.
        [UnityTest]
        public IEnumerator Impulse_MotionAtPayableFront()
        {
#if UNITY_EDITOR
            const int res = 3;
            var particles = new iparticle[res * res];
            particles[1 * res + 1].fire = IFloatTestValue.FromFloat(0.5f);  // center can pay locally
            particles[1 * res + 1].ice = IFloatTestValue.FromFloat(0.5f);
            particles[1 * res + 2].ice = IFloatTestValue.FromFloat(0.5f);   // extra ice on the right -> gradient for direction

            var m = Matrix4x4.zero; m[0, 2] = -0.5f; m[1, 2] = 1f; m[3, 2] = -0.5f;
            var imp = Matrix4x4.zero; imp[0, 2] = 1f;

            Vector2 v = DispatchReadImpulse(
                particles, res, new[] { FIRE, WATER, STEAM, ICE },
                m, Vector4.zero, Vector4.zero, imp,
                new Vector3(1f, 1f, 0.707f), 1f, 1f, Vector4.zero, 1, 1);

            yield return null;
            Assert.That(v.magnitude, Is.GreaterThan(0f),
                "A payable local reaction with a gradient must still generate reaction impulse.");
#else
            yield break;
#endif
        }

        // The SPLIT OrganicGroup2 sink (ckpt-016): each pair column targets a SINGLE plant channel,
        // so the negative-only sink removes plant even when only one plant channel is present — the
        // coupled (two-negative-row) version would scale to zero when the other channel was absent.
        // Slots [Steam, Ice, PlantSeeded, PlantGrown]; columns:
        //   Steam×PlantSeeded (col1) -> PlantSeeded only; Steam×PlantGrown (col2) -> PlantGrown only;
        //   Ice×PlantSeeded  (col3) -> PlantSeeded only; Ice×PlantGrown (Col4)  -> PlantGrown only.
        private static Matrix4x4 SplitSinkMatrix()
        {
            var m = Matrix4x4.zero;
            m[2, 1] = -0.0002f; // Steam×PlantSeeded -> -PlantSeeded (row2, col1)
            m[3, 2] = -0.0002f; // Steam×PlantGrown  -> -PlantGrown  (row3, col2)
            m[2, 3] = -0.0002f; // Ice×PlantSeeded   -> -PlantSeeded (row2, col3)
            return m;
        }
        private static Vector4 SplitSinkCol4() => new Vector4(0f, 0f, 0f, -0.0002f); // Ice×PlantGrown -> -PlantGrown

        // Grown-only cell (PlantSeeded absent) must still be damped by the split sink.
        [UnityTest]
        public IEnumerator SplitSink_DampsGrownOnlyCell()
        {
#if UNITY_EDITOR
            const int res = 1;
            var particles = new iparticle[res * res];
            particles[0].steam = IFloatTestValue.FromFloat(1f);
            particles[0].plantSeeded = IFloatTestValue.FromFloat(0f);
            particles[0].plantGrown = IFloatTestValue.FromFloat(0.5f);

            var outData = Dispatch(
                particles, res, new[] { STEAM, ICE, PLANT_SEEDED, PLANT_GROWN },
                SplitSinkMatrix(), SplitSinkCol4(), Vector4.zero,
                new Vector3(1f, 1f, 0.707f), 1f, 1f, Vector4.zero);

            yield return null;

            Assert.That(outData[0].plantGrown, Is.LessThan(0.5f),
                "Split sink must remove plantGrown even when plantSeeded is absent (was coupled-to-zero before).");
            Assert.That(outData[0].plantGrown, Is.GreaterThanOrEqualTo(0f), "Plant must not go negative.");
            Assert.That(outData[0].steam, Is.EqualTo(1f).Within(1e-3f), "Sink must not create/alter steam.");
#else
            yield break;
#endif
        }

        // Seeded-only cell (PlantGrown absent) must still be damped by the split sink.
        [UnityTest]
        public IEnumerator SplitSink_DampsSeededOnlyCell()
        {
#if UNITY_EDITOR
            const int res = 1;
            var particles = new iparticle[res * res];
            particles[0].steam = IFloatTestValue.FromFloat(1f);
            particles[0].plantSeeded = IFloatTestValue.FromFloat(0.5f);
            particles[0].plantGrown = IFloatTestValue.FromFloat(0f);

            var outData = Dispatch(
                particles, res, new[] { STEAM, ICE, PLANT_SEEDED, PLANT_GROWN },
                SplitSinkMatrix(), SplitSinkCol4(), Vector4.zero,
                new Vector3(1f, 1f, 0.707f), 1f, 1f, Vector4.zero);

            yield return null;

            Assert.That(outData[0].plantSeeded, Is.LessThan(0.5f),
                "Split sink must remove plantSeeded even when plantGrown is absent.");
            Assert.That(outData[0].plantSeeded, Is.GreaterThanOrEqualTo(0f), "Plant must not go negative.");
            Assert.That(outData[0].plantGrown, Is.EqualTo(0f).Within(1e-5f), "Grown must stay zero (nothing to remove).");
#else
            yield break;
#endif
        }
    }
}
