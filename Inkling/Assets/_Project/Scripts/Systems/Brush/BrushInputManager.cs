using UnityEngine;
using UnityEngine.InputSystem;
using Magi.Inkling.Services;

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

        private ISimulationWriter writer;
        private Vector2 lastPrimaryUv;
        private bool hasLastPrimary;
        private Vector2 lastMirrorUv;
        private bool hasLastMirror;

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
        }

        private void Update()
        {
            if (Mouse.current == null || writer == null) return;

            var mouse = Mouse.current;
            if (!mouse.leftButton.isPressed)
            {
                hasLastPrimary = false;
                hasLastMirror = false;
                return;
            }

            Vector2 uv = ComputeUv(mouse.position.ReadValue());

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

            var color = Color.white * config.densityMultiplier;
            writer.InjectDensity(uv, color, 0); // default ink channel; higher-level gesture/action maps will override

            if (hasLast)
            {
                Vector2 delta = (uv - lastUv) * config.forceMultiplier;
                if (mirror && config.mirrorInvertForceX)
                    delta.x = -delta.x;
                writer.InjectForce(uv, delta);
            }

            hasLast = true;
            lastUv = uv;

            if (config.verboseLogging)
            {
                Debug.Log($"[BrushInputManager] Inject {(mirror ? \"(mirror)\" : \"(primary)\")} uv={uv} color={color}");
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
    }
}
