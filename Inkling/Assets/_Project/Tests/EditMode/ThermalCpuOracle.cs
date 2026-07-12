using UnityEngine;
using Magi.Inkling.Systems.SimulationLOD0;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.EditMode
{
    /// <summary>
    /// CPU reference implementation of the CP7d thermal execution model. This is the SPEC ORACLE for
    /// the (future) buffer-driven kernel: emission -> snapshot -> Cold phase (authored order) ->
    /// Hot phase (authored order) -> clamp. It is test-only and is deliberately NOT wired into the
    /// runtime. Parity tests assert it reproduces today's hardcoded CP5/CP7b/CP7c GPU results.
    /// </summary>
    public static class ThermalCpuOracle
    {
        private const float EPS = 1e-6f;

        /// <summary>Per-cell thermal state: ink concentrations indexed by iparticle FIELD index (0..9), plus heat.</summary>
        public class Cell
        {
            public readonly float[] Inks = new float[10];
            public float Heat;

            public float this[InkTypeId id]
            {
                get => Inks[(int)id];
                set => Inks[(int)id] = value;
            }
        }

        /// <summary>
        /// Applies one thermal step in-place. An INVALID rule set is inert (exact pass-through),
        /// matching the runtime contract.
        /// </summary>
        public static void Apply(Cell c, ThermalRuleSet rules, float dt,
            float minTemp, float maxHeat, bool enableHeatSources)
        {
            if (rules == null || !rules.IsValid) return;

            // 0. Emission (fuel-like): heat added only up to headroom AND only as much as local fuel
            //    supports; fuel burned in proportion to the heat ACTUALLY added. Source clamped >= 0.
            if (enableHeatSources)
            {
                foreach (var s in rules.Sources)
                {
                    float fuel = Mathf.Max(0f, c.Inks[s.field]);
                    float rawEmission = fuel * s.heatEmissionRate * dt;
                    float headroom = Mathf.Max(0f, maxHeat - c.Heat);
                    float heatAdded;

                    if (s.fuelCost > EPS)
                    {
                        heatAdded = Mathf.Min(rawEmission, Mathf.Min(headroom, fuel / s.fuelCost));
                        c.Inks[s.field] = Mathf.Max(0f, fuel - heatAdded * s.fuelCost);
                    }
                    else
                    {
                        heatAdded = Mathf.Min(rawEmission, headroom);
                        c.Inks[s.field] = fuel;   // no burn, but still clamp an underflowed source to 0
                    }

                    c.Heat += heatAdded;   // heatAdded >= 0 always: heat can never decrease here
                }
            }

            // 1. Cold phase, authored order. Gated by heat < threshold; rate-limited; may release heat.
            //    Because cold runs before hot, nothing has produced steam yet, so the "condense from
            //    steam0" hazard fix falls out of the phase ordering rather than being a special case.
            foreach (var t in rules.Transitions)
            {
                if (t.regime != ThermalRegime.Cold) continue;
                if (c.Heat >= t.threshold) continue;

                // Clamp the source first: a negative source would give conv < 0, draining the
                // destination and inverting the heat release. (Matches the kernel.)
                float src = Mathf.Max(0f, c.Inks[t.fromField]);
                float conv = Mathf.Min(src, src * t.rate * dt);
                c.Inks[t.fromField] = src - conv;
                c.Inks[t.toField] += conv;
                c.Heat = Mathf.Min(maxHeat, c.Heat + conv * t.heatRelease);
            }

            // 2. Hot phase, authored order. Each transition re-derives its excess against its OWN
            //    threshold from the RUNNING heat, after prior drawdown, and is capped by excess/heatCost.
            foreach (var t in rules.Transitions)
            {
                if (t.regime != ThermalRegime.Hot) continue;

                // Clamp the source first: a negative source would give conv < 0, which drains the
                // destination AND inverts the heat budget (heat -= conv*cost would ADD heat, minting
                // energy from an underflow). (Matches the kernel.)
                float excess = Mathf.Max(0f, c.Heat - t.threshold);
                float src = Mathf.Max(0f, c.Inks[t.fromField]);
                float conv = Mathf.Min(src, Mathf.Min(src * t.rate * dt, excess / Mathf.Max(t.heatCost, EPS)));
                c.Inks[t.fromField] = src - conv;
                c.Inks[t.toField] += conv;
                c.Heat -= conv * t.heatCost;
            }

            // 3. Clamp.
            // CP8a: clamp to the absolute MIN temperature, not the neutral/room temperature — using
            // neutral as the floor would make room temperature the coldest attainable state.
            c.Heat = Mathf.Clamp(c.Heat, minTemp, maxHeat);
            for (int i = 0; i < c.Inks.Length; i++)
                c.Inks[i] = Mathf.Max(0f, c.Inks[i]);
        }
    }
}
