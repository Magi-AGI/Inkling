using UnityEngine;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Defines a group of 4 interacting inks and their reaction matrix.
    /// The matrix encodes reaction rates (per second) between inks.
    /// Negative values = consumption, Positive = production.
    /// </summary>
    [CreateAssetMenu(fileName = "NewAffinityGroup", menuName = "Inkling/Affinity Group")]
    public class AffinityGroup : ScriptableObject
    {
        [Tooltip("Name for debugging")]
        public string groupName;

        [Tooltip("Exactly 4 ink types that form this interaction group")]
        public InkTypeDef[] inks = new InkTypeDef[4];

        [Header("Reaction Matrix (per second)")]
        [Tooltip("Row i, Col j = rate at which ink j affects ink i. Negative = consumption, Positive = production.")]
        public Matrix4x4 reactionMatrix = Matrix4x4.zero;

        [Tooltip("Global multiplier for reaction rates. Reference uses ~10-20 for visible effects.")]
        [Range(0.1f, 50f)]
        public float reactionRateMultiplier = 10f;

        [Header("Adjacency Weights")]
        [Tooltip("Weight for inks in the same cell")]
        public float selfWeight = 1.0f;

        [Tooltip("Weight for cardinal neighbors (up/down/left/right)")]
        public float cardinalWeight = 1.0f;

        [Tooltip("Weight for diagonal neighbors (corners)")]
        public float diagonalWeight = 0.707f;

        /// <summary>
        /// Returns the particle field indices as int[4] for GPU upload.
        /// </summary>
        public int[] GetInkIndices()
        {
            return new int[]
            {
                inks[0] != null ? inks[0].ParticleFieldIndex : 0,
                inks[1] != null ? inks[1].ParticleFieldIndex : 0,
                inks[2] != null ? inks[2].ParticleFieldIndex : 0,
                inks[3] != null ? inks[3].ParticleFieldIndex : 0
            };
        }

        /// <summary>
        /// Returns weights as Vector3 for GPU upload.
        /// </summary>
        public Vector3 GetWeights()
        {
            return new Vector3(selfWeight, cardinalWeight, diagonalWeight);
        }

        private void OnValidate()
        {
            if (inks == null || inks.Length != 4)
            {
                inks = new InkTypeDef[4];
            }
        }
    }
}
