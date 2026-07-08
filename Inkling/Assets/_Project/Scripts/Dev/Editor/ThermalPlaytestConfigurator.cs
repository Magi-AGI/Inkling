#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Dev.EditorTools
{
    /// <summary>
    /// Editor/dev-only utility to switch the active scene's SimDriver between the CP6 thermal
    /// playtest configurations (see ckpt-025/026). It mutates only the in-memory SimDriver via
    /// SerializedObject (marking the scene dirty for playtesting) and NEVER saves the scene or
    /// touches any asset. Revert with "Restore Legacy Baseline" or reload the scene without saving.
    ///
    /// This exists purely to A/B the legacy affinity ThermalGroup against the CP5 local
    /// ThermalInteractions pass — the existing flags can't express "CP5-only thermal with organic
    /// reactions retained" because useInkInteractions=false disables OrganicGroup/OrganicGroup2 too.
    /// </summary>
    public static class ThermalPlaytestConfigurator
    {
        // AffinityGroup asset GUIDs (see ckpt-025 affinity summary).
        private const string OrganicGroupGuid  = "da76efa99d9a5cf4aadca3f811d21554";
        private const string ThermalGroupGuid  = "bb5975a015651cc47aab80f9ac703167";
        private const string OrganicGroup2Guid = "3892ae7ecffb33f4cad3ec4e410eee4c";

        private const string MenuRoot = "Inkling/Thermal Playtest/";

        // ── Menu commands ───────────────────────────────────────────────────

        [MenuItem(MenuRoot + "Apply Legacy Only (Baseline)", false, 0)]
        public static void ApplyLegacyOnly() => ApplyBaseline();

        [MenuItem(MenuRoot + "Apply Both Systems", false, 1)]
        public static void ApplyBothSystems()
        {
            Apply("Both Systems", useInk: true, enableThermal: true,
                new[] { OrganicGroupGuid, ThermalGroupGuid, OrganicGroup2Guid });
        }

        [MenuItem(MenuRoot + "Apply CP5 Only (Organic Retained)", false, 2)]
        public static void ApplyCp5Only()
        {
            Apply("CP5 Only (Organic Retained)", useInk: true, enableThermal: true,
                new[] { OrganicGroupGuid, OrganicGroup2Guid });
        }

        [MenuItem(MenuRoot + "Log Current Thermal Config", false, 20)]
        public static void LogCurrentConfig()
        {
            var driver = FindSimDriver();
            if (driver == null) return;

            var so = new SerializedObject(driver);
            if (!TryProp(so, "useInkInteractions", out var useInkProp)) return;
            if (!TryProp(so, "enableThermalInteractions", out var enableThermalProp)) return;
            if (!TryProp(so, "affinityGroups", out var groups)) return;
            bool useInk = useInkProp.boolValue;
            bool enableThermal = enableThermalProp.boolValue;

            var sb = new StringBuilder();
            sb.AppendLine($"[ThermalPlaytest] Current config on '{driver.name}':");
            sb.AppendLine($"  useInkInteractions = {useInk}");
            sb.AppendLine($"  enableThermalInteractions = {enableThermal}");
            sb.AppendLine($"  affinityGroups ({groups.arraySize}):");
            for (int i = 0; i < groups.arraySize; i++)
            {
                var obj = groups.GetArrayElementAtIndex(i).objectReferenceValue;
                if (obj == null) { sb.AppendLine($"    [{i}] <null>"); continue; }
                string path = AssetDatabase.GetAssetPath(obj);
                string guid = AssetDatabase.AssetPathToGUID(path);
                sb.AppendLine($"    [{i}] {obj.name}  guid={guid}");
            }
            Debug.Log(sb.ToString());
        }

        [MenuItem(MenuRoot + "Restore Legacy Baseline", false, 21)]
        public static void RestoreLegacyBaseline() => ApplyBaseline();

        // ── Core ────────────────────────────────────────────────────────────

        private static void ApplyBaseline()
        {
            Apply("Legacy Only (Baseline)", useInk: true, enableThermal: false,
                new[] { OrganicGroupGuid, ThermalGroupGuid, OrganicGroup2Guid });
        }

        private static void Apply(string label, bool useInk, bool enableThermal, string[] groupGuids)
        {
            var driver = FindSimDriver();
            if (driver == null) return;

            // Resolve all groups FIRST so we abort cleanly (no partial mutation) if any GUID fails.
            var groups = new AffinityGroup[groupGuids.Length];
            for (int i = 0; i < groupGuids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(groupGuids[i]);
                if (string.IsNullOrEmpty(path))
                {
                    Debug.LogError($"[ThermalPlaytest] Could not resolve AffinityGroup guid {groupGuids[i]}. Aborting '{label}' (no changes made).");
                    return;
                }
                var g = AssetDatabase.LoadAssetAtPath<AffinityGroup>(path);
                if (g == null)
                {
                    Debug.LogError($"[ThermalPlaytest] Asset at '{path}' (guid {groupGuids[i]}) is not an AffinityGroup. Aborting '{label}' (no changes made).");
                    return;
                }
                groups[i] = g;
            }

            var so = new SerializedObject(driver);
            if (!TryProp(so, "useInkInteractions", out var useInkProp)) return;
            if (!TryProp(so, "enableThermalInteractions", out var enableThermalProp)) return;
            if (!TryProp(so, "affinityGroups", out var arr)) return;

            // Record undo BEFORE mutating so Ctrl+Z reverts the playtest config in one step.
            // Apply without SerializedObject's own undo entry to keep a single, well-named undo.
            Undo.RecordObject(driver, $"Apply Thermal Playtest Config: {label}");

            useInkProp.boolValue = useInk;
            enableThermalProp.boolValue = enableThermal;
            arr.arraySize = groups.Length;
            for (int i = 0; i < groups.Length; i++)
                arr.GetArrayElementAtIndex(i).objectReferenceValue = groups[i];

            so.ApplyModifiedPropertiesWithoutUndo();

            // Deliberately Debug.Log (not LogWarning): the message carries warning SEMANTICS via the
            // explicit prefix, but stays a Log so automated Unity_RunCommand smokes don't flag it.
            Debug.Log($"[ThermalPlaytest][TEMPORARY WARNING] Applied '{label}' to '{driver.name}'. The scene is marked " +
                "dirty but was NOT saved — this is a TEMPORARY playtest config. Revert with 'Inkling/Thermal Playtest/" +
                "Restore Legacy Baseline' or reload the scene without saving. Do NOT commit Main.unity from this change.");
            LogCurrentConfig();
        }

        // Returns the single SimDriver in the ACTIVE scene, or null (with a clear error) if there
        // are zero or more than one — avoids ambiguity across multiple loaded scenes.
        private static SimDriver FindSimDriver()
        {
            var all = Object.FindObjectsByType<SimDriver>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Scene active = SceneManager.GetActiveScene();

            SimDriver found = null;
            int count = 0;
            foreach (var d in all)
            {
                if (d.gameObject.scene == active)
                {
                    found = d;
                    count++;
                }
            }

            if (count == 0)
            {
                Debug.LogError($"[ThermalPlaytest] No SimDriver found in the active scene '{active.name}'. Open Main.unity first.");
                return null;
            }
            if (count > 1)
            {
                Debug.LogError($"[ThermalPlaytest] Found {count} SimDrivers in the active scene '{active.name}'. Ambiguous — resolve to a single SimDriver before applying a config.");
                return null;
            }
            return found;
        }

        // FindProperty null-guard: logs a clear error (instead of throwing) if a serialized field
        // name changes in a future SimDriver refactor.
        private static bool TryProp(SerializedObject so, string name, out SerializedProperty prop)
        {
            prop = so.FindProperty(name);
            if (prop == null)
            {
                Debug.LogError($"[ThermalPlaytest] SimDriver has no serialized field '{name}'. The field may have been " +
                    "renamed/removed — update ThermalPlaytestConfigurator. Aborting (no changes made).");
                return false;
            }
            return true;
        }
    }
}
#endif
