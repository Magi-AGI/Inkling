using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using Magi.UnityTools.Core;
using Magi.InkTools;
using Magi.InkTools.Simulation;
using Magi.UnityTools.Patterns;
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
        // ── Compute Shaders ─────────────────────────────────────────────────
        [Header("Compute Shader")]
        [SerializeField] public ComputeShader fluidCompute;

        [Header("Simulation Parameters")]
        [SerializeField] private int resolution = 256;
        [SerializeField] private float viscosity = 0.0001f;
        [SerializeField] private float vorticity = 5.0f;
        [SerializeField] private float dissipation = 0.999f;
        [SerializeField] private float velocityDissipation = 0.99f;
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
        [SerializeField] private int pressureIterations = 40;
        [SerializeField] private int diffusionIterations = 0;
        [SerializeField] private bool useRedBlackSolver = false;

        [Header("Injection")]
        [SerializeField] private float injectionForce = 100f;
        [SerializeField] private float densityAmount = 10.0f;
        [SerializeField] private float forceRadius = 40f;
        [SerializeField] private float forceStrength = 50f;
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

        private void Start()
        {
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

                // Initial density seeds
                InjectDensity(new Vector2(0.5f, 0.5f), Color.white, 0);
                InjectDensity(new Vector2(0.3f, 0.7f), new Color(1f, 0.5f, 0f, 1f), 0);
                InjectDensity(new Vector2(0.7f, 0.3f), new Color(0f, 0.5f, 1f, 1f), 0);
            }

            // Register with ServiceLocator
            var locator = ServiceLocator.Instance;
            if (locator != null)
                locator.RegisterService(this);

            Debug.Log($"[SimDriver] Initialized {resolution}x{resolution} simulation");
        }

        /// <summary>
        /// Copies all [SerializeField] values into the shared SimulationContext.
        /// Called once in Start() and at the top of each frame (to pick up Inspector changes).
        /// </summary>
        private void SyncContextFromFields()
        {
            ctx.Resolution = resolution;
            ctx.Timestep = timestep;
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

            // Simulation update rate throttling
            simRanThisFrame = false;
            simAccumulator += Time.deltaTime;
            float targetStep = simulationUpdateRate > 0 ? 1f / simulationUpdateRate : 0f;
            if (targetStep <= 0f || simAccumulator >= targetStep)
            {
                SimulateFrame();
                simRanThisFrame = true;
                if (targetStep > 0f)
                    simAccumulator = Mathf.Max(0f, simAccumulator - targetStep);
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
            operationQueue.ProcessPending();
            var sw = measurePerformance ? stopwatch : null;
            fluidSolver.Step(sw);
        }

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
