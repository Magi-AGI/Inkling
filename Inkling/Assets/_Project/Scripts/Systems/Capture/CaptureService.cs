using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Magi.Inkling.Services;

namespace Magi.Inkling.Systems.Capture
{
    /// <summary>
    /// Lightweight capture scaffold. Uses AsyncGPUReadback when hooked up.
    /// Currently writes a stub PNG with clear color to prove the path.
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
        }

        public void CaptureFrameStub()
        {
            string dir = string.IsNullOrEmpty(config.outputPath)
                ? Application.persistentDataPath
                : config.outputPath;
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, $"capture_stub_{Time.frameCount:D06}.png");

            // For now write a 2x2 black image as a placeholder; real pipeline will GPU-read buffers.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            tex.SetPixels(new[] { Color.black, Color.black, Color.black, Color.black });
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);

            Debug.Log($"[CaptureService] Wrote stub capture to {path}");
        }
    }
}
