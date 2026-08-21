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

            // Phase-8 modular split: the density ping-pong is no longer a SimDriver.density field; it lives
            // on the shared SimulationContext (SimDriver.ctx.Density). Navigate ctx -> Density via reflection.
            var ctxField = typeof(SimDriver).GetField("ctx", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(ctxField, "ctx field not found via reflection");
            var ctx = ctxField.GetValue(driver);
            Assert.IsNotNull(ctx, "SimulationContext not allocated");

            var densityField = ctx.GetType().GetField("Density", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(densityField, "Density field not found via reflection");
            var density = densityField.GetValue(ctx);
            Assert.IsNotNull(density, "density ping-pong buffer not allocated");

            var readProperty = density.GetType().GetProperty("Read", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(readProperty, "PingPongRenderTexture.Read property missing");

            var writeProperty = density.GetType().GetProperty("Write", BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(writeProperty, "PingPongRenderTexture.Write property missing");

            // Capture the ping-pong's two stable buffers before any injection.
            int idA = ((RenderTexture)readProperty.GetValue(density)).GetInstanceID();
            int idB = ((RenderTexture)writeProperty.GetValue(density)).GetInstanceID();
            Assert.AreNotEqual(idA, idB, "Ping-pong must hold two distinct buffers.");
            AssertStablePair(readProperty, writeProperty, density, idA, idB, "before injection");

            driver.InjectDensity(new Vector2(0.5f, 0.5f), Color.white);

            // Injection frame: pending injection is drained once, then density advection runs once per
            // scheduler substep. The Phase-8 substepped scheduler makes the per-rendered-frame swap count
            // timing-dependent, so fixed even/odd Read-buffer parity is not a stable invariant.
            yield return null;
            density = densityField.GetValue(ctx);
            AssertStablePair(readProperty, writeProperty, density, idA, idB, "after injection frame");

            // A normal simulation frame with no extra injections must keep the same two-buffer pair.
            yield return null;
            density = densityField.GetValue(ctx);
            AssertStablePair(readProperty, writeProperty, density, idA, idB, "after normal frame");
        }

        private static void AssertStablePair(PropertyInfo readProperty, PropertyInfo writeProperty, object density, int idA, int idB, string when)
        {
            var read = readProperty.GetValue(density) as RenderTexture;
            var write = writeProperty.GetValue(density) as RenderTexture;
            Assert.IsNotNull(read, $"Read buffer null ({when}).");
            Assert.IsNotNull(write, $"Write buffer null ({when}).");

            int readId = read.GetInstanceID();
            int writeId = write.GetInstanceID();
            Assert.AreNotEqual(readId, writeId, $"Read and Write must stay distinct ({when}).");
            Assert.IsTrue(readId == idA || readId == idB, $"Read drifted off the ping-pong pair ({when}).");
            Assert.IsTrue(writeId == idA || writeId == idB, $"Write drifted off the ping-pong pair ({when}).");
        }
    }
}
