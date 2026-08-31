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

        [Header("Directional emitters (right mouse)")]
        [Tooltip("CP8p: owns the continuous right-mouse emitters. Each uses the ink selected when it was " +
                 "created (CP8s). Auto-created if not assigned.")]
        [SerializeField] private DirectionalEmitterController emitters;

        private ISimulationWriter simWriter;
        private SimDriver simDriver;
        private bool isInjecting;
        private Vector2 lastUv;
        private bool hasLast;
        private Vector2 lastFrameUv;
        private bool hasLastFrame;

        // CP8p: right-mouse drag gesture state (create/remove directional emitters).
        private bool rmbDown;
        private Vector2 rmbStartUv;

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

            if (emitters == null) emitters = gameObject.AddComponent<DirectionalEmitterController>();
            emitters.SetWriter(simWriter);
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

                // Inject velocity in the direction the mouse is moving, proportional to mouse SPEED.
                // Using mouse velocity (UV/sec) rather than per-frame displacement keeps the impulse
                // frame-rate independent: the dt-normalized force path integrates the passed value over
                // the frame, so faster framerates (smaller per-frame deltas) still sum to the same push.
                // Injected every frame while dragging so motion stays responsive regardless of stamp spacing.
                if (hasLastFrame)
                {
                    float dt = Mathf.Clamp(Time.deltaTime, 1e-4f, 0.1f);
                    Vector2 mouseVelUv = (uv - lastFrameUv) / dt;
                    Vector2 force = mouseVelUv * config.forceMultiplier;
                    if (force.sqrMagnitude > 1e-8f)
                    {
                        simWriter.InjectForce(uv, force);
                    }
                }
                lastFrameUv = uv;
                hasLastFrame = true;

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
                hasLastFrame = false;
            }

            UpdateDirectionalEmitters();
        }

        /// <summary>
        /// CP8p right-mouse gesture: press begins a drag, release applies it. A drag through empty space
        /// CREATES a continuous emitter of the currently selected ink pushing along the drag direction
        /// (CP8s — was always Fire in CP8p); a drag whose PATH crosses
        /// an existing emitter REMOVES it instead (removal wins, so a delete gesture cannot also spawn a
        /// duplicate). Uses the same robust SimulationUvUtility mapping as the left-mouse brush — the old
        /// SimDriverDebugInput right-click path used a naive screen-ratio UV and is superseded here.
        ///
        /// Emitters then tick EVERY frame regardless of mouse state — that continuity is the whole point.
        /// </summary>
        private void UpdateDirectionalEmitters()
        {
            if (emitters == null) return;

            var cam = inputCamera != null ? inputCamera : Camera.main;
            bool pressed = Mouse.current.rightButton.isPressed;

            if (pressed && !rmbDown)
            {
                rmbStartUv = SimulationUvUtility.ComputeUv(Mouse.current.position.ReadValue(), targetRenderer, cam, lastUv);
                rmbDown = true;
            }
            else if (!pressed && rmbDown)
            {
                Vector2 endUv = SimulationUvUtility.ComputeUv(Mouse.current.position.ReadValue(), targetRenderer, cam, rmbStartUv);
                // CP8s: emitters use the CURRENTLY SELECTED ink, read from the same source left-click
                // painting uses, so the two input paths cannot drift apart. Read at release time and
                // snapshot into the emitter — changing selection later leaves placed emitters alone.
                emitters.ApplyDragGesture(rmbStartUv, endUv, simDriver != null ? simDriver.CurrentInkType : 0);
                rmbDown = false;
            }

            emitters.Tick();
        }

        private void PerformInjection(Vector2 uv, bool mirror)
        {
            int inkType = simDriver != null ? simDriver.CurrentInkType : 0;
            Color color = GetInkKeyColor(inkType);
            
            simWriter.InjectDensity(uv, color, inkType);
        }

        private static Color GetInkKeyColor(int inkTypeIndex)
        {
            // CP8w: ColdAir carries a colour for UI/gizmo purposes ONLY — its injection path writes no
            // density, so this value never reaches a particle channel. Handled before the 0..9 clamp,
            // which would otherwise report it as Ice's cyan and make the two indistinguishable on screen.
            if (SimulationContext.IsColdSource(inkTypeIndex))
                // Pale mint-frost. NOT (0.7, 0.9, 1) — that literal is already Ice's colour in
                // SimDriverDebugInput, ElementSpriteGenerator and ScenarioDropdownHelper, so using it
                // would make ColdAir read as Ice in exactly the places a reader checks first.
                return new Color(0.8f, 1f, 0.95f, 1f);

            switch (Mathf.Clamp(inkTypeIndex, 0, (int)InkTypeId.Count - 1))
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
                case 10: return new Color(0.6f, 0.6f, 0.65f, 1f); // Metal (placeholder silver; real color in M1)
                default: return Color.red;
            }
        }
    }
}
