using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Magi.Inkling.Services;
using Magi.Inkling.Services.Core;
using Magi.Inkling.Services.Diagnostics;

namespace Magi.Inkling.Systems.Capture
{
    /// <summary>
    /// Capture service: reads a RenderTexture to PNG and writes metadata JSON.
    /// Uses AsyncGPUReadback when available, falls back to ReadPixels.
    /// </summary>
    public class CaptureService : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MonoBehaviour simulationReaderSource; // ISimulationReader provider
        [SerializeField] private CaptureConfig config;

        private ISimulationReader reader;
        private ReadbackUtility.ReadbackMetadata lastMeta;
        private Result lastCaptureResult = Result.Success();

        private void Awake()
        {
            if (simulationReaderSource is ISimulationReader r)
                reader = r;

            if (config == null)
            {
                config = ScriptableObject.CreateInstance<CaptureConfig>();
            }

            var locator = ServiceLocator.Instance;
            if (locator != null)
            {
                locator.RegisterService(this);
            }
        }

        public void CaptureFrame()
        {
            if (reader == null) return;
            var src = config.captureSimulationBuffer
                ? reader.GetDisplayTexture() ?? reader.GetObstacleTexture()
                : reader.GetDisplayTexture();
            if (src == null)
            {
                Debug.LogWarning("[CaptureService] No source texture available.");
                return;
            }
            string dir = ResolveOutputDir();
            string baseName = $"capture_{Time.frameCount:D06}";
            string pngPath = Path.Combine(dir, baseName + ".png");
            string jsonPath = Path.Combine(dir, baseName + ".json");

            var result = CaptureRenderTexture(src, pngPath);
            WriteMetadataDetailed(jsonPath, src, result, lastMeta);
        }

        public Result CaptureRenderTexture(RenderTexture src, string outputPngPath)
        {
            if (src == null) return Result.Fail("Source texture null");

            var sink = ServiceLocator.Instance?.Resolve<LogSink>();
            var result = ReadbackUtility.RequestTextureToPng(src, outputPngPath, sink, out lastMeta);
            lastCaptureResult = result;
            return result;
        }

        // Legacy signature kept for tests; uses last capture meta/result.
        private void WriteMetadata(string path, RenderTexture src)
        {
            WriteMetadataDetailed(path, src, lastCaptureResult, lastMeta);
        }

        private void WriteMetadataDetailed(string path, RenderTexture src, Result captureResult, ReadbackUtility.ReadbackMetadata captureMeta)
        {
            var meta = new
            {
                frame = Time.frameCount,
                width = captureMeta.width != 0 ? captureMeta.width : src.width,
                height = captureMeta.height != 0 ? captureMeta.height : src.height,
                format = string.IsNullOrEmpty(captureMeta.format) ? src.graphicsFormat.ToString() : captureMeta.format,
                asyncSupported = captureMeta.asyncSupported,
                usedAsync = captureMeta.usedAsync,
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                timestamp = System.DateTime.UtcNow.ToString("o"),
                captureStatus = captureResult.IsSuccess ? "OK" : captureResult.Error,
                logs = CollectLogs()
            };
            var json = JsonUtility.ToJson(meta, true);
            File.WriteAllText(path, json);
        }

        private string[] CollectLogs()
        {
            var sink = ServiceLocator.Instance?.Resolve<Magi.Inkling.Services.Diagnostics.LogSink>();
            if (sink == null) return System.Array.Empty<string>();
            var list = new System.Collections.Generic.List<string>(sink.GetEntries());
            return list.ToArray();
        }

        private string ResolveOutputDir()
        {
            return string.IsNullOrEmpty(config.outputPath)
                ? Application.persistentDataPath
                : config.outputPath;
        }
    }
}
