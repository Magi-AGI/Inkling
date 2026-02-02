using System.Collections.Generic;
using UnityEngine;

namespace Magi.Inkling.Systems.Gestures
{
    /// <summary>
    /// Minimal placeholder recognizer.
    /// For now, returns the first template and a dummy score to keep pipelines compiling.
    /// A full P$ implementation will replace this in Phase 7B.
    /// </summary>
    public static class GestureRecognizer
    {
        public static (GestureTemplate template, float score) Recognize(IReadOnlyList<Vector2> input, IReadOnlyList<GestureTemplate> templates)
        {
            if (templates == null || templates.Count == 0)
                return (null, 0f);

            // TODO: replace with proper P$ matching; this is a stub
            return (templates[0], 1f);
        }
    }
}
