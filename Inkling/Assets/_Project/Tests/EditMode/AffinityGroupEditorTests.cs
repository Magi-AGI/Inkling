using System.IO;
using NUnit.Framework;
using UnityEditor;
using Magi.Inkling.Systems.SimulationLOD0;

namespace Magi.Inkling.Tests.EditMode
{
    /// <summary>
    /// CP7d slice 1b guards for the AffinityGroup thermal authoring surface.
    ///
    /// These are source-level assertions. A Unity inspector's visual output can't be asserted
    /// headlessly, but a regression here is almost always a regression *back to raw hidden arrays*
    /// (the slice 1a state) or the silent loss of validation. So we pin the specific API usages and
    /// user-facing strings that make the surface safe: an ordered/reorderable list, a live
    /// ThermalRuleBaker validation call, error/warning/info help boxes, and the "not a pairwise
    /// adjacency product matrix" disclaimer.
    /// </summary>
    public class AffinityGroupEditorTests
    {
        private const string EditorPath =
            "Assets/_Project/Scripts/Systems/SimulationLOD0/Data/Editor/AffinityGroupEditor.cs";

        private static string Source()
        {
            Assert.IsTrue(File.Exists(EditorPath), $"AffinityGroupEditor not found at {EditorPath}");
            return File.ReadAllText(EditorPath);
        }

        // ── Ordered-list surface (regression guard against raw PropertyField arrays) ──────────

        [Test]
        public void Editor_UsesReorderableList_ForThermalTransitionsAndSources()
        {
            string src = Source();

            StringAssert.Contains("ReorderableList", src,
                "Thermal transitions/sources must use an explicit ordered (reorderable) list surface — " +
                "order is load-bearing, so a plain array field is not acceptable.");

            // Both lists must actually be built, not just the type referenced.
            StringAssert.Contains("thermalTransitions", src);
            StringAssert.Contains("thermalSources", src);
        }

        [Test]
        public void Editor_DoesNotFallBackToRawArrayPropertyFields_ForThermalData()
        {
            string src = Source();

            Assert.IsFalse(src.Contains("PropertyField(thermalTransitions, true)"),
                "Raw array PropertyField for thermalTransitions is the slice 1a state — it hides ordering. " +
                "Use the reorderable list surface.");
            Assert.IsFalse(src.Contains("PropertyField(thermalSources, true)"),
                "Raw array PropertyField for thermalSources is the slice 1a state. Use the reorderable list surface.");
        }

        // ── Live validation surface ──────────────────────────────────────────────────────────

        [Test]
        public void Editor_RunsThermalRuleBakerValidation()
        {
            string src = Source();
            StringAssert.Contains("ThermalRuleBaker.Bake", src,
                "The inspector must bake the current group to surface collisions/ladder/cap errors " +
                "BEFORE the author edits a shipped asset.");
        }

        [Test]
        public void Editor_RendersErrorWarningAndInfoStates()
        {
            string src = Source();
            StringAssert.Contains("MessageType.Error", src, "Invalid baked rule sets must surface as an Error box");
            StringAssert.Contains("MessageType.Warning", src, "Baker Warnings (e.g. per-category fallback) must surface");
            StringAssert.Contains("MessageType.Info", src, "Default-fallback / guidance state must surface");
        }

        [Test]
        public void Editor_SurfacesBakerErrorAndWarningText()
        {
            string src = Source();
            // Must render the baker's actual messages, not a generic "invalid" string.
            StringAssert.Contains(".Error", src, "Editor should display ThermalRuleSet.Error text");
            StringAssert.Contains(".Warnings", src, "Editor should display ThermalRuleSet.Warnings text");
        }

        // ── Semantics disclaimers (kept from slice 1a) ────────────────────────────────────────

        [Test]
        public void Editor_StatesThermalRulesAreNotPairwiseAdjacencyProducts()
        {
            string src = Source();
            StringAssert.Contains("NOT pairwise", src,
                "The thermal section must keep stating these are local directed transitions, not " +
                "pairwise adjacency products — that confusion is the whole reason for the separate surface.");
        }

        [Test]
        public void Editor_ExplainsLoadBearingOrdering()
        {
            string src = Source();
            StringAssert.Contains("Cold", src);
            StringAssert.Contains("Hot", src);
            StringAssert.Contains("order", src.ToLowerInvariant(),
                "The thermal section must explain that Cold-then-Hot authored order is load-bearing.");
        }

        [Test]
        public void Editor_PreservesProductAndImpulseMatrixUI()
        {
            string src = Source();
            StringAssert.Contains("Product Reaction Matrix", src, "Existing product matrix UI must be preserved");
            StringAssert.Contains("Reaction Impulse Matrix", src, "Existing impulse matrix UI must be preserved");
        }

        // ── Shipped assets remain unauthored (slice 1b writes no assets) ──────────────────────

        [Test]
        public void ShippedAffinityGroupAssets_StillDoNotAuthorThermalData()
        {
            string[] guids = AssetDatabase.FindAssets("t:AffinityGroup");
            Assert.IsNotEmpty(guids, "Expected shipped AffinityGroup assets to be discoverable");

            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var g = AssetDatabase.LoadAssetAtPath<AffinityGroup>(path);
                Assert.IsNotNull(g, path);
                Assert.IsTrue(g.thermalTransitions == null || g.thermalTransitions.Length == 0,
                    $"{path} must not author thermal transitions in slice 1b (editor-only slice)");
                Assert.IsTrue(g.thermalSources == null || g.thermalSources.Length == 0,
                    $"{path} must not author thermal sources in slice 1b (editor-only slice)");
            }
        }
    }
}
