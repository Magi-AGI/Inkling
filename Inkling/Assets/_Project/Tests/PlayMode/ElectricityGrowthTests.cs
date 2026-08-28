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
    /// Electricity growth contract tests for the GrowSeeds kernel in Growth.compute.
    ///
    /// Slice 2a: grown electricity now spreads like plants but through a CONDUCTIVE SUBSTRATE — it creeps
    /// into neighboring cells that contain WATER or ICE (destination water>_ElectricitySpreadWaterThreshold
    /// || ice>_ElectricitySpreadIceThreshold), replacing the old destination-electricitySeeded>0.001 gate.
    /// Direct seeded->grown MATURATION stays deliberately water-INDEPENDENT (unchanged from slice 1).
    /// Every dispatch path sets the new spread-threshold uniforms explicitly to avoid stale compute state.
    /// Reads are cast to float so the NUnit comparer is safe in both float and transient half builds.
    /// </summary>
    public class ElectricityGrowthTests
    {
#if UNITY_EDITOR
        private static ComputeShader LoadGrowth()
        {
            var growth = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Growth/Compute/Growth.compute");
            Assert.IsNotNull(growth, "Growth.compute not found");
            return growth;
        }

        // Disable the plant path so only the electricity path can move values.
        private static void DisablePlant(ComputeShader growth)
        {
            growth.SetFloat("_PlantGrowthRate", 0f);
            growth.SetFloat("_PlantMaxGrown", 0f);
            growth.SetFloat("_PlantSeedThreshold", 1f);
            growth.SetFloat("_PlantGrowthWaterThreshold", 1f);
            growth.SetFloat("_PlantSpreadWaterThreshold", 1f);
        }

        // Always set the slice-2a electricity spread substrate thresholds so no dispatch reads stale state.
        private static void SetElectricitySpread(ComputeShader growth, float waterThreshold, float iceThreshold)
        {
            growth.SetFloat("_ElectricitySpreadWaterThreshold", waterThreshold);
            growth.SetFloat("_ElectricitySpreadIceThreshold", iceThreshold);
        }
#endif

        [UnityTest]
        public IEnumerator ElectricitySeeded_ConvertsToGrown_AtConfiguredRate()
        {
#if UNITY_EDITOR
            var growth = LoadGrowth();
            int kernel = growth.FindKernel("GrowSeeds");

            // Seeded, NO water and NO ice: maturation must NOT require a substrate (water-independent).
            var particles = new iparticle[1];
            particles[0].electricitySeeded = IFloatTestValue.FromFloat(1f);
            particles[0].electricityGrown = IFloatTestValue.FromFloat(0f);

            using var buffer = new ComputeBuffer(1, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            growth.SetBuffer(kernel, "_Particles", buffer);
            growth.SetInt("_Resolution", 1);
            growth.SetFloat("_DeltaTime", 1f);
            DisablePlant(growth);
            growth.SetFloat("_ElectricityGrowthRate", 0.5f);
            growth.SetFloat("_ElectricityMaxGrown", 1f);
            growth.SetFloat("_ElectricitySeedThreshold", 0f);
            growth.SetInt("_EnableSpread", 0);
            growth.SetFloat("_CardinalSpreadWeight", 0f);
            growth.SetFloat("_DiagonalSpreadWeight", 0f);
            SetElectricitySpread(growth, 0.01f, 0.01f);

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null;

            buffer.GetData(particles);
            // elecGrowth = min(seeded=1, rate*dt=0.5) then capped by headroom(1) = 0.5, with no substrate present.
            Assert.That((float)particles[0].electricityGrown, Is.EqualTo(0.5f).Within(1e-3f),
                "electricitySeeded must mature to electricityGrown at rate*dt (0.5) WITHOUT any water/ice.");
            Assert.That((float)particles[0].electricitySeeded, Is.EqualTo(0.5f).Within(1e-3f),
                "the converted amount must be removed from electricitySeeded (expected 0.5).");
#else
            yield break;
#endif
        }

        [UnityTest]
        public IEnumerator ElectricityGrown_CapsAtMaxGrown()
        {
#if UNITY_EDITOR
            var growth = LoadGrowth();
            int kernel = growth.FindKernel("GrowSeeds");

            var particles = new iparticle[1];
            particles[0].electricitySeeded = IFloatTestValue.FromFloat(1f);
            particles[0].electricityGrown = IFloatTestValue.FromFloat(0.9f);

            using var buffer = new ComputeBuffer(1, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            growth.SetBuffer(kernel, "_Particles", buffer);
            growth.SetInt("_Resolution", 1);
            growth.SetFloat("_DeltaTime", 1f);
            DisablePlant(growth);
            growth.SetFloat("_ElectricityGrowthRate", 0.5f);
            growth.SetFloat("_ElectricityMaxGrown", 1f);
            growth.SetFloat("_ElectricitySeedThreshold", 0f);
            growth.SetInt("_EnableSpread", 0);
            growth.SetFloat("_CardinalSpreadWeight", 0f);
            growth.SetFloat("_DiagonalSpreadWeight", 0f);
            SetElectricitySpread(growth, 0.01f, 0.01f);

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null;

            buffer.GetData(particles);
            // elecGrowth = min(1, 0.5) = 0.5, clamped to headroom (1 - 0.9 = 0.1): grown -> 1.0, seeded -> 0.9.
            Assert.That((float)particles[0].electricityGrown, Is.EqualTo(1f).Within(1e-3f),
                "electricityGrown must cap at _ElectricityMaxGrown (expected 1.0, not 1.4).");
            Assert.That((float)particles[0].electricityGrown, Is.LessThanOrEqualTo(1f + 1e-4f),
                "electricityGrown must never exceed _ElectricityMaxGrown.");
            Assert.That((float)particles[0].electricitySeeded, Is.EqualTo(0.9f).Within(1e-3f),
                "only the capped amount (0.1) must be removed from electricitySeeded.");
#else
            yield break;
#endif
        }

        // Slice 2a contract (replaces the old ElectricitySpread_OnlyEntersSeededCells seed-gated lock):
        // grown electricity conducts through WATER, NOT through a dry non-conductive cell, and — crucially —
        // NOT into a dry-but-SEEDED cell (proving the old electricitySeeded spread gate is truly gone).
        [UnityTest]
        public IEnumerator ElectricitySpread_ConductsThroughWater_NotDryEvenWhenSeeded()
        {
#if UNITY_EDITOR
            var growth = LoadGrowth();
            int kernel = growth.FindKernel("GrowSeeds");

            // 3x3. Center (1,1)=idx4 is a grown-electricity source. Cell A (0,1)=idx3 is WET (water>thresh),
            // adjacent on the right -> must gain grown via conduction. Cell B (2,1)=idx5 is DRY / non-icy /
            // unseeded, adjacent on the left -> must NOT gain. Cell C (1,0)=idx1 is DRY but electricitySeeded
            // (>0.001), adjacent below -> must ALSO NOT gain (the old seed gate would have wrongly conducted).
            const int res = 3;
            var particles = new iparticle[res * res];
            particles[4].electricityGrown = IFloatTestValue.FromFloat(1f); // source
            particles[3].water = IFloatTestValue.FromFloat(0.5f);          // A: conductive substrate (water)
            particles[1].electricitySeeded = IFloatTestValue.FromFloat(0.5f); // C: dry but SEEDED
            // B (idx5) left at zero: no water, no ice, no electricitySeeded.

            using var buffer = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            growth.SetBuffer(kernel, "_Particles", buffer);
            growth.SetInt("_Resolution", res);
            growth.SetFloat("_DeltaTime", 1f);
            DisablePlant(growth);
            // Maturation OFF (rate 0, threshold above any seed) so ONLY spread can move grown.
            growth.SetFloat("_ElectricityGrowthRate", 0f);
            growth.SetFloat("_ElectricityMaxGrown", 1f);
            growth.SetFloat("_ElectricitySeedThreshold", 1f);
            growth.SetInt("_EnableSpread", 1);
            growth.SetFloat("_CardinalSpreadWeight", 1f);
            growth.SetFloat("_DiagonalSpreadWeight", 0f);
            SetElectricitySpread(growth, 0.01f, 0.01f);

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null;

            buffer.GetData(particles);
            Assert.That((float)particles[3].electricityGrown, Is.GreaterThan(0f),
                "A WET cell adjacent to grown electricity must gain grown via conduction through water.");
            Assert.That((float)particles[5].electricityGrown, Is.EqualTo(0f).Within(1e-4f),
                "A dry, non-icy, non-electric cell must NOT gain grown (no conductive substrate).");
            Assert.That((float)particles[1].electricityGrown, Is.EqualTo(0f).Within(1e-4f),
                "A dry-but-SEEDED cell must NOT gain grown — the old electricitySeeded spread gate is gone.");
#else
            yield break;
#endif
        }

        // Slice 2a contract: grown electricity also conducts through ICE.
        [UnityTest]
        public IEnumerator ElectricitySpread_ConductsThroughIce()
        {
#if UNITY_EDITOR
            var growth = LoadGrowth();
            int kernel = growth.FindKernel("GrowSeeds");

            // 3x3. Center (1,1)=idx4 grown source. Cell A (0,1)=idx3 is ICY (ice>thresh, no water) adjacent
            // to the source -> must gain grown via conduction through ice.
            const int res = 3;
            var particles = new iparticle[res * res];
            particles[4].electricityGrown = IFloatTestValue.FromFloat(1f); // source
            particles[3].ice = IFloatTestValue.FromFloat(0.5f);            // A: conductive substrate (ice)

            using var buffer = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            growth.SetBuffer(kernel, "_Particles", buffer);
            growth.SetInt("_Resolution", res);
            growth.SetFloat("_DeltaTime", 1f);
            DisablePlant(growth);
            growth.SetFloat("_ElectricityGrowthRate", 0f);
            growth.SetFloat("_ElectricityMaxGrown", 1f);
            growth.SetFloat("_ElectricitySeedThreshold", 1f);
            growth.SetInt("_EnableSpread", 1);
            growth.SetFloat("_CardinalSpreadWeight", 1f);
            growth.SetFloat("_DiagonalSpreadWeight", 0f);
            // Water threshold set high so ONLY ice can gate here; ice threshold low so the icy cell conducts.
            SetElectricitySpread(growth, 1f, 0.01f);

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null;

            buffer.GetData(particles);
            Assert.That((float)particles[3].electricityGrown, Is.GreaterThan(0f),
                "An ICY cell adjacent to grown electricity must gain grown via conduction through ice.");
#else
            yield break;
#endif
        }
    }
}
