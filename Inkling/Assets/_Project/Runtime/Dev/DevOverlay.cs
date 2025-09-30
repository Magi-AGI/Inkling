using UnityEngine;

namespace Magi.Inkling.Dev
{
    public class DevOverlay : MonoBehaviour
    {
        public bool show = true;
        public float simMs;
        public float inferMs;
        public float composeMs;

        void OnGUI()
        {
            if (!show) return;
            GUILayout.BeginArea(new Rect(10, 10, 250, 120), GUI.skin.box);
            GUILayout.Label($"sim: {simMs:F2} ms");
            GUILayout.Label($"infer: {inferMs:F2} ms");
            GUILayout.Label($"compose: {composeMs:F2} ms");
            GUILayout.EndArea();
        }
    }
}

