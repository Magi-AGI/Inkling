using UnityEngine;
using UnityEngine.InputSystem;
using Magi.Inkling.Services;
using Magi.Inkling.Services.Core;
using Magi.InkTools.ITUMS;
using Magi.Inkling.Services.ITUMS;

namespace Magi.Inkling.Systems.Brush
{
    /// <summary>
    /// Minimal brush input manager.
    /// Reads pointer input, injects density and optional force into the simulation via ISimulationWriter.
    /// This is a scaffold for Phase 7B; stroke smoothing/gestures will be layered on later.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class BrushInputManager : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private MonoBehaviour simulationWriterSource; // ISimulationWriter provider (e.g., SimDriver)
        [SerializeField] private BrushConfig config;
        [Tooltip("Optional renderer used to map screen position to simulation UV (e.g., display quad). If null, falls back to screen-normalized UV.")]
        [SerializeField] private Renderer targetRenderer;
        [Tooltip("Camera used for screen-to-world mapping when targetRenderer is set. Defaults to Camera.main.")]
        [SerializeField] private Camera inputCamera;
        [Header("ITUMS Telemetry")]
        [SerializeField] private bool emitPersonaTelemetry = true;
        [Header("Persona Response")]
        [SerializeField] private float quietDensityScale = 0.75f;
        [SerializeField] private float aggressiveDensityScale = 1.5f;
        [SerializeField] private float quietForceScale = 0.75f;
        [SerializeField] private float aggressiveForceScale = 1.5f;
        [Header("ITUMS Logger (optional)")]
        [SerializeField] private ITUMSEventLogger itumsLogger;

        private ISimulationWriter writer;
        private Vector2 lastPrimaryUv;
        private bool hasLastPrimary;
        private Vector2 lastMirrorUv;
        private bool hasLastMirror;
        private IPersonaService personaService;
        private float personaDensityScale = 1f;
        private float personaForceScale = 1f;

        private void Awake()
        {
            if (simulationWriterSource is ISimulationWriter w)
                writer = w;

            if (writer == null)
            {
                Debug.LogWarning("[BrushInputManager] ISimulationWriter not assigned; brush input disabled.");
                enabled = false;
            }

            if (config == null)
            {
                config = ScriptableObject.CreateInstance<BrushConfig>();
            }

            if (emitPersonaTelemetry)
            {
                personaService = ServiceLocator.Instance?.Resolve<IPersonaService>();
                if (itumsLogger == null)
                {
                    itumsLogger = ServiceLocator.Instance?.Resolve<ITUMSEventLogger>();
                }
                if (personaService != null)
                {
                    personaService.OnPersonaChanged += HandlePersonaChanged;
                }
            }
        }

        private void Update()
        {
            if (Mouse.current == null || writer == null) return;

            var mouse = Mouse.current;
            if (!mouse.leftButton.isPressed)
            {
                if (emitPersonaTelemetry && personaService != null)
                {
                    personaService.RecordIdle(Time.deltaTime);
                }
                hasLastPrimary = false;
                hasLastMirror = false;
                return;
            }

            Vector2 uv = ComputeUv(mouse.position.ReadValue());
            if (emitPersonaTelemetry && personaService != null && hasLastPrimary)
            {
                float speed = Vector2.Distance(uv, lastPrimaryUv) / Mathf.Max(Time.deltaTime, 0.0001f);
                personaService.RecordStrokeSpeed(speed);
            }
            if (emitPersonaTelemetry && itumsLogger != null && hasLastPrimary)
            {
                float speed = Vector2.Distance(uv, lastPrimaryUv) / Mathf.Max(Time.deltaTime, 0.0001f);
                itumsLogger.LogStrokeSample(lastPrimaryUv, uv, speed, mirror: false);
            }

            InjectStrokePair(uv);
        }

        private void InjectStrokePair(Vector2 uv)
        {
            InjectStrokeSingle(uv, ref lastPrimaryUv, ref hasLastPrimary, mirror: false);

            if (config.enableMirror)
            {
                float mirroredX = config.mirrorAxisX + (config.mirrorAxisX - uv.x);
                var uvMirror = new Vector2(Mathf.Clamp01(mirroredX), uv.y);
                InjectStrokeSingle(uvMirror, ref lastMirrorUv, ref hasLastMirror, mirror: true);
            }
        }

        private void InjectStrokeSingle(Vector2 uv, ref Vector2 lastUv, ref bool hasLast, bool mirror)
        {
            // Min-distance gating
            if (hasLast && config.minDistanceUv > 0f && Vector2.Distance(uv, lastUv) < config.minDistanceUv)
                return;

            var color = Color.white * (config.densityMultiplier * personaDensityScale);
            writer.InjectDensity(uv, color, 0); // default ink channel; higher-level gesture/action maps will override

            if (hasLast)
            {
                Vector2 delta = (uv - lastUv) * config.forceMultiplier * personaForceScale;
                if (mirror && config.mirrorInvertForceX)
                    delta.x = -delta.x;
                writer.InjectForce(uv, delta);
            }

            hasLast = true;
            lastUv = uv;

            if (config.verboseLogging)
            {
                Debug.Log($"[BrushInputManager] Inject {(mirror ? "(mirror)" : "(primary)")} uv={uv} color={color}");
            }
        }

        private Vector2 ComputeUv(Vector2 screenPos)
        {
            if (targetRenderer == null)
            {
                return new Vector2(
                    Mathf.Clamp01(screenPos.x / Screen.width),
                    Mathf.Clamp01(screenPos.y / Screen.height));
            }

            var cam = inputCamera != null ? inputCamera : Camera.main;
            if (cam == null)
            {
                return new Vector2(
                    Mathf.Clamp01(screenPos.x / Screen.width),
                    Mathf.Clamp01(screenPos.y / Screen.height));
            }

            Ray ray = cam.ScreenPointToRay(screenPos);
            Plane plane = new Plane(targetRenderer.transform.forward, targetRenderer.transform.position);
            if (!plane.Raycast(ray, out float dist))
                return Vector2.zero;

            Vector3 hit = ray.GetPoint(dist);
            Bounds b = targetRenderer.bounds;
            float u = Mathf.InverseLerp(b.min.x, b.max.x, hit.x);
            float v = Mathf.InverseLerp(b.min.y, b.max.y, hit.y);
            return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
        }

        private void HandlePersonaChanged(Persona prev, Persona current, float quietScore, float avgStroke)
        {
            switch (current)
            {
                case Persona.Quiet:
                    personaDensityScale = quietDensityScale;
                    personaForceScale = quietForceScale;
                    break;
                case Persona.Aggressive:
                    personaDensityScale = aggressiveDensityScale;
                    personaForceScale = aggressiveForceScale;
                    break;
                default:
                    personaDensityScale = 1f;
                    personaForceScale = 1f;
                    break;
            }
            itumsLogger?.LogAdaptiveResponse("brush_scaling", current, personaDensityScale, "BrushInputManager");
        }

        private void OnDestroy()
        {
            if (personaService != null)
            {
                personaService.OnPersonaChanged -= HandlePersonaChanged;
            }
        }
    }
}
