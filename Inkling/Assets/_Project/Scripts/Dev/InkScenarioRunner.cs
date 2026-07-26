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
    public partial class InkScenarioRunner : MonoBehaviour
    {
        [Header("Trigger")]
        [SerializeField] private bool runOnStart = false;
        [Tooltip("Set true at runtime (e.g. via MCP) to kick off a run.")]
        public bool runRequested = false;
        [Tooltip("Set true at runtime to run the transport dt-normalization cross-rate test.")]
        public bool runTransportDtRequested = false;

        [Header("Output")]
        [Tooltip("Folder under the project root (sibling of Assets) for captures.")]
        [SerializeField] private string outputSubdir = "InkCaptures";
        [SerializeField] private int captureSize = 512;

        [Header("Scenario timing (fixed steps)")]
        [SerializeField] private int settleSteps = 120;   // total steps the sim is advanced per scenario
        [SerializeField] private int injectSteps = 20;    // steps during which the stimulus is injected

        [Header("Sweep")]
        [SerializeField] private float[] viscositySweep = { 0.0001f, 0.0005f, 0.001f, 0.002f, 0.005f };

        [Header("Transport DT test")]
        [Tooltip("Viscosity used during the Transport DT test. 0 isolates advection; >0 also exercises " +
                 "the dt-normalized diffusion path (stage 2). Captures get a _visc suffix when >0.")]
        public float transportTestViscosity = 0f;
        [Tooltip("Vorticity strength used during the Transport DT test. >0 exercises the dt-normalized " +
                 "vorticity-confinement impulse (stage 4). Captures get a _vort suffix when >0.")]
        public float transportTestVorticity = 0f;
        [Tooltip("Per-frame velocity retention during the Transport DT test. 1 = no damping (clean " +
                 "advection drift). Use <1 for vorticity tests so the energy-pumping confinement " +
                 "reaches a bounded equilibrium instead of saturating the velocity clamp.")]
        public float transportTestVelocityDissipation = 1f;

        [Header("Ink tuning pass")]
        [Tooltip("Label tag for ink-tuning captures (tune_<tag>_fire/water_display.png).")]
        public string tuningTag = "p0";
        [Tooltip("Set true at runtime to capture fire+water under a directional push (vorticity active).")]
        public bool runInkTuningRequested = false;
        [Tooltip("Directional push strength for the ink-tuning pass.")]
        public float tuningPush = 0.2f;
        [Tooltip("Ink index to capture in the tuning pass. -1 = fire+water; otherwise that single ink (0..9).")]
        public int tuningInkOverride = -1;

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
            if (runTransportDtRequested && !running)
            {
                runTransportDtRequested = false;
                TriggerTransportDt();
            }
            if (runInkTuningRequested && !running)
            {
                runInkTuningRequested = false;
                if (!running) StartCoroutine(RunInkTuningPass());
            }
            // CP8p: Fire-vs-Ice evaluation harness (see FireIceScenario.cs).
            if (runFireIceRequested && !running)
            {
                runFireIceRequested = false;
                StartCoroutine(RunFireIceTest());
            }
        }

        /// <summary>
        /// Captures fire and water under a constant directional push with the scene's CURRENT global
        /// solver params (viscosity/vorticity/velocityDissipation active — NOT zeroed). For iterating
        /// on fire/water dynamics: set params via the SimDriver inspector/SerializedObject, set a
        /// tuningTag, trigger, compare tune_<tag>_fire/water captures.
        /// </summary>
        private IEnumerator RunInkTuningPass()
        {
            running = true;
            Application.runInBackground = true;

            var sim = FindFirstObjectByType<SimDriver>();
            if (sim == null) { Debug.LogError("[InkScenarioRunner] No SimDriver in scene."); running = false; yield break; }

            int guard = 0;
            while (sim.GetDisplayTexture() == null && guard++ < 300) yield return null;
            if (sim.GetDisplayTexture() == null) { Debug.LogError("[InkScenarioRunner] Sim never became ready."); running = false; yield break; }

            foreach (var inj in FindObjectsByType<TexturedInjector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                inj.enabled = false;

            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputSubdir));
            Directory.CreateDirectory(root);

            bool prevExternal = sim.ExternalStepControl;
            sim.ExternalStepControl = true;
            sim.SetDisplayVelocity(false);

            var all = new (int idx, string name, Color col)[]
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
            int[] sel = (tuningInkOverride >= 0 && tuningInkOverride < all.Length)
                ? new[] { tuningInkOverride }
                : new[] { 0, 1 };
            var push = new Vector2(tuningPush, 0f);
            foreach (var idx in sel)
            {
                var ink = all[idx];
                sim.ResetSimulation();
                yield return RunStimulus(sim, push, ink.idx, ink.col);
                Capture(sim, root, "tune_" + tuningTag + "_" + ink.name);
                Debug.Log("[InkScenarioRunner] captured tune_" + tuningTag + "_" + ink.name
                    + " (visc=" + sim.Viscosity + " vort=" + sim.Vorticity + " velDiss=" + sim.VelocityDissipation + ")");
            }

            sim.ExternalStepControl = prevExternal;
            running = false;
            Debug.Log("[InkScenarioRunner] INK TUNING DONE tag=" + tuningTag);
        }

        [ContextMenu("Run Scenarios")]
        public void Trigger()
        {
            if (!running) StartCoroutine(RunAll());
        }

        [ContextMenu("Run Transport DT Test")]
        public void TriggerTransportDt()
        {
            if (!running) StartCoroutine(RunTransportDtTest());
        }

        /// <summary>
        /// Cross-rate validation for transport dt-normalization. Builds an IDENTICAL initial
        /// condition (fixed-dt injection burst with a directional push), then settles for the SAME
        /// real sim-time at several timesteps. Viscosity + vorticity are zeroed so advection is the
        /// only dt-dependent transport — if advection is dt-normalized, the drifted plume's centroid
        /// converges across rates (spread differs slightly from semi-Lagrangian numerical diffusion).
        /// </summary>
        private IEnumerator RunTransportDtTest()
        {
            running = true;
            Application.runInBackground = true;

            var sim = FindFirstObjectByType<SimDriver>();
            if (sim == null) { Debug.LogError("[InkScenarioRunner] No SimDriver in scene."); running = false; yield break; }

            int guard = 0;
            while (sim.GetDisplayTexture() == null && guard++ < 300) yield return null;
            if (sim.GetDisplayTexture() == null) { Debug.LogError("[InkScenarioRunner] Sim never became ready."); running = false; yield break; }

            foreach (var inj in FindObjectsByType<TexturedInjector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                inj.enabled = false;

            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", outputSubdir));
            Directory.CreateDirectory(root);
            Debug.Log("[InkScenarioRunner] Transport DT test output -> " + root);

            bool prevExternal = sim.ExternalStepControl;
            sim.ExternalStepControl = true;
            sim.SetDisplayVelocity(false);

            // Isolate ADVECTION: no viscous diffusion, no per-frame vorticity impulse, and no decay
            // (dissipation/velocityDissipation = 1) so the blob's drift is driven purely by advection
            // and scales linearly with advection-time — making the framerate dependence unambiguous.
            float prevVisc = sim.Viscosity, prevVort = sim.Vorticity, prevTs = sim.Timestep;
            float prevDiss = sim.Dissipation, prevVelDiss = sim.VelocityDissipation;
            sim.SetTunable("viscosity", transportTestViscosity);   // 0 = isolate advection; >0 also tests diffusion
            sim.SetTunable("vorticity", transportTestVorticity);   // >0 tests the dt-normalized vorticity impulse
            sim.SetTunable("dissipation", 1f);
            sim.SetTunable("velocityDissipation", transportTestVelocityDissipation);
            string viscSuffix = (transportTestViscosity > 0f ? "_visc" : "")
                              + (transportTestVorticity > 0f ? "_vort" : "");

            // Stimulus: a fluid ink (water, advectionWeight 1) given a GENTLE rightward push so it
            // drifts a measurable distance while staying well inside the domain (no absorbing-boundary
            // mass loss) and at a low Courant number (so per-step numerical diffusion stays small).
            const int inkType = 1;
            Color col = new Color(0f, 0f, 1f);
            Vector2 injectPos = new Vector2(0.35f, 0.5f);
            Vector2 force = new Vector2(0.01f, 0f); // small; SimDriver.forceStrength scales this up internally

            // The solver Timestep stays pinned at the legacy fixed value for the WHOLE test. Only the
            // per-step real frame dt (FrameDeltaTime, via StepSimulation(dt)) varies — exactly the
            // play-mode situation the fix targets (one step/frame at fixed timestep, variable real dt).
            const float fixedDt = 0.016f;
            const int buildSteps = 20;   // density injected for this many steps (builds the blob)
            const int forceSteps = 4;    // force injected only for the first few steps (brief impulse)
            const float realTime = 1.0f; // real seconds of settling, held constant across framerates
            const float maxSubstepDt = 0.016f; // mirrors SimDriver.maxSubstepDt for the substep variant
            const int maxSubsteps = 8;
            sim.SetTunable("timestep", fixedDt);

            // Emulated framerates. Over the SAME real time, a higher framerate takes more (smaller) steps.
            var fpsCases = new (float fps, string tag)[] { (20f, "fps020"), (120f, "fps120") };

            // For each framerate, run THREE variants over identical initial conditions:
            //   OLD    = advect by the fixed timestep every step (pre-fix: framerate-dependent)
            //   NEW    = advect by the real frame dt             (post-fix: framerate-independent, but
            //            coarse at low fps where the single step has a high Courant number)
            //   NEWSUB = NEW with SimDriver-style substepping    (splits the frame dt to <= maxSubstepDt,
            //            recovering low-Courant accuracy at low fps)
            foreach (var fc in fpsCases)
            {
                int n = Mathf.Max(1, Mathf.RoundToInt(fc.fps * realTime));
                float realDt = 1f / fc.fps;

                // OLD emulation: every step advects by fixedDt regardless of real frame time.
                sim.ResetSimulation();
                yield return BuildInitialCondition(sim, injectPos, col, inkType, force, buildSteps, forceSteps, fixedDt);
                for (int i = 0; i < n; i++)
                {
                    sim.StepSimulation(fixedDt);
                    if ((i & 15) == 15) { sim.RefreshDisplay(); yield return null; }
                }
                sim.RefreshDisplay(); yield return null;
                CaptureTransport(sim, root, "transport_old_" + fc.tag + viscSuffix, fixedDt, n, realTime);
                Debug.Log("[InkScenarioRunner] captured transport_old_" + fc.tag + viscSuffix + " (n=" + n + ", advTime=" + (n * fixedDt) + ")");

                // NEW: each step advects by the real frame dt, so advection-time == real time.
                sim.ResetSimulation();
                yield return BuildInitialCondition(sim, injectPos, col, inkType, force, buildSteps, forceSteps, fixedDt);
                for (int i = 0; i < n; i++)
                {
                    sim.StepSimulation(realDt);
                    if ((i & 15) == 15) { sim.RefreshDisplay(); yield return null; }
                }
                sim.RefreshDisplay(); yield return null;
                CaptureTransport(sim, root, "transport_new_" + fc.tag + viscSuffix, realDt, n, realTime);
                Debug.Log("[InkScenarioRunner] captured transport_new_" + fc.tag + viscSuffix + " (n=" + n + ", advTime=" + (n * realDt) + ")");

                // NEWSUB: each frame's real dt is split into <= maxSubstepDt substeps (mirrors
                // SimDriver.SimulateFrameSubstepped) so per-step Courant stays low even at 20 fps.
                int sub = (maxSubstepDt > 0f && realDt > maxSubstepDt) ? Mathf.CeilToInt(realDt / maxSubstepDt) : 1;
                sub = Mathf.Clamp(sub, 1, maxSubsteps);
                float subDt = realDt / sub;
                sim.ResetSimulation();
                yield return BuildInitialCondition(sim, injectPos, col, inkType, force, buildSteps, forceSteps, fixedDt);
                for (int i = 0; i < n; i++)
                {
                    for (int s = 0; s < sub; s++) sim.StepSimulation(subDt);
                    if ((i & 15) == 15) { sim.RefreshDisplay(); yield return null; }
                }
                sim.RefreshDisplay(); yield return null;
                CaptureTransport(sim, root, "transport_newsub_" + fc.tag + viscSuffix, subDt, n * sub, realTime);
                Debug.Log("[InkScenarioRunner] captured transport_newsub_" + fc.tag + viscSuffix + " (n=" + n + " x sub=" + sub + ", advTime=" + (n * realDt) + ")");
            }

            sim.SetTunable("viscosity", prevVisc);
            sim.SetTunable("vorticity", prevVort);
            sim.SetTunable("dissipation", prevDiss);
            sim.SetTunable("velocityDissipation", prevVelDiss);
            sim.SetTunable("timestep", prevTs);
            sim.ExternalStepControl = prevExternal;
            running = false;
            Debug.Log("[InkScenarioRunner] TRANSPORT DT DONE -> " + root);
        }

        /// <summary>Builds the identical blob+impulse initial condition (fixed dt) for a transport test variant.</summary>
        private IEnumerator BuildInitialCondition(SimDriver sim, Vector2 pos, Color col, int inkType,
            Vector2 force, int buildSteps, int forceSteps, float dt)
        {
            for (int i = 0; i < buildSteps; i++)
            {
                sim.InjectDensity(pos, col, inkType);
                if (i < forceSteps) sim.InjectForce(pos, force);
                sim.StepSimulation(dt);
                if ((i & 15) == 15) { sim.RefreshDisplay(); yield return null; }
            }
        }

        private void CaptureTransport(SimDriver sim, string root, string label, float dt, int steps, float realTime)
        {
            SaveRT(sim.GetDisplayTexture(), Path.Combine(root, label + "_display.png"), false, out _, out _);
            SaveRT(sim.GetVelocityTexture(), Path.Combine(root, label + "_velocity.png"), true, out float avgSpeed, out float maxSpeed);
            MeasureCentroid(sim.GetDisplayTexture(), out float cx, out float cy, out float mass);
            var ci = CultureInfo.InvariantCulture;
            string json = "{"
                + "\"label\":\"" + label + "\","
                + "\"dt\":" + dt.ToString(ci) + ","
                + "\"steps\":" + steps + ","
                + "\"advectionTime\":" + (steps * dt).ToString(ci) + ","
                + "\"realTime\":" + realTime.ToString(ci) + ","
                + "\"centroidX\":" + cx.ToString(ci) + ","
                + "\"centroidY\":" + cy.ToString(ci) + ","
                + "\"mass\":" + mass.ToString(ci) + ","
                + "\"avgSpeed\":" + avgSpeed.ToString(ci) + ","
                + "\"maxSpeed\":" + maxSpeed.ToString(ci)
                + "}";
            File.WriteAllText(Path.Combine(root, label + ".json"), json);
        }

        /// <summary>Luminance-weighted centroid (UV 0..1) and total luminance of an RT, for cross-rate drift comparison.</summary>
        private void MeasureCentroid(RenderTexture rt, out float cx, out float cy, out float mass)
        {
            cx = 0f; cy = 0f; mass = 0f;
            if (rt == null) return;
            var tmp = RenderTexture.GetTemporary(captureSize, captureSize, 0, RenderTextureFormat.ARGBFloat);
            Graphics.Blit(rt, tmp);
            var prev = RenderTexture.active;
            RenderTexture.active = tmp;
            var tex = new Texture2D(captureSize, captureSize, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(0, 0, captureSize, captureSize), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp);
            var px = tex.GetPixels();
            double sum = 0, sx = 0, sy = 0;
            for (int y = 0; y < captureSize; y++)
            {
                for (int x = 0; x < captureSize; x++)
                {
                    Color p = px[y * captureSize + x];
                    float lum = 0.299f * p.r + 0.587f * p.g + 0.114f * p.b;
                    sum += lum;
                    sx += lum * x;
                    sy += lum * y;
                }
            }
            Destroy(tex);
            mass = (float)sum;
            if (sum > 1e-6)
            {
                cx = (float)(sx / sum) / captureSize;
                cy = (float)(sy / sum) / captureSize;
            }
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

        [MenuItem("Inkling/Run Transport DT Test")]
        private static void RunTransportDtMenu()
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
            runner.TriggerTransportDt();
        }
#endif
    }
}
