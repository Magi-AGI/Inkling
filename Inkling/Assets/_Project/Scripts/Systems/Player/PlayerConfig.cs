using UnityEngine;

namespace Magi.Inkling.Systems.Player
{
    /// <summary>
    /// Configuration for the player character controller.
    /// </summary>
    [CreateAssetMenu(fileName = "PlayerConfig", menuName = "Inkling/Player Config")]
    public class PlayerConfig : ScriptableObject
    {
        [Tooltip("Default ink type index (0-15) used for creature color affinity.")]
        [Range(0, 15)]
        public int defaultInkType = 0;
    }
}
