using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
using Magi.Inkling.Systems.SimulationLOD0;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Magi.Inkling.Dev
{
    /// <summary>
    /// Deterministic, automated scenario runner for the LOD0 ink simulation.
    /// Drives the sim with full external step control (no real-time dependence), injects known
    /// stimuli, advances a fixed number of steps, and writes labeled PNG + JSON captures.
    ///
    /// Doubles as the seed of the LOD0 training-data capture pipeline. Triggered via the
    /// "Inkling/Run Ink Scenarios" menu (in play mode), the component context menu, or by
    /// setting <see cref="runRequested"/> (e.g. from an MCP command).
    /// </summary>
    public class InkScenarioRunner : MonoBehaviour
    {
        [Header("Trigger")]
        [SerializeField] private bool runOnStart = false;
        [Tooltip("Set true at runtime (e.g. via MCP) to kick off a run.")]
        public bool runRequested = false;

        [Header("Output")]
        [Tooltip("Folder under the project root (sibling of Assets) for captures.")]
        [SerializeField] private string outputSubdir = "InkCaptures";
        [SerializeField] private int captureSize = 512;

        [Header("Scenario timing (fixed steps)")]
        [SerializeField] private int settleSteps = 120;   // total steps the sim is advanced per scenario
        [SerializeField] private int injectSteps = 20;    // steps during which the stimulus is injected

        [Header("Sweep")]
        [SerializeField] private float[] viscositySweep = { 0.0001f, 0.0005f, 0.001f, 0.002f, 0.005f };

        private bool running;

        private void Start()
        {
            if (runOnStart) Trigger();
        }

        private void Update()
        {
            if (runRequested && !running)
            {
                runRequested = false;
                Trigger();
            }
        }

        [ContextMenu("Run Scenarios")]
        public void Trigger()
        {
            if (!running) StartCoroutine(RunAll());
        }

        private IEnumerator RunAll()
        {
            running = true;
            // Keep play mode advancing even when the Unity editor is unfocused (required for
            // automated/headless runs driven via MCP, otherwise the sim freezes and the coroutine stalls).
            Application.runInBackground = true;

            var sim = FindFirstObjectByType<SimDriver>();
            if (sim == null) { Debug.LogError("[InkScenarioRunner] No SimDriver in scene."); running = false; yield break; }

            // Wait until the sim has allocated its render targets.
            int guard = 0;
            while (sim.GetDisplayTexture() == null && guard++ < 300) yield return null;
            if (sim.GetDisplayTexture() == null) { Debug.LogError("[InkScenarioRunner] Sim never became ready."); running = false; yield break; }

            // Neutralize autonomous injectors so the only stimulus is ours.
            foreach (var inj in FindObjectsByType<TexturedInjector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                inj.enabled = false;

            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputSubdir));
            Directory.CreateDirectory(root);
            Debug.Log("[InkScenarioRunner] Output -> " + root);

            bool prevExternal = sim.ExternalStepControl;
            sim.ExternalStepControl = true;
            sim.SetDisplayVelocity(false); // DisplayRT shows ink; velocity is captured separately via GetVelocityTexture

            // ---- Scenario set 1: viscosity sweep on a constant force + ink stimulus (motion) ----
            float baseVisc = sim.Viscosity;
            foreach (var v in viscositySweep)
            {
                sim.ResetSimulation();
                sim.SetTunable("viscosity", v);
                yield return RunStimulus(sim, new Vector2(0.1f, 0.05f), 0, Color.red);
                Capture(sim, root, "sweep_visc_" + Fmt(v));
                Debug.Log("[InkScenarioRunner] captured sweep_visc " + v);
            }
            sim.SetTunable("viscosity", baseVisc);

            // ---- Scenario set 2: per-ink blob library (diffusion/dissipation character) ----
            var inks = new (int idx, string name, Color col)[]
            {
                (0, "fire",        new Color(1f, 0f, 0f)),
                (1, "water",       new Color(0f, 0f, 1f)),
                (2, "plantSeeded", new Color(0f, 1f, 0f)),
                (3, "plantGrown",  new Color(0f, 0.5f, 0f)),
                (4, "steam",       new Color(0.49f, 0.49f, 0.49f)),
                (5, "glitter",     new Color(1f, 0.5f, 1f)),
                (6, "blackBody",   new Color(0.1f, 0.1f, 0.1f)),
                (7, "elecSeeded",  new Color(1f, 1f, 0f)),
                (8, "elecGrown",   new Color(0.5f, 0.5f, 0f)),
                (9, "ice",         new Color(0f, 1f, 1f)),
            };
            foreach (var ink in inks)
            {
                sim.ResetSimulation();
                yield return RunStimulus(sim, Vector2.zero, ink.idx, ink.col);
                Capture(sim, root, "ink_" + ink.idx.ToString("D2") + "_" + ink.name);
                Debug.Log("[InkScenarioRunner] captured ink " + ink.name);
            }

            // ---- Scenario set 3: per-ink blob under a constant directional push (motion character) ----
            // A horizontal force at the blob reveals advection (fire/water/etc. streak) vs. obstacle
            // behavior (plant/ice/blackBody have advectionWeight 0 and stay put as the flow moves around them).
            var pushForce = new Vector2(0.2f, 0f);
            foreach (var ink in inks)
            {
                sim.ResetSimulation();
                yield return RunStimulus(sim, pushForce, ink.idx, ink.col);
                Capture(sim, root, "inkforce_" + ink.idx.ToString("D2") + "_" + ink.name);
                Debug.Log("[InkScenarioRunner] captured inkforce " + ink.name);
            }

            sim.ExternalStepControl = prevExternal;
            running = false;
            Debug.Log("[InkScenarioRunner] DONE -> " + root);
        }

        private IEnumerator RunStimulus(SimDriver sim, Vector2 force, int inkType, Color color)
        {
            bool injectForce = force.sqrMagnitude > 0f;
            for (int i = 0; i < settleSteps; i++)
            {
                if (i < injectSteps)
                {
                    sim.InjectDensity(new Vector2(0.5f, 0.5f), color, inkType);
                    if (injectForce) sim.InjectForce(new Vector2(0.5f, 0.5f), force);
                }
                sim.StepSimulation();
                // Breathe periodically so the editor stays responsive. Determinism is preserved
                // because ExternalStepControl suspends SimDriver's own auto-stepping.
                if ((i & 15) == 15) { sim.RefreshDisplay(); yield return null; }
            }
            sim.RefreshDisplay();
            yield return null; // allow GPU to settle before readback
        }

        private void Capture(SimDriver sim, string root, string label)
        {
            SaveRT(sim.GetDisplayTexture(), Path.Combine(root, label + "_display.png"), false, out _, out _);
            SaveRT(sim.GetVelocityTexture(), Path.Combine(root, label + "_velocity.png"), true, out float avgSpeed, out float maxSpeed);
            File.WriteAllText(Path.Combine(root, label + ".json"), Metadata(sim, label, avgSpeed, maxSpeed));
        }

        /// <summary>Saves a PNG of the RT; when measureSpeed, also returns avg/max |xy| (real magnitudes, unclamped).</summary>
        private void SaveRT(RenderTexture rt, string path, bool measureSpeed, out float avgSpeed, out float maxSpeed)
        {
            avgSpeed = 0f; maxSpeed = 0f;
            if (rt == null) return;
            var fmt = measureSpeed ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32;
            var tmp = RenderTexture.GetTemporary(captureSize, captureSize, 0, fmt);
            Graphics.Blit(rt, tmp);
            var prev = RenderTexture.active;
            RenderTexture.active = tmp;
            var texFmt = measureSpeed ? TextureFormat.RGBAFloat : TextureFormat.RGBA32;
            var tex = new Texture2D(captureSize, captureSize, texFmt, false);
            tex.ReadPixels(new Rect(0, 0, captureSize, captureSize), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            if (measureSpeed)
            {
                var px = tex.GetPixels();
                double sum = 0; float mx = 0f;
                foreach (var p in px)
                {
                    float s = Mathf.Sqrt(p.r * p.r + p.g * p.g);
                    sum += s; if (s > mx) mx = s;
                }
                avgSpeed = (float)(sum / px.Length); maxSpeed = mx;
            }
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
        }

        private string Metadata(SimDriver sim, string label, float avgSpeed, float maxSpeed)
        {
            var ci = CultureInfo.InvariantCulture;
            return "{"
                + "\"label\":\"" + label + "\","
                + "\"resolution\":" + sim.Resolution + ","
                + "\"viscosity\":" + sim.Viscosity.ToString(ci) + ","
                + "\"vorticity\":" + sim.Vorticity.ToString(ci) + ","
                + "\"dissipation\":" + sim.Dissipation.ToString(ci) + ","
                + "\"velocityDissipation\":" + sim.VelocityDissipation.ToString(ci) + ","
                + "\"timestep\":" + sim.Timestep.ToString(ci) + ","
                + "\"settleSteps\":" + settleSteps + ","
                + "\"injectSteps\":" + injectSteps + ","
                + "\"avgSpeed\":" + avgSpeed.ToString(ci) + ","
                + "\"maxSpeed\":" + maxSpeed.ToString(ci)
                + "}";
        }

        private static string Fmt(float v) => v.ToString("0.0000", CultureInfo.InvariantCulture).Replace(".", "p");

#if UNITY_EDITOR
        [MenuItem("Inkling/Run Ink Scenarios")]
        private static void RunMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[InkScenarioRunner] Enter Play mode first, then run this menu.");
                return;
            }
            var runner = FindFirstObjectByType<InkScenarioRunner>();
            if (runner == null)
            {
                var go = new GameObject("InkScenarioRunner");
                runner = go.AddComponent<InkScenarioRunner>();
            }
            runner.Trigger();
        }
#endif
    }
}
