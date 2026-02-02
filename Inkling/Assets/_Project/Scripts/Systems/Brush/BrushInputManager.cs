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
        private Vector2 lastUv;
        private bool hasLast;

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
                hasLast = false;
                return;
            }

            Vector2 uv = new Vector2(
                Mathf.Clamp01(mouse.position.ReadValue().x / Screen.width),
                Mathf.Clamp01(mouse.position.ReadValue().y / Screen.height));

            InjectStroke(uv);
        }

        private void InjectStroke(Vector2 uv)
        {
            // Density injection at pointer
            var color = Color.white * config.densityMultiplier;
            writer.InjectDensity(uv, color, 0); // default to fire; gesture/action map will refine later

            // Velocity injection based on drag delta
            if (hasLast)
            {
                Vector2 delta = (uv - lastUv) * config.forceMultiplier;
                writer.InjectForce(uv, delta);
            }

            hasLast = true;
            lastUv = uv;

            if (config.verboseLogging)
            {
                Debug.Log($"[BrushInputManager] Inject at {uv} color {color} delta {(hasLast ? (uv - lastUv) : Vector2.zero)}");
            }
        }
    }
}
