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
            }
        }
    }
}
