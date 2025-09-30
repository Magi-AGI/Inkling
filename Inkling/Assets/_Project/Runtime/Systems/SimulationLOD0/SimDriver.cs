using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;
using Magi.UnityTools.Runtime.Core;

namespace Magi.Inkling.Runtime.Systems.SimulationLOD0
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
        [SerializeField] private float viscosity = 0.01f;
        [SerializeField] private float vorticity = 2.0f;
        [SerializeField] private float dissipation = 0.999f;  // Slower fade for density
        [SerializeField] private float velocityDissipation = 0.995f;  // Slower fade for velocity
        [SerializeField] private float timestep = 0.016f;

        // Public properties for metadata export
        public float Viscosity => viscosity;
        public float Vorticity => vorticity;
        public float Dissipation => dissipation;
        public float VelocityDissipation => velocityDissipation;
        public float Timestep => timestep;
        public int Resolution => resolution;

        [Header("Solver Settings")]
        [SerializeField] private int pressureIterations = 20;
        [SerializeField] private int diffusionIterations = 2;
        [SerializeField] private bool useRedBlackSolver = true; // Use faster Red-Black Gauss-Seidel

        [Header("Injection")]
        [SerializeField] private bool autoInject = true;
        [SerializeField] private float injectionForce = 500f;  // Increased for better visibility
        [SerializeField] private float densityAmount = 5.0f;    // More density
        [SerializeField] private float forceRadius = 30f;       // Larger injection area
        [SerializeField] private float forceStrength = 10f;     // Stronger forces
        [SerializeField] private Color injectionColor = Color.white;

        [Header("Display")]
        [SerializeField] private Renderer displayRenderer;
        [SerializeField] private bool displayVelocity = false;

        [Header("Performance")]
        [SerializeField] private bool measurePerformance = true;

        // Render textures (using PingPongRenderTexture from MagiUnityTools)
        private PingPongRenderTexture velocity;
        private PingPongRenderTexture density;
        private PingPongRenderTexture pressure;
        private RenderTexture divergence;
        private RenderTexture vorticityTex;
        private RenderTexture obstacles;
        private RenderTexture displayRT;

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
            density = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.ARGBHalf, "Density");
            pressure = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.RHalf, "Pressure");
            divergence = CreateRT(RenderTextureFormat.RHalf, "Divergence");
            vorticityTex = CreateRT(RenderTextureFormat.RHalf, "Vorticity");
            obstacles = CreateRT(RenderTextureFormat.RFloat, "Obstacles");

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

            // User input injection - use new Input System
            if (autoInject || (Mouse.current != null && Mouse.current.leftButton.isPressed))
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

            // Advect density
            fluidCompute.SetTexture(kernelAdvection, "_VelocityRead", velocity.Read);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityRead", density.Read);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityWrite", density.Write);
            fluidCompute.SetFloat("_Dissipation", dissipation);
            fluidCompute.Dispatch(kernelAdvection, threadGroups, threadGroups, 1);
            density.Swap();

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

            // Skip clearing pressure - the Clear kernel clears ALL fields which we don't want
            // TODO: Create a separate ClearPressure kernel that only clears pressure
            // For now, pressure will accumulate but should stabilize through Jacobi iterations

            // Clear pressure using PingPongRenderTexture's Clear method
            pressure.Clear(Color.clear);

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

            // Convert delta to UV space and scale
            Vector2 velocity = new Vector2(
                mouseDelta.x / Screen.width,
                mouseDelta.y / Screen.height
            ) * injectionForce;

            // Debug to verify injection position
            if (mouseDelta.magnitude > 0.1f || Mouse.current.leftButton.wasPressedThisFrame)
            {
                Debug.Log($"[SimDriver] Injecting at UV: {uv}, Velocity: {velocity.magnitude}");
            }

            // Inject force and density
            InjectForce(uv, velocity);
            InjectDensity(uv, injectionColor);
        }

        private void InjectForce(Vector2 position, Vector2 force)
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

        private void InjectDensity(Vector2 position, Color color)
        {
            if (fluidCompute == null) return;

            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            // Convert UV position to pixel coordinates
            Vector2 pixelPos = position * resolution;

            // Debug log to verify injection is happening (commented out to avoid spam)
            // Debug.Log($"Injecting density at {pixelPos} with amount {color.a * densityAmount}");

            fluidCompute.SetVector("_ForcePosition", pixelPos);
            fluidCompute.SetFloat("_ForceRadius", forceRadius);
            fluidCompute.SetFloat("_DensityAmount", color.a * densityAmount);
            fluidCompute.SetVector("_SimulationSize", new Vector2(resolution, resolution));
            fluidCompute.SetFloat("_DeltaTime", timestep);

            fluidCompute.SetTexture(kernelAddDensity, "_DensityRead", density.Read);
            fluidCompute.SetTexture(kernelAddDensity, "_DensityWrite", density.Write);
            fluidCompute.Dispatch(kernelAddDensity, threadGroups, threadGroups, 1);
            density.Swap();
        }

        private void UpdateDisplay()
        {
            // Blit density or velocity to display RT
            Graphics.Blit(displayVelocity ? velocity.Read : density.Read, displayRT);

            // Update display renderer if assigned
            if (displayRenderer != null)
            {
                displayRenderer.material.mainTexture = displayRT;
            }
        }

        // SwapBuffers method removed - using PingPongBuffer.Swap() instead

        public RenderTexture GetDensityTexture() => density?.Read;
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
            density?.Dispose();
            pressure?.Dispose();
            if (divergence) divergence.Release();
            if (vorticityTex) vorticityTex.Release();
            if (obstacles) obstacles.Release();
            if (displayRT) displayRT.Release();
        }

        private void OnGUI()
        {
            if (!measurePerformance || !Application.isEditor) return;

            int y = 10;
            GUI.Label(new Rect(10, y, 300, 20), $"=== SimDriver ({resolution}x{resolution}) ===");
            GUI.Label(new Rect(10, y + 20, 300, 20), $"Total: {lastFrameMs:F2}ms");
            GUI.Label(new Rect(10, y + 40, 300, 20), $"Advection: {advectionMs:F2}ms");
            if (diffusionMs > 0)
                GUI.Label(new Rect(10, y + 60, 300, 20), $"Diffusion: {diffusionMs:F2}ms");
            GUI.Label(new Rect(10, y + 80, 300, 20), $"Pressure: {pressureMs:F2}ms");
            GUI.Label(new Rect(10, y + 100, 300, 20), $"Projection: {projectionMs:F2}ms");
            if (vorticityMs > 0)
                GUI.Label(new Rect(10, y + 120, 300, 20), $"Vorticity: {vorticityMs:F2}ms");
        }
    }
}