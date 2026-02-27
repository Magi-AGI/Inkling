using UnityEngine;
using UnityEngine.InputSystem;
using Magi.InkTools.Simulation;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Systems.Brush
{
    /// <summary>
    /// Handles manual ink injection via mouse/touch/pen using robust UV mapping.
    /// </summary>
    public class BrushInputController : MonoBehaviour
    {
        [Header("Configuration")]
        [SerializeField] private BrushConfig config;

        [Header("Mapping")]
        [SerializeField] private Renderer targetRenderer;
        [SerializeField] private Camera inputCamera;

        private ISimulationWriter simWriter;
        private SimDriver simDriver;
        private bool isInjecting;
        private Vector2 lastUv;
        private bool hasLast;

        private void Start()
        {
            simDriver = FindFirstObjectByType<SimDriver>();
            if (simDriver != null)
            {
                simWriter = simDriver.AsWriter();
            }

            if (config == null)
            {
                config = ScriptableObject.CreateInstance<BrushConfig>();
                Debug.LogWarning("[BrushInputController] No BrushConfig assigned, using defaults.");
            }
        }

        private void Update()
        {
            if (simWriter == null || Mouse.current == null) return;

            bool wasInjecting = isInjecting;
            isInjecting = Mouse.current.leftButton.isPressed;

            if (isInjecting)
            {
                Vector2 screenPos = Mouse.current.position.ReadValue();
                var cam = inputCamera != null ? inputCamera : Camera.main;
                
                Vector2 uv = SimulationUvUtility.ComputeUv(screenPos, targetRenderer, cam, lastUv);

                // Force should be injected continuously while dragging so velocity
                // remains responsive even when stamp spacing is coarse.
                if (hasLast)
                {
                    Vector2 force = (uv - lastUv) * config.forceMultiplier;
                    if (force.sqrMagnitude > 0.000001f)
                    {
                        simWriter.InjectForce(uv, force);
                    }
                }

                bool shouldStamp = !wasInjecting
                    || !hasLast
                    || Vector2.Distance(uv, lastUv) > config.minDistanceUv;

                // Stamp density at configurable spacing to avoid oversaturation.
                if (shouldStamp)
                {
                    PerformInjection(uv, false);
                    if (config.enableMirror)
                    {
                        float mirroredX = config.mirrorAxisX + (config.mirrorAxisX - uv.x);
                        PerformInjection(new Vector2(Mathf.Clamp01(mirroredX), uv.y), true);
                    }
                    lastUv = uv;
                    hasLast = true;
                }
            }
            else
            {
                hasLast = false;
            }
        }

        private void PerformInjection(Vector2 uv, bool mirror)
        {
            int inkType = simDriver != null ? simDriver.CurrentInkType : 0;
            Color color = GetInkKeyColor(inkType);
            
            simWriter.InjectDensity(uv, color, inkType);
        }

        private static Color GetInkKeyColor(int inkTypeIndex)
        {
            switch (Mathf.Clamp(inkTypeIndex, 0, 9))
            {
                case 0: return new Color(1f, 0f, 0f, 1f); // Fire
                case 1: return new Color(0f, 0f, 1f, 1f); // Water
                case 2: return new Color(0f, 1f, 0f, 1f); // PlantSeeded
                case 3: return new Color(0f, 0.5f, 0f, 1f); // PlantGrown
                case 4: return new Color(0.49f, 0.49f, 0.49f, 1f); // Steam
                case 5: return new Color(1f, 0.5f, 1f, 1f); // Glitter
                case 6: return new Color(0.1f, 0.1f, 0.1f, 1f); // BlackBody
                case 7: return new Color(1f, 1f, 0f, 1f); // ElectricitySeeded
                case 8: return new Color(0.5f, 0.5f, 0f, 1f); // ElectricityGrown
                case 9: return new Color(0f, 1f, 1f, 1f); // Ice
                default: return Color.red;
            }
        }
    }
}
