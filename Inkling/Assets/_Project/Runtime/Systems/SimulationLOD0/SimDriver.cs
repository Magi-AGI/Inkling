using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace Magi.Inkling.Runtime.Systems.SimulationLOD0
{
    /// <summary>
    /// Drives the fluid simulation compute shader with ping-pong buffers and proper kernel dispatch order.
    /// Manages RT allocation, kernel execution, and display output.
    /// </summary>
    public class SimDriver : MonoBehaviour
    {
        [Header("Compute Shader")]
        [SerializeField] private ComputeShader fluidCompute;

        [Header("Simulation Parameters")]
        [SerializeField] private int resolution = 256;
        [SerializeField] private float viscosity = 0.01f;
        [SerializeField] private float vorticity = 2.0f;
        [SerializeField] private float dissipation = 0.98f;
        [SerializeField] private float velocityDissipation = 0.99f;
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

        [Header("Injection")]
        [SerializeField] private bool autoInject = true;
        [SerializeField] private float injectionRadius = 0.05f;
        [SerializeField] private float injectionForce = 100f;
        [SerializeField] private Color injectionColor = Color.white;

        [Header("Display")]
        [SerializeField] private Renderer displayRenderer;
        [SerializeField] private bool displayVelocity = false;

        [Header("Performance")]
        [SerializeField] private bool measurePerformance = true;

        // Render textures (ping-pong buffers)
        private RenderTexture velocityA, velocityB;
        private RenderTexture densityA, densityB;
        private RenderTexture pressure, divergence;
        private RenderTexture vorticityTex;
        private RenderTexture displayRT;

        // Kernel indices
        private int kernelAdvection;
        private int kernelDiffusion;
        private int kernelDivergence;
        private int kernelPressure;
        private int kernelSubtractGradient;
        private int kernelVorticity;
        private int kernelVorticityConfinement;
        private int kernelAddForce;
        private int kernelAddDensity;
        private int kernelClear;

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
            }

            Debug.Log($"[SimDriver] Initialized {resolution}x{resolution} simulation");
        }

        private void AllocateRenderTextures()
        {
            // Velocity buffers (RG for X/Y components)
            velocityA = CreateRT(RenderTextureFormat.RGHalf, "VelocityA");
            velocityB = CreateRT(RenderTextureFormat.RGHalf, "VelocityB");

            // Density buffers (RGBA for multiple ink types or color channels)
            densityA = CreateRT(RenderTextureFormat.ARGBHalf, "DensityA");
            densityB = CreateRT(RenderTextureFormat.ARGBHalf, "DensityB");

            // Solver buffers
            pressure = CreateRT(RenderTextureFormat.RHalf, "Pressure");
            divergence = CreateRT(RenderTextureFormat.RHalf, "Divergence");
            vorticityTex = CreateRT(RenderTextureFormat.RHalf, "Vorticity");

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
            fluidCompute.SetInt("_Resolution", resolution);
            fluidCompute.SetFloat("_DeltaTime", timestep);
            fluidCompute.SetFloat("_Viscosity", viscosity);
            fluidCompute.SetFloat("_Vorticity", vorticity);
            fluidCompute.SetFloat("_Dissipation", dissipation);
            fluidCompute.SetFloat("_VelocityDissipation", velocityDissipation);
            fluidCompute.SetVector("_TexelSize", new Vector4(1f / resolution, 1f / resolution, resolution, resolution));
        }

        private void ClearBuffers()
        {
            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            fluidCompute.SetTexture(kernelClear, "_VelocityWrite", velocityA);
            fluidCompute.SetTexture(kernelClear, "_DensityWrite", densityA);
            fluidCompute.SetTexture(kernelClear, "_PressureWrite", pressure);
            fluidCompute.Dispatch(kernelClear, threadGroups, threadGroups, 1);
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

            // 1. Advection - Move quantities along velocity field
            if (sw != null) sw.Restart();

            // Advect velocity
            fluidCompute.SetTexture(kernelAdvection, "_VelocityRead", velocityA);
            fluidCompute.SetTexture(kernelAdvection, "_VelocityWrite", velocityB);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityRead", velocityA);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityWrite", velocityB);
            fluidCompute.SetFloat("_Dissipation", velocityDissipation);
            fluidCompute.Dispatch(kernelAdvection, threadGroups, threadGroups, 1);
            SwapBuffers(ref velocityA, ref velocityB);

            // Advect density
            fluidCompute.SetTexture(kernelAdvection, "_VelocityRead", velocityA);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityRead", densityA);
            fluidCompute.SetTexture(kernelAdvection, "_QuantityWrite", densityB);
            fluidCompute.SetFloat("_Dissipation", dissipation);
            fluidCompute.Dispatch(kernelAdvection, threadGroups, threadGroups, 1);
            SwapBuffers(ref densityA, ref densityB);

            if (sw != null) advectionMs = (float)sw.Elapsed.TotalMilliseconds;

            // 2. Diffusion (optional, for high viscosity)
            if (viscosity > 0.001f && diffusionIterations > 0)
            {
                if (sw != null) sw.Restart();

                for (int i = 0; i < diffusionIterations; i++)
                {
                    fluidCompute.SetTexture(kernelDiffusion, "_VelocityRead", velocityA);
                    fluidCompute.SetTexture(kernelDiffusion, "_VelocityWrite", velocityB);
                    fluidCompute.Dispatch(kernelDiffusion, threadGroups, threadGroups, 1);
                    SwapBuffers(ref velocityA, ref velocityB);
                }

                if (sw != null) diffusionMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 3. Vorticity confinement (adds swirling motion)
            if (vorticity > 0)
            {
                if (sw != null) sw.Restart();

                // Calculate vorticity
                fluidCompute.SetTexture(kernelVorticity, "_VelocityRead", velocityA);
                fluidCompute.SetTexture(kernelVorticity, "_VorticityMag", vorticityTex);
                fluidCompute.Dispatch(kernelVorticity, threadGroups, threadGroups, 1);

                // Apply vorticity confinement
                fluidCompute.SetTexture(kernelVorticityConfinement, "_VelocityRead", velocityA);
                fluidCompute.SetTexture(kernelVorticityConfinement, "_VelocityWrite", velocityB);
                fluidCompute.SetTexture(kernelVorticityConfinement, "_VorticityMag", vorticityTex);
                fluidCompute.Dispatch(kernelVorticityConfinement, threadGroups, threadGroups, 1);
                SwapBuffers(ref velocityA, ref velocityB);

                if (sw != null) vorticityMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 4. Pressure projection (ensure divergence-free velocity)
            if (sw != null) sw.Restart();

            // Calculate divergence
            fluidCompute.SetTexture(kernelDivergence, "_VelocityRead", velocityA);
            fluidCompute.SetTexture(kernelDivergence, "_DivergenceWrite", divergence);
            fluidCompute.Dispatch(kernelDivergence, threadGroups, threadGroups, 1);

            // Clear pressure
            fluidCompute.SetTexture(kernelClear, "_PressureWrite", pressure);
            fluidCompute.Dispatch(kernelClear, threadGroups, threadGroups, 1);

            // Jacobi iterations for pressure
            for (int i = 0; i < pressureIterations; i++)
            {
                fluidCompute.SetTexture(kernelPressure, "_PressureRead", pressure);
                fluidCompute.SetTexture(kernelPressure, "_DivergenceRead", divergence);
                fluidCompute.SetTexture(kernelPressure, "_PressureWrite", pressure);
                fluidCompute.Dispatch(kernelPressure, threadGroups, threadGroups, 1);
            }

            if (sw != null) pressureMs = (float)sw.Elapsed.TotalMilliseconds;

            // 5. Subtract pressure gradient (make velocity divergence-free)
            if (sw != null) sw.Restart();

            fluidCompute.SetTexture(kernelSubtractGradient, "_VelocityRead", velocityA);
            fluidCompute.SetTexture(kernelSubtractGradient, "_VelocityWrite", velocityB);
            fluidCompute.SetTexture(kernelSubtractGradient, "_PressureRead", pressure);
            fluidCompute.Dispatch(kernelSubtractGradient, threadGroups, threadGroups, 1);
            SwapBuffers(ref velocityA, ref velocityB);

            if (sw != null) projectionMs = (float)sw.Elapsed.TotalMilliseconds;
        }

        private void InjectAtMousePosition()
        {
            if (Camera.main == null) return;

            Vector3 mousePos = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector3.zero;
            mousePos.z = 10f;
            Vector3 worldPos = Camera.main.ScreenToWorldPoint(mousePos);

            // Convert to UV space (0-1)
            Vector2 uv = new Vector2(
                (worldPos.x + 5f) / 10f,  // Assuming -5 to 5 world space
                (worldPos.y + 5f) / 10f
            );

            // Calculate velocity from mouse delta - use new Input System
            Vector2 mouseDelta = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
            Vector2 velocity = mouseDelta * injectionForce * 0.01f; // Scale down delta as it's in pixels

            // Inject force and density
            InjectForce(uv, velocity);
            InjectDensity(uv, injectionColor);
        }

        private void InjectForce(Vector2 position, Vector2 force)
        {
            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            fluidCompute.SetVector("_InjectionPoint", new Vector4(position.x, position.y, injectionRadius, 0));
            fluidCompute.SetVector("_InjectionForce", new Vector4(force.x, force.y, 0, 0));
            fluidCompute.SetTexture(kernelAddForce, "_VelocityRead", velocityA);
            fluidCompute.SetTexture(kernelAddForce, "_VelocityWrite", velocityB);
            fluidCompute.Dispatch(kernelAddForce, threadGroups, threadGroups, 1);
            SwapBuffers(ref velocityA, ref velocityB);
        }

        private void InjectDensity(Vector2 position, Color color)
        {
            int threadGroups = Mathf.CeilToInt(resolution / 8f);

            fluidCompute.SetVector("_InjectionPoint", new Vector4(position.x, position.y, injectionRadius, 0));
            fluidCompute.SetVector("_InjectionColor", color);
            fluidCompute.SetTexture(kernelAddDensity, "_DensityRead", densityA);
            fluidCompute.SetTexture(kernelAddDensity, "_DensityWrite", densityB);
            fluidCompute.Dispatch(kernelAddDensity, threadGroups, threadGroups, 1);
            SwapBuffers(ref densityA, ref densityB);
        }

        private void UpdateDisplay()
        {
            // Blit density or velocity to display RT
            Graphics.Blit(displayVelocity ? velocityA : densityA, displayRT);

            // Update display renderer if assigned
            if (displayRenderer != null)
            {
                displayRenderer.material.mainTexture = displayRT;
            }
        }

        private void SwapBuffers(ref RenderTexture a, ref RenderTexture b)
        {
            (a, b) = (b, a);
        }

        public RenderTexture GetDensityTexture() => densityA;
        public RenderTexture GetVelocityTexture() => velocityA;
        public RenderTexture GetDisplayTexture() => displayRT;

        public float GetLastFrameMs() => lastFrameMs;
        public (float adv, float diff, float press, float proj, float vort) GetDetailedTimings()
        {
            return (advectionMs, diffusionMs, pressureMs, projectionMs, vorticityMs);
        }

        private void OnDestroy()
        {
            // Clean up render textures
            if (velocityA) velocityA.Release();
            if (velocityB) velocityB.Release();
            if (densityA) densityA.Release();
            if (densityB) densityB.Release();
            if (pressure) pressure.Release();
            if (divergence) divergence.Release();
            if (vorticityTex) vorticityTex.Release();
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