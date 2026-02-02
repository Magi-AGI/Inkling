using UnityEngine;
using Magi.Inkling.Services;

namespace Magi.Inkling.Systems.Agents
{
    /// <summary>
    /// GPU-based agent system with flocking and fluid advection.
    /// All agent state lives in GPU compute buffers; CPU handles spawning and dispatch.
    /// </summary>
    [DefaultExecutionOrder(100)] // Run after SimDriver (50) so velocity texture is ready
    public class AgentSystem : MonoBehaviour, IAgentSystem
    {
        [Header("Compute")]
        [SerializeField] private ComputeShader agentCompute;

        [Header("Capacity")]
        [SerializeField] private int maxAgents = 4096;

        [Header("Configuration")]
        [SerializeField] private AgentConfig defaultConfig;

        [Header("Simulation Reference")]
        [Tooltip("Reference to simulation for velocity texture. If null, will attempt to find ISimulationReader at runtime.")]
        [SerializeField] private MonoBehaviour simulationSource;

        // GPU buffers (ping-pong)
        private ComputeBuffer agentsBufferA;
        private ComputeBuffer agentsBufferB;
        private int readIndex = 0;

        // Kernel indices
        private int kernelFlocking;
        private int kernelUpdate;
        private int kernelSpawn;

        // Runtime state
        private ISimulationReader simReader;
        private int activeCount;
        private int nextSpawnIndex;
        private bool isInitialized;

        // Current parameters (for runtime updates)
        private float neighborRadius;
        private float alignmentWeight;
        private float cohesionWeight;
        private float separationWeight;
        private float maxSpeed;
        private float maxForce;

        #region IAgentSystem Implementation

        public int MaxAgents => maxAgents;
        public int ActiveAgentCount => activeCount;
        public bool IsInitialized => isInitialized;

        public int SpawnAgents(Vector2[] positions, AgentConfig config)
        {
            if (!isInitialized || positions == null || positions.Length == 0)
                return 0;

            config = config ?? defaultConfig;
            if (config == null)
            {
                Debug.LogError("[AgentSystem] No config provided and no default config set.");
                return 0;
            }

            int spawnCount = Mathf.Min(positions.Length, maxAgents - nextSpawnIndex);
            if (spawnCount <= 0)
            {
                Debug.LogWarning("[AgentSystem] Agent buffer full, cannot spawn more agents.");
                return 0;
            }

            // Upload agents via CPU (for specific positions)
            var agents = new Agent[spawnCount];
            for (int i = 0; i < spawnCount; i++)
            {
                agents[i] = Agent.Create(
                    positions[i],
                    Random.insideUnitCircle.normalized * config.initialSpeed,
                    config.advectionStrength,
                    config.flockingStrength,
                    config.defaultInkType,
                    config.defaultBehaviorId
                );
            }

            var writeBuffer = readIndex == 0 ? agentsBufferA : agentsBufferB;
            writeBuffer.SetData(agents, 0, nextSpawnIndex, spawnCount);

            nextSpawnIndex += spawnCount;
            activeCount += spawnCount;

            return spawnCount;
        }

