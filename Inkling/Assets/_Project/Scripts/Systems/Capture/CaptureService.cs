using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Magi.Inkling.Services;

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

        private void Awake()
        {
            if (simulationReaderSource is ISimulationReader r)
                reader = r;

            if (config == null)
            {
                config = ScriptableObject.CreateInstance<CaptureConfig>();
            }

            var locator = Magi.Inkling.Services.Core.ServiceLocator.Instance;
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
            WriteMetadata(jsonPath, src, result);
        }

        public Magi.Inkling.Services.Core.Result CaptureRenderTexture(RenderTexture src, string outputPngPath)
        {
            if (src == null) return Magi.Inkling.Services.Core.Result.Fail("Source texture null");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);

            // Async path when possible
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                UnityEngine.Rendering.AsyncGPUReadback.Request(src, 0, TextureFormat.RGBA32, req =>
                {
                    if (req.hasError)
                    {
                        Debug.LogWarning("[CaptureService] AsyncGPUReadback failed, falling back.");
                        Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal("Capture async readback failed; using fallback.");
                        var fallback = FallbackReadback(src, outputPngPath);
                        // cannot return Result from async callback; log only
                        return;
                    }
                    var data = req.GetData<byte>();
                    var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
                    tex.LoadRawTextureData(data);
                    tex.Apply();
                    File.WriteAllBytes(outputPngPath, tex.EncodeToPNG());
                    Destroy(tex);
                });
                return Magi.Inkling.Services.Core.Result.Success();
            }
            else
            {
                return FallbackReadback(src, outputPngPath);
            }
        }

        private Magi.Inkling.Services.Core.Result FallbackReadback(RenderTexture src, string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = src;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
            try
            {
                tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            catch (System.Exception ex)
            {
                return Magi.Inkling.Services.Core.Result.Fail(ex);
            }
            finally
            {
                Destroy(tex);
                RenderTexture.active = prev;
            }
            return Magi.Inkling.Services.Core.Result.Success();
        }

        private void WriteMetadata(string path, RenderTexture src, Magi.Inkling.Services.Core.Result captureResult)
        {
            var meta = new
            {
                frame = Time.frameCount,
                width = src.width,
                height = src.height,
                format = src.format.ToString(),
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
            var sink = Magi.Inkling.Services.Core.ServiceLocator.Instance?.Resolve<Magi.Inkling.Services.Diagnostics.LogSink>();
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
