using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Magi.Inkling.Systems.SimulationLOD0;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.EditMode
{
    /// <summary>
    /// CP7d slice 1a: the baker merges thermal rules authored across all active AffinityGroups into
    /// one deterministic, validated, ordered rule set. Collisions/caps/ladder violations are HARD
    /// errors that make the whole set inert — never truncate, first-win, or average.
    /// </summary>
    public class ThermalRuleBakerTests
    {
        private readonly List<Object> spawned = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in spawned) if (o != null) Object.DestroyImmediate(o);
            spawned.Clear();
        }

        private InkTypeDef Ink(InkTypeId id)
        {
            var d = ScriptableObject.CreateInstance<InkTypeDef>();
            d.inkType = id;
            spawned.Add(d);
            return d;
        }

        private AffinityGroup Group(string name, params InkTypeId[] slots)
        {
            var g = ScriptableObject.CreateInstance<AffinityGroup>();
            g.name = name;
            g.groupName = name;
            g.inks = new InkTypeDef[4];
            for (int i = 0; i < 4; i++) g.inks[i] = Ink(slots[i]);
            spawned.Add(g);
            return g;
        }

        private static ThermalTransition T(int from, int to, ThermalRegime regime,
            float threshold = 0.5f, float rate = 1f, float heatCost = 0f, float heatRelease = 0f) =>
            new ThermalTransition
            {
                fromSlot = from, toSlot = to, regime = regime,
                threshold = threshold, rate = rate, heatCost = heatCost, heatRelease = heatRelease
            };

        private static ThermalSource S(int slot, float rate = 1f, float fuelCost = 0f) =>
            new ThermalSource { slot = slot, heatEmissionRate = rate, fuelCost = fuelCost };

        private static List<AffinityGroup> Groups(params AffinityGroup[] g) => new List<AffinityGroup>(g);

        // A conventional thermal group: [Fire, Water, Steam, Ice] => fields [0, 1, 4, 9].
        private AffinityGroup ThermalGroup(string name = "Thermal") =>
            Group(name, InkTypeId.Fire, InkTypeId.Water, InkTypeId.Steam, InkTypeId.Ice);

        // ── Default parity & ORDER ───────────────────────────────────────────────────────────

        [Test]
        public void NoAuthoredData_BakesDefaultRules_InExactOrder()
        {
            var rs = ThermalRuleBaker.Bake(null, ThermalDefaults.Cp7Defaults);

            Assert.IsTrue(rs.IsValid);
            Assert.IsTrue(rs.UsedDefaultTransitions);
            Assert.IsTrue(rs.UsedDefaultSources);
            Assert.AreEqual(4, rs.Transitions.Count);

            // Cold first, in order: Steam->Water (condense), Water->Ice (freeze).
            Assert.AreEqual((int)InkTypeId.Steam, rs.Transitions[0].fromField);
            Assert.AreEqual((int)InkTypeId.Water, rs.Transitions[0].toField);
            Assert.AreEqual(ThermalRegime.Cold, rs.Transitions[0].regime);
            Assert.AreEqual(0.2f, rs.Transitions[0].threshold, 1e-5f);

            Assert.AreEqual((int)InkTypeId.Water, rs.Transitions[1].fromField);
            Assert.AreEqual((int)InkTypeId.Ice, rs.Transitions[1].toField);
            Assert.AreEqual(ThermalRegime.Cold, rs.Transitions[1].regime);

            // Hot second, in order: Ice->Water (melt), Water->Steam (boil).
            Assert.AreEqual((int)InkTypeId.Ice, rs.Transitions[2].fromField);
            Assert.AreEqual((int)InkTypeId.Water, rs.Transitions[2].toField);
            Assert.AreEqual(ThermalRegime.Hot, rs.Transitions[2].regime);
            Assert.AreEqual(0.5f, rs.Transitions[2].heatCost, 1e-5f);

            Assert.AreEqual((int)InkTypeId.Water, rs.Transitions[3].fromField);
            Assert.AreEqual((int)InkTypeId.Steam, rs.Transitions[3].toField);
            Assert.AreEqual(ThermalRegime.Hot, rs.Transitions[3].regime);
            Assert.AreEqual(0.7f, rs.Transitions[3].threshold, 1e-5f);

            // Default source: Fire.
            Assert.AreEqual(1, rs.Sources.Count);
            Assert.AreEqual((int)InkTypeId.Fire, rs.Sources[0].field);
        }

        [Test]
        public void EmptyGroupList_UsesDefaults()
        {
            var rs = ThermalRuleBaker.Bake(Groups(ThermalGroup()), ThermalDefaults.Cp7Defaults);
            Assert.IsTrue(rs.IsValid);
            Assert.IsTrue(rs.UsedDefaultTransitions, "A group with no authored thermal data must fall back to defaults");
            Assert.IsTrue(rs.UsedDefaultSources);
        }

        // ── Multi-group bake + field resolution ─────────────────────────────────────────────

        [Test]
        public void MultiGroup_MergesSourceAndTransition_WithFieldResolution()
        {
            var g1 = ThermalGroup("G1");
            g1.thermalSources = new[] { S(slot: 0, rate: 2f, fuelCost: 3f) };   // slot0 = Fire

            var g2 = ThermalGroup("G2");
            g2.thermalTransitions = new[] { T(3, 1, ThermalRegime.Hot, 0.4f, heatCost: 0.5f) }; // Ice->Water

            var rs = ThermalRuleBaker.Bake(Groups(g1, g2), ThermalDefaults.Cp7Defaults);

            Assert.IsTrue(rs.IsValid, rs.Error);
            Assert.AreEqual(1, rs.Sources.Count);
            Assert.AreEqual((int)InkTypeId.Fire, rs.Sources[0].field);
            Assert.AreEqual(2f, rs.Sources[0].heatEmissionRate, 1e-5f);
            Assert.AreEqual(3f, rs.Sources[0].fuelCost, 1e-5f);
            Assert.IsFalse(rs.UsedDefaultSources);

            Assert.AreEqual(1, rs.Transitions.Count, "Authored transitions fully replace defaults");
            Assert.AreEqual((int)InkTypeId.Ice, rs.Transitions[0].fromField);
            Assert.AreEqual((int)InkTypeId.Water, rs.Transitions[0].toField);
            Assert.IsFalse(rs.UsedDefaultTransitions);
        }

        [Test]
        public void CrossGroupCollision_OnResolvedFieldIndex_IsHardError()
        {
            // Same ink (Water) in DIFFERENT slots of two groups, both with a cold outgoing transition.
            var g1 = ThermalGroup("G1");                                        // slot1 = Water
            g1.thermalTransitions = new[] { T(1, 3, ThermalRegime.Cold, 0.2f) };  // Water->Ice

            var g2 = Group("G2", InkTypeId.Fire, InkTypeId.Steam, InkTypeId.Ice, InkTypeId.Water); // slot3 = Water
            g2.thermalTransitions = new[] { T(3, 2, ThermalRegime.Cold, 0.2f) };  // Water->Ice again

            var rs = ThermalRuleBaker.Bake(Groups(g1, g2), ThermalDefaults.Cp7Defaults);

            Assert.IsFalse(rs.IsValid, "Same resolved source field + regime across groups must collide");
            Assert.IsEmpty(rs.Transitions, "An invalid rule set must be inert");
            Assert.IsEmpty(rs.Sources);
        }

        [Test]
        public void ExactDuplicateTransition_IsHardError()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[]
            {
                T(3, 1, ThermalRegime.Hot, 0.4f, heatCost: 0.5f),
                T(3, 1, ThermalRegime.Hot, 0.4f, heatCost: 0.5f),
            };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid);
        }

        [Test]
        public void MultipleOutgoingSameSourceSameRegime_IsHardError_ForCp7d()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[]
            {
                T(1, 3, ThermalRegime.Cold, 0.2f),   // Water->Ice
                T(1, 2, ThermalRegime.Cold, 0.2f),   // Water->Steam (also cold, same source)
            };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid);
        }

        [Test]
        public void DuplicateSourceField_IsHardError()
        {
            var g1 = ThermalGroup("G1"); g1.thermalSources = new[] { S(0) };
            var g2 = ThermalGroup("G2"); g2.thermalSources = new[] { S(0) };
            var rs = ThermalRuleBaker.Bake(Groups(g1, g2), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid);
        }

        [Test]
        public void FromEqualsTo_IsHardError()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[] { T(1, 1, ThermalRegime.Hot) };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid);
        }

        [Test]
        public void SlotOutOfRange_IsHardError()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[] { T(7, 1, ThermalRegime.Hot) };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid);
        }

        [Test]
        public void NullInkInSlot_IsHardError()
        {
            var g = ThermalGroup();
            g.inks[2] = null;
            g.thermalTransitions = new[] { T(2, 1, ThermalRegime.Hot) };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid);
        }

        [Test]
        public void LadderViolation_ColdAboveHot_IsHardError()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[]
            {
                T(2, 1, ThermalRegime.Cold, threshold: 0.9f),                  // cold at 0.9
                T(3, 1, ThermalRegime.Hot, threshold: 0.4f, heatCost: 0.5f),   // hot at 0.4  => violation
            };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid, "A cell could both freeze and melt in one pass");
            Assert.IsEmpty(rs.Transitions);
        }

        [Test]
        public void CapOverflow_IsHardError_NotTruncation()
        {
            // Two groups with DISJOINT inks so the one-outgoing invariant is not what trips first.
            var g1 = ThermalGroup("G1");   // Fire, Water, Steam, Ice
            g1.thermalTransitions = new[]
            {
                T(1, 3, ThermalRegime.Cold, 0.1f), T(2, 1, ThermalRegime.Cold, 0.1f),
                T(3, 1, ThermalRegime.Hot, 0.5f, heatCost: 0.5f), T(0, 2, ThermalRegime.Hot, 0.5f, heatCost: 0.5f),
            };
            var g2 = Group("G2", InkTypeId.Glitter, InkTypeId.PlantSeeded, InkTypeId.PlantGrown, InkTypeId.BlackBody);
            g2.thermalTransitions = new[]
            {
                T(0, 1, ThermalRegime.Cold, 0.1f), T(1, 2, ThermalRegime.Cold, 0.1f), T(2, 3, ThermalRegime.Cold, 0.1f),
                T(3, 0, ThermalRegime.Hot, 0.5f, heatCost: 0.5f), T(0, 3, ThermalRegime.Hot, 0.5f, heatCost: 0.5f),
            };

            var rs = ThermalRuleBaker.Bake(Groups(g1, g2), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid, "9 transitions exceeds the cap of 8");
            Assert.IsEmpty(rs.Transitions, "Cap overflow must be inert, never truncated");
        }

        // ── Per-category replacement ────────────────────────────────────────────────────────

        [Test]
        public void AuthoredSourcesOnly_KeepsDefaultTransitions_AndWarns()
        {
            var g = ThermalGroup();
            g.thermalSources = new[] { S(0, rate: 5f, fuelCost: 1f) };

            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);

            Assert.IsTrue(rs.IsValid, rs.Error);
            Assert.IsFalse(rs.UsedDefaultSources, "Authored sources replace the default source");
            Assert.IsTrue(rs.UsedDefaultTransitions, "Transitions fall back per-category");
            Assert.AreEqual(4, rs.Transitions.Count);
            Assert.IsNotEmpty(rs.Warnings, "Authoring one category only must warn");
        }

        [Test]
        public void AuthoredTransitionsOnly_KeepsDefaultSource_AndWarns()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[] { T(3, 1, ThermalRegime.Hot, 0.4f, heatCost: 0.5f) };

            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);

            Assert.IsTrue(rs.IsValid, rs.Error);
            Assert.IsFalse(rs.UsedDefaultTransitions);
            Assert.IsTrue(rs.UsedDefaultSources);
            Assert.AreEqual((int)InkTypeId.Fire, rs.Sources[0].field);
            Assert.IsNotEmpty(rs.Warnings);
        }

        [Test]
        public void AuthoredBothCategories_NoWarning()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[] { T(3, 1, ThermalRegime.Hot, 0.4f, heatCost: 0.5f) };
            g.thermalSources = new[] { S(0) };

            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsTrue(rs.IsValid, rs.Error);
            Assert.IsEmpty(rs.Warnings);
        }

        // ── Sanitization ────────────────────────────────────────────────────────────────────

        [Test]
        public void NegativeRatesCostsReleases_AreClampedToZero_NotErrors()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[]
            {
                T(3, 1, ThermalRegime.Hot, threshold: -0.5f, rate: -2f, heatCost: -1f, heatRelease: -3f)
            };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);

            Assert.IsTrue(rs.IsValid, "Negatives are sanitized, not fatal");
            Assert.AreEqual(0f, rs.Transitions[0].threshold, 1e-5f);
            Assert.AreEqual(0f, rs.Transitions[0].rate, 1e-5f);
            Assert.AreEqual(0f, rs.Transitions[0].heatCost, 1e-5f);
            Assert.AreEqual(0f, rs.Transitions[0].heatRelease, 1e-5f);
        }

        // ── Guards: editor exposure + no asset mutation ──────────────────────────────────────

        [Test]
        public void AffinityGroupEditor_DrawsNewThermalFields()
        {
            // The custom OnInspectorGUI would silently hide new serialized fields (the same bug class
            // caught in FluidSolver during CP7c). Pin that the editor references them.
            const string path = "Assets/_Project/Scripts/Systems/SimulationLOD0/Data/Editor/AffinityGroupEditor.cs";
            string src = File.ReadAllText(path);
            StringAssert.Contains("thermalTransitions", src, "Editor must draw thermalTransitions");
            StringAssert.Contains("thermalSources", src, "Editor must draw thermalSources");
        }

        [Test]
        public void ShippedAffinityGroupAssets_DoNotAuthorThermalData()
        {
            // Slice 1a writes no assets, so every shipped group must still take the default path —
            // which is what makes "runtime behavior unchanged" provable.
            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:AffinityGroup");
            Assert.IsNotEmpty(guids, "Expected the shipped AffinityGroup assets to be discoverable");

            foreach (var guid in guids)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var g = UnityEditor.AssetDatabase.LoadAssetAtPath<AffinityGroup>(path);
                Assert.IsNotNull(g, path);
                Assert.IsTrue(g.thermalTransitions == null || g.thermalTransitions.Length == 0,
                    $"{path} must not author thermal transitions in slice 1a");
                Assert.IsTrue(g.thermalSources == null || g.thermalSources.Length == 0,
                    $"{path} must not author thermal sources in slice 1a");
            }
        }
    }
}
