using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Tests.EditMode
{
    public class SimDriverFacadeTests
    {
        [Test]
        public void ReaderProperties_ReflectSerializedFields()
        {
            var go = new GameObject("SimDriverTest");
            var driver = go.AddComponent<SimDriver>();

            // Set serialized fields via reflection (they're private [SerializeField])
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

            typeof(SimDriver).GetField("resolution", flags)?.SetValue(driver, 128);
            typeof(SimDriver).GetField("viscosity", flags)?.SetValue(driver, 0.002f);
            typeof(SimDriver).GetField("vorticity", flags)?.SetValue(driver, 3.5f);
            typeof(SimDriver).GetField("dissipation", flags)?.SetValue(driver, 0.95f);
            typeof(SimDriver).GetField("velocityDissipation", flags)?.SetValue(driver, 0.9f);
            typeof(SimDriver).GetField("timestep", flags)?.SetValue(driver, 0.032f);

            // ISimulationReader properties should directly return serialized field values
            Assert.AreEqual(128, driver.Resolution);
            Assert.AreEqual(0.002f, driver.Viscosity);
            Assert.AreEqual(3.5f, driver.Vorticity);
            Assert.AreEqual(0.95f, driver.Dissipation);
            Assert.AreEqual(0.9f, driver.VelocityDissipation);
            Assert.AreEqual(0.032f, driver.Timestep);

            Object.DestroyImmediate(go);
        }

        [Test]
        public void InjectDensity_BeforeInit_DoesNotThrow()
        {
            var go = new GameObject("SimDriverTest");
            var driver = go.AddComponent<SimDriver>();

            // SimDriver.Start() hasn't run (edit mode), so ctx is null.
            // InjectDensity should not throw — it should guard on null ctx/compute.
            // We need to manually create the ctx so the null check on FluidCompute is reached.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var ctx = new SimulationContext();
            typeof(SimDriver).GetField("ctx", flags)?.SetValue(driver, ctx);
            typeof(SimDriver).GetField("operationQueue", flags)?.SetValue(driver, new OperationQueue(ctx));

            // FluidCompute is null, so this should early-return without throwing
            Assert.DoesNotThrow(() => driver.InjectDensity(Vector2.one * 0.5f, Color.red));

            Object.DestroyImmediate(go);
        }
    }
}
