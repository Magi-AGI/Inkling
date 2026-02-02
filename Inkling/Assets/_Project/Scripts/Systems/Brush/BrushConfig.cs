using UnityEngine;

namespace Magi.Inkling.Systems.Brush
{
    /// <summary>
    /// Configuration for brush-based ink injection.
    /// Values are deliberately simple; tuning happens in Phase 7B tasks.
    /// </summary>
    [CreateAssetMenu(fileName = "BrushConfig", menuName = "Inkling/Brush Config")]
    public class BrushConfig : ScriptableObject
    {
        [Header("Injection")]
        [Tooltip("Radius of the brush stamp in UV space (0-1).")]
        [Range(0.001f, 0.2f)]
        public float brushRadiusUv = 0.02f;

        [Tooltip("Density multiplier applied to stamp colors.")]
        [Range(0.01f, 10f)]
        public float densityMultiplier = 1f;

        [Tooltip("Force multiplier for velocity injection when dragging.")]
        [Range(0f, 200f)]
        public float forceMultiplier = 50f;

        [Header("Appearance")]
        [Tooltip("Optional stamp texture; if null, falls back to a circular stamp.")]
        public Texture2D stampTexture;

        [Header("Debug")]
        [Tooltip("Log brush injections for tuning.")]
        public bool verboseLogging = false;
    }
}
