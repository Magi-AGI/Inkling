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
    /// Electricity slice 1 — REGRESSION/CHARACTERIZATION LOCKS for the existing electricitySeeded ->
    /// electricityGrown path in Growth.compute (GrowSeeds kernel). These prove NO new feature; they pin
    /// behavior that already runs but was previously untested (GrowthComputeTests only ZEROES the electricity
    /// params to isolate plant). Electricity growth is deliberately water-INDEPENDENT, unlike plant.
    /// Reuses the proven GrowthComputeTests GPU-dispatch pattern. Reads are cast to float so the NUnit
    /// comparer is happy in both the default float build and a transient half build.
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
#endif

        [UnityTest]
        public IEnumerator ElectricitySeeded_ConvertsToGrown_AtConfiguredRate()
        {
#if UNITY_EDITOR
            var growth = LoadGrowth();
            int kernel = growth.FindKernel("GrowSeeds");

            // Seeded, NO water (electricity growth must not require water).
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

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null;

            buffer.GetData(particles);
            // elecGrowth = min(seeded=1, rate*dt=0.5) then capped by headroom(1) = 0.5.
            Assert.That((float)particles[0].electricityGrown, Is.EqualTo(0.5f).Within(1e-3f),
                "electricitySeeded must convert to electricityGrown at rate*dt (expected 0.5).");
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

            // Almost-full grown: only 0.1 of headroom remains under a 0.5 rate.
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

        [UnityTest]
        public IEnumerator ElectricitySpread_OnlyEntersSeededCells()
        {
#if UNITY_EDITOR
            var growth = LoadGrowth();
            int kernel = growth.FindKernel("GrowSeeds");

            // 3x3 grid. Center (1,1)=idx4 is a grown source. Cell A (0,1)=idx3 is SEEDED (gate passes),
            // adjacent to the source on its right. Cell B (2,1)=idx5 is UNSEEDED (gate fails), adjacent to
            // the source on its left. Spread must reach A but not B.
            const int res = 3;
            var particles = new iparticle[res * res];
            particles[4].electricityGrown = IFloatTestValue.FromFloat(1f); // source
            particles[3].electricitySeeded = IFloatTestValue.FromFloat(0.5f); // A: gated in (>0.001)
            // B (idx5) left at zero: electricitySeeded = 0 (gated out).

            using var buffer = new ComputeBuffer(res * res, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            growth.SetBuffer(kernel, "_Particles", buffer);
            growth.SetInt("_Resolution", res);
            growth.SetFloat("_DeltaTime", 1f);
            DisablePlant(growth);
            // Conversion OFF (threshold above the seed so seeded->grown cannot fire) to isolate SPREAD.
            growth.SetFloat("_ElectricityGrowthRate", 0f);
            growth.SetFloat("_ElectricityMaxGrown", 1f);
            growth.SetFloat("_ElectricitySeedThreshold", 1f);
            growth.SetInt("_EnableSpread", 1);
            growth.SetFloat("_CardinalSpreadWeight", 1f);
            growth.SetFloat("_DiagonalSpreadWeight", 0f);

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null;

            buffer.GetData(particles);
            Assert.That((float)particles[3].electricityGrown, Is.GreaterThan(0f),
                "A seeded cell (electricitySeeded>0.001) adjacent to grown electricity must gain grown via spread.");
            Assert.That((float)particles[5].electricityGrown, Is.EqualTo(0f).Within(1e-4f),
                "An unseeded cell (electricitySeeded<=0.001) must NOT gain grown via spread (the gate blocks it).");
#else
            yield break;
#endif
        }
    }
}
