using System.Runtime.InteropServices;
using UnityEngine;

namespace Magi.Inkling.Systems.Agents
{
    /// <summary>
    /// GPU agent struct definition. Must match Agent in AgentCommon.hlsl exactly.
    /// Agents are advected by the fluid velocity field with optional flocking behavior.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Agent
    {
        /// <summary>Position in UV space [0,1].</summary>
        public Vector2 position;

        /// <summary>Current velocity (UV units per second).</summary>
        public Vector2 velocity;

        /// <summary>Computed flocking steering force (set by AgentFlocking kernel).</summary>
        public Vector2 flockForce;

        /// <summary>How much this agent follows fluid velocity (0=ignore, 1=full advection).</summary>
        public float advectionWeight;

        /// <summary>How much this agent participates in flocking (0=solo, 1=full flocking).</summary>
        public float flockWeight;

        /// <summary>
        /// Packed flags:
        /// - Bit 0: active (1=alive, 0=inactive/dead)
        /// - Bits 1-4: ink type index (0-15)
        /// - Bits 5-7: behavior ID (0-7)
        /// </summary>
        public uint flags;

        /// <summary>GPU buffer stride in bytes (9 floats = 36 bytes).</summary>
        public const int Stride = 36;

        /// <summary>Returns true if this agent is active.</summary>
        public bool IsActive => (flags & 1) != 0;

        /// <summary>Gets the ink type index (0-15).</summary>
        public int InkType => (int)((flags >> 1) & 0xF);

        /// <summary>Gets the behavior ID (0-7).</summary>
        public int BehaviorId => (int)((flags >> 5) & 0x7);

        /// <summary>Creates an active agent at the specified position.</summary>
        public static Agent Create(Vector2 pos, Vector2 vel, float advection = 1f, float flock = 1f, int inkType = 0, int behaviorId = 0)
        {
            return new Agent
            {
                position = pos,
                velocity = vel,
                flockForce = Vector2.zero,
                advectionWeight = advection,
                flockWeight = flock,
                flags = 1u | ((uint)(inkType & 0xF) << 1) | ((uint)(behaviorId & 0x7) << 5)
            };
        }

        /// <summary>Creates an inactive/dead agent.</summary>
        public static Agent Inactive => new Agent { flags = 0 };
    }
}
