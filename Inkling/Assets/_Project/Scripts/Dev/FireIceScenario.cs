using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEngine;
using Magi.Inkling.Systems.SimulationLOD0;
using Magi.InkTools.Simulation;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Magi.Inkling.Dev
{
    /// <summary>
    /// CP8p — FIRE-vs-ICE evaluation harness (Lake: "the reason this isn't coming through is that we
    /// don't have a good test").
    ///
    /// Drives a continuous directional Fire emitter into a controlled Ice formation under deterministic
    /// external stepping, and dumps PNGs plus RAW per-region metrics sampled from the particle buffer and
    /// the heat layer. The point is to answer, with numbers rather than vibes, WHICH link in the chain is
    /// failing:
    ///
    ///   fireReachesIce      — did fire mass actually arrive at the ice front? (if not: flow/obstacle problem)
    ///   heatEntersIce       — is heat rising INSIDE the ice region? (if not: conduction problem)
    ///   iceMelting          — is ice mass falling and water rising? (if not: melt/threshold problem)
    ///   heatDrainsFirst     — is heat leaving the front faster than it enters the ice? (dissipation problem)
    ///   meltRefreezes       — is ice mass going back UP after melting? (freeze-loop problem)
    ///   thinIceIsObstacle   — did the thin section wrongly become a velocity obstacle? (CP8o regression)
    ///
    /// CP8q adds the OBSTACLE-STRENGTH variant (`obstacleStrengthWall = true`, wall seeded at 1.0 instead
    /// of brush density 0.3). That is the case Lake actually reports — "when the Ice value is high enough
    /// to make an obstacle, the heat still doesn't advect into the Ice" — which the thin wall by design
    /// never triggers. It adds:
    ///
    ///   wallIsObstacle            — did the wall really become a velocity obstacle after the real rebuild?
    ///   obstacleWallNeverFormed   — obstacle run requested but the wall never went solid (setup failure)
    ///   obstacleIceMeltsUnderFire — THE CP8q HEADLINE: solid wall + heat got in + ice mass fell
    ///
    /// Also runs an AMBIENT-ONLY subscenario (identical ice, NO fire) so Lake's specific question — "over
    /// long enough timeframes, can ice melt from ambient heat, or does the heat drain before it can" — is
    /// answered directly instead of inferred from a screenshot.
    ///
    /// Run: Play mode, then menu "Inkling/Run Fire vs Ice Test", or set runFireIceRequested at runtime.
    /// Output: &lt;project&gt;/InkCaptures/fire_ice_&lt;tag&gt;/
    /// </summary>
    public partial class InkScenarioRunner
    {
        [Header("Fire vs Ice test (CP8p)")]
        [Tooltip("Set true at runtime to run the Fire-vs-Ice evaluation harness.")]
        public bool runFireIceRequested = false;
        [Tooltip("Label for this run: captures land in InkCaptures/fire_ice_<tag>/.")]
        public string fireIceTag = "p0";
        [Tooltip("Total steps for the fire scenario (600 @ dt 1/60 = 10 simulated seconds).")]
        public int fireIceSteps = 600;
        [Tooltip("Steps for the ambient-only (no fire) thaw scenario. Longer: ambient thaw is slow.")]
        public int ambientThawSteps = 1800;
        [Tooltip("Rightward push applied by the Fire emitter every step.")]
        public float fireIcePush = 0.25f;

        // CP8z: obstacle-heat model to force for this run. -1 = leave the scene/SimDriver value alone;
        // 0 = strict conduction-only; 1 = legacy CP8q advective. This is what makes the harness a
        // side-by-side A/B for the advection-vs-conduction decision — run the same scenario under each
        // model and diff the JSON (face heat, ice-interior heat, ice mass loss, water gain, punch-through).
        [Tooltip("Force obstacle-heat model for this run: -1 = scene default, 0 = strict conduction, 1 = legacy advective.")]
        public int fireIceHeatModeOverride = -1;

        // Formation geometry (UV). Ice wall sits ahead of the emitter; fire is pushed rightward into it.
        //
        // CP8p-fix (Codex): the lane is deliberately placed in the LOWER band (y 0.12..0.32), NOT through
        // the middle. Obstacles.hlsl:UpdateObstacles stamps a built-in circular geometry obstacle at the
        // sim CENTRE with radius 0.1 UV — i.e. spanning UV [0.4, 0.6] on both axes. The original lane
        // (emitter y=0.5, wall x 0.58..0.72, y 0.35..0.65) ran straight through it and the wall's left
        // edge overlapped it, so `fireReachesIce`, `thinIceIsObstacle` and the breakthrough metrics could
        // all have conflated GEOMETRY blockage with ICE behaviour — misdiagnosing the exact CP8o question
        // the harness exists to answer.
        //
        // (FluidSolver.Step does clear the obstacle RT before InkToObstacles repopulates it, so the circle
        // is wiped once stepping begins. But the BASELINE sample is taken before any step, and relying on
        // that clear is fragile reasoning to bake a diagnostic on. Moving the lane makes it unconditional.)
        //
        // Edge walls are 2 px thick (~0.002 UV at 1024), so y=0.12 keeps a wide margin from those too.
        private static readonly Vector2 EmitterUv = new Vector2(0.15f, 0.22f);
        private const float IceX0 = 0.55f, IceX1 = 0.70f, IceY0 = 0.12f, IceY1 = 0.32f;
        private const float FrontX0 = 0.40f, FrontX1 = 0.55f;   // gap between emitter and ice
        private const float BeyondX0 = 0.70f, BeyondX1 = 0.85f; // past the wall => breakthrough signal

        // Far-field control region, also clear of the centre circle and of the lane itself.
        private const float AmbX0 = 0.05f, AmbX1 = 0.20f, AmbY0 = 0.70f, AmbY1 = 0.90f;

        /// <summary>Per-region raw sample, read from the particle buffer + heat RT.</summary>
        private struct RegionStats
        {
            public float fire, ice, water, steam;
            public float heatAvg, heatMax, heatMin;
            public float obstacleCoverage;
            // CP8p-fix (Codex): regional flow, so impulse efficacy is a NUMBER, not just a velocity PNG.
            // velAvgX is signed — it is the rightward push actually reaching each region, which is what
            // distinguishes "fire never got moving" from "fire moved but the ice stopped it".
            public float velAvg, velMax, velAvgX;
        }

        private IEnumerator RunFireIceTest()
        {
            running = true;
            Application.runInBackground = true;

            var sim = FindFirstObjectByType<SimDriver>();
            if (sim == null) { Debug.LogError("[FireIce] No SimDriver in scene."); running = false; yield break; }

            int guard = 0;
            while (sim.GetDisplayTexture() == null && guard++ < 300) yield return null;
            if (sim.GetDisplayTexture() == null) { Debug.LogError("[FireIce] Sim never became ready."); running = false; yield break; }

            foreach (var inj in FindObjectsByType<TexturedInjector>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                inj.enabled = false;

            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "InkCaptures", "fire_ice_" + fireIceTag));
            Directory.CreateDirectory(root);
            Debug.Log("[FireIce] Output -> " + root);

            bool prevExternal = sim.ExternalStepControl;
            sim.ExternalStepControl = true;
            sim.SetDisplayVelocity(false);

            // CP8z: force the obstacle-heat model for this run, if requested, so the two models can be
            // compared under an otherwise identical scenario. Restored in the finally-equivalent cleanup.
            int prevHeatMode = sim.HeatObstacleMode;
            if (fireIceHeatModeOverride >= 0)
            {
                sim.HeatObstacleMode = fireIceHeatModeOverride;
                Debug.Log("[FireIce] HeatObstacleMode forced to " + sim.HeatObstacleMode
                    + (sim.HeatObstacleMode == 0 ? " (strict conduction-only)" : " (legacy advective)"));
            }

            const float dt = 1f / 60f;
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"tag\":\"").Append(fireIceTag).Append("\",");
            sb.Append("\"heatObstacleMode\":").Append(sim.HeatObstacleMode).Append(',');
            sb.Append("\"resolution\":").Append(sim.Resolution).Append(',');

            // ---------- Scenario A: continuous Fire emitter driven into the Ice wall ----------
            yield return SeedIceFormation(sim);
            var baseline = SampleAll(sim);
            SaveRT(sim.GetDisplayTexture(), Path.Combine(root, "A0_baseline_display.png"), false, out _, out _);
            SaveRT(sim.GetObstacleTexture(), Path.Combine(root, "A0_baseline_obstacle.png"), false, out _, out _);

            sb.Append("\"fireScenario\":{\"series\":[");
            var series = new System.Collections.Generic.List<RegionStats[]>();
            bool firstEntry = true;

            for (int step = 0; step < fireIceSteps; step++)
            {
                // Continuous emitter: density + rightward force EVERY step (mirrors the RMB emitter).
                sim.InjectDensity(EmitterUv, new Color(1f, 0f, 0f, 1f), (int)InkTypeId.Fire);
                sim.InjectForce(EmitterUv, new Vector2(fireIcePush, 0f));
                sim.StepSimulation(dt);

                if (step % 120 == 119 || step == fireIceSteps - 1)
                {
                    sim.RefreshDisplay();
                    yield return null;
                    var s = SampleAll(sim);
                    series.Add(s);
                    if (!firstEntry) sb.Append(',');
                    firstEntry = false;
                    sb.Append(SeriesEntry(step + 1, (step + 1) * dt, s));

                    string tag = "A" + (series.Count);
                    SaveRT(sim.GetDisplayTexture(), Path.Combine(root, tag + "_display.png"), false, out _, out _);
                    SaveRT(sim.GetVelocityTexture(), Path.Combine(root, tag + "_velocity.png"), true, out _, out _);
                    SaveRT(sim.GetObstacleTexture(), Path.Combine(root, tag + "_obstacle.png"), false, out _, out _);
                }
            }
            sb.Append("],");
            sb.Append(Diagnose(baseline, series, obstacleStrengthWall, ActiveWallConcentration)).Append("},");

            // ---------- Scenario B: ambient-only thaw (identical ice, NO fire at all) ----------
            sim.ResetSimulation();
            yield return SeedIceFormation(sim);
            var ambBase = SampleAll(sim);
            SaveRT(sim.GetDisplayTexture(), Path.Combine(root, "B0_ambient_baseline_display.png"), false, out _, out _);

            sb.Append("\"ambientScenario\":{\"series\":[");
            var ambSeries = new System.Collections.Generic.List<RegionStats[]>();
            firstEntry = true;
            for (int step = 0; step < ambientThawSteps; step++)
            {
                sim.StepSimulation(dt);   // NO fire, NO force — ambient/neutral relaxation only
                if (step % 300 == 299 || step == ambientThawSteps - 1)
                {
                    sim.RefreshDisplay();
                    yield return null;
                    var s = SampleAll(sim);
                    ambSeries.Add(s);
                    if (!firstEntry) sb.Append(',');
                    firstEntry = false;
                    sb.Append(SeriesEntry(step + 1, (step + 1) * dt, s));
                    SaveRT(sim.GetDisplayTexture(),
                        Path.Combine(root, "B" + ambSeries.Count + "_ambient_display.png"), false, out _, out _);
                }
            }
            sb.Append("],");
            sb.Append(AmbientDiagnose(ambBase, ambSeries)).Append('}');
            sb.Append('}');

            File.WriteAllText(Path.Combine(root, "fire_ice_metrics.json"), sb.ToString());
            Debug.Log("[FireIce] Metrics -> " + Path.Combine(root, "fire_ice_metrics.json"));

            sim.ExternalStepControl = prevExternal;
            if (fireIceHeatModeOverride >= 0) sim.HeatObstacleMode = prevHeatMode;
            running = false;
        }

        /// <summary>Ice concentration seeded into the wall. MUST stay below Ice.obstacleThreshold (0.5).</summary>
        private const float WallIceConcentration = 0.3f;

        /// <summary>
        /// CP8q: OBSTACLE-STRENGTH wall variant. Lake's reported bug is specifically about ice that is
        /// dense enough to become a velocity obstacle ("when the Ice value is high enough to make an
        /// obstacle, the heat still doesn't advect into the Ice"), which the CP8p thin wall (0.3, below
        /// Ice.obstacleThreshold 0.5) deliberately never triggers. 1.0 is unambiguously solid.
        ///
        /// Set true and re-run to reproduce the reported case; the metrics JSON records which mode ran.
        /// </summary>
        [Tooltip("CP8q: seed the wall at obstacle strength (1.0) instead of brush density (0.3), so the " +
                 "wall becomes a real velocity obstacle — the case Lake reports as never melting.")]
        public bool obstacleStrengthWall = false;

        /// <summary>Concentration actually seeded this run — 1.0 when exercising the obstacle case.</summary>
        private float ActiveWallConcentration => obstacleStrengthWall ? 1f : WallIceConcentration;

        /// <summary>
        /// Seeds the Ice wall as a CONTROLLED RAW INITIAL CONDITION — it does NOT paint through public
        /// injection.
        ///
        /// This was originally six overlapping passes of sim.InjectDensity(). That was wrong and it
        /// quietly invalidated the whole harness: injection is ADDITIVE (BatchedInjection.compute does
        /// `AddInkByIndex(p, idx, _DensityAmount * w)`), so six passes at densityAmount 0.3 stack to ~1.8
        /// — three-and-a-half times the 0.5 obstacle threshold. The "normal/thin" wall would have been a
        /// solid velocity obstacle from frame zero, so `thinIceIsObstacle` would have reported exactly the
        /// CP8o regression it exists to rule out, and every downstream number would describe the wrong
        /// experiment.
        ///
        /// Writing the buffers directly gives an exact, reproducible 0.3 — comfortably under 0.5, so the
        /// wall is thermally conductive (>= thermalSolidThresholdIce 0.1) but NOT a flow obstacle, which
        /// is precisely the CP8o case Lake wants evaluated.
        ///
        /// Both ping-pong sides are written for particles AND heat: the solver swaps after the first step,
        /// so seeding only the read side would silently lose the wall (or half the heat field) one frame in.
        /// </summary>
        private IEnumerator SeedIceFormation(SimDriver sim)
        {
            var ctx = GetContext(sim);
            int res = sim.Resolution;
            if (ctx?.ParticlesBuffer == null || res <= 0)
            {
                Debug.LogError("[FireIce] Cannot seed: no particle buffers.");
                yield break;
            }

            // CP8p-fix (Codex): seed from a ZERO array, never by reading and patching the live buffer.
            // Reading the live buffer inherited whatever was already in the sim — startup injections, the
            // Main scene's creature/TexturedInjector ink, leftover user painting — and only overwrote the
            // `ice` field inside the wall. Every other channel and every cell outside the wall carried
            // contamination straight into the metrics. A zero array makes the baseline exactly "empty room
            // + the wall we asked for", which is the only baseline the diagnostics can be trusted against.
            int count = res * res;
            var all = new iparticle[count];   // all fields default to 0 — a genuinely empty world

            int ix0 = Mathf.Clamp(Mathf.FloorToInt(IceX0 * res), 0, res - 1);
            int ix1 = Mathf.Clamp(Mathf.CeilToInt(IceX1 * res), 0, res - 1);
            int iy0 = Mathf.Clamp(Mathf.FloorToInt(IceY0 * res), 0, res - 1);
            int iy1 = Mathf.Clamp(Mathf.CeilToInt(IceY1 * res), 0, res - 1);

            // Heat: ice cells start COLD (min), everything else at NEUTRAL room temperature. Explicitly
            // initialising both makes the ambient-thaw subscenario meaningful — it measures a genuinely
            // cold wall warming toward a genuinely neutral room, not whatever the previous run left behind.
            var heat = new Color[count];
            for (int i = 0; i < count; i++) heat[i] = new Color(NeutralTemperature, 0f, 0f, 0f);

            for (int y = iy0; y <= iy1; y++)
            {
                for (int x = ix0; x <= ix1; x++)
                {
                    int i = y * res + x;
                    all[i].ice = ActiveWallConcentration;
                    heat[i] = new Color(MinTemperature, 0f, 0f, 0f);
                }
            }

            // Both particle sides, so a solver swap cannot drop the wall.
            for (int b = 0; b < ctx.ParticlesBuffer.Length; b++)
                if (ctx.ParticlesBuffer[b] != null) ctx.ParticlesBuffer[b].SetData(all);

            // Both heat sides, same reason.
            if (ctx.Heat != null)
            {
                var tex = new Texture2D(res, res, TextureFormat.RGBAFloat, false);
                try
                {
                    tex.SetPixels(heat);
                    tex.Apply();
                    var prev = RenderTexture.active;
                    if (ctx.Heat.Read != null) Graphics.Blit(tex, ctx.Heat.Read);
                    if (ctx.Heat.Write != null) Graphics.Blit(tex, ctx.Heat.Write);
                    RenderTexture.active = prev;   // Blit leaves `active` set; restore it
                }
                finally { Object.DestroyImmediate(tex); }
            }

            // CP8p-fix (Codex): zero VELOCITY on both ping-pong sides. Leftover flow from prior play would
            // push the fire around and make the directional-impulse metrics meaningless.
            ClearRT(ctx.Velocity?.Read);
            ClearRT(ctx.Velocity?.Write);
            ClearRT(ctx.Pressure?.Read);
            ClearRT(ctx.Pressure?.Write);
            ClearRT(ctx.Divergence);

            // CP8q (Codex CKPT-096): the PRE-BOUNDARY thermal velocity snapshot must be zeroed too.
            // Scenario A does not call ResetSimulation() before seeding, and AdvectHeat binds
            // VelocityThermal whenever it is non-null — so without this the very first fire-vs-ice step
            // would advect heat using stale pre-boundary velocity left over from startup or prior play.
            // That is precisely the "known clean state" the harness promises, and it was the one buffer
            // the clean-up missed: it is newer than the others and does not live on the ping-pong pairs.
            ClearRT(ctx.VelocityThermal);

            // Clear the obstacle RT so the baseline sample sees a KNOWN state. Step() rebuilds it from
            // ink each step anyway; clearing here means the pre-step baseline cannot report a stale
            // geometry circle (or stale ink obstacles) as if the seeded wall had produced them.
            ClearRT(ctx.Obstacles);

            sim.RefreshDisplay();
            yield return null;
        }

        private static void ClearRT(RenderTexture rt)
        {
            if (rt == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            RenderTexture.active = prev;
        }

        private const float NeutralTemperature = 0.5f;
        private const float MinTemperature = 0f;

        private static SimulationContext GetContext(SimDriver sim)
        {
            var f = typeof(SimDriver).GetField("ctx", BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(sim) as SimulationContext;
        }

        /// <summary>
        /// Samples ice/front/beyond/ambient regions from RAW particle + heat data.
        ///
        /// Reads the particle buffer and both RTs ONCE and shares them across all four regions. At 1024^2
        /// the particle buffer is ~56 MB per readback, so sampling each region independently would pull
        /// ~224 MB per capture and stall the editor — easily bad enough to look like a hang.
        /// </summary>
        private RegionStats[] SampleAll(SimDriver sim)
        {
            int res = sim.Resolution;
            var buf = sim.GetParticleBuffer();
            if (buf == null || res <= 0)
                return new[] { default(RegionStats), default, default, default };

            var all = new iparticle[res * res];
            buf.GetData(all);
            float[] heat = ReadHeat(sim, res);
            float[] obstacle = ReadObstacle(sim, res);
            Vector2[] vel = ReadRG(sim.GetVelocityTexture(), res);

            return new[]
            {
                SampleRegion(all, heat, obstacle, vel, res, IceX0, IceX1, IceY0, IceY1),        // 0 = inside the ice wall
                SampleRegion(all, heat, obstacle, vel, res, FrontX0, FrontX1, IceY0, IceY1),    // 1 = approach/front gap
                SampleRegion(all, heat, obstacle, vel, res, BeyondX0, BeyondX1, IceY0, IceY1),  // 2 = beyond (breakthrough)
                SampleRegion(all, heat, obstacle, vel, res, AmbX0, AmbX1, AmbY0, AmbY1),        // 3 = far ambient control
            };
        }

        private static RegionStats SampleRegion(iparticle[] all, float[] heat, float[] obstacle, Vector2[] vel,
            int res, float x0, float x1, float y0, float y1)
        {
            var stats = new RegionStats { heatMin = float.MaxValue };

            int ix0 = Mathf.Clamp(Mathf.FloorToInt(x0 * res), 0, res - 1);
            int ix1 = Mathf.Clamp(Mathf.CeilToInt(x1 * res), 0, res - 1);
            int iy0 = Mathf.Clamp(Mathf.FloorToInt(y0 * res), 0, res - 1);
            int iy1 = Mathf.Clamp(Mathf.CeilToInt(y1 * res), 0, res - 1);

            int n = 0;
            double f = 0, ic = 0, w = 0, st = 0, h = 0; float hmax = 0f, hmin = float.MaxValue; int obs = 0;
            double vsum = 0, vxsum = 0; float vmax = 0f;
            for (int y = iy0; y <= iy1; y++)
            {
                for (int x = ix0; x <= ix1; x++)
                {
                    int i = y * res + x;
                    var p = all[i];
                    f += p.fire; ic += p.ice; w += p.water; st += p.steam;
                    if (heat != null) { float hv = heat[i]; h += hv; if (hv > hmax) hmax = hv; if (hv < hmin) hmin = hv; }
                    if (obstacle != null && obstacle[i] > 0.5f) obs++;
                    if (vel != null)
                    {
                        Vector2 v = vel[i];
                        float m = v.magnitude;
                        vsum += m; vxsum += v.x; if (m > vmax) vmax = m;
                    }
                    n++;
                }
            }
            if (n == 0) return stats;
            stats.fire = (float)f; stats.ice = (float)ic; stats.water = (float)w; stats.steam = (float)st;
            stats.heatAvg = heat != null ? (float)(h / n) : 0f;
            stats.heatMax = hmax; stats.heatMin = hmin == float.MaxValue ? 0f : hmin;
            stats.obstacleCoverage = (float)obs / n;
            stats.velAvg = vel != null ? (float)(vsum / n) : 0f;
            stats.velMax = vmax;
            stats.velAvgX = vel != null ? (float)(vxsum / n) : 0f;
            return stats;
        }

        /// <summary>
        /// Heat lives on the private SimulationContext. Reflection is confined to this dev harness (the
        /// same approach the PlayMode tests use) rather than widening the runtime public API for a tool.
        /// </summary>
        private static float[] ReadHeat(SimDriver sim, int res)
        {
            var rt = GetContext(sim)?.Heat?.Read;
            return rt == null ? null : ReadR(rt, res);
        }

        /// <summary>Reads the RG (x,y) velocity field, for regional flow metrics.</summary>
        private static Vector2[] ReadRG(RenderTexture rt, int res)
        {
            if (rt == null) return null;
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                var px = tex.GetPixels();
                var outp = new Vector2[res * res];
                for (int y = 0; y < res && y < rt.height; y++)
                    for (int x = 0; x < res && x < rt.width; x++)
                    {
                        Color c = px[y * rt.width + x];
                        outp[y * res + x] = new Vector2(c.r, c.g);
                    }
                return outp;
            }
            finally { RenderTexture.active = prev; Object.DestroyImmediate(tex); }
        }

        private static float[] ReadObstacle(SimDriver sim, int res)
        {
            var rt = sim.GetObstacleTexture();
            return rt == null ? null : ReadR(rt, res);
        }

        private static float[] ReadR(RenderTexture rt, int res)
        {
            var prev = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                var px = tex.GetPixels();
                var outp = new float[res * res];
                for (int y = 0; y < res && y < rt.height; y++)
                    for (int x = 0; x < res && x < rt.width; x++)
                        outp[y * res + x] = px[y * rt.width + x].r;
                return outp;
            }
            finally { RenderTexture.active = prev; Object.DestroyImmediate(tex); }
        }

        private static string SeriesEntry(int step, float t, RegionStats[] s)
        {
            var ci = CultureInfo.InvariantCulture;
            string R(RegionStats r) =>
                "{\"fire\":" + r.fire.ToString("0.###", ci) + ",\"ice\":" + r.ice.ToString("0.###", ci)
                + ",\"water\":" + r.water.ToString("0.###", ci) + ",\"steam\":" + r.steam.ToString("0.###", ci)
                + ",\"heatAvg\":" + r.heatAvg.ToString("0.####", ci) + ",\"heatMax\":" + r.heatMax.ToString("0.####", ci)
                + ",\"heatMin\":" + r.heatMin.ToString("0.####", ci)
                + ",\"obstacleCoverage\":" + r.obstacleCoverage.ToString("0.###", ci)
                + ",\"velAvg\":" + r.velAvg.ToString("0.#####", ci)
                + ",\"velMax\":" + r.velMax.ToString("0.#####", ci)
                + ",\"velAvgX\":" + r.velAvgX.ToString("0.#####", ci) + "}";
            return "{\"step\":" + step + ",\"t\":" + t.ToString("0.###", ci)
                + ",\"ice\":" + R(s[0]) + ",\"front\":" + R(s[1])
                + ",\"beyond\":" + R(s[2]) + ",\"ambient\":" + R(s[3]) + "}";
        }

        /// <summary>Turns the raw series into the actionable verdicts described on the class doc.</summary>
        private static string Diagnose(RegionStats[] baseline, System.Collections.Generic.List<RegionStats[]> series,
            bool obstacleWallMode, float seededConcentration)
        {
            var ci = CultureInfo.InvariantCulture;
            if (series.Count == 0) return "\"diagnosis\":{}";
            var last = series[series.Count - 1];

            float iceStart = baseline[0].ice, iceEnd = last[0].ice;
            float iceDelta = iceEnd - iceStart;

            bool fireReachesIce = last[1].fire > 0.01f || last[0].fire > 0.01f;
            bool heatEntersIce = last[0].heatAvg > baseline[0].heatAvg + 0.02f;
            bool iceMelting = iceDelta < -0.01f * Mathf.Max(iceStart, 1e-3f);
            bool heatDrainsFirst = last[1].heatAvg > last[0].heatAvg + 0.15f;   // hot front, cold ice

            // Obstacle coverage across the WHOLE series, not just baseline: the mask is rebuilt inside
            // StepSimulation, so at baseline (seeded, not yet stepped) it is still empty and would give a
            // falsely clean reading.
            //
            // Its MEANING depends on which wall variant ran, and conflating the two would make the report
            // lie in one of the modes:
            //   thin wall (0.3, CP8o)      -> coverage MUST stay ~0. If it rises, thin ice wrongly became
            //                                 a flow obstacle — the CP8o regression.
            //   obstacle wall (1.0, CP8q)  -> coverage MUST be high. If it is ~0 the wall never became
            //                                 solid and the run is NOT exercising Lake's reported case.
            float maxObstacleCoverage = 0f;
            foreach (var s in series) maxObstacleCoverage = Mathf.Max(maxObstacleCoverage, s[0].obstacleCoverage);
            bool wallIsObstacle = maxObstacleCoverage > 0.01f;
            bool thinIceIsObstacle = !obstacleWallMode && wallIsObstacle;              // CP8o regression
            bool obstacleWallNeverFormed = obstacleWallMode && !wallIsObstacle;        // CP8q setup failure

            // CP8q headline: for the obstacle-strength run, did heat get INTO a genuinely solid wall and
            // melt it? This is the exact question Lake asked, answered as one boolean.
            bool obstacleIceMeltsUnderFire = obstacleWallMode && wallIsObstacle && heatEntersIce && iceMelting;

            // Refreeze: did ice mass ever rise between consecutive samples?
            bool meltRefreezes = false;
            for (int i = 1; i < series.Count; i++)
                if (series[i][0].ice > series[i - 1][0].ice + 1e-3f) meltRefreezes = true;

            return "\"diagnosis\":{"
                + "\"iceStart\":" + iceStart.ToString("0.###", ci) + ","
                + "\"iceEnd\":" + iceEnd.ToString("0.###", ci) + ","
                + "\"icePercentMelted\":" + (iceStart > 1e-4f ? (100f * -iceDelta / iceStart) : 0f).ToString("0.#", ci) + ","
                + "\"fireReachesIce\":" + (fireReachesIce ? "true" : "false") + ","
                + "\"heatEntersIce\":" + (heatEntersIce ? "true" : "false") + ","
                + "\"iceMelting\":" + (iceMelting ? "true" : "false") + ","
                + "\"heatDrainsFirst\":" + (heatDrainsFirst ? "true" : "false") + ","
                + "\"meltRefreezes\":" + (meltRefreezes ? "true" : "false") + ","
                + "\"thinIceIsObstacle\":" + (thinIceIsObstacle ? "true" : "false") + ","
                + "\"maxObstacleCoverageInIce\":" + maxObstacleCoverage.ToString("0.###", ci) + ","
                + "\"seededWallConcentration\":" + seededConcentration.ToString("0.###", ci) + ","
                + "\"obstacleWallMode\":" + (obstacleWallMode ? "true" : "false") + ","
                + "\"wallIsObstacle\":" + (wallIsObstacle ? "true" : "false") + ","
                + "\"obstacleWallNeverFormed\":" + (obstacleWallNeverFormed ? "true" : "false") + ","
                + "\"obstacleIceMeltsUnderFire\":" + (obstacleIceMeltsUnderFire ? "true" : "false") + ","
                + "\"waterGained\":" + (last[0].water - baseline[0].water).ToString("0.###", ci) + ","
                + "\"breakthroughFireBeyond\":" + last[2].fire.ToString("0.###", ci)
                + "}";
        }

        private static string AmbientDiagnose(RegionStats[] baseline, System.Collections.Generic.List<RegionStats[]> series)
        {
            var ci = CultureInfo.InvariantCulture;
            if (series.Count == 0) return "\"diagnosis\":{}";
            var last = series[series.Count - 1];
            float iceStart = baseline[0].ice, iceEnd = last[0].ice;

            // Lake's question, answered directly: does ambient warm the ice, and does the ice actually go?
            bool ambientWarmsIce = last[0].heatAvg > baseline[0].heatAvg + 0.02f;
            bool ambientMeltsIce = iceEnd < iceStart * 0.95f;
            bool heatDrainedBeforeMelt = !ambientMeltsIce && last[0].heatAvg < 0.15f;

            return "\"diagnosis\":{"
                + "\"iceStart\":" + iceStart.ToString("0.###", ci) + ","
                + "\"iceEnd\":" + iceEnd.ToString("0.###", ci) + ","
                + "\"icePercentMelted\":" + (iceStart > 1e-4f ? (100f * (iceStart - iceEnd) / iceStart) : 0f).ToString("0.#", ci) + ","
                + "\"iceHeatStart\":" + baseline[0].heatAvg.ToString("0.####", ci) + ","
                + "\"iceHeatEnd\":" + last[0].heatAvg.ToString("0.####", ci) + ","
                + "\"ambientHeatEnd\":" + last[3].heatAvg.ToString("0.####", ci) + ","
                + "\"ambientWarmsIce\":" + (ambientWarmsIce ? "true" : "false") + ","
                + "\"ambientMeltsIce\":" + (ambientMeltsIce ? "true" : "false") + ","
                + "\"heatDrainedBeforeMelt\":" + (heatDrainedBeforeMelt ? "true" : "false")
                + "}";
        }

