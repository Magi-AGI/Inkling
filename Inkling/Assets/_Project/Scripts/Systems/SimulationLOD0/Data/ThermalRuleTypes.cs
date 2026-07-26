using System;
using UnityEngine;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Which side of the thermal ladder a transition fires on.
    /// Cold: gated by `heat &lt; threshold`, rate-limited, may RELEASE heat (`heatRelease`) and/or
    ///       REMOVE heat as the destination forms (`heatCost`, CP8g). Not heat-budget capped.
    /// Hot:  gated by excess `heat - threshold`, additionally capped by `excess / heatCost` (heat budget).
    /// </summary>
    public enum ThermalRegime
    {
        Cold = 0,
        Hot = 1,
    }

    /// <summary>
    /// A LOCAL, heat-gated directed transition: one source ink converts into one destination ink
    /// within the SAME cell, driven by that cell's own heat. This is NOT a pairwise adjacency
    /// product (see AffinityGroup.productMatrix for those) — there is no neighbour sampling, so a
    /// transition can never mint mass: every conversion is a paired `from-- / to++`.
    ///
    /// ORDER IS LOAD-BEARING. Transitions execute: emission -> snapshot -> all Cold (in authored
    /// order) -> all Hot (in authored order) -> clamp. That ordering is what makes condensation run
    /// before boiling can produce steam (the ckpt-024 same-pass hazard fix) and lets cold cascade
    /// steam -> water -> ice in one dispatch.
    /// </summary>
    [Serializable]
    public class ThermalTransition
    {
        [Tooltip("Source slot index into the owning AffinityGroup.inks[] (0..3). Consumed by this transition.")]
        [Range(0, 3)] public int fromSlot;

        [Tooltip("Destination slot index into the owning AffinityGroup.inks[] (0..3). Produced by this " +
                 "transition.\n\n" +
                 "CP8k: set to -1 for a SINK — the source ink is REMOVED outright rather than converted " +
                 "into anything. This is how 'cold fire simply goes out' is expressed: a dying flame " +
                 "should not mint smoke or a puddle. A sink only ever destroys ink, never creates it, so " +
                 "it cannot mint mass.")]
        [Range(-1, 3)] public int toSlot;

        [Tooltip("Cold = fires when heat < threshold. Hot = fires on excess heat above threshold.")]
        public ThermalRegime regime = ThermalRegime.Hot;

        [Tooltip("Heat threshold. Cold fires below it; Hot fires above it. " +
                 "Ladder invariant: every cold threshold must be <= every hot threshold.")]
        [Min(0f)] public float threshold;

        [Tooltip("Fraction of the source ink converted per second.")]
        [Min(0f)] public float rate = 1f;

        [Tooltip("Heat consumed per unit converted. HOT: this is the heat budget — it also CAPS the " +
                 "conversion to excess/heatCost. COLD: a ONE-SHOT cooling event applied as the " +
                 "destination material FORMS (water->ice chills the cell as ice appears); it does NOT " +
                 "cap the conversion. Because it scales with how much actually converted, a cell that " +
                 "converts nothing is cooled by nothing — so this makes ice a cold source at formation, " +
                 "NOT a continuous emitter. 0 means no heat is consumed.")]
        [Min(0f)] public float heatCost;

        [Tooltip("COLD only: latent heat released per unit converted (clamped to maxHeat). " +
                 "Keep 0 to avoid cold->hot feedback.")]
        [Min(0f)] public float heatRelease;
    }

    /// <summary>
    /// A LOCAL heat source: an ink emits heat into its own cell, optionally burning itself as fuel.
    /// Energy is never minted — heat is added only up to the field's remaining headroom AND only as
    /// much as the local fuel supports, and fuel is burned in proportion to the heat ACTUALLY added.
    /// `fuelCost == 0` means add-only emission (the ink is not consumed).
    /// </summary>
    [Serializable]
    public class ThermalSource
    {
        [Tooltip("Slot index into the owning AffinityGroup.inks[] (0..3) that emits heat.")]
        [Range(0, 3)] public int slot;

        [Tooltip("Raw heat emitted per unit of this ink per second (before headroom/fuel capping).")]
        [Min(0f)] public float heatEmissionRate = 1f;

        [Tooltip("Ink burned per unit of heat ACTUALLY added. 0 = add-only (ink is not consumed).")]
        [Min(0f)] public float fuelCost;
    }
}