        public int SpawnAgentsInRegion(Rect uvRegion, int count, AgentConfig config)
        {
            if (!isInitialized) return 0;

            config = config ?? defaultConfig;
            if (config == null)
            {
                Debug.LogError("[AgentSystem] No config provided and no default config set.");
                return 0;
            }

            int spawnCount = Mathf.Min(count, maxAgents - nextSpawnIndex);
            if (spawnCount <= 0)
            {
                Debug.LogWarning("[AgentSystem] Agent buffer full, cannot spawn more agents.");
                return 0;
            }

            // Use GPU spawn kernel for random positions
            var writeBuffer = readIndex == 0 ? agentsBufferA : agentsBufferB;

            agentCompute.SetBuffer(kernelSpawn, "_AgentsWrite", writeBuffer);
            agentCompute.SetVector("_SpawnRegionMin", new Vector4(uvRegion.xMin, uvRegion.yMin, 0, 0));
            agentCompute.SetVector("_SpawnRegionMax", new Vector4(uvRegion.xMax, uvRegion.yMax, 0, 0));
            agentCompute.SetInt("_SpawnStartIndex", nextSpawnIndex);
            agentCompute.SetInt("_SpawnCount", spawnCount);
            agentCompute.SetFloat("_SpawnAdvectionWeight", config.advectionStrength);
            agentCompute.SetFloat("_SpawnFlockWeight", config.flockingStrength);
            agentCompute.SetInt("_SpawnFlags", 1 | (config.defaultInkType << 1) | (config.defaultBehaviorId << 5));
            agentCompute.SetVector("_SpawnVelocity", (Vector4)(Random.insideUnitCircle.normalized * config.initialSpeed));

            int threadGroups = Mathf.CeilToInt(spawnCount / 64f);
            agentCompute.Dispatch(kernelSpawn, threadGroups, 1, 1);

            nextSpawnIndex += spawnCount;
            activeCount += spawnCount;

            return spawnCount;
        }

        public void DespawnAll()
        {
            if (!isInitialized) return;

            // Clear both buffers by uploading inactive agents
            var empty = new Agent[maxAgents];
            for (int i = 0; i < maxAgents; i++)
                empty[i] = Agent.Inactive;

            agentsBufferA.SetData(empty);
            agentsBufferB.SetData(empty);

            activeCount = 0;
            nextSpawnIndex = 0;
        }

        public void DespawnInRegion(Rect uvRegion)
        {
            // TODO: Implement GPU despawn kernel or CPU readback approach
            Debug.LogWarning("[AgentSystem] DespawnInRegion not yet implemented.");
        }

        public void SetFlockingParams(float radius, float alignment, float cohesion, float separation)
        {
            neighborRadius = radius;
            alignmentWeight = alignment;
            cohesionWeight = cohesion;
            separationWeight = separation;
        }

        public void SetAdvectionStrength(float strength)
        {
            // This would need to modify all agents' advectionWeight
            // For now, only affects newly spawned agents via config
            Debug.LogWarning("[AgentSystem] SetAdvectionStrength only affects new spawns. " +
                           "Modifying existing agents requires GPU kernel.");
        }

        public ComputeBuffer GetAgentBuffer()
        {
            return readIndex == 0 ? agentsBufferA : agentsBufferB;
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (agentCompute == null)
            {
                Debug.LogError("[AgentSystem] Agent compute shader not assigned.");
                enabled = false;
                return;
            }

            // Find kernel indices
            kernelFlocking = agentCompute.FindKernel("AgentFlocking");
            kernelUpdate = agentCompute.FindKernel("AgentUpdate");
            kernelSpawn = agentCompute.FindKernel("AgentSpawn");

            // Create compute buffers
            agentsBufferA = new ComputeBuffer(maxAgents, Agent.Stride);
            agentsBufferB = new ComputeBuffer(maxAgents, Agent.Stride);

            // Initialize with inactive agents
            DespawnAll();

            // Apply default config
            if (defaultConfig != null)
            {
                neighborRadius = defaultConfig.neighborRadius;
                alignmentWeight = defaultConfig.alignmentWeight;
                cohesionWeight = defaultConfig.cohesionWeight;
                separationWeight = defaultConfig.separationWeight;
                maxSpeed = defaultConfig.maxSpeed;
                maxForce = defaultConfig.maxForce;
            }
            else
            {
                // Sensible defaults
                neighborRadius = 0.05f;
                alignmentWeight = 1f;
                cohesionWeight = 1f;
                separationWeight = 1.5f;
                maxSpeed = 0.1f;
                maxForce = 0.05f;
            }

            isInitialized = true;
        }

