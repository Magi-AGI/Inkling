using UnityEngine;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Defines an ink type with its index into the iparticle struct.
    /// Used by AffinityGroup to map inks to matrix slots.
    ///
    /// Named "InkTypeDef" to avoid collision with SimDriver.InkType enum.
    /// </summary>
    [CreateAssetMenu(fileName = "NewInkType", menuName = "Inkling/Ink Type Definition")]
    public class InkTypeDef : ScriptableObject
    {
        [Tooltip("Ink type from the standard InkTypeId enum (matches iparticle field order)")]
        public InkTypeId inkType = InkTypeId.Fire;

        [Tooltip("Display name for debugging (auto-populated from inkType if empty)")]
        public string displayName;

        [Tooltip("Debug visualization color")]
        public Color debugColor = Color.white;

        [Header("Input Color Mapping")]
        [Tooltip("Key color used to identify this ink type when stamping from textures. Stamp pixels closest to this color will map to this ink.")]
        public Color inputKeyColor = Color.white;

        [Tooltip("How close a stamp color must be to inputKeyColor to match (0=exact, 1=any color). Uses RGB distance.")]
        [Range(0f, 1f)]
        public float colorMatchTolerance = 0.3f;

        [Header("Simulation Properties")]
        [Tooltip("Half-life: seconds for this ink to fade to 50% concentration. Frame-rate independent. Lower = fades faster. " +
                 "Uncapped upward for authoring (set 1000+ for near-persistent inks); still exponential decay, never truly permanent.")]
        [Min(0.25f)]
        public float dissipationHalfLife = 8f;

        [Tooltip("How much this ink spreads/diffuses (0=none, 1=max spread). Higher values make ink bleed into neighbors.")]
        [Range(0f, 1f)]
        public float viscosity = 0.1f;

        [Tooltip("How much this ink contributes to swirl/vortex effects (0=none, 1=max swirl). Fire might swirl more, water less.")]
        [Range(0f, 2f)]
        public float vorticity = 1.0f;

        [Tooltip("Minimum concentration before this ink participates in reactions (0=always react, 0.1=needs 10% presence).")]
        [Range(0f, 1f)]
        public float interactionThreshold = 0.01f;

        [Header("Advection / Pressure")]
        [Tooltip("How strongly this ink is advected by velocity (0=static, 1=full advection).")]
        [Range(0f, 5f)]
        public float advectionWeight = 1.0f;

        [Tooltip("Reserved: how strongly this ink contributes to pressure/divergence (0=none, 1=full).")]
        [Range(0f, 5f)]
        public float pressureWeight = 1.0f;

        [Header("Special Behaviors")]
        [Tooltip("Enable clearing behavior (this ink clears other inks when above threshold). Used for black body ink.")]
        public bool enableClearing = false;

        [Tooltip("Concentration threshold to activate clearing behavior.")]
        [Range(0f, 1f)]
        public float clearingThreshold = 0.5f;

        [Tooltip("Rate at which other inks are cleared per tick when clearing is active.")]
        [Range(0f, 0.2f)]
        public float clearingRate = 0.05f;

        [Header("Obstacle Behavior")]
        [Tooltip("When true, this ink acts as a velocity obstacle above the threshold concentration.")]
        public bool actsAsObstacle = false;

        [Tooltip("Minimum concentration for this ink to block velocity (0.01 = very sensitive, 1.0 = only at full saturation).")]
        [Range(0.01f, 1f)]
        public float obstacleThreshold = 0.1f;

        /// <summary>
        /// Returns the particle field index for GPU upload.
        /// </summary>
        public int ParticleFieldIndex => (int)inkType;

        private void OnValidate()
        {
            // Auto-populate display name from enum if empty
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = inkType.ToString();
            }

            // Validate range
            int idx = (int)inkType;
            if (idx < 0 || idx >= (int)InkTypeId.Count)
            {
                Debug.LogWarning($"[InkTypeDef] '{name}' has invalid inkType {inkType} (index {idx}). Must be 0-{(int)InkTypeId.Count - 1}.");
                Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal($"InkTypeDef invalid index: {name} idx {idx}");
            }
        }
    }
}
