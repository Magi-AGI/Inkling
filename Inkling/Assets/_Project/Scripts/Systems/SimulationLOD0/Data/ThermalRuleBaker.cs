using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// GPU upload layout for a baked transition. MUST match `struct GpuThermalTransition` in
    /// ThermalInteractions.compute field-for-field. Explicitly padded to 32 bytes (a 16-byte
    /// multiple) rather than relying on an implicit stride.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuThermalTransition
    {
        public int fromField;
        public int toField;
        public int regime;      // 0 = Cold, 1 = Hot
        public int pad0;
        public float threshold;
        public float rate;
        public float heatCost;
        public float heatRelease;

        public const int Stride = 32;
    }

    /// <summary>
    /// GPU upload layout for a baked heat source. MUST match `struct GpuThermalSource` in
    /// ThermalInteractions.compute field-for-field. Padded to 16 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuThermalSource
    {
        public int field;
        public float heatEmissionRate;
        public float fuelCost;
        public int pad0;

        public const int Stride = 16;
    }

    /// <summary>Global thermal knobs + the CP7b/CP7c defaults used when nothing is authored.</summary>
    public struct ThermalDefaults
    {
        public float fireHeatEmissionRate, fireHeatFuelCost;
        public float condenseThreshold, condenseRate, condenseHeatRelease;
        public float freezeThreshold, freezeRate;

        // CP8g: heat REMOVED per unit of water that freezes into ice — the one-shot cooling of ice
        // FORMING. Scales with the amount actually converted, so a cell holding settled ice with no
        // water left to freeze converts nothing and therefore cools nothing. That is the whole design:
        // ice is a cold source at creation, never a continuous cold emitter. Legacy Cp7Defaults keeps
        // this at 0 so the frozen kernel-mechanics fixtures keep pinning their original numbers.
        public float freezeHeatCost;
        public float meltThreshold, meltRate, meltHeatCost;
        public float boilThreshold, boilRate, boilHeatCost;

        // CP8d: heat-driven plant ignition (Plant -> Fire above an ignition temperature).
        // Gated by an EXPLICIT flag rather than appended unconditionally: the legacy Cp7Defaults is a
        // frozen fixture for the kernel-mechanics tests and must keep baking exactly its original 4
        // transitions. Silently growing it to 6 changed what those tests were pinning.
        public bool includePlantIgnition;
        public float plantIgnitionThreshold, plantIgnitionRate, plantIgnitionHeatCost;

        // CP8k: cold fire GOES OUT. A cold transition Fire -> SINK (removal, not conversion), so a
        // guttering flame does not mint smoke or water on its way out. Gated by an explicit flag for
        // the same reason as plant ignition: the legacy Cp7Defaults is a frozen fixture and must keep
        // baking exactly its original 4 transitions.
        public bool includeFireColdSink;
        public float fireSinkThreshold, fireSinkRate;

        /// <summary>
        /// CP8a SHIPPED layout — mirrors the SimDriver serialized defaults. Thresholds are placed
        /// around a NEUTRAL (room) temperature of 0.5 so that water is the stable phase:
        ///
        ///     min 0 .. [freeze == melt == .15] .. [NEUTRAL .5] .. condense .65 .. boil .85 .. max 1
        ///
        /// CP8j collapsed freeze and melt onto ONE point (they were .15 and .35). The gap between them
        /// was a dead band in which ice was above freezing yet still refused to melt.
        ///
        /// At neutral: water neither freezes (needs &lt; .15) nor boils (needs &gt; .85); ice melts
        /// (.5 &gt; .15); steam SLOWLY condenses (.5 &lt; .65). Note condense sits ABOVE melt — physically
        /// required, and only legal because the baker validates per-INVERSE-PAIR rather than with a
        /// global "all cold &lt;= all hot" ladder.
        /// </summary>
        public static ThermalDefaults Cp8Defaults => new ThermalDefaults
        {
            fireHeatEmissionRate = 4f,
            fireHeatFuelCost = 0f,
            condenseThreshold = 0.65f,

            // CP8h: condensation is deliberately GENTLE. Cooling steam sheds only a little water per
            // second (~15%) instead of collapsing wholesale the instant it drops below the threshold —
            // steam should linger and drizzle, not vanish into a puddle in one tick. This is a RATE
            // change only: the threshold is untouched, so steam still condenses whenever it is cold
            // enough, just slowly. Legacy Cp7Defaults keeps 1f; its kernel-mechanics tests pin exact
            // full-conversion numbers against that.
            condenseRate = 0.15f,
            condenseHeatRelease = 0f,
            freezeThreshold = 0.15f,
            freezeRate = 0.4f,

            // CP8g: forming ice CHILLS its cell — Lake's "ice should be a cold source, but only when it
            // forms". The key property is that it scales with the amount CONVERTED: settled ice with no
            // water left to freeze converts nothing, so it cools nothing and just sits there.
            //
            // CP8k reduced this from 1.0, which was the dominant term in a global heat RATCHET. Every
            // thermal transition is a heat sink (freeze, melt, boil, ignition) and NONE returns heat, so
            // a water -> ice -> water round trip destroyed 1.0 + 0.5 = 1.5 units of heat and put the
            // matter back exactly where it started: a perpetual refrigerator. The whole field trended to
            // frozen. 0.2 keeps the "forming ice is cold" feel — painted ice gets its chill from the
            // injection stamp anyway, which stamps the floor outright — without the runaway.
            freezeHeatCost = 0.1f,

            // CP8j: melt sits EXACTLY ON the freeze point, not above it. A gap between them (it was
            // 0.15..0.35) is a band where ice is above freezing yet still refuses to melt — ice looking
            // cold while sitting at a temperature that is not cold. Lake: "ice above the freezing point
            // should simply melt."
            //
            // Equal thresholds are legal AND stable, which is the whole reason this works:
            //   cold gate is `heat >= threshold => skip`  => freezes only strictly BELOW 0.15
            //   hot  gate is `excess = heat - threshold`  => at exactly 0.15 excess is 0, so conv is 0
            // so at the boundary neither fires and there is no freeze/melt churn. The baker's
            // inverse-cycle rule compares with `>` (not `>=`), so it accepts freeze == melt.
            meltThreshold = 0.15f,
            meltRate = 1f,
            meltHeatCost = 0.10f,
            boilThreshold = 0.85f,
            boilRate = 1f,
            boilHeatCost = 0.5f,

            // CP8e: SPONTANEOUS combustion from ambient heat alone, deliberately set just below max
            // heat (1.0). This is the rare extreme case — a cell has to be practically a furnace — not
            // the normal route for fire to spread. Everyday fire spread stays with the legacy
            // Fire x Plant CONTACT reaction in OrganicGroup, which this threshold does not gate.
            // Burning consumes heat (endothermic pyrolysis), which also bounds the conversion so a hot
            // cell cannot flash the whole plant mass to fire in one step.
            includePlantIgnition = true,
            plantIgnitionThreshold = 0.75f,
            plantIgnitionRate = 0.5f,
            plantIgnitionHeatCost = 0.25f,

            // CP8k: fire only burns where it is genuinely HOT. Below the boil threshold it goes out
            // rapidly — removed, not converted, so a dying flame leaves no smoke or puddle behind.
            // Fire emits heat into its own cell, so a healthy flame keeps itself above 0.85 and is
            // unaffected; it is fire that has DRIFTED somewhere cold that gutters. Heat-neutral by
            // design (no heatCost/heatRelease): fire going out must not itself chill the cell, or we
            // would just be reintroducing the ratchet under a new name.
            includeFireColdSink = true,
            fireSinkThreshold = 0.6f,
            fireSinkRate = 4f,
        };

        /// <summary>
        /// LEGACY zero-baseline layout (pre-CP8a). Retained as a fixture for the kernel-mechanics
        /// tests, which pin numeric conversion behaviour against these thresholds. NOT the shipped
        /// defaults — see <see cref="Cp8Defaults"/>.
        /// </summary>
        public static ThermalDefaults Cp7Defaults => new ThermalDefaults
        {
            fireHeatEmissionRate = 1f,
            fireHeatFuelCost = 0f,
            condenseThreshold = 0.2f,
            condenseRate = 1f,
            condenseHeatRelease = 0f,
            freezeThreshold = 0.2f,
            freezeRate = 1f,

            // Explicitly 0: CP8g's ice-formation cooling must NOT leak into this frozen fixture, or the
            // kernel-mechanics tests would silently start measuring different heat numbers.
            freezeHeatCost = 0f,

            meltThreshold = 0.4f,
            meltRate = 1f,
            meltHeatCost = 0.5f,
            boilThreshold = 0.7f,
            boilRate = 1f,
            boilHeatCost = 0.5f,
        };
    }

    /// <summary>A transition resolved to absolute iparticle field indices (not group-local slots).</summary>
    public struct BakedThermalTransition
    {
        public int fromField, toField;
        public ThermalRegime regime;
        public float threshold, rate, heatCost, heatRelease;
    }

    /// <summary>A heat source resolved to an absolute iparticle field index.</summary>
    public struct BakedThermalSource
    {
        public int field;
        public float heatEmissionRate, fuelCost;
    }

    /// <summary>
    /// The deterministic, validated rule set for one thermal dispatch. Transitions are ordered
    /// COLD (authored order) then HOT (authored order). If <see cref="IsValid"/> is false the rule
    /// set is INERT (both lists empty) — the thermal pass must pass through unchanged. We never
    /// truncate, first-win, or average: an authoring conflict is a loud, deterministic failure.
    /// </summary>
    public class ThermalRuleSet
    {
        public bool IsValid = true;
        public string Error;
        public readonly List<string> Warnings = new List<string>();
        public readonly List<BakedThermalTransition> Transitions = new List<BakedThermalTransition>();
        public readonly List<BakedThermalSource> Sources = new List<BakedThermalSource>();
        public bool UsedDefaultTransitions;
        public bool UsedDefaultSources;

        public ThermalRuleSet Fail(string error)
        {
            IsValid = false;
            Error = error;
            Transitions.Clear();
            Sources.Clear();
            return this;
        }
    }

    /// <summary>
    /// Bakes the thermal rules authored across all ACTIVE AffinityGroups into one ordered rule set
    /// for a single thermal dispatch (heat is one global scalar field, so per-group dispatches would
    /// break snapshot/cold/hot phase coherence).
    ///
    /// Replacement is PER-CATEGORY: if any group authors >= 1 transition, authored transitions fully
    /// replace the defaults; independently, if any group authors >= 1 source, authored sources fully
    /// replace the default source. Never per-rule merging.
    ///
    /// All collision detection uses RESOLVED iparticle field indices, never group-local slot numbers —
    /// group A slot1=Water and group B slot3=Water are the same ink and must collide.
    /// </summary>
    public static class ThermalRuleBaker
    {
        public const int MaxTransitions = 8;   // 4 slots x 2 regimes under the one-outgoing invariant
        public const int MaxSources = 4;       // one per slot

        /// <summary>
        /// CP8k SINK sentinel for <c>toField</c>: the source ink is REMOVED rather than converted into
        /// another ink. Authored as a negative <c>toSlot</c>.
        ///
        /// <para>
        /// Every transition until now was a paired <c>from-- / to++</c>, which is what made the pass
        /// incapable of minting mass. A sink is the one deliberate exception: it only ever DESTROYS ink,
        /// never creates it, so it cannot mint either — the conservation argument still holds, just on
        /// the safe side of the inequality.
        /// </para>
        /// <para>
        /// This exists because "cold fire simply goes out" was previously inexpressible: a transition
        /// maps one ink field to another, and <c>from == to</c> is a hard error, so there was no way to
        /// say "this ink stops existing". Routing dying fire into Steam or Water was rejected — a
        /// guttering flame should not mint smoke or puddles.
        /// </para>
        /// </summary>
        public const int SinkField = -1;

        /// <summary>
        /// EXACT sentinel test. Deliberately <c>== SinkField</c> and not <c>&lt; 0</c>: -1 is the only
        /// value that means "remove this ink". Any other negative is an out-of-range destination and a
        /// hard bake error, so nothing else may be *described* as a sink either — a loose predicate here
        /// would let the inspector preview happily render a stale <c>toSlot = -2</c> as a removal while
        /// the baker was rejecting it, telling the author two different stories about the same data.
        /// </summary>
        public static bool IsSink(int toField) => toField == SinkField;

        /// <summary>
        /// Fills caller-owned arrays with the GPU layout (no allocation). Returns the element count.
        /// An INVALID rule set yields 0 — the runtime must then flag the set invalid so the kernel
        /// passes through inert rather than partially applying anything.
        /// </summary>
        public static int ToGpu(ThermalRuleSet rules, GpuThermalTransition[] dst)
        {
            if (rules == null || !rules.IsValid || dst == null) return 0;

            int n = Mathf.Min(rules.Transitions.Count, dst.Length);
            for (int i = 0; i < n; i++)
            {
                BakedThermalTransition t = rules.Transitions[i];
                dst[i] = new GpuThermalTransition
                {
                    fromField = t.fromField,
                    toField = t.toField,
                    regime = (int)t.regime,
                    pad0 = 0,
                    threshold = t.threshold,
                    rate = t.rate,
                    heatCost = t.heatCost,
                    heatRelease = t.heatRelease,
                };
            }
            return n;
        }

        /// <summary>Fills caller-owned arrays with the GPU layout (no allocation). Returns the element count.</summary>
        public static int ToGpu(ThermalRuleSet rules, GpuThermalSource[] dst)
        {
            if (rules == null || !rules.IsValid || dst == null) return 0;

            int n = Mathf.Min(rules.Sources.Count, dst.Length);
            for (int i = 0; i < n; i++)
            {
                BakedThermalSource s = rules.Sources[i];
                dst[i] = new GpuThermalSource
                {
                    field = s.field,
                    heatEmissionRate = s.heatEmissionRate,
                    fuelCost = s.fuelCost,
                    pad0 = 0,
                };
            }
            return n;
        }

        public static ThermalRuleSet Bake(IReadOnlyList<AffinityGroup> groups, ThermalDefaults defaults)
        {
            var rs = new ThermalRuleSet();

            var authoredCold = new List<BakedThermalTransition>();
            var authoredHot = new List<BakedThermalTransition>();
            var authoredSources = new List<BakedThermalSource>();
            bool anyTransitions = false, anySources = false;

            // ── 1. Per-group validation + resolution to field indices ────────────────────
            if (groups != null)
            {
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi];
                    if (g == null) continue;

                    if (g.thermalTransitions != null)
                    {
                        foreach (var t in g.thermalTransitions)
                        {
                            if (t == null) continue;
                            anyTransitions = true;

                            if (!TryResolve(g, t.fromSlot, out int from, out string err))
                                return rs.Fail($"[{Name(g)}] transition source: {err}");

                            // CP8k: toSlot == SinkField (-1) EXACTLY means SINK — the source ink is
                            // removed outright rather than converted. It resolves to no slot, so it
                            // deliberately skips slot resolution (and can never trip the from == to
                            // check).
                            //
                            // The test is `== SinkField`, NOT `< 0`. A blanket negative check would
                            // silently bake toSlot = -2 (a typo, or a stale serialized value) as a
                            // REMOVAL — quietly deleting ink instead of hard-erroring. Only the one
                            // documented sentinel is a sink; every other out-of-range value, negative or
                            // positive, must still fail loudly.
                            int to;
                            if (t.toSlot == SinkField)
                            {
                                to = SinkField;
                            }
                            else if (t.toSlot < 0)
                            {
                                return rs.Fail(
                                    $"[{Name(g)}] transition destination: slot {t.toSlot} is out of range. " +
                                    $"The only legal negative destination is {SinkField} (SINK — removes the " +
                                    "source ink outright).");
                            }
                            else
                            {
                                if (!TryResolve(g, t.toSlot, out to, out err))
                                    return rs.Fail($"[{Name(g)}] transition destination: {err}");
                                if (from == to)
                                    return rs.Fail($"[{Name(g)}] transition from == to (field {from}); a transition must change ink.");
                            }

                            var baked = new BakedThermalTransition
                            {
                                fromField = from,
                                toField = to,
                                regime = t.regime,
                                threshold = Mathf.Max(0f, t.threshold),
                                rate = Mathf.Max(0f, t.rate),
                                heatCost = Mathf.Max(0f, t.heatCost),
                                heatRelease = Mathf.Max(0f, t.heatRelease),
                            };
                            if (t.regime == ThermalRegime.Cold) authoredCold.Add(baked);
                            else authoredHot.Add(baked);
                        }
                    }

                    if (g.thermalSources != null)
                    {
                        foreach (var s in g.thermalSources)
                        {
                            if (s == null) continue;
                            anySources = true;

                            if (!TryResolve(g, s.slot, out int field, out string err))
                                return rs.Fail($"[{Name(g)}] heat source: {err}");

                            authoredSources.Add(new BakedThermalSource
                            {
                                field = field,
                                heatEmissionRate = Mathf.Max(0f, s.heatEmissionRate),
                                fuelCost = Mathf.Max(0f, s.fuelCost),
                            });
                        }
                    }
                }
            }

            // ── 2. Caps (checked before dedup: disjoint-ink groups can legitimately overflow) ──
            if (authoredCold.Count + authoredHot.Count > MaxTransitions)
                return rs.Fail($"Too many thermal transitions ({authoredCold.Count + authoredHot.Count} > {MaxTransitions}).");
            if (authoredSources.Count > MaxSources)
                return rs.Fail($"Too many thermal sources ({authoredSources.Count} > {MaxSources}).");

            // ── 3. Global collision checks on RESOLVED field indices ─────────────────────
            var seenExact = new HashSet<(int, int, ThermalRegime)>();
            var seenOutgoing = new HashSet<(int, ThermalRegime)>();
            foreach (var t in Concat(authoredCold, authoredHot))
            {
                if (!seenExact.Add((t.fromField, t.toField, t.regime)))
                    return rs.Fail($"Duplicate thermal transition (field {t.fromField} -> {t.toField}, {t.regime}) across the active groups.");
                // CP7d-scoped restriction; the ordered-list model supports multi-target and would
                // consume the running remainder in authored order once this is lifted.
                if (!seenOutgoing.Add((t.fromField, t.regime)))
                    return rs.Fail($"More than one outgoing {t.regime} transition from field {t.fromField} (CP7d allows one per source per regime).");
            }

            var seenSource = new HashSet<int>();
            foreach (var s in authoredSources)
            {
                if (!seenSource.Add(s.field))
                    return rs.Fail($"Duplicate thermal source for field {s.field} across the active groups.");
            }

            // ── 4. Per-category replacement ──────────────────────────────────────────────
            if (anyTransitions)
            {
                rs.Transitions.AddRange(authoredCold);
                rs.Transitions.AddRange(authoredHot);
            }
            else
            {
                rs.Transitions.AddRange(DefaultTransitions(defaults));
                rs.UsedDefaultTransitions = true;
            }

            if (anySources)
            {
                rs.Sources.AddRange(authoredSources);
            }
            else
            {
                rs.Sources.AddRange(DefaultSources(defaults));
                rs.UsedDefaultSources = true;
            }

            if (anyTransitions != anySources)
            {
                string authored = anyTransitions ? "transitions" : "sources";
                string fellBack = anyTransitions ? "sources" : "transitions";
                rs.Warnings.Add($"Thermal {authored} are authored but {fellBack} are not — {fellBack} fall back to CP7 defaults. " +
                                "Per-category replacement: author both, or neither, to avoid surprises.");
            }

            // ── 5. Cycle invariant over the FINAL set (CP8a) ──────────────────────────────
            // The hazard is a CYCLE, not a global ordering: a cold A->B whose exact INVERSE hot B->A
            // can also fire would churn A<->B forever. So for every inverse pair we require
            //     cold(A->B).threshold <= hot(B->A).threshold
            // (i.e. the "freeze below, melt above" band must not overlap).
            //
            // This REPLACES the old global "max(cold) <= min(hot)" ladder, which was strictly stronger
            // than the real hazard and rejected the physically correct room-temperature layout: at
            // neutral, BOTH steam->water (condense) and ice->water (melt) must be active, which forces
            // condense ABOVE melt. Those two are not inverses — they both PRODUCE water, so they can
            // safely co-fire. Only true inverses can oscillate.
            foreach (var cold in rs.Transitions)
            {
                if (cold.regime != ThermalRegime.Cold) continue;

                foreach (var hot in rs.Transitions)
                {
                    if (hot.regime != ThermalRegime.Hot) continue;
                    // Inverse pair? hot must run exactly backwards along the cold edge.
                    if (hot.fromField != cold.toField || hot.toField != cold.fromField) continue;

                    if (cold.threshold > hot.threshold)
                        return rs.Fail(
                            $"Thermal cycle violated: cold transition (field {cold.fromField} -> {cold.toField}, " +
                            $"threshold {cold.threshold}) sits ABOVE its inverse hot transition " +
                            $"(field {hot.fromField} -> {hot.toField}, threshold {hot.threshold}). " +
                            "A cell between them would convert back and forth forever.");
                }
            }

            return rs;
        }

        // ── Defaults (must mirror the hardcoded CP7b/CP7c kernel exactly, including ORDER) ──

        public static List<BakedThermalTransition> DefaultTransitions(ThermalDefaults d)
        {
            int fire = (int)InkTypeId.Fire;
            int water = (int)InkTypeId.Water;
            int steam = (int)InkTypeId.Steam;
            int ice = (int)InkTypeId.Ice;
            int plantSeeded = (int)InkTypeId.PlantSeeded;
            int plantGrown = (int)InkTypeId.PlantGrown;

            var transitions = new List<BakedThermalTransition>
            {
                // Cold, in authored order: condense, then freeze.
                new BakedThermalTransition { fromField = steam, toField = water, regime = ThermalRegime.Cold,
                    threshold = d.condenseThreshold, rate = d.condenseRate, heatRelease = d.condenseHeatRelease },
                // CP8g: freezing REMOVES heat as the ice forms (one-shot, scales with what converted).
                new BakedThermalTransition { fromField = water, toField = ice, regime = ThermalRegime.Cold,
                    threshold = d.freezeThreshold, rate = d.freezeRate, heatRelease = 0f,
                    heatCost = d.freezeHeatCost },

                // Hot, in authored order: melt, then boil.
                new BakedThermalTransition { fromField = ice, toField = water, regime = ThermalRegime.Hot,
                    threshold = d.meltThreshold, rate = d.meltRate, heatCost = d.meltHeatCost },
                new BakedThermalTransition { fromField = water, toField = steam, regime = ThermalRegime.Hot,
                    threshold = d.boilThreshold, rate = d.boilRate, heatCost = d.boilHeatCost },
            };

            // CP8k: cold fire GOES OUT — a COLD transition Fire -> SINK. REMOVAL, not conversion: a
            // guttering flame must not mint smoke or a puddle, so this is the one transition with no
            // destination ink. Deliberately HEAT-NEUTRAL (no heatCost, no heatRelease) — fire dying must
            // not itself chill the cell, or we would be reintroducing the very heat ratchet CP8k exists
            // to remove, just under a new name.
            //
            // Fire is its own heat source, so a healthy flame holds its own cell above the threshold and
            // is untouched. What this culls is fire that has DRIFTED somewhere cold. Fire has no other
            // outgoing cold transition (one-outgoing rule holds) and a sink has no inverse (no cycle).
            // Appended only on opt-in, so the legacy Cp7Defaults fixture keeps its original 4.
            if (d.includeFireColdSink)
            {
                transitions.Add(new BakedThermalTransition
                {
                    fromField = fire, toField = SinkField, regime = ThermalRegime.Cold,
                    threshold = d.fireSinkThreshold, rate = d.fireSinkRate,
                    heatCost = 0f, heatRelease = 0f,
                });
            }

            // CP8d: heat-driven plant IGNITION — plant must actually get HOT to catch fire. Appended
            // only when the caller opts in, so the legacy Cp7Defaults fixture keeps baking exactly its
            // original 4 transitions and the CP7 tests keep pinning what they were written to pin.
            //
            // Each plant channel has exactly one outgoing HOT transition, and Fire has no inverse cold
            // transition back to plant, so neither the one-outgoing-per-source-per-regime rule nor the
            // inverse-cycle rule is violated. 6 transitions when enabled — under the cap of 8.
            if (d.includePlantIgnition)
            {
                transitions.Add(new BakedThermalTransition { fromField = plantSeeded, toField = fire, regime = ThermalRegime.Hot,
                    threshold = d.plantIgnitionThreshold, rate = d.plantIgnitionRate, heatCost = d.plantIgnitionHeatCost });
                transitions.Add(new BakedThermalTransition { fromField = plantGrown, toField = fire, regime = ThermalRegime.Hot,
                    threshold = d.plantIgnitionThreshold, rate = d.plantIgnitionRate, heatCost = d.plantIgnitionHeatCost });
            }

            return transitions;
        }

        public static List<BakedThermalSource> DefaultSources(ThermalDefaults d)
        {
            return new List<BakedThermalSource>
            {
                new BakedThermalSource { field = (int)InkTypeId.Fire,
                    heatEmissionRate = d.fireHeatEmissionRate, fuelCost = d.fireHeatFuelCost },
            };
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────

        private static bool TryResolve(AffinityGroup g, int slot, out int field, out string error)
        {
            field = -1;
            if (slot < 0 || slot > 3) { error = $"slot {slot} out of range (0..3)."; return false; }
            if (g.inks == null || g.inks.Length != 4 || g.inks[slot] == null)
            {
                error = $"slot {slot} has no InkTypeDef assigned.";
                return false;
            }
            field = g.inks[slot].ParticleFieldIndex;
            error = null;
            return true;
        }

        private static string Name(AffinityGroup g) =>
            string.IsNullOrEmpty(g.groupName) ? g.name : g.groupName;

        private static IEnumerable<BakedThermalTransition> Concat(
            List<BakedThermalTransition> a, List<BakedThermalTransition> b)
        {
            foreach (var x in a) yield return x;
            foreach (var x in b) yield return x;
        }
    }
}
