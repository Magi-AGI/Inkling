using UnityEngine;

namespace Magi.Inkling.Systems.Agents
{
    /// <summary>
    /// CPU-side interface for the GPU agent system.
    /// Provides spawning, despawning, and parameter configuration.
    /// Agent state lives entirely on GPU; CPU only manages lifecycle.
    /// </summary>
    public interface IAgentSystem
    {
        /// <summary>Maximum number of agents the system can handle.</summary>
        int MaxAgents { get; }

        /// <summary>Current number of active agents (may be approximate, requires GPU readback for exact count).</summary>
        int ActiveAgentCount { get; }

        /// <summary>Whether the agent system is initialized and ready.</summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Spawns agents at specific positions with the given configuration.
        /// </summary>
        /// <param name="positions">UV positions for each agent.</param>
        /// <param name="config">Configuration for spawned agents.</param>
        /// <returns>Number of agents actually spawned (may be less if buffer is full).</returns>
        int SpawnAgents(Vector2[] positions, AgentConfig config);

        /// <summary>
        /// Spawns agents randomly within a UV region.
        /// </summary>
        /// <param name="uvRegion">Region in UV space (x,y = min corner, width/height = size).</param>
        /// <param name="count">Number of agents to spawn.</param>
        /// <param name="config">Configuration for spawned agents.</param>
        /// <returns>Number of agents actually spawned.</returns>
        int SpawnAgentsInRegion(Rect uvRegion, int count, AgentConfig config);

        /// <summary>
        /// Marks all agents as inactive.
        /// </summary>
        void DespawnAll();

        /// <summary>
        /// Marks agents within a UV region as inactive.
        /// Requires GPU readback or approximate culling.
        /// </summary>
        /// <param name="uvRegion">Region to despawn agents from.</param>
        void DespawnInRegion(Rect uvRegion);

        /// <summary>
        /// Updates flocking parameters at runtime.
        /// </summary>
        void SetFlockingParams(float neighborRadius, float alignment, float cohesion, float separation);

        /// <summary>
        /// Updates advection strength at runtime.
        /// </summary>
        void SetAdvectionStrength(float strength);

        /// <summary>
        /// Gets the GPU compute buffer containing agent data.
        /// For use by rendering systems (DrawProceduralIndirect).
        /// </summary>
        ComputeBuffer GetAgentBuffer();
    }
}
