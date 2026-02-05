using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Magi.Inkling.Systems.OpticalFlow;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.PlayMode
{
    public class OpticalFlowInputTests
    {
        private class StubWriter : MonoBehaviour, ISimulationWriter
        {
            public int forceCalls;
            public Vector2 lastForce;
            public void InjectForce(Vector2 position, Vector2 force)
            {
                forceCalls++;
                lastForce = force;
            }
            public void InjectDensity(Vector2 position, Color color, int inkTypeIndex = 0) { }
            public void StampDensity(Vector2 uvPosition, Texture2D stamp, float densityMultiplier, bool useColorOverride, Color overrideColor) { }
            public void ClearDensityWithMask(Vector2 uvPosition, Texture2D mask, float blackLuminanceThreshold = 0.2f) { }
            public void StampObstacles(Vector2 uvPosition, Texture2D stamp) { }
        }

        [UnityTest]
        public IEnumerator FlowTextureInjectsForce()
        {
            var go = new GameObject("FlowTest");
            var writer = go.AddComponent<StubWriter>();
            var flow = go.AddComponent<OpticalFlowInput>();

            // Make a small flow texture pointing +X
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            tex.SetPixel(0, 0, new Color(1f, 0.5f, 0, 1)); // r=1,g=0.5 => dir (1,0)
            tex.Apply();

            flow.GetType().GetField("flowTexture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.SetValue(flow, tex);
            flow.GetType().GetField("simulationWriterSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.SetValue(flow, writer);
            flow.GetType().GetField("forceMultiplier", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.SetValue(flow, 100f);
            flow.GetType().GetField("enabledModule", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!.SetValue(flow, true);

            yield return null;

            Assert.GreaterOrEqual(writer.forceCalls, 1, "Force should be injected from flow");
            Assert.IsTrue(writer.lastForce.x > 0f, "Force should point +X");
        }
    }
}