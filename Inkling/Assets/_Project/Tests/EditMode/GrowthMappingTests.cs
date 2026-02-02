using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Magi.Inkling.Systems.Growth;
using Magi.Inkling.Services;

namespace Magi.Inkling.Tests.EditMode
{
    public class GrowthMappingTests
    {
        private class FakeWriter : ISimulationWriter
        {
            public Vector2 LastPosition { get; private set; }
            public Color LastColor { get; private set; }
            public int LastIndex { get; private set; } = -1;

            public void InjectDensity(Vector2 position, Color color, int inkTypeIndex = 0)
            {
                LastPosition = position;
                LastColor = color;
                LastIndex = inkTypeIndex;
            }

            public void InjectForce(Vector2 position, Vector2 force) { }
            public void StampDensity(Vector2 uvPosition, Texture2D stamp, float densityMultiplier, bool useColorOverride, Color overrideColor) { }
            public void ClearDensityWithMask(Vector2 uvPosition, Texture2D mask, float blackLuminanceThreshold = 0.2f) { }
            public void StampObstacles(Vector2 uvPosition, Texture2D stamp) { }
        }

        [Test]
        public void PlantSeed_UsesSeedChannelIndices()
        {
            var go = new GameObject("GrowthSystemTest");
            var growth = go.AddComponent<GrowthSystem>();
            var writer = new FakeWriter();

            // Bypass runtime wiring: mark initialized and inject our writer via reflection
            typeof(GrowthSystem).GetField("isInitialized", BindingFlags.Instance | BindingFlags.NonPublic)
                                 ?.SetValue(growth, true);
            typeof(GrowthSystem).GetField("simWriter", BindingFlags.Instance | BindingFlags.NonPublic)
                                 ?.SetValue(growth, writer);

            growth.PlantSeed(new Vector2(0.5f, 0.5f), SeedType.Plant, 1f);
            Assert.AreEqual(2, writer.LastIndex, "Plant seeds must target plantSeeded (index 2)");

            growth.PlantSeed(new Vector2(0.25f, 0.75f), SeedType.Electricity, 1f);
            Assert.AreEqual(7, writer.LastIndex, "Electricity seeds must target electricitySeeded (index 7)");
        }
    }
}