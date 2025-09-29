using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Magi.Inkling.Runtime.Systems.Foveation
{
    /// <summary>
    /// Compute shader-based baseline stylizer for non-ML fallback.
    /// Provides fast GPU stylization without neural network inference.
    /// </summary>
    public class BaselineStylizerCompute : MonoBehaviour
    {
        public enum StyleMode
        {
            Standard,
            Watercolor,
            CelShaded
        }

        [Header("Compute Shader")]
        [SerializeField] private ComputeShader stylizerShader;
        [SerializeField] private StyleMode styleMode = StyleMode.Standard;

        [Header("Style Parameters")]
        [Range(0, 1)] public float styleIntensity = 0.5f;
        [Range(0, 1)] public float edgeThreshold = 0.1f;
        [Range(0, 1)] public float colorBleeding = 0.3f;
        [Range(0, 1)] public float paperInfluence = 0.2f;

        [Header("Color Ramp")]
        [SerializeField] private Gradient colorGradient;

        [Header("Performance")]
        [SerializeField] private bool measureTiming = true;

        // Kernel indices
        private int kernelStandard;
        private int kernelWatercolor;
        private int kernelCelShaded;

        // Performance
        private Stopwatch stopwatch = new Stopwatch();
        private float lastProcessMs;

        private void Start()
        {
            if (stylizerShader == null)
            {
                Debug.LogError("[BaselineStylizerCompute] Compute shader not assigned");
                enabled = false;
                return;
            }

            // Get kernel indices
            kernelStandard = stylizerShader.FindKernel("Stylize");
            kernelWatercolor = stylizerShader.FindKernel("StylizeWatercolor");
            kernelCelShaded = stylizerShader.FindKernel("StylizeCelShaded");

            // Initialize gradient if null
            if (colorGradient == null)
            {
                colorGradient = CreateDefaultGradient();
            }
        }

        /// <summary>
        /// Process input texture through baseline stylization
        /// </summary>
        public RenderTexture Process(RenderTexture input, RenderTexture output = null)
        {
            if (stylizerShader == null || input == null)
            {
                Debug.LogError("[BaselineStylizerCompute] Missing shader or input");
                return null;
            }

            if (measureTiming) stopwatch.Restart();

            // Create output if needed
            if (output == null)
            {
                output = new RenderTexture(input.width, input.height, 0, RenderTextureFormat.ARGBHalf);
                output.enableRandomWrite = true;
                output.Create();
            }

            // Set textures
            int kernel = GetKernel();
            stylizerShader.SetTexture(kernel, "_InputTexture", input);
            stylizerShader.SetTexture(kernel, "_OutputTexture", output);

            // Set parameters
            stylizerShader.SetFloat("_StyleIntensity", styleIntensity);
            stylizerShader.SetFloat("_EdgeThreshold", edgeThreshold);
            stylizerShader.SetFloat("_ColorBleeding", colorBleeding);
            stylizerShader.SetFloat("_PaperInfluence", paperInfluence);

            // Set dimensions
            stylizerShader.SetInts("_TextureSize", input.width, input.height);
            stylizerShader.SetFloats("_TexelSize", 1f / input.width, 1f / input.height);

            // Set color ramp from gradient
            SetColorRamp();

            // Dispatch (8x8 thread groups)
            int threadGroupsX = Mathf.CeilToInt(input.width / 8f);
            int threadGroupsY = Mathf.CeilToInt(input.height / 8f);
            stylizerShader.Dispatch(kernel, threadGroupsX, threadGroupsY, 1);

            if (measureTiming)
            {
                stopwatch.Stop();
                lastProcessMs = (float)stopwatch.Elapsed.TotalMilliseconds;
            }

            return output;
        }

        /// <summary>
        /// Process with explicit LOD transition (LOD0 → LOD1)
        /// </summary>
        public RenderTexture ProcessLODTransition(RenderTexture lod0Input, int targetResolution = 256)
        {
            // Create downsampled output for LOD1
            var lod1Output = new RenderTexture(targetResolution, targetResolution, 0, RenderTextureFormat.ARGBHalf);
            lod1Output.enableRandomWrite = true;
            lod1Output.Create();

            // First downsample if needed
            RenderTexture processInput = lod0Input;
            if (lod0Input.width != targetResolution)
            {
                var temp = RenderTexture.GetTemporary(targetResolution, targetResolution, 0, RenderTextureFormat.ARGBHalf);
                Graphics.Blit(lod0Input, temp);
                processInput = temp;
            }

            // Apply stylization
            Process(processInput, lod1Output);

            // Release temporary if used
            if (processInput != lod0Input)
            {
                RenderTexture.ReleaseTemporary(processInput);
            }

            return lod1Output;
        }

        private int GetKernel()
        {
            switch (styleMode)
            {
                case StyleMode.Watercolor:
                    return kernelWatercolor;
                case StyleMode.CelShaded:
                    return kernelCelShaded;
                default:
                    return kernelStandard;
            }
        }

        private void SetColorRamp()
        {
            // Convert gradient to color array
            const int rampSize = 8;
            Color[] colors = new Color[rampSize];

            for (int i = 0; i < rampSize; i++)
            {
                float t = i / (float)(rampSize - 1);
                colors[i] = colorGradient.Evaluate(t);
            }

            // Convert to Vector4 array and set
            Vector4[] colorVectors = new Vector4[rampSize];
            for (int i = 0; i < rampSize; i++)
            {
                colorVectors[i] = colors[i];
            }

            stylizerShader.SetVectorArray("_ColorRamp", colorVectors);
            stylizerShader.SetInt("_ColorRampLength", rampSize);
        }

        private Gradient CreateDefaultGradient()
        {
            // Create fire-like gradient (blue → orange → white)
            var gradient = new Gradient();

            var colorKeys = new GradientColorKey[]
            {
                new GradientColorKey(new Color(0.1f, 0.1f, 0.3f), 0f),    // Dark blue
                new GradientColorKey(new Color(0.8f, 0.2f, 0.1f), 0.3f),   // Red-orange
                new GradientColorKey(new Color(1f, 0.6f, 0.2f), 0.6f),     // Orange
                new GradientColorKey(new Color(1f, 0.95f, 0.8f), 1f)       // Near white
            };

            var alphaKeys = new GradientAlphaKey[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.1f),
                new GradientAlphaKey(1f, 1f)
            };

            gradient.SetKeys(colorKeys, alphaKeys);
            return gradient;
        }

        public float GetLastProcessMs() => lastProcessMs;

        private void OnGUI()
        {
            if (!Application.isEditor || !measureTiming) return;

            GUI.Label(new Rect(10, 140, 300, 20), $"Baseline Stylizer: {lastProcessMs:F2}ms");
            GUI.Label(new Rect(10, 160, 300, 20), $"Mode: {styleMode}");
        }
    }
}