using UnityEngine;
using UnityEngine.Rendering;

namespace Magi.Inkling.Runtime.Systems.Rendering
{
    /// <summary>
    /// Manages the full ink rendering pipeline from simulation to final display.
    /// Follows the pattern: Compute Simulation → Compute Stylization → Fragment Gradient Mapping
    /// </summary>
    public class InkRenderPipeline : MonoBehaviour
    {
        [Header("Pipeline Components")]
        [SerializeField] private ComputeShader simulationCompute; // From InkTools
        // [SerializeField] private Magi.Inkling.Runtime.Systems.Foveation.BaselineStylizerCompute baselineStylizer;
        [SerializeField] private Material baselineStylizer; // Using material-based stylizer for now
        [SerializeField] private Material gradientRenderMaterial;

        [Header("Gradient Control")]
        [SerializeField] private InkGradientPreset currentPreset;
        [SerializeField] private bool autoUpdateGradients = true;

        [Header("Render Targets")]
        [SerializeField] private RenderTexture simulationRT; // LOD0 from compute
        [SerializeField] private RenderTexture stylizedRT;   // After stylization
        [SerializeField] private RenderTexture finalRT;      // After gradient mapping

        [Header("Display")]
        [SerializeField] private Renderer displayRenderer;
        [SerializeField] private bool displayFinal = true;

        [Header("Performance")]
        [SerializeField] private bool measurePerformance = true;

        // Performance tracking
        private float simulationMs;
        private float stylizationMs;
        private float gradientMs;
        private float totalMs;

        private void Start()
        {
            InitializePipeline();
        }

        private void InitializePipeline()
        {
            // Create render textures if not assigned
            if (simulationRT == null)
            {
                simulationRT = new RenderTexture(512, 512, 0, RenderTextureFormat.ARGBHalf);
                simulationRT.enableRandomWrite = true;
                simulationRT.Create();
                simulationRT.name = "SimulationRT";
            }

            if (stylizedRT == null)
            {
                stylizedRT = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGBHalf);
                stylizedRT.enableRandomWrite = true;
                stylizedRT.Create();
                stylizedRT.name = "StylizedRT";
            }

            if (finalRT == null)
            {
                finalRT = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32);
                finalRT.Create();
                finalRT.name = "FinalRT";
            }

            // Create gradient material if needed
            if (gradientRenderMaterial == null)
            {
                var shader = Shader.Find("Inkling/InkGradientRenderer");
                if (shader != null)
                {
                    gradientRenderMaterial = new Material(shader);
                    gradientRenderMaterial.name = "InkGradientMaterial";
                }
            }

