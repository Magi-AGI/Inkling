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

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Drives the fluid simulation compute shader with ping-pong buffers and proper kernel dispatch order.
    /// Manages RT allocation, kernel execution, and display output.
    /// </summary>
    [DefaultExecutionOrder(50)] // Run after TexturedInjector (-50) so queued stamps are drained in SimulateFrame
    public class SimDriver : MonoBehaviour
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

        [Header("Creature / Stamp Rendering")]
        [SerializeField] private Shader densityStampShader;
        [Tooltip("Compute-shader stamp (preferred over Blit shader). Eliminates DX12 cross-queue barriers on density ping-pong buffers. If unassigned, stamps fall back to Graphics.Blit via densityStampShader.")]
        [SerializeField] private ComputeShader stampCompute;

        [Header("Particle Simulation")]
        [SerializeField] private bool useParticleSimulation = false;
        [Tooltip("When enabled (with useParticleSimulation), runs AdvectParticles each frame.")]
        [SerializeField] private bool useParticleAdvection = false;
        [Tooltip("When enabled (with useParticleSimulation), runs DissipateParticles each frame.")]
        [SerializeField] private bool useParticleDissipation = true;
        [Tooltip("Safety cap: particle kernels are skipped when resolution exceeds this value.")]
        [SerializeField] private int maxParticleSimResolution = 512;

        // Render textures (using PingPongRenderTexture from MagiUnityTools)
        private PingPongRenderTexture velocity;
        private PingPongRenderTexture pressure;
        private PingPongRenderTexture density;  // Used by some kernels (Clear, AddDensity) even though particles are primary
        private RenderTexture divergence;
        private RenderTexture vorticityTex;
        private RenderTexture obstacles;
        private RenderTexture displayRT;
        private RenderTexture gradientRT;  // For gradient-rendered output
        private RenderTexture creatureInkBuffer;  // Separate buffer for creature stamps (cleared each frame)

        // Particle-based density buffer (replaces RGBA texture with multi-channel iparticle)
        private ComputeBuffer[] particlesBuffer;  // Ping-pong buffer for iparticle structs
        private int particleReadIndex = 0;
        private int particleWriteIndex = 1;

        // Materials
        private Material densityStampMaterial;
        [Header("Particle Rendering")]
        [Tooltip("Compute shader that converts iparticle buffer to ARGB display texture. If unassigned, useParticleRenderPass has no effect and display falls through to density RT or CPU particle conversion.")]
        [SerializeField] private ComputeShader particleToColorCompute;
        [SerializeField] private bool useParticleRenderPass = false;

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
        private int kernelClear;
        private int kernelUpdateObstacles;
        private int kernelApplyObstacleBoundary;

        // Particle-based kernels
        private int kernelAdvectParticles;
        private int kernelDissipateParticles;
        private int kernelAddParticlesGaussian;

        // Stamp compute kernels (from StampDensityCompute.compute)
        private int kernelStampDensity;
        private int kernelClearBlackDensity;
        private bool stampComputeReady;

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
        }

        private struct PendingClearDensityMask
        {
            public Vector2 uvPosition;
            public Texture2D mask;
            public float blackLuminanceThreshold;
        }

        private readonly List<PendingDensityStamp> pendingDensityStamps = new List<PendingDensityStamp>();
        private readonly List<PendingForceInjection> pendingForceInjections = new List<PendingForceInjection>();
        private readonly List<PendingDensityInjection> pendingDensityInjections = new List<PendingDensityInjection>();
        private readonly List<PendingClearDensityMask> pendingClearDensityMasks = new List<PendingClearDensityMask>();

        private void Start()
        {
            InitializeSimulation();
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
                return;
            }

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

        private void InitializeSimulation()
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
            velocity = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.RGHalf, "Velocity");
            pressure = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.RHalf, "Pressure");
            density = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.ARGBHalf, "Density");
            divergence = CreateRT(RenderTextureFormat.RHalf, "Divergence");
            vorticityTex = CreateRT(RenderTextureFormat.RHalf, "Vorticity");
            obstacles = CreateRT(RenderTextureFormat.RFloat, "Obstacles");

            // Creature ink buffer (cleared each frame, composited with density before simulation)
            creatureInkBuffer = CreateRT(RenderTextureFormat.ARGBHalf, "CreatureInk");

            // Particle buffer (replaces density RenderTexture)
            int particleCount = resolution * resolution;
            int stride = Marshal.SizeOf<iparticle>();
            particlesBuffer = new ComputeBuffer[2];
            for (int i = 0; i < 2; i++)
            {
                  particlesBuffer[i] = new ComputeBuffer(particleCount, stride, ComputeBufferType.Default);
            }

            Debug.Log($"[SimDriver] Allocated particle buffer: {particleCount} particles, stride {stride} bytes");

            // Display output (used as UAV by particle render pass)
            displayRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32);
            displayRT.enableRandomWrite = true;
            displayRT.filterMode = FilterMode.Bilinear;
            displayRT.wrapMode = TextureWrapMode.Clamp;
            displayRT.name = "DisplayRT";
            displayRT.Create();

        }

        private RenderTexture CreateRT(RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(resolution, resolution, 0, format);
            rt.enableRandomWrite = true;
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

            // Per-channel dissipation rates
            fluidCompute.SetFloat("_DissipationFire", 0.995f);
            fluidCompute.SetFloat("_DissipationWater", 0.998f);
            fluidCompute.SetFloat("_DissipationPlant", 0.997f);
            fluidCompute.SetFloat("_DissipationSteam", 0.990f);
            fluidCompute.SetFloat("_DissipationGlitter", 0.999f);
            fluidCompute.SetFloat("_DissipationBlackBody", 0.5f);  // Black body dissipates VERY quickly
            fluidCompute.SetFloat("_DissipationElectricity", 0.985f);
            fluidCompute.SetFloat("_DissipationIce", 0.996f);

            // Additional useful parameters
            fluidCompute.SetVector("_TexelSize", new Vector4(1f / resolution, 1f / resolution, resolution, resolution));
        }

        private void ClearBuffers()
        {
            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            fluidCompute.SetTexture(kernelClear, "_VelocityWrite", velocity.Write);
            fluidCompute.SetTexture(kernelClear, "_DensityWrite", density.Write);
            fluidCompute.SetTexture(kernelClear, "_PressureWrite", pressure.Write);
            fluidCompute.SetTexture(kernelClear, "_DivergenceWrite", divergence);
            fluidCompute.SetTexture(kernelClear, "_VorticityMag", vorticityTex);

            // Clear particle buffer with zero data
            int particleCount = resolution * resolution;
            iparticle[] zeroParticles = new iparticle[particleCount];
            // Array is already zero-initialized in C#
            particlesBuffer[particleReadIndex].SetData(zeroParticles);
            particlesBuffer[particleWriteIndex].SetData(zeroParticles);

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
            // Only inject if mouse is actually moving OR button was just pressed
            bool shouldInject = false;
            if (Mouse.current != null && Mouse.current.leftButton.isPressed)
            {
                Vector2 mouseDelta = Mouse.current.delta.ReadValue();
                shouldInject = mouseDelta.magnitude > 0.01f || Mouse.current.leftButton.wasPressedThisFrame;
            }

            if (shouldInject || autoInject)
            {
                InjectAtMousePosition();
            }

            // Run simulation pipeline
            SimulateFrame();

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
            UpdateDisplay();
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
                pendingDensityStamps.Clear();
            }

            // ── Clear-density masks ──────────────────────────────────────────
            if (pendingClearDensityMasks.Count > 0 && density != null)
            {
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

                    if (useParticleDissipation && kernelDissipateParticles != 0)
                    {
                        fluidCompute.SetBuffer(kernelDissipateParticles, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                        fluidCompute.SetBuffer(kernelDissipateParticles, "_ParticlesWrite", particlesBuffer[particleWriteIndex]);
                        fluidCompute.Dispatch(kernelDissipateParticles, threadGroups, threadGroups, 1);
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

                // Apply vorticity confinement
                fluidCompute.SetTexture(kernelVorticityConfinement, "_VelocityRead", velocity.Read);
                fluidCompute.SetTexture(kernelVorticityConfinement, "_VelocityWrite", velocity.Write);
                fluidCompute.SetTexture(kernelVorticityConfinement, "_VorticityMag", vorticityTex);
                fluidCompute.Dispatch(kernelVorticityConfinement, threadGroups, threadGroups, 1);
                velocity.Swap();

                if (sw != null) vorticityMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 4. Pressure projection (ensure divergence-free velocity)
            if (sw != null) sw.Restart();

            // Calculate divergence
            fluidCompute.SetTexture(kernelDivergence, "_VelocityRead", velocity.Read);
            fluidCompute.SetTexture(kernelDivergence, "_DivergenceWrite", divergence);
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
        }

        private void InjectAtMousePosition()
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
            Color inkColor = GetInkTypeColor(currentInkType);
            InjectDensity(uv, inkColor);
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
        public void InjectDensity(Vector2 position, Color color)
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
                color = color
            });

            // Mirror this injection into the particle buffer so iparticle remains authoritative.
            // This is CPU-only and safe to run immediately.
            InjectParticlesAtPoint(position, color);
        }

        /// <summary>
        /// CPU helper: inject ink at a single UV position into the iparticle buffer
        /// using a simple circular radius and mapping RGB to fire/water/ice channels.
        /// </summary>
        private void InjectParticlesAtPoint(Vector2 position, Color color)
        {
            if (particlesBuffer == null)
            {
                return;
            }

            int radiusPixels = Mathf.RoundToInt(forceRadius);
            if (radiusPixels <= 0)
            {
                return;
            }

            int centerX = Mathf.RoundToInt(position.x * resolution);
            int centerY = Mathf.RoundToInt(position.y * resolution);

            int particleCount = resolution * resolution;
            iparticle[] particles = new iparticle[particleCount];
            particlesBuffer[0].GetData(particles);

            float rMul = color.r * densityAmount;
            float gMul = color.g * densityAmount;
            float bMul = color.b * densityAmount;

            int minX = Mathf.Max(0, centerX - radiusPixels);
            int maxX = Mathf.Min(resolution - 1, centerX + radiusPixels);
            int minY = Mathf.Max(0, centerY - radiusPixels);
            int maxY = Mathf.Min(resolution - 1, centerY + radiusPixels);

            float radiusSqr = radiusPixels * radiusPixels;

            for (int y = minY; y <= maxY; y++)
            {
                int dy = y - centerY;
                for (int x = minX; x <= maxX; x++)
                {
                    int dx = x - centerX;
                    if (dx * dx + dy * dy > radiusSqr)
                    {
                        continue;
                    }

                    int idx = y * resolution + x;

                    // Add ink proportionally; this mirrors the RT-based injection in a simple way.
                    particles[idx].fire += (half)rMul;
                    particles[idx].water += (half)gMul;
                    particles[idx].ice  += (half)bMul;
                }
            }

            // Keep both ping-pong buffers in sync while particle kernels are disabled.
            for (int i = 0; i < particlesBuffer.Length; i++)
            {
                particlesBuffer[i].SetData(particles);
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

        /// <summary>
        /// CPU path: stamp a texture into the particle buffer (iparticle) so that
        /// multi-ink interactions can be driven from the canonical particle state.
        /// This is a transitional implementation and may be replaced by a GPU kernel.
        /// </summary>
        public void StampParticles(Vector2 uvPosition, Texture2D stamp)
        {
            if (particlesBuffer == null || stamp == null) return;

            int stampWidth = stamp.width;
            int stampHeight = stamp.height;
            Color[] stampPixels = stamp.GetPixels();

            int centerX = Mathf.RoundToInt(uvPosition.x * resolution);
            int centerY = Mathf.RoundToInt(uvPosition.y * resolution);
            int startX = centerX - stampWidth / 2;
            int startY = centerY - stampHeight / 2;

            int particleCount = resolution * resolution;
            iparticle[] particles = new iparticle[particleCount];

            // Read from first buffer; we will write back to all buffers to keep them in sync.
            particlesBuffer[0].GetData(particles);

            for (int y = 0; y < stampHeight; y++)
            {
                for (int x = 0; x < stampWidth; x++)
                {
                    int targetX = startX + x;
                    int targetY = startY + y;

                    if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution)
                        continue;

                    Color stampColor = stampPixels[y * stampWidth + x];
                    if (stampColor.a < 0.01f)
                        continue;

                    int targetIdx = targetY * resolution + targetX;

                    // Map RGB channels into iparticle ink channels.
                    // Assumes stampColor already includes any density multiplier.
                    particles[targetIdx].fire += (half)(stampColor.r * stampColor.a);
                    particles[targetIdx].water += (half)(stampColor.g * stampColor.a);
                    particles[targetIdx].ice  += (half)(stampColor.b * stampColor.a);
                }
            }

            // Write back to all ping-pong buffers to keep them identical until
            // particle kernels are re-enabled.
            for (int i = 0; i < particlesBuffer.Length; i++)
            {
                particlesBuffer[i].SetData(particles);
            }
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
        /// CPU path: stamp black/body ink into the iparticle buffer based on a mask texture.
        /// Black pixels (by luminance) are written into the blackBody channel so that
        /// gradient-based rendering can treat them as an overriding ink layer.
        /// </summary>
        /// <param name="uvPosition">Center position in UV space (0-1)</param>
        /// <param name="mask">Source mask texture</param>
        /// <param name="alphaThreshold">Minimum alpha for a pixel to be considered</param>
        /// <param name="blackLuminanceThreshold">Luminance threshold for "black" classification</param>
        public void StampBlackBody(Vector2 uvPosition, Texture2D mask, float alphaThreshold, float blackLuminanceThreshold)
        {
            if (particlesBuffer == null || mask == null) return;

            int maskWidth = mask.width;
            int maskHeight = mask.height;
            Color[] maskPixels = mask.GetPixels();

            int centerX = Mathf.RoundToInt(uvPosition.x * resolution);
            int centerY = Mathf.RoundToInt(uvPosition.y * resolution);
            int startX = centerX - maskWidth / 2;
            int startY = centerY - maskHeight / 2;

            int particleCount = resolution * resolution;
            iparticle[] particles = new iparticle[particleCount];

            // Read from first buffer; write back to all to keep them in sync
            particlesBuffer[0].GetData(particles);

            for (int y = 0; y < maskHeight; y++)
            {
                for (int x = 0; x < maskWidth; x++)
                {
                    int targetX = startX + x;
                    int targetY = startY + y;

                    if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution)
                        continue;

                    Color maskColor = maskPixels[y * maskWidth + x];
                    if (maskColor.a < alphaThreshold)
                        continue;

                    float luminance = 0.299f * maskColor.r + 0.587f * maskColor.g + 0.114f * maskColor.b;
                    bool isBlack = luminance < blackLuminanceThreshold;
                    if (!isBlack)
                        continue;

                    int targetIdx = targetY * resolution + targetX;

                    // Mark black body presence; clamp to 1.0
                    float current = (float)particles[targetIdx].blackBody;
                    float updated = Mathf.Clamp01(current + maskColor.a);
                    particles[targetIdx].blackBody = (half)updated;
                }
            }

            for (int i = 0; i < particlesBuffer.Length; i++)
            {
                particlesBuffer[i].SetData(particles);
            }
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
            iparticle[] particles = new iparticle[particleCount];
            particlesBuffer[particleReadIndex].GetData(particles);

            bool particlesModified = false;

            // Stamp obstacles and clear particles
            for (int y = 0; y < stampHeight; y++)
            {
                for (int x = 0; x < stampWidth; x++)
                {
                    int targetX = startX + x;
                    int targetY = startY + y;

                    if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution)
                        continue;

                    Color stampColor = stampPixels[y * stampWidth + x];
                    if (stampColor.a < 0.01f)
                        continue;

                    int targetIdx = targetY * resolution + targetX;

                    // Mark as obstacle (1.0 = solid obstacle)
                    obstaclePixels[targetIdx] = new Color(1f, 0, 0, 0);  // R channel = obstacle

                    // Clear all ink channels at obstacle positions (displaces existing inks)
                    particles[targetIdx] = new iparticle();  // Zero all channels
                    particlesModified = true;
                }
            }

            // Write back obstacles
            tempObstacles.SetPixels(obstaclePixels);
            tempObstacles.Apply();
            Graphics.Blit(tempObstacles, obstacles);

            // Write back particles if modified
              if (particlesModified)
              {
                  particlesBuffer[particleWriteIndex].SetData(particles);
              }

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

            // Apply gradient rendering if enabled
            if (useGradientRendering && gradientMaterial != null && gradientPreset != null && !displayVelocity)
            {
                // Ensure gradient RT exists
                if (gradientRT == null || gradientRT.width != resolution)
                {
                    if (gradientRT != null) gradientRT.Release();
                    gradientRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBHalf);
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

                // Provide particle buffer and resolution for direct gradient sampling
                if (particlesBuffer != null)
                {
                    gradientMaterial.SetBuffer("_ParticlesRead", particlesBuffer[particleReadIndex]);
                    gradientMaterial.SetInt("_ParticleResolution", resolution);
                }

                // Blit through gradient material
                Graphics.Blit(sourceTexture, gradientRT, gradientMaterial);
                Graphics.Blit(gradientRT, displayRT);
            }
            else
            {
                // Direct blit without gradient
                Graphics.Blit(sourceTexture, displayRT);
            }

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

            // Create temporary RT
            RenderTexture tempRT = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGBHalf);

            // Read particles
            int particleCount = resolution * resolution;
            iparticle[] particles = new iparticle[particleCount];
            particlesBuffer[particleReadIndex].GetData(particles);

            // Convert to colors
            Color[] colors = new Color[particleCount];
            for (int i = 0; i < particleCount; i++)
            {
                iparticle p = particles[i];

                // Base color from RGB ink channels
                float r = p.fire;  // Fire -> red
                float g = p.water;  // Water -> green
                float b = p.ice;  // Ice -> blue

                // Black body ink darkens the pixel (subtractive)
                // When blackBody is present, reduce RGB proportionally
                if (p.blackBody > 0)
                {
                    float darken = 1.0f - Mathf.Clamp01(p.blackBody);
                    r *= darken;
                    g *= darken;
                    b *= darken;
                }

                // Alpha is max of all channels
                float a = Mathf.Max(p.fire, p.water, p.ice, p.blackBody);

                colors[i] = new Color(r, g, b, a);
            }

            // Upload to texture
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

        public float GetLastFrameMs() => lastFrameMs;
        public (float adv, float diff, float press, float proj, float vort) GetDetailedTimings()
        {
            return (advectionMs, diffusionMs, pressureMs, projectionMs, vorticityMs);
        }

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
