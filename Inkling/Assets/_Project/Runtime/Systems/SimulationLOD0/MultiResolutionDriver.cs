using UnityEngine;
using UnityEngine.Rendering;
using Magi.UnityTools.Runtime.Core;

namespace Magi.Inkling.Runtime.Systems.SimulationLOD0
{
    /// <summary>
    /// Manages multi-resolution rendering for fluid simulation.
    /// Allows simulation at lower resolution with high-quality display.
    /// </summary>
    public class MultiResolutionDriver : MonoBehaviour
    {
        [Header("Resolution Settings")]
        [SerializeField] private int simulationResolution = 128;  // Low res for simulation
        [SerializeField] private int displayResolution = 512;     // High res for display
        [SerializeField] private bool useTemporalUpsampling = true;
        [SerializeField] private bool useAdaptiveResolution = false;

        [Header("References")]
        [SerializeField] private SimDriver simDriver;
        [SerializeField] private ComputeShader multiResCompute;
        [SerializeField] private Renderer displayRenderer;

        [Header("Quality Settings")]
        [SerializeField] private FilterMode upsampleFilter = FilterMode.Bilinear;
        [SerializeField] private float temporalBlend = 0.9f;

        // Render textures for multi-resolution pipeline
        private RenderTexture displayDensity;
        private RenderTexture displayVelocity;
        private RenderTexture temporalHistory;
        private RenderTexture adaptiveResMap;

        // Kernel indices
        private int kernelUpsampleBilinear;
        private int kernelDownsampleAverage;
        private int kernelTemporalUpsample;
        private int kernelAdaptiveResolution;

        private void Start()
        {
            if (simDriver == null)
            {
                simDriver = GetComponent<SimDriver>();
                if (simDriver == null)
                {
                    Debug.LogError("[MultiResolutionDriver] No SimDriver found!");
                    enabled = false;
                    return;
                }
            }

            // Use the same compute shader as SimDriver
            multiResCompute = simDriver.fluidCompute;
            if (multiResCompute == null)
            {
                Debug.LogWarning("[MultiResolutionDriver] No compute shader assigned. Multi-resolution disabled.");
                enabled = false;
                return;
            }

            InitializeKernels();
            AllocateRenderTextures();
        }

        private void InitializeKernels()
        {
            try
            {
                kernelUpsampleBilinear = multiResCompute.FindKernel("UpsampleBilinear");
                kernelDownsampleAverage = multiResCompute.FindKernel("DownsampleAverage");
                kernelTemporalUpsample = multiResCompute.FindKernel("TemporalUpsample");
                kernelAdaptiveResolution = multiResCompute.FindKernel("AdaptiveResolution");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MultiResolutionDriver] Some kernels not found: {e.Message}");
                // Continue with available kernels
            }
        }

        private void AllocateRenderTextures()
        {
            // High-resolution display textures
            displayDensity = CreateRT(displayResolution, RenderTextureFormat.ARGBHalf, "DisplayDensity");
            displayVelocity = CreateRT(displayResolution, RenderTextureFormat.RGHalf, "DisplayVelocity");

            if (useTemporalUpsampling)
            {
                temporalHistory = CreateRT(displayResolution, RenderTextureFormat.ARGBHalf, "TemporalHistory");
            }

            if (useAdaptiveResolution)
            {
                adaptiveResMap = CreateRT(simulationResolution, RenderTextureFormat.RFloat, "AdaptiveResMap");
            }
        }

        private RenderTexture CreateRT(int resolution, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(resolution, resolution, 0, format);
            rt.enableRandomWrite = true;
            rt.filterMode = upsampleFilter;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = name;
            rt.Create();
            return rt;
        }

        private void LateUpdate()
        {
            if (multiResCompute == null || simDriver == null) return;

            // Get simulation textures
            var simDensity = simDriver.GetDensityTexture();
            var simVelocity = simDriver.GetVelocityTexture();

            if (simDensity == null || simVelocity == null) return;

            // Perform upsampling
            if (useTemporalUpsampling && temporalHistory != null)
            {
                PerformTemporalUpsampling(simDensity, simVelocity);
            }
            else
            {
                PerformBilinearUpsampling(simDensity, simVelocity);
            }

            // Update display
            UpdateDisplay();

            // Update adaptive resolution map if enabled
            if (useAdaptiveResolution && adaptiveResMap != null)
            {
                UpdateAdaptiveResolution(simVelocity);
            }
        }

