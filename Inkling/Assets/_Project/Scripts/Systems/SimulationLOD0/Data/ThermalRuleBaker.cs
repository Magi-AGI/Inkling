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
        public float meltThreshold, meltRate, meltHeatCost;
        public float boilThreshold, boilRate, boilHeatCost;

        /// <summary>Mirrors the current SimDriver serialized defaults (CP5 + CP7b + CP7c).</summary>
        public static ThermalDefaults Cp7Defaults => new ThermalDefaults
        {
            fireHeatEmissionRate = 1f,
            fireHeatFuelCost = 0f,
            condenseThreshold = 0.2f,
            condenseRate = 1f,
            condenseHeatRelease = 0f,
            freezeThreshold = 0.2f,
            freezeRate = 1f,
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
                            if (!TryResolve(g, t.toSlot, out int to, out err))
                                return rs.Fail($"[{Name(g)}] transition destination: {err}");
                            if (from == to)
                                return rs.Fail($"[{Name(g)}] transition from == to (field {from}); a transition must change ink.");

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

            // ── 5. Ladder invariant over the FINAL set: every cold threshold <= every hot ──
            float maxCold = float.NegativeInfinity, minHot = float.PositiveInfinity;
            bool hasCold = false, hasHot = false;
            foreach (var t in rs.Transitions)
            {
                if (t.regime == ThermalRegime.Cold) { hasCold = true; maxCold = Mathf.Max(maxCold, t.threshold); }
                else { hasHot = true; minHot = Mathf.Min(minHot, t.threshold); }
            }
            if (hasCold && hasHot && maxCold > minHot)
                return rs.Fail($"Thermal ladder violated: highest cold threshold ({maxCold}) exceeds lowest hot threshold ({minHot}). " +
                               "A cell could both freeze and melt in one pass.");

            return rs;
        }

        // ── Defaults (must mirror the hardcoded CP7b/CP7c kernel exactly, including ORDER) ──

        public static List<BakedThermalTransition> DefaultTransitions(ThermalDefaults d)
        {
            int water = (int)InkTypeId.Water;
            int steam = (int)InkTypeId.Steam;
            int ice = (int)InkTypeId.Ice;

            return new List<BakedThermalTransition>
            {
                // Cold, in authored order: condense, then freeze.
                new BakedThermalTransition { fromField = steam, toField = water, regime = ThermalRegime.Cold,
                    threshold = d.condenseThreshold, rate = d.condenseRate, heatRelease = d.condenseHeatRelease },
                new BakedThermalTransition { fromField = water, toField = ice, regime = ThermalRegime.Cold,
                    threshold = d.freezeThreshold, rate = d.freezeRate, heatRelease = 0f },

                // Hot, in authored order: melt, then boil.
                new BakedThermalTransition { fromField = ice, toField = water, regime = ThermalRegime.Hot,
                    threshold = d.meltThreshold, rate = d.meltRate, heatCost = d.meltHeatCost },
                new BakedThermalTransition { fromField = water, toField = steam, regime = ThermalRegime.Hot,
                    threshold = d.boilThreshold, rate = d.boilRate, heatCost = d.boilHeatCost },
            };
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
