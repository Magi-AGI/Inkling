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

            Vector2 uv = new Vector2(
                Mathf.Clamp01(mouse.position.ReadValue().x / Screen.width),
                Mathf.Clamp01(mouse.position.ReadValue().y / Screen.height));

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
    }
}
