using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Magi.InkTools.Simulation;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Tests.PlayMode
{
    /// <summary>
    /// M0 (true Metal) — enum/index locks + selection guards proving Metal is a first-class ink at index 10
    /// that never aliases to BlackBody (6) or Ice (9), and that ColdAir shifts correctly with the new Count.
    /// Storage/round-trip of the metal field itself is covered by ParticleBufferRoundTripTests (60-byte,
    /// 15-field layout) and ParticleLayoutContractTests.
    /// </summary>
    public class MetalM0Tests
    {
        [Test]
        public void InkTypeId_MetalIs10_CountIs11_DistinctFromBlackBody()
        {
            Assert.AreEqual(10, (int)InkTypeId.Metal, "Metal must be ink index 10 (after Ice=9).");
            Assert.AreEqual(11, (int)InkTypeId.Count, "Count must be 11 (Metal is a real ink channel).");
            Assert.AreEqual(6, (int)InkTypeId.BlackBody, "BlackBody stays index 6, distinct from Metal.");
            Assert.AreEqual(9, (int)InkTypeId.Ice, "Ice stays index 9, distinct from Metal.");
        }

        [Test]
        public void ColdSourceInkIndex_IsOnePastRealInks_NotMetal()
        {
            // ColdAir is heat-only and lives one past the real inks; with Metal it shifts from 10 to 11.
            Assert.AreEqual((int)InkTypeId.Count, SimulationContext.ColdSourceInkIndex,
                "ColdSourceInkIndex must equal InkTypeId.Count (=11) so ColdAir never collides with Metal(10).");
            Assert.IsTrue(SimulationContext.IsColdSource(SimulationContext.ColdSourceInkIndex));
            Assert.IsFalse(SimulationContext.IsColdSource((int)InkTypeId.Metal),
                "Metal(10) must NOT be treated as a cold source.");
        }

        [Test]
        public void SimDriverDebugInput_MapsMetalToRealIndex10_NotBlackBody()
        {
            var go = new GameObject("MetalM0_DebugInput");
            try
            {
                var dbg = go.AddComponent<SimDriverDebugInput>();
                var enumType = typeof(SimDriverDebugInput).GetNestedType("InkType", BindingFlags.Public);
                Assert.IsNotNull(enumType, "SimDriverDebugInput.InkType enum not found.");
                object metal = Enum.Parse(enumType, "Metal");

                var mi = typeof(SimDriverDebugInput).GetMethod("GetParticleFieldIndex",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(mi, "SimDriverDebugInput.GetParticleFieldIndex not found.");

                int idx = (int)mi.Invoke(dbg, new[] { metal });
                Assert.AreEqual((int)InkTypeId.Metal, idx,
                    "Debug 'Metal' selection must map to the real Metal field index 10.");
                Assert.AreNotEqual((int)InkTypeId.BlackBody, idx,
                    "Debug 'Metal' must NOT alias to BlackBody (index 6).");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
