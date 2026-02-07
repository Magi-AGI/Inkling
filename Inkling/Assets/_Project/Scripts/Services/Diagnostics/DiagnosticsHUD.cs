using UnityEngine;
using Magi.UnityTools.Patterns;
using Magi.InkTools.ITUMS;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Services.Diagnostics
{
    /// <summary>
    /// Lightweight on-screen diagnostics HUD (toggle via enabled flag).
    /// Shows frame timings and capture status when available.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class DiagnosticsHUD : MonoBehaviour
    {
        [SerializeField] private bool show = true;
        [SerializeField] private Color textColor = Color.white;
        [SerializeField] private LogSink logSink;
        [SerializeField] private KeyCode toggleKey = KeyCode.F9;
        [SerializeField] private IPersonaService personaService;
        [SerializeField] private bool showPersona = true;
        [Header("Debug Visuals")]
        [SerializeField] private TracerSystem tracerSystem;
#if INKTOOLS_DEBUG
        [SerializeField] private VelocityArrowsRenderer arrowsRenderer;
#endif
        [SerializeField] private VelocityStatsSystem velocityStats;
#if INKTOOLS_DEBUG
        [SerializeField] private VelocityMaskRenderer velocityMask;
#endif
        [SerializeField] private bool showTracerToggle = true;
        [SerializeField] private bool showArrowsToggle = true;
        [SerializeField] private bool showMaskToggle = true;
        [SerializeField] private bool showMaskPreview = false;
#if INKTOOLS_DEBUG
        [SerializeField] private SplitVelocityRenderer splitRenderer;
#endif
        [SerializeField] private bool showSplitToggle = false;
#if INKTOOLS_DEBUG
        [SerializeField] private PressureOverlayRenderer pressureRenderer;
#endif
        [SerializeField] private bool showPressureToggle = false;
        [SerializeField] private bool showAirDebugToggles = false;
        [SerializeField] private bool showStats = true;

        private ISimulationReader sim;
        private float lastFrameMs;
        private (float adv, float diff, float press, float proj, float vort) timings;

        private void Start()
        {
            sim = ServiceLocator.Instance?.Resolve<ISimulationReader>();
            if (logSink == null)
                logSink = ServiceLocator.Instance?.Resolve<LogSink>();
            if (personaService == null)
                personaService = ServiceLocator.Instance?.Resolve<IPersonaService>();
            if (tracerSystem == null)
                tracerSystem = FindAnyObjectByType<TracerSystem>();
#if INKTOOLS_DEBUG
            if (arrowsRenderer == null)
                arrowsRenderer = FindAnyObjectByType<VelocityArrowsRenderer>();
#endif
            if (velocityStats == null)
                velocityStats = FindAnyObjectByType<VelocityStatsSystem>();
#if INKTOOLS_DEBUG
            if (velocityMask == null)
                velocityMask = FindAnyObjectByType<VelocityMaskRenderer>();
            if (splitRenderer == null)
                splitRenderer = FindAnyObjectByType<SplitVelocityRenderer>();
            if (pressureRenderer == null)
                pressureRenderer = FindAnyObjectByType<PressureOverlayRenderer>();
#endif
        }

        private void Update()
        {
            if (toggleKey != KeyCode.None && Input.GetKeyDown(toggleKey))
            {
                show = !show;
            }

            if (sim != null)
            {
                lastFrameMs = sim.GetLastFrameMs();
                timings = sim.GetDetailedTimings();
            }
        }

        private void OnGUI()
        {
            if (!show) return;

            GUI.color = textColor;
            GUILayout.BeginArea(new Rect(10, 10, 320, 200), GUI.skin.box);
            GUILayout.Label($"Frame ms: {lastFrameMs:F2}");
            GUILayout.Label($"Adv/Diff/Press/Proj/Vort: {timings.adv:F2}/{timings.diff:F2}/{timings.press:F2}/{timings.proj:F2}/{timings.vort:F2}");
            if (showPersona && personaService != null)
            {
                GUILayout.Label($"Persona: {personaService.CurrentPersona} (quiet={personaService.QuietScore:F2}s, avgStroke={personaService.AggressiveScore:F3} u/s)");
            }
            if (showStats && velocityStats != null)
            {
                GUILayout.Label($"Avg Vel: {velocityStats.AverageVelocity} | Avg Speed: {velocityStats.AverageSpeed:F3}");
            }
            if (showTracerToggle && tracerSystem != null)
            {
                bool newRender = GUILayout.Toggle(tracerSystem.enabled && tracerSystem.isActiveAndEnabled, "Render Tracers");
                tracerSystem.enabled = newRender;
            }
#if INKTOOLS_DEBUG
            if (showArrowsToggle && arrowsRenderer != null)
            {
                bool newRender = GUILayout.Toggle(arrowsRenderer.enabled, "Render Velocity Arrows");
                arrowsRenderer.enabled = newRender;
            }
            if (showMaskToggle && velocityMask != null)
            {
                bool newRender = GUILayout.Toggle(velocityMask.enabled, "Render Velocity Mask");
                velocityMask.enabled = newRender;
                if (showMaskPreview && velocityMask.enabled)
                {
                    var rt = velocityMask.Output;
                    if (rt != null)
                        GUILayout.Box(rt, GUILayout.Width(128), GUILayout.Height(128));
                }
            }
            if (showSplitToggle && splitRenderer != null)
            {
                bool newRender = GUILayout.Toggle(splitRenderer.enabled, "Render Velocity Split (div/curl)");
                splitRenderer.enabled = newRender;
                if (showMaskPreview && splitRenderer.enabled && splitRenderer.Output != null)
                {
                    GUILayout.Box(splitRenderer.Output, GUILayout.Width(128), GUILayout.Height(128));
                }
            }
            if (showPressureToggle && pressureRenderer != null)
            {
                bool newRender = GUILayout.Toggle(pressureRenderer.enabled, "Render Pressure Overlay");
                pressureRenderer.enabled = newRender;
                if (showMaskPreview && pressureRenderer.enabled && pressureRenderer.Output != null)
                {
                    GUILayout.Box(pressureRenderer.Output, GUILayout.Width(128), GUILayout.Height(128));
                }
            }
#endif
            if (showAirDebugToggles)
            {
                var simDebug = sim as Magi.Inkling.Systems.SimulationLOD0.ISimulationDebug;
                if (simDebug != null)
                {
                    var zeroP = GUILayout.Toggle(simDebug.DebugZeroPressure, "Air: Zero Pressure");
                    var zeroV = GUILayout.Toggle(simDebug.DebugZeroVelocity, "Air: Zero Velocity");
                    var skip = GUILayout.Toggle(simDebug.DebugSkipAir, "Air: Skip Air Update");
                    simDebug.DebugZeroPressure = zeroP;
                    simDebug.DebugZeroVelocity = zeroV;
                    simDebug.DebugSkipAir = skip;
                }
            }
            if (logSink != null)
            {
                GUILayout.Label("Recent logs:");
                foreach (var e in logSink.GetEntries())
                {
                    GUILayout.Label($"- {e}");
                }
            }
            GUILayout.EndArea();
        }
    }
}
