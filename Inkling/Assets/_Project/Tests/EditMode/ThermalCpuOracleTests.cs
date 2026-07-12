using NUnit.Framework;
using Magi.Inkling.Systems.SimulationLOD0;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Tests.EditMode
{
    /// <summary>
    /// CP7d slice 1a: the CPU oracle must reproduce the CP5/CP7b/CP7c GPU behavior exactly, using the
    /// DEFAULT baked rule set. These values are the same ones asserted by the GPU tests in
    /// ThermalInteractionsTests, so the oracle is a trustworthy spec for the slice-2 kernel rewrite.
    /// </summary>
    public class ThermalCpuOracleTests
    {
        private const float Tol = 1e-4f;

        private static ThermalRuleSet Defaults() =>
            ThermalRuleBaker.Bake(null, ThermalDefaults.Cp7Defaults);

        private static ThermalCpuOracle.Cell Cell(float fire = 0, float water = 0, float steam = 0, float ice = 0, float heat = 0)
        {
            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.Fire] = fire;
            c[InkTypeId.Water] = water;
            c[InkTypeId.Steam] = steam;
            c[InkTypeId.Ice] = ice;
            c.Heat = heat;
            return c;
        }

        // Sources off unless a fuel test needs them (matches the GPU tests, where phase tests keep fire at 0).
        // CP8a: the 4th arg is the CLAMP FLOOR (minTemperature), NOT the neutral/room temperature.
        // Neutral is the relaxation target used by heat TRANSPORT; it must never be the clamp floor,
        // or nothing could ever get colder than room temperature and ice could never form.
        private static void Step(ThermalCpuOracle.Cell c, ThermalRuleSet r, float dt = 1f,
            float maxHeat = 1f, bool sources = false, float minTemp = 0f) =>
            ThermalCpuOracle.Apply(c, r, dt, minTemp, maxHeat, sources);

        // ── CP8a: neutral (room-temperature) baseline ───────────────────────────────────────
        // Shipped layout (Cp8Defaults): freeze .15 < melt .35 < NEUTRAL .5 < condense .65 < boil .85.
        // Cp7Defaults is kept as the legacy fixture for the kernel-mechanics tests above.
        private const float Neutral = 0.5f;

        private static ThermalRuleSet NeutralRules() =>
            ThermalRuleBaker.Bake(null, ThermalDefaults.Cp8Defaults);

        [Test]
        public void AtNeutral_WaterIsStable_NeitherFreezesNorBoils()
        {
            var c = Cell(water: 1f, heat: Neutral);
            Step(c, NeutralRules());

            Assert.That(c[InkTypeId.Water], Is.EqualTo(1f).Within(Tol),
                "Water is the stable phase at room temperature");
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0f).Within(Tol), "No freeze at neutral (needs < .15)");
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(0f).Within(Tol), "No boil at neutral (needs > .85)");
            Assert.That(c.Heat, Is.EqualTo(Neutral).Within(Tol), "No phase change => no heat drawn");
        }

        [Test]
        public void AtNeutral_IceMelts()
        {
            var c = Cell(ice: 1f, heat: Neutral);   // .5 > melt .35
            Step(c, NeutralRules());

            Assert.That(c[InkTypeId.Water], Is.GreaterThan(0f), "Ice melts at room temperature");
            Assert.That(c[InkTypeId.Ice], Is.LessThan(1f), "Ice consumed");
            Assert.That(c.Heat, Is.LessThan(Neutral), "Melting draws heat (latent)");
        }

        [Test]
        public void AtNeutral_SteamCondenses()
        {
            var c = Cell(steam: 1f, heat: Neutral);  // .5 < condense .65
            Step(c, NeutralRules());

            Assert.That(c[InkTypeId.Water], Is.GreaterThan(0f), "Steam condenses at room temperature");
            Assert.That(c[InkTypeId.Steam], Is.LessThan(1f), "Steam consumed");
        }

        [Test]
        public void NeutralIsNotTheClampFloor_TemperatureCanFallBelowNeutral()
        {
            // THE critical CP8a guard. If neutral (.5) were used as the clamp floor, a cell could never
            // be colder than room temperature and ice could NEVER form. Start at .1 (below neutral,
            // above minTemp 0) and assert it stays there rather than being clamped up to neutral.
            var c = Cell(water: 0f, heat: 0.1f);
            Step(c, NeutralRules(), minTemp: 0f);

            Assert.That(c.Heat, Is.EqualTo(0.1f).Within(Tol),
                "A sub-neutral temperature must NOT be clamped up to neutral");
        }

        [Test]
        public void BelowMinTemperature_ClampsUpToMin_NotToNeutral()
        {
            var c = Cell(heat: -0.2f);
            Step(c, NeutralRules(), minTemp: 0f);

            Assert.That(c.Heat, Is.EqualTo(0f).Within(Tol), "Clamps to minTemperature (0), not neutral (.5)");
        }

        [Test]
        public void ColdCell_FreezesWater_ProvingSubNeutralIsReachable()
        {
            // The end-to-end consequence of the clamp split: a genuinely cold cell still freezes.
            var c = Cell(water: 1f, heat: 0.1f);   // < freeze .15
            Step(c, NeutralRules(), minTemp: 0f);

            Assert.That(c[InkTypeId.Ice], Is.GreaterThan(0f), "Cold water still freezes under the neutral baseline");
        }

        // ── Phase-change parity (mirrors ThermalInteractionsTests GPU expectations) ──────────

        [Test]
        public void Melt_IceAt0_6_Yields_Ice0_6_Water0_4_Heat0_4()
        {
            var c = Cell(ice: 1f, heat: 0.6f);
            Step(c, Defaults());
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0.6f).Within(Tol));
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0.4f).Within(Tol));
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(0f).Within(Tol));
            Assert.That(c.Heat, Is.EqualTo(0.4f).Within(Tol));
        }

        [Test]
        public void Boil_WaterAt1_0_Yields_Water0_4_Steam0_6_Heat0_7()
        {
            var c = Cell(water: 1f, heat: 1f);
            Step(c, Defaults());
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0.4f).Within(Tol));
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(0.6f).Within(Tol));
            Assert.That(c.Heat, Is.EqualTo(0.7f).Within(Tol));
        }

        [Test]
        public void Condense_ColdSteam_BecomesWater_WhenFreezeDisabled()
        {
            // Isolate condensation: with default freezeRate the condensed water would freeze in the
            // same pass (cold cascade). Zero the freeze rate to observe condensation alone.
            var d = ThermalDefaults.Cp7Defaults;
            d.freezeRate = 0f;
            var c = Cell(steam: 1f, heat: 0.1f);
            Step(c, ThermalRuleBaker.Bake(null, d));

            Assert.That(c[InkTypeId.Steam], Is.EqualTo(0f).Within(Tol));
            Assert.That(c[InkTypeId.Water], Is.EqualTo(1f).Within(Tol));
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0f).Within(Tol));
        }

        [Test]
        public void Condense_WarmSteam_DoesNotCondense()
        {
            var c = Cell(steam: 1f, heat: 0.5f);
            Step(c, Defaults());
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(1f).Within(Tol));
        }

        [Test]
        public void Freeze_WaterAt0_1_BecomesIce()
        {
            var c = Cell(water: 1f, heat: 0.1f);
            Step(c, Defaults());
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(1f).Within(Tol));
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0f).Within(Tol));
            Assert.That(c.Heat, Is.EqualTo(0.1f).Within(Tol), "Freezing neither consumes nor releases heat");
        }

        [Test]
        public void ColdCascade_Steam_To_Water_To_Ice_InOnePass()
        {
            var c = Cell(steam: 1f, heat: 0.1f);
            Step(c, Defaults());
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(0f).Within(Tol));
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0f).Within(Tol));
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(1f).Within(Tol));
        }

        [Test]
        public void HotCascade_Ice_To_Water_To_Steam_InOnePass()
        {
            var c = Cell(ice: 1f, heat: 3f);
            Step(c, Defaults(), maxHeat: 3f);
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0f).Within(Tol));
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0f).Within(Tol));
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(1f).Within(Tol));
            Assert.That(c.Heat, Is.EqualTo(2f).Within(Tol));
        }

        [Test]
        public void BoiledSteam_DoesNotSamePassCondenseOrFreeze()
        {
            // Uses a VALID ladder (cold 0.3 <= hot 0.4/0.5). Boil converts all the water to steam and
            // draws heat down to the boil threshold; the newly-made steam must survive the pass.
            var d = ThermalDefaults.Cp7Defaults;
            d.condenseThreshold = 0.3f; d.freezeThreshold = 0.3f;
            d.meltThreshold = 0.4f; d.boilThreshold = 0.5f;
            var c = Cell(water: 1f, heat: 1f);
            var rules = ThermalRuleBaker.Bake(null, d);
            Assume.That(rules.IsValid, "ladder must be valid for this scenario");

            Step(c, rules);

            Assert.That(c[InkTypeId.Steam], Is.EqualTo(1f).Within(Tol), "Newly boiled steam must remain steam");
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0f).Within(Tol));
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0f).Within(Tol), "…and must not condense-then-freeze either");
            Assert.That(c.Heat, Is.EqualTo(0.5f).Within(Tol), "Boil draws heat down to (not below) its threshold");
        }

        /// <summary>
        /// STRUCTURAL INVARIANT (discovered while writing the oracle): a hot transition can never drive
        /// heat below its OWN threshold, because the heat-budget cap limits conversion to
        /// `excess / heatCost`, so `heat_after = heat - conv*heatCost >= heat - (heat - threshold) = threshold`.
        ///
        /// CP8a: combine that with the baker's PER-INVERSE-CYCLE rule — for a hot B->A, its inverse cold
        /// A->B satisfies `cold.threshold <= hot.threshold` — and it follows that after a hot transition
        /// fires, heat is still >= the threshold of the cold transition that would undo it. So the
        /// "boiled steam immediately re-condenses" hazard is IMPOSSIBLE for the cycle that matters; the
        /// cold-before-hot phase ordering is belt-and-braces, not the sole defence.
        ///
        /// (The guarantee is now stated per-cycle rather than globally, which is the correct scope: only
        /// a transition's own inverse can undo it. Non-inverse cold transitions may sit above a hot
        /// threshold — that is exactly the room-temperature layout — and they cannot oscillate with it.)
        /// This test pins that guarantee.
        /// </summary>
        [Test]
        public void HotTransition_NeverDrivesHeatBelowItsOwnThreshold()
        {
            var d = ThermalDefaults.Cp7Defaults;

            // Sweep starting heats across and above the boil threshold, with a large rate and lots of fuel.
            foreach (float startHeat in new[] { 0.71f, 0.8f, 0.95f, 1f })
            {
                d.boilRate = 100f;
                var c = Cell(water: 10f, heat: startHeat);
                Step(c, ThermalRuleBaker.Bake(null, d), maxHeat: 1f);

                Assert.That(c.Heat, Is.GreaterThanOrEqualTo(d.boilThreshold - Tol),
                    $"Boil from heat {startHeat} drove heat below its own threshold");
                Assert.That(c.Heat, Is.GreaterThanOrEqualTo(d.condenseThreshold - Tol),
                    "…and therefore can never re-enter the condense band");
            }
        }

        [Test]
        public void PerThresholdBudget_MeltsButDoesNotBoil()
        {
            var c = Cell(ice: 1f, water: 1f, heat: 0.5f);
            Step(c, Defaults());
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(0f).Within(Tol), "Heat below boil threshold");
            Assert.That(c[InkTypeId.Ice], Is.LessThan(1f), "Ice melted");
        }

        // ── CP7b fuel parity ────────────────────────────────────────────────────────────────

        [Test]
        public void Fuel_ZeroCost_EmitsHeat_FireUnchanged()
        {
            var c = Cell(fire: 0.5f, heat: 0f);
            Step(c, Defaults(), sources: true);   // default fuelCost = 0
            Assert.That(c.Heat, Is.EqualTo(0.5f).Within(Tol));
            Assert.That(c[InkTypeId.Fire], Is.EqualTo(0.5f).Within(Tol));
        }

        [Test]
        public void Fuel_HeadroomLimited_BurnsOnlyForHeatAdded()
        {
            var d = ThermalDefaults.Cp7Defaults;
            d.fireHeatEmissionRate = 100f; d.fireHeatFuelCost = 2f;
            var c = Cell(fire: 1f, heat: 0.8f);
            Step(c, ThermalRuleBaker.Bake(null, d), maxHeat: 1f, sources: true);

            Assert.That(c.Heat, Is.EqualTo(1f).Within(Tol), "Fills the 0.2 headroom");
            Assert.That(c[InkTypeId.Fire], Is.EqualTo(0.6f).Within(Tol), "Only 0.2*2 = 0.4 fire burned");
        }

        [Test]
        public void Fuel_FuelLimited_CapsHeat_FireHitsZero()
        {
            var d = ThermalDefaults.Cp7Defaults;
            d.fireHeatEmissionRate = 100f; d.fireHeatFuelCost = 5f;
            var c = Cell(fire: 0.1f, heat: 0f);
            Step(c, ThermalRuleBaker.Bake(null, d), maxHeat: 3f, sources: true);

            Assert.That(c.Heat, Is.EqualTo(0.02f).Within(1e-3f), "Capped by fire/fuelCost = 0.02");
            Assert.That(c[InkTypeId.Fire], Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void Fuel_AtMaxHeat_NoHeatAdded_NoFireBurned()
        {
            var d = ThermalDefaults.Cp7Defaults;
            d.fireHeatEmissionRate = 10f; d.fireHeatFuelCost = 2f;
            var c = Cell(fire: 1f, heat: 1f);
            Step(c, ThermalRuleBaker.Bake(null, d), maxHeat: 1f, sources: true);

            Assert.That(c.Heat, Is.EqualTo(1f).Within(Tol));
            Assert.That(c[InkTypeId.Fire], Is.EqualTo(1f).Within(Tol), "Zero headroom => no fuel burned");
        }

        [Test]
        public void Fuel_NegativeFire_ClampsToZero_NoNegativeEmission()
        {
            var d = ThermalDefaults.Cp7Defaults;
            d.fireHeatEmissionRate = 100f; d.fireHeatFuelCost = 2f;
            var c = Cell(fire: -0.01f, heat: 0.25f);
            Step(c, ThermalRuleBaker.Bake(null, d), maxHeat: 1f, sources: true);

            Assert.That(c.Heat, Is.EqualTo(0.25f).Within(Tol), "Negative fire must not drain heat");
            Assert.That(c[InkTypeId.Fire], Is.EqualTo(0f).Within(Tol));
        }

        [Test]
        public void SourcesDisabled_NoEmission_NoBurn()
        {
            var d = ThermalDefaults.Cp7Defaults;
            d.fireHeatEmissionRate = 10f; d.fireHeatFuelCost = 2f;
            var c = Cell(fire: 1f, heat: 0.3f);
            Step(c, ThermalRuleBaker.Bake(null, d), sources: false);

            Assert.That(c.Heat, Is.EqualTo(0.3f).Within(Tol));
            Assert.That(c[InkTypeId.Fire], Is.EqualTo(1f).Within(Tol));
        }

        // ── Negative-source underflow (Codex blocker; the oracle is the normative spec) ──────
        // A source that has underflowed below 0 must not yield a NEGATIVE conversion, which would
        // drain the DESTINATION and — for hot transitions — invert the heat budget into heat CREATION.
        // Source magnitude is -0.1 so each pre-fix deviation (0.05..0.1) dwarfs the 1e-4 tolerance.

        private static ThermalRuleSet Custom(params BakedThermalTransition[] transitions)
        {
            var rs = new ThermalRuleSet();
            rs.Transitions.AddRange(transitions);
            return rs;
        }

        [Test]
        public void NegativeSource_ColdTransition_DoesNotDrainDestinationOrInvertHeat()
        {
            // PRE-FIX: conv = -0.1 => ice 0.4 -> 0.3, heat 0.1 -> 0.05.
            var c = Cell(water: -0.1f, ice: 0.4f, heat: 0.1f);
            Step(c, Custom(new BakedThermalTransition
            {
                fromField = (int)InkTypeId.Water, toField = (int)InkTypeId.Ice,
                regime = ThermalRegime.Cold, threshold = 0.2f, rate = 1f, heatRelease = 0.5f
            }));

            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0.4f).Within(Tol), "Destination not drained (pre-fix: 0.3)");
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0f).Within(Tol), "Underflowed source clamps to 0");
            Assert.That(c.Heat, Is.EqualTo(0.1f).Within(Tol), "Heat release not inverted (pre-fix: 0.05)");
        }

        [Test]
        public void NegativeSource_HotTransition_DoesNotDrainDestinationOrMintHeat()
        {
            // PRE-FIX: conv = -0.1 => water 0.3 -> 0.2, heat 0.6 -> 0.65 (energy created).
            var c = Cell(ice: -0.1f, water: 0.3f, heat: 0.6f);
            Step(c, Defaults());   // heat 0.6: above melt (0.4), below boil (0.7)

            Assert.That(c[InkTypeId.Water], Is.EqualTo(0.3f).Within(Tol), "Destination not drained (pre-fix: 0.2)");
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0f).Within(Tol), "Underflowed source clamps to 0");
            Assert.That(c.Heat, Is.EqualTo(0.6f).Within(Tol), "Heat NOT minted (pre-fix: 0.65)");
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(0f).Within(Tol), "Below boil threshold");
        }

        [Test]
        public void NegativeSource_CustomNonDefaultFields_DoesNotDrainOrMint()
        {
            // PRE-FIX: conv = -0.1 => plantGrown 0.5 -> 0.4, heat 1.0 -> 1.05.
            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.PlantSeeded] = -0.1f;
            c[InkTypeId.PlantGrown] = 0.5f;
            c.Heat = 1f;

            ThermalCpuOracle.Apply(c, Custom(new BakedThermalTransition
            {
                fromField = (int)InkTypeId.PlantSeeded, toField = (int)InkTypeId.PlantGrown,
                regime = ThermalRegime.Hot, threshold = 0.4f, rate = 1f, heatCost = 0.5f
            }), dt: 1f, minTemp: 0f, maxHeat: 2f, enableHeatSources: false);

            Assert.That(c[InkTypeId.PlantGrown], Is.EqualTo(0.5f).Within(Tol), "Destination not drained (pre-fix: 0.4)");
            Assert.That(c[InkTypeId.PlantSeeded], Is.EqualTo(0f).Within(Tol), "Underflowed source clamps to 0");
            Assert.That(c.Heat, Is.EqualTo(1f).Within(Tol), "Heat NOT minted (pre-fix: 1.05)");
        }

        [Test]
        public void InvalidRuleSet_IsInert_PassThrough()
        {
            var rules = new ThermalRuleSet();
            rules.Fail("test");
            var c = Cell(fire: 0.5f, water: 0.3f, ice: 0.2f, heat: 0.9f);
            ThermalCpuOracle.Apply(c, rules, 1f, 0f, 1f, true);

            Assert.That(c[InkTypeId.Fire], Is.EqualTo(0.5f).Within(Tol));
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0.3f).Within(Tol));
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0.2f).Within(Tol));
            Assert.That(c.Heat, Is.EqualTo(0.9f).Within(Tol));
        }
    }
}
