using System.Collections.Generic;
using UnityEngine;

namespace Magi.Inkling.Systems.Gestures
{
    /// <summary>
    /// Maps gesture names to action identifiers for downstream systems (brush, seeds, forces).
    /// </summary>
    [CreateAssetMenu(fileName = "GestureActionMap", menuName = "Inkling/Gesture Action Map")]
    public class GestureActionMap : ScriptableObject
    {
        [System.Serializable]
        public class GestureAction
        {
            public string gestureName;
            public string actionId; // e.g., \"seed.burst.plant\", \"force.line.fire\"
        }

        public List<GestureAction> actions = new List<GestureAction>();

        public bool TryGetAction(string gestureName, out string actionId)
        {
            if (string.IsNullOrEmpty(gestureName))
            {
                actionId = null;
                return false;
            }

            foreach (var a in actions)
            {
                if (a.gestureName == gestureName)
                {
                    actionId = a.actionId;
                    return true;
                }
            }

            actionId = null;
            return false;
        }
    }
}