        private void PerformBilinearUpsampling(RenderTexture simDensity, RenderTexture simVelocity)
        {
            int threadGroups = Mathf.CeilToInt(displayResolution / 8f);

            // Set parameters
            multiResCompute.SetVector("_SimulationSize", new Vector2(simulationResolution, simulationResolution));
            multiResCompute.SetFloat("_DeltaTime", simDriver.Timestep);

            // Upsample density
            multiResCompute.SetTexture(kernelUpsampleBilinear, "_DensityRead", simDensity);
            multiResCompute.SetTexture(kernelUpsampleBilinear, "_DensityWrite", displayDensity);
            multiResCompute.Dispatch(kernelUpsampleBilinear, threadGroups, threadGroups, 1);

            // Upsample velocity
            multiResCompute.SetTexture(kernelUpsampleBilinear, "_VelocityRead", simVelocity);
            multiResCompute.SetTexture(kernelUpsampleBilinear, "_VelocityWrite", displayVelocity);
            multiResCompute.Dispatch(kernelUpsampleBilinear, threadGroups, threadGroups, 1);
        }

        private void PerformTemporalUpsampling(RenderTexture simDensity, RenderTexture simVelocity)
        {
            int threadGroups = Mathf.CeilToInt(displayResolution / 8f);

            multiResCompute.SetVector("_SimulationSize", new Vector2(simulationResolution, simulationResolution));
            multiResCompute.SetFloat("_DeltaTime", simDriver.Timestep);

            // Temporal upsampling with history
            multiResCompute.SetTexture(kernelTemporalUpsample, "_DensityRead", simDensity);
            multiResCompute.SetTexture(kernelTemporalUpsample, "_VelocityRead", simVelocity);
            multiResCompute.SetTexture(kernelTemporalUpsample, "_PressureRead", temporalHistory); // Previous frame
            multiResCompute.SetTexture(kernelTemporalUpsample, "_DensityWrite", displayDensity);
            multiResCompute.Dispatch(kernelTemporalUpsample, threadGroups, threadGroups, 1);

            // Copy current frame to history
            Graphics.Blit(displayDensity, temporalHistory);
        }

        private void UpdateAdaptiveResolution(RenderTexture simVelocity)
        {
            int threadGroups = Mathf.CeilToInt(simulationResolution / 8f);

            multiResCompute.SetVector("_SimulationSize", new Vector2(simulationResolution, simulationResolution));
            multiResCompute.SetTexture(kernelAdaptiveResolution, "_VelocityRead", simVelocity);
            multiResCompute.SetTexture(kernelAdaptiveResolution, "_DivergenceWrite", adaptiveResMap);
            multiResCompute.Dispatch(kernelAdaptiveResolution, threadGroups, threadGroups, 1);
        }

        private void UpdateDisplay()
        {
            if (displayRenderer != null)
            {
                displayRenderer.material.mainTexture = displayDensity;
            }
        }

        public RenderTexture GetHighResolutionDensity() => displayDensity;
        public RenderTexture GetHighResolutionVelocity() => displayVelocity;

        private void OnDestroy()
        {
            if (displayDensity) displayDensity.Release();
            if (displayVelocity) displayVelocity.Release();
            if (temporalHistory) temporalHistory.Release();
            if (adaptiveResMap) adaptiveResMap.Release();
        }

        private void OnGUI()
        {
            if (!Application.isEditor) return;

            int y = 200;
            GUI.Label(new Rect(10, y, 400, 20), $"=== Multi-Resolution ({simulationResolution}→{displayResolution}) ===");
            GUI.Label(new Rect(10, y + 20, 300, 20), $"Temporal: {(useTemporalUpsampling ? "ON" : "OFF")}");
            GUI.Label(new Rect(10, y + 40, 300, 20), $"Adaptive: {(useAdaptiveResolution ? "ON" : "OFF")}");
        }
    }
}