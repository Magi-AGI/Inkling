using UnityEngine;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Defines a group of 4 interacting inks and their product reaction matrix.
    /// Product matrix encodes reactions requiring TWO inks (A + B → C).
    /// Negative values = consumption, Positive = production.
    /// </summary>
    [CreateAssetMenu(fileName = "NewAffinityGroup", menuName = "Inkling/Affinity Group")]
    public class AffinityGroup : ScriptableObject
    {
        [Tooltip("Name for debugging")]
        public string groupName;

        [Tooltip("Exactly 4 ink types that form this interaction group")]
        public InkTypeDef[] inks = new InkTypeDef[4];

        [Header("Product Reaction Matrix (A + B → C)")]
        [Tooltip("For reactions requiring TWO inks (e.g., fire+water→steam). Columns: 0×1, 0×2, 0×3, 1×2 (stored in Matrix4x4)")]
        public Matrix4x4 productMatrix = Matrix4x4.zero;

        [Tooltip("Product matrix column 4: reactions involving inks 1×3")]
        public Vector4 productCol4 = Vector4.zero;

        [Tooltip("Product matrix column 5: reactions involving inks 2×3")]
        public Vector4 productCol5 = Vector4.zero;

        [Header("Reaction Impulse Matrix (motion, A + B → C)")]
        [Tooltip("Drives fluid MOTION from reactions, independent of the concentration productMatrix " +
                 "(so motion strength tunes separately from conversion speed). Same 6 pair columns as " +
                 "productMatrix: 0×1, 0×2, 0×3, 1×2 here; 1×3, 2×3 in Col4/Col5. For a pair A×B the " +
                 "impulse DIRECTION is grad(slotB) - grad(slotA) (front normal, A → B/fuel) and the " +
                 "INTENSITY is this column's signed sum. For 'A + B → C' put a positive coefficient in " +
                 "row C; a negative coefficient flips the direction. All-zero = no reaction motion.")]
        public Matrix4x4 reactionImpulseMatrix = Matrix4x4.zero;

        [Tooltip("Reaction impulse matrix column 4: motion for pair 1×3")]
        public Vector4 reactionImpulseCol4 = Vector4.zero;

        [Tooltip("Reaction impulse matrix column 5: motion for pair 2×3")]
        public Vector4 reactionImpulseCol5 = Vector4.zero;

        [Header("Rate Settings")]
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
