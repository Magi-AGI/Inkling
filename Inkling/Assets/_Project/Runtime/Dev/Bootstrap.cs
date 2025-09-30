using UnityEngine;
using UnityEngine.UI;
using Magi.Inkling.Runtime.Systems.SimulationLOD0;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Magi.Inkling.Runtime.Dev
{
    public class Bootstrap : MonoBehaviour
    {
        [Header("RenderTextures")]
        public int width = 512;
        public int height = 512;
        [Tooltip("Lo-res is hi-res divided by this scale")] public int loResScale = 2;

        [Header("Capture")] public string scenario = "test01";
        public string outputFolder = "Captures";

        private RenderTexture hiResRT;
        private RenderTexture loResRT;
        private TestPatternGenerator pattern;
        private SimulationRecorder recorder;
        private SimDriver simDriver;

        [Header("Direct References (Required to avoid Resources.Load)")]
        public ComputeShader fluidComputeShader;

        void Start()
        {
            SetupRenderTextures();
            SetupSimDriver();
            SetupUI(GetDisplayTexture());
            // Optionally keep pattern generator for fallback
            // SetupPatternGenerator(hiResRT);
            SetupRecorder(GetDisplayTexture(), loResRT);
            Debug.Log("[Bootstrap] Scene wired with fluid simulation. Press Space to capture (new Input System).");
        }

        void Update()
        {
            // Blit simulation output to our render textures
            var displayTex = GetDisplayTexture();
            if (displayTex != null)
            {
                if (hiResRT != null)
                    Graphics.Blit(displayTex, hiResRT);
                if (hiResRT != null && loResRT != null)
                    Graphics.Blit(hiResRT, loResRT);
            }
        }

        void SetupRenderTextures()
        {
            hiResRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32) { name = "HiResRT" };
            hiResRT.Create();
            int lw = Mathf.Max(1, width / Mathf.Max(1, loResScale));
            int lh = Mathf.Max(1, height / Mathf.Max(1, loResScale));
            loResRT = new RenderTexture(lw, lh, 0, RenderTextureFormat.ARGB32) { name = "LoResRT" };
            loResRT.Create();
        }

        void SetupUI(RenderTexture rt)
        {
            var canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null)
            {
                var go = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas = go.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                var scaler = go.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            }
            var riGO = new GameObject("Output", typeof(RawImage));
            riGO.transform.SetParent(canvas.transform, false);
            var ri = riGO.GetComponent<RawImage>();
            ri.texture = rt;
            var rtRect = ri.GetComponent<RectTransform>();
            rtRect.anchorMin = Vector2.zero; rtRect.anchorMax = Vector2.one;
            rtRect.offsetMin = Vector2.zero; rtRect.offsetMax = Vector2.zero;
        }

        void SetupPatternGenerator(RenderTexture target)
        {
            var go = new GameObject("TestPattern");
            pattern = go.AddComponent<TestPatternGenerator>();
            pattern.target = target;
        }

        void SetupSimDriver()
        {
            // First, look for an existing SimDriver in the scene
            simDriver = FindFirstObjectByType<SimDriver>();

            if (simDriver != null)
            {
                Debug.Log($"[Bootstrap] Using existing SimDriver: {simDriver.gameObject.name}");

                // Check if it has a compute shader assigned
                if (simDriver.fluidCompute != null)
                {
                    Debug.Log("[Bootstrap] SimDriver already has Fluids.compute shader assigned");
                }
                else
                {
                    Debug.LogWarning("[Bootstrap] Existing SimDriver found but no compute shader assigned");
                    TryLoadAndAssignComputeShader();
                }
            }
            else
            {
                // No existing SimDriver, create a new one
                Debug.Log("[Bootstrap] No existing SimDriver found, creating new one");
                var go = new GameObject("SimDriver");
                simDriver = go.AddComponent<SimDriver>();
                TryLoadAndAssignComputeShader();
            }
        }

        void TryLoadAndAssignComputeShader()
        {
            // Use the directly assigned compute shader
            var computeShader = fluidComputeShader;

            #if UNITY_EDITOR
            if (computeShader == null)
            {
                // Try direct asset loading in editor only
                computeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/com.inktools.sim/Runtime/Compute/Fluids.compute");
                if (computeShader == null)
                {
                    // Try the local project path
                    computeShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                        "Assets/_Project/InkTools.Simulation/Runtime/Compute/Fluids.compute");
                }
            }
            #endif

            if (computeShader != null)
            {
                simDriver.fluidCompute = computeShader;
                Debug.Log("[Bootstrap] Successfully loaded and assigned Fluids.compute shader");
            }
            else
            {
                Debug.LogWarning("[Bootstrap] Could not find Fluids.compute shader to assign. SimDriver will run in test pattern mode.");
            }
        }

        RenderTexture GetDisplayTexture()
        {
            // Use SimDriver output if available, otherwise fall back to hiResRT
            if (simDriver != null)
            {
                var tex = simDriver.GetDisplayTexture();
                if (tex != null) return tex;
            }
            return hiResRT;
        }

        void SetupRecorder(RenderTexture hi, RenderTexture lo)
        {
            var go = new GameObject("Recorder");
            recorder = go.AddComponent<SimulationRecorder>();
            recorder.hiRes = hi;
            recorder.loResPhysics = lo;
            recorder.outputFolder = outputFolder;
            recorder.BeginScenario(scenario);

            var driver = go.AddComponent<CaptureDriver>();
            driver.recorder = recorder;
            driver.scenario = scenario;
        }
    }
}

