using UnityEditor;
using UnityEngine;
using Magi.Inkling.Systems.Gestures;

namespace Magi.Inkling.Systems.Gestures.Editor
{
    /// <summary>
    /// Utility to auto-assign GestureInputManager templates/action map from Configs folder.
    /// </summary>
    public static class GestureAutoAssign
    {
        [MenuItem("Inkling/Gestures/Auto-Assign Templates & Action Map")]
        public static void Assign()
        {
            var manager = Object.FindAnyObjectByType<GestureInputManager>();
            if (manager == null)
            {
                Debug.LogWarning("GestureInputManager not found in scene.");
                return;
            }

            // Load assets from Configs
            var templates = AssetDatabase.LoadAllAssetsAtPath("Assets/_Project/Configs/GestureTemplate_Circle.asset");
            // Instead of assuming filenames, load all GestureTemplate assets under Configs
            var guids = AssetDatabase.FindAssets("t:GestureTemplate", new[] { "Assets/_Project/Configs" });
            var list = new System.Collections.Generic.List<GestureTemplate>();
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var tmpl = AssetDatabase.LoadAssetAtPath<GestureTemplate>(path);
                if (tmpl != null) list.Add(tmpl);
            }

            var mapGuid = AssetDatabase.FindAssets("t:GestureActionMap", new[] { "Assets/_Project/Configs" });
            GestureActionMap map = null;
            if (mapGuid.Length > 0)
            {
                map = AssetDatabase.LoadAssetAtPath<GestureActionMap>(AssetDatabase.GUIDToAssetPath(mapGuid[0]));
            }

            Undo.RecordObject(manager, "Assign Gesture Configs");
            manager.GetType().GetField("templates", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(manager, list);
            manager.GetType().GetField("actionMap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?.SetValue(manager, map);

            EditorUtility.SetDirty(manager);
            Debug.Log($"Assigned {list.Count} gesture templates and action map {(map ? map.name : \"<none>\")}");
        }
    }
}
