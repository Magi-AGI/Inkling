using UnityEngine;
using System.IO;

namespace Inkling.Systems.SimulationLOD0
{
    public class SimulationRecorder : MonoBehaviour
    {
        [Header("Capture Settings")]
        public string outputFolder = "Captures";
        public string scenarioName = "scene01";
        public int frameIndex = 0;

        // Assign these from the sim pipeline
        public RenderTexture hiRes;
        public RenderTexture loResPhysics;

        public void BeginScenario(string name)
        {
            scenarioName = name;
            frameIndex = 0;
            var dir = Path.Combine(Application.persistentDataPath, outputFolder);
            Directory.CreateDirectory(dir);
            Debug.Log($"[SimulationRecorder] BeginScenario '{scenarioName}' → {dir}");
        }

        public void CaptureFrame()
        {
            if (hiRes == null || loResPhysics == null) return;
            var root = Path.Combine(Application.persistentDataPath, outputFolder);
            var stem = $"{scenarioName}_frame_{frameIndex:0000}";
            var pathHi = Path.Combine(root, stem + "_hires.png");
            var pathLo = Path.Combine(root, stem + "_lores_physics.png");
            SaveRT(hiRes, pathHi);
            SaveRT(loResPhysics, pathLo);
            Debug.Log($"[SimulationRecorder] Saved {pathHi} and {pathLo}");
            frameIndex++;
        }

        private static void SaveRT(RenderTexture rt, string path)
        {
            var tmp = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tmp.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tmp.Apply(false, false);
            var png = tmp.EncodeToPNG();
            File.WriteAllBytes(path, png);
            RenderTexture.active = prev;
            Object.Destroy(tmp);
        }
    }
}
