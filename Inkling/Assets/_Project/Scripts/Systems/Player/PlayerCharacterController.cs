using UnityEngine;
using UnityEngine.InputSystem;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Systems.Player
{
    /// <summary>
    /// Gives the player a visible ink avatar that tracks the mouse cursor.
    /// Drives a TexturedInjector so the player appears in the simulation.
    /// Uses SimulationUvUtility for robust screen-to-UV mapping.
    /// </summary>
    [DefaultExecutionOrder(-55)] // Before TexturedInjector (-50)
    [RequireComponent(typeof(TexturedInjector))]
    public class PlayerCharacterController : MonoBehaviour, IPlayerCharacter
    {
        [Header("UV Mapping")]
        [Tooltip("Renderer used to map screen position to simulation UV.")]
        [SerializeField] private Renderer targetRenderer;
        [Tooltip("Camera used for mapping. Defaults to Camera.main.")]
        [SerializeField] private Camera inputCamera;

        private TexturedInjector injector;
        private SimDriver simDriver;
        private Vector2 currentUV = new Vector2(0.5f, 0.5f);
        private bool mousePresent;
        private int appliedInkType = -1;

        #region IPlayerCharacter

        public Vector2 PositionUV => currentUV;
        public int ActiveInkType => simDriver != null ? simDriver.CurrentInkType : 0;
        public bool IsActive => mousePresent;

        #endregion

        private void Awake()
        {
            injector = GetComponent<TexturedInjector>();
        }

        private void Start()
        {
            injector.ExternallyControlled = true;
            simDriver = FindFirstObjectByType<SimDriver>();
            ApplyInkSelectionIfChanged();
        }

        private void Update()
        {
            if (Mouse.current == null)
            {
                mousePresent = false;
                return;
            }

            mousePresent = true;

            Vector2 screenPos = Mouse.current.position.ReadValue();
            var cam = inputCamera != null ? inputCamera : Camera.main;
            
            currentUV = SimulationUvUtility.ComputeUv(screenPos, targetRenderer, cam, currentUV);
            ApplyInkSelectionIfChanged();

            injector.SetPosition(currentUV);

            // Sync world position so CreatureBehavior.WorldToUV (distance checks) works
            transform.position = new Vector3(
                (currentUV.x - 0.5f) * 10f,
                (currentUV.y - 0.5f) * 10f,
                0f
            );
        }

        private void ApplyInkSelectionIfChanged()
        {
            // CP8w: upper bound is ColdSourceInkIndex, not 9. Clamping to 9 here pinned a ColdAir
            // selection to Ice, so the avatar turned Ice-cyan the moment you pressed C — visually
            // indistinguishable from the one ink ColdAir exists to be an alternative to.
            int activeInk = simDriver != null
                ? Mathf.Clamp(simDriver.CurrentInkType, 0, SimulationContext.ColdSourceInkIndex)
                : 0;
            if (activeInk == appliedInkType) return;

            appliedInkType = activeInk;
            injector.SetInkOverrideColor(GetInkKeyColor(appliedInkType));
        }

        private static Color GetInkKeyColor(int inkTypeIndex)
        {
            // CP8w: ColdAir before the clamp, matching BrushInputController and
            // DirectionalEmitterController exactly. This is a tint only — the player's ColdAir
            // injection still routes through SimDriver.InjectDensity, which writes no mass.
            if (SimulationContext.IsColdSource(inkTypeIndex))
                return new Color(0.8f, 1f, 0.95f, 1f);   // pale mint-frost, matches the brush/emitter maps

            switch (Mathf.Clamp(inkTypeIndex, 0, SimulationContext.ColdSourceInkIndex - 1))
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
