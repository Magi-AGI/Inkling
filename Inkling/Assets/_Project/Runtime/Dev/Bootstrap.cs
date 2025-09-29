using UnityEngine;
using UnityEngine.UI;
using Magi.Inkling.Runtime.Systems.SimulationLOD0;

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

        void Start()
        {
            SetupRenderTextures();
            SetupUI(hiResRT);
            SetupPatternGenerator(hiResRT);
            SetupRecorder(hiResRT, loResRT);
            Debug.Log("[Bootstrap] Scene wired. Press Space to capture (new Input System).");
        }

        void Update()
        {
            // Downsample hi-res into lo-res each frame so recorder has both
            if (hiResRT != null && loResRT != null)
            {
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

