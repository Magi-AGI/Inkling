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

        // ── CP8d/CP8e: heat-driven plant ignition ───────────────────────────────────────────
        // SPONTANEOUS combustion from ambient heat alone. CP8e raised the threshold to 0.98 — just shy
        // of max heat (1.0) — so this is a rare, furnace-only event. It is NOT the normal way fire
        // spreads: fire catching adjacent vegetation is still the legacy Fire x Plant CONTACT reaction
        // in OrganicGroup, which this threshold does not gate.
        private const float IgnitionThreshold = 0.98f;

        [Test]
        public void HotPlant_IgnitesToFire_AboveIgnitionThreshold()
        {
            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.PlantGrown] = 1f;
            c.Heat = 1f;   // above ignition (0.98)
            Step(c, NeutralRules(), maxHeat: 1f);

            Assert.That(c[InkTypeId.Fire], Is.GreaterThan(0f), "Hot plant must ignite into fire");
            Assert.That(c[InkTypeId.PlantGrown], Is.LessThan(1f), "…consuming the plant");
            Assert.That(c.Heat, Is.LessThan(1f), "…and consuming heat (endothermic pyrolysis)");
        }

        // CP8e: the whole point of raising the threshold. 0.9 is "hot" — hotter than boiling water —
        // and used to ignite plant. It must no longer do so, or fire still spreads through vegetation
        // on ambient heat alone, which is exactly what Lake asked to stop.
        [Test]
        public void HotButNotFurnacePlant_DoesNotSpontaneouslyIgnite_AtCp8eThreshold()
        {
            var rs = NeutralRules();
            var ignition = rs.Transitions.Find(t =>
                t.fromField == (int)InkTypeId.PlantGrown && t.toField == (int)InkTypeId.Fire);
            Assert.That(ignition.threshold, Is.EqualTo(IgnitionThreshold).Within(Tol),
                "Shipped heat-only ignition threshold must be near max heat");

            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.PlantGrown] = 1f;
            c.Heat = 0.9f;   // hot — above boiling (0.85) — but below the 0.98 ignition threshold
            Step(c, rs, maxHeat: 1f);

            Assert.That(c[InkTypeId.Fire], Is.EqualTo(0f).Within(Tol),
                "Plant at 0.9 must NOT spontaneously combust — only a near-max cell may");
            Assert.That(c[InkTypeId.PlantGrown], Is.EqualTo(1f).Within(Tol), "Plant untouched");
        }

        [Test]
        public void WarmPlant_DoesNotIgnite_BelowIgnitionThreshold()
        {
            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.PlantSeeded] = 1f;
            c.Heat = Neutral;   // room temperature: nowhere near ignition
            Step(c, NeutralRules());

            Assert.That(c[InkTypeId.Fire], Is.EqualTo(0f).Within(Tol),
                "Plant at room temperature must NOT spontaneously ignite");
            Assert.That(c[InkTypeId.PlantSeeded], Is.EqualTo(1f).Within(Tol), "Plant untouched");
        }

        [Test]
        public void PlantIgnition_IsBoundedByHeatBudget()
        {
            // Only just above the threshold => very little excess heat => only a sliver may burn,
            // capped by excess/heatCost. A hot cell cannot flash all its plant to fire in one step.
            // NOTE: heat must stay ABOVE the CP8e threshold (0.98) or this test passes VACUOUSLY —
            // nothing ignites, so "bounded by the budget" would be trivially true and prove nothing.
            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.PlantGrown] = 1f;
            c.Heat = 0.99f;   // excess over 0.98 is 0.01; heatCost 0.25 => cap 0.04
            Step(c, NeutralRules(), maxHeat: 1f);

            Assert.That(c[InkTypeId.Fire], Is.GreaterThan(0f),
                "Guard against a vacuous pass: the cell IS above the ignition threshold, so it must burn");
            Assert.That(c[InkTypeId.Fire], Is.LessThanOrEqualTo(0.04f + Tol),
                "Ignition must be bounded by the heat budget (excess / heatCost)");
            Assert.That(c[InkTypeId.PlantGrown], Is.GreaterThan(0.9f), "Most of the plant survives one step");
        }

        [Test]
        public void Cp8dDefaults_KeepWaterIceSteamTransitions_AndAddIgnition()
        {
            var rs = NeutralRules();
            Assert.IsTrue(rs.IsValid, "CP8d defaults must bake cleanly: " + rs.Error);
            Assert.AreEqual(6, rs.Transitions.Count,
                "condense + freeze + melt + boil + 2 plant ignitions — the steam/water/ice defaults must NOT be dropped");
            Assert.LessOrEqual(rs.Transitions.Count, ThermalRuleBaker.MaxTransitions, "under the transition cap");
        }

        // Plant ignition is opt-in via ThermalDefaults.includePlantIgnition. The LEGACY Cp7Defaults
        // fixture must not pick it up — it is a frozen fixture the kernel-mechanics tests pin against,
        // and silently growing it from 4 to 6 transitions changed what those tests were measuring.
        [Test]
        public void Cp7Defaults_DoNotIncludePlantIgnition()
        {
            var legacy = ThermalRuleBaker.Bake(null, ThermalDefaults.Cp7Defaults);
            Assert.IsTrue(legacy.IsValid, legacy.Error);
            Assert.AreEqual(4, legacy.Transitions.Count,
                "Legacy CP7 fixture bakes exactly condense/freeze/melt/boil — no CP8d plant ignition");
            foreach (var t in legacy.Transitions)
                Assert.AreNotEqual((int)InkTypeId.Fire, t.toField,
                    "No transition in the legacy fixture may produce Fire");
        }

        // RUNTIME WIRING GUARD: FluidSolver builds its defaults from ctx, NOT from Cp8Defaults. If it
        // forgets to opt in, ignition silently never fires in play while every oracle test still passes.
        // (This is the CP7c bug class: correct kernel, dead runtime.)
        [Test]
        public void FluidSolver_OptsIntoPlantIgnition_WhenBuildingRuntimeDefaults()
        {
            const string path = "Assets/_Project/Scripts/Systems/SimulationLOD0/Core/FluidSolver.cs";
            string src = System.IO.File.ReadAllText(path);
            StringAssert.Contains("includePlantIgnition = true", src,
                "FluidSolver.BuildThermalDefaults must opt into plant ignition, or it never fires at runtime");
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