        private void Start()
        {
            // Find simulation reader
            if (simulationSource != null && simulationSource is ISimulationReader reader)
            {
                simReader = reader;
            }
            else
            {
                // Try to find in scene
                var simDriver = FindFirstObjectByType<MonoBehaviour>();
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb is ISimulationReader r)
                    {
                        simReader = r;
                        break;
                    }
                }
            }

            if (simReader == null)
            {
                Debug.LogWarning("[AgentSystem] No ISimulationReader found. " +
                               "Agents will not be advected by fluid velocity.");
            }
        }

        private void Update()
        {
            if (!isInitialized || activeCount == 0) return;

            UpdateAgents(Time.deltaTime);
        }

        private void OnDestroy()
        {
            agentsBufferA?.Release();
            agentsBufferB?.Release();
            agentsBufferA = null;
            agentsBufferB = null;
            isInitialized = false;
        }

        #endregion

        #region GPU Dispatch

        /// <summary>
        /// Runs the agent simulation for one frame.
        /// </summary>
        public void UpdateAgents(float deltaTime)
        {
            if (!isInitialized) return;

            var readBuffer = readIndex == 0 ? agentsBufferA : agentsBufferB;
            var writeBuffer = readIndex == 0 ? agentsBufferB : agentsBufferA;

            int threadGroups = Mathf.CeilToInt(maxAgents / 64f);

            // Set common parameters
            agentCompute.SetInt("_AgentCount", maxAgents);
            agentCompute.SetFloat("_DeltaTime", deltaTime);
            agentCompute.SetFloat("_NeighborRadius", neighborRadius);
            agentCompute.SetFloat("_AlignmentWeight", alignmentWeight);
            agentCompute.SetFloat("_CohesionWeight", cohesionWeight);
            agentCompute.SetFloat("_SeparationWeight", separationWeight);
            agentCompute.SetFloat("_MaxSpeed", maxSpeed);
            agentCompute.SetFloat("_MaxForce", maxForce);

            // Flocking pass: read → write flock forces
            agentCompute.SetBuffer(kernelFlocking, "_AgentsRead", readBuffer);
            agentCompute.SetBuffer(kernelFlocking, "_AgentsWrite", writeBuffer);
            agentCompute.Dispatch(kernelFlocking, threadGroups, 1, 1);

            // Update pass: read (with flock forces) → write (new positions)
            // Note: After flocking, writeBuffer has flock forces, so we read from there
            agentCompute.SetBuffer(kernelUpdate, "_AgentsRead", writeBuffer);
            agentCompute.SetBuffer(kernelUpdate, "_AgentsWrite", readBuffer);

            if (simReader != null)
            {
                var velocityTex = simReader.GetVelocityTexture();
                if (velocityTex != null)
                {
                    agentCompute.SetTexture(kernelUpdate, "_VelocityRead", velocityTex);
                    agentCompute.SetVector("_SimulationSize", new Vector4(simReader.Resolution, simReader.Resolution, 0, 0));
                }
            }
            else
            {
                agentCompute.SetVector("_SimulationSize", new Vector4(256, 256, 0, 0));
            }

            agentCompute.Dispatch(kernelUpdate, threadGroups, 1, 1);

            // After update pass, readBuffer contains final state
            // No swap needed because we wrote back to readBuffer
        }

        #endregion

        #region Editor Helpers

        /// <summary>
        /// Test spawning from inspector.
        /// </summary>
        [ContextMenu("Spawn 100 Test Agents")]
        private void SpawnTestAgents()
        {
            if (!isInitialized)
            {
                Debug.LogError("[AgentSystem] Not initialized. Enter play mode first.");
                return;
            }

            SpawnAgentsInRegion(new Rect(0.3f, 0.3f, 0.4f, 0.4f), 100, defaultConfig);
            Debug.Log($"[AgentSystem] Spawned agents. Active count: {activeCount}");
        }

        [ContextMenu("Despawn All")]
        private void EditorDespawnAll()
        {
            if (!isInitialized) return;
            DespawnAll();
            Debug.Log("[AgentSystem] All agents despawned.");
        }

        #endregion
    }
}
