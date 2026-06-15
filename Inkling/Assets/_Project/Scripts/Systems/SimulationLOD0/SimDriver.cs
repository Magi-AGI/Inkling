using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using Magi.UnityTools.Core;
using Magi.InkTools;
using Magi.InkTools.Simulation;
using Magi.UnityTools.Patterns;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Debug = UnityEngine.Debug;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Facade for the fluid simulation. Owns all [SerializeField] references (single Inspector surface),
    /// creates modules in Start(), delegates to them in Update()/LateUpdate(), and disposes in OnDestroy().
    /// Implements ISimulationService as thin wrappers around OperationQueue and context accessors.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class SimDriver : MonoBehaviour, ISimulationService, ISimulationDebug
    {
        private static readonly string[] FluidComputeCandidatePaths =
        {
            "Packages/com.inktools.sim/Compute/Fluids.compute",
            "Packages/com.inktools.sim/Runtime/Compute/Fluids.compute",
            "Assets/_Project/Scripts/Simulation/Compute/Fluids.compute",
            "Assets/_Project/Scripts/Systems/SimulationLOD0/Fluids.compute",
        };

        // ── Compute Shaders ─────────────────────────────────────────────────
        [Header("Compute Shader")]
        [SerializeField] public ComputeShader fluidCompute;

        [Header("Simulation Parameters")]
        [SerializeField] private int resolution = 256;
        [SerializeField] private float viscosity = 0.0001f;
        [SerializeField] private float vorticity = 20.0f;
        [SerializeField] private float dissipation = 0.999f;
        [SerializeField] private float velocityDissipation = 0.9995f;
        [SerializeField] private float timestep = 0.016f;

        [Header("Air Debug")]
        [SerializeField] private bool debugZeroPressure = false;
        [SerializeField] private bool debugZeroVelocity = false;
        [SerializeField] private bool debugSkipAir = false;

        // ISimulationDebug
        public bool DebugZeroPressure { get => debugZeroPressure; set => debugZeroPressure = value; }
        public bool DebugZeroVelocity { get => debugZeroVelocity; set => debugZeroVelocity = value; }
        public bool DebugSkipAir { get => debugSkipAir; set => debugSkipAir = value; }

        // ISimulationReader properties
        public float Viscosity => viscosity;
        public float Vorticity => vorticity;
        public float Dissipation => dissipation;
        public float VelocityDissipation => velocityDissipation;
        public float Timestep => timestep;
        public int Resolution => resolution;

        [Header("Solver Settings")]
        [SerializeField] private int pressureIterations = 10;
        [SerializeField] private int diffusionIterations = 2;
        [SerializeField] private bool useRedBlackSolver = false;

        [Header("Injection")]
        [SerializeField] private float injectionForce = 100f;
        [SerializeField] private float densityAmount = 0.3f;
        [SerializeField] private float forceRadius = 56f;
        [SerializeField] private float forceStrength = 1.5f;
        [SerializeField] private bool useBatchedDensityInjection = true;
        [SerializeField] private ComputeShader batchedInjectionCompute;
        [SerializeField] private bool useBatchedStamping = true;
        [SerializeField] private bool useBatchedMasks = true;

        [Header("Display")]
        [SerializeField] private Renderer displayRenderer;
        [SerializeField] private bool displayVelocity = false;
        [SerializeField] private bool useParticleDisplay = false;
        [SerializeField] private bool useCpuCreatureComposite = false;
        [SerializeField] private bool useGradientRendering = true;
        [SerializeField] private Magi.Inkling.Systems.Rendering.InkGradientPreset gradientPreset;
        [SerializeField] private Material gradientMaterial;

        [Header("Performance")]
        [SerializeField] private bool measurePerformance = true;

        [Header("Display Resolution")]
        [SerializeField] private int displayResolution = 0;
        [Range(0.1f, 1f)]
        [SerializeField] private float displayResolutionScale = 1f;

        [Header("Creature / Stamp Rendering")]
        [SerializeField] private Shader densityStampShader;
        [SerializeField] private ComputeShader stampCompute;
        [SerializeField] private ComputeShader stampParticlesCompute;
        [SerializeField] private ComputeShader batchedStampCompute;
        [SerializeField] private ComputeShader batchedMaskCompute;

        [Header("Particle Simulation")]
        [SerializeField] private bool useParticleSimulation = true;
        [SerializeField] private bool useParticleAdvection = true;
        [SerializeField] private bool useParticleDissipation = true;
        [SerializeField] private bool useParticleDiffusion = true;
        [SerializeField] private int maxParticleSimResolution = 512;

        [Header("Ink Interactions")]
        [SerializeField] private ComputeShader inkInteractionsCompute;
        [SerializeField] private bool useInkInteractions = true;
        [SerializeField] private bool inkInteractionsDebugMode = false;
        [SerializeField] private AffinityGroup[] affinityGroups;

        [Header("Black Body Ink (Fallback)")]
        [SerializeField] private bool enableBlackBodyClearingFallback = false;
        [Range(0f, 1f)]
        [SerializeField] private float blackBodyThresholdFallback = 0.5f;
        [Range(0f, 0.2f)]
        [SerializeField] private float blackBodyClearingRateFallback = 0.05f;

        [Header("Ink Properties")]
        [SerializeField] private InkTypeDef[] inkDefinitions = new InkTypeDef[10];

        [Header("Particle Rendering")]
        [SerializeField] private ComputeShader particleToColorCompute;
        [SerializeField] private ComputeShader particleChannelSplatCompute;
        [SerializeField] private bool useParticleRenderPass = false;

        [Header("Update Rate")]
        [SerializeField] private int simulationUpdateRate = 0;

        [Header("Substepping (CFL stability)")]
        [Tooltip("Max real seconds advanced by a single solver step. When a frame's real dt exceeds " +
                 "this, the step is split into N equal substeps so per-step advection stays stable / " +
                 "low-Courant at low framerates. <= 0 disables substepping (single step per frame).")]
        [SerializeField] private float maxSubstepDt = 0.016f;
        [Tooltip("Hard cap on substeps per frame, bounding worst-case cost on a hitched frame.")]
        [SerializeField] private int maxSubsteps = 8;

        [Header("Debug / Diagnostics")]
        [Tooltip("Inject a constant circular force every frame to test the velocity pipeline. Enable displayVelocity to visualize.")]
        [SerializeField] private bool debugInjectTestForce = false;
        [Tooltip("Log force injection diagnostics every 60 frames.")]
        [SerializeField] private bool debugLogForces = false;

        [Header("Runtime Selection")]
        [SerializeField] private int currentInkType = 0;

        public int CurrentInkType { get => currentInkType; set => currentInkType = Mathf.Clamp(value, 0, 9); }

        // ── Modules ─────────────────────────────────────────────────────────
        private SimulationContext ctx;
        private SimulationResources resources;
        private OperationQueue operationQueue;
        private FluidSolver fluidSolver;
        private SimulationDisplay display;

        // ── Frame state ─────────────────────────────────────────────────────
        private Stopwatch stopwatch = new Stopwatch();
        private float lastFrameMs;
        private float simAccumulator;
        private bool simRanThisFrame;

        private static bool HasRequiredFluidKernels(ComputeShader shader)
        {
            if (shader == null) return false;

            return shader.HasKernel("Advection")
                && shader.HasKernel("Diffusion")
                && shader.HasKernel("Divergence")
                && shader.HasKernel("Pressure")
                && shader.HasKernel("SubtractGradient")
                && shader.HasKernel("Vorticity")
                && shader.HasKernel("VorticityConfinement")
                && shader.HasKernel("AddForce")
                && shader.HasKernel("AddDensity")
                && shader.HasKernel("Clear");
        }

        #if UNITY_EDITOR
        private static void ForceReimportCompute(ComputeShader shader)
        {
            if (shader == null) return;

            string path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path)) return;

            AssetDatabase.ImportAsset(
                path,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }
        #endif

        private ComputeShader ResolveFluidCompute()
        {
            #if UNITY_EDITOR
            ForceReimportCompute(fluidCompute);
            #endif

            if (HasRequiredFluidKernels(fluidCompute))
                return fluidCompute;

            if (fluidCompute != null)
                Debug.LogWarning($"[SimDriver] Assigned compute shader '{fluidCompute.name}' is missing required fluid kernels.");

            #if UNITY_EDITOR
            foreach (string path in FluidComputeCandidatePaths)
            {
                var candidate = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (candidate == null) continue;

                ForceReimportCompute(candidate);

                if (HasRequiredFluidKernels(candidate))
                {
                    Debug.Log($"[SimDriver] Auto-assigned Fluids.compute from '{path}'.");
                    return candidate;
                }

                Debug.LogWarning($"[SimDriver] Candidate shader at '{path}' is missing required fluid kernels.");
            }
            #endif

            var resourcesCandidate = Resources.Load<ComputeShader>("Fluids");
            if (HasRequiredFluidKernels(resourcesCandidate))
            {
                Debug.Log("[SimDriver] Auto-assigned Fluids.compute from Resources/Fluids.");
                return resourcesCandidate;
            }

            Debug.LogError("[SimDriver] No valid Fluids.compute found. Simulation will run in test/static mode.");
            return fluidCompute;
        }

        private void Start()
        {
            fluidCompute = ResolveFluidCompute();

            ctx = new SimulationContext();
            resources = new SimulationResources();

            SyncContextFromFields();

            // Compute effective display resolution
            ctx.EffectiveDisplayRes = displayResolution > 0
                ? displayResolution
                : Mathf.Max(32, Mathf.RoundToInt(resolution * displayResolutionScale));

            resources.Allocate(ctx);

            operationQueue = new OperationQueue(ctx);
            fluidSolver = new FluidSolver(ctx, operationQueue);
            display = new SimulationDisplay(ctx);

            bool kernelsFound = true;
            if (ctx.FluidCompute != null)
            {
                kernelsFound = fluidSolver.InitializeKernels();
                if (!kernelsFound)
                {
                    Debug.LogWarning("[SimDriver] Missing required kernels. Running in test pattern mode.");
                }
            }

            operationQueue.InitializeKernels();

            if (kernelsFound && ctx.FluidCompute != null)
            {
                fluidSolver.SetConstants();
                fluidSolver.ClearAll();

                if (ctx.FluidKernelUpdateObstacles != 0)
                    fluidSolver.InitializeObstacles();

                // Seed initial density so there's something visible on startup.
                InjectDensity(new Vector2(0.5f, 0.5f), Color.white, 0);
                InjectDensity(new Vector2(0.3f, 0.7f), new Color(1f, 0.5f, 0f, 1f), 0);
                InjectDensity(new Vector2(0.7f, 0.3f), new Color(0f, 0.5f, 1f, 1f), 0);
            }

            // Register with ServiceLocator
            var locator = ServiceLocator.Instance;
            if (locator != null)
                locator.RegisterService(this);

            Debug.Log($"[SimDriver] Initialized {resolution}x{resolution} simulation | " +
                      $"forceStrength={forceStrength}, radius={forceRadius}, " +
                      $"pressureIter={pressureIterations}, diffusionIter={diffusionIterations}, " +
                      $"vorticity={vorticity}, densityAmt={densityAmount}, " +
                      $"redBlack={useRedBlackSolver}");
        }

        /// <summary>
        /// Copies all [SerializeField] values into the shared SimulationContext.
        /// Called once in Start() and at the top of each frame (to pick up Inspector changes).
        /// </summary>
        private void SyncContextFromFields()
        {
            ctx.Resolution = resolution;
            ctx.Timestep = timestep;
            // Real-time this step represents: fixed timestep under external/deterministic control
            // (reproducible) or a fixed sim rate; otherwise the (clamped) real frame delta so
            // dt-normalized decays stay frame-rate independent during live play.
            ctx.FrameDeltaTime = ExternalStepControl ? timestep
                : (simulationUpdateRate > 0 ? 1f / simulationUpdateRate : Mathf.Min(Time.deltaTime, 0.05f));
            ctx.Viscosity = viscosity;
            ctx.VorticityStrength = vorticity;
            ctx.Dissipation = dissipation;
            ctx.VelocityDissipation = velocityDissipation;
            ctx.PressureIterations = pressureIterations;
            ctx.DiffusionIterations = diffusionIterations;
            ctx.UseRedBlackSolver = useRedBlackSolver;
            ctx.InjectionForce = injectionForce;
            ctx.DensityAmount = densityAmount;
            ctx.ForceRadius = forceRadius;
            ctx.ForceStrength = forceStrength;

            ctx.FluidCompute = fluidCompute;
            ctx.StampCompute = stampCompute;
            ctx.StampParticlesCompute = stampParticlesCompute;
            ctx.BatchedStampCompute = batchedStampCompute;
            ctx.BatchedMaskCompute = batchedMaskCompute;
            ctx.BatchedInjectionCompute = batchedInjectionCompute;
            ctx.ParticleToColorCompute = particleToColorCompute;
            ctx.ParticleChannelSplatCompute = particleChannelSplatCompute;
            ctx.InkInteractionsCompute = inkInteractionsCompute;

            ctx.UseParticleSimulation = useParticleSimulation;
            ctx.UseParticleAdvection = useParticleAdvection;
            ctx.UseParticleDissipation = useParticleDissipation;
            ctx.UseParticleDiffusion = useParticleDiffusion;
            ctx.MaxParticleSimResolution = maxParticleSimResolution;
            ctx.UseParticleRenderPass = useParticleRenderPass;

            ctx.UseBatchedDensityInjection = useBatchedDensityInjection;
            ctx.UseBatchedStamping = useBatchedStamping;
            ctx.UseBatchedMasks = useBatchedMasks;

            ctx.UseInkInteractions = useInkInteractions;
            ctx.InkInteractionsDebugMode = inkInteractionsDebugMode;
            ctx.AffinityGroups = affinityGroups;
            ctx.InkDefinitions = inkDefinitions;

            ctx.EnableBlackBodyClearingFallback = enableBlackBodyClearingFallback;
            ctx.BlackBodyThresholdFallback = blackBodyThresholdFallback;
            ctx.BlackBodyClearingRateFallback = blackBodyClearingRateFallback;

            ctx.DensityStampShader = densityStampShader;

            ctx.DisplayVelocity = displayVelocity;
            ctx.UseParticleDisplay = useParticleDisplay;
            ctx.UseCpuCreatureComposite = useCpuCreatureComposite;
            ctx.UseGradientRendering = useGradientRendering;
            ctx.DisplayRenderer = displayRenderer;
            ctx.GradientPreset = gradientPreset;
            ctx.GradientMaterial = gradientMaterial;

            ctx.DebugZeroPressure = debugZeroPressure;
            ctx.DebugZeroVelocity = debugZeroVelocity;
            ctx.DebugSkipAir = debugSkipAir;
            ctx.MeasurePerformance = measurePerformance;
        }

        private void Update()
        {
            if (measurePerformance) stopwatch.Restart();

            SyncContextFromFields();
            UpdateHotkeys();

            // Debug: inject a circular force at center every frame to test pipeline.
            // The force vector (0.1, 0.05) in UV-space → ~100px at 1024 res → clearly visible flow.
            // Suppressed under ExternalStepControl so automated scenarios stay uncontaminated.
            if (!ExternalStepControl && debugInjectTestForce && ctx.FluidCompute != null)
            {
                InjectForce(new Vector2(0.5f, 0.5f), new Vector2(0.1f, 0.05f));
                InjectDensity(new Vector2(0.5f, 0.5f), Color.red, 0);
            }

            // Simulation update rate throttling
            simRanThisFrame = false;
            if (!ExternalStepControl)
            {
                simAccumulator += Time.deltaTime;
                float targetStep = simulationUpdateRate > 0 ? 1f / simulationUpdateRate : 0f;
                if (targetStep <= 0f || simAccumulator >= targetStep)
                {
                    // ctx.FrameDeltaTime was set in SyncContextFromFields to the clamped real frame
                    // dt (or 1/rate). Substep it so per-step advection stays stable at low framerates.
                    SimulateFrameSubstepped(ctx.FrameDeltaTime);
                    simRanThisFrame = true;
                    if (targetStep > 0f)
                        simAccumulator = Mathf.Max(0f, simAccumulator - targetStep);
                }
            }

            if (measurePerformance)
                lastFrameMs = (float)stopwatch.Elapsed.TotalMilliseconds;
        }

        private void LateUpdate()
        {
            if (simRanThisFrame || simulationUpdateRate <= 0)
                display.UpdateDisplay();
        }

        private void SimulateFrame()
        {
            operationQueue.DebugLogForces = debugLogForces;
            operationQueue.ProcessPending();

            var sw = measurePerformance ? stopwatch : null;
            fluidSolver.Step(sw);
        }

        /// <summary>
        /// Advances the sim by <paramref name="frameDt"/> real seconds, split into N ≤ maxSubsteps equal
        /// substeps of ≤ maxSubstepDt each so per-step advection stays low-Courant at low framerates.
        /// Queued injections are applied once at frame start; the dt-normalized decays compose exactly
        /// (pow(r, frameDt/N)^N == pow(r, frameDt)).
        /// </summary>
        private void SimulateFrameSubstepped(float frameDt)
        {
            operationQueue.DebugLogForces = debugLogForces;
            operationQueue.ProcessPending();

            int n = 1;
            if (maxSubstepDt > 0f && frameDt > maxSubstepDt)
                n = Mathf.CeilToInt(frameDt / maxSubstepDt);
            n = Mathf.Clamp(n, 1, Mathf.Max(1, maxSubsteps));

            float subDt = frameDt / n;
            var sw = measurePerformance ? stopwatch : null;
            for (int i = 0; i < n; i++)
            {
                ctx.FrameDeltaTime = subDt;
                fluidSolver.Step(sw);
            }
        }

        // ── Deterministic external control (automated scenario / dataset harness) ──
        /// <summary>
        /// When true, Update() does not auto-step the simulation. Call <see cref="StepSimulation"/>
        /// to advance deterministically. Used by the scenario runner / training-data capture.
        /// </summary>
        public bool ExternalStepControl { get; set; }

        /// <summary>Advances the simulation exactly one fixed-timestep frame (drains the op queue, runs the solver).</summary>
        public void StepSimulation()
        {
            if (ctx == null || ctx.FluidCompute == null) return;
            SyncContextFromFields();
            SimulateFrame();
        }

        /// <summary>
        /// Advances one step but with an explicit real-time delta (<paramref name="frameDtOverride"/>)
        /// for the dt-normalized transport/decay paths, while keeping the fixed solver Timestep.
        /// Lets callers emulate variable framerates (validation) and is the building block for dt
        /// clamping/substepping. A non-positive override falls back to the fixed-timestep step.
        /// </summary>
        public void StepSimulation(float frameDtOverride)
        {
            if (ctx == null || ctx.FluidCompute == null) return;
            SyncContextFromFields();
            if (frameDtOverride > 0f) ctx.FrameDeltaTime = frameDtOverride;
            SimulateFrame();
        }

        /// <summary>Clears all simulation state (density, velocity, pressure, particles) to zero.</summary>
        public void ResetSimulation()
        {
            fluidSolver?.ClearAll();
        }

        /// <summary>Recomposites the display RT from the current sim state (call after manual stepping before capture).</summary>
        public void RefreshDisplay()
        {
            display?.UpdateDisplay();
        }

        /// <summary>Sets a global solver tunable by name (for parameter sweeps / scenario runs). Applied on next sync.</summary>
        public void SetTunable(string key, float value)
        {
            switch (key)
            {
                case "viscosity": viscosity = value; break;
                case "vorticity": vorticity = value; break;
                case "dissipation": dissipation = value; break;
                case "velocityDissipation": velocityDissipation = value; break;
                case "timestep": timestep = value; break;
                default: Debug.LogWarning("[SimDriver] Unknown tunable: " + key); break;
            }
        }

        /// <summary>Toggle the display between ink (gradient) and the raw velocity field. For scenario capture.</summary>
        public void SetDisplayVelocity(bool value) => displayVelocity = value;

        // ── ISimulationWriter ───────────────────────────────────────────────

        public void InjectForce(Vector2 position, Vector2 force)
        {
            if (ctx.FluidCompute == null) return;
            operationQueue.EnqueueForceInjection(position, force);
        }

        void ISimulationWriter.InjectDensity(Vector2 position, Color color, int inkTypeIndex)
        {
            InjectDensity(position, color, inkTypeIndex);
        }

        public void InjectDensity(Vector2 position, Color color, int inkTypeIndex = 0)
        {
            if (ctx.FluidCompute == null || ctx.Density == null) return;

            float colorIntensity = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            if (colorIntensity <= 0f || ctx.DensityAmount <= 0f) return;

            int validIndex = Mathf.Clamp(inkTypeIndex, 0, 9);
            operationQueue.EnqueueDensityInjection(position, color, validIndex);
        }

        public void StampDensity(Vector2 uvPosition, Texture2D stamp, float densityMultiplier, bool useColorOverride, Color overrideColor)
        {
            if (ctx.Density == null || stamp == null) return;
            operationQueue.EnqueueDensityStamp(uvPosition, stamp, densityMultiplier, useColorOverride, overrideColor);
        }

        public void ClearDensityWithMask(Vector2 uvPosition, Texture2D mask, float blackLuminanceThreshold = 0.2f)
        {
            if (ctx.Density == null || mask == null) return;
            operationQueue.EnqueueClearDensityMask(uvPosition, mask, blackLuminanceThreshold);
        }

        /// <summary>
        /// Stamps obstacles and clears particles at obstacle positions.
        /// Kept inline (CPU-heavy, doesn't fit cleanly in a module).
        /// </summary>
        public void StampObstacles(Vector2 uvPosition, Texture2D stamp)
        {
            if (ctx.Obstacles == null || stamp == null || ctx.ParticlesBuffer == null) return;

            int stampWidth = stamp.width;
            int stampHeight = stamp.height;
            Color[] stampPixels = stamp.GetPixels();

            int centerX = Mathf.RoundToInt(uvPosition.x * resolution);
            int centerY = Mathf.RoundToInt(uvPosition.y * resolution);
            int startX = centerX - stampWidth / 2;
            int startY = centerY - stampHeight / 2;

            RenderTexture.active = ctx.Obstacles;
            Texture2D tempObstacles = new Texture2D(resolution, resolution, TextureFormat.RFloat, false);
            tempObstacles.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            tempObstacles.Apply();
            Color[] obstaclePixels = tempObstacles.GetPixels();
            RenderTexture.active = null;

            int particleCount = resolution * resolution;
            bool particlesModified = false;

            if (ctx.GpuPromotesHalf)
            {
                var particles = new SimulationDisplay_iparticle_gpu[particleCount];
                ctx.ParticlesBuffer[ctx.ParticleReadIndex].GetData(particles);
                for (int y = 0; y < stampHeight; y++)
                    for (int x = 0; x < stampWidth; x++)
                    {
                        int targetX = startX + x;
                        int targetY = startY + y;
                        if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution) continue;
                        Color sc = stampPixels[y * stampWidth + x];
                        if (sc.a < 0.01f) continue;
                        int idx = targetY * resolution + targetX;
                        obstaclePixels[idx] = new Color(1f, 0, 0, 0);
                        particles[idx] = new SimulationDisplay_iparticle_gpu();
                        particlesModified = true;
                    }
                if (particlesModified)
                    ctx.ParticlesBuffer[ctx.ParticleWriteIndex].SetData(particles);
            }
            else
            {
                var particles = new iparticle[particleCount];
                ctx.ParticlesBuffer[ctx.ParticleReadIndex].GetData(particles);
                for (int y = 0; y < stampHeight; y++)
                    for (int x = 0; x < stampWidth; x++)
                    {
                        int targetX = startX + x;
                        int targetY = startY + y;
                        if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution) continue;
                        Color sc = stampPixels[y * stampWidth + x];
                        if (sc.a < 0.01f) continue;
                        int idx = targetY * resolution + targetX;
                        obstaclePixels[idx] = new Color(1f, 0, 0, 0);
                        particles[idx] = new iparticle();
                        particlesModified = true;
                    }
                if (particlesModified)
                    ctx.ParticlesBuffer[ctx.ParticleWriteIndex].SetData(particles);
            }

            tempObstacles.SetPixels(obstaclePixels);
            tempObstacles.Apply();
            Graphics.Blit(tempObstacles, ctx.Obstacles);
            Destroy(tempObstacles);
        }

        /// <summary>
        /// Stamps creature ink texture (non-persistent, composited for display only).
        /// Kept inline (CPU-heavy, doesn't fit cleanly in a module).
        /// </summary>
        public void StampTexture(Vector2 uvPosition, Texture2D stamp, float scale = 1.0f)
        {
            if (ctx.CreatureInkBuffer == null || stamp == null) return;

            int stampWidth = stamp.width;
            int stampHeight = stamp.height;
            Color[] stampPixels = stamp.GetPixels();

            int centerX = Mathf.RoundToInt(uvPosition.x * resolution);
            int centerY = Mathf.RoundToInt(uvPosition.y * resolution);
            int startX = centerX - stampWidth / 2;
            int startY = centerY - stampHeight / 2;

            Texture2D tempTex = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
            Color[] tempPixels = new Color[resolution * resolution];

            RenderTexture.active = ctx.CreatureInkBuffer;
            Texture2D currentCreature = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
            currentCreature.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            currentCreature.Apply();
            Color[] currentPixels = currentCreature.GetPixels();
            RenderTexture.active = null;

            System.Array.Copy(currentPixels, tempPixels, currentPixels.Length);

            for (int y = 0; y < stampHeight; y++)
            {
                for (int x = 0; x < stampWidth; x++)
                {
                    int targetX = startX + x;
                    int targetY = startY + y;
                    if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution) continue;

                    Color stampColor = stampPixels[y * stampWidth + x];
                    if (stampColor.a < 0.01f) continue;

                    int targetIdx = targetY * resolution + targetX;
                    tempPixels[targetIdx] += stampColor;
                }
            }

            tempTex.SetPixels(tempPixels);
            tempTex.Apply();
            Graphics.Blit(tempTex, ctx.CreatureInkBuffer);
            Destroy(tempTex);
            Destroy(currentCreature);
        }

        // ── ISimulationReader ───────────────────────────────────────────────

        public RenderTexture GetDensityTexture()
        {
            if (ctx == null) return null;
            if (ctx.UseParticleRenderPass && ctx.DisplayRT != null) return ctx.DisplayRT;
            if (ctx.Density != null && !ctx.UseParticleDisplay) return ctx.Density.Read;
            return null;
        }

        public RenderTexture GetVelocityTexture() => ctx?.Velocity?.Read;
        public RenderTexture GetDisplayTexture() => ctx?.DisplayRT;
        public RenderTexture GetObstacleTexture() => ctx?.Obstacles;

        public ComputeBuffer GetParticleBuffer()
        {
            if (ctx?.ParticlesBuffer == null || ctx.ParticlesBuffer.Length == 0) return null;
            int readIndex = Mathf.Clamp(ctx.ParticleReadIndex, 0, ctx.ParticlesBuffer.Length - 1);
            return ctx.ParticlesBuffer[readIndex];
        }

        public float GetLastFrameMs() => lastFrameMs;
        public (float advection, float diffusion, float pressure, float projection, float vorticity) GetDetailedTimings()
        {
            if (fluidSolver == null) return (0f, 0f, 0f, 0f, 0f);
            return (fluidSolver.AdvectionMs, fluidSolver.DiffusionMs,
                fluidSolver.PressureMs, fluidSolver.ProjectionMs, fluidSolver.VorticityMs);
        }

        // ── Service accessors ───────────────────────────────────────────────

        public ISimulationService AsService() => this;
        public ISimulationReader AsReader() => this;
        public ISimulationWriter AsWriter() => this;

        // ── Hotkeys ─────────────────────────────────────────────────────────

        private void UpdateHotkeys()
        {
            int hotkey = GetInkHotkeyIndex();
            if (hotkey >= 0)
            {
                currentInkType = hotkey;
                Debug.Log($"[SimDriver] Switched to ink type: {currentInkType}");
            }

            // V key: toggle velocity display (shows raw velocity texture, bypasses gradient)
            if (Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame)
            {
                displayVelocity = !displayVelocity;
                Debug.Log($"[SimDriver] displayVelocity = {displayVelocity}");
            }
        }

        private static int GetInkHotkeyIndex()
        {
            if (Keyboard.current != null)
            {
                if (Keyboard.current.digit1Key.wasPressedThisFrame || Keyboard.current.numpad1Key.wasPressedThisFrame) return 0;
                if (Keyboard.current.digit2Key.wasPressedThisFrame || Keyboard.current.numpad2Key.wasPressedThisFrame) return 1;
                if (Keyboard.current.digit3Key.wasPressedThisFrame || Keyboard.current.numpad3Key.wasPressedThisFrame) return 2;
                if (Keyboard.current.digit4Key.wasPressedThisFrame || Keyboard.current.numpad4Key.wasPressedThisFrame) return 3;
                if (Keyboard.current.digit5Key.wasPressedThisFrame || Keyboard.current.numpad5Key.wasPressedThisFrame) return 4;
                if (Keyboard.current.digit6Key.wasPressedThisFrame || Keyboard.current.numpad6Key.wasPressedThisFrame) return 5;
                if (Keyboard.current.digit7Key.wasPressedThisFrame || Keyboard.current.numpad7Key.wasPressedThisFrame) return 6;
                if (Keyboard.current.digit8Key.wasPressedThisFrame || Keyboard.current.numpad8Key.wasPressedThisFrame) return 7;
                if (Keyboard.current.digit9Key.wasPressedThisFrame || Keyboard.current.numpad9Key.wasPressedThisFrame) return 8;
                if (Keyboard.current.digit0Key.wasPressedThisFrame || Keyboard.current.numpad0Key.wasPressedThisFrame) return 9;
            }

            #if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1)) return 0;
            if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) return 1;
            if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) return 2;
            if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4)) return 3;
            if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5)) return 4;
            if (Input.GetKeyDown(KeyCode.Alpha6) || Input.GetKeyDown(KeyCode.Keypad6)) return 5;
            if (Input.GetKeyDown(KeyCode.Alpha7) || Input.GetKeyDown(KeyCode.Keypad7)) return 6;
            if (Input.GetKeyDown(KeyCode.Alpha8) || Input.GetKeyDown(KeyCode.Keypad8)) return 7;
            if (Input.GetKeyDown(KeyCode.Alpha9) || Input.GetKeyDown(KeyCode.Keypad9)) return 8;
            if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0)) return 9;
            #endif

            return -1;
        }

        // ── Cleanup ─────────────────────────────────────────────────────────

        private void OnDestroy()
        {
            resources?.Dispose(ctx);
        }

        private void OnGUI()
        {
            display?.DrawOnGUI(lastFrameMs, fluidSolver.AdvectionMs, fluidSolver.DiffusionMs,
                fluidSolver.PressureMs, fluidSolver.ProjectionMs, fluidSolver.VorticityMs);
        }
    }
}
