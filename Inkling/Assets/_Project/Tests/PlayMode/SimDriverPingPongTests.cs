using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Tests
{
    public class SimDriverPingPongTests
    {
        private const string MainScenePath = "Assets/_Project/Scenes/Main.unity";

        [UnityTest]
        public IEnumerator InjectDensityKeepsPingPongDeterministic()
        {
            // Ensure the main scene is loaded after play mode has started.
            yield return SceneManager.LoadSceneAsync(MainScenePath, LoadSceneMode.Single);

            var driver = Object.FindFirstObjectByType<SimDriver>();
            Assert.IsNotNull(driver, "SimDriver not found in scene");

            if (driver.fluidCompute == null)
            {
                Assert.Inconclusive("Scene is missing fluidCompute assignment; swap-ordering test requires simulation active.");
            }

            // Disable autonomous injectors to avoid extra swaps during the test
            var injectors = Object.FindObjectsByType<TexturedInjector>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var injector in injectors)
            {
                injector.enabled = false;
            }

            // Let Start/InitializeSimulation run
            yield return null;

            var densityField = typeof(SimDriver).GetField("density", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(densityField, "density field not found via reflection");
            var density = densityField.GetValue(driver);
            Assert.IsNotNull(density, "density ping-pong buffer not allocated");

            var readProperty = density.GetType().GetProperty("Read", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(readProperty, "PingPongRenderTexture.Read property missing");

            int idBefore = ((RenderTexture)readProperty.GetValue(density)).GetInstanceID();

            driver.InjectDensity(new Vector2(0.5f, 0.5f), Color.white);

            // Frame 1: pending injection is drained + advection runs
            yield return null;
            density = densityField.GetValue(driver);
            int idAfterInjection = ((RenderTexture)readProperty.GetValue(density)).GetInstanceID();

            // Frame 2: normal simulation step with no extra injections
            yield return null;
            density = densityField.GetValue(driver);
            int idNextFrame = ((RenderTexture)readProperty.GetValue(density)).GetInstanceID();

            Assert.AreEqual(idBefore, idAfterInjection, "Injection frame should end on the same Read buffer (even number of swaps).");
            Assert.AreNotEqual(idBefore, idNextFrame, "Subsequent frame should advance the ping-pong state by one swap.");
        }
    }
}
