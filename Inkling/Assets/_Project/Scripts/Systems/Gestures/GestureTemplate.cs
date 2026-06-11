using System.Collections.Generic;
using UnityEngine;

namespace Magi.Inkling.Systems.Gestures
{
    /// <summary>
    /// P$-style gesture template: name + ordered 2D points in normalized screen space.
    /// </summary>
    [CreateAssetMenu(fileName = "GestureTemplate", menuName = "Inkling/Gesture Template")]
    public class GestureTemplate : ScriptableObject
    {
        [Tooltip("Template name used for matching and action maps.")]
        public string templateName = "Gesture";

        [Tooltip("Normalized points (0-1) defining the stroke path.")]
        public List<Vector2> points = new List<Vector2>();
    }
}
