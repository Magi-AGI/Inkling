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
        [UnityTest]
        public IEnumerator GrowSeeds_ConvertsPlantSeededToPlantGrown()
        {
#if UNITY_EDITOR
            var growth = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_Project/Scripts/Systems/Growth/Compute/Growth.compute");
            Assert.IsNotNull(growth, "Growth.compute not found");

            int kernel = growth.FindKernel("GrowSeeds");

            // single pixel sim
            var particles = new iparticle[1];
            particles[0].plantSeeded = 1f;
            particles[0].plantGrown = 0f;

            using var buffer = new ComputeBuffer(1, Marshal.SizeOf<iparticle>());
            buffer.SetData(particles);

            growth.SetBuffer(kernel, "_Particles", buffer);
            growth.SetInt("_Resolution", 1);
            growth.SetFloat("_DeltaTime", 1f);
            growth.SetFloat("_PlantGrowthRate", 0.5f);
            growth.SetFloat("_PlantMaxGrown", 1f);
            growth.SetFloat("_PlantSeedThreshold", 0.0f);
            growth.SetFloat("_ElectricityGrowthRate", 0f);
            growth.SetFloat("_ElectricityMaxGrown", 0f);
            growth.SetFloat("_ElectricitySeedThreshold", 1f);
            growth.SetInt("_EnableSpread", 0);
            growth.SetFloat("_CardinalSpreadWeight", 0f);
            growth.SetFloat("_DiagonalSpreadWeight", 0f);

            growth.Dispatch(kernel, 1, 1, 1);
            yield return null; // allow GPU to finish

            buffer.GetData(particles);
            Assert.That(particles[0].plantSeeded, Is.LessThan(1f));
            Assert.That(particles[0].plantGrown, Is.GreaterThan(0f));
#else
            yield break;
#endif
        }
    }
}