using UnityEngine;

namespace Magi.Inkling.Systems.Growth
{
    /// <summary>
    /// ScriptableObject containing growth simulation parameters.
    /// </summary>
    [CreateAssetMenu(fileName = "GrowthConfig", menuName = "Inkling/Growth Config")]
    public class GrowthConfig : ScriptableObject
    {
        [Header("Plant Growth")]
        [Tooltip("Rate at which plantSeeded converts to plantGrown per second")]
        [Range(0f, 1f)]
        public float plantGrowthRate = 0.1f;

        [Tooltip("Maximum plantGrown value (caps growth)")]
        [Range(0f, 2f)]
        public float plantMaxGrown = 1f;

        [Tooltip("Minimum plantSeeded threshold to start growth")]
        [Range(0f, 0.5f)]
        public float plantSeedThreshold = 0.01f;

        [Tooltip("Direct seeded->grown maturation only occurs in cells whose water exceeds this " +
                 "threshold. Plant matures where there is water; dry seeds do not grow.")]
        [Range(0f, 1f)]
        public float plantGrowthWaterThreshold = 0.01f;

        [Header("Electricity Growth")]
        [Tooltip("Rate at which electricitySeeded converts to electricityGrown per second")]
        [Range(0f, 2f)]
        public float electricityGrowthRate = 0.3f;

        [Tooltip("Maximum electricityGrown value")]
        [Range(0f, 2f)]
        public float electricityMaxGrown = 1f;

        [Tooltip("Minimum electricitySeeded threshold to start growth")]
        [Range(0f, 0.5f)]
        public float electricitySeedThreshold = 0.01f;

        [Header("Spreading")]
        [Tooltip("Enable spread from neighbors (cellular automata style)")]
        public bool enableSpread = false;

        [Tooltip("Weight for spread from cardinal neighbors")]
        [Range(0f, 0.5f)]
        public float cardinalSpreadWeight = 0.1f;

        [Tooltip("Weight for spread from diagonal neighbors")]
        [Range(0f, 0.25f)]
        public float diagonalSpreadWeight = 0.05f;

        [Tooltip("Grown plant only spreads (neighbor expansion) into cells whose water exceeds this " +
                 "threshold — grown expands across water, not across the plant-seed bed. Does not affect " +
                 "direct seeded->grown maturation.")]
        [Range(0f, 1f)]
        public float plantSpreadWaterThreshold = 0.01f;

        [Header("Decay")]
        [Tooltip("Enable decay of grown plants/electricity over time")]
        public bool enableDecay = false;

        [Tooltip("Rate at which grown values decay per second (when not being fed by seeds)")]
        [Range(0f, 0.5f)]
        public float decayRate = 0.01f;
    }
}
