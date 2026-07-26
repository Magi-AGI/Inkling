using UnityEngine;
using Magi.UnityTools.Core;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Shared state container for all simulation modules.
    /// Holds references to GPU resources, simulation parameters, compute shader refs,
    /// feature flags, and helpers. No logic — just fields.
    /// SimDriver populates this from its [SerializeField] values each frame.
    /// </summary>
    public class SimulationContext
    {
        // ── Render textures (ping-pong) ─────────────────────────────────────
        public PingPongRenderTexture Velocity;
        public PingPongRenderTexture Pressure;
        public PingPongRenderTexture Density;
        // Heat: scalar environment/temperature layer (separate field, like velocity/pressure —
        // NOT an iparticle channel). CP1: inert, transported/decayed but with no sources.
        public PingPongRenderTexture Heat;

        // ── Single render textures ──────────────────────────────────────────
        public RenderTexture Divergence;
        // Velocity as it stands BEFORE ApplyObstacleBoundary clips it.
        // CP8z: UNUSED IN THE DEFAULT (strict) MODEL. Strict conduction-only binds the CLIPPED
        // ctx.Velocity.Read to AdvectHeat, because advection is transport by the fluid and fluid cannot
        // enter a solid — heat gets no advective licence a wall would deny to mass. This snapshot is
        // bound ONLY when HeatObstacleMode == 1 (legacy CP8q advective path), which exists purely so the
        // Fire-vs-Ice harness can A/B the two models. It is still allocated/copied/cleared to keep that
        // comparison available; it does not participate in normal play.
        public RenderTexture VelocityThermal;
        public RenderTexture VorticityTex;
        public RenderTexture Obstacles;
        public RenderTexture DisplayRT;
        public RenderTexture GradientRT;
        public RenderTexture CreatureInkBuffer;
        // Per-cell reaction magnitude (fire replacing plant), written by InkInteractions and
        // consumed by the ApplyReactionImpulse kernel. Cleared each step.
        public RenderTexture ReactionImpulseTex;

        // ── Channel textures (UAV + mipped + downsampled) ───────────────────
        public RenderTexture ChannelRT0;
        public RenderTexture ChannelRT1;
        public RenderTexture ChannelRT2;
        public RenderTexture ChannelRT0Mipped;
        public RenderTexture ChannelRT1Mipped;
        public RenderTexture ChannelRT2Mipped;
        public RenderTexture ChannelRT0Down;
        public RenderTexture ChannelRT1Down;
        public RenderTexture ChannelRT2Down;

        // ── Thermal rule buffers (CP7d slice 2) ─────────────────────────────
        // Fixed-capacity (MaxTransitions / MaxSources), allocated once and re-uploaded only when the
        // baked rule set actually changes — so there is no per-frame allocation or upload churn.
        public ComputeBuffer ThermalTransitionBuffer;
        public ComputeBuffer ThermalSourceBuffer;

        // ── Particle buffers ────────────────────────────────────────────────
        public ComputeBuffer[] ParticlesBuffer;
        public int ParticleReadIndex;
        public int ParticleWriteIndex = 1;
        public bool GpuPromotesHalf;
        public int GpuParticleStride;

        // ── Simulation parameters ───────────────────────────────────────────
        public int Resolution;
        public float Timestep;
        // Real-time seconds the current step represents (real frame dt in play, fixed Timestep
        // under deterministic/external step control). Drives dt-normalized decays in the shader.
        public float FrameDeltaTime;
        public float Viscosity;
        public float VorticityStrength;
        public float Dissipation;
        public float VelocityDissipation;
        public int PressureIterations;
        public int DiffusionIterations;
        public bool UseRedBlackSolver;
        public float InjectionForce;
        public float DensityAmount;
        public float ForceRadius;
        public float ForceStrength;

        // ── Display parameters ──────────────────────────────────────────────
        public int EffectiveDisplayRes;
        public bool DisplayVelocity;
        public bool UseParticleDisplay;
        public bool UseCpuCreatureComposite;
        public bool UseGradientRendering;
        public Renderer DisplayRenderer;
        public Magi.Inkling.Systems.Rendering.InkGradientPreset GradientPreset;
        public Material GradientMaterial;
        public Material DensityStampMaterial;

        // ── Compute shader refs ─────────────────────────────────────────────
        public ComputeShader FluidCompute;
        public ComputeShader StampCompute;
        public ComputeShader StampParticlesCompute;
        public ComputeShader BatchedStampCompute;
        public ComputeShader BatchedMaskCompute;
        public ComputeShader BatchedInjectionCompute;
        public ComputeShader ParticleToColorCompute;
        public ComputeShader ParticleChannelSplatCompute;
        public ComputeShader InkInteractionsCompute;
        public ComputeShader ThermalInteractionsCompute;

        // ── Particle simulation flags ───────────────────────────────────────
        public bool UseParticleSimulation;
        public bool UseParticleAdvection;
        public bool UseParticleDissipation;
        public bool UseParticleDiffusion;
        public int MaxParticleSimResolution;
        public bool UseParticleRenderPass;

        // ── Injection flags ─────────────────────────────────────────────────
        public bool UseBatchedDensityInjection;
        public bool UseBatchedStamping;
        public bool UseBatchedMasks;

        // ── Ink interactions ────────────────────────────────────────────────
        public bool UseInkInteractions;
        public bool InkInteractionsDebugMode;
        public AffinityGroup[] AffinityGroups;

        // ── Reaction impulse (fire replacing plant seeds motion) ────────────
        public bool EnableReactionImpulse;
        public float ReactionImpulseStrength;
        public float ReactionImpulseMax;
        public float ReactionImpulseCurlBias;
        public float ReactionImpulseExpansionBias;
        public float ReactionImpulseGain;

        // ── Heat layer parameters (CP1: inert defaults) ─────────────────────
        // ThermalDissipationHalfLife: seconds for heat to fade 50% toward NEUTRAL (large ≈ persistent).
        // ThermalDiffusion: 0..1 conduction — blend toward neighbour average per step. Non-zero so fire
        //   and ice actually modulate the temperature AROUND them, not just their own cell. CP8e raised
        //   this from 0.05, which was too subtle to read: heat barely left the cell that emitted it.
        //
        // CP8a — these two were ONE field (`AmbientTemperature`) and must stay separate:
        //   NeutralTemperature: room temperature. The value heat RELAXES TOWARD, and what the heat
        //     field is initialised/cleared to. Water is the stable phase here.
        //   MinTemperature: the absolute clamp FLOOR. Must NOT be the neutral, or nothing could ever
        //     get colder than room temperature and ice could never form.
        // CP8k: 1000s was not "low dissipation", it was NO THERMOSTAT — it restored only 0.000346 heat
        // per second, taking ~515s to warm a frozen cell back to the melt point. Meanwhile every thermal
        // transition REMOVES heat and none returns it, so the field could only ratchet colder. Relaxation
        // toward NEUTRAL is not heat leaking away; it is the room being a room. 60s keeps heat clearly
        // persistent (explicit effects still dominate on their own timescales) while letting an isolated
        // frozen patch actually recover.
        public float ThermalDissipationHalfLife = 60f;
        // CP8l: conduction rate PER SECOND (was a per-frame blend, the only heat term that was not
        // dt-normalised — at 60fps a 0.2 blend/frame was an effective ~12/sec and beat fire's own
        // emission 6:1, so fire could not hold its own temperature and hot spots smeared away).
        public float ThermalDiffusion = 2f;

        // CP8l: conduction rate PER SECOND inside solids. Lake: "Heat should travel even more readily
        // through solids than it does in the open fluids." Physically right — ice and rock conduct far
        // better than the fluid around them — and under CP8z's strict model it is once again the ONLY
        // way heat enters a solid, since advection cannot cross a wall.
        //
        // Rate history: 12 (CP8l) -> 30 (CP8aa) -> 60 (CP8ab). CP8aa/CP8ab were driven by the strict
        // Fire-vs-Ice data: at 12, strict melted only 12.6% of the obstacle wall in 10s while the wall's
        // average temperature plateaued at ~0.1486, just BELOW meltThreshold 0.15 — the bulk never crossed
        // the threshold, so only the surface skin melted. Raising the rate lifted that: 30 -> ~31%, and
        // Lake judged 60 better by eye. 60 is the top of the useful range: per-frame blend 1-exp(-60/60)
        // = 0.632; past this the blend approaches 1 and further increases do little.
        //
        // (Historical note: CP8aa/CP8ab cited heatDrainsFirst=true as evidence "melting outran
        // conduction". CP8ac later showed that flag is a MISLEADING static front-vs-ice temperature gap,
        // not a flux comparison — it is near-tautological for a large wall mid-melt. So it did not prove a
        // conduction bottleneck; it merely reflected that the wall was not yet mostly melted.)
        //
        // CP8ad separately lowered meltHeatCost 0.15 -> 0.10 so each unit of delivered heat melts more ice
        // (Lake: obstacle walls too stubborn). That is the melt-side lever; this is the conduction-side one.
        //
        // Raising the solid rate attacks that equilibrium from both sides — it moves more energy across
        // the fluid/solid interface AND carries it deeper, which keeps the surface cooler and so widens
        // the gradient driving further inflow. Numerically safe at any magnitude: the kernel blends toward
        // the neighbour AVERAGE (a convex combination), so it is bounded and cannot overshoot or go
        // unstable; at very high rates a cell simply becomes the neighbour average each frame.
        // Useful range ~12 (pre-CP8aa) .. 60; past ~60 the per-frame blend approaches 1 and saturates.
        // NOTE the obstacle mask cannot distinguish ink solids from geometry walls — both get this rate.
        public float ThermalDiffusionSolid = 60f;

        // CP8o: ice concentration at/above which a cell CONDUCTS at the solid rate. This is DECOUPLED from
        // the velocity/flow obstacle threshold (Ice.obstacleThreshold, 0.5). Lake wants thin ice to keep
        // NOT blocking flow, but still to conduct heat and melt — so thermal-solid classification reads the
        // ice concentration directly against this lower threshold. 0.1 sits below brush density (0.3), so a
        // normal painted stroke conducts, while staying below the 0.5 flow threshold so it does not dam
        // fluid. Fixes the CP8n coupling where making ice conductive also made thin ice a flow obstacle.
        public float ThermalSolidThresholdIce = 0.1f;

        // LEGACY (CP8q): fraction of the surrounding fluid velocity a SOLID cell borrows for HEAT
        // advection. IGNORED unless HeatObstacleMode == 1.
        //
        // CP8z retired this from the default model. Lake: "we may have made a mistake in allowing
        // advection through obstacles when what we really needed was conduction." Letting heat ride the
        // flow through a wall conflated two different physics; the real fix for unmeltable obstacle ice
        // is stronger CONDUCTION (ThermalDiffusionSolid), not an advective loophole. Kept only so the
        // Fire-vs-Ice harness can still run the old model side by side.
        public float ThermalSolidPermeability = 1f;

        // CP8z: how AdvectHeat treats obstacles. 0 = STRICT conduction-only (DEFAULT) — heat crosses a
        // solid only via DiffuseHeat conduction, never by advection. 1 = LEGACY CP8q advective path,
        // retained as an opt-in diagnostic so the Fire-vs-Ice harness can A/B the two models. Lake's
        // revised model is strict: advection is transport by the fluid, and fluid cannot enter a solid.
        // In strict mode ThermalSolidPermeability and VelocityThermal are both unused.
        public int HeatObstacleMode = 0;

        // CP8r: heat absorbed per unit of Fire+Water converted to Steam by the CONTACT quench —
        // evaporative cooling. Lake: dousing burning plant "is never enough to completely cancel out the
        // fire". The quench removed fire MASS but not HEAT, so the cell stayed above plantIgnitionThreshold
        // (0.75) and kept regenerating Fire from Plant, and above fireSinkThreshold (0.6) so surviving fire
        // never guttered out. Vaporising water absorbs latent heat — that is how water actually puts fire
        // out. Applies ONLY to the Fire+Water quench group; 0 disables it.
        public float QuenchCoolingPerUnit = 1f;

        public float NeutralTemperature = 0.5f;
        public float MinTemperature = 0f;

        // CP8f: Steam is born HOT — between Water (neutral 0.5) and Fire (max 1.0). 0.75 is the midpoint,
        // and the band matters: it sits ABOVE CondenseThreshold (0.65), so freshly painted steam does not
        // instantly condense back into water, and BELOW BoilThreshold (0.85), so it does not read as fire-hot.
        public float SteamInjectionTemperature = 0.75f;

        // Sanitized temperature bounds, guaranteeing min <= neutral <= max. These live on the context
        // (not just FluidSolver) because OperationQueue also needs them: injection heat stamping runs
        // during ProcessPending(), which is BEFORE FluidSolver.Step() uploads SetConstants() — so the
        // queue must upload the bounds itself or the clamp would use stale/zero uniforms.
        public float SanitizedMinTemperature => Mathf.Min(MinTemperature, MaxHeat);
        public float SanitizedMaxTemperature => Mathf.Max(SanitizedMinTemperature, MaxHeat);
        public float SanitizedNeutralTemperature =>
            Mathf.Clamp(NeutralTemperature, SanitizedMinTemperature, SanitizedMaxTemperature);

        // CP8f: clamped into [neutral, max] so Steam stays hotter than Water and no hotter than Fire even
        // if the knobs are retuned. Steam colder than water, or hotter than fire, is never a valid state.
        public float SanitizedSteamInjectionTemperature =>
            Mathf.Clamp(SteamInjectionTemperature, SanitizedNeutralTemperature, SanitizedMaxTemperature);

        // ── CP8w: ColdAir, a temperature-only "ink" ────────────────────────────────────────────────
        //
        // Lake: "since ice is the only way to lower the temperature, we can't determine whether water
        // will freeze on its own. Let's make a new ink for cold air ... so that we can experiment with
        // temperature without inserting ice into the scene."
        //
        // ColdAir has NO mass channel and NO InkTypeDef. It is a selection index one past the last real
        // ink that routes to a HEAT-ONLY injection: it stamps the heat field and touches nothing else.
        // That is the entire point — if water freezes after painting ColdAir, it froze because the
        // Water->Ice thermal rule reacted to the cold, not because we seeded ice behind Lake's back.
        //
        // Deliberately NOT an iparticle channel: that struct is a fixed 10-field layout shared with
        // InkTools, and expanding it is a structural package change we do not need for a thermal probe.
        public const int ColdSourceInkIndex = (int)InkTypeId.Count;   // 10 — one past Ice (9)

        /// <summary>True if this selection index is the ColdAir temperature probe rather than a real ink.</summary>
        public static bool IsColdSource(int inkTypeIndex) => inkTypeIndex == ColdSourceInkIndex;

        // The temperature ColdAir drives its cell toward. Defaults to the floor so it is the strongest
        // cooling tool available; serialized so it can be dialled back to a milder chill mid-experiment.
        public float ColdSourceTemperature = 0f;

        /// <summary>ColdAir's target, clamped into the valid band so it can never mint heat above max.</summary>
        public float SanitizedColdSourceTemperature =>
            Mathf.Clamp(ColdSourceTemperature, SanitizedMinTemperature, SanitizedMaxTemperature);

        /// <summary>
        /// The temperature an injection of this ink stamps into the heat field (CP8b, CP8f, CP8k).
        /// Only THREE inks have a characteristic temperature: Fire injects at the ceiling, Ice at the
        /// floor (so painting ice creates genuinely sub-neutral cold), and Steam hot, between the two.
        /// <para>
        /// CP8k: EVERY other ink — Water, Plant, Glitter, BlackBody, Electricity — stamps NEUTRAL.
        /// Previously they returned false and left the heat field untouched, which quietly made painting
        /// a non-thermal ink a way to PRESERVE stale cold: paint plant over a frozen patch and the patch
        /// stayed frozen, so ink and temperature drifted apart. Room-temperature ink should arrive at
        /// room temperature. Only an out-of-range index returns false now.
        /// </para>
        /// </summary>
        public bool TryGetInjectionTemperature(int inkTypeIndex, out float targetTemperature)
        {
            // Out of range => not an ink => leave the heat field alone. This is the ONLY false case.
            if (inkTypeIndex < 0 || inkTypeIndex >= (int)InkTypeId.Count)
            {
                targetTemperature = SanitizedNeutralTemperature;   // never the floor: a caller that
                return false;                                      // ignores the bool must not freeze.
            }

            switch ((InkTypeId)inkTypeIndex)
            {
                case InkTypeId.Fire:  targetTemperature = SanitizedMaxTemperature;            return true;
                case InkTypeId.Ice:   targetTemperature = SanitizedMinTemperature;            return true;
                case InkTypeId.Steam: targetTemperature = SanitizedSteamInjectionTemperature; return true;

                // Water, PlantSeeded, PlantGrown, Glitter, BlackBody, Electricity*: all room temperature.
                default:              targetTemperature = SanitizedNeutralTemperature;        return true;
            }
        }
        // Heat sources (CP3): fire emits heat (add-only, diagnostic — heat drives nothing yet).
        public bool EnableHeatSources = true;
        public float FireHeatEmissionRate = 4f;
        public float MaxHeat = 1f;

        // Thermal interactions (CP5): heat-driven LOCAL phase changes. Default OFF (opt-in) —
        // this is the first pass that alters ink state, so baseline stays unchanged until enabled.
        public bool EnableThermalInteractions = false;
        // CP8a/CP8j thermal layout, placed around the NEUTRAL (room) temperature of 0.5:
        //   min 0 .. [freeze == melt == .15] .. [NEUTRAL .5] .. condense .65 .. boil .85 .. max 1
        // At neutral water is stable, ice melts, steam slowly condenses. Condense sits ABOVE melt —
        // required, and legal because the baker validates per-inverse-pair, not with a global cold<=hot
        // ladder. Sanitized per-cycle before upload: freeze <= melt, condense <= boil.
        //
        // CP8j collapsed freeze and melt onto ONE point. They used to be .15 and .35, and that gap was a
        // dead band: ice sitting between them was above freezing yet still refused to melt.
        public float FreezeThreshold = 0.15f;
        // CP8j: melt sits ON the freeze point. Any gap above it is a band where ice is warmer than
        // freezing but stubbornly refuses to melt — ice divorced from cold, which is what Lake flagged.
        public float MeltThreshold = 0.15f;
        public float CondenseThreshold = 0.65f;
        public float BoilThreshold = 0.85f;
        public float MeltRate = 1f;
        public float BoilRate = 1f;
        // CP8h: condensation is deliberately GENTLE — cooling steam sheds only ~15% of itself into water
        // per second rather than collapsing wholesale. Rate only; the threshold is untouched, so steam
        // still condenses whenever it is cold enough, just slowly.
        public float CondenseRate = 0.15f;
        public float FreezeRate = 0.4f;
        public float MeltHeatCost = 0.10f;
        public float BoilHeatCost = 0.5f;
        public float CondenseHeatRelease = 0f;
        // CP8g: heat removed per unit of Water that FREEZES into Ice — a ONE-SHOT cooling event at the
        // moment ice forms, not continuous cooling from ice that already exists. It scales with the
        // amount actually converted, so a cell of settled ice (no water left to freeze) converts
        // nothing and therefore cools nothing. Ice is deliberately NOT a thermal source like Fire is.
        //
        // CP8k cut this from 1.0. It was the dominant term in a global heat RATCHET: every transition is
        // a heat sink and none returns heat, so a water -> ice -> water round trip destroyed 1.5 units of
        // heat and left the matter exactly where it started — a perpetual refrigerator that dragged the
        // whole field toward frozen. Painted ice gets its chill from the injection stamp (which stamps
        // the floor outright) regardless, so 0.2 keeps the feel without the runaway.
        public float FreezeHeatCost = 0.1f;
        // CP8d/CP8e/CP8l: SPONTANEOUS combustion (Plant -> Fire) from ambient heat alone. Burning consumes
        // heat, which also bounds how fast it can convert.
        //
        // CP8e set this to 0.98 to keep it rare. CP8l lowered it to 0.75 because 0.98 turned out to be
        // UNREACHABLE, not merely rare: a plant cell beside a max-heat fire converges to its neighbour
        // average, (1.0 + 0.5*3)/4 = 0.625, and can never climb to 0.98 by conduction — so heat-only
        // ignition was dead code. 0.75 is reachable when plant is genuinely well-surrounded by fire, yet
        // still far above ordinary warmth (ambient 0.5), so plant never combusts just from a warm room.
        //
        // Fire catching ADJACENT plant is a DIFFERENT mechanism and is not gated by this threshold at
        // all: that is the Fire x Plant contact reaction authored in OrganicGroup.
        public float PlantIgnitionThreshold = 0.75f;
        public float PlantIgnitionRate = 0.5f;
        public float PlantIgnitionHeatCost = 0.25f;
        // CP8k: cold fire GOES OUT. Below FireSinkThreshold, fire is REMOVED (a sink transition), not
        // converted — a guttering flame must not mint smoke or a puddle. Heat-neutral, so fire dying
        // does not itself chill the cell. Fire heats its own cell, so a healthy flame stays above the
        // threshold and is unaffected; this culls fire that has drifted somewhere cold.
        public float FireSinkThreshold = 0.6f;
        public float FireSinkRate = 4f;
        // Fuel-like fire (CP7b): fire burned per unit heat actually added. 0 = add-only (no burn).
        public float FireHeatFuelCost = 0f;

        // ── Ink definitions ─────────────────────────────────────────────────
        public InkTypeDef[] InkDefinitions;

        // ── Black body fallback ─────────────────────────────────────────────
        public bool EnableBlackBodyClearingFallback;
        public float BlackBodyThresholdFallback;
        public float BlackBodyClearingRateFallback;

        // ── Stamp shader ref ────────────────────────────────────────────────
        public Shader DensityStampShader;

        // ── Debug flags ─────────────────────────────────────────────────────
        public bool DebugZeroPressure;
        public bool DebugZeroVelocity;
        public bool DebugSkipAir;
        public bool MeasurePerformance;

        // ── Fluid compute kernel indices ────────────────────────────────────
        // Initialized by FluidSolver.InitializeKernels(), shared with OperationQueue
        public int FluidKernelAdvection;
        public int FluidKernelDiffusion;
        public int FluidKernelDivergence;
        public int FluidKernelPressure;
        public int FluidKernelPressureRedBlack;
        public int FluidKernelSubtractGradient;
        public int FluidKernelVorticity;
        public int FluidKernelVorticityConfinement;
        public int FluidKernelAddForce;
        public int FluidKernelAddDensity;
        public int FluidKernelClear;
        public int FluidKernelUpdateObstacles;
        public int FluidKernelApplyObstacleBoundary;
        public int FluidKernelAdvectParticles;
        public int FluidKernelDissipateParticles;
        public int FluidKernelDiffuseParticles;
        public int FluidKernelAddParticlesGaussian;
        public int FluidKernelInkToObstacles = -1;
        public int FluidKernelAdvectHeat = -1;
        public int FluidKernelDiffuseHeat = -1;
        public int FluidKernelAddHeatSources = -1;
        public int FluidKernelStampInjectionHeat = -1;
        public int KernelThermalInteractions = -1;

        // ── Helpers ─────────────────────────────────────────────────────────

        public void SwapParticleBuffers()
        {
            int temp = ParticleReadIndex;
            ParticleReadIndex = ParticleWriteIndex;
            ParticleWriteIndex = temp;
        }
    }
}
