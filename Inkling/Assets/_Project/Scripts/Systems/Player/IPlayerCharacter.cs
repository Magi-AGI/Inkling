using UnityEngine;

namespace Magi.Inkling.Systems.Player
{
    /// <summary>
    /// Interface for other systems to reference the player character.
    /// Creatures find the player by tag, not by this interface.
    /// </summary>
    public interface IPlayerCharacter
    {
        /// <summary>Current UV position (0-1) in simulation space.</summary>
        Vector2 PositionUV { get; }

        /// <summary>Ink type index for creature color affinity.</summary>
        int ActiveInkType { get; }

        /// <summary>Whether the player is providing input.</summary>
        bool IsActive { get; }
    }
}
