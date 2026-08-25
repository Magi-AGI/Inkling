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
    public class GrowthComputeTests
    {
        // Direct plant maturation now requires local WATER: a seeded cell only matures to grown
        // where water is present. This test is the WET case (water present → converts).
        [UnityTest]
        public IEnumerator GrowSeeds_WetSeededCell_ConvertsToPlantGrown()
        {
#if UNITY_EDITOR
            var growth = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Growth/Compute/Growth.compute");
            Assert.IsNotNull(growth, "Growth.compute not found");

            int kernel = growth.FindKernel("GrowSeeds");

            // single pixel sim: seeded AND wet
            var particles = new iparticle[1];
            particles[0].plantSeeded = IFloatTestValue.FromFloat(1f);
            particles[0].plantGrown = IFloatTestValue.FromFloat(0f);
            particles[0].water = IFloatTestValue.FromFloat(0.5f);

            using var buffer = new ComputeBuffer(1, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            growth.SetBuffer(kernel, "_Particles", buffer);
            growth.SetInt("_Resolution", 1);
            growth.SetFloat("_DeltaTime", 1f);
            growth.SetFloat("_PlantGrowthRate", 0.5f);
            growth.SetFloat("_PlantMaxGrown", 1f);
            growth.SetFloat("_PlantSeedThreshold", 0.0f);
            growth.SetFloat("_PlantGrowthWaterThreshold", 0.01f);
            growth.SetFloat("_ElectricityGrowthRate", 0f);
            growth.SetFloat("_ElectricityMaxGrown", 0f);
            growth.SetFloat("_ElectricitySeedThreshold", 1f);
            growth.SetInt("_EnableSpread", 0);
            growth.SetFloat("_CardinalSpreadWeight", 0f);
            growth.SetFloat("_DiagonalSpreadWeight", 0f);
            growth.SetFloat("_PlantSpreadWaterThreshold", 0.01f);

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null; // allow GPU to finish

            buffer.GetData(particles);
            Assert.That(particles[0].plantSeeded, Is.LessThan(1f));
            Assert.That(particles[0].plantGrown, Is.GreaterThan(0f));
#else
            yield break;
#endif
        }

        // Direct plant maturation DRY case: a seeded cell with no water must NOT mature to grown.
        [UnityTest]
        public IEnumerator GrowSeeds_DrySeededCell_DoesNotConvert()
        {
#if UNITY_EDITOR
            var growth = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Growth/Compute/Growth.compute");
            Assert.IsNotNull(growth, "Growth.compute not found");

            int kernel = growth.FindKernel("GrowSeeds");

            // single pixel sim: seeded but DRY (no water)
            var particles = new iparticle[1];
            particles[0].plantSeeded = IFloatTestValue.FromFloat(1f);
            particles[0].plantGrown = IFloatTestValue.FromFloat(0f);
            particles[0].water = IFloatTestValue.FromFloat(0f);

            using var buffer = new ComputeBuffer(1, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            growth.SetBuffer(kernel, "_Particles", buffer);
            growth.SetInt("_Resolution", 1);
            growth.SetFloat("_DeltaTime", 1f);
            growth.SetFloat("_PlantGrowthRate", 0.5f);
            growth.SetFloat("_PlantMaxGrown", 1f);
            growth.SetFloat("_PlantSeedThreshold", 0.0f);
            growth.SetFloat("_PlantGrowthWaterThreshold", 0.01f);
            growth.SetFloat("_ElectricityGrowthRate", 0f);
            growth.SetFloat("_ElectricityMaxGrown", 0f);
            growth.SetFloat("_ElectricitySeedThreshold", 1f);
            growth.SetInt("_EnableSpread", 0);
            growth.SetFloat("_CardinalSpreadWeight", 0f);
            growth.SetFloat("_DiagonalSpreadWeight", 0f);
            growth.SetFloat("_PlantSpreadWaterThreshold", 0.01f);

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null; // allow GPU to finish

            buffer.GetData(particles);
            Assert.That(particles[0].plantGrown, Is.EqualTo(0f).Within(1e-5f),
                "A dry seeded cell must not mature into grown plant.");
            Assert.That(particles[0].plantSeeded, Is.EqualTo(1f).Within(1e-5f),
                "A dry seeded cell must retain its seeds (no maturation).");
#else
            yield break;
#endif
        }

        // Optional plant spread must expand grown plant across WATER, not across the plant-seed bed.
        // Both tests disable direct maturation (_PlantGrowthRate = 0) so ONLY neighbor spread can
        // move plantGrown, isolating the water gate. Grid is 3x3 with a grown source at the center;
        // the cell to its left has water (should gain grown), the cell to its right has only seeds
        // (should NOT gain grown).
        [UnityTest]
        public IEnumerator PlantSpread_ExpandsIntoWater_NotIntoSeedsOnly()
        {
#if UNITY_EDITOR
            var growth = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Growth/Compute/Growth.compute");
            Assert.IsNotNull(growth, "Growth.compute not found");

            int kernel = growth.FindKernel("GrowSeeds");

            const int res = 3;
            var particles = new iparticle[res * res];
            // Center (1,1): grown source.
            int center = 1 * res + 1;
            particles[center].plantGrown = IFloatTestValue.FromFloat(1f);
            // Left of center (0,1): has water, no seeds/grown → should gain grown via spread.
            int waterCell = 1 * res + 0;
            particles[waterCell].water = IFloatTestValue.FromFloat(0.5f);
            // Right of center (2,1): has seeds, no water/grown → should NOT gain grown.
            int seedCell = 1 * res + 2;
            particles[seedCell].plantSeeded = IFloatTestValue.FromFloat(0.5f);

            using var buffer = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            growth.SetBuffer(kernel, "_Particles", buffer);
            growth.SetInt("_Resolution", res);
            growth.SetFloat("_DeltaTime", 1f);
            growth.SetFloat("_PlantGrowthRate", 0f);      // disable direct maturation → isolate spread
            growth.SetFloat("_PlantMaxGrown", 1f);
            growth.SetFloat("_PlantSeedThreshold", 0.01f);
            growth.SetFloat("_PlantGrowthWaterThreshold", 0.01f);
            growth.SetFloat("_ElectricityGrowthRate", 0f);
            growth.SetFloat("_ElectricityMaxGrown", 0f);
            growth.SetFloat("_ElectricitySeedThreshold", 1f);
            growth.SetInt("_EnableSpread", 1);
            growth.SetFloat("_CardinalSpreadWeight", 0.5f);
            growth.SetFloat("_DiagonalSpreadWeight", 0f);
            growth.SetFloat("_PlantSpreadWaterThreshold", 0.1f); // water 0.5 passes, 0 fails

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null;

            buffer.GetData(particles);
            Assert.That(particles[waterCell].plantGrown, Is.GreaterThan(0f),
                "Grown plant should spread into a water cell adjacent to grown plant.");
            Assert.That(particles[seedCell].plantGrown, Is.EqualTo(0f).Within(1e-5f),
                "Grown plant must NOT spread into a seed-only cell with no water.");
#else
            yield break;
#endif
        }
    }
}