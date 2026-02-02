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

            CaptureRenderTexture(src, pngPath);
            WriteMetadata(jsonPath, src);
        }

        public void CaptureRenderTexture(RenderTexture src, string outputPngPath)
        {
            if (src == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPngPath)!);

            // Async path when possible
            if (SystemInfo.supportsAsyncGPUReadback)
            {
                UnityEngine.Rendering.AsyncGPUReadback.Request(src, 0, TextureFormat.RGBA32, req =>
                {
                    if (req.hasError)
                    {
                        Debug.LogWarning("[CaptureService] AsyncGPUReadback failed, falling back.");
                        FallbackReadback(src, outputPngPath);
                        return;
                    }
                    var data = req.GetData<byte>();
                    var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
                    tex.LoadRawTextureData(data);
                    tex.Apply();
                    File.WriteAllBytes(outputPngPath, tex.EncodeToPNG());
                    Destroy(tex);
                });
            }
            else
            {
                FallbackReadback(src, outputPngPath);
            }
        }

        private void FallbackReadback(RenderTexture src, string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = src;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, true);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
            RenderTexture.active = prev;
        }

        private void WriteMetadata(string path, RenderTexture src)
        {
            var meta = new
            {
                frame = Time.frameCount,
                width = src.width,
                height = src.height,
                format = src.format.ToString(),
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                timestamp = System.DateTime.UtcNow.ToString("o")
            };
            var json = JsonUtility.ToJson(meta, true);
            File.WriteAllText(path, json);
        }

        private string ResolveOutputDir()
        {
            return string.IsNullOrEmpty(config.outputPath)
                ? Application.persistentDataPath
                : config.outputPath;
        }
    }
}
