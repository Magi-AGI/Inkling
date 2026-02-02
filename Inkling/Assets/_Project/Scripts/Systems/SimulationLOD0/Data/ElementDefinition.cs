using UnityEngine;

namespace Magi.Inkling.Data
{
    /// <summary>
    /// ScriptableObject defining an element/ink type with consolidated properties.
    /// Combines ink simulation properties with input color mapping and visual configuration.
    /// </summary>
    /// <remarks>
    /// This is designed as a future replacement for InkTypeDef + AffinityGroup concepts,
    /// providing a single source of truth for element configuration. InkTypeDef remains
    /// for existing ink assets; ElementDefinition is for new elements or migration.
    /// </remarks>
    [CreateAssetMenu(fileName = "NewElement", menuName = "Inkling/Element Definition")]
    public class ElementDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Human-readable name for this element.")]
        public string displayName;

        [Tooltip("Index mapping to iparticle struct field. Must be unique per element.")]
        [Range(0, 15)]
        public int typeIndex;

        [Header("Simulation Properties")]
        [Tooltip("How fast this element fades (0.9=fast, 0.999=slow, 1.0=never).")]
        [Range(0f, 1f)]
        public float dissipation = 0.995f;

        [Tooltip("How much this element spreads/diffuses (0=none, higher=more spread).")]
        [Range(0f, 5f)]
        public float viscosity = 1f;

        [Tooltip("How much this element contributes to swirl/vortex effects.")]
        [Range(0f, 5f)]
        public float vorticity = 0f;

        [Tooltip("How strongly this element is carried by the velocity field (0=static, 1=full).")]
        [Range(0f, 5f)]
        public float advectionWeight = 1f;

        [Tooltip("How strongly this element contributes to pressure/divergence.")]
        [Range(0f, 5f)]
        public float pressureWeight = 1f;

        [Tooltip("Minimum concentration before this element participates in reactions.")]
        [Range(0f, 1f)]
        public float interactionThreshold = 0.1f;

        [Header("Input Mapping")]
        [Tooltip("Key color used to identify this element when stamping from textures.")]
        public Color inputKeyColor = Color.white;

        [Tooltip("How close a stamp color must be to inputKeyColor to match (0=exact, 1=any).")]
        [Range(0f, 1f)]
        public float colorMatchTolerance = 0.3f;

        [Header("Visual")]
        [Tooltip("Debug visualization color for gizmos and inspector.")]
        public Color debugColor = Color.white;

        [Tooltip("Optional gradient texture for rendering this element.")]
        public Texture2D gradientTexture;

        /// <summary>
        /// Validates configuration and auto-populates display name if empty.
        /// </summary>
        private void OnValidate()
        {
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = name;
            }

            // Warn on extreme values
            if (dissipation < 0.9f)
            {
                Debug.LogWarning($"[ElementDefinition] '{name}' has very low dissipation ({dissipation}). " +
                    "Element will fade very quickly.");
            }

            if (typeIndex < 0 || typeIndex > 15)
            {
                Debug.LogError($"[ElementDefinition] '{name}' has invalid typeIndex {typeIndex}. " +
                    "Must be 0-15 to fit in iparticle struct.");
            }
        }
    }
}