            // Apply initial preset
            if (currentPreset != null && gradientRenderMaterial != null)
            {
                currentPreset.ApplyToMaterial(gradientRenderMaterial);
            }
        }

        private void Update()
        {
            if (autoUpdateGradients && currentPreset != null && gradientRenderMaterial != null)
            {
                currentPreset.ApplyToMaterial(gradientRenderMaterial);
            }

            ProcessRenderPipeline();
        }

        private void ProcessRenderPipeline()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Step 1: Simulation (compute shader) - handled by FluidSimulationManager
            // For now, we'll assume simulationRT is being filled by the simulation

            if (measurePerformance)
            {
                simulationMs = (float)sw.Elapsed.TotalMilliseconds;
                sw.Restart();
            }

            // Step 2: Stylization (using material-based approach)
            if (baselineStylizer != null)
            {
                Graphics.Blit(simulationRT, stylizedRT, baselineStylizer);
            }
            else
            {
                // Direct copy if no stylizer
                Graphics.Blit(simulationRT, stylizedRT);
            }

            if (measurePerformance)
            {
                stylizationMs = (float)sw.Elapsed.TotalMilliseconds;
                sw.Restart();
            }

            // Step 3: Gradient mapping (fragment shader)
            if (gradientRenderMaterial != null)
            {
                Graphics.Blit(stylizedRT, finalRT, gradientRenderMaterial);
            }
            else
            {
                Graphics.Blit(stylizedRT, finalRT);
            }

            if (measurePerformance)
            {
                gradientMs = (float)sw.Elapsed.TotalMilliseconds;
                totalMs = simulationMs + stylizationMs + gradientMs;
            }

            // Update display
            if (displayRenderer != null)
            {
                var tex = displayFinal ? finalRT : stylizedRT;
                displayRenderer.material.mainTexture = tex;
            }
        }

        /// <summary>
        /// Switch to a different gradient preset at runtime
        /// </summary>
        public void SetGradientPreset(InkGradientPreset preset)
        {
            if (preset == null) return;

            currentPreset = preset;

            if (gradientRenderMaterial != null)
            {
                preset.ApplyToMaterial(gradientRenderMaterial);
            }
        }

        /// <summary>
        /// Adjust a specific ink type's gradient at runtime
        /// </summary>
        public void SetInkGradient(InkType inkType, Gradient gradient)
        {
            if (currentPreset == null) return;

            switch (inkType)
            {
                case InkType.Fire:
                    currentPreset.fireGradient = gradient;
                    break;
                case InkType.Water:
                    currentPreset.waterGradient = gradient;
                    break;
                case InkType.Metal:
                    currentPreset.metalGradient = gradient;
                    break;
                case InkType.Electricity:
                    currentPreset.electricityGradient = gradient;
                    break;
                case InkType.Ice:
                    currentPreset.iceGradient = gradient;
                    break;
                case InkType.Plant:
                    currentPreset.plantGradient = gradient;
                    break;
                case InkType.Steam:
                    currentPreset.steamGradient = gradient;
                    break;
                case InkType.Dust:
                    currentPreset.dustGradient = gradient;
                    break;
            }

            if (autoUpdateGradients && gradientRenderMaterial != null)
            {
                currentPreset.ApplyToMaterial(gradientRenderMaterial);
            }
        }

        /// <summary>
        /// Get current performance metrics
        /// </summary>
        public PerformanceMetrics GetPerformanceMetrics()
        {
            return new PerformanceMetrics
            {
                simulationMs = simulationMs,
                stylizationMs = stylizationMs,
                gradientMs = gradientMs,
                totalMs = totalMs
            };
        }

        private void OnGUI()
        {
            if (!measurePerformance || !Application.isEditor) return;

            int y = 180;
            GUI.Label(new Rect(10, y, 300, 20), "=== Render Pipeline ===");
            GUI.Label(new Rect(10, y + 20, 300, 20), $"Simulation: {simulationMs:F2}ms");
            GUI.Label(new Rect(10, y + 40, 300, 20), $"Stylization: {stylizationMs:F2}ms");
            GUI.Label(new Rect(10, y + 60, 300, 20), $"Gradient: {gradientMs:F2}ms");
            GUI.Label(new Rect(10, y + 80, 300, 20), $"Total: {totalMs:F2}ms");
        }

        private void OnDestroy()
        {
            // Clean up render textures if we created them
            if (simulationRT != null && simulationRT.name == "SimulationRT")
            {
                simulationRT.Release();
                DestroyImmediate(simulationRT);
            }

            if (stylizedRT != null && stylizedRT.name == "StylizedRT")
            {
                stylizedRT.Release();
                DestroyImmediate(stylizedRT);
            }

            if (finalRT != null && finalRT.name == "FinalRT")
            {
                finalRT.Release();
                DestroyImmediate(finalRT);
            }
        }

        public enum InkType
        {
            Fire,
            Water,
            Metal,
            Electricity,
            Ice,
            Plant,
            Steam,
            Dust
        }

        public struct PerformanceMetrics
        {
            public float simulationMs;
            public float stylizationMs;
            public float gradientMs;
            public float totalMs;
        }
    }
}