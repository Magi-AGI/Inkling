using UnityEngine;

namespace Magi.Inkling.Systems.Creatures
{
    /// <summary>
    /// Configuration for creature AI behavior.
    /// Controls movement, flocking, and player interaction.
    /// </summary>
    [CreateAssetMenu(fileName = "NewBehaviorConfig", menuName = "Inkling/Creatures/Behavior Config")]
    public class CreatureBehaviorConfig : ScriptableObject
    {
        [Header("Behavior Type")]
        [Tooltip("Primary behavior pattern for this creature.")]
        public CreatureBehaviorType behaviorType = CreatureBehaviorType.ColorAffinity;

        [Header("Movement")]
        [Tooltip("Base movement speed in UV units per second.")]
        [Range(0.01f, 1f)]
        public float baseSpeed = 0.1f;

        [Tooltip("Maximum movement speed.")]
        [Range(0.01f, 2f)]
        public float maxSpeed = 0.3f;

        [Tooltip("Rotation speed for turning toward movement direction.")]
        [Range(0.5f, 10f)]
        public float rotationSpeed = 4f;

        [Tooltip("How quickly direction changes are made (0=instant, 1=very smooth).")]
        [Range(0f, 0.99f)]
        public float directionSmoothing = 0.8f;

        [Header("Flocking")]
        [Tooltip("Enable boid-like flocking with nearby creatures of same type.")]
        public bool enableFlocking = true;

        [Tooltip("Radius to detect other creatures for flocking (UV units).")]
        [Range(0.01f, 0.5f)]
        public float flockingRadius = 0.1f;

        [Tooltip("Alignment weight - match neighbors' heading.")]
        [Range(0f, 2f)]
        public float alignmentWeight = 1f;

        [Tooltip("Cohesion weight - move toward center of group.")]
        [Range(0f, 2f)]
        public float cohesionWeight = 1f;

        [Tooltip("Separation weight - avoid crowding neighbors.")]
        [Range(0f, 2f)]
        public float separationWeight = 1.5f;

        [Tooltip("Minimum distance before separation kicks in (UV units).")]
        [Range(0.001f, 0.1f)]
        public float separationDistance = 0.02f;

        [Header("Player Interaction")]
        [Tooltip("Radius to detect player (UV units).")]
        [Range(0.01f, 0.5f)]
        public float playerDetectRadius = 0.15f;

        [Tooltip("How strongly the creature reacts to player presence.")]
        [Range(0f, 5f)]
        public float playerInfluenceWeight = 2f;

        [Tooltip("If true, creature follows player with matching ink color.")]
        public bool followMatchingColor = true;

        [Tooltip("If true, creature escapes from player with different ink color.")]
        public bool escapeNonMatchingColor = true;

        [Tooltip("Close range where creature stops approaching (UV units).")]
        [Range(0.01f, 0.2f)]
        public float closeRange = 0.05f;

        [Header("Wandering")]
        [Tooltip("Chance per frame to change direction when wandering (0-1).")]
        [Range(0f, 0.1f)]
        public float directionChangeChance = 0.02f;

        [Tooltip("Bounds for movement (UV space). Creature bounces off these.")]
        public Vector2 movementBounds = new Vector2(0.9f, 0.9f);

        [Tooltip("Minimum distance from bounds before turning.")]
        [Range(0.01f, 0.2f)]
        public float boundaryMargin = 0.1f;

        [Header("Ink Affinity")]
        [Tooltip("Ink type index this creature is associated with.")]
        public int inkTypeIndex = 0;

        [Tooltip("How strongly creature is advected by fluid velocity.")]
        [Range(0f, 1f)]
        public float fluidAdvectionWeight = 0.3f;
    }
}
