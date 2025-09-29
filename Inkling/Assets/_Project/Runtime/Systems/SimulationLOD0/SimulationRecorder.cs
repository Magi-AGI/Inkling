using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Magi.Inkling.Runtime.Systems.SimulationLOD0
{
    /// <summary>
    /// Runtime simulation recorder with metadata export for ML training datasets.
    /// Follows Codex recommendations for deterministic capture and metadata.
    /// </summary>
    public class SimulationRecorder : MonoBehaviour
    {
        [Header("Capture Settings")]
        [SerializeField] public string outputFolder = "Datasets/Captures";
        [SerializeField] private string scenarioName = "scene01";
        [SerializeField] private int frameIndex = 0;

        [Header("Render Textures")]
        [SerializeField] public RenderTexture hiRes; // 512x512 RGBAHalf for mobile
        [SerializeField] public RenderTexture loResPhysics; // 256x256 RGBAHalf

        [Header("Batch Capture")]
        [SerializeField] private bool isCapturing = false;
        [SerializeField] private int targetFrameCount = 100;
        [SerializeField] private float captureInterval = 0.033f; // ~30fps

        [Header("Simulation Parameters")]
        [SerializeField] private SimDriver simDriver;
        [SerializeField] private float viscosity = 0.01f;
        [SerializeField] private float vorticity = 2.0f;
        [SerializeField] private float dissipation = 0.98f;
        [SerializeField] private float velocityDissipation = 0.99f;

        private string sessionId;
        private float captureTimer;
        private Dictionary<string, object> sessionMetadata;

        #region Data Structures

        [Serializable]
        public class FrameMetadata
        {
            public string scenario;
            public int frame;
            public float timestamp;
            public string colorSpace;
            public string rtFormat;
            public Vector2Int hiResSize;
            public Vector2Int loResSize;
            public Dictionary<string, float> simParams;
        }

        #endregion

        #region Unity Lifecycle

        private void Start()
        {
            CreateOutputDirectory();
        }

        private void Update()
        {
            if (isCapturing)
            {
                captureTimer += Time.deltaTime;
                if (captureTimer >= captureInterval)
                {
                    CaptureFrame();
                    captureTimer = 0f;

                    if (frameIndex >= targetFrameCount)
                    {
                        EndScenario();
                    }
                }
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Begin a new capture scenario with metadata
        /// </summary>
        public void BeginScenario(string name, int? randomSeed = null)
        {
            scenarioName = name;
            frameIndex = 0;
            sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            // Don't auto-start capture - wait for manual trigger
            isCapturing = false;
            captureTimer = 0f;

            // Set random seed for determinism
            if (randomSeed.HasValue)
            {
                UnityEngine.Random.InitState(randomSeed.Value);
            }

            // Initialize session metadata
            sessionMetadata = new Dictionary<string, object>
            {
                ["scenario"] = scenarioName,
                ["sessionId"] = sessionId,
                ["unityVersion"] = Application.unityVersion,
                ["colorSpace"] = QualitySettings.activeColorSpace.ToString(),
                ["startTime"] = DateTime.Now.ToString("o"),
                ["randomSeed"] = randomSeed ?? UnityEngine.Random.state.GetHashCode()
            };

            var dir = GetOutputPath();
            Directory.CreateDirectory(dir);
            Debug.Log($"[SimulationRecorder] BeginScenario '{scenarioName}' → {dir}");
        }

        /// <summary>
        /// End current scenario and write summary metadata
        /// </summary>
        /// <summary>
        /// Start capturing frames (after BeginScenario has been called)
        /// </summary>
        public void StartCapture()
        {
            if (string.IsNullOrEmpty(scenarioName))
            {
                Debug.LogWarning("[SimulationRecorder] Call BeginScenario first to set up the capture session");
                return;
            }
            isCapturing = true;
            captureTimer = 0f;
            Debug.Log($"[SimulationRecorder] Started capture for scenario '{scenarioName}'");
        }

        /// <summary>
        /// Stop capturing frames without ending the scenario
        /// </summary>
        public void StopCapture()
        {
            isCapturing = false;
            Debug.Log($"[SimulationRecorder] Stopped capture at frame {frameIndex}");
        }

        public void EndScenario()
        {
            isCapturing = false;

            if (sessionMetadata != null)
            {
                sessionMetadata["endTime"] = DateTime.Now.ToString("o");
                sessionMetadata["totalFrames"] = frameIndex;

                WriteSessionSummary();
            }

            Debug.Log($"[SimulationRecorder] EndScenario '{scenarioName}' - Captured {frameIndex} frames");
        }

        /// <summary>
        /// Capture current frame with metadata
        /// </summary>
        public void CaptureFrame()
        {
            if (hiRes == null || loResPhysics == null)
            {
                Debug.LogWarning("[SimulationRecorder] RenderTextures not assigned");
                return;
            }

            var root = GetOutputPath();
            var stem = $"{scenarioName}_{sessionId}_frame_{frameIndex:D4}";

            // Save textures
            var pathHi = Path.Combine(root, $"{stem}_hires.png");
            var pathLo = Path.Combine(root, $"{stem}_lores_physics.png");

            SaveRT(hiRes, pathHi);
            SaveRT(loResPhysics, pathLo);

            // Create and save frame metadata
            var metadata = CreateFrameMetadata();
            var metaPath = Path.Combine(root, $"{stem}.meta.json");
            SaveMetadata(metadata, metaPath);

            Debug.Log($"[SimulationRecorder] Frame {frameIndex} saved");
            frameIndex++;
        }

        /// <summary>
        /// Capture a batch of frames
        /// </summary>
        public IEnumerator CaptureBatch(int frames, float interval, string scenario = "batch")
        {
            BeginScenario(scenario);
            targetFrameCount = frames;
            captureInterval = interval;

            // Start capture for batch operation
            StartCapture();

            while (isCapturing)
            {
                yield return null;
            }
        }

        /// <summary>
        /// Capture golden frame for regression testing
        /// </summary>
        public void CaptureGoldenFrame(string identifier)
        {
            if (hiRes == null || loResPhysics == null) return;

            var goldenDir = Path.Combine(Application.persistentDataPath, outputFolder, "golden");
            Directory.CreateDirectory(goldenDir);

            var pathHi = Path.Combine(goldenDir, $"{identifier}_golden_hires.png");
            var pathLo = Path.Combine(goldenDir, $"{identifier}_golden_lores.png");

            SaveRT(hiRes, pathHi);
            SaveRT(loResPhysics, pathLo);

            // Save golden metadata
            var metadata = CreateFrameMetadata();
            metadata.scenario = $"golden_{identifier}";
            var metaPath = Path.Combine(goldenDir, $"{identifier}_golden.meta.json");
            SaveMetadata(metadata, metaPath);

            Debug.Log($"[SimulationRecorder] Golden frame '{identifier}' captured");
        }

        #endregion

        #region Implementation

        private void SaveRT(RenderTexture rt, string path)
        {
            // Check color space and format
            bool isLinear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var format = isLinear ? TextureFormat.RGBAFloat : TextureFormat.RGBA32;

            var tmp = new Texture2D(rt.width, rt.height, format, false, isLinear);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tmp.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tmp.Apply(false, false);

            // Encode based on format - PNG preserves 16-bit when needed
            var png = tmp.EncodeToPNG();
            File.WriteAllBytes(path, png);

            RenderTexture.active = prev;
            Destroy(tmp);
        }

        private FrameMetadata CreateFrameMetadata()
        {
            return new FrameMetadata
            {
                scenario = scenarioName,
                frame = frameIndex,
                timestamp = Time.time,
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                rtFormat = hiRes.graphicsFormat.ToString(),
                hiResSize = new Vector2Int(hiRes.width, hiRes.height),
                loResSize = new Vector2Int(loResPhysics.width, loResPhysics.height),
                simParams = new Dictionary<string, float>
                {
                    ["deltaTime"] = Time.fixedDeltaTime,
                    ["timeScale"] = Time.timeScale,
                    ["viscosity"] = simDriver != null ? simDriver.Viscosity : viscosity,
                    ["vorticity"] = simDriver != null ? simDriver.Vorticity : vorticity,
                    ["dissipation"] = simDriver != null ? simDriver.Dissipation : dissipation,
                    ["velocityDissipation"] = simDriver != null ? simDriver.VelocityDissipation : velocityDissipation
                }
            };
        }

        private void SaveMetadata(FrameMetadata metadata, string path)
        {
            var json = JsonUtility.ToJson(metadata, true);
            File.WriteAllText(path, json);
        }

        private void WriteSessionSummary()
        {
            var summaryPath = Path.Combine(GetOutputPath(), $"{scenarioName}_{sessionId}_summary.json");
            var json = JsonUtility.ToJson(sessionMetadata);
            File.WriteAllText(summaryPath, json);
        }

        private string GetOutputPath()
        {
            return Path.Combine(Application.persistentDataPath, outputFolder, scenarioName);
        }

        private void CreateOutputDirectory()
        {
            var baseDir = Path.Combine(Application.persistentDataPath, outputFolder);
            if (!Directory.Exists(baseDir))
            {
                Directory.CreateDirectory(baseDir);
                Directory.CreateDirectory(Path.Combine(baseDir, "golden"));
            }
        }

        #endregion

        #region Editor Support

#if UNITY_EDITOR
        [UnityEditor.MenuItem("Inkling/Capture/Capture N Frames")]
        private static void CaptureNFramesMenu()
        {
            var recorder = FindFirstObjectByType<SimulationRecorder>();
            if (recorder != null)
            {
                recorder.StartCoroutine(recorder.CaptureBatch(100, 0.033f, "editor_batch"));
            }
        }

        [UnityEditor.MenuItem("Inkling/Capture/Golden Frame Snapshot")]
        private static void GoldenFrameMenu()
        {
            var recorder = FindFirstObjectByType<SimulationRecorder>();
            if (recorder != null)
            {
                recorder.CaptureGoldenFrame($"snapshot_{DateTime.Now:HHmmss}");
            }
        }
#endif

        #endregion
    }
}