#if UNITY_EDITOR
        [MenuItem("Inkling/Run Fire vs Ice Test")]
        private static void RunFireIceMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[FireIce] Enter Play mode first, then run this menu.");
                return;
            }
            var runner = FindFirstObjectByType<InkScenarioRunner>();
            if (runner == null)
            {
                var go = new GameObject("InkScenarioRunner");
                runner = go.AddComponent<InkScenarioRunner>();
            }
            runner.runFireIceRequested = true;
        }

        /// <summary>CP8q: same harness, wall seeded at OBSTACLE strength (1.0) — Lake's reported case.</summary>
        [MenuItem("Inkling/Run Fire vs Ice Test (Obstacle-Strength Wall)")]
        private static void RunFireIceObstacleMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[FireIce] Enter Play mode first, then run this menu.");
                return;
            }
            var runner = FindFirstObjectByType<InkScenarioRunner>();
            if (runner == null)
            {
                var go = new GameObject("InkScenarioRunner");
                runner = go.AddComponent<InkScenarioRunner>();
            }
            runner.obstacleStrengthWall = true;
            runner.fireIceTag = "cp8q_obstacle";
            runner.runFireIceRequested = true;
        }

        /// <summary>
        /// CP8z: obstacle-strength wall under STRICT conduction-only heat (the new default model). Compare
        /// its fire_ice_metrics.json against the legacy-advective run below — same scenario, only the
        /// obstacle-heat model differs, so the diff isolates advection-vs-conduction game feel.
        /// </summary>
        [MenuItem("Inkling/Run Fire vs Ice Test (Strict Conduction, Obstacle Wall)")]
        private static void RunFireIceStrictMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[FireIce] Enter Play mode first, then run this menu.");
                return;
            }
            var runner = FindFirstObjectByType<InkScenarioRunner>();
            if (runner == null)
            {
                var go = new GameObject("InkScenarioRunner");
                runner = go.AddComponent<InkScenarioRunner>();
            }
            runner.obstacleStrengthWall = true;
            runner.fireIceHeatModeOverride = 0;   // strict conduction-only
            runner.fireIceTag = "cp8z_strict";
            runner.runFireIceRequested = true;
        }

        /// <summary>CP8z: same obstacle-strength wall under the LEGACY CP8q advective heat path, for A/B.</summary>
        [MenuItem("Inkling/Run Fire vs Ice Test (Legacy Advective, Obstacle Wall)")]
        private static void RunFireIceLegacyAdvectiveMenu()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[FireIce] Enter Play mode first, then run this menu.");
                return;
            }
            var runner = FindFirstObjectByType<InkScenarioRunner>();
            if (runner == null)
            {
                var go = new GameObject("InkScenarioRunner");
                runner = go.AddComponent<InkScenarioRunner>();
            }
            runner.obstacleStrengthWall = true;
            runner.fireIceHeatModeOverride = 1;   // legacy CP8q advective
            runner.fireIceTag = "cp8z_legacy";
            runner.runFireIceRequested = true;
        }
#endif
    }
}
