using System.Collections.Generic;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Magi.Inkling.Systems.Gestures;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.PlayMode
{
    public class GestureInputControllerTests
    {
        // TODO: migrate to Helpers.StubSimulationWriter
        private class StubWriter : MonoBehaviour, ISimulationWriter
        {
            public int densityCalls;
            public int forceCalls;
            public Vector2 lastForce;
            public void InjectForce(Vector2 position, Vector2 force)
            {
                forceCalls++;
                lastForce = force;
            }
            public void InjectDensity(Vector2 position, Color color, int inkTypeIndex = 0)
            {
                densityCalls++;
            }
            public void StampDensity(Vector2 uvPosition, Texture2D stamp, float densityMultiplier, bool useColorOverride, Color overrideColor) { }
            public void ClearDensityWithMask(Vector2 uvPosition, Texture2D mask, float blackLuminanceThreshold = 0.2f) { }
            public void StampObstacles(Vector2 uvPosition, Texture2D stamp) { }
        }

        [UnityTest]
        public IEnumerator RecognizeLineDispatchesForce()
        {
            var go = new GameObject("GestureTest");
            var writer = go.AddComponent<StubWriter>();
            var manager = go.AddComponent<GestureInputController>();

            // Inject private fields via reflection for test.
            manager.GetType().GetField("simulationWriterSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(manager, writer);
            // Awake already ran (during AddComponent) with a null source, so the resolved private `writer`
            // is still null. Set it directly — otherwise the reflection-invoked dispatch NREs on writer.
            manager.GetType().GetField("writer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(manager, writer);
            manager.GetType().GetField("templates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(manager,
                new List<GestureTemplate> { CreateLineTemplate() });
            var map = ScriptableObject.CreateInstance<GestureActionMap>();
            map.actions = new List<GestureActionMap.GestureAction> { new GestureActionMap.GestureAction { gestureName = "Line", actionId = "force.line" } };
            manager.GetType().GetField("actionMap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(manager, map);
            manager.GetType().GetField("minScore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.SetValue(manager, 0.1f);

            // Simulate a line stroke
            var strokeField = manager.GetType().GetField("stroke", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var stroke = new List<Vector2> { new(0.1f, 0.5f), new(0.9f, 0.5f) };
            strokeField?.SetValue(manager, stroke);
            manager.GetType().GetMethod("RecognizeAndDispatch", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(manager, null);

            yield return null;

            Assert.GreaterOrEqual(writer.forceCalls, 1, "Force should be injected for line gesture");
            Assert.IsTrue(writer.lastForce.x > 0f, "Force should point in +X");
        }

        private GestureTemplate CreateLineTemplate()
        {
            var tmpl = ScriptableObject.CreateInstance<GestureTemplate>();
            tmpl.templateName = "Line";
            tmpl.points = new List<Vector2> { new(0f, 0.5f), new(1f, 0.5f) };
            return tmpl;
        }
    }
}