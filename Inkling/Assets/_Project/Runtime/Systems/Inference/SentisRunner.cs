using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

#if SENTIS_PRESENT
using Unity.Sentis;
#endif

namespace Magi.Inkling.Runtime.Systems.Inference
{
    /// <summary>
    /// Sentis ML inference runner with TextAsset .onnx.bytes support.
    /// Implements Codex recommendations for simple integration.
    /// </summary>
    public class SentisRunner : MonoBehaviour
    {
        [Header("Model")]
        [Tooltip("ONNX model bytes (use .onnx.bytes so Unity imports as TextAsset)")]
        public TextAsset onnxBytes;

        [Header("Channels")]
        public int inputChannels = 4;
        public int outputChannels = 4;

        [Header("Performance")]
        [SerializeField] private bool measureTiming = true;

#if SENTIS_PRESENT
        private Model model;
        private IWorker worker;
#endif
        private Stopwatch stopwatch = new Stopwatch();
        private float lastInferenceMs;

        public void Load()
        {
#if SENTIS_PRESENT
            if (onnxBytes == null)
            {
                Debug.LogError("[SentisRunner] No ONNX bytes assigned");
                return;
            }

            try
            {
                // Load model from TextAsset bytes
                model = ModelLoader.Load(onnxBytes.bytes);

                // Create worker with GPU compute backend
                worker = WorkerFactory.CreateWorker(WorkerFactory.Type.ComputePrecompiled, model);

                Debug.Log($"[SentisRunner] Loaded model: {onnxBytes.name}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SentisRunner] Failed to load: {e.Message}");
            }
#else
            Debug.LogWarning("[SentisRunner] Unity.Sentis not available. Add package reference to asmdef.");
#endif
        }

        public void Run(RenderTexture input, RenderTexture output)
        {
#if SENTIS_PRESENT
            if (worker == null)
            {
                Debug.LogError("[SentisRunner] Worker not initialized. Call Load() first.");
                return;
            }

            if (measureTiming) stopwatch.Restart();

            // Create tensor from input RT
            // Note: Sentis 2.x uses TextureInput/TextureOutput converters
            var inputTensor = TextureInput.CreateFromTexture(input);

            // Execute inference
            worker.Execute(inputTensor);

            // Get output and render to texture
            var outputTensor = worker.PeekOutput() as TensorFloat;
            if (outputTensor != null)
            {
                // Render tensor back to texture
                outputTensor.ToRenderTexture(output);
            }

            if (measureTiming)
            {
                stopwatch.Stop();
                lastInferenceMs = (float)stopwatch.Elapsed.TotalMilliseconds;
                Debug.Log($"[SentisRunner] Inference: {lastInferenceMs:F2}ms");
            }

            inputTensor.Dispose();
#else
            // Fallback: just copy input to output when Sentis not available
            if (measureTiming) stopwatch.Restart();

            Graphics.Blit(input, output);

            if (measureTiming)
            {
                stopwatch.Stop();
                lastInferenceMs = (float)stopwatch.Elapsed.TotalMilliseconds;
                Debug.Log($"[SentisRunner] Fallback Blit: {lastInferenceMs:F2}ms");
            }
#endif
        }

        public float GetLastInferenceMs() => lastInferenceMs;

        private void OnDestroy()
        {
#if SENTIS_PRESENT
            worker?.Dispose();
#endif
        }
    }
}
