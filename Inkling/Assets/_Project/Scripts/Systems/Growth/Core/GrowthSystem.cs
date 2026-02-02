using UnityEngine;
using Magi.Inkling.Services;

namespace Magi.Inkling.Systems.Growth
{
    /// <summary>
    /// GPU-based growth system that converts seeded particles to grown particles.
    /// Handles both plant and electricity growth.
    /// </summary>
    [DefaultExecutionOrder(55)] // Run after SimDriver (50) but before late rendering
    public class GrowthSystem : MonoBehaviour, IGrowthSystem
    {
        [Header("Compute")]
        [SerializeField] private ComputeShader growthCompute;

        [Header("Configuration")]
        [SerializeField] private GrowthConfig config;

        [Header("Simulation Reference")]
        [Tooltip("Reference to simulation for particle buffer. If null, will attempt to find ISimulationReader at runtime.")]
        [SerializeField] private MonoBehaviour simulationSource;

        // Kernel index
        private int kernelGrowSeeds;

        // Runtime state
        private ISimulationReader simReader;
        private ISimulationWriter simWriter;
        private bool isInitialized;

        #region IGrowthSystem Implementation

        public bool IsInitialized => isInitialized;
        public GrowthConfig Config => config;

        public void PlantSeed(Vector2 uvPosition, SeedType seedType, float amount = 1f)
        {
            if (!isInitialized || simWriter == null) return;

            // Use the simulation writer to inject the seed as density
            // Plant seeds go to plantSeeded channel (index 2)
            // Electricity seeds go to electricitySeeded channel (index 7)
            int inkIndex = seedType == SeedType.Plant ? 2 : 7;

            // Create a color that represents the seed amount
            // The StampParticlesCompute shader uses inkTypeIndex to route to correct channel
            Color seedColor = new Color(amount, amount, amount, 1f);

            simWriter.InjectDensity(uvPosition, seedColor, inkIndex);
        }

        public void PlantSeedsInRegion(Rect uvRegion, int count, SeedType seedType, float amount = 1f)
        {
            if (!isInitialized || simWriter == null) return;

            for (int i = 0; i < count; i++)
            {
                Vector2 pos = new Vector2(
                    Random.Range(uvRegion.xMin, uvRegion.xMax),
                    Random.Range(uvRegion.yMin, uvRegion.yMax)
                );
                PlantSeed(pos, seedType, amount);
            }
        }

        public void SetGrowthRates(float plantRate, float electricityRate)
        {
            if (config == null) return;
            config.plantGrowthRate = plantRate;
            config.electricityGrowthRate = electricityRate;
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (growthCompute == null)
            {
                Debug.LogError("[GrowthSystem] Growth compute shader not assigned.");
                enabled = false;
                return;
            }

            // Find kernel
            kernelGrowSeeds = growthCompute.FindKernel("GrowSeeds");

            if (config == null)
            {
                config = ScriptableObject.CreateInstance<GrowthConfig>();
                Debug.LogWarning("[GrowthSystem] No config assigned, using defaults.");
            }

            isInitialized = true;
        }

        private void Start()
        {
            // Find simulation interfaces
            if (simulationSource != null)
            {
                if (simulationSource is ISimulationReader reader)
                    simReader = reader;
                if (simulationSource is ISimulationWriter writer)
                    simWriter = writer;
            }
            else
            {
                foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb is ISimulationReader r && simReader == null)
                        simReader = r;
                    if (mb is ISimulationWriter w && simWriter == null)
                        simWriter = w;
                }
            }

            if (simReader == null)
            {
                Debug.LogWarning("[GrowthSystem] No ISimulationReader found. " +
                               "Growth simulation will not run.");
            }
        }

        private void LateUpdate()
        {
            if (!isInitialized || simReader == null) return;

            RunGrowthSimulation(Time.deltaTime);
        }

        #endregion

        #region GPU Dispatch

        private void RunGrowthSimulation(float deltaTime)
        {
            var particleBuffer = simReader.GetParticleBuffer();
            if (particleBuffer == null) return;

            int resolution = simReader.Resolution;
            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            // Set buffers (in-place modification)
            growthCompute.SetBuffer(kernelGrowSeeds, "_Particles", particleBuffer);

            // Set parameters
            growthCompute.SetInt("_Resolution", resolution);
            growthCompute.SetFloat("_DeltaTime", deltaTime);

            // Plant growth params
            growthCompute.SetFloat("_PlantGrowthRate", config.plantGrowthRate);
            growthCompute.SetFloat("_PlantMaxGrown", config.plantMaxGrown);
            growthCompute.SetFloat("_PlantSeedThreshold", config.plantSeedThreshold);

            // Electricity growth params
            growthCompute.SetFloat("_ElectricityGrowthRate", config.electricityGrowthRate);
            growthCompute.SetFloat("_ElectricityMaxGrown", config.electricityMaxGrown);
            growthCompute.SetFloat("_ElectricitySeedThreshold", config.electricitySeedThreshold);

            // Spreading params
            growthCompute.SetInt("_EnableSpread", config.enableSpread ? 1 : 0);
            growthCompute.SetFloat("_CardinalSpreadWeight", config.cardinalSpreadWeight);
            growthCompute.SetFloat("_DiagonalSpreadWeight", config.diagonalSpreadWeight);

            // Dispatch
            growthCompute.Dispatch(kernelGrowSeeds, threadGroups, threadGroups, 1);
        }

        #endregion

        #region Editor Helpers

        [ContextMenu("Plant Test Seeds")]
        private void PlantTestSeeds()
        {
            if (!isInitialized)
            {
                Debug.LogError("[GrowthSystem] Not initialized. Enter play mode first.");
                return;
            }

            PlantSeedsInRegion(new Rect(0.4f, 0.4f, 0.2f, 0.2f), 10, SeedType.Plant, 0.5f);
            Debug.Log("[GrowthSystem] Planted 10 test plant seeds.");
        }

        [ContextMenu("Plant Electricity Seeds")]
        private void PlantElectricitySeeds()
        {
            if (!isInitialized)
            {
                Debug.LogError("[GrowthSystem] Not initialized. Enter play mode first.");
                return;
            }

            PlantSeedsInRegion(new Rect(0.4f, 0.4f, 0.2f, 0.2f), 5, SeedType.Electricity, 0.5f);
            Debug.Log("[GrowthSystem] Planted 5 test electricity seeds.");
        }

        #endregion
    }
}
