using UnityEngine;

namespace Magi.Inkling.Systems.Capture
{
    /// <summary>
    /// Configuration for runtime capture/export.
    /// </summary>
    [CreateAssetMenu(fileName = "CaptureConfig", menuName = "Inkling/Capture Config")]
    public class CaptureConfig : ScriptableObject
    {
        [Tooltip("Output directory; if empty uses Application.persistentDataPath.")]
        public string outputPath = string.Empty;

        [Tooltip("Scale factor for capture (1 = sim resolution).")]
        [Range(0.25f, 2f)]
        public float resolutionScale = 1f;

        [Tooltip("Capture simulation buffer instead of display RT.")]
        public bool captureSimulationBuffer = true;
    }
}
