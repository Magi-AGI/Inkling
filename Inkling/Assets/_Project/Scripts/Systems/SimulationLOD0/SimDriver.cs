using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;
using Unity.Mathematics;
using Debug = UnityEngine.Debug;
using Magi.UnityTools.Core;
using Magi.InkTools.Simulation;
using Magi.Inkling.Services;
using Magi.Inkling.Services.Core;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Drives the fluid simulation compute shader with ping-pong buffers and proper kernel dispatch order.
    /// Manages RT allocation, kernel execution, and display output.
    /// </summary>
    [DefaultExecutionOrder(50)] // Run after TexturedInjector (-50) so queued stamps are drained in SimulateFrame
    public class SimDriver : MonoBehaviour, ISimulationService
    {
        [Header("Compute Shader")]
        [SerializeField] public ComputeShader fluidCompute;

        [Header("Simulation Parameters")]
        [SerializeField] private int resolution = 256;
        [SerializeField] private float viscosity = 0.0001f;  // Lower viscosity for more fluid movement
        [SerializeField] private float vorticity = 5.0f;  // Higher vorticity for more swirls
        [SerializeField] private float dissipation = 0.999f;  // Normal fade for regular inks (creatures use separate buffer)
        [SerializeField] private float velocityDissipation = 0.99f;  // Keep velocity longer
        [SerializeField] private float timestep = 0.016f;

        // Public properties for metadata export
        public float Viscosity => viscosity;
        public float Vorticity => vorticity;
        public float Dissipation => dissipation;
        public float VelocityDissipation => velocityDissipation;
        public float Timestep => timestep;
        public int Resolution => resolution;

        [Header("Solver Settings")]
        [SerializeField] private int pressureIterations = 40;  // Increased for better convergence
        [SerializeField] private int diffusionIterations = 0;   // Disable diffusion for now (it slows things down)
        [SerializeField] private bool useRedBlackSolver = false; // Red-Black Gauss-Seidel (faster convergence). Falls back to Jacobi if PressureRedBlack kernel is missing.

        [Header("Injection")]
        [SerializeField] private bool autoInject = false;  // Disable auto-inject by default
        [SerializeField] private float injectionForce = 100f;  // Direct velocity multiplier
        [SerializeField] private float densityAmount = 10.0f;    // Scalar density factor for mouse/texture injection
        [SerializeField] private float forceRadius = 40f;       // Larger injection area
        [SerializeField] private float forceStrength = 50f;     // Force gets multiplied by velocity magnitude
        [SerializeField] private bool useBatchedDensityInjection = true; // Use batched compute pass for point injections
        [SerializeField] private ComputeShader batchedInjectionCompute;  // Optional: AddDensityBatched / AddParticlesBatched
        [SerializeField] private bool useBatchedStamping = true; // Use batched compute pass for density stamps when textures match
        [SerializeField] private bool useBatchedMasks = true;    // Use batched compute pass for clear-density masks when textures match

        [Header("Ink Type Selection")]
        [SerializeField] private InkType currentInkType = InkType.Fire;

        /// <summary>
        /// Ink types that map to gradient channels in the shader.
        /// Use number keys 1-8 to switch ink types during runtime.
        /// </summary>
        public enum InkType
        {
            Fire = 0,       // R channel - Press 1
            Water = 1,      // G channel - Press 2
            Metal = 2,      // B channel - Press 3
            Electricity = 3,
            Ice = 4,
            Plant = 5,
            Steam = 6,
            Dust = 7,
            Test = 8
        }

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
        [Tooltip("Resolution for gradient and display render targets. 0 = auto (match screen height). " +
            "Decouples display from simulation: the gradient shader runs at this resolution, " +
            "sampling sim-resolution channel textures with hardware minification.")]
        [SerializeField] private int displayResolution = 0;
        [Tooltip("When displayResolution is 0, scale the simulation resolution by this factor to derive display resolution.")]
        [Range(0.1f, 1f)]
        [SerializeField] private float displayResolutionScale = 1f;

        [Header("Multi-Resolution Overrides")]
        [Tooltip("Override resolution for velocity RT (0 = match sim). Allows lower-res velocity for perf.")]
        [Range(0, 4096)]
        [SerializeField] private int velocityResolutionOverride = 0;
        [Tooltip("Override resolution for obstacle RT (0 = match sim). Allows lower-res obstacles for perf.")]
        [Range(0, 4096)]
        [SerializeField] private int obstacleResolutionOverride = 0;
        [Tooltip("Override resolution for particle buffers (0 = match sim). Lowering cuts particle count but reduces fidelity.")]
        [Range(0, 4096)]
        [SerializeField] private int particleResolutionOverride = 0;

        [Header("Creature / Stamp Rendering")]
        [SerializeField] private Shader densityStampShader;
        [Tooltip("Compute-shader stamp (preferred over Blit shader). Eliminates DX12 cross-queue barriers on density ping-pong buffers. If unassigned, stamps fall back to Graphics.Blit via densityStampShader.")]
        [SerializeField] private ComputeShader stampCompute;
        [Tooltip("Compute-shader stamp for particle buffer. If unassigned, particle stamps fall back to CPU path.")]
        [SerializeField] private ComputeShader stampParticlesCompute;
        [Tooltip("Optional compute to batch stamp payloads (density + particles) into a staging buffer to reduce dispatch count.")]
        [SerializeField] private ComputeShader batchedStampCompute;
        [Tooltip("Optional compute to batch clear-density masks.")]
        [SerializeField] private ComputeShader batchedMaskCompute;

        [Header("Particle Simulation")]
        [SerializeField] private bool useParticleSimulation = true;
        [Tooltip("When enabled (with useParticleSimulation), runs AdvectParticles each frame.")]
        [SerializeField] private bool useParticleAdvection = true;
        [Tooltip("When enabled (with useParticleSimulation), runs DissipateParticles each frame.")]
        [SerializeField] private bool useParticleDissipation = true;
        [Tooltip("When enabled (with useParticleSimulation), runs DiffuseParticles each frame for per-ink viscosity/spreading.")]
        [SerializeField] private bool useParticleDiffusion = true;
        [Tooltip("Safety cap: particle kernels are skipped when resolution exceeds this value.")]
        [SerializeField] private int maxParticleSimResolution = 512;

        [Header("Ink Interactions")]
        [Tooltip("Compute shader for cellular automata ink reactions.")]
        [SerializeField] private ComputeShader inkInteractionsCompute;
        [Tooltip("When enabled (with useParticleSimulation), runs ink interactions each frame.")]
        [SerializeField] private bool useInkInteractions = true;
        [Tooltip("Debug mode: bypasses matrix math with simple hardcoded plant→fire conversion.")]
        [SerializeField] private bool inkInteractionsDebugMode = false;
        [Tooltip("Affinity groups defining which inks interact and how. Each group processes 4 inks.")]
        [SerializeField] private AffinityGroup[] affinityGroups;

        [Header("Black Body Ink (Fallback)")]
        [Tooltip("Fallback: Enable black body clearing if no InkTypeDef has enableClearing set.")]
        [SerializeField] private bool enableBlackBodyClearingFallback = true;
        [Tooltip("Fallback: Black body threshold if not set in InkTypeDef.")]
        [Range(0f, 1f)]
        [SerializeField] private float blackBodyThresholdFallback = 0.5f;
        [Tooltip("Fallback: Clearing rate if not set in InkTypeDef.")]
        [Range(0f, 0.2f)]
        [SerializeField] private float blackBodyClearingRateFallback = 0.05f;

        [Header("Ink Properties")]
        [Tooltip("Ink type definitions with per-ink properties. Index must match InkTypeId enum.")]
        [SerializeField] private InkTypeDef[] inkDefinitions = new InkTypeDef[10];

        // Render textures (using PingPongRenderTexture from MagiUnityTools)
        private PingPongRenderTexture velocity;
        private PingPongRenderTexture pressure;
        private PingPongRenderTexture density;  // Used by some kernels (Clear, AddDensity) even though particles are primary
        private RenderTexture divergence;
        private RenderTexture vorticityTex;
        private RenderTexture obstacles;
        private RenderTexture displayRT;
        private RenderTexture gradientRT;  // For gradient-rendered output
        private int effectiveDisplayRes;   // Resolved from displayResolution (0 = auto)
        private RenderTexture creatureInkBuffer;  // Separate buffer for creature stamps (cleared each frame)

        // Channel textures for particle-authoritative gradient rendering.
        // ParticleChannelSplat.compute writes these; InkGradientRenderer reads them.
        private RenderTexture channelRT0;  // fire, water, plantSeeded, plantGrown
        private RenderTexture channelRT1;  // steam, glitter, blackBody, ice
        private RenderTexture channelRT2;  // electricitySeeded, electricityGrown, 0, 0
        // Mipped copies (non-UAV) for safe minification sampling in the gradient shader.
        private RenderTexture channelRT0Mipped;
        private RenderTexture channelRT1Mipped;
        private RenderTexture channelRT2Mipped;
        // Downsampled copies at display resolution for 1:1 sampling in the gradient shader.
        private RenderTexture channelRT0Down;
        private RenderTexture channelRT1Down;
        private RenderTexture channelRT2Down;

        // Particle-based density buffer (replaces RGBA texture with multi-channel iparticle)
        private ComputeBuffer[] particlesBuffer;  // Ping-pong buffer for iparticle structs
        private int particleReadIndex = 0;
        private int particleWriteIndex = 1;

        // GPU stride management.  DX11's FXC compiler promotes half→float in
        // StructuredBuffers, doubling the expected stride from 28 to 56.  We
        // detect this at runtime and create the buffer at the GPU-expected stride.
        // CPU↔GPU transfers marshal through iparticle_gpu (float fields) when
        // promoted, preserving the half-precision domain model (iparticle).
        private bool gpuPromotesHalf;
        private int gpuParticleStride;

        /// <summary>
        /// Float-field mirror of iparticle for GPU buffer marshaling on platforms
        /// where half is promoted to float in StructuredBuffers (DX11/FXC).
        /// Field order must exactly match iparticle.cs.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct iparticle_gpu
        {
            public float fire, water, plantSeeded, plantGrown;
            public float steam, glitter, blackBody;
            public float electricitySeeded, electricityGrown;
            public float ice;
            public float red, green, blue, alpha;
        }

        // Materials
        private Material densityStampMaterial;
        [Header("Particle Rendering")]
        [Tooltip("Compute shader that converts iparticle buffer to ARGB display texture. If unassigned, useParticleRenderPass has no effect and display falls through to density RT or CPU particle conversion.")]
        [SerializeField] private ComputeShader particleToColorCompute;
        [Tooltip("Compute shader that splats iparticle channels into 3 RGBA render textures for gradient rendering. Required for particle-authoritative gradient path (avoids half/float stride mismatch in fragment shaders).")]
        [SerializeField] private ComputeShader particleChannelSplatCompute;
        [SerializeField] private bool useParticleRenderPass = false;
        [Header("Update Rate")]
        [Tooltip("If >0, run the simulation at this target Hz (skipping frames if needed). 0 = every frame.")]
        [SerializeField] private int simulationUpdateRate = 0;

        // Kernel indices
        private int kernelAdvection;
        private int kernelDiffusion;
        private int kernelDivergence;
        private int kernelPressure;
        private int kernelPressureRedBlack;
        private int kernelSubtractGradient;
        private int kernelVorticity;
        private int kernelVorticityConfinement;
        private int kernelAddForce;
        private int kernelAddDensity;
        private int kernelAddDensityBatched;
        private int kernelClear;
        private int kernelUpdateObstacles;
        private int kernelApplyObstacleBoundary;

        // Particle-based kernels
        private int kernelAdvectParticles;
        private int kernelDissipateParticles;
        private int kernelDiffuseParticles;
        private int kernelAddParticlesGaussian;
        private int kernelAddParticlesBatched;

        // Ink interactions kernel (from InkInteractions.compute)
        private int kernelInkInteractions;
        private bool inkInteractionsReady;
        private bool batchedInjectionReady;

        // Stamp compute kernels (from StampDensityCompute.compute)
        private int kernelStampDensity;
        private int kernelClearBlackDensity;
        private bool stampComputeReady;

        // Stamp particles compute kernel (from StampParticlesCompute.compute)
        private int kernelStampParticles;
        private bool stampParticlesComputeReady;

        // Batched stamp kernels (optional)
        private int kernelStampDensityBatched;
        private bool batchedStampReady;
        private int kernelClearMaskBatched;
        private bool batchedMaskReady;
        private bool loggedStampBatchMixedTextures;
        private bool loggedStampBatchUnavailable;
        private bool loggedMaskBatchUnavailable;

        // Channel splat compute kernel (from ParticleChannelSplat.compute)
        private int kernelChannelSplat;
        private bool channelSplatReady;
        private bool loggedDisplayDiagnostic;
        private bool hasLoggedFirstParticleStamp;
        private float simAccumulator;
        private bool simRanThisFrame;

        // Performance tracking
        private Stopwatch stopwatch = new Stopwatch();
        private float lastFrameMs;
        private float advectionMs, diffusionMs, pressureMs, projectionMs, vorticityMs;

        // ── Pending GPU operations ──────────────────────────────────────────
        // External callers (TexturedInjector, etc.) queue operations here.
        // ProcessPendingOperations() drains them at the top of SimulateFrame
        // so that ALL GPU work (stamps + advection + pressure) executes inside
        // a single Update(), eliminating DX12 cross-queue synchronisation
        // issues and keeping ping-pong Swap counts deterministic.

        private struct PendingDensityStamp
        {
            public Vector2 uvPosition;
            public Texture2D stamp;
            public float multiplier;
            public bool useColorOverride;
            public Color overrideColor;
        }

        private struct PendingForceInjection
        {
            public Vector2 position;
            public Vector2 force;
        }

        private struct PendingDensityInjection
        {
            public Vector2 position;
            public Color color;
            public int inkTypeIndex;  // Maps to InkTypeId for particle injection routing
        }

        private struct PendingClearDensityMask
        {
            public Vector2 uvPosition;
            public Texture2D mask;
            public float blackLuminanceThreshold;
        }

        private struct BatchedInjection
        {
            public Vector2 uv;
            public Vector3 color;
            public int inkIndex;
        }

        private readonly List<PendingDensityStamp> pendingDensityStamps = new List<PendingDensityStamp>();
        private readonly List<PendingForceInjection> pendingForceInjections = new List<PendingForceInjection>();
        private readonly List<PendingDensityInjection> pendingDensityInjections = new List<PendingDensityInjection>();
        private readonly List<PendingClearDensityMask> pendingClearDensityMasks = new List<PendingClearDensityMask>();

        private void Start()
        {
            var init = InitializeSimulation();
            if (!init.IsSuccess)
            {
                Debug.LogError($"[SimDriver] Initialization failed: {init}");
                Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal($"SimDriver init failed: {init}");
                enabled = false;
                return;
            }

            // Register with ServiceLocator if present
            var locator = ServiceLocator.Instance;
            if (locator != null)
            {
                locator.RegisterService(this);
            }
        }

        private void EnsureStampMaterial()
        {
            if (densityStampMaterial != null)
            {
                return;
            }

            Shader shader = densityStampShader != null
                ? densityStampShader
                : Shader.Find("Hidden/Magi/StampDensity");

            if (shader == null)
            {
                Debug.LogWarning("[SimDriver] Could not find stamp shader 'Hidden/Magi/StampDensity'. Creature stamping will be disabled.");
                return;
            }

            densityStampMaterial = new Material(shader);
        }

        private void InitializeStampCompute()
        {
            if (stampCompute == null)
            {
                stampComputeReady = false;
            }
            else
            {
                try
                {
                    kernelStampDensity = stampCompute.FindKernel("StampDensity");
                    kernelClearBlackDensity = stampCompute.FindKernel("ClearBlackDensity");
                    stampComputeReady = true;
                    Debug.Log("[SimDriver] Stamp compute shader ready – density stamps will use compute pipeline.");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[SimDriver] Stamp compute init failed ({e.Message}). Falling back to Blit-based stamps.");
                    stampComputeReady = false;
                }
            }

            if (stampParticlesCompute == null)
            {
                stampParticlesComputeReady = false;
            }
            else
            {
                try
                {
                    kernelStampParticles = stampParticlesCompute.FindKernel("StampParticles");
                    stampParticlesComputeReady = true;
                    Debug.Log("[SimDriver] Stamp particles compute shader ready – particle stamps will use GPU pipeline.");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[SimDriver] Stamp particles compute init failed ({e.Message}). Falling back to CPU particle stamps.");
                    stampParticlesComputeReady = false;
                }
            }

            if (particleChannelSplatCompute == null)
            {
                channelSplatReady = false;
            }
            else
            {
                try
                {
                    kernelChannelSplat = particleChannelSplatCompute.FindKernel("ChannelSplat");
                    channelSplatReady = true;
                    Debug.Log("[SimDriver] Channel splat compute shader ready – gradient rendering will use particle channel textures.");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[SimDriver] Channel splat compute init failed ({e.Message}). Gradient rendering will fall back to density RT.");
                    channelSplatReady = false;
                }
            }

            // Ink interactions compute shader
            if (inkInteractionsCompute == null)
            {
                inkInteractionsReady = false;
            }
            else
            {
                try
                {
                    kernelInkInteractions = inkInteractionsCompute.FindKernel("InkInteractions");
                    inkInteractionsReady = true;
                    Debug.Log("[SimDriver] Ink interactions compute shader ready – cellular automata reactions enabled.");
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[SimDriver] Ink interactions compute init failed ({e.Message}). Ink reactions disabled.");
                    inkInteractionsReady = false;
                }
            }

            // Batched stamp compute (optional)
            if (batchedStampCompute != null)
            {
                try
                {
                    kernelStampDensityBatched = batchedStampCompute.FindKernel("StampDensityBatched");
                    kernelStampParticlesBatched = batchedStampCompute.FindKernel("StampParticlesBatched");
                    batchedStampReady = true;
                    Debug.Log("[SimDriver] Batched stamp compute ready.");
                }
                catch (System.Exception e)
                {
                    batchedStampReady = false;
                    Debug.LogWarning($"[SimDriver] Batched stamp compute init failed ({e.Message}).");
                }
            }
            else
            {
                batchedStampReady = false;
            }

            // Batched mask compute (optional)
            if (batchedMaskCompute != null)
            {
                try
                {
                    kernelClearMaskBatched = batchedMaskCompute.FindKernel("ClearMaskBatched");
                    batchedMaskReady = true;
                    Debug.Log("[SimDriver] Batched mask compute ready.");
                }
                catch (System.Exception e)
                {
                    batchedMaskReady = false;
                    Debug.LogWarning($"[SimDriver] Batched mask compute init failed ({e.Message}).");
                }
            }
            else
            {
                batchedMaskReady = false;
            }
        }

        private Result InitializeSimulation()
        {
            if (fluidCompute == null)
            {
                Debug.LogWarning("[SimDriver] No compute shader assigned. Running in test pattern mode. To enable fluid simulation, assign Fluids.compute from Packages/InkTools Simulation.");
                AllocateRenderTextures();
                return;
            }

            // Try to get kernel indices - handle gracefully if missing
            bool kernelsFound = true;
            try
            {
                kernelAdvection = fluidCompute.FindKernel("Advection");
                kernelDiffusion = fluidCompute.FindKernel("Diffusion");
                kernelDivergence = fluidCompute.FindKernel("Divergence");
                kernelPressure = fluidCompute.FindKernel("Pressure");
                kernelSubtractGradient = fluidCompute.FindKernel("SubtractGradient");
                kernelVorticity = fluidCompute.FindKernel("Vorticity");
                kernelVorticityConfinement = fluidCompute.FindKernel("VorticityConfinement");
                kernelAddForce = fluidCompute.FindKernel("AddForce");
                kernelAddDensity = fluidCompute.FindKernel("AddDensity");
                kernelClear = fluidCompute.FindKernel("Clear");

                // Try to find optional kernels
                try
                {
                    kernelPressureRedBlack = fluidCompute.FindKernel("PressureRedBlack");
                    kernelUpdateObstacles = fluidCompute.FindKernel("UpdateObstacles");
                    kernelApplyObstacleBoundary = fluidCompute.FindKernel("ApplyObstacleBoundary");

                    // Particle kernels (new)
                    kernelAdvectParticles = fluidCompute.FindKernel("AdvectParticles");
                    kernelDissipateParticles = fluidCompute.FindKernel("DissipateParticles");
                    kernelDiffuseParticles = fluidCompute.FindKernel("DiffuseParticles");
                    kernelAddParticlesGaussian = fluidCompute.FindKernel("AddParticlesGaussian");
                }
                catch
                {
                    // Optional kernels - not critical if missing
                    useRedBlackSolver = false;
                }
            }
            catch (System.Exception)
            {
                Debug.LogWarning("[SimDriver] Compute shader doesn't have required kernels. Running in test pattern mode. Make sure you've assigned the correct Fluids.compute.");
                kernelsFound = false;
                fluidCompute = null; // Disable compute operations
            }

            // Optional batched injection compute
            if (batchedInjectionCompute != null)
            {
                try
                {
                    kernelAddDensityBatched = batchedInjectionCompute.FindKernel("AddDensityBatched");
                    kernelAddParticlesBatched = batchedInjectionCompute.FindKernel("AddParticlesBatched");
                    batchedInjectionReady = true;
                    Debug.Log("[SimDriver] Batched injection compute ready (density + particles).");
                }
                catch (System.Exception e)
                {
                    batchedInjectionReady = false;
                    Debug.LogWarning($"[SimDriver] Batched injection compute init failed ({e.Message}). Falling back to per-injection dispatch.");
                }
            }
            else
            {
                batchedInjectionReady = false;
            }

            AllocateRenderTextures();
            InitializeStampCompute();

            if (kernelsFound && fluidCompute != null)
            {
                SetShaderConstants();
                ClearBuffers();

                // Initialize obstacles
                if (kernelUpdateObstacles != 0)
                {
                    InitializeObstacles();
                }

                // Add initial density seed so we can see something
                InjectDensity(new Vector2(0.5f, 0.5f), Color.white);
                InjectDensity(new Vector2(0.3f, 0.7f), new Color(1f, 0.5f, 0f, 1f)); // Orange
                InjectDensity(new Vector2(0.7f, 0.3f), new Color(0f, 0.5f, 1f, 1f)); // Blue
            }

            Debug.Log($"[SimDriver] Initialized {resolution}x{resolution} simulation");
        }

        private void AllocateRenderTextures()
        {
            // Use PingPongRenderTexture from MagiUnityTools for cleaner ping-pong management
            int velRes = velocityResolutionOverride > 0 ? velocityResolutionOverride : resolution;
            velocity = new PingPongRenderTexture(velRes, velRes, RenderTextureFormat.RGHalf, "Velocity");
            pressure = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.RHalf, "Pressure");
            density = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.ARGBHalf, "Density");
            divergence = CreateRT(RenderTextureFormat.RHalf, "Divergence");
            vorticityTex = CreateRT(RenderTextureFormat.RHalf, "Vorticity");
            int obsRes = obstacleResolutionOverride > 0 ? obstacleResolutionOverride : resolution;
            obstacles = CreateRT(RenderTextureFormat.RFloat, "Obstacles", obsRes);

            // Creature ink buffer (cleared each frame, composited with density before simulation)
            creatureInkBuffer = CreateRT(RenderTextureFormat.ARGBHalf, "CreatureInk");

            // Particle buffer (replaces density RenderTexture)
            // Most desktop HLSL compilers promote half→float in StructuredBuffer
            // layouts, expecting stride 56 (14 fields × 4 bytes) instead of C#'s
            // stride 28 (14 fields × 2 bytes).  This includes:
            //   DX11 (FXC always promotes), DX12 (DXC promotes without
            //   -enable-16bit-types, which Unity does not enable by default),
            //   OpenGL (no native half), Vulkan desktop (SPIR-V typically promotes).
            // Native half in StructuredBuffers is only reliable on mobile GPUs
            // (Metal on Apple Silicon, GLES on Mali/Adreno with explicit support).
            int particleRes = particleResolutionOverride > 0 ? particleResolutionOverride : resolution;
            int particleCount = particleRes * particleRes;
            // Force float stride (all-float iparticle) for all platforms to avoid half/float promotion mismatch
            gpuPromotesHalf = true;
            gpuParticleStride = Marshal.SizeOf<iparticle>(); // 56 bytes

            particlesBuffer = new ComputeBuffer[2];
            for (int i = 0; i < 2; i++)
            {
                  particlesBuffer[i] = new ComputeBuffer(particleCount, gpuParticleStride, ComputeBufferType.Default);
            }

            Debug.Log($"[SimDriver] Allocated particle buffer: {particleCount} particles, " +
                $"stride {gpuParticleStride} bytes (float stride), " +
                $"API={SystemInfo.graphicsDeviceType}");

            // Channel textures for particle-authoritative gradient rendering.
            // ARGBFloat avoids packing artifacts on DX12 when compute writes
            // (UAV) are immediately sampled (SRV) by the gradient fragment shader.
            // Mipmaps enable proper hardware minification when the gradient shader
            // runs at display resolution (lower than sim resolution).
            channelRT0 = CreateChannelRT(RenderTextureFormat.ARGBFloat, "Channels0_fire_water_plant");
            channelRT1 = CreateChannelRT(RenderTextureFormat.ARGBFloat, "Channels1_steam_glitter_bb_ice");
            channelRT2 = CreateChannelRT(RenderTextureFormat.ARGBFloat, "Channels2_electricity");
            channelRT0Mipped = CreateChannelMippedRT(RenderTextureFormat.ARGBFloat, "Channels0_fire_water_plant_mipped");
            channelRT1Mipped = CreateChannelMippedRT(RenderTextureFormat.ARGBFloat, "Channels1_steam_glitter_bb_ice_mipped");
            channelRT2Mipped = CreateChannelMippedRT(RenderTextureFormat.ARGBFloat, "Channels2_electricity_mipped");

            // Decouple display resolution from sim resolution.
            // The gradient shader runs at display resolution, sampling sim-resolution
            // channel textures — hardware bilinear + mipmaps handle the minification.
            // No enableRandomWrite: autoGenerateMips works correctly, and we avoid
            // the silent mipmap failure that caused blockiness at sim resolution.
            effectiveDisplayRes = displayResolution > 0
                ? displayResolution
                : Mathf.Max(32, Mathf.RoundToInt(resolution * displayResolutionScale));

            // Downsampled SRV copies at display resolution for 1:1 sampling.
            // Only allocate when display < sim; otherwise mipped copies are sufficient.
            if (effectiveDisplayRes < resolution)
            {
                channelRT0Down = CreateChannelDownRT(RenderTextureFormat.ARGBFloat, "Channels0_fire_water_plant_down");
                channelRT1Down = CreateChannelDownRT(RenderTextureFormat.ARGBFloat, "Channels1_steam_glitter_bb_ice_down");
                channelRT2Down = CreateChannelDownRT(RenderTextureFormat.ARGBFloat, "Channels2_electricity_down");
            }

            displayRT = new RenderTexture(effectiveDisplayRes, effectiveDisplayRes, 0, RenderTextureFormat.ARGBHalf);
            displayRT.filterMode = FilterMode.Bilinear;
            displayRT.wrapMode = TextureWrapMode.Clamp;
            displayRT.name = "DisplayRT";
            displayRT.Create();

            Debug.Log($"[SimDriver] Display resolution: {effectiveDisplayRes}x{effectiveDisplayRes} " +
                $"(sim={resolution}, screen={Screen.width}x{Screen.height}, " +
                $"configured={displayResolution})");

        }

        private RenderTexture CreateRT(RenderTextureFormat format, string name, int sizeOverride = 0)
        {
            int size = sizeOverride > 0 ? sizeOverride : resolution;
            var rt = new RenderTexture(size, size, 0, format);
            rt.enableRandomWrite = true;
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = name;
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Create a channel RT at sim resolution with mipmaps for minification.
        /// enableRandomWrite is required for compute splat writes;
        /// autoGenerateMips is false (manual GenerateMips after compute dispatch).
        /// Trilinear filtering lets tex2D auto-select the right mip when the
        /// gradient shader runs at a lower display resolution.
        /// </summary>
        private RenderTexture CreateChannelRT(RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(resolution, resolution, 0, format);
            rt.enableRandomWrite = true;   // compute writes
            rt.useMipMap = false;          // mipmap generation on UAV RTs is unreliable
            rt.autoGenerateMips = false;
            rt.filterMode = FilterMode.Trilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = name;
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Create a non-UAV, mipmapped copy for gradient sampling.
        /// Mips are auto-generated by Blit from the UAV source each frame.
        /// </summary>
        private RenderTexture CreateChannelMippedRT(RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(resolution, resolution, 0, format);
            rt.enableRandomWrite = false;  // SRV only
            rt.useMipMap = true;
            rt.autoGenerateMips = true;
            rt.filterMode = FilterMode.Trilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = name;
            rt.Create();
            return rt;
        }

        /// <summary>
        /// Create a downsampled SRV texture at display resolution (no UAV, no mips).
        /// Gradient samples these at 1:1 to avoid heavy minification of the 4K channel data.
        /// </summary>
        private RenderTexture CreateChannelDownRT(RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(effectiveDisplayRes, effectiveDisplayRes, 0, format);
            rt.enableRandomWrite = false;
            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = name;
            rt.Create();
            return rt;
        }

        private void SetShaderConstants()
        {
            // Set all parameters expected by INIT_PARAMS macro
            fluidCompute.SetInt("_Resolution", resolution);
            fluidCompute.SetFloat("_DeltaTime", timestep);
            fluidCompute.SetFloat("_Viscosity", viscosity);
            fluidCompute.SetFloat("_VorticityStrength", vorticity);
            fluidCompute.SetFloat("_Dissipation", dissipation);
            fluidCompute.SetVector("_SimulationSize", new Vector2(resolution, resolution));

            // Jacobi iteration parameters for diffusion
            float dx = 1.0f / resolution;
            fluidCompute.SetFloat("_Alpha", dx * dx / (viscosity * timestep));
            fluidCompute.SetFloat("_InverseBeta", 1.0f / (4.0f + dx * dx / (viscosity * timestep)));

            // Set default force parameters (will be overridden when injecting)
            fluidCompute.SetVector("_ForcePosition", Vector2.zero);
            fluidCompute.SetVector("_ForceDirection", Vector2.zero);
            fluidCompute.SetFloat("_ForceRadius", forceRadius);
            fluidCompute.SetFloat("_ForceStrength", 0f);
            fluidCompute.SetFloat("_DensityAmount", 0f);

            // Per-ink dissipation rates (from InkTypeDef assets or defaults)
            fluidCompute.SetFloat("_DissipationFire", GetInkDissipation(InkTypeId.Fire, 0.995f));
            fluidCompute.SetFloat("_DissipationWater", GetInkDissipation(InkTypeId.Water, 0.998f));
            fluidCompute.SetFloat("_DissipationPlantSeeded", GetInkDissipation(InkTypeId.PlantSeeded, 0.997f));
            fluidCompute.SetFloat("_DissipationPlantGrown", GetInkDissipation(InkTypeId.PlantGrown, 0.997f));
            fluidCompute.SetFloat("_DissipationSteam", GetInkDissipation(InkTypeId.Steam, 0.990f));
            fluidCompute.SetFloat("_DissipationGlitter", GetInkDissipation(InkTypeId.Glitter, 0.999f));
            fluidCompute.SetFloat("_DissipationBlackBody", GetInkDissipation(InkTypeId.BlackBody, 0.5f));
            fluidCompute.SetFloat("_DissipationElectricitySeeded", GetInkDissipation(InkTypeId.ElectricitySeeded, 0.985f));
            fluidCompute.SetFloat("_DissipationElectricityGrown", GetInkDissipation(InkTypeId.ElectricityGrown, 0.985f));
            fluidCompute.SetFloat("_DissipationIce", GetInkDissipation(InkTypeId.Ice, 0.996f));

            // Per-ink viscosity (spreading/diffusion)
            fluidCompute.SetFloat("_ViscosityFire", GetInkViscosity(InkTypeId.Fire, 0.05f));
            fluidCompute.SetFloat("_ViscosityWater", GetInkViscosity(InkTypeId.Water, 0.2f));
            fluidCompute.SetFloat("_ViscosityPlantSeeded", GetInkViscosity(InkTypeId.PlantSeeded, 0.0f));
            fluidCompute.SetFloat("_ViscosityPlantGrown", GetInkViscosity(InkTypeId.PlantGrown, 0.0f));
            fluidCompute.SetFloat("_ViscositySteam", GetInkViscosity(InkTypeId.Steam, 0.15f));
            fluidCompute.SetFloat("_ViscosityGlitter", GetInkViscosity(InkTypeId.Glitter, 0.02f));
            fluidCompute.SetFloat("_ViscosityBlackBody", GetInkViscosity(InkTypeId.BlackBody, 0.1f));
            fluidCompute.SetFloat("_ViscosityElectricitySeeded", GetInkViscosity(InkTypeId.ElectricitySeeded, 0.0f));
            fluidCompute.SetFloat("_ViscosityElectricityGrown", GetInkViscosity(InkTypeId.ElectricityGrown, 0.0f));
            fluidCompute.SetFloat("_ViscosityIce", GetInkViscosity(InkTypeId.Ice, 0.0f));

            // Per-ink vorticity contribution (swirl effects)
            fluidCompute.SetFloat("_VorticityFire", GetInkVorticity(InkTypeId.Fire, 1.5f));
            fluidCompute.SetFloat("_VorticityWater", GetInkVorticity(InkTypeId.Water, 0.8f));
            fluidCompute.SetFloat("_VorticityPlantSeeded", GetInkVorticity(InkTypeId.PlantSeeded, 0.0f));
            fluidCompute.SetFloat("_VorticityPlantGrown", GetInkVorticity(InkTypeId.PlantGrown, 0.0f));
            fluidCompute.SetFloat("_VorticitySteam", GetInkVorticity(InkTypeId.Steam, 1.2f));
            fluidCompute.SetFloat("_VorticityGlitter", GetInkVorticity(InkTypeId.Glitter, 0.5f));
            fluidCompute.SetFloat("_VorticityBlackBody", GetInkVorticity(InkTypeId.BlackBody, 0.3f));
            fluidCompute.SetFloat("_VorticityElectricitySeeded", GetInkVorticity(InkTypeId.ElectricitySeeded, 0.0f));
            fluidCompute.SetFloat("_VorticityElectricityGrown", GetInkVorticity(InkTypeId.ElectricityGrown, 0.0f));
            fluidCompute.SetFloat("_VorticityIce", GetInkVorticity(InkTypeId.Ice, 0.2f));

            // Per-ink advection weights
            fluidCompute.SetFloat("_AdvectionFire", GetInkAdvection(InkTypeId.Fire, 1.0f));
            fluidCompute.SetFloat("_AdvectionWater", GetInkAdvection(InkTypeId.Water, 1.0f));
            fluidCompute.SetFloat("_AdvectionPlantSeeded", GetInkAdvection(InkTypeId.PlantSeeded, 1.0f));
            fluidCompute.SetFloat("_AdvectionPlantGrown", GetInkAdvection(InkTypeId.PlantGrown, 1.0f));
            fluidCompute.SetFloat("_AdvectionSteam", GetInkAdvection(InkTypeId.Steam, 1.0f));
            fluidCompute.SetFloat("_AdvectionGlitter", GetInkAdvection(InkTypeId.Glitter, 1.0f));
            fluidCompute.SetFloat("_AdvectionBlackBody", GetInkAdvection(InkTypeId.BlackBody, 1.0f));
            fluidCompute.SetFloat("_AdvectionElectricitySeeded", GetInkAdvection(InkTypeId.ElectricitySeeded, 1.0f));
            fluidCompute.SetFloat("_AdvectionElectricityGrown", GetInkAdvection(InkTypeId.ElectricityGrown, 1.0f));
            fluidCompute.SetFloat("_AdvectionIce", GetInkAdvection(InkTypeId.Ice, 1.0f));

            // Per-ink pressure weights (reserved for future use)
            fluidCompute.SetFloat("_PressureFire", GetInkPressureWeight(InkTypeId.Fire, 1.0f));
            fluidCompute.SetFloat("_PressureWater", GetInkPressureWeight(InkTypeId.Water, 1.0f));
            fluidCompute.SetFloat("_PressurePlantSeeded", GetInkPressureWeight(InkTypeId.PlantSeeded, 1.0f));
            fluidCompute.SetFloat("_PressurePlantGrown", GetInkPressureWeight(InkTypeId.PlantGrown, 1.0f));
            fluidCompute.SetFloat("_PressureSteam", GetInkPressureWeight(InkTypeId.Steam, 1.0f));
            fluidCompute.SetFloat("_PressureGlitter", GetInkPressureWeight(InkTypeId.Glitter, 1.0f));
            fluidCompute.SetFloat("_PressureBlackBody", GetInkPressureWeight(InkTypeId.BlackBody, 1.0f));
            fluidCompute.SetFloat("_PressureElectricitySeeded", GetInkPressureWeight(InkTypeId.ElectricitySeeded, 1.0f));
            fluidCompute.SetFloat("_PressureElectricityGrown", GetInkPressureWeight(InkTypeId.ElectricityGrown, 1.0f));
            fluidCompute.SetFloat("_PressureIce", GetInkPressureWeight(InkTypeId.Ice, 1.0f));

            // Additional useful parameters
            fluidCompute.SetVector("_TexelSize", new Vector4(1f / resolution, 1f / resolution, resolution, resolution));
        }

        private float GetInkDissipation(InkTypeId type, float defaultValue)
        {
            int idx = (int)type;
            if (inkDefinitions != null && idx < inkDefinitions.Length && inkDefinitions[idx] != null)
                return inkDefinitions[idx].dissipation;
            return defaultValue;
        }

        private float GetInkViscosity(InkTypeId type, float defaultValue)
        {
            int idx = (int)type;
            if (inkDefinitions != null && idx < inkDefinitions.Length && inkDefinitions[idx] != null)
                return inkDefinitions[idx].viscosity;
            return defaultValue;
        }

        private float GetInkVorticity(InkTypeId type, float defaultValue)
        {
            int idx = (int)type;
            if (inkDefinitions != null && idx < inkDefinitions.Length && inkDefinitions[idx] != null)
                return inkDefinitions[idx].vorticity;
            return defaultValue;
        }

        private float GetInkAdvection(InkTypeId type, float defaultValue)
        {
            int idx = (int)type;
            if (inkDefinitions != null && idx < inkDefinitions.Length && inkDefinitions[idx] != null)
                return inkDefinitions[idx].advectionWeight;
            return defaultValue;
        }

        private float GetInkPressureWeight(InkTypeId type, float defaultValue)
        {
            int idx = (int)type;
            if (inkDefinitions != null && idx < inkDefinitions.Length && inkDefinitions[idx] != null)
                return inkDefinitions[idx].pressureWeight;
            return defaultValue;
        }
        private float GetInkInteractionThreshold(InkTypeId type, float defaultValue)
        {
            int idx = (int)type;
            if (inkDefinitions != null && idx < inkDefinitions.Length && inkDefinitions[idx] != null)
                return inkDefinitions[idx].interactionThreshold;
            return defaultValue;
        }

        /// <summary>
        /// Gets clearing parameters from InkTypeDef that has enableClearing=true.
        /// Falls back to standalone fields if no ink has clearing enabled.
        /// </summary>
        private (bool enabled, float threshold, float rate) GetClearingParameters()
        {
            // Search for an ink with clearing enabled
            if (inkDefinitions != null)
            {
                foreach (var def in inkDefinitions)
                {
                    if (def != null && def.enableClearing)
                    {
                        return (true, def.clearingThreshold, def.clearingRate);
                    }
                }
            }

            // Fallback to standalone fields
            return (enableBlackBodyClearingFallback, blackBodyThresholdFallback, blackBodyClearingRateFallback);
        }

        /// <summary>
        /// Builds the ink key color palette for GPU upload.
        /// Returns array of Vector4 where xyz = RGB key color, w = tolerance.
        /// </summary>
        private Vector4[] BuildInkKeyColorPalette()
        {
            var palette = new Vector4[10];

            // Default key colors based on traditional mappings
            var defaults = new Color[]
            {
                new Color(1f, 0.3f, 0f),      // Fire - orange-red
                new Color(0f, 0.5f, 1f),      // Water - blue
                new Color(0.2f, 0.8f, 0.2f),  // PlantSeeded - green
                new Color(0f, 0.5f, 0f),      // PlantGrown - dark green
                new Color(0.8f, 0.8f, 0.9f),  // Steam - light gray
                new Color(1f, 0.8f, 0f),      // Glitter - gold
                new Color(0.1f, 0.1f, 0.1f),  // BlackBody - near black
                new Color(1f, 1f, 0f),        // ElectricitySeeded - yellow
                new Color(0.5f, 0f, 1f),      // ElectricityGrown - purple
                new Color(0.5f, 0.8f, 1f),    // Ice - light cyan
            };

            for (int i = 0; i < 10; i++)
            {
                Color keyColor = defaults[i];
                float tolerance = 0.3f;

                if (inkDefinitions != null && i < inkDefinitions.Length && inkDefinitions[i] != null)
                {
                    keyColor = inkDefinitions[i].inputKeyColor;
                    tolerance = inkDefinitions[i].colorMatchTolerance;
                }

                palette[i] = new Vector4(keyColor.r, keyColor.g, keyColor.b, tolerance);
            }

            return palette;
        }

        private void ClearBuffers()
        {
            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            fluidCompute.SetTexture(kernelClear, "_VelocityWrite", velocity.Write);
            fluidCompute.SetTexture(kernelClear, "_DensityWrite", density.Write);
            fluidCompute.SetTexture(kernelClear, "_PressureWrite", pressure.Write);
            fluidCompute.SetTexture(kernelClear, "_DivergenceWrite", divergence);
            fluidCompute.SetTexture(kernelClear, "_VorticityMag", vorticityTex);

            // Clear particle buffer with zero data (stride-aware)
            int particleCount = resolution * resolution;
            if (gpuPromotesHalf)
            {
                var zero = new iparticle_gpu[particleCount];
                particlesBuffer[particleReadIndex].SetData(zero);
                particlesBuffer[particleWriteIndex].SetData(zero);
            }
            else
            {
                var zero = new iparticle[particleCount];
                particlesBuffer[particleReadIndex].SetData(zero);
                particlesBuffer[particleWriteIndex].SetData(zero);
            }

            fluidCompute.Dispatch(kernelClear, threadGroups, threadGroups, 1);
        }

        private void InitializeObstacles()
        {
            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            fluidCompute.SetTexture(kernelUpdateObstacles, "_ObstacleWrite", obstacles);
            fluidCompute.Dispatch(kernelUpdateObstacles, threadGroups, threadGroups, 1);

            Debug.Log("[SimDriver] Initialized obstacles");
        }

        private void Update()
        {
            // Allow test pattern mode even without compute shader

            if (measurePerformance) stopwatch.Restart();

            // Handle ink type switching with number keys
            if (Keyboard.current != null)
            {
                if (Keyboard.current.digit1Key.wasPressedThisFrame) currentInkType = InkType.Fire;
                if (Keyboard.current.digit2Key.wasPressedThisFrame) currentInkType = InkType.Water;
                if (Keyboard.current.digit3Key.wasPressedThisFrame) currentInkType = InkType.Metal;
                if (Keyboard.current.digit4Key.wasPressedThisFrame) currentInkType = InkType.Electricity;
                if (Keyboard.current.digit5Key.wasPressedThisFrame) currentInkType = InkType.Ice;
                if (Keyboard.current.digit6Key.wasPressedThisFrame) currentInkType = InkType.Plant;
                if (Keyboard.current.digit7Key.wasPressedThisFrame) currentInkType = InkType.Steam;
                if (Keyboard.current.digit8Key.wasPressedThisFrame) currentInkType = InkType.Dust;

                // Quick reset: clear fields and re-seed density
                if (Keyboard.current.rKey.wasPressedThisFrame)
                {
                    Debug.Log("[SimDriver] Reset requested via 'R' key - clearing buffers and re-seeding density.");
                    ClearBuffers();

                    // Re-seed a few blobs so the user immediately sees flow again
                    InjectDensity(new Vector2(0.5f, 0.5f), Color.white);
                    InjectDensity(new Vector2(0.3f, 0.7f), new Color(1f, 0.5f, 0f, 1f)); // Orange
                    InjectDensity(new Vector2(0.7f, 0.3f), new Color(0f, 0.5f, 1f, 1f)); // Blue
                }
            }

            // User input injection - use new Input System
            // Left mouse: inject current ink type
            // Right mouse: inject water (for quick fire+water testing)
            bool shouldInjectLeft = false;
            bool shouldInjectRight = false;
            if (Mouse.current != null)
            {
                Vector2 mouseDelta = Mouse.current.delta.ReadValue();
                bool isMoving = mouseDelta.magnitude > 0.01f;

                if (Mouse.current.leftButton.isPressed)
                    shouldInjectLeft = isMoving || Mouse.current.leftButton.wasPressedThisFrame;

                if (Mouse.current.rightButton.isPressed)
                    shouldInjectRight = isMoving || Mouse.current.rightButton.wasPressedThisFrame;
            }

            if (shouldInjectLeft || autoInject)
            {
                InjectAtMousePosition();
            }

            if (shouldInjectRight)
            {
                InjectAtMousePosition(InkType.Water);
            }

            // Run simulation pipeline (honor optional target Hz)
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
            {
                lastFrameMs = (float)stopwatch.Elapsed.TotalMilliseconds;
            }
        }

        private void LateUpdate()
        {
            // Display runs in LateUpdate so density.Read is guaranteed to reflect
            // the final post-simulation state — all Update() calls (injection
            // queuing, simulation, swaps) have completed by this point.
            if (simRanThisFrame || simulationUpdateRate <= 0)
            {
                UpdateDisplay();
            }
        }

        /// <summary>
        /// Drains all pending GPU operations (density stamps, force injections, etc.)
        /// that were queued by external callers since the last frame.
        /// </summary>
        /// <remarks>
        /// <b>Execution-order contract:</b>
        /// TexturedInjector (order −50) queues stamps/forces into the pending lists
        /// during its Update(). SimDriver (order +50) calls SimulateFrame() → here,
        /// which drains every queue in a single Update(). This guarantees all GPU
        /// work (Dispatch / Blit / Swap) runs inside one contiguous command stream,
        /// giving the DX12 backend correct resource barriers.
        ///
        /// <b>Ping-pong invariant:</b>
        /// Each queue section reads from <c>.Read</c>, writes to <c>.Write</c>, then
        /// calls <c>.Swap()</c> per operation. After this method returns, <c>.Read</c>
        /// always holds the latest state for the simulation kernels that follow.
        ///
        /// <b>Dual path (compute / Blit):</b>
        /// Density stamps prefer the compute path (<c>stampCompute</c>) to stay on
        /// the compute queue. If the compute shader is unassigned, falls back to
        /// Graphics.Blit (graphics queue — may flicker on DX12).
        /// </remarks>
        private void ProcessPendingOperations()
        {
            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            // ── Density stamps ───────────────────────────────────────────────
            // Prefer compute path to keep density on the compute queue (avoids
            // DX12 graphics→compute barriers).  Falls back to Blit if the
            // compute shader is not assigned.
            if (pendingDensityStamps.Count > 0 && density != null)
            {
                if (stampComputeReady)
                {
                    bool canBatch = useBatchedStamping && batchedStampReady;
                    if (canBatch)
                    {
                        // Batch only if all stamps share the same texture (current compute supports single _StampTex)
                        Texture2D firstTex = pendingDensityStamps[0].stamp;
                        for (int i = 0; i < pendingDensityStamps.Count; i++)
                        {
                            if (pendingDensityStamps[i].stamp != firstTex || firstTex == null)
                            {
                                canBatch = false;
                                break;
                            }
                        }
                        if (canBatch)
                        {
                            int count = pendingDensityStamps.Count;
                            var payloadA = new Vector4[count];
                            var payloadB = new Vector4[count];
                            var payloadC = new Vector4[count];
                            var payloadD = new Vector4[count];
                            for (int i = 0; i < count; i++)
                            {
                                var s = pendingDensityStamps[i];
                                float stampWidthUV = (float)s.stamp.width / resolution;
                                float stampHeightUV = (float)s.stamp.height / resolution;
                                payloadA[i] = new Vector4(s.uvPosition.x, s.uvPosition.y, stampWidthUV, stampHeightUV);
                                payloadB[i] = Vector4.zero; // reserved
                                payloadC[i] = new Vector4(0.01f, s.multiplier, s.useColorOverride ? 1f : 0f, 0f);
                                payloadD[i] = (Vector4)s.overrideColor;
                            }

                            using (var bufA = new ComputeBuffer(count, sizeof(float) * 4))
                            using (var bufB = new ComputeBuffer(count, sizeof(float) * 4))
                            using (var bufC = new ComputeBuffer(count, sizeof(float) * 4))
                            using (var bufD = new ComputeBuffer(count, sizeof(float) * 4))
                            {
                                bufA.SetData(payloadA);
                                bufB.SetData(payloadB);
                                bufC.SetData(payloadC);
                                bufD.SetData(payloadD);

                                batchedStampCompute.SetInt("_StampCount", count);
                                batchedStampCompute.SetVector("_Resolution", new Vector2(resolution, resolution));
                                batchedStampCompute.SetBuffer(kernelStampDensityBatched, "_StampPayloadA", bufA);
                                batchedStampCompute.SetBuffer(kernelStampDensityBatched, "_StampPayloadB", bufB);
                                batchedStampCompute.SetBuffer(kernelStampDensityBatched, "_StampPayloadC", bufC);
                                batchedStampCompute.SetBuffer(kernelStampDensityBatched, "_StampPayloadD", bufD);
                                batchedStampCompute.SetTexture(kernelStampDensityBatched, "_StampTex", firstTex);
                                batchedStampCompute.SetTexture(kernelStampDensityBatched, "_DensityRead", density.Read);
                                batchedStampCompute.SetTexture(kernelStampDensityBatched, "_DensityWrite", density.Write);
                                batchedStampCompute.Dispatch(kernelStampDensityBatched, threadGroups, threadGroups, 1);
                                density.Swap();
                            }
                        }
                    }

                    if (!canBatch)
                    {
                        if (useBatchedStamping && !batchedStampReady && !loggedStampBatchUnavailable)
                        {
                            loggedStampBatchUnavailable = true;
                            Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal("[SimDriver] Stamp batching requested but batchedStampCompute is not ready; falling back to per-stamp dispatch.");
                        }
                        if (useBatchedStamping && batchedStampReady && !loggedStampBatchMixedTextures && pendingDensityStamps.Count > 1)
                        {
                            loggedStampBatchMixedTextures = true;
                            Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal("[SimDriver] Stamp batching skipped because stamps use different textures; running per-stamp dispatch.");
                        }

                        foreach (var s in pendingDensityStamps)
                        {
                            if (s.stamp == null) continue;

                            float stampWidthUV  = (float)s.stamp.width  / resolution;
                            float stampHeightUV = (float)s.stamp.height / resolution;

                            stampCompute.SetTexture(kernelStampDensity, "_DensityRead",  density.Read);
                            stampCompute.SetTexture(kernelStampDensity, "_DensityWrite", density.Write);
                            stampCompute.SetTexture(kernelStampDensity, "_StampTex",     s.stamp);
                            stampCompute.SetVector("_StampCenter", new Vector4(s.uvPosition.x, s.uvPosition.y, 0f, 0f));
                            stampCompute.SetVector("_StampSize",   new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                            stampCompute.SetFloat("_AlphaThreshold", 0.01f);
                            stampCompute.SetFloat("_DensityMul",     s.multiplier);
                            stampCompute.SetFloat("_UseOverride",    s.useColorOverride ? 1f : 0f);
                            stampCompute.SetVector("_OverrideColor", (Vector4)s.overrideColor);
                            stampCompute.SetVector("_Resolution",    new Vector2(resolution, resolution));

                            stampCompute.Dispatch(kernelStampDensity, threadGroups, threadGroups, 1);
                            density.Swap();
                        }
                    }
                }
                else
                {
                    // Blit fallback (graphics queue – may flicker on DX12)
                    EnsureStampMaterial();
                    if (densityStampMaterial != null)
                    {
                        foreach (var s in pendingDensityStamps)
                        {
                            if (s.stamp == null) continue;

                            float stampWidthUV  = (float)s.stamp.width  / resolution;
                            float stampHeightUV = (float)s.stamp.height / resolution;

                            densityStampMaterial.SetTexture("_StampTex", s.stamp);
                            densityStampMaterial.SetVector("_StampCenterUV", new Vector4(s.uvPosition.x, s.uvPosition.y, 0f, 0f));
                            densityStampMaterial.SetVector("_StampSizeUV", new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                            densityStampMaterial.SetFloat("_AlphaThreshold", 0.01f);
                            densityStampMaterial.SetFloat("_StampMode", 0f);
                            densityStampMaterial.SetFloat("_BlackLuminanceThreshold", 0.2f);
                            densityStampMaterial.SetFloat("_DensityMultiplier", s.multiplier);
                            densityStampMaterial.SetFloat("_UseColorOverride", s.useColorOverride ? 1f : 0f);
                            densityStampMaterial.SetColor("_ColorOverride", s.overrideColor);

                            Graphics.Blit(density.Read, density.Write, densityStampMaterial);
                            density.Swap();
                        }
                    }
                }
                // ── GPU particle stamps (mirror density stamps into particle buffer) ─
                if (stampParticlesComputeReady && particlesBuffer != null)
                {
                    // Upload ink key color palette for color-to-ink-type matching
                    Vector4[] inkPalette = BuildInkKeyColorPalette();
                    stampParticlesCompute.SetVectorArray("_InkKeyColors", inkPalette);
                    stampParticlesCompute.SetInt("_NumActiveInks", 10);
                    stampParticlesCompute.SetInt("_UsePaletteLookup", 1); // Enable palette lookup

                    // Debug: log first particle stamp
                    if (!hasLoggedFirstParticleStamp && pendingDensityStamps.Count > 0)
                    {
                        hasLoggedFirstParticleStamp = true;
                        var firstStamp = pendingDensityStamps[0];
                        Debug.Log($"[SimDriver] First particle stamp: texture={firstStamp.stamp?.name}, " +
                                  $"pos={firstStamp.uvPosition}, mul={firstStamp.multiplier}, " +
                                  $"useOverride={firstStamp.useColorOverride}, override={firstStamp.overrideColor}");
                        // Log palette colors
                        for (int i = 0; i < 10; i++)
                        {
                            Debug.Log($"[SimDriver] Ink palette[{i}]: RGB=({inkPalette[i].x:F2},{inkPalette[i].y:F2},{inkPalette[i].z:F2}), tolerance={inkPalette[i].w:F2}");
                        }
                    }

                    foreach (var s in pendingDensityStamps)
                    {
                        if (s.stamp == null) continue;

                        float stampWidthUV  = (float)s.stamp.width  / resolution;
                        float stampHeightUV = (float)s.stamp.height / resolution;

                        stampParticlesCompute.SetBuffer(kernelStampParticles, "_ParticlesRW",
                            particlesBuffer[particleReadIndex]);
                        stampParticlesCompute.SetTexture(kernelStampParticles, "_StampTex", s.stamp);
                        stampParticlesCompute.SetVector("_StampCenter", new Vector4(s.uvPosition.x, s.uvPosition.y, 0f, 0f));
                        stampParticlesCompute.SetVector("_StampSize",   new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                        stampParticlesCompute.SetFloat("_AlphaThreshold", 0.01f);
                        stampParticlesCompute.SetFloat("_DensityMul",     s.multiplier);
                        stampParticlesCompute.SetFloat("_UseOverride",    s.useColorOverride ? 1f : 0f);
                        stampParticlesCompute.SetVector("_OverrideColor", (Vector4)s.overrideColor);
                        stampParticlesCompute.SetVector("_Resolution",    new Vector2(resolution, resolution));

                        stampParticlesCompute.Dispatch(kernelStampParticles, threadGroups, threadGroups, 1);
                    }
                }
                else if (!hasLoggedFirstParticleStamp && pendingDensityStamps.Count > 0)
                {
                    // One-time warning that particle stamps won't work
                    hasLoggedFirstParticleStamp = true;
                    Debug.LogWarning($"[SimDriver] Particle stamps SKIPPED - stampParticlesComputeReady={stampParticlesComputeReady}, " +
                                     $"particlesBuffer={(particlesBuffer != null ? "valid" : "null")}. " +
                                     "Assign StampParticlesCompute in Inspector for creature visibility.");
                }

                pendingDensityStamps.Clear();
            }

            // ── Clear-density masks ──────────────────────────────────────────
            if (pendingClearDensityMasks.Count > 0 && density != null)
            {
                bool canBatchMasks = useBatchedMasks && batchedMaskReady;
                if (canBatchMasks)
                {
                    Texture2D firstMask = pendingClearDensityMasks[0].mask;
                    for (int i = 0; i < pendingClearDensityMasks.Count; i++)
                    {
                        if (pendingClearDensityMasks[i].mask != firstMask || firstMask == null)
                        {
                            canBatchMasks = false;
                            break;
                        }
                    }
                }

                if (canBatchMasks)
                {
                    int count = pendingClearDensityMasks.Count;
                    var payloadA = new Vector4[count];
                    var payloadB = new Vector4[count];
                    for (int i = 0; i < count; i++)
                    {
                        var m = pendingClearDensityMasks[i];
                        float stampWidthUV = (float)m.mask.width / resolution;
                        float stampHeightUV = (float)m.mask.height / resolution;
                        payloadA[i] = new Vector4(m.uvPosition.x, m.uvPosition.y, stampWidthUV, stampHeightUV);
                        payloadB[i] = new Vector4(0.01f, m.blackLuminanceThreshold, 0f, 0f);
                    }

                    using (var bufA = new ComputeBuffer(count, sizeof(float) * 4))
                    using (var bufB = new ComputeBuffer(count, sizeof(float) * 4))
                    {
                        bufA.SetData(payloadA);
                        bufB.SetData(payloadB);

                        batchedMaskCompute.SetInt("_MaskCount", count);
                        batchedMaskCompute.SetVector("_Resolution", new Vector2(resolution, resolution));
                        batchedMaskCompute.SetBuffer(kernelClearMaskBatched, "_MaskPayloadA", bufA);
                        batchedMaskCompute.SetBuffer(kernelClearMaskBatched, "_MaskPayloadB", bufB);
                        batchedMaskCompute.SetTexture(kernelClearMaskBatched, "_MaskTex", pendingClearDensityMasks[0].mask);
                        batchedMaskCompute.SetTexture(kernelClearMaskBatched, "_DensityRead", density.Read);
                        batchedMaskCompute.SetTexture(kernelClearMaskBatched, "_DensityWrite", density.Write);
                        batchedMaskCompute.Dispatch(kernelClearMaskBatched, threadGroups, threadGroups, 1);
                        density.Swap();
                    }
                }
                else
                {
                    if (useBatchedMasks && !batchedMaskReady && !loggedMaskBatchUnavailable)
                    {
                        loggedMaskBatchUnavailable = true;
                        Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal("[SimDriver] Mask batching requested but batchedMaskCompute is not ready; falling back to per-mask dispatch.");
                    }

                    foreach (var c in pendingClearDensityMasks)
                    {
                        if (c.mask == null) continue;

                        float stampWidthUV  = (float)c.mask.width  / resolution;
                        float stampHeightUV = (float)c.mask.height / resolution;

                        // Density clear – compute path preferred
                        if (stampComputeReady)
                        {
                            stampCompute.SetTexture(kernelClearBlackDensity, "_DensityRead",  density.Read);
                            stampCompute.SetTexture(kernelClearBlackDensity, "_DensityWrite", density.Write);
                            stampCompute.SetTexture(kernelClearBlackDensity, "_StampTex",     c.mask);
                            stampCompute.SetVector("_StampCenter", new Vector4(c.uvPosition.x, c.uvPosition.y, 0f, 0f));
                            stampCompute.SetVector("_StampSize",   new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                            stampCompute.SetFloat("_AlphaThreshold", 0.01f);
                            stampCompute.SetFloat("_BlackLumThreshold", c.blackLuminanceThreshold);
                            stampCompute.SetVector("_Resolution", new Vector2(resolution, resolution));

                            stampCompute.Dispatch(kernelClearBlackDensity, threadGroups, threadGroups, 1);
                            density.Swap();
                        }
                        else
                        {
                            EnsureStampMaterial();
                            if (densityStampMaterial != null)
                            {
                                densityStampMaterial.SetTexture("_StampTex", c.mask);
                                densityStampMaterial.SetVector("_StampCenterUV", new Vector4(c.uvPosition.x, c.uvPosition.y, 0f, 0f));
                                densityStampMaterial.SetVector("_StampSizeUV", new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                                densityStampMaterial.SetFloat("_AlphaThreshold", 0.01f);
                                densityStampMaterial.SetFloat("_StampMode", 1f);
                                densityStampMaterial.SetFloat("_BlackLuminanceThreshold", c.blackLuminanceThreshold);

                                Graphics.Blit(density.Read, density.Write, densityStampMaterial);
                                density.Swap();
                            }
                        }

                        // Obstacle map update (single RT, always Blit-based – not on density ping-pong)
                        if (obstacles != null)
                        {
                            EnsureStampMaterial();
                            if (densityStampMaterial != null)
                            {
                                RenderTexture tmpObs = RenderTexture.GetTemporary(
                                    obstacles.width, obstacles.height, 0, obstacles.format);
                                Graphics.Blit(obstacles, tmpObs);

                                densityStampMaterial.SetTexture("_MainTex", tmpObs);
                                densityStampMaterial.SetTexture("_StampTex", c.mask);
                                densityStampMaterial.SetVector("_StampCenterUV", new Vector4(c.uvPosition.x, c.uvPosition.y, 0f, 0f));
                                densityStampMaterial.SetVector("_StampSizeUV", new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                                densityStampMaterial.SetFloat("_AlphaThreshold", 0.01f);
                                densityStampMaterial.SetFloat("_StampMode", 2f);
                                densityStampMaterial.SetFloat("_BlackLuminanceThreshold", c.blackLuminanceThreshold);

                                Graphics.Blit(tmpObs, obstacles, densityStampMaterial);
                                RenderTexture.ReleaseTemporary(tmpObs);
                            }
                        }
                    }
                }
                pendingClearDensityMasks.Clear();
            }

            // ── Force injections (Compute-based) ────────────────────────────
            if (pendingForceInjections.Count > 0 && fluidCompute != null)
            {
                foreach (var f in pendingForceInjections)
                {
                    Vector2 pixelPos = f.position * resolution;

                    fluidCompute.SetVector("_ForcePosition", pixelPos);
                    fluidCompute.SetVector("_ForceDirection", f.force.normalized);
                    fluidCompute.SetFloat("_ForceRadius", forceRadius);
                    fluidCompute.SetFloat("_ForceStrength", forceStrength * f.force.magnitude);
                    fluidCompute.SetFloat("_DeltaTime", timestep);
                    fluidCompute.SetVector("_SimulationSize", new Vector2(resolution, resolution));

                    fluidCompute.SetTexture(kernelAddForce, "_VelocityRead", velocity.Read);
                    fluidCompute.SetTexture(kernelAddForce, "_VelocityWrite", velocity.Write);
                    fluidCompute.Dispatch(kernelAddForce, threadGroups, threadGroups, 1);
                    velocity.Swap();
                }
                pendingForceInjections.Clear();
            }

            // ── Density injections (Compute-based) ──────────────────────────
            if (pendingDensityInjections.Count > 0 && fluidCompute != null && density != null)
            {
                if (useBatchedDensityInjection && batchedInjectionReady)
                {
                    int count = pendingDensityInjections.Count;
                    var injA = new Vector4[count];
                    var injB = new Vector4[count];
                    for (int i = 0; i < count; i++)
                    {
                        var d = pendingDensityInjections[i];
                        float colorIntensity = Mathf.Max(d.color.r, Mathf.Max(d.color.g, d.color.b));
                        if (colorIntensity <= 0f) continue;
                        injA[i] = new Vector4(d.position.x, d.position.y, d.color.r, d.color.g);
                        injB[i] = new Vector4(d.color.b, d.inkTypeIndex, 0f, 0f);
                    }

                    using (var bufferA = new ComputeBuffer(count, sizeof(float) * 4))
                    using (var bufferB = new ComputeBuffer(count, sizeof(float) * 4))
                    {
                        bufferA.SetData(injA);
                        bufferB.SetData(injB);

                        batchedInjectionCompute.SetInt("_InjectionCount", count);
                        batchedInjectionCompute.SetFloat("_ForceRadius", forceRadius);
                        batchedInjectionCompute.SetFloat("_DensityAmount", densityAmount);
                        batchedInjectionCompute.SetVector("_Resolution", new Vector2(resolution, resolution));
                        batchedInjectionCompute.SetBuffer(kernelAddDensityBatched, "_Injections", bufferA);
                        batchedInjectionCompute.SetBuffer(kernelAddDensityBatched, "_Injections2", bufferB);
                        batchedInjectionCompute.SetTexture(kernelAddDensityBatched, "_DensityRead", density.Read);
                        batchedInjectionCompute.SetTexture(kernelAddDensityBatched, "_DensityWrite", density.Write);
                        batchedInjectionCompute.Dispatch(kernelAddDensityBatched, threadGroups, threadGroups, 1);
                        density.Swap();

                        if (useParticleSimulation && particlesBuffer != null && kernelAddParticlesBatched != 0)
                        {
                            batchedInjectionCompute.SetBuffer(kernelAddParticlesBatched, "_Injections", bufferA);
                            batchedInjectionCompute.SetBuffer(kernelAddParticlesBatched, "_Injections2", bufferB);
                            batchedInjectionCompute.SetBuffer(kernelAddParticlesBatched, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                            batchedInjectionCompute.SetBuffer(kernelAddParticlesBatched, "_ParticlesWrite", particlesBuffer[particleWriteIndex]);
                            batchedInjectionCompute.SetVector("_Resolution", new Vector2(resolution, resolution));
                            batchedInjectionCompute.Dispatch(kernelAddParticlesBatched, threadGroups, threadGroups, 1);
                            SwapParticleBuffers();
                        }
                    }
                }
                else
                {
                    foreach (var d in pendingDensityInjections)
                    {
                        float colorIntensity = Mathf.Max(d.color.r, Mathf.Max(d.color.g, d.color.b));
                        if (colorIntensity <= 0f) continue;

                        Vector2 pixelPos = d.position * resolution;

                        fluidCompute.SetVector("_ForcePosition", pixelPos);
                        fluidCompute.SetFloat("_ForceRadius", forceRadius);
                        fluidCompute.SetFloat("_DensityAmount", densityAmount);
                        fluidCompute.SetVector("_DensityColor", (Vector4)d.color);
                        fluidCompute.SetVector("_SimulationSize", new Vector2(resolution, resolution));

                        fluidCompute.SetTexture(kernelAddDensity, "_DensityRead", density.Read);
                        fluidCompute.SetTexture(kernelAddDensity, "_DensityWrite", density.Write);
                        fluidCompute.Dispatch(kernelAddDensity, threadGroups, threadGroups, 1);
                        density.Swap();

                        // Mirror into particle buffer via GPU (same ForceParams already set)
                        if (useParticleSimulation && particlesBuffer != null && kernelAddParticlesGaussian != 0)
                        {
                            fluidCompute.SetInt("_InkTypeIndex", d.inkTypeIndex);
                            fluidCompute.SetBuffer(kernelAddParticlesGaussian, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                            fluidCompute.SetBuffer(kernelAddParticlesGaussian, "_ParticlesWrite", particlesBuffer[particleWriteIndex]);
                            fluidCompute.Dispatch(kernelAddParticlesGaussian, threadGroups, threadGroups, 1);
                            SwapParticleBuffers();
                        }
                    }
                }
                pendingDensityInjections.Clear();
            }

        }

        private void SimulateFrame()
        {
            // Process queued stamps / injections from external callers first
            ProcessPendingOperations();

            if (fluidCompute == null)
            {
                // Test pattern mode - just cycle colors
                return;
            }

            int threadGroups = Mathf.CeilToInt(resolution / 8f);
            var sw = measurePerformance ? stopwatch : null;

            // Set global parameters that are constant for the frame
            SetShaderConstants();

            // CLEAR CREATURE BUFFERS: Both obstacle and creature ink buffers are rewritten each frame
            // This ensures black outlines don't persist
            if (obstacles != null)
            {
                RenderTexture.active = obstacles;
                GL.Clear(true, true, new Color(0, 0, 0, 0));  // Clear to no obstacles
                RenderTexture.active = null;
            }

            if (creatureInkBuffer != null)
            {
                RenderTexture.active = creatureInkBuffer;
                GL.Clear(true, true, Color.clear);  // Clear creature inks from last frame
                RenderTexture.active = null;
            }

            // 1. Advection - Move quantities along velocity field
            if (sw != null) sw.Restart();

            // Advect velocity (read from velocity.Read, write to velocity.Write)
            fluidCompute.SetTexture(kernelAdvection, "_VelocityRead", velocity.Read);
            fluidCompute.SetTexture(kernelAdvection, "_VelocityWrite", velocity.Write);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityRead", velocity.Read);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityWrite", velocity.Write);
            fluidCompute.SetFloat("_Dissipation", velocityDissipation);
            fluidCompute.Dispatch(kernelAdvection, threadGroups, threadGroups, 1);
            velocity.Swap();

            // Advect density (if using RT-based density)
            if (density != null)
            {
                fluidCompute.SetTexture(kernelAdvection, "_VelocityRead", velocity.Read);
                // Bind _VelocityWrite to velocity.Write (not velocity.Read) to avoid
                // DX12 UAV aliasing: same resource in two UAV slots causes resource
                // barrier conflicts and GPU hangs (TDR). The Advection kernel does not
                // write to _VelocityWrite, but DX12 still creates barriers for all
                // declared RWTexture2D slots regardless of actual usage.
                fluidCompute.SetTexture(kernelAdvection, "_VelocityWrite", velocity.Write);
                fluidCompute.SetTexture(kernelAdvection, "_QuantityRead", density.Read);
                fluidCompute.SetTexture(kernelAdvection, "_QuantityWrite", density.Write);
                fluidCompute.SetFloat("_Dissipation", dissipation);
                fluidCompute.Dispatch(kernelAdvection, threadGroups, threadGroups, 1);
                density.Swap();
            }

            // Advect and dissipate particles (iparticle) if enabled and within safe resolution
            if (useParticleSimulation && particlesBuffer != null)
            {
                if (resolution > maxParticleSimResolution)
                {
                    // Particle simulation skipped at this resolution
                }
                else
                {
                    if (useParticleAdvection && kernelAdvectParticles != 0)
                    {
                        fluidCompute.SetBuffer(kernelAdvectParticles, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                        fluidCompute.SetBuffer(kernelAdvectParticles, "_ParticlesWrite", particlesBuffer[particleWriteIndex]);
                        fluidCompute.SetTexture(kernelAdvectParticles, "_VelocityRead", velocity.Read);
                        fluidCompute.Dispatch(kernelAdvectParticles, threadGroups, threadGroups, 1);
                        SwapParticleBuffers();
                    }

                    // Ink interactions (cellular automata reactions)
                    // Runs after advection, before dissipation.
                    // Uses same timestep as fluid sim for consistency/determinism.
                    // Each group dispatches separately; buffer swaps between groups.
                    if (useInkInteractions && inkInteractionsReady && affinityGroups != null)
                    {
                        inkInteractionsCompute.SetInt("_Resolution", resolution);
                        inkInteractionsCompute.SetFloat("_DeltaTime", timestep);
                        inkInteractionsCompute.SetInt("_DebugMode", inkInteractionsDebugMode ? 1 : 0);

                        // Black body ink clearing parameters (from InkTypeDef or fallback)
                        var clearing = GetClearingParameters();
                        inkInteractionsCompute.SetInt("_EnableBlackBodyClearing", clearing.enabled ? 1 : 0);
                        inkInteractionsCompute.SetFloat("_BlackBodyThreshold", clearing.threshold);
                        inkInteractionsCompute.SetFloat("_BlackBodyClearingRate", clearing.rate);

                        foreach (var group in affinityGroups)
                        {
                            if (group == null) continue;

                            // Upload affinity group data
                            int[] indices = group.GetInkIndices();
                            inkInteractionsCompute.SetInts("_InkIndices", indices);
                            inkInteractionsCompute.SetMatrix("_ProductMatrix", group.productMatrix);
                            inkInteractionsCompute.SetVector("_ProductCol4", group.productCol4);
                            inkInteractionsCompute.SetVector("_ProductCol5", group.productCol5);
                            Vector3 weights = group.GetWeights();
                            inkInteractionsCompute.SetFloats("_Weights", weights.x, weights.y, weights.z);
                            inkInteractionsCompute.SetFloat("_RateMultiplier", group.reactionRateMultiplier);

                            // Per-ink interaction thresholds (matching the 4 inks in this group)
                            Vector4 thresholds = new Vector4(
                                GetInkInteractionThreshold((InkTypeId)indices[0], 0.01f),
                                GetInkInteractionThreshold((InkTypeId)indices[1], 0.01f),
                                GetInkInteractionThreshold((InkTypeId)indices[2], 0.01f),
                                GetInkInteractionThreshold((InkTypeId)indices[3], 0.01f)
                            );
                            inkInteractionsCompute.SetVector("_InteractionThresholds", thresholds);

                            // Debug: log dispatch info once
                            if (Time.frameCount == 10)
                            {
                                var pm = group.productMatrix;
                                Debug.Log($"[InkInteractions] Group '{group.groupName}' dispatch:\n" +
                                    $"  Indices: [{indices[0]}, {indices[1]}, {indices[2]}, {indices[3]}]\n" +
                                    $"  ProductMatrix row0: [{pm.m00:F2}, {pm.m01:F2}, {pm.m02:F2}, {pm.m03:F2}]\n" +
                                    $"  ProductMatrix row1: [{pm.m10:F2}, {pm.m11:F2}, {pm.m12:F2}, {pm.m13:F2}]\n" +
                                    $"  ProductMatrix row2: [{pm.m20:F2}, {pm.m21:F2}, {pm.m22:F2}, {pm.m23:F2}]\n" +
                                    $"  ProductMatrix row3: [{pm.m30:F2}, {pm.m31:F2}, {pm.m32:F2}, {pm.m33:F2}]\n" +
                                    $"  Weights: [{weights.x}, {weights.y}, {weights.z}]\n" +
                                    $"  RateMultiplier: {group.reactionRateMultiplier}\n" +
                                    $"  Resolution: {resolution}, DeltaTime: {timestep}");
                            }

                            // Dispatch
                            inkInteractionsCompute.SetBuffer(kernelInkInteractions, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                            inkInteractionsCompute.SetBuffer(kernelInkInteractions, "_ParticlesWrite", particlesBuffer[particleWriteIndex]);
                            inkInteractionsCompute.Dispatch(kernelInkInteractions, threadGroups, threadGroups, 1);
                            SwapParticleBuffers();
                        }
                    }

                    if (useParticleDissipation && kernelDissipateParticles != 0)
                    {
                        fluidCompute.SetBuffer(kernelDissipateParticles, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                        fluidCompute.SetBuffer(kernelDissipateParticles, "_ParticlesWrite", particlesBuffer[particleWriteIndex]);
                        fluidCompute.Dispatch(kernelDissipateParticles, threadGroups, threadGroups, 1);
                        SwapParticleBuffers();
                    }

                    if (useParticleDiffusion && kernelDiffuseParticles != 0)
                    {
                        fluidCompute.SetBuffer(kernelDiffuseParticles, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                        fluidCompute.SetBuffer(kernelDiffuseParticles, "_ParticlesWrite", particlesBuffer[particleWriteIndex]);
                        fluidCompute.Dispatch(kernelDiffuseParticles, threadGroups, threadGroups, 1);
                        SwapParticleBuffers();
                    }
                }
            }

            if (sw != null) advectionMs = (float)sw.Elapsed.TotalMilliseconds;

            // 2. Diffusion (optional, for high viscosity)
            if (viscosity > 0.001f && diffusionIterations > 0)
            {
                if (sw != null) sw.Restart();

                for (int i = 0; i < diffusionIterations; i++)
                {
                    fluidCompute.SetTexture(kernelDiffusion, "_VelocityRead", velocity.Read);
                    fluidCompute.SetTexture(kernelDiffusion, "_VelocityWrite", velocity.Write);
                    fluidCompute.Dispatch(kernelDiffusion, threadGroups, threadGroups, 1);
                    velocity.Swap();
                }

                if (sw != null) diffusionMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 3. Vorticity confinement (adds swirling motion)
            if (vorticity > 0)
            {
                if (sw != null) sw.Restart();

                // Calculate vorticity
                fluidCompute.SetTexture(kernelVorticity, "_VelocityRead", velocity.Read);
                fluidCompute.SetTexture(kernelVorticity, "_VorticityMag", vorticityTex);
                fluidCompute.Dispatch(kernelVorticity, threadGroups, threadGroups, 1);

                // Apply vorticity confinement (with per-ink vorticity weights)
                fluidCompute.SetTexture(kernelVorticityConfinement, "_VelocityRead", velocity.Read);
                fluidCompute.SetTexture(kernelVorticityConfinement, "_VelocityWrite", velocity.Write);
                fluidCompute.SetTexture(kernelVorticityConfinement, "_VorticityMag", vorticityTex);
                // Bind particle buffer for per-ink vorticity sampling
                if (useParticleSimulation && particlesBuffer != null && particlesBuffer[particleReadIndex] != null)
                {
                    fluidCompute.SetBuffer(kernelVorticityConfinement, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                }
                fluidCompute.Dispatch(kernelVorticityConfinement, threadGroups, threadGroups, 1);
                velocity.Swap();

                if (sw != null) vorticityMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 4. Pressure projection (ensure divergence-free velocity)
            if (sw != null) sw.Restart();

            // Calculate divergence
            fluidCompute.SetTexture(kernelDivergence, "_VelocityRead", velocity.Read);
            fluidCompute.SetTexture(kernelDivergence, "_DivergenceWrite", divergence);
            if (useParticleSimulation && particlesBuffer != null && particlesBuffer[particleReadIndex] != null)
            {
                fluidCompute.SetBuffer(kernelDivergence, "_ParticlesRead", particlesBuffer[particleReadIndex]);
            }
            fluidCompute.Dispatch(kernelDivergence, threadGroups, threadGroups, 1);

            // DON'T clear pressure - let it persist for better convergence
            // Clearing it every frame causes instability
            // pressure.Clear(Color.clear);

            // Choose pressure solver based on settings
            if (useRedBlackSolver && kernelPressureRedBlack != 0)
            {
                // Red-Black Gauss-Seidel (faster convergence)
                for (int i = 0; i < pressureIterations; i++)
                {
                    // Red cells pass (set alpha = 0 to select red cells)
                    fluidCompute.SetFloat("_Alpha", 0f);
                    fluidCompute.SetTexture(kernelPressureRedBlack, "_PressureRead", pressure.Read);
                    fluidCompute.SetTexture(kernelPressureRedBlack, "_DivergenceRead", divergence);
                    fluidCompute.Dispatch(kernelPressureRedBlack, threadGroups, threadGroups, 1);

                    // Black cells pass (set alpha = 1 to select black cells)
                    fluidCompute.SetFloat("_Alpha", 1f);
                    fluidCompute.SetTexture(kernelPressureRedBlack, "_PressureRead", pressure.Read);
                    fluidCompute.SetTexture(kernelPressureRedBlack, "_DivergenceRead", divergence);
                    fluidCompute.Dispatch(kernelPressureRedBlack, threadGroups, threadGroups, 1);
                }
            }
            else
            {
                // Standard Jacobi iterations with ping-pong
                for (int i = 0; i < pressureIterations; i++)
                {
                    fluidCompute.SetTexture(kernelPressure, "_PressureRead", pressure.Read);
                    fluidCompute.SetTexture(kernelPressure, "_DivergenceRead", divergence);
                    fluidCompute.SetTexture(kernelPressure, "_PressureWrite", pressure.Write);
                    fluidCompute.Dispatch(kernelPressure, threadGroups, threadGroups, 1);
                    pressure.Swap();
                }
            }

            if (sw != null) pressureMs = (float)sw.Elapsed.TotalMilliseconds;

            // 5. Subtract pressure gradient (make velocity divergence-free)
            if (sw != null) sw.Restart();

            fluidCompute.SetTexture(kernelSubtractGradient, "_VelocityRead", velocity.Read);
            fluidCompute.SetTexture(kernelSubtractGradient, "_VelocityWrite", velocity.Write);
            fluidCompute.SetTexture(kernelSubtractGradient, "_PressureRead", pressure.Read);
            fluidCompute.Dispatch(kernelSubtractGradient, threadGroups, threadGroups, 1);
            velocity.Swap();

            // 6. Apply obstacle boundaries (if available)
            if (kernelApplyObstacleBoundary != 0)
            {
                fluidCompute.SetTexture(kernelApplyObstacleBoundary, "_VelocityRead", velocity.Read);
                fluidCompute.SetTexture(kernelApplyObstacleBoundary, "_VelocityWrite", velocity.Write);
                fluidCompute.SetTexture(kernelApplyObstacleBoundary, "_ObstacleRead", obstacles);
                fluidCompute.Dispatch(kernelApplyObstacleBoundary, threadGroups, threadGroups, 1);
                velocity.Swap();
            }

            if (sw != null) projectionMs = (float)sw.Elapsed.TotalMilliseconds;

            // 7. Splat particle channels for display (compute → textures)
            if (useParticleSimulation && channelSplatReady && particlesBuffer != null
                && channelRT0 != null && channelRT1 != null && channelRT2 != null)
            {
                particleChannelSplatCompute.SetInt("_Resolution", resolution);
                particleChannelSplatCompute.SetBuffer(kernelChannelSplat, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                particleChannelSplatCompute.SetTexture(kernelChannelSplat, "_Channels0", channelRT0);
                particleChannelSplatCompute.SetTexture(kernelChannelSplat, "_Channels1", channelRT1);
                particleChannelSplatCompute.SetTexture(kernelChannelSplat, "_Channels2", channelRT2);
                int splatGroups = Mathf.CeilToInt(resolution / 8f);
                particleChannelSplatCompute.Dispatch(kernelChannelSplat, splatGroups, splatGroups, 1);

                // Copy into SRV-only, mipmapped textures so the gradient shader can minify safely.
                if (channelRT0Mipped != null && channelRT1Mipped != null && channelRT2Mipped != null)
                {
                    Graphics.Blit(channelRT0, channelRT0Mipped);
                    Graphics.Blit(channelRT1, channelRT1Mipped);
                    Graphics.Blit(channelRT2, channelRT2Mipped);
                }

                // Downsample to display resolution for 1:1 sampling (avoids large minification ratios).
                if (effectiveDisplayRes < resolution &&
                    channelRT0Down != null && channelRT1Down != null && channelRT2Down != null)
                {
                    Graphics.Blit(channelRT0Mipped ?? channelRT0, channelRT0Down);
                    Graphics.Blit(channelRT1Mipped ?? channelRT1, channelRT1Down);
                    Graphics.Blit(channelRT2Mipped ?? channelRT2, channelRT2Down);
                }

                // Force UAV -> SRV transition before the fragment shader samples the channel textures.
                Graphics.SetRenderTarget(null);
            }
        }

        private void InjectAtMousePosition(InkType? overrideInkType = null)
        {
            if (Mouse.current == null) return;

            // Get mouse position in screen space
            Vector2 mousePos = Mouse.current.position.ReadValue();

            // Convert to normalized viewport coordinates (0-1)
            Vector2 uv = new Vector2(
                mousePos.x / Screen.width,
                mousePos.y / Screen.height
            );

            // Clamp to valid range
            uv.x = Mathf.Clamp01(uv.x);
            uv.y = Mathf.Clamp01(uv.y);

            // Calculate velocity from mouse delta - use new Input System
            Vector2 mouseDelta = Mouse.current.delta.ReadValue();

            // Convert mouse delta to velocity in pixel space
            // Scale directly by injectionForce - keep it simple!
            Vector2 velocity = new Vector2(
                mouseDelta.x,
                mouseDelta.y
            ) * injectionForce;

            // Inject force and density with ink type color
            InjectForce(uv, velocity);
            InkType inkType = overrideInkType ?? currentInkType;
            Color inkColor = GetInkTypeColor(inkType);
            InjectDensity(uv, inkColor, inkType);
        }

        /// <summary>
        /// Public API: Inject force at UV position (0-1 range).
        /// The operation is queued and executed inside SimulateFrame.
        /// </summary>
        public void InjectForce(Vector2 position, Vector2 force)
        {
            if (fluidCompute == null) return;

            pendingForceInjections.Add(new PendingForceInjection
            {
                position = position,
                force = force
            });
        }

        /// <summary>
        /// Public API: Inject density at UV position (0-1 range) using the AddDensity kernel.
        /// The GPU dispatch is queued and executed inside SimulateFrame.
        /// CPU-side particle injection runs immediately.
        /// </summary>
        public void InjectDensity(Vector2 position, Color color, InkType inkType = InkType.Fire)
        {
            if (fluidCompute == null || density == null)
            {
                return;
            }

            float colorIntensity = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            if (colorIntensity <= 0f || densityAmount <= 0f) return;

            pendingDensityInjections.Add(new PendingDensityInjection
            {
                position = position,
                color = color,
                inkTypeIndex = GetParticleFieldIndex(inkType)
            });

            // Mirror this injection into the particle buffer.
            // GPU path (AddParticlesGaussian) is dispatched in ProcessPendingOperations
            // when particle sim is enabled. Fall back to CPU when unavailable.
            if (!useParticleSimulation || particlesBuffer == null || kernelAddParticlesGaussian == 0)
            {
                InjectParticlesAtPoint(position, color);
            }
        }

        /// <summary>
        /// Interface-compatible overload using raw iparticle field index (0-9).
        /// This bypasses the InkType enum to allow direct injection into specific channels.
        /// Use InkTypeId values: PlantSeeded=2, ElectricitySeeded=7, etc.
        /// </summary>
        void ISimulationWriter.InjectDensity(Vector2 position, Color color, int inkTypeIndex)
        {
            if (fluidCompute == null || density == null) return;

            float colorIntensity = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            if (colorIntensity <= 0f || densityAmount <= 0f) return;

            // Use raw iparticle field index directly (0-9), clamped to valid range
            int validIndex = Mathf.Clamp(inkTypeIndex, 0, 9);

            pendingDensityInjections.Add(new PendingDensityInjection
            {
                position = position,
                color = color,
                inkTypeIndex = validIndex
            });

            // GPU path (AddParticlesGaussian) is dispatched in ProcessPendingOperations.
            // CPU fallback doesn't support arbitrary channels, so skip it for seeds.
            // This is acceptable since seed injection requires particle simulation anyway.
        }

        /// <summary>
        /// CPU helper: inject ink at a single UV position into the iparticle buffer
        /// using a simple circular radius and mapping RGB to fire/water/ice channels.
        /// </summary>
        private void InjectParticlesAtPoint(Vector2 position, Color color)
        {
            if (particlesBuffer == null) return;

            int radiusPixels = Mathf.RoundToInt(forceRadius);
            if (radiusPixels <= 0) return;

            int centerX = Mathf.RoundToInt(position.x * resolution);
            int centerY = Mathf.RoundToInt(position.y * resolution);

            int particleCount = resolution * resolution;

            float rMul = color.r * densityAmount;
            float gMul = color.g * densityAmount;
            float bMul = color.b * densityAmount;

            int minX = Mathf.Max(0, centerX - radiusPixels);
            int maxX = Mathf.Min(resolution - 1, centerX + radiusPixels);
            int minY = Mathf.Max(0, centerY - radiusPixels);
            int maxY = Mathf.Min(resolution - 1, centerY + radiusPixels);
            float radiusSqr = radiusPixels * radiusPixels;

            if (gpuPromotesHalf)
            {
                var particles = new iparticle_gpu[particleCount];
                particlesBuffer[particleReadIndex].GetData(particles);
                for (int y = minY; y <= maxY; y++)
                {
                    int dy = y - centerY;
                    for (int x = minX; x <= maxX; x++)
                    {
                        int dx = x - centerX;
                        if (dx * dx + dy * dy > radiusSqr) continue;
                        int idx = y * resolution + x;
                        particles[idx].fire  += rMul;
                        particles[idx].water += gMul;
                        particles[idx].ice   += bMul;
                    }
                }
                particlesBuffer[particleReadIndex].SetData(particles);
            }
            else
            {
                var particles = new iparticle[particleCount];
                particlesBuffer[particleReadIndex].GetData(particles);
                for (int y = minY; y <= maxY; y++)
                {
                    int dy = y - centerY;
                    for (int x = minX; x <= maxX; x++)
                    {
                        int dx = x - centerX;
                        if (dx * dx + dy * dy > radiusSqr) continue;
                        int idx = y * resolution + x;
                        particles[idx].fire  += (half)rMul;
                        particles[idx].water += (half)gMul;
                        particles[idx].ice   += (half)bMul;
                    }
                }
                particlesBuffer[particleReadIndex].SetData(particles);
            }
        }

        /// <summary>
        /// Public API: Stamp a texture directly into the GPU density field.
        /// This uses a lightweight full-screen blit shader to add the stamp
        /// into the current density RenderTexture without touching CPU buffers.
        /// </summary>
        /// <param name="uvPosition">Center position in UV space (0-1)</param>
        /// <param name="stamp">Texture to stamp directly into density</param>
        /// <param name="densityMultiplier">Scalar multiplier applied to sampled color</param>
        /// <param name="useColorOverride">If true, ignore stamp RGB and use overrideColor * alpha</param>
        /// <param name="overrideColor">Color to use when useColorOverride is true</param>
        public void StampDensity(Vector2 uvPosition, Texture2D stamp, float densityMultiplier, bool useColorOverride, Color overrideColor)
        {
            if (density == null || stamp == null)
            {
                return;
            }

            pendingDensityStamps.Add(new PendingDensityStamp
            {
                uvPosition = uvPosition,
                stamp = stamp,
                multiplier = densityMultiplier,
                useColorOverride = useColorOverride,
                overrideColor = overrideColor
            });
        }

        private void SwapParticleBuffers()
        {
            int temp = particleReadIndex;
            particleReadIndex = particleWriteIndex;
            particleWriteIndex = temp;
        }

        /// <summary>
        /// Clears density in regions defined as "black" in the given mask texture.
        /// Used for black ink/obstacle style behavior so that these regions
        /// do not advect or linger in the simulation.
        /// </summary>
        public void ClearDensityWithMask(Vector2 uvPosition, Texture2D mask, float blackLuminanceThreshold = 0.2f)
        {
            if (density == null || mask == null)
            {
                return;
            }

            pendingClearDensityMasks.Add(new PendingClearDensityMask
            {
                uvPosition = uvPosition,
                mask = mask,
                blackLuminanceThreshold = blackLuminanceThreshold
            });
        }

        /// <summary>
        /// Public API: Stamp obstacles from a texture and clear particles at obstacle positions.
        /// Obstacles block ink flow and actively displace existing inks by clearing them.
        /// Obstacle buffer is cleared each frame, so stamp every frame to maintain obstacles.
        /// </summary>
        /// <param name="uvPosition">Center position in UV space (0-1)</param>
        /// <param name="stamp">Texture to stamp - uses alpha channel for obstacle density</param>
        public void StampObstacles(Vector2 uvPosition, Texture2D stamp)
        {
            if (obstacles == null || stamp == null || particlesBuffer == null) return;

            int stampWidth = stamp.width;
            int stampHeight = stamp.height;
            Color[] stampPixels = stamp.GetPixels();

            int centerX = Mathf.RoundToInt(uvPosition.x * resolution);
            int centerY = Mathf.RoundToInt(uvPosition.y * resolution);
            int startX = centerX - stampWidth / 2;
            int startY = centerY - stampHeight / 2;

            // Read current obstacles
            RenderTexture.active = obstacles;
            Texture2D tempObstacles = new Texture2D(resolution, resolution, TextureFormat.RFloat, false);
            tempObstacles.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            tempObstacles.Apply();
            Color[] obstaclePixels = tempObstacles.GetPixels();
            RenderTexture.active = null;

            // Read current particles to clear at obstacle positions
            int particleCount = resolution * resolution;
            bool particlesModified = false;

            // Stamp obstacles and clear particles (stride-aware)
            if (gpuPromotesHalf)
            {
                var particles = new iparticle_gpu[particleCount];
                particlesBuffer[particleReadIndex].GetData(particles);
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
                        particles[idx] = new iparticle_gpu();
                        particlesModified = true;
                    }
                if (particlesModified)
                    particlesBuffer[particleWriteIndex].SetData(particles);
            }
            else
            {
                var particles = new iparticle[particleCount];
                particlesBuffer[particleReadIndex].GetData(particles);
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
                    particlesBuffer[particleWriteIndex].SetData(particles);
            }

            // Write back obstacles
            tempObstacles.SetPixels(obstaclePixels);
            tempObstacles.Apply();
            Graphics.Blit(tempObstacles, obstacles);

            Destroy(tempObstacles);
        }

        /// <summary>
        /// Public API: Stamp a texture onto the creature ink buffer (NOT persistent in simulation).
        /// Creature inks are composited each frame and then cleared, so they don't persist or blur.
        /// This directly writes pixels without Gaussian falloff, creating sharp edges.
        /// </summary>
        /// <param name="uvPosition">Center position in UV space (0-1)</param>
        /// <param name="stamp">Texture to stamp (will use its colors directly)</param>
        /// <param name="scale">Size multiplier (1.0 = match simulation resolution)</param>
        public void StampTexture(Vector2 uvPosition, Texture2D stamp, float scale = 1.0f)
        {
            if (creatureInkBuffer == null || stamp == null) return;

            // Calculate stamp size in simulation pixels
            int stampWidth = stamp.width;
            int stampHeight = stamp.height;

            // Get stamp pixels
            Color[] stampPixels = stamp.GetPixels();

            // Convert center UV to pixel position
            int centerX = Mathf.RoundToInt(uvPosition.x * resolution);
            int centerY = Mathf.RoundToInt(uvPosition.y * resolution);

            // Calculate stamp origin (top-left corner)
            int startX = centerX - stampWidth / 2;
            int startY = centerY - stampHeight / 2;

            // Create a temporary texture to upload the stamp
            Texture2D tempTex = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
            Color[] tempPixels = new Color[resolution * resolution];

            // Read current creature ink buffer state (accumulate multiple stamps if needed)
            RenderTexture.active = creatureInkBuffer;
            Texture2D currentCreature = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
            currentCreature.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            currentCreature.Apply();
            Color[] currentPixels = currentCreature.GetPixels();
            RenderTexture.active = null;

            // Copy current state
            System.Array.Copy(currentPixels, tempPixels, currentPixels.Length);

            // Stamp the texture onto it
            for (int y = 0; y < stampHeight; y++)
            {
                for (int x = 0; x < stampWidth; x++)
                {
                    int targetX = startX + x;
                    int targetY = startY + y;

                    // Skip if out of bounds
                    if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution)
                        continue;

                    Color stampColor = stampPixels[y * stampWidth + x];

                    // Skip transparent pixels
                    if (stampColor.a < 0.01f)
                        continue;

                    // Write directly (additive blending for multiple creatures)
                    int targetIdx = targetY * resolution + targetX;
                    tempPixels[targetIdx] += stampColor;
                }
            }

            // Upload to creature ink buffer
            tempTex.SetPixels(tempPixels);
            tempTex.Apply();

            Graphics.Blit(tempTex, creatureInkBuffer);

            // Cleanup
            Destroy(tempTex);
            Destroy(currentCreature);
        }

        private void UpdateDisplay()
        {
            RenderTexture sourceTexture;

            if (useParticleRenderPass && particlesBuffer != null && particleToColorCompute != null)
            {
                // Render from particle buffer into displayRT
                RenderParticlesToDisplay();
                sourceTexture = displayRT;
            }
            else if (displayVelocity)
            {
                sourceTexture = velocity.Read;
            }
            else
            {
                // Prefer RT-based density; fall back to particle display if explicitly enabled.
                sourceTexture = (!useParticleDisplay && density != null)
                    ? density.Read
                    : ConvertParticlesToTexture();
            }

            // Staging RT workaround removed; flickering was caused by Blend SrcAlpha in gradient shader.

            // COMPOSITE CREATURE INK FOR DISPLAY ONLY
            // Creature buffer is NOT added to particles (doesn't persist in simulation)
            // It's only composited visually for display
            RenderTexture compositeRT = null;
            if (creatureInkBuffer != null && !displayVelocity && useCpuCreatureComposite)
            {
                compositeRT = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGBHalf);

                // Read base density
                RenderTexture.active = sourceTexture;
                Texture2D tempDensity = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
                tempDensity.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                tempDensity.Apply();
                Color[] densityPixels = tempDensity.GetPixels();

                // Read creature buffer
                RenderTexture.active = creatureInkBuffer;
                Texture2D tempCreature = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
                tempCreature.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                tempCreature.Apply();
                Color[] creaturePixels = tempCreature.GetPixels();
                RenderTexture.active = null;

                // Composite for display
                for (int i = 0; i < densityPixels.Length; i++)
                {
                    densityPixels[i] += creaturePixels[i];
                }

                // Write to composite RT
                Texture2D composite = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
                composite.SetPixels(densityPixels);
                composite.Apply();
                Graphics.Blit(composite, compositeRT);

                // Use composite as source
                sourceTexture = compositeRT;

                // Cleanup
                Destroy(tempDensity);
                Destroy(tempCreature);
                Destroy(composite);
            }

            // One-time diagnostic for rendering path
            if (!loggedDisplayDiagnostic)
            {
                loggedDisplayDiagnostic = true;
                bool canSplat = useParticleSimulation && channelSplatReady && particlesBuffer != null
                    && channelRT0 != null && channelRT1 != null && channelRT2 != null;
                Debug.Log($"[SimDriver Display] gradient={useGradientRendering}, " +
                    $"channelSplat={channelSplatReady}, canSplat={canSplat}, " +
                    $"source={sourceTexture?.name ?? "null"}");

                // Verify gradient textures after ApplyToMaterial
                if (gradientPreset != null && gradientMaterial != null)
                {
                    gradientPreset.ApplyToMaterial(gradientMaterial);
                    string[] texNames = { "_FireGradientTex", "_WaterGradientTex", "_IceGradientTex" };
                    foreach (var tname in texNames)
                    {
                        var tex = gradientMaterial.GetTexture(tname);
                        Debug.Log($"[SimDriver Gradient] {tname}: {(tex != null ? $"{tex.width}x{tex.height}" : "NULL")}");
                    }

                    float ac = gradientMaterial.HasProperty("_AlphaCutoff") ? gradientMaterial.GetFloat("_AlphaCutoff") : -1f;
                    if (ac > 0.1f)
                    {
                        Debug.LogWarning($"[SimDriver] _AlphaCutoff={ac} is high — most ink detail will be clipped. " +
                            "Set to 0.01 in the gradient material Inspector for best results.");
                    }
                }
            }

            // Clear to opaque black every frame before rendering
            RenderTexture.active = displayRT;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = null;

            // Apply gradient rendering if enabled
            if (useGradientRendering && gradientMaterial != null && gradientPreset != null && !displayVelocity)
            {
                // Ensure gradient RT exists at display resolution (not sim resolution).
                // The gradient shader runs here, sampling sim-resolution channel textures
                // with hardware minification via mipmaps — this is where the downscale happens.
                if (gradientRT == null || gradientRT.width != effectiveDisplayRes)
                {
                    if (gradientRT != null) gradientRT.Release();
                    gradientRT = new RenderTexture(effectiveDisplayRes, effectiveDisplayRes, 0, RenderTextureFormat.ARGBHalf);
                    gradientRT.enableRandomWrite = false;
                    gradientRT.filterMode = FilterMode.Bilinear;
                    gradientRT.Create();
                }

                // Apply gradient preset to material
                gradientPreset.ApplyToMaterial(gradientMaterial);

                // Drive the _SHOWCHANNELS_ON keyword from the _ShowChannels float property
                // so the shader can switch between combined view and raw channel debug.
                float showChannels = gradientMaterial.HasProperty("_ShowChannels")
                    ? gradientMaterial.GetFloat("_ShowChannels")
                    : 0f;

                if (showChannels > 0.5f)
                {
                    gradientMaterial.EnableKeyword("_SHOWCHANNELS_ON");
                }
                else
                {
                    gradientMaterial.DisableKeyword("_SHOWCHANNELS_ON");
                }

                // Enable particle-authoritative rendering when channel splat textures are available.
                // Prefer downsampled copies (display resolution, 1:1 sampling) to avoid heavy
                // minification of 4K→1080 in the fragment shader. Fall back to mipped copies
                // (sim resolution with mipmaps) or raw UAV textures as last resort.
                if (useParticleSimulation && channelSplatReady && channelRT0 != null && channelRT1 != null && channelRT2 != null)
                {
                    gradientMaterial.EnableKeyword("_PARTICLEBUFFER_ON");

                    var ch0 = channelRT0Down ?? channelRT0Mipped ?? channelRT0;
                    var ch1 = channelRT1Down ?? channelRT1Mipped ?? channelRT1;
                    var ch2 = channelRT2Down ?? channelRT2Mipped ?? channelRT2;
                    gradientMaterial.SetTexture("_Channels0", ch0);
                    gradientMaterial.SetTexture("_Channels1", ch1);
                    gradientMaterial.SetTexture("_Channels2", ch2);
                }
                else
                {
                    gradientMaterial.DisableKeyword("_PARTICLEBUFFER_ON");
                }

                // Clear gradientRT to black to avoid residuals
                RenderTexture.active = gradientRT;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = null;

                // Blit through gradient material
                Graphics.Blit(sourceTexture, gradientRT, gradientMaterial);
                Graphics.Blit(gradientRT, displayRT);
            }
            else
            {
                // Direct blit without gradient
                // Clear to black background before blitting
                RenderTexture.active = displayRT;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = null;
                Graphics.Blit(sourceTexture, displayRT);
            }

            // displayRT is allocated at screen resolution (effectiveDisplayRes) without
            // enableRandomWrite or mipmaps — no GenerateMips needed. The minification
            // happens in the gradient shader via mipmapped channel textures.

            // Cleanup temporary RT
            if (compositeRT != null)
            {
                RenderTexture.ReleaseTemporary(compositeRT);
            }

            // Clear creature buffer for next frame
            if (creatureInkBuffer != null)
            {
                RenderTexture.active = creatureInkBuffer;
                GL.Clear(true, true, Color.clear);
                RenderTexture.active = null;
            }

            // Update display renderer if assigned
            if (displayRenderer != null)
            {
                displayRenderer.material.mainTexture = displayRT;
            }
        }

        /// <summary>
        /// Converts particle buffer to RGBA texture for display.
        /// Maps ink channels to RGB colors:
        /// - fire->red, water->green, ice->blue
        /// - black body->grayscale (desaturated)
        /// NOTE: This path is CPU-heavy and should only be used when explicitly enabled.
        /// </summary>
        private RenderTexture ConvertParticlesToTexture()
        {
            if (particlesBuffer == null || !useParticleDisplay) return null;

            RenderTexture tempRT = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGBHalf);
            int particleCount = resolution * resolution;
            Color[] colors = new Color[particleCount];

            if (gpuPromotesHalf)
            {
                var particles = new iparticle_gpu[particleCount];
                particlesBuffer[particleReadIndex].GetData(particles);
                for (int i = 0; i < particleCount; i++)
                {
                    var p = particles[i];
                    float r = p.fire, g = p.water, b = p.ice;
                    if (p.blackBody > 0) { float d = 1f - Mathf.Clamp01(p.blackBody); r *= d; g *= d; b *= d; }
                    colors[i] = new Color(r, g, b, Mathf.Max(p.fire, Mathf.Max(p.water, Mathf.Max(p.ice, p.blackBody))));
                }
            }
            else
            {
                var particles = new iparticle[particleCount];
                particlesBuffer[particleReadIndex].GetData(particles);
                for (int i = 0; i < particleCount; i++)
                {
                    var p = particles[i];
                    float r = p.fire, g = p.water, b = p.ice;
                    if (p.blackBody > 0) { float d = 1f - Mathf.Clamp01(p.blackBody); r *= d; g *= d; b *= d; }
                    colors[i] = new Color(r, g, b, Mathf.Max(p.fire, p.water, p.ice, p.blackBody));
                }
            }

            Texture2D temp = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
            temp.SetPixels(colors);
            temp.Apply();
            Graphics.Blit(temp, tempRT);
            Destroy(temp);

            return tempRT;
        }

        /// <summary>
        /// Converts ink type enum to color for shader injection.
        /// Colors are encoded into RGB channels that match the gradient shader:
        /// - Fire (R channel high)
        /// - Water (G channel high)
        /// - Metal (B channel high)
        /// </summary>
        private Color GetInkTypeColor(InkType type)
        {
            switch (type)
            {
                case InkType.Fire:
                    return new Color(1f, 0f, 0f, 1f);  // Red channel = Fire
                case InkType.Water:
                    return new Color(0f, 1f, 0f, 1f);  // Green channel = Water
                case InkType.Metal:
                    return new Color(0f, 0f, 1f, 1f);  // Blue channel = Metal
                case InkType.Electricity:
                    return new Color(0.5f, 0.5f, 1f, 1f);  // Blue-ish
                case InkType.Ice:
                    return new Color(0.7f, 0.9f, 1f, 1f);  // Cyan-ish
                case InkType.Plant:
                    return new Color(0.3f, 0.8f, 0.2f, 1f);  // Green-ish
                case InkType.Steam:
                    return new Color(0.9f, 0.9f, 0.9f, 0.7f);  // Light gray, semi-transparent
                case InkType.Dust:
                    return new Color(0.7f, 0.6f, 0.5f, 0.8f);  // Brown-ish
                default:
                    return Color.white;
            }
        }

        /// <summary>
        /// Maps SimDriver.InkType to iparticle field index (InkTypeId values).
        /// This routes particle injection to the correct field in the structured buffer.
        /// </summary>
        private int GetParticleFieldIndex(InkType type)
        {
            switch (type)
            {
                case InkType.Fire:        return 0;  // iparticle.fire
                case InkType.Water:       return 1;  // iparticle.water
                case InkType.Plant:       return 2;  // iparticle.plantSeeded
                case InkType.Metal:       return 6;  // iparticle.blackBody (repurposed)
                case InkType.Steam:       return 4;  // iparticle.steam
                case InkType.Dust:        return 5;  // iparticle.glitter
                case InkType.Electricity: return 7;  // iparticle.electricitySeeded
                case InkType.Ice:         return 9;  // iparticle.ice
                default:                  return 0;  // Fire fallback
            }
        }

        public RenderTexture GetDensityTexture()
        {
            if (useParticleRenderPass && displayRT != null)
            {
                return displayRT;
            }

            if (density != null && !useParticleDisplay)
            {
                return density.Read;
            }

            // Fallback to particle visualization when explicitly enabled
            return ConvertParticlesToTexture();
        }
        public RenderTexture GetVelocityTexture() => velocity?.Read;
        public RenderTexture GetDisplayTexture() => displayRT;
        public RenderTexture GetObstacleTexture() => obstacles;
        public ComputeBuffer GetParticleBuffer() => particlesBuffer?[particleReadIndex];

        public float GetLastFrameMs() => lastFrameMs;
        public (float adv, float diff, float press, float proj, float vort) GetDetailedTimings()
        {
            return (advectionMs, diffusionMs, pressureMs, projectionMs, vorticityMs);
        }

        // Explicit interface implementation for tuple element names
        (float advection, float diffusion, float pressure, float projection, float vorticity)
            ISimulationReader.GetDetailedTimings() => GetDetailedTimings();

        #region Service Accessors

        /// <summary>
        /// Returns this SimDriver as an ISimulationService for full read/write access.
        /// </summary>
        public ISimulationService AsService() => this;

        /// <summary>
        /// Returns this SimDriver as an ISimulationReader for read-only access.
        /// </summary>
        public ISimulationReader AsReader() => this;

        /// <summary>
        /// Returns this SimDriver as an ISimulationWriter for injection/stamping access.
        /// </summary>
        public ISimulationWriter AsWriter() => this;

        #endregion

        private void OnDestroy()
        {
              // Clean up render textures
              velocity?.Dispose();
              pressure?.Dispose();
              density?.Dispose();
              if (divergence) divergence.Release();
              if (vorticityTex) vorticityTex.Release();
              if (obstacles) obstacles.Release();
              if (displayRT) displayRT.Release();
              if (gradientRT) gradientRT.Release();
              if (channelRT0) channelRT0.Release();
              if (channelRT1) channelRT1.Release();
              if (channelRT2) channelRT2.Release();
              if (channelRT0Mipped) channelRT0Mipped.Release();
              if (channelRT1Mipped) channelRT1Mipped.Release();
              if (channelRT2Mipped) channelRT2Mipped.Release();
              if (channelRT0Down) channelRT0Down.Release();
              if (channelRT1Down) channelRT1Down.Release();
              if (channelRT2Down) channelRT2Down.Release();
              if (creatureInkBuffer) creatureInkBuffer.Release();

              // Clean up compute buffers
              if (particlesBuffer != null)
              {
                    for (int i = 0; i < particlesBuffer.Length; i++)
                    {
                          particlesBuffer[i]?.Release();
                    }
              }

              if (densityStampMaterial != null)
              {
                  Destroy(densityStampMaterial);
                  densityStampMaterial = null;
              }

        }

        private void RenderParticlesToDisplay()
        {
            if (particlesBuffer == null || particleToColorCompute == null || displayRT == null)
            {
                return;
            }

            // This compute path writes at sim resolution directly into the output texture.
            // Since displayRT is now at effectiveDisplayRes (screen-sized), the dispatch
            // and _Resolution must match the output texture, not the sim grid.
            // For now this path only works when displayRes == simRes.
            if (effectiveDisplayRes != resolution)
            {
                Debug.LogWarning("[SimDriver] RenderParticlesToDisplay skipped: " +
                    $"displayRes ({effectiveDisplayRes}) != simRes ({resolution}). " +
                    "Use gradient rendering path instead.");
                return;
            }

            int kernel = particleToColorCompute.FindKernel("ParticleToColor");

            // Ensure brightness is reasonable even if no preset is assigned
            float brightness = gradientPreset != null ? gradientPreset.globalBrightness : 1.0f;

            particleToColorCompute.SetInt("_Resolution", resolution);
            particleToColorCompute.SetFloat("_GlobalBrightness", brightness);
            particleToColorCompute.SetBuffer(kernel, "_ParticlesRead", particlesBuffer[particleReadIndex]);
            particleToColorCompute.SetTexture(kernel, "_Output", displayRT);

            int threadGroups = Mathf.CeilToInt(resolution / 8f);
            particleToColorCompute.Dispatch(kernel, threadGroups, threadGroups, 1);
        }

        private void OnGUI()
        {
            if (!Application.isEditor) return;

            int y = 10;

            if (measurePerformance)
            {
                GUI.Label(new Rect(10, y, 300, 20), $"=== SimDriver ({resolution}x{resolution}) ===");
                GUI.Label(new Rect(10, y + 20, 300, 20), $"Total: {lastFrameMs:F2}ms");
                GUI.Label(new Rect(10, y + 40, 300, 20), $"Advection: {advectionMs:F2}ms");
                if (diffusionMs > 0)
                    GUI.Label(new Rect(10, y + 60, 300, 20), $"Diffusion: {diffusionMs:F2}ms");
                GUI.Label(new Rect(10, y + 80, 300, 20), $"Pressure: {pressureMs:F2}ms");
                GUI.Label(new Rect(10, y + 100, 300, 20), $"Projection: {projectionMs:F2}ms");
                if (vorticityMs > 0)
                    GUI.Label(new Rect(10, y + 120, 300, 20), $"Vorticity: {vorticityMs:F2}ms");
                y += 140;
            }

            // Always show ink type and controls
            GUI.Label(new Rect(10, y, 400, 20), $"Current Ink: {currentInkType} (Press 1-8 to change)");
            GUI.Label(new Rect(10, y + 20, 400, 20), "1=Fire, 2=Water, 3=Metal, 4=Electric, 5=Ice, 6=Plant, 7=Steam, 8=Dust");
        }
    }
}
