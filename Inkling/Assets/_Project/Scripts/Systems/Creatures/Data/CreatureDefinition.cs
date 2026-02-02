using UnityEngine;

namespace Magi.Inkling.Systems.Creatures
{
    /// <summary>
    /// ScriptableObject defining a creature's appearance and animation sprites.
    /// Each animation state has an array of sprites for frame-based animation.
    /// </summary>
    [CreateAssetMenu(fileName = "NewCreature", menuName = "Inkling/Creatures/Creature Definition")]
    public class CreatureDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Display name for this creature type.")]
        public string creatureName = "Creature";

        [Tooltip("Brief description of this creature.")]
        [TextArea(2, 4)]
        public string description;

        [Header("Animation Sprites")]
        [Tooltip("Sprites for idle state.")]
        public Texture2D[] idleSprites;

        [Tooltip("Sprites for movement state.")]
        public Texture2D[] moveSprites;

        [Tooltip("Sprites for positive reaction.")]
        public Texture2D[] positiveSprites;

        [Tooltip("Sprites for negative reaction.")]
        public Texture2D[] negativeSprites;

        [Tooltip("Sprites for befriended state.")]
        public Texture2D[] befriendedSprites;

        [Tooltip("Sprites for activation/special ability.")]
        public Texture2D[] activateSprites;

        [Header("Animation Timing")]
        [Tooltip("Default frames per second for animations.")]
        [Range(1f, 30f)]
        public float defaultFrameRate = 10f;

        [Tooltip("Per-state frame rate overrides (optional).")]
        public AnimationTimingOverride[] timingOverrides;

        [Header("Behavior")]
        [Tooltip("Default ink type this creature produces.")]
        public int defaultInkType = 0;

        [Tooltip("Movement speed multiplier.")]
        [Range(0.1f, 3f)]
        public float speedMultiplier = 1f;

        /// <summary>
        /// Gets the sprite array for a given animation state.
        /// </summary>
        public Texture2D[] GetSpritesForState(CreatureAnimationState state)
        {
            return state switch
            {
                CreatureAnimationState.Idle => idleSprites,
                CreatureAnimationState.Move => moveSprites,
                CreatureAnimationState.Positive => positiveSprites,
                CreatureAnimationState.Negative => negativeSprites,
                CreatureAnimationState.Befriended => befriendedSprites,
                CreatureAnimationState.Activate => activateSprites,
                _ => idleSprites
            };
        }

        /// <summary>
        /// Gets the frame rate for a given state, using override if available.
        /// </summary>
        public float GetFrameRateForState(CreatureAnimationState state)
        {
            if (timingOverrides != null)
            {
                foreach (var ovr in timingOverrides)
                {
                    if (ovr.state == state)
                        return ovr.frameRate;
                }
            }
            return defaultFrameRate;
        }

        /// <summary>
        /// Validates that the definition has at least idle sprites.
        /// </summary>
        public bool IsValid => idleSprites != null && idleSprites.Length > 0;
    }

    /// <summary>
    /// Per-state frame rate override.
    /// </summary>
    [System.Serializable]
    public struct AnimationTimingOverride
    {
        public CreatureAnimationState state;
        [Range(1f, 30f)]
        public float frameRate;
    }
}
