using UnityEngine;
using Magi.UnityTools.Core;
using Magi.UnityTools.Patterns;
using Magi.InkTools.Simulation;

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

            // Use canonical InkTypeId enum for iparticle field indices
            int inkIndex = seedType == SeedType.Plant
                ? (int)InkTypeId.PlantSeeded       // 2
                : (int)InkTypeId.ElectricitySeeded; // 7

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
            var init = InitializeGrowth();
            if (!init.IsSuccess)
            {
                Debug.LogError($"[GrowthSystem] Init failed: {init}");
                Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal($"Growth init failed: {init}");
                enabled = false;
                return;
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

            // Register as service
            var locator = ServiceLocator.Instance;
            if (locator != null)
            {
                locator.RegisterService(this);
            }
        }

        private Result InitializeGrowth()
        {
            if (growthCompute == null)
            {
                return Result.Fail("Growth compute shader not assigned.");
            }

            try
            {
                kernelGrowSeeds = growthCompute.FindKernel("GrowSeeds");
            }
            catch (System.Exception e)
            {
                return Result.Fail(e);
            }

            if (config == null)
            {
                config = ScriptableObject.CreateInstance<GrowthConfig>();
                Debug.LogWarning("[GrowthSystem] No config assigned, using defaults.");
            }

            return Result.Success();
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
            growthCompute.SetFloat("_PlantGrowthWaterThreshold", config.plantGrowthWaterThreshold);

            // Electricity growth params
            growthCompute.SetFloat("_ElectricityGrowthRate", config.electricityGrowthRate);
            growthCompute.SetFloat("_ElectricityMaxGrown", config.electricityMaxGrown);
            growthCompute.SetFloat("_ElectricitySeedThreshold", config.electricitySeedThreshold);

            // Spreading params
            growthCompute.SetInt("_EnableSpread", config.enableSpread ? 1 : 0);
            growthCompute.SetFloat("_CardinalSpreadWeight", config.cardinalSpreadWeight);
            growthCompute.SetFloat("_DiagonalSpreadWeight", config.diagonalSpreadWeight);
            growthCompute.SetFloat("_PlantSpreadWaterThreshold", config.plantSpreadWaterThreshold);
            growthCompute.SetFloat("_ElectricitySpreadWaterThreshold", config.electricitySpreadWaterThreshold);
            growthCompute.SetFloat("_ElectricitySpreadIceThreshold", config.electricitySpreadIceThreshold);

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
