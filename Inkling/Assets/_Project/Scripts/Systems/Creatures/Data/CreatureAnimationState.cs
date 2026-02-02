namespace Magi.Inkling.Systems.Creatures
{
    /// <summary>
    /// Animation states for creature mask sprites.
    /// Matches reference InkParticlesByMaskEmitter states.
    /// </summary>
    public enum CreatureAnimationState
    {
        /// <summary>Default resting state.</summary>
        Idle = 0,

        /// <summary>Moving/locomotion state.</summary>
        Move = 1,

        /// <summary>Positive reaction (happy, eating, etc.).</summary>
        Positive = 2,

        /// <summary>Negative reaction (hurt, scared, etc.).</summary>
        Negative = 3,

        /// <summary>Befriended/tamed state.</summary>
        Befriended = 4,

        /// <summary>Special activation (ability use, etc.).</summary>
        Activate = 5
    }
}
