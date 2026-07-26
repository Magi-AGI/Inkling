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

        // ── CP8k: SINK transitions (removal, not conversion) ────────────────────────────────

        // The shipped rules must contain a cold Fire -> SINK transition: cold fire goes out, and it
        // becomes NOTHING. Lake explicitly ruled out smoke/steam for general cold-fire decay, so the
        // absence of a destination is the whole point and is asserted directly.
        [Test]
        public void Cp8Defaults_ExtinguishColdFire_ViaSink_NotConversion()
        {
            var rs = ThermalRuleBaker.Bake(null, ThermalDefaults.Cp8Defaults);
            Assert.IsTrue(rs.IsValid, rs.Error);

            var sinks = rs.Transitions.FindAll(t => ThermalRuleBaker.IsSink(t.toField));
            Assert.AreEqual(1, sinks.Count, "Exactly one sink in the shipped rules: cold fire going out");

            var fireSink = sinks[0];
            Assert.AreEqual((int)InkTypeId.Fire, fireSink.fromField, "…and it is Fire that gets removed");
            Assert.AreEqual(ThermalRegime.Cold, fireSink.regime, "Fire goes out when it is COLD");
            // CP8l lowered this from 0.85. Assert the ORDERING that makes fire work, not just the
            // literal: the sink must sit BELOW the temperature a fire-adjacent cell settles at (0.625 =
            // the neighbour average with one max-heat neighbour), or fire spreading into that cell is
            // extinguished before it can establish and heat its own cell — fire strangles itself and
            // plant only smoulders. It must still sit ABOVE room temperature (0.5), or fire adrift in
            // the cold would never go out and CP8k's whole point is lost.
            Assert.AreEqual(0.6f, fireSink.threshold, 1e-5f, "shipped cold-fire sink threshold");
            Assert.Less(fireSink.threshold, 0.625f,
                "…below what a fire-adjacent cell reaches, or fire cannot spread into plant at all");
            Assert.Greater(fireSink.threshold, 0.5f,
                "…but above room temperature, or cold fire would never go out");
            Assert.Greater(fireSink.rate, 1f, "…and it goes out RAPIDLY");

            // Heat-neutral: extinguishing must not itself move heat, or the sink becomes another term in
            // the very heat ratchet CP8k exists to remove.
            Assert.AreEqual(0f, fireSink.heatCost, 1e-5f, "Extinguishing must not consume heat");
            Assert.AreEqual(0f, fireSink.heatRelease, 1e-5f, "…nor release it");

            // No transition anywhere may turn Fire into Steam or Water — that is the smoke/puddle Lake
            // rejected for general cold-fire decay.
            foreach (var t in rs.Transitions)
            {
                if (t.fromField != (int)InkTypeId.Fire) continue;
                Assert.AreNotEqual((int)InkTypeId.Steam, t.toField, "Cold fire must not become smoke/steam");
                Assert.AreNotEqual((int)InkTypeId.Water, t.toField, "Cold fire must not become water");
            }
        }

        // The legacy fixture is frozen and must NOT pick up the sink — the same regression CP8d caused
        // when plant ignition leaked into Cp7Defaults and silently changed what those tests measured.
        [Test]
        public void Cp7Defaults_HaveNoSinkTransitions()
        {
            var legacy = ThermalRuleBaker.Bake(null, ThermalDefaults.Cp7Defaults);
            Assert.IsTrue(legacy.IsValid, legacy.Error);
            Assert.AreEqual(4, legacy.Transitions.Count, "Legacy fixture stays at condense/freeze/melt/boil");
            foreach (var t in legacy.Transitions)
                Assert.IsFalse(ThermalRuleBaker.IsSink(t.toField), "No sink in the legacy fixture");
        }

        // A sink is AUTHORABLE: toSlot == -1 (SinkField) EXACTLY means "remove this ink". It must skip
        // slot resolution entirely (there is no destination slot to resolve) and must not trip the
        // from == to guard.
        [Test]
        public void AuthoredSink_SinkFieldToSlot_BakesAsRemoval()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[]
            {
                T(0, -1, ThermalRegime.Cold, threshold: 0.85f),   // slot 0 = Fire -> SINK
            };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);

            Assert.IsTrue(rs.IsValid, "A negative toSlot must bake as a sink, not fail resolution: " + rs.Error);
            Assert.AreEqual(1, rs.Transitions.Count);
            Assert.AreEqual((int)InkTypeId.Fire, rs.Transitions[0].fromField);
            Assert.IsTrue(ThermalRuleBaker.IsSink(rs.Transitions[0].toField),
                "toField must be the SINK sentinel, not a resolved ink field");
        }

        // Guard the sentinel's blast radius: a sink is the ONLY way toField may be negative, and an
        // out-of-range POSITIVE slot must still be a hard error, exactly as before.
        [Test]
        public void OutOfRangeToSlot_IsStillHardError_SinkSentinelDidNotWidenIt()
        {
            var g = ThermalGroup();
            g.thermalTransitions = new[] { T(0, 7, ThermalRegime.Hot, threshold: 0.5f) };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);

            Assert.IsFalse(rs.IsValid,
                "An out-of-range positive slot must still be a hard error — the sink sentinel must not " +
                "have turned slot validation into a free-for-all");
        }

        // CP8k / Codex blocker. The sink test is `toSlot == -1` EXACTLY, not `toSlot < 0`. A blanket
        // negative check would silently bake -2 (a typo, or a stale serialized value) as a REMOVAL —
        // quietly deleting ink where the author expected a hard error. Only the one documented sentinel
        // may mean "sink"; every other negative must fail loudly.
        //
        // The two halves are asserted together so the test cannot pass by rejecting BOTH (which would
        // "fix" the widening by breaking the feature).
        [Test]
        public void OnlyMinusOne_IsTheSinkSentinel_OtherNegativesHardError()
        {
            // The PREDICATE must be exact too, not just the bake path. `IsSink` is what the inspector
            // preview uses to decide how to render a destination; if it stayed `< 0` while the baker
            // rejected -2, the preview would cheerfully draw a stale -2 as "removed" while validation
            // called it an error — two different stories about the same data.
            Assert.IsTrue(ThermalRuleBaker.IsSink(ThermalRuleBaker.SinkField), "-1 IS the sink");
            foreach (int notASink in new[] { -2, -5, int.MinValue, 0, 3 })
                Assert.IsFalse(ThermalRuleBaker.IsSink(notASink),
                    $"{notASink} is NOT a sink — only {ThermalRuleBaker.SinkField} is");

            var sinkGroup = ThermalGroup();
            sinkGroup.thermalTransitions = new[] { T(0, -1, ThermalRegime.Cold, threshold: 0.85f) };
            var valid = ThermalRuleBaker.Bake(Groups(sinkGroup), ThermalDefaults.Cp7Defaults);
            Assert.IsTrue(valid.IsValid, "-1 IS the sink sentinel and must bake: " + valid.Error);
            Assert.IsTrue(ThermalRuleBaker.IsSink(valid.Transitions[0].toField), "…as a removal");

            foreach (int bad in new[] { -2, -5, int.MinValue })
            {
                var g = ThermalGroup();
                g.thermalTransitions = new[] { T(0, bad, ThermalRegime.Cold, threshold: 0.85f) };
                var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);

                Assert.IsFalse(rs.IsValid,
                    $"toSlot {bad} must be a HARD ERROR, not silently treated as a sink — otherwise a " +
                    "typo quietly deletes ink instead of failing");
                StringAssert.Contains("out of range", rs.Error,
                    "…and the error must say so, naming the only legal negative destination");
            }
        }

        // CP8k / Codex blocker. The sentinel is useless if authors cannot reach it. The inspector popup
        // used to offer slots 0..3 only, so a sink existed in the data model but was unauthorable, and
        // the matrix preview clamped toSlot to 0..3 — rendering a Fire -> SINK as "Fire -> Fire", which
        // is worse than hiding it. Source-asserted because the drawer needs a live IMGUI context to run.
        [Test]
        public void AffinityGroupEditor_CanAuthorAndPreview_TheSinkDestination()
        {
            const string path = "Assets/_Project/Scripts/Systems/SimulationLOD0/Data/Editor/AffinityGroupEditor.cs";
            Assert.IsTrue(File.Exists(path), "AffinityGroupEditor.cs must exist");
            string src = File.ReadAllText(path);

            StringAssert.Contains("ThermalRuleBaker.SinkField", src,
                "The destination popup must offer the SINK sentinel, or it cannot be authored at all");
            StringAssert.Contains("DestinationValues", src,
                "Destinations need their own value list — sources cannot be sinks");
            StringAssert.Contains("IsSink", src,
                "The matrix preview must detect sinks and render them distinctly");

            Assert.IsFalse(src.Contains("int to = Mathf.Clamp(el.FindPropertyRelative(\"toSlot\").intValue, 0, 3)"),
                "REGRESSION: clamping toSlot into 0..3 renders a sink as a transition INTO slot 0 " +
                "(a Fire -> SINK would display as 'Fire -> Fire'), actively misleading the author");

            // A destination is exactly one of three things and the preview must distinguish all three:
            // -1 = sink, 0..3 = conversion, anything else = INVALID. Blurring the third into either of
            // the first two is how the author ends up trusting a rule the baker is actually rejecting.
            StringAssert.Contains("INVALID destination", src,
                "The preview must render an out-of-range destination as INVALID — not clamp it into a " +
                "slot, and not call it a sink. The baker hard-errors on it, so the preview must agree.");
        }

        // CP8g: the SHIPPED defaults make forming ice chill its cell; the LEGACY fixture must not.
        // Pinning both sides in one test keeps the two fixtures from drifting into each other — the
        // exact failure the CP8d plant-ignition regression caused when defaults leaked into CP7.
        [Test]
        public void Cp8Defaults_GiveFreezeOneShotHeatCost_ButCp7DoesNot()
        {
            BakedThermalTransition Freeze(ThermalDefaults d)
            {
                ThermalRuleSet rs = ThermalRuleBaker.Bake(null, d);
                Assert.IsTrue(rs.IsValid, rs.Error);
                foreach (var t in rs.Transitions)
                    if (t.fromField == (int)InkTypeId.Water &&
                        t.toField == (int)InkTypeId.Ice &&
                        t.regime == ThermalRegime.Cold)
                        return t;
                Assert.Fail("No Water -> Ice cold transition in the baked default rules");
                return default;
            }

            Assert.Greater(Freeze(ThermalDefaults.Cp8Defaults).heatCost, 0f,
                "Shipped defaults: forming ice must remove heat — ice is a cold source when it FORMS");
            Assert.AreEqual(0f, Freeze(ThermalDefaults.Cp7Defaults).heatCost, 1e-5f,
                "Legacy fixture: freeze stays heat-neutral so CP7 mechanics tests keep their numbers");
        }

        // CP8h: shipped condensation is GENTLE (only a little water per second from cooling steam),
        // while the legacy fixture keeps full-rate condensation because its kernel-mechanics tests pin
        // exact full-conversion numbers against it. Pinning both sides here stops the two fixtures from
        // drifting into each other.
        [Test]
        public void Cp8Defaults_CondenseGently_ButCp7StaysFullRate()
        {
            BakedThermalTransition Condense(ThermalDefaults d)
            {
                ThermalRuleSet rs = ThermalRuleBaker.Bake(null, d);
                Assert.IsTrue(rs.IsValid, rs.Error);
                foreach (var t in rs.Transitions)
                    if (t.fromField == (int)InkTypeId.Steam &&
                        t.toField == (int)InkTypeId.Water &&
                        t.regime == ThermalRegime.Cold)
                        return t;
                Assert.Fail("No Steam -> Water cold transition in the baked default rules");
                return default;
            }

            Assert.AreEqual(0.15f, Condense(ThermalDefaults.Cp8Defaults).rate, 1e-5f,
                "Shipped: cooling steam sheds only a little water per second");
            Assert.AreEqual(1f, Condense(ThermalDefaults.Cp7Defaults).rate, 1e-5f,
                "Legacy fixture: full-rate condensation, so CP7 mechanics tests keep their numbers");

            // The THRESHOLD must be untouched — CP8h is a rate change, not a gate change. Steam still
            // condenses whenever it is cold enough; it just does so slowly.
            Assert.AreEqual(0.65f, Condense(ThermalDefaults.Cp8Defaults).threshold, 1e-5f,
                "CP8h must not move the condense threshold");
        }

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
            // CP8g's ice-formation cooling must NOT leak into the legacy fixture, or these
            // kernel-mechanics tests would silently start measuring different heat numbers.
            Assert.AreEqual(0f, rs.Transitions[1].heatCost, 1e-5f,
                "Legacy CP7 freeze must stay heat-neutral");

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

        // ── CP8a: the ladder is now PER-INVERSE-PAIR, not a global "all cold <= all hot" ──────
        // The real hazard is a CYCLE: a cold A->B whose exact inverse hot B->A can also fire. Two
        // transitions that merely happen to be cold/hot but are NOT inverses can safely co-fire — and
        // the neutral-baseline layout REQUIRES it: at room temperature both steam->water (condense)
        // and ice->water (melt) are active, so condense sits ABOVE melt.

        [Test]
        public void NeutralLayout_CondenseAboveMelt_IsAccepted()
        {
            // An AUTHORED room-temperature layout — NOT the shipped CP8j one (which has freeze == melt
            // == .15). A group is free to author melt anywhere at or above freeze; .35 is just a
            // convenient arbitrary value that keeps the two thresholds distinct, which is what makes this
            // a sharper test of the validator than the shipped layout would be.
            //
            // The point under test is the VALIDATOR, not the tuning: the OLD global ladder rejected this
            // layout (max cold .65 > min hot .35). Per-inverse-pair validation must accept it.
            var g = ThermalGroup();
            g.thermalTransitions = new[]
            {
                T(2, 1, ThermalRegime.Cold, threshold: 0.65f),                  // Steam->Water (condense)
                T(1, 3, ThermalRegime.Cold, threshold: 0.15f),                  // Water->Ice   (freeze)
                T(3, 1, ThermalRegime.Hot, threshold: 0.35f, heatCost: 0.5f),   // Ice->Water   (melt)
                T(1, 2, ThermalRegime.Hot, threshold: 0.85f, heatCost: 0.5f),   // Water->Steam (boil)
            };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);

            Assert.IsTrue(rs.IsValid,
                "Room-temperature layout (condense above melt) must be accepted: " + rs.Error);
            Assert.AreEqual(4, rs.Transitions.Count);
        }

        [Test]
        public void NonInverseColdAboveHot_IsAccepted()
        {
            // AUTHORED thresholds, not the shipped CP8j ones — .35 is an arbitrary melt value chosen to
            // create the cold-above-hot overlap this test exists to exercise.
            //
            // Cold Steam->Water (.65) and hot Ice->Water (.35) are NOT inverses — both PRODUCE water,
            // so co-firing is correct physics, not oscillation. (The old global ladder rejected this;
            // note the pre-CP8a ladder test asserted exactly this non-inverse pair, i.e. it was
            // guarding a hazard that does not exist.)
            var g = ThermalGroup();
            g.thermalTransitions = new[]
            {
                T(2, 1, ThermalRegime.Cold, threshold: 0.65f),
                T(3, 1, ThermalRegime.Hot, threshold: 0.35f, heatCost: 0.5f),
            };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsTrue(rs.IsValid, "Non-inverse cold/hot overlap is safe: " + rs.Error);
        }

        [Test]
        public void InverseCycle_FreezeAboveMelt_IsHardError()
        {
            // Water->Ice (cold, .5) vs its INVERSE Ice->Water (hot, .3): a cell at .4 would freeze and
            // melt forever. This is the genuine cycle the invariant exists to stop.
            var g = ThermalGroup();
            g.thermalTransitions = new[]
            {
                T(1, 3, ThermalRegime.Cold, threshold: 0.5f),                   // Water->Ice
                T(3, 1, ThermalRegime.Hot, threshold: 0.3f, heatCost: 0.5f),    // Ice->Water (inverse)
            };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid, "Inverse water<->ice cycle with cold above hot must be rejected");
            Assert.IsEmpty(rs.Transitions, "An invalid set must be inert");
        }

        [Test]
        public void InverseCycle_CondenseAboveBoil_IsHardError()
        {
            // Steam->Water (cold, .9) vs its INVERSE Water->Steam (hot, .5): water<->steam churn.
            var g = ThermalGroup();
            g.thermalTransitions = new[]
            {
                T(2, 1, ThermalRegime.Cold, threshold: 0.9f),                   // Steam->Water
                T(1, 2, ThermalRegime.Hot, threshold: 0.5f, heatCost: 0.5f),    // Water->Steam (inverse)
            };
            var rs = ThermalRuleBaker.Bake(Groups(g), ThermalDefaults.Cp7Defaults);
            Assert.IsFalse(rs.IsValid, "Inverse water<->steam cycle with cold above hot must be rejected");
            Assert.IsEmpty(rs.Transitions);
        }

        [Test]
        public void Cp8Defaults_UseNeutralLayout_AndBakeCleanly()
        {
            // Cp8Defaults is the SHIPPED layout (mirrors the SimDriver serialized defaults).
            // Cp7Defaults is retained as the legacy kernel-mechanics fixture used by the older tests.
            var rs = ThermalRuleBaker.Bake(null, ThermalDefaults.Cp8Defaults);
            Assert.IsTrue(rs.IsValid, "The shipped neutral defaults must bake cleanly: " + rs.Error);

            // Order is cold-then-hot: [condense, freeze, melt, boil].
            Assert.AreEqual(0.65f, rs.Transitions[0].threshold, 1e-5f, "condense");
            Assert.AreEqual(0.15f, rs.Transitions[1].threshold, 1e-5f, "freeze");
            Assert.AreEqual(0.15f, rs.Transitions[2].threshold, 1e-5f, "melt");
            Assert.AreEqual(0.85f, rs.Transitions[3].threshold, 1e-5f, "boil");

            // CP8j: freeze and melt are the SAME point — the freezing point. Any gap between them is a
            // dead band where ice sits above freezing and still will not melt, which is ice divorced from
            // cold. Equal thresholds are legal because the baker's inverse-cycle rule compares with `>`,
            // and they are STABLE because the cold gate needs heat strictly below the threshold while the
            // hot gate is driven by heat strictly above it — at the boundary itself, neither fires.
            Assert.AreEqual(rs.Transitions[1].threshold, rs.Transitions[2].threshold, 1e-5f,
                "Ice must melt the instant it is above freezing — freeze and melt share one boundary");

            // The shipped layout is exactly the one the OLD global ladder would have rejected.
            Assert.Greater(rs.Transitions[0].threshold, rs.Transitions[2].threshold,
                "condense must sit ABOVE melt for room-temperature water stability");
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
