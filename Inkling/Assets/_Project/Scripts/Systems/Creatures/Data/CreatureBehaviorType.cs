namespace Magi.Inkling.Systems.Creatures
{
    /// <summary>
    /// Behavior types for creature AI.
    /// Determines how the creature responds to the player and environment.
    /// </summary>
    public enum CreatureBehaviorType
    {
        /// <summary>Flees from the player.</summary>
        Escape = 0,

        /// <summary>Approaches and collides with targets.</summary>
        Collide = 1,

        /// <summary>Follows the player or specified target.</summary>
        Follow = 2,

        /// <summary>Stays in place, minimal movement.</summary>
        Stay = 3,

        /// <summary>Wanders randomly with no specific target.</summary>
        Wander = 4,

        /// <summary>Follows same ink color, escapes different colors.</summary>
        ColorAffinity = 5
    }
}
