using UnityEngine;

namespace Magi.Inkling.Services.ITUMS
{
    [CreateAssetMenu(fileName = "ITUMSConfig", menuName = "Inkling/ITUMS/Config", order = 1)]
    public class ITUMSConfig : ScriptableObject
    {
        [Header("Event Logging")]
        public bool enableEventLogging = false;
        [Tooltip("Write JSONL events to persistentDataPath when logging is enabled.")]
        public bool writeJsonlFile = false;
        public string jsonlFileName = "itums_events.jsonl";

        [Header("Brush Sampling")]
        [Tooltip("Log stroke samples each frame while drawing.")]
        public bool logStrokeSamples = true;
        [Tooltip("Log idle ticks when not drawing.")]
        public bool logIdleTicks = true;

        [Header("Gesture Events")]
        public bool logGestureRecognized = true;

        [Header("Adaptive Responses")]
        public bool logAdaptiveResponses = true;
    }
}
