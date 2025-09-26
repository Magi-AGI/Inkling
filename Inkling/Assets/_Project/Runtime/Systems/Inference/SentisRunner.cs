using UnityEngine;

namespace Inkling.Systems.Inference
{
    // Placeholder for Sentis integration without direct dependency
    public class SentisRunner : MonoBehaviour
    {
        [Tooltip("ONNX model bytes (use .onnx.bytes so Unity imports as TextAsset)")] public TextAsset onnxBytes;
        public int inputChannels = 4;
        public int outputChannels = 4;

        public void Load()
        {
            // Load ONNX bytes into Sentis runtime (implemented once Sentis is present)
        }

        public void Run(RenderTexture input, RenderTexture output)
        {
            // Bind input RT and execute inference into output RT
        }
    }
}
