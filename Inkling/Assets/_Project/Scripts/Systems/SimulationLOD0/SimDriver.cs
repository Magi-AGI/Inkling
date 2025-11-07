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
        [SerializeField] private bool useRedBlackSolver = true; // Use faster Red-Black Gauss-Seidel

        [Header("Injection")]
        [SerializeField] private bool autoInject = false;  // Disable auto-inject by default
        [SerializeField] private float injectionForce = 100f;  // Direct velocity multiplier
#pragma warning disable 0414 // Field assigned but never used - reserved for future use
        [SerializeField] private float densityAmount = 10.0f;    // More density
#pragma warning restore 0414
        [SerializeField] private float forceRadius = 40f;       // Larger injection area
        [SerializeField] private float forceStrength = 50f;     // Force gets multiplied by velocity magnitude
        [SerializeField] private Color injectionColor = Color.white;

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
            Dust = 7
        }

        [Header("Display")]
        [SerializeField] private Renderer displayRenderer;
        [SerializeField] private bool displayVelocity = false;
        [SerializeField] private bool useGradientRendering = true;
        [SerializeField] private Magi.Inkling.Systems.Rendering.InkGradientPreset gradientPreset;
        [SerializeField] private Material gradientMaterial;

        [Header("Performance")]
        [SerializeField] private bool measurePerformance = true;

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

        // Performance tracking
        private Stopwatch stopwatch = new Stopwatch();
        private float lastFrameMs;
        private float advectionMs, diffusionMs, pressureMs, projectionMs, vorticityMs;

        private void Start()
        {
            InitializeSimulation();
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

            // Display output
            displayRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32);
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

            // Update display
            UpdateDisplay();

            if (measurePerformance)
            {
                lastFrameMs = (float)stopwatch.Elapsed.TotalMilliseconds;
            }
        }

        private void SimulateFrame()
        {
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

            // Advect velocity
            fluidCompute.SetTexture(kernelAdvection, "_VelocityRead", velocity.Read);
            fluidCompute.SetTexture(kernelAdvection, "_VelocityWrite", velocity.Write);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityRead", velocity.Read);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityWrite", velocity.Write);
            fluidCompute.SetFloat("_Dissipation", velocityDissipation);
            fluidCompute.Dispatch(kernelAdvection, threadGroups, threadGroups, 1);
            velocity.Swap();

            // TEMPORARY: Particle kernels disabled - they cause GPU hangs (DXGI_ERROR_DEVICE_HUNG)
            // TODO: Debug and re-enable particle simulation
            /*
            // Advect particles (multi-channel density)
            if (kernelAdvectParticles != 0)
            {
                fluidCompute.SetBuffer(kernelAdvectParticles, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                fluidCompute.SetBuffer(kernelAdvectParticles, "_ParticlesWrite", particlesBuffer[particleWriteIndex]);
                fluidCompute.SetTexture(kernelAdvectParticles, "_VelocityRead", velocity.Read);
                fluidCompute.Dispatch(kernelAdvectParticles, threadGroups, threadGroups, 1);
                SwapParticleBuffers();
            }

            // Dissipate particles (per-channel dissipation rates)
            if (kernelDissipateParticles != 0)
            {
                fluidCompute.SetBuffer(kernelDissipateParticles, "_ParticlesRead", particlesBuffer[particleReadIndex]);
                fluidCompute.SetBuffer(kernelDissipateParticles, "_ParticlesWrite", particlesBuffer[particleWriteIndex]);
                fluidCompute.Dispatch(kernelDissipateParticles, threadGroups, threadGroups, 1);
                SwapParticleBuffers();
            }
            */

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
            // TEMPORARILY DISABLE Red-Black solver due to in-place write issues
            // Use standard Jacobi instead
            bool useJacobi = true;  // Force Jacobi for now
            if (!useJacobi && useRedBlackSolver && kernelPressureRedBlack != 0)
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

            // Debug to verify injection position
            if (mouseDelta.magnitude > 0.1f || Mouse.current.leftButton.wasPressedThisFrame)
            {
                Vector2 pixelPos = uv * resolution;
                float expectedVelAfterForce = velocity.magnitude * forceStrength * timestep;
                Debug.Log($"[SimDriver] Injecting at UV: {uv} (pixel: {pixelPos}), Mouse Delta: {mouseDelta}, Velocity (pixels/frame): {velocity} (mag: {velocity.magnitude}), Expected velocity after force injection: {expectedVelAfterForce} pixels, ForceStrength: {forceStrength}, InjectionForce: {injectionForce}");
            }

            // Inject force and density with ink type color
            InjectForce(uv, velocity);
            Color inkColor = GetInkTypeColor(currentInkType);
            InjectDensity(uv, inkColor);

            // Debug: Log the parameters being set
            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                Debug.Log($"[SimDriver] Simulation params - Resolution: {resolution}, Timestep: {timestep}, Viscosity: {viscosity}, Vorticity: {vorticity}, VelDissipation: {velocityDissipation}, PressureIterations: {pressureIterations}");
            }
        }

        /// <summary>
        /// Public API: Inject force at UV position (0-1 range)
        /// </summary>
        public void InjectForce(Vector2 position, Vector2 force)
        {
            if (fluidCompute == null) return;

            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            // Convert UV position to pixel coordinates
            Vector2 pixelPos = position * resolution;

            fluidCompute.SetVector("_ForcePosition", pixelPos);
            fluidCompute.SetVector("_ForceDirection", force.normalized);
            fluidCompute.SetFloat("_ForceRadius", forceRadius);
            fluidCompute.SetFloat("_ForceStrength", forceStrength * force.magnitude);
            fluidCompute.SetFloat("_DeltaTime", timestep);
            fluidCompute.SetVector("_SimulationSize", new Vector2(resolution, resolution));

            fluidCompute.SetTexture(kernelAddForce, "_VelocityRead", velocity.Read);
            fluidCompute.SetTexture(kernelAddForce, "_VelocityWrite", velocity.Write);
            fluidCompute.Dispatch(kernelAddForce, threadGroups, threadGroups, 1);
            velocity.Swap();
        }

        /// <summary>
        /// Public API: Inject density at UV position (0-1 range) with Gaussian falloff radius
        /// Color is mapped to ink channels in the iparticle structure:
        /// - Red channel -> Fire ink (f)
        /// - Green channel -> Water ink (w)
        /// - Blue channel -> Ice ink (i)
        /// </summary>
        public void InjectDensity(Vector2 position, Color color)
        {
            // TODO: This needs to be updated to work with particle buffer and compute shader
            // For now, just log that injection was requested
            if (Time.frameCount % 120 == 0 && color.a > 0.1f)
            {
                Vector2 pixelPos = position * resolution;
                Debug.Log($"[SimDriver] InjectDensity requested at pixel {pixelPos:F1} (UV {position:F3}), " +
                         $"color RGBA({color.r:F2},{color.g:F2},{color.b:F2},{color.a:F2}) - NEEDS PARTICLE BUFFER IMPL");
            }
        }

        /// <summary>
        /// Public API: Directly write pixels to particle buffer without Gaussian falloff.
        /// Use this for precise, sharp injection like colored creature fills.
        /// Maps texture colors to ink channels:
        /// - Black pixels (luminance < 0.2) -> bb (black body) channel
        /// - Colored pixels: R->fire, G->water, B->ice
        /// </summary>
        /// <param name="uvPosition">Center position in UV space (0-1)</param>
        /// <param name="stamp">Texture to stamp directly into particle buffer</param>
        public void StampDensity(Vector2 uvPosition, Texture2D stamp)
        {
            if (particlesBuffer == null || stamp == null) return;

            int stampWidth = stamp.width;
            int stampHeight = stamp.height;
            Color[] stampPixels = stamp.GetPixels();

            int centerX = Mathf.RoundToInt(uvPosition.x * resolution);
            int centerY = Mathf.RoundToInt(uvPosition.y * resolution);
            int startX = centerX - stampWidth / 2;
            int startY = centerY - stampHeight / 2;

            // Read current particles
            int particleCount = resolution * resolution;
            iparticle[] particles = new iparticle[particleCount];
            particlesBuffer[particleReadIndex].GetData(particles);

            // Stamp pixels to particle channels
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

                    // Determine if pixel is black (use luminance calculation)
                    float luminance = 0.299f * stampColor.r + 0.587f * stampColor.g + 0.114f * stampColor.b;
                    bool isBlack = luminance < 0.2f;

                    if (isBlack)
                    {
                        // Black pixels go to black body channel
                        particles[targetIdx].blackBody += (half)stampColor.a;  // Black body from black pixels
                    }
                    else
                    {
                        // Colored pixels map RGB channels to ink types (additive)
                        particles[targetIdx].fire += (half)(stampColor.r * stampColor.a);  // Fire from red
                        particles[targetIdx].water += (half)(stampColor.g * stampColor.a);  // Water from green
                        particles[targetIdx].ice += (half)(stampColor.b * stampColor.a);  // Ice from blue
                    }
                }
            }

            // Write back
            particlesBuffer[particleWriteIndex].SetData(particles);

            // Swap particle buffers
            SwapParticleBuffers();
        }

        private void SwapParticleBuffers()
        {
            int temp = particleReadIndex;
            particleReadIndex = particleWriteIndex;
            particleWriteIndex = temp;
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
                SwapParticleBuffers();
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

            if (displayVelocity)
            {
                sourceTexture = velocity.Read;
            }
            else
            {
                // Convert particle buffer to RGBA texture for display
                sourceTexture = ConvertParticlesToTexture();
            }

            // COMPOSITE CREATURE INK FOR DISPLAY ONLY
            // Creature buffer is NOT added to particles (doesn't persist in simulation)
            // It's only composited visually for display
            RenderTexture compositeRT = null;
            if (creatureInkBuffer != null && !displayVelocity)
            {
                compositeRT = RenderTexture.GetTemporary(resolution, resolution, 0, RenderTextureFormat.ARGBHalf);

                // Read particle-based density
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
        /// </summary>
        private RenderTexture ConvertParticlesToTexture()
        {
            if (particlesBuffer == null) return null;

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

        // SwapBuffers method removed - using PingPongBuffer.Swap() instead

        public RenderTexture GetDensityTexture() => ConvertParticlesToTexture();  // Converts particles to RGBA texture
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