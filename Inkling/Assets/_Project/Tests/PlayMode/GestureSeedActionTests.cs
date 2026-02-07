using System.Collections.Generic;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Magi.Inkling.Systems.Gestures;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.PlayMode
{
    public class GestureSeedActionTests
    {
        // TODO: migrate to Helpers.StubSimulationWriter
        private class StubWriter : MonoBehaviour, ISimulationWriter
        {
            public int plantCalls;
            public int electricCalls;
            public void InjectForce(Vector2 position, Vector2 force) { }
            public void InjectDensity(Vector2 position, Color color, int inkTypeIndex = 0)
            {
                if (inkTypeIndex == 2) plantCalls++;
                if (inkTypeIndex == 7) electricCalls++;
            }
            public void StampDensity(Vector2 uvPosition, Texture2D stamp, float densityMultiplier, bool useColorOverride, Color overrideColor) { }
            public void ClearDensityWithMask(Vector2 uvPosition, Texture2D mask, float blackLuminanceThreshold = 0.2f) { }
            public void StampObstacles(Vector2 uvPosition, Texture2D stamp) { }
        }

        [UnityTest]
        public IEnumerator CircleTriggersPlantSeed()
        {
            yield return RunGesture("Circle", expectedPlant: true);
        }

        [UnityTest]
        public IEnumerator LightningTriggersElectricSeed()
        {
            yield return RunGesture("LightningZigzag", expectedElectric: true);
        }

        private IEnumerator RunGesture(string templateName, bool expectedPlant = false, bool expectedElectric = false)
        {
            var go = new GameObject("GestureSeedTest");
            var writer = go.AddComponent<StubWriter>();
            var manager = go.AddComponent<GestureInputController>();

            // Inject fields
            manager.GetType().GetField("simulationWriterSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(manager, writer);
            manager.GetType().GetField("minScore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(manager, 0.1f);

            var template = ScriptableObject.CreateInstance<GestureTemplate>();
            template.templateName = templateName;
            template.points = templateName == "Circle"
                ? new List<Vector2> { new(0.5f,0f), new(1f,0.5f), new(0.5f,1f), new(0f,0.5f), new(0.5f,0f) }
                : new List<Vector2> { new(0f,0.8f), new(0.3f,0.2f), new(0.5f,0.8f), new(0.7f,0.2f), new(1f,0.8f) };

            manager.GetType().GetField("templates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(manager,
                new List<GestureTemplate> { template });

            var map = ScriptableObject.CreateInstance<GestureActionMap>();
            map.actions = new List<GestureActionMap.GestureAction>
            {
                new GestureActionMap.GestureAction { gestureName = "Circle", actionId = "seed.plant" },
                new GestureActionMap.GestureAction { gestureName = "LightningZigzag", actionId = "seed.electric" }
            };
            manager.GetType().GetField("actionMap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(manager, map);

            // Provide stroke matching the template
            var strokeField = manager.GetType().GetField("stroke", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            strokeField?.SetValue(manager, template.points);
            manager.GetType().GetMethod("RecognizeAndDispatch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(manager, null);

            yield return null;

            if (expectedPlant)
                Assert.GreaterOrEqual(writer.plantCalls, 1, "Circle should inject plant seeds (index 2)");
            if (expectedElectric)
                Assert.GreaterOrEqual(writer.electricCalls, 1, "Lightning should inject electric seeds (index 7)");
        }
    }
}