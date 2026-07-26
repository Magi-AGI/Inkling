using NUnit.Framework;
using UnityEngine;
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
        // Shipped layout (Cp8Defaults): freeze == melt == .15 < NEUTRAL .5 < condense .65 < boil .85.
        // CP8j collapsed freeze and melt onto one point; the old .15/.35 gap was the dead band.
        // Cp7Defaults is kept as the legacy fixture for the kernel-mechanics tests above.
        private const float Neutral = 0.5f;

        private static ThermalRuleSet NeutralRules() =>
            ThermalRuleBaker.Bake(null, ThermalDefaults.Cp8Defaults);

        // CP8o — AMBIENT THAW PROOF (Lake: "I still don't see ... ice melting due to ambient heat").
        //
        // The single-cell oracle has no conduction, so this composes the real loop by hand: a 1-D row of
        // cells, a cold ice blob (heat 0) in a NEUTRAL room (heat 0.5), and NO FIRE ANYWHERE. Each step
        // applies (a) AdvectHeat's ambient relaxation toward neutral, (b) DiffuseHeat conduction — solid
        // rate inside ice, fluid rate outside, exactly as the shader selects post-CP8o — then (c) the
        // oracle's phase pass per cell. The formulas mirror Heat.hlsl / ThermalInteractions.compute.
        //
        // It would FAIL if ambient/neutral heat could not thaw ice: the assertion is that the blob strictly
        // shrinks and eventually melts substantially, with zero fire involved. That is the "real proof, not
        // prose" the packet demanded — and it also guards the melt→water→refreeze loop Lake suspected, by
        // asserting ice only ever decreases.
        [Test]
        public void AmbientNeutralHeat_ThawsColdIce_WithoutAnyFire()
        {
            const int N = 40;
            const float dt = 1f / 60f, neutral = 0.5f, minT = 0f, maxT = 1f;
            const float diffFluid = 2f, diffSolid = 12f, iceThermalThreshold = 0.1f;
            const float thermalHalfLife = 60f;
            float ambientRetention = Mathf.Pow(Mathf.Pow(0.5f, 1f / thermalHalfLife), dt);

            var rules = NeutralRules();
            var cells = new ThermalCpuOracle.Cell[N];
            for (int i = 0; i < N; i++) cells[i] = Cell(heat: neutral);
            for (int i = 15; i < 25; i++) { cells[i][InkTypeId.Ice] = 0.3f; cells[i].Heat = minT; }  // cold blob

            float IceTotal() { float t = 0; foreach (var c in cells) t += c[InkTypeId.Ice]; return t; }
            float start = IceTotal(), prev = start;

            for (int step = 0; step < 900; step++)   // 15 s at 60 fps, NO fire injected at any point
            {
                // (a) ambient relaxation toward neutral (AdvectHeat, still fluid).
                for (int i = 0; i < N; i++)
                    cells[i].Heat = neutral + (cells[i].Heat - neutral) * ambientRetention;

                // (b) conduction: solid rate where ice clears its OWN threshold (the CP8o decoupling).
                var nh = new float[N];
                for (int i = 0; i < N; i++)
                {
                    float l = cells[Mathf.Max(i - 1, 0)].Heat, r = cells[Mathf.Min(i + 1, N - 1)].Heat;
                    float avg = (l + r) * 0.5f;
                    float rate = cells[i][InkTypeId.Ice] >= iceThermalThreshold
                        ? Mathf.Max(diffSolid, diffFluid) : diffFluid;
                    float blend = 1f - Mathf.Exp(-rate * dt);
                    nh[i] = Mathf.Clamp(cells[i].Heat + blend * (avg - cells[i].Heat), minT, maxT);
                }
                for (int i = 0; i < N; i++) cells[i].Heat = nh[i];

                // (c) phase pass per cell (sources OFF — nothing emits heat).
                float ice = 0f;
                for (int i = 0; i < N; i++)
                {
                    ThermalCpuOracle.Apply(cells[i], rules, dt, minT, maxT, false);
                    ice += cells[i][InkTypeId.Ice];
                }

                Assert.That(ice, Is.LessThanOrEqualTo(prev + 1e-5f),
                    $"step {step}: ice must never GROW with no fire present — that would be the " +
                    "melt->water->refreeze runaway Lake suspected. It does not exist: melt caps heat at " +
                    "the threshold, never below, so fresh water cannot re-enter the freeze band.");
                prev = ice;
            }

            Assert.That(IceTotal(), Is.LessThan(start * 0.5f),
                "Ambient neutral heat ALONE must visibly thaw cold ice — at least half gone in 15s, no " +
                "fire. If this fails, the model genuinely cannot thaw ice from room temperature and the " +
                "fix is a design change (passive thaw), NOT another knob.");
        }

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

        // ── CP8l: the fire/plant deadlock ───────────────────────────────────────────────────
        // Lake: "plant is hardly reactive with fire, smoldering only and often not catching on fire at
        // all." The cause was an ORDERING defect between two CP8k/CP8e thresholds, not a weak number.
        //
        // A plant cell adjacent to one max-heat fire cell converges to its neighbour average, which is
        // (1.0 + 0.5*3)/4 = 0.625. That single fact broke fire twice over:
        //
        //   * plantIgnitionThreshold was 0.98 — UNREACHABLE by conduction. Thermal ignition was dead.
        //   * fireSinkThreshold was 0.85 — ABOVE 0.625, so fire spreading into a plant cell was
        //     EXTINGUISHED BY THE SINK before it could establish and heat its own cell. CP8k's cold-fire
        //     sink was strangling the fire it was meant to only cull when adrift in the cold.
        //
        // These assertions pin the ordering, not the specific numbers, so a future retune cannot silently
        // recreate the deadlock.
        private const float PlantCellNextToFire = 0.625f;   // (1.0 + 0.5*3) / 4

        [Test]
        public void FireSinkThreshold_IsBelowWhatAFireAdjacentCellReaches_OrFireStranglesItself()
        {
            var ctx = new SimulationContext();

            Assert.That(ctx.FireSinkThreshold, Is.LessThan(PlantCellNextToFire),
                "The cold-fire sink must sit BELOW the temperature a fire-adjacent cell settles at " +
                $"({PlantCellNextToFire}), or fire spreading into that cell is extinguished before it can " +
                "establish — fire smoulders and never catches");

            Assert.That(ctx.FireSinkThreshold, Is.GreaterThan(ctx.NeutralTemperature),
                "…but still ABOVE room temperature, or fire adrift in cold/neutral fluid would never go " +
                "out and CP8k's whole point is lost");
        }

        [Test]
        public void PlantIgnitionThreshold_IsReachableByConduction_ButWellAboveAmbient()
        {
            var ctx = new SimulationContext();

            Assert.That(ctx.PlantIgnitionThreshold, Is.LessThanOrEqualTo(0.75f + Tol),
                "Heat-only plant ignition must be REACHABLE. At 0.98 it never fired: a plant cell beside " +
                $"a max-heat fire converges to {PlantCellNextToFire} and simply cannot get there.");

            Assert.That(ctx.PlantIgnitionThreshold, Is.GreaterThan(ctx.NeutralTemperature + 0.2f),
                "…but far enough above room temperature that plant never spontaneously combusts in " +
                "ordinary ambient heat, which is what CP8e was protecting");
        }

        // ── CP8k: cold fire GOES OUT, and the heat ratchet is broken ────────────────────────
        // Lake: "Fire should probably dissipate rapidly if the temperature drops under 85 or so."
        // Implemented as a SINK transition (Fire -> removed), not a conversion: a guttering flame must
        // not mint smoke or a puddle.

        [Test]
        public void ColdFire_IsRemovedEntirely_NotConvertedIntoAnything()
        {
            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.Fire] = 1f;
            c.Heat = 0.5f;                          // room temperature: below the 0.6 sink threshold
            Step(c, NeutralRules(), maxHeat: 1f);   // sources OFF, so fire cannot reheat its own cell

            Assert.That(c[InkTypeId.Fire], Is.LessThan(0.05f),
                "Fire below the sink threshold must go out RAPIDLY (rate 4/s)");

            // THE POINT OF A SINK: the fire is gone, and it became NOTHING. Lake explicitly ruled out
            // smoke/steam for general cold-fire decay. If this ever converted instead of removing, a
            // cooling world would silently fill up with steam or water.
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(0f).Within(Tol), "Dying fire must NOT mint steam/smoke");
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0f).Within(Tol), "…nor water");
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0f).Within(Tol), "…nor anything else");

            // Heat-neutral by design: fire going out must not itself chill the cell, or the sink would
            // just be the heat ratchet again under a new name.
            Assert.That(c.Heat, Is.EqualTo(0.5f).Within(Tol),
                "Extinguishing must not consume or release heat");
        }

        [Test]
        public void HotFire_IsNotExtinguished_BySink()
        {
            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.Fire] = 1f;
            c.Heat = 0.9f;   // above the 0.6 sink threshold: this flame is healthy
            Step(c, NeutralRules(), maxHeat: 1f);

            Assert.That(c[InkTypeId.Fire], Is.EqualTo(1f).Within(Tol),
                "Fire in a genuinely hot cell must survive — fire heats its own cell, so a burning " +
                "flame holds itself above the threshold. The sink culls fire that DRIFTED somewhere cold.");
        }

        // The CP8k root-cause regression. Every thermal transition removes heat and none returns it, so
        // a water -> ice -> water round trip used to destroy 1.0 + 0.5 = 1.5 units of heat and put the
        // matter back exactly where it started — a perpetual refrigerator that dragged the whole field
        // to frozen.
        //
        // The invariant that actually matters is the ROUND-TRIP COST, not the relative size of the two
        // legs. (An earlier version of this test asserted `freeze < melt`; CP8l cut meltHeatCost to 0.15,
        // which makes that false — and it was never the real safety property anyway. Freeze and melt are
        // BOTH sinks; which of the two is larger says nothing about whether the loop runs away. What
        // matters is that a full cycle costs little enough for the thermostat to make it back.)
        [Test]
        public void FreezeThawCycle_DoesNotDestroyRunawayHeat()
        {
            var rs = NeutralRules();
            var freeze = rs.Transitions.Find(t =>
                t.fromField == (int)InkTypeId.Water && t.toField == (int)InkTypeId.Ice);
            var melt = rs.Transitions.Find(t =>
                t.fromField == (int)InkTypeId.Ice && t.toField == (int)InkTypeId.Water);

            // Both legs are real latent heat and must stay real — zeroing them would be a different bug.
            Assert.That(freeze.heatCost, Is.GreaterThan(0f), "Forming ice still chills its cell (CP8g)");
            Assert.That(melt.heatCost, Is.GreaterThan(0f), "Melting still draws latent heat");

            // THE RATCHET GUARD. A water -> ice -> water round trip returns the matter to its starting
            // state, so whatever heat it consumed is destroyed outright — no transition anywhere returns
            // heat. At 1.5 per cycle that outran the thermostat and the world could only freeze. Cap the
            // cycle well below that; the exact split between the two legs is a tuning choice.
            float cycleCost = freeze.heatCost + melt.heatCost;
            Assert.That(cycleCost, Is.LessThan(0.5f),
                $"A freeze/thaw round trip costs {cycleCost} heat and returns the matter to where it " +
                "started — that heat is destroyed outright, since no transition ever returns any. It used " +
                "to be 1.5, which outran the thermostat and dragged the entire field to frozen.");

            // And the specific CP8l consequence Lake asked for: cheap melting means heat already
            // delivered keeps eating ice, instead of every unit of ice demanding a fresh half-unit of heat.
            Assert.That(melt.heatCost, Is.LessThanOrEqualTo(0.15f + Tol),
                "Melting must be CHEAP in heat, or ice needs a constant fire stream to make any progress");
        }

        // ── CP8g: ice is a cold source WHEN IT FORMS ────────────────────────────────────────
        // Lake: "Ice should be a cold source, but only when it forms, whether by painting or growing."
        // Painted ice cools via the CP8b injection stamp; GROWN ice cools via the freeze transition's
        // heatCost. The cooling scales with the amount CONVERTED, which is what makes it a formation
        // event rather than a standing emitter.

        [Test]
        public void Cp8DefaultFreeze_ChillsWhenIceForms()
        {
            var c = Cell(water: 1f, heat: 0.1f);   // below freeze (.15)
            Step(c, NeutralRules(), minTemp: 0f);

            // CP8u slowed freeze (Lake: "make ice formation a bit slower"): freezeRate 1 -> 0.4, freezeHeatCost 0.2 -> 0.1.
            // A COLD transition converts min(src, src*rate*dt), so one step freezes 0.4 water and removes 0.4*0.1 = 0.04
            // heat: 0.1 -> 0.06 (earlier this assumed a full unit froze and expected 0.0). The chill guarantee is unchanged.
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0.4f).Within(1e-2f), "Cold water freezes into ice at freezeRate 0.4");
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0.6f).Within(1e-2f), "…consuming that much water");
            Assert.That(c.Heat, Is.EqualTo(0.06f).Within(1e-2f),
                "Forming ice CHILLS its cell: 0.1 - (0.4 frozen * 0.1 cost) = 0.06");
            Assert.That(c.Heat, Is.LessThan(0.1f),
                "…and be genuinely colder than it started, not merely unchanged");
        }

        // THE CORE CP8g GUARANTEE, now stated at a temperature that is genuinely COLD. Ice that already
        // exists, with no water left to freeze, must not keep pulling its own temperature down —
        // otherwise ice fields would run away to the floor forever. No conversion => no cooling. Ice is
        // deliberately NOT a standing cold emitter the way Fire is a standing heat source (Fire is a
        // ThermalSource; Ice is not, and must not become one).
        //
        // CP8j NOTE: this test used to sit at heat 0.2 and assert ice persists. Under the old layout
        // (melt 0.35) that passed — but 0.2 is ABOVE the freezing point (0.15), so it was pinning
        // exactly the bug Lake reported: ice hanging around at a temperature that is not cold. The
        // guarantee is real, so it is kept; it just has to be asserted BELOW freezing to mean anything.
        [Test]
        public void ExistingIce_BelowFreezing_DoesNotContinuouslyCool_WhenNoNewIceForms()
        {
            var c = Cell(ice: 1f, heat: 0.1f);   // genuinely cold: below freeze (.15), above the floor
            Step(c, NeutralRules(), minTemp: 0f);

            Assert.That(c[InkTypeId.Ice], Is.EqualTo(1f).Within(Tol),
                "Ice below the freezing point persists — it is cold, so it has every right to be there");
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0f).Within(Tol), "…and none of it melts");
            Assert.That(c.Heat, Is.EqualTo(0.1f).Within(Tol),
                "Settled ice must NOT keep cooling — the chill happens at FORMATION only, so with no " +
                "water left to freeze there is no conversion and therefore no heat drawn");
        }

        // CP8j. Lake: "we can't have it be a radically different temperature than it appears. For
        // example, ice above the freezing point should simply melt."
        //
        // The counterpart to the test above: the moment ice is warmer than freezing, it must go. Melting
        // pays for itself out of the local heat (latent), pulling the cell back DOWN toward the freezing
        // point — which is also why this cannot become the "forever feedback loop of expanding cold"
        // Lake warned about. The melt is capped by excess/heatCost, so it consumes exactly the heat that
        // was above freezing and then stalls AT freezing. It cannot chill past that point, and it stops
        // entirely once the ice is gone.
        [Test]
        public void ExistingIce_AboveFreezing_MeltsAndPaysHeatTowardFreezePoint()
        {
            var c = Cell(ice: 1f, heat: 0.2f);   // above freeze (.15) — this must NOT survive
            Step(c, NeutralRules(), minTemp: 0f);

            // excess = 0.2 - 0.15 = 0.05; CP8ad meltHeatCost 0.10 => conv capped at 0.05/0.10 = 0.5.
            // The cost has fallen 0.5 (Cp7) -> 0.15 (CP8l) -> 0.10 (CP8ad), so the SAME deposited heat now
            // melts 5x more ice than the legacy 0.5 cost — that is the point of CP8ad: ice keeps melting
            // from heat already delivered instead of needing a constant fire stream, so obstacle walls are
            // less stubborn. Note the heat still lands on exactly 0.15 regardless of cost; only the amount
            // of ice bought with it changed.
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0.5f).Within(1e-2f),
                "Ice above the freezing point must MELT — bounded by the heat available above freezing");
            Assert.That(c[InkTypeId.Water], Is.EqualTo(0.5f).Within(1e-2f), "…into exactly that much water");
            Assert.That(c[InkTypeId.Ice] + c[InkTypeId.Water], Is.EqualTo(1f).Within(Tol), "ice + water conserved");

            Assert.That(c.Heat, Is.EqualTo(0.15f).Within(Tol),
                "Melting pays latent heat, drawing the cell back down to EXACTLY the freezing point — " +
                "not below it. Ice cannot chill its surroundings past freezing, so no runaway cold loop.");
            Assert.That(c.Heat, Is.GreaterThanOrEqualTo(0.15f - Tol),
                "…and it must never undershoot into the freeze band, or the fresh water would re-freeze " +
                "and the cell would churn ice<->water forever");
        }

        // ── CP8d/CP8e/CP8l: heat-driven plant ignition ──────────────────────────────────────
        // SPONTANEOUS combustion from ambient heat alone. It is NOT the normal way fire spreads — fire
        // catching ADJACENT vegetation is the legacy Fire x Plant CONTACT reaction in OrganicGroup, which
        // this threshold does not gate.
        //
        // CP8e set this to 0.98 to keep it rare. CP8l lowered it to 0.75 because 0.98 turned out to be
        // UNREACHABLE, not merely rare: a plant cell beside a max-heat fire converges to its neighbour
        // average, (1.0 + 0.5*3)/4 = 0.625, and can never climb to 0.98. Thermal ignition was dead code.
        // 0.75 is reachable when plant is well-surrounded by fire, yet still far above ambient (0.5), so
        // the CP8e intent — no spontaneous combustion in ordinary heat — is preserved.
        private const float IgnitionThreshold = 0.75f;

        [Test]
        public void HotPlant_IgnitesToFire_AboveIgnitionThreshold()
        {
            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.PlantGrown] = 1f;
            c.Heat = 1f;   // above ignition (0.75)
            Step(c, NeutralRules(), maxHeat: 1f);

            Assert.That(c[InkTypeId.Fire], Is.GreaterThan(0f), "Hot plant must ignite into fire");
            Assert.That(c[InkTypeId.PlantGrown], Is.LessThan(1f), "…consuming the plant");
            Assert.That(c.Heat, Is.LessThan(1f), "…and consuming heat (endothermic pyrolysis)");
        }

        // The CP8e intent, restated at the CP8l threshold: plant must not combust in ORDINARY warmth.
        // 0.7 is comfortably above room temperature (0.5) and must still not ignite anything.
        [Test]
        public void WarmButNotFireHotPlant_DoesNotSpontaneouslyIgnite()
        {
            var rs = NeutralRules();
            var ignition = rs.Transitions.Find(t =>
                t.fromField == (int)InkTypeId.PlantGrown && t.toField == (int)InkTypeId.Fire);
            Assert.That(ignition.threshold, Is.EqualTo(IgnitionThreshold).Within(Tol),
                "Shipped heat-only ignition threshold");

            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.PlantGrown] = 1f;
            c.Heat = 0.7f;   // warm — well above ambient 0.5 — but below the 0.75 ignition threshold
            Step(c, rs, maxHeat: 1f);

            Assert.That(c[InkTypeId.Fire], Is.EqualTo(0f).Within(Tol),
                "Plant merely WARM must not spontaneously combust — it has to be genuinely fire-hot");
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
            // NOTE: heat must stay ABOVE the ignition threshold (0.75) or this test passes VACUOUSLY —
            // nothing ignites, so "bounded by the budget" would be trivially true and prove nothing.
            var c = new ThermalCpuOracle.Cell();
            c[InkTypeId.PlantGrown] = 1f;
            c.Heat = 0.8f;   // excess over 0.75 is 0.05; heatCost 0.25 => cap 0.2
            Step(c, NeutralRules(), maxHeat: 1f);

            Assert.That(c[InkTypeId.Fire], Is.GreaterThan(0f),
                "Guard against a vacuous pass: the cell IS above the ignition threshold, so it must burn");
            Assert.That(c[InkTypeId.Fire], Is.LessThanOrEqualTo(0.2f + Tol),
                "Ignition must be bounded by the heat budget (excess / heatCost)");
            Assert.That(c[InkTypeId.PlantGrown], Is.GreaterThan(0.5f), "Most of the plant survives one step");
        }

        [Test]
        public void Cp8dDefaults_KeepWaterIceSteamTransitions_AndAddIgnition()
        {
            var rs = NeutralRules();
            Assert.IsTrue(rs.IsValid, "CP8d defaults must bake cleanly: " + rs.Error);
            Assert.AreEqual(7, rs.Transitions.Count,
                "condense + freeze + melt + boil + 2 plant ignitions + CP8k cold-fire sink — the " +
                "steam/water/ice defaults must NOT be dropped");
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
            var c = Cell(ice: 1f, heat: Neutral);   // .5 > melt .15
            Step(c, NeutralRules());

            Assert.That(c[InkTypeId.Water], Is.GreaterThan(0f), "Ice melts at room temperature");
            Assert.That(c[InkTypeId.Ice], Is.LessThan(1f), "Ice consumed");
            Assert.That(c.Heat, Is.LessThan(Neutral), "Melting draws heat (latent)");
        }

        // CP8h: steam still condenses at room temperature — but GENTLY. Lake: "the steam should
        // dissipate into water at a lesser rate. Only a little bit of water should form from cooling
        // steam." The exact numbers matter: a `> 0` assertion would pass just as happily on the old
        // full-collapse behaviour, so it would not pin the thing we actually changed.
        [Test]
        public void AtNeutral_SteamCondenses_GentlyNotWholesale()
        {
            var c = Cell(steam: 1f, heat: Neutral);  // .5 < condense .65
            Step(c, NeutralRules());

            Assert.That(c[InkTypeId.Water], Is.EqualTo(0.15f).Within(Tol),
                "Only a LITTLE water forms per second (condenseRate 0.15), not a wholesale collapse");
            Assert.That(c[InkTypeId.Steam], Is.EqualTo(0.85f).Within(Tol),
                "…and most of the steam survives the step to keep drifting");
            Assert.That(c[InkTypeId.Water] + c[InkTypeId.Steam], Is.EqualTo(1f).Within(Tol),
                "steam + water conserved");
            Assert.That(c[InkTypeId.Ice], Is.EqualTo(0f).Within(Tol),
                "The condensed water must not then freeze — neutral (.5) is well above freeze (.15)");
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
