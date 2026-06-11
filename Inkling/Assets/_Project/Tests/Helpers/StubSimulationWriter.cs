using System.Collections.Generic;
using UnityEngine;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.Helpers
{
    /// <summary>
    /// Shared test stub for ISimulationWriter. MonoBehaviour so existing tests
    /// can use AddComponent&lt;StubSimulationWriter&gt;().
    /// Replaces inline StubWriter classes duplicated across GestureInputControllerTests,
    /// GestureSeedActionTests, OpticalFlowInputTests, etc.
    /// </summary>
    public class StubSimulationWriter : MonoBehaviour, ISimulationWriter
    {
        public int ForceCalls;
        public int DensityCalls;
        public int StampCalls;
        public int ClearMaskCalls;
        public int ObstacleCalls;

        public Vector2 LastForcePosition;
        public Vector2 LastForce;
        public Vector2 LastDensityPosition;
        public Color LastDensityColor;
        public int LastDensityInkTypeIndex = -1;

        public readonly List<(Vector2 pos, Vector2 force)> ForceHistory = new();
        public readonly List<(Vector2 pos, Color color, int index)> DensityHistory = new();

        public void InjectForce(Vector2 position, Vector2 force)
        {
            ForceCalls++;
            LastForcePosition = position;
            LastForce = force;
            ForceHistory.Add((position, force));
        }

        public void InjectDensity(Vector2 position, Color color, int inkTypeIndex = 0)
        {
            DensityCalls++;
            LastDensityPosition = position;
            LastDensityColor = color;
            LastDensityInkTypeIndex = inkTypeIndex;
            DensityHistory.Add((position, color, inkTypeIndex));
        }

        public void StampDensity(Vector2 uvPosition, Texture2D stamp, float densityMultiplier,
            bool useColorOverride, Color overrideColor)
        {
            StampCalls++;
        }

        public void ClearDensityWithMask(Vector2 uvPosition, Texture2D mask,
            float blackLuminanceThreshold = 0.2f)
        {
            ClearMaskCalls++;
        }

        public void StampObstacles(Vector2 uvPosition, Texture2D stamp)
        {
            ObstacleCalls++;
        }

        public void Reset()
        {
            ForceCalls = DensityCalls = StampCalls = ClearMaskCalls = ObstacleCalls = 0;
            LastDensityInkTypeIndex = -1;
            ForceHistory.Clear();
            DensityHistory.Clear();
        }
    }
}
