using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using Magi.UnityTools.Core;
using Magi.InkTools;
using Magi.InkTools.Simulation;
using Magi.UnityTools.Patterns;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Debug = UnityEngine.Debug;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Facade for the fluid simulation. Owns all [SerializeField] references (single Inspector surface),
    /// creates modules in Start(), delegates to them in Update()/LateUpdate(), and disposes in OnDestroy().
    /// Implements ISimulationService as thin wrappers around OperationQueue and context accessors.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class SimDriver : MonoBehaviour, ISimulationService, ISimulationDebug
    {
        private static readonly string[] FluidComputeCandidatePaths =
        {
            "Packages/com.inktools.sim/Compute/Fluids.compute",
            "Packages/com.inktools.sim/Runtime/Compute/Fluids.compute",
            "Assets/_Project/Scripts/Simulation/Compute/Fluids.compute",
            "Assets/_Project/Scripts/Systems/SimulationLOD0/Fluids.compute",
        };

        // ── Compute Shaders ─────────────────────────────────────────────────
        [Header("Compute Shader")]
        [SerializeField] public ComputeShader fluidCompute;

        [Header("Simulation Parameters")]
        [SerializeField] private int resolution = 256;
        [SerializeField] private float viscosity = 0.0001f;
        [SerializeField] private float vorticity = 20.0f;
        [SerializeField] private float dissipation = 0.999f;
        [SerializeField] private float velocityDissipation = 0.9995f;
        [SerializeField] private float timestep = 0.016f;

        [Header("Air Debug")]
        [SerializeField] private bool debugZeroPressure = false;
        [SerializeField] private bool debugZeroVelocity = false;
        [SerializeField] private bool debugSkipAir = false;

        // ISimulationDebug
        public bool DebugZeroPressure { get => debugZeroPressure; set => debugZeroPressure = value; }
        public bool DebugZeroVelocity { get => debugZeroVelocity; set => debugZeroVelocity = value; }
        public bool DebugSkipAir { get => debugSkipAir; set => debugSkipAir = value; }

        // ISimulationReader properties
        public float Viscosity => viscosity;
        public float Vorticity => vorticity;
        public float Dissipation => dissipation;
        public float VelocityDissipation => velocityDissipation;
        public float Timestep => timestep;
        public int Resolution => resolution;

        [Header("Solver Settings")]
        [SerializeField] private int pressureIterations = 10;
        [SerializeField] private int diffusionIterations = 2;
        [SerializeField] private bool useRedBlackSolver = false;

        [Header("Injection")]
        [SerializeField] private float injectionForce = 100f;
        [SerializeField] private float densityAmount = 0.3f;
        [SerializeField] private float forceRadius = 56f;
        [SerializeField] private float forceStrength = 1.5f;
        [SerializeField] private bool useBatchedDensityInjection = true;
        [SerializeField] private ComputeShader batchedInjectionCompute;
        [SerializeField] private bool useBatchedStamping = true;
        [SerializeField] private bool useBatchedMasks = true;

        [Header("Display")]
        [SerializeField] private Renderer displayRenderer;
        [SerializeField] private bool displayVelocity = false;
        [SerializeField] private bool useParticleDisplay = false;
        [SerializeField] private bool useCpuCreatureComposite = false;
        [SerializeField] private bool useGradientRendering = true;
        [SerializeField] private Magi.Inkling.Systems.Rendering.InkGradientPreset gradientPreset;
        [SerializeField] private Material gradientMaterial;

        [Header("Performance")]
        [SerializeField] private bool measurePerformance = true;

        [Header("Display Resolution")]
        [SerializeField] private int displayResolution = 0;
        [Range(0.1f, 1f)]
        [SerializeField] private float displayResolutionScale = 1f;

        [Header("Creature / Stamp Rendering")]
        [SerializeField] private Shader densityStampShader;
        [SerializeField] private ComputeShader stampCompute;
        [SerializeField] private ComputeShader stampParticlesCompute;
        [SerializeField] private ComputeShader batchedStampCompute;
        [SerializeField] private ComputeShader batchedMaskCompute;

        [Header("Particle Simulation")]
        [SerializeField] private bool useParticleSimulation = true;
        [SerializeField] private bool useParticleAdvection = true;
        [SerializeField] private bool useParticleDissipation = true;
        [SerializeField] private bool useParticleDiffusion = true;
        [SerializeField] private int maxParticleSimResolution = 512;

        [Header("Ink Interactions")]
        [SerializeField] private ComputeShader inkInteractionsCompute;
        [SerializeField] private ComputeShader thermalInteractionsCompute;
        [SerializeField] private bool useInkInteractions = true;
        [SerializeField] private bool inkInteractionsDebugMode = false;
        [SerializeField] private AffinityGroup[] affinityGroups;

        [Header("Reaction Impulse (fire replacing plant)")]
        [Tooltip("Seed velocity where fire replaces plant so fire can spread out of a still plant " +
                 "region. Flag-guarded: off = byte-identical to the concentration-only reaction path.")]
        [SerializeField] private bool enableReactionImpulse = true;
        [Tooltip("Velocity delta per unit reaction impulse magnitude, per-step (dt-scaled by subDt/timestep). " +
                 "Raise for stronger spread.")]
        [Range(0f, 40f)]
        [SerializeField] private float reactionImpulseStrength = 40f;
        [Tooltip("Hard clamp on the per-cell velocity delta magnitude. Guards against a " +
                 "reaction->fuel->faster-reaction runaway. Uncapped for authoring; the runtime clamp still applies.")]
        [Min(0f)]
        [SerializeField] private float reactionImpulseMax = 10f;
        [Tooltip("Weight of the tangential (swirl/curl) component of the impulse.")]
        [Range(0f, 2f)]
        [SerializeField] private float reactionImpulseCurlBias = 1f;
        [Tooltip("Weight of the outward front-normal (expansion) component. Kept weaker than curl.")]
        [Range(0f, 2f)]
        [SerializeField] private float reactionImpulseExpansionBias = 0.35f;
        [Tooltip("Gain applied to the accumulated reaction magnitude before it becomes an impulse.")]
        [Range(0f, 8f)]
        [SerializeField] private float reactionImpulseGain = 1f;

        [Header("Heat / Thermal (temperature field)")]
        [Tooltip("Seconds for temperature to relax 50% toward NEUTRAL (room temperature). Frame-rate " +
                 "independent. This is the THERMOSTAT, not a leak — heat is not draining away to zero, " +
                 "it is settling back to room temperature. CP8k lowered this from 1000, which restored " +
                 "only 0.0003 heat/sec (~515s to thaw a frozen cell) and so was effectively no thermostat " +
                 "at all: since every thermal transition REMOVES heat and none returns it, the field could " +
                 "only ever ratchet colder. Keep it long enough that explicit effects (fire, ice, phase " +
                 "changes) dominate on their own timescales, but short enough that the world recovers.")]
        [Min(0.25f)]
        [SerializeField] private float thermalDissipationHalfLife = 60f;
        [Tooltip("Thermal conduction: how fast temperature spreads to neighbours per step (0 = none). " +
                 "This is what lets fire warm — and ice chill — the region AROUND them rather than only " +
                 "their own cell.\n\n" +
                 "CP8l: this is now a rate PER SECOND, not a per-frame blend. DiffuseHeat was the only " +
                 "heat term that was not dt-normalised: at 60fps the old 0.2/frame was an effective " +
                 "~12/sec and beat fire's own emission 6:1, so fire could not hold its own temperature " +
                 "and every hot spot smeared away before it could melt ice or ignite plant. UNITS " +
                 "CHANGED — the old 0.2 and a new 0.2 are not the same thing.")]
        [Min(0f)]
        [SerializeField] private float thermalDiffusion = 2f;
        [Tooltip("Conduction rate PER SECOND inside SOLID/obstacle cells. Heat travels MORE readily " +
                 "through solids than open fluid — physically right, since ice and rock conduct better " +
                 "than the fluid around them. This is what lets a block of ice heat THROUGH rather than " +
                 "only skinning at its surface: a multi-cell solid has no fluid neighbours to borrow " +
                 "velocity from, so conduction carries heat through the INTERIOR.\n\n" +
                 "CP8z: in the DEFAULT strict model, conduction is once again the ONLY way heat crosses a " +
                 "solid — advection is transport by the fluid, and fluid cannot enter a solid. (CP8q's " +
                 "advective path survives only as the opt-in diagnostic thermalObstacleHeatMode = 1.)\n\n" +
                 "NOTE the obstacle mask cannot tell ink solids from geometry walls, so both get this rate.")]
        [Min(0f)]
        [SerializeField] private float thermalDiffusionSolid = 60f;
        [Tooltip("Ice concentration at/above which a cell CONDUCTS at the solid rate. DECOUPLED from the " +
                 "velocity/flow obstacle threshold (Ice.obstacleThreshold, 0.5): thin ice must keep NOT " +
                 "blocking flow, yet still conduct heat and melt. 0.1 sits below brush density (0.3) so a " +
                 "normal painted stroke conducts, while staying below 0.5 so it does not dam fluid. Set 0 " +
                 "to fall back to the geometry obstacle mask alone (the CP8n behaviour).")]
        [Min(0f)]
        [SerializeField] private float thermalSolidThresholdIce = 0.1f;
        [Tooltip("LEGACY (CP8q), ignored unless thermalObstacleHeatMode = 1. How much of the surrounding " +
                 "fluid velocity a SOLID cell borrows for HEAT advection. In the default strict model heat " +
                 "never advects through a solid, so this does nothing. Kept so the Fire-vs-Ice harness can " +
                 "A/B the old advective behaviour. 1 = energy crosses a solid as freely as fluid; 0 = none.")]
        [Range(0f, 1f)]
        [SerializeField] private float thermalSolidPermeability = 1f;
        [Tooltip("CP8z: how AdvectHeat treats obstacles. 0 = STRICT conduction-only (DEFAULT) — heat " +
                 "crosses a solid only by conduction (DiffuseHeat), never by advection, matching Lake's " +
                 "revised model. 1 = LEGACY CP8q advective path, an opt-in diagnostic for comparing the " +
                 "two models in the Fire-vs-Ice test. Leave at 0 for normal play.")]
        [Range(0, 1)]
        [SerializeField] private int thermalObstacleHeatMode = 0;
        [Tooltip("CP8r: heat absorbed per unit of Fire+Water converted to Steam by the CONTACT quench — " +
                 "evaporative cooling, i.e. how water actually extinguishes fire. Without it the quench " +
                 "removed fire MASS but left the cell hot, so plant kept re-igniting above 0.75 and " +
                 "surviving fire stayed above the 0.6 cold-fire sink — dousing could never finish the job. " +
                 "Applies ONLY to the Fire+Water quench group (not Fire x Plant, not any other pair). " +
                 "0 disables it and restores the pre-CP8r behaviour.")]
        [Min(0f)]
        [SerializeField] private float quenchCoolingPerUnit = 1f;
        [Tooltip("NEUTRAL (room) temperature. Temperature relaxes toward this, and the heat field is " +
                 "INITIALISED to it. Water is the stable phase here: it neither freezes nor boils. " +
                 "Thresholds are laid out around it (freeze/melt below, condense/boil above).")]
        [Range(0f, 1f)]
        [SerializeField] private float neutralTemperature = 0.5f;
        [Tooltip("Absolute lower clamp for temperature. Must stay BELOW neutral — if this were the " +
                 "neutral value, nothing could ever get colder than room temperature and ice could " +
                 "never form.")]
        [Range(0f, 1f)]
        [SerializeField] private float minTemperature = 0f;
        [Tooltip("Temperature stamped when Steam is painted DIRECTLY (typed injection). Steam is born hot: " +
                 "between Water (neutral) and Fire (max). Kept above the condense threshold so freshly " +
                 "painted steam does not immediately collapse back into water, and below the boil threshold " +
                 "so it does not read as fire-hot. Clamped into [neutral, max] at use. Does NOT affect steam " +
                 "produced by boiling or by the Fire+Water contact quench — those inherit their cell's heat.")]
        [Range(0f, 1f)]
        [SerializeField] private float steamInjectionTemperature = 0.75f;
        [Tooltip("When true, fire concentration emits heat into the heat layer (add-only; does not modify " +
                 "particles). Diagnostic in CP3 — visible only in the Heat debug view, not Combined rendering.")]
        [SerializeField] private bool enableHeatSources = true;
        [Tooltip("Heat added per unit fire per second (dt-normalized). CP8l raised this from 1: fire " +
                 "must OUT-PRODUCE conduction in its own cell, or it cannot hold its temperature, drops " +
                 "below the sink threshold and extinguishes itself.")]
        [Min(0f)]
        [SerializeField] private float fireHeatEmissionRate = 4f;
        [Tooltip("Clamp ceiling for the heat field to prevent runaway values.")]
        [Min(0f)]
        [SerializeField] private float maxHeat = 1f;

        [Header("Thermal Interactions (CP5: heat-driven phase changes, opt-in)")]
        [Tooltip("When true, heat drives LOCAL phase changes: ice->water (melt), water->steam (boil), " +
                 "steam->water (condense), water->ice (freeze). Default OFF — first pass that alters ink " +
                 "state, so baseline is unchanged until enabled. Local-only conversions (no neighbor " +
                 "sampling) are conservation-safe.")]
        [SerializeField] private bool enableThermalInteractions = false;
        // CP8a/CP8j layout around neutral 0.5:
        //   [freeze == melt == .15] .. [NEUTRAL .5] .. condense .65 .. boil .85
        // CP8j collapsed freeze and melt onto ONE point (they were .15 and .35) — the gap between them
        // was a dead band where ice was above freezing yet still refused to melt.
        //
        // Sanitized PER CYCLE (freeze <= melt, condense <= boil). Condense is deliberately ABOVE melt:
        // at room temperature both steam->water and ice->water must run, which is what makes water the
        // stable phase. They are not inverses, so they cannot oscillate.
        [Tooltip("Temperature below which water freezes to ice. Sanitized to <= meltThreshold (its inverse).")]
        [Range(0f, 1f)]
        [SerializeField] private float freezeThreshold = 0.15f;
        [Tooltip("Temperature above which ice melts to water. Sanitized to >= freezeThreshold (its inverse). " +
                 "CP8j sets this EQUAL to freezeThreshold: anything above the freezing point melts, full " +
                 "stop. A gap between the two would be a band where ice is warmer than freezing yet still " +
                 "refuses to melt — ice that looks cold while sitting at a temperature that isn't. Equal is " +
                 "stable, not churny: freezing needs heat strictly BELOW the threshold, and melting is " +
                 "driven by heat ABOVE it, so exactly at the boundary neither fires.")]
        [Range(0f, 1f)]
        [SerializeField] private float meltThreshold = 0.15f;
        [Tooltip("Temperature below which steam condenses to water. Sanitized to <= boilThreshold (its " +
                 "inverse). Sits ABOVE neutral so steam condenses at room temperature.")]
        [Range(0f, 1f)]
        [SerializeField] private float condenseThreshold = 0.65f;
        [Tooltip("Temperature above which water boils to steam. Sanitized to >= condenseThreshold (its inverse).")]
        [Range(0f, 1f)]
        [SerializeField] private float boilThreshold = 0.85f;
        [Tooltip("Melt/boil conversion rates (fraction of the source ink per second).")]
        [Min(0f)]
        [SerializeField] private float meltRate = 1f;
        [Min(0f)]
        [SerializeField] private float boilRate = 1f;
        [Tooltip("Fraction of local steam condensed back into water per second when below " +
                 "condenseThreshold. Deliberately MUCH gentler than melt/boil: cooling steam should " +
                 "linger and drizzle out only a little water at a time, not collapse into a puddle the " +
                 "instant it drops below the threshold. This is a rate, not a gate — steam still " +
                 "condenses whenever it is cold enough, just slowly.")]
        [Min(0f)]
        [SerializeField] private float condenseRate = 0.15f;
        [Tooltip("Fraction of local water frozen to ice per second when below freezeThreshold.")]
        [Min(0f)]
        [SerializeField] private float freezeRate = 0.4f;
        [Tooltip("Latent heat consumed per unit of ice melted. CP8l cut this from 0.5 so deposited heat " +
                 "melts ~3x more ice: melt is capped by excess/heatCost, so a lower cost means ice keeps " +
                 "melting from heat already delivered instead of needing a constant fire stream. Ambient " +
                 "warmth alone now thaws a unit of ice in ~37s rather than ~124s.")]
        [Min(0f)]
        [SerializeField] private float meltHeatCost = 0.10f;
        [Min(0f)]
        [SerializeField] private float boilHeatCost = 0.5f;
        [Tooltip("Latent heat released per unit condensed. Kept 0 in CP5 to avoid condense->heat->boil feedback.")]
        [Min(0f)]
        [SerializeField] private float condenseHeatRelease = 0f;
        [Tooltip("Heat removed per unit of water that FREEZES into ice — the one-shot chill of ice " +
                 "FORMING. This is what makes ice a cold source when it appears (painted ice stamps the " +
                 "min temperature directly; grown/frozen ice cools through this). It scales with how " +
                 "much actually converted, so ice that already exists — with no water left to freeze — " +
                 "cools nothing. Ice is deliberately NOT a continuous cold emitter the way fire is a " +
                 "continuous heat source. CP8k cut this from 1.0: every thermal transition removes heat " +
                 "and none returns it, so a water->ice->water round trip destroyed 1.5 units of heat and " +
                 "put the matter back where it started — a refrigerator that dragged the field to frozen.")]
        [Min(0f)]
        [SerializeField] private float freezeHeatCost = 0.1f;
        [Tooltip("Fuel-like fire (CP7b): fire burned per unit of heat ACTUALLY emitted. 0 = add-only " +
                 "(fire emits heat forever and only fades via its own dissipation). Only applies while " +
                 "thermal interactions are enabled — that pass then owns fire->heat emission.")]
        [Min(0f)]
        [SerializeField] private float fireHeatFuelCost = 0f;

        [Tooltip("Temperature above which plant SPONTANEOUSLY COMBUSTS from ambient heat alone.\n\n" +
                 "CP8e set this to 0.98 to keep it rare. CP8l lowered it to 0.75 because 0.98 turned out " +
                 "to be UNREACHABLE, not merely rare: a plant cell beside a max-heat fire converges to " +
                 "its neighbour average, (1.0 + 0.5*3)/4 = 0.625, and can never climb to 0.98 by " +
                 "conduction — so heat-only ignition was dead code. 0.75 is reachable when plant is " +
                 "genuinely well-surrounded by fire, yet still far above ordinary warmth (ambient 0.5), " +
                 "so plant never combusts just from a warm room.\n\n" +
                 "Fire catching ADJACENT plant is a different mechanism and is NOT gated by this: that is " +
                 "the Fire x Plant CONTACT reaction authored in OrganicGroup.asset.")]
        [Range(0f, 1f)]
        [SerializeField] private float plantIgnitionThreshold = 0.75f;
        [Tooltip("Fraction of local plant converted to fire per second once above the ignition temperature.")]
        [Min(0f)]
        [SerializeField] private float plantIgnitionRate = 0.5f;
        [Tooltip("Heat consumed per unit of plant burned (endothermic pyrolysis). Also bounds how much " +
                 "plant a hot cell can ignite in one step.")]
        [Min(0f)]
        [SerializeField] private float plantIgnitionHeatCost = 0.25f;

        [Tooltip("Temperature below which fire GOES OUT. Fire is REMOVED outright (a sink), not converted " +
                 "— a guttering flame does not leave smoke or a puddle behind. Fire heats its own cell, so " +
                 "a healthy flame holds itself above this and is unaffected; what this culls is fire that " +
                 "has drifted somewhere cold. Deliberately heat-neutral: fire dying must not itself chill " +
                 "the cell, or it would just be another heat sink.\n\n" +
                 "CP8l lowered this from 0.85. A plant cell beside a max-heat fire settles at 0.625, so a " +
                 "0.85 sink was EXTINGUISHING fire as it spread into plant — before it could establish " +
                 "and heat its own cell. Fire was strangling itself, which is why plant only smouldered. " +
                 "This MUST stay below what a fire-adjacent cell reaches (~0.625), yet above room " +
                 "temperature (0.5) so fire adrift in the cold still goes out.")]
        [Range(0f, 1f)]
        [SerializeField] private float fireSinkThreshold = 0.6f;
        [Tooltip("Fraction of local fire extinguished per second while below fireSinkThreshold. High = " +
                 "cold fire dies fast, which is the point.")]
        [Min(0f)]
        [SerializeField] private float fireSinkRate = 4f;

        [Header("Black Body Ink (Fallback)")]
        [SerializeField] private bool enableBlackBodyClearingFallback = false;
        [Range(0f, 1f)]
        [SerializeField] private float blackBodyThresholdFallback = 0.5f;
        [Range(0f, 0.2f)]
        [SerializeField] private float blackBodyClearingRateFallback = 0.05f;

        [Header("Ink Properties")]
        [SerializeField] private InkTypeDef[] inkDefinitions = new InkTypeDef[10];

        [Header("Particle Rendering")]
        [SerializeField] private ComputeShader particleToColorCompute;
        [SerializeField] private ComputeShader particleChannelSplatCompute;
        [SerializeField] private bool useParticleRenderPass = false;

        [Header("Update Rate")]
        [SerializeField] private int simulationUpdateRate = 0;

        [Header("Substepping (CFL stability)")]
        [Tooltip("Max real seconds advanced by a single solver step. When a frame's real dt exceeds " +
                 "this, the step is split into N equal substeps so per-step advection stays stable / " +
                 "low-Courant at low framerates. <= 0 disables substepping (single step per frame).")]
        [SerializeField] private float maxSubstepDt = 0.016f;
        [Tooltip("Hard cap on substeps per frame, bounding worst-case cost on a hitched frame.")]
        [SerializeField] private int maxSubsteps = 8;

        [Header("Debug / Diagnostics")]
        [Tooltip("Inject a constant circular force every frame to test the velocity pipeline. Enable displayVelocity to visualize.")]
        [SerializeField] private bool debugInjectTestForce = false;
        [Tooltip("Log force injection diagnostics every 60 frames.")]
        [SerializeField] private bool debugLogForces = false;

        [Header("Runtime Selection")]
        [SerializeField] private int currentInkType = 0;

        // CP8w: upper bound is now ColdSourceInkIndex (10), not 9 — the selection range covers the ten
        // real inks PLUS the ColdAir temperature probe. Painting routes on the index, so widening this
        // clamp is what makes ColdAir reachable from the brush and from right-mouse emitters.
        public int CurrentInkType
        {
            get => currentInkType;
            set => currentInkType = Mathf.Clamp(value, 0, SimulationContext.ColdSourceInkIndex);
        }

        /// <summary>
        /// CP8z: obstacle-heat model. 0 = strict conduction-only (default), 1 = legacy CP8q advective.
        /// Writes the serialized field (so it survives the per-frame SetConstants push) AND the live ctx,
        /// so the Fire-vs-Ice harness can flip models between runs without a scene edit.
        /// </summary>
        public int HeatObstacleMode
        {
            get => thermalObstacleHeatMode;
            set
            {
                thermalObstacleHeatMode = Mathf.Clamp(value, 0, 1);
                if (ctx != null) ctx.HeatObstacleMode = thermalObstacleHeatMode;
            }
        }

        /// <summary>
        /// Fire-replacing-plant reaction impulse. Defaults enabled for live playtest of fire spread.
        /// Deterministic callers (InkScenarioRunner / baseline tests) can set this false before stepping
        /// to keep byte-identical, concentration-only reaction behavior — SyncContextFromFields() runs
        /// at the top of every StepSimulation, so the change propagates on the next step.
        /// </summary>
        public bool EnableReactionImpulse { get => enableReactionImpulse; set => enableReactionImpulse = value; }

        // ── Modules ─────────────────────────────────────────────────────────
        private SimulationContext ctx;
        private SimulationResources resources;
        private OperationQueue operationQueue;
        private FluidSolver fluidSolver;
        private SimulationDisplay display;

        // ── Frame state ─────────────────────────────────────────────────────
        private Stopwatch stopwatch = new Stopwatch();
        private float lastFrameMs;
        private float simAccumulator;
        private bool simRanThisFrame;

        private static bool HasRequiredFluidKernels(ComputeShader shader)
        {
            if (shader == null) return false;

            return shader.HasKernel("Advection")
                && shader.HasKernel("Diffusion")
                && shader.HasKernel("Divergence")
                && shader.HasKernel("Pressure")
                && shader.HasKernel("SubtractGradient")
                && shader.HasKernel("Vorticity")
                && shader.HasKernel("VorticityConfinement")
                && shader.HasKernel("AddForce")
                && shader.HasKernel("AddDensity")
                && shader.HasKernel("Clear");
        }

        #if UNITY_EDITOR
        private static void ForceReimportCompute(ComputeShader shader)
        {
            if (shader == null) return;

            string path = AssetDatabase.GetAssetPath(shader);
            if (string.IsNullOrEmpty(path)) return;

            AssetDatabase.ImportAsset(
                path,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }
        #endif

        private ComputeShader ResolveFluidCompute()
        {
            #if UNITY_EDITOR
            ForceReimportCompute(fluidCompute);
            #endif

            if (HasRequiredFluidKernels(fluidCompute))
                return fluidCompute;

            if (fluidCompute != null)
                Debug.LogWarning($"[SimDriver] Assigned compute shader '{fluidCompute.name}' is missing required fluid kernels.");

            #if UNITY_EDITOR
            foreach (string path in FluidComputeCandidatePaths)
            {
                var candidate = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (candidate == null) continue;

                ForceReimportCompute(candidate);

                if (HasRequiredFluidKernels(candidate))
                {
                    Debug.Log($"[SimDriver] Auto-assigned Fluids.compute from '{path}'.");
                    return candidate;
                }

                Debug.LogWarning($"[SimDriver] Candidate shader at '{path}' is missing required fluid kernels.");
            }
            #endif

            var resourcesCandidate = Resources.Load<ComputeShader>("Fluids");
            if (HasRequiredFluidKernels(resourcesCandidate))
            {
                Debug.Log("[SimDriver] Auto-assigned Fluids.compute from Resources/Fluids.");
                return resourcesCandidate;
            }

            Debug.LogError("[SimDriver] No valid Fluids.compute found. Simulation will run in test/static mode.");
            return fluidCompute;
        }

        private void Start()
        {
            fluidCompute = ResolveFluidCompute();

            ctx = new SimulationContext();
            resources = new SimulationResources();

            SyncContextFromFields();

            // Compute effective display resolution
            ctx.EffectiveDisplayRes = displayResolution > 0
                ? displayResolution
                : Mathf.Max(32, Mathf.RoundToInt(resolution * displayResolutionScale));

            resources.Allocate(ctx);

            operationQueue = new OperationQueue(ctx);
            fluidSolver = new FluidSolver(ctx, operationQueue);
            display = new SimulationDisplay(ctx);

            bool kernelsFound = true;
            if (ctx.FluidCompute != null)
            {
                kernelsFound = fluidSolver.InitializeKernels();
                if (!kernelsFound)
                {
                    Debug.LogWarning("[SimDriver] Missing required kernels. Running in test pattern mode.");
                }
            }

            operationQueue.InitializeKernels();

            if (kernelsFound && ctx.FluidCompute != null)
            {
                fluidSolver.SetConstants();
                fluidSolver.ClearAll();

                if (ctx.FluidKernelUpdateObstacles != 0)
                    fluidSolver.InitializeObstacles();

                // Seed initial density so there's something visible on startup.
                InjectDensity(new Vector2(0.5f, 0.5f), Color.white, 0);
                InjectDensity(new Vector2(0.3f, 0.7f), new Color(1f, 0.5f, 0f, 1f), 0);
                InjectDensity(new Vector2(0.7f, 0.3f), new Color(0f, 0.5f, 1f, 1f), 0);
            }

            // Register with ServiceLocator
            var locator = ServiceLocator.Instance;
            if (locator != null)
                locator.RegisterService(this);

            Debug.Log($"[SimDriver] Initialized {resolution}x{resolution} simulation | " +
                      $"forceStrength={forceStrength}, radius={forceRadius}, " +
                      $"pressureIter={pressureIterations}, diffusionIter={diffusionIterations}, " +
                      $"vorticity={vorticity}, densityAmt={densityAmount}, " +
                      $"redBlack={useRedBlackSolver}");
        }

        /// <summary>
        /// Copies all [SerializeField] values into the shared SimulationContext.
        /// Called once in Start() and at the top of each frame (to pick up Inspector changes).
        /// </summary>
        private void SyncContextFromFields()
        {
            ctx.Resolution = resolution;
            ctx.Timestep = timestep;
            // Real-time this step represents: fixed timestep under external/deterministic control
            // (reproducible) or a fixed sim rate; otherwise the (clamped) real frame delta so
            // dt-normalized decays stay frame-rate independent during live play.
            ctx.FrameDeltaTime = ExternalStepControl ? timestep
                : (simulationUpdateRate > 0 ? 1f / simulationUpdateRate : Mathf.Min(Time.deltaTime, 0.05f));
            ctx.Viscosity = viscosity;
            ctx.VorticityStrength = vorticity;
            ctx.Dissipation = dissipation;
            ctx.VelocityDissipation = velocityDissipation;
            ctx.PressureIterations = pressureIterations;
            ctx.DiffusionIterations = diffusionIterations;
            ctx.UseRedBlackSolver = useRedBlackSolver;
            ctx.InjectionForce = injectionForce;
            ctx.DensityAmount = densityAmount;
            ctx.ForceRadius = forceRadius;
            ctx.ForceStrength = forceStrength;

            ctx.FluidCompute = fluidCompute;
            ctx.StampCompute = stampCompute;
            ctx.StampParticlesCompute = stampParticlesCompute;
            ctx.BatchedStampCompute = batchedStampCompute;
            ctx.BatchedMaskCompute = batchedMaskCompute;
            ctx.BatchedInjectionCompute = batchedInjectionCompute;
            ctx.ParticleToColorCompute = particleToColorCompute;
            ctx.ParticleChannelSplatCompute = particleChannelSplatCompute;
            ctx.InkInteractionsCompute = inkInteractionsCompute;
            ctx.ThermalInteractionsCompute = thermalInteractionsCompute;

            ctx.UseParticleSimulation = useParticleSimulation;
            ctx.UseParticleAdvection = useParticleAdvection;
            ctx.UseParticleDissipation = useParticleDissipation;
            ctx.UseParticleDiffusion = useParticleDiffusion;
            ctx.MaxParticleSimResolution = maxParticleSimResolution;
            ctx.UseParticleRenderPass = useParticleRenderPass;

            ctx.UseBatchedDensityInjection = useBatchedDensityInjection;
            ctx.UseBatchedStamping = useBatchedStamping;
            ctx.UseBatchedMasks = useBatchedMasks;

            ctx.UseInkInteractions = useInkInteractions;
            ctx.InkInteractionsDebugMode = inkInteractionsDebugMode;
            ctx.AffinityGroups = affinityGroups;
            ctx.InkDefinitions = inkDefinitions;

            ctx.EnableReactionImpulse = enableReactionImpulse;
            ctx.ReactionImpulseStrength = reactionImpulseStrength;
            ctx.ReactionImpulseMax = reactionImpulseMax;
            ctx.ReactionImpulseCurlBias = reactionImpulseCurlBias;
            ctx.ReactionImpulseExpansionBias = reactionImpulseExpansionBias;
            ctx.ReactionImpulseGain = reactionImpulseGain;

            ctx.ThermalDissipationHalfLife = thermalDissipationHalfLife;
            ctx.ThermalDiffusion = thermalDiffusion;
            ctx.ThermalDiffusionSolid = thermalDiffusionSolid;
            ctx.ThermalSolidThresholdIce = thermalSolidThresholdIce;
            ctx.ThermalSolidPermeability = thermalSolidPermeability;
            ctx.HeatObstacleMode = Mathf.Clamp(thermalObstacleHeatMode, 0, 1);
            ctx.QuenchCoolingPerUnit = quenchCoolingPerUnit;
            ctx.NeutralTemperature = neutralTemperature;
            ctx.MinTemperature = minTemperature;
            ctx.SteamInjectionTemperature = steamInjectionTemperature;
            ctx.EnableHeatSources = enableHeatSources;
            ctx.FireHeatEmissionRate = fireHeatEmissionRate;
            ctx.MaxHeat = maxHeat;

            ctx.EnableThermalInteractions = enableThermalInteractions;
            ctx.FreezeThreshold = freezeThreshold;
            ctx.CondenseThreshold = condenseThreshold;
            ctx.MeltThreshold = meltThreshold;
            ctx.BoilThreshold = boilThreshold;
            ctx.MeltRate = meltRate;
            ctx.BoilRate = boilRate;
            ctx.CondenseRate = condenseRate;
            ctx.FreezeRate = freezeRate;
            ctx.MeltHeatCost = meltHeatCost;
            ctx.BoilHeatCost = boilHeatCost;
            ctx.CondenseHeatRelease = condenseHeatRelease;
            ctx.FreezeHeatCost = freezeHeatCost;
            ctx.FireSinkThreshold = fireSinkThreshold;
            ctx.FireSinkRate = fireSinkRate;
            ctx.FireHeatFuelCost = fireHeatFuelCost;
            ctx.PlantIgnitionThreshold = plantIgnitionThreshold;
            ctx.PlantIgnitionRate = plantIgnitionRate;
            ctx.PlantIgnitionHeatCost = plantIgnitionHeatCost;

            ctx.EnableBlackBodyClearingFallback = enableBlackBodyClearingFallback;
            ctx.BlackBodyThresholdFallback = blackBodyThresholdFallback;
            ctx.BlackBodyClearingRateFallback = blackBodyClearingRateFallback;

            ctx.DensityStampShader = densityStampShader;

            ctx.DisplayVelocity = displayVelocity;
            ctx.UseParticleDisplay = useParticleDisplay;
            ctx.UseCpuCreatureComposite = useCpuCreatureComposite;
            ctx.UseGradientRendering = useGradientRendering;
            ctx.DisplayRenderer = displayRenderer;
            ctx.GradientPreset = gradientPreset;
            ctx.GradientMaterial = gradientMaterial;

            ctx.DebugZeroPressure = debugZeroPressure;
            ctx.DebugZeroVelocity = debugZeroVelocity;
            ctx.DebugSkipAir = debugSkipAir;
            ctx.MeasurePerformance = measurePerformance;
        }

        private void Update()
        {
            if (measurePerformance) stopwatch.Restart();

            SyncContextFromFields();
            UpdateHotkeys();

            // Debug: inject a circular force at center every frame to test pipeline.
            // The force vector (0.1, 0.05) in UV-space → ~100px at 1024 res → clearly visible flow.
            // Suppressed under ExternalStepControl so automated scenarios stay uncontaminated.
            if (!ExternalStepControl && debugInjectTestForce && ctx.FluidCompute != null)
            {
                InjectForce(new Vector2(0.5f, 0.5f), new Vector2(0.1f, 0.05f));
                InjectDensity(new Vector2(0.5f, 0.5f), Color.red, 0);
            }

            // Simulation update rate throttling
            simRanThisFrame = false;
            if (!ExternalStepControl)
            {
                simAccumulator += Time.deltaTime;
                float targetStep = simulationUpdateRate > 0 ? 1f / simulationUpdateRate : 0f;
                if (targetStep <= 0f || simAccumulator >= targetStep)
                {
                    // ctx.FrameDeltaTime was set in SyncContextFromFields to the clamped real frame
                    // dt (or 1/rate). Substep it so per-step advection stays stable at low framerates.
                    SimulateFrameSubstepped(ctx.FrameDeltaTime);
                    simRanThisFrame = true;
                    if (targetStep > 0f)
                        simAccumulator = Mathf.Max(0f, simAccumulator - targetStep);
                }
            }

            if (measurePerformance)
                lastFrameMs = (float)stopwatch.Elapsed.TotalMilliseconds;
        }

        private void LateUpdate()
        {
            if (simRanThisFrame || simulationUpdateRate <= 0)
                display.UpdateDisplay();
        }

        private void SimulateFrame()
        {
            operationQueue.DebugLogForces = debugLogForces;
            operationQueue.ProcessPending();

            var sw = measurePerformance ? stopwatch : null;
            fluidSolver.Step(sw);
        }

        /// <summary>
        /// Advances the sim by <paramref name="frameDt"/> real seconds, split into N ≤ maxSubsteps equal
        /// substeps of ≤ maxSubstepDt each so per-step advection stays low-Courant at low framerates.
        /// Queued injections are applied once at frame start; the dt-normalized decays compose exactly
        /// (pow(r, frameDt/N)^N == pow(r, frameDt)).
        /// STABILITY: the dt-normalized velocity impulses (vorticity confinement, force injection) scale
        /// by (subDt / timestep). Keeping maxSubstepDt ≤ the solver timestep makes that ratio ≤ 1, so an
        /// energy-pumping impulse never exceeds its tuned per-step magnitude even at low fps. Raising
        /// maxSubstepDt above the timestep (or disabling substeps) can let those impulses spike — keep
        /// maxSubstepDt == timestep unless you know what you're doing.
        /// </summary>
        private void SimulateFrameSubstepped(float frameDt)
        {
            operationQueue.DebugLogForces = debugLogForces;
            operationQueue.ProcessPending();

            int n = 1;
            if (maxSubstepDt > 0f && frameDt > maxSubstepDt)
                n = Mathf.CeilToInt(frameDt / maxSubstepDt);
            n = Mathf.Clamp(n, 1, Mathf.Max(1, maxSubsteps));

            float subDt = frameDt / n;
            var sw = measurePerformance ? stopwatch : null;
            for (int i = 0; i < n; i++)
            {
                ctx.FrameDeltaTime = subDt;
                fluidSolver.Step(sw);
            }
        }

        // ── Deterministic external control (automated scenario / dataset harness) ──
        /// <summary>
        /// When true, Update() does not auto-step the simulation. Call <see cref="StepSimulation"/>
        /// to advance deterministically. Used by the scenario runner / training-data capture.
        /// </summary>
        public bool ExternalStepControl { get; set; }

        /// <summary>Advances the simulation exactly one fixed-timestep frame (drains the op queue, runs the solver).</summary>
        public void StepSimulation()
        {
            if (ctx == null || ctx.FluidCompute == null) return;
            SyncContextFromFields();
            SimulateFrame();
        }

        /// <summary>
        /// Advances one step but with an explicit real-time delta (<paramref name="frameDtOverride"/>)
        /// for the dt-normalized transport/decay paths, while keeping the fixed solver Timestep.
        /// Lets callers emulate variable framerates (validation) and is the building block for dt
        /// clamping/substepping. A non-positive override falls back to the fixed-timestep step.
        /// </summary>
        public void StepSimulation(float frameDtOverride)
        {
            if (ctx == null || ctx.FluidCompute == null) return;
            SyncContextFromFields();
            if (frameDtOverride > 0f) ctx.FrameDeltaTime = frameDtOverride;
            SimulateFrame();
        }

        /// <summary>Clears all simulation state (density, velocity, pressure, particles) to zero.</summary>
        public void ResetSimulation()
        {
            fluidSolver?.ClearAll();
        }

        /// <summary>Recomposites the display RT from the current sim state (call after manual stepping before capture).</summary>
        public void RefreshDisplay()
        {
            display?.UpdateDisplay();
        }

        /// <summary>Sets a global solver tunable by name (for parameter sweeps / scenario runs). Applied on next sync.</summary>
        public void SetTunable(string key, float value)
        {
            switch (key)
            {
                case "viscosity": viscosity = value; break;
                case "vorticity": vorticity = value; break;
                case "dissipation": dissipation = value; break;
                case "velocityDissipation": velocityDissipation = value; break;
                case "timestep": timestep = value; break;
                case "reactionImpulseStrength": reactionImpulseStrength = value; break;
                case "reactionImpulseMax": reactionImpulseMax = value; break;
                case "reactionImpulseCurlBias": reactionImpulseCurlBias = value; break;
                case "reactionImpulseExpansionBias": reactionImpulseExpansionBias = value; break;
                case "reactionImpulseGain": reactionImpulseGain = value; break;
                default: Debug.LogWarning("[SimDriver] Unknown tunable: " + key); break;
            }
        }

        /// <summary>Toggle the display between ink (gradient) and the raw velocity field. For scenario capture.</summary>
        public void SetDisplayVelocity(bool value) => displayVelocity = value;

        // ── ISimulationWriter ───────────────────────────────────────────────

        public void InjectForce(Vector2 position, Vector2 force)
        {
            if (ctx.FluidCompute == null) return;
            operationQueue.EnqueueForceInjection(position, force);
        }

        void ISimulationWriter.InjectDensity(Vector2 position, Color color, int inkTypeIndex)
        {
            InjectDensity(position, color, inkTypeIndex);
        }

        public void InjectDensity(Vector2 position, Color color, int inkTypeIndex = 0)
        {
            // CP8w: ColdAir is intercepted FIRST — before every guard below. Two reasons this ordering
            // is load-bearing:
            //   1. The Mathf.Clamp(inkTypeIndex, 0, 9) further down would silently turn index 10 into
            //      ICE, seeding exactly the mass this feature exists to avoid. Nothing about that
            //      failure would be visible: you would paint "cold air" and get ice.
            //   2. The density/colour guards are irrelevant to a temperature probe. ColdAir must still
            //      cool with DensityAmount at 0, and must not require a Density buffer at all.
            if (SimulationContext.IsColdSource(inkTypeIndex))
            {
                if (ctx.FluidCompute == null) return;
                operationQueue.EnqueueHeatInjection(position, ctx.SanitizedColdSourceTemperature);
                return;   // heat only: no density, no particles, no obstacle, no ice
            }

            if (ctx.FluidCompute == null || ctx.Density == null) return;

            float colorIntensity = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            if (colorIntensity <= 0f || ctx.DensityAmount <= 0f) return;

            int validIndex = Mathf.Clamp(inkTypeIndex, 0, 9);
            operationQueue.EnqueueDensityInjection(position, color, validIndex);
        }

        public void StampDensity(Vector2 uvPosition, Texture2D stamp, float densityMultiplier, bool useColorOverride, Color overrideColor)
        {
            if (ctx.Density == null || stamp == null) return;
            operationQueue.EnqueueDensityStamp(uvPosition, stamp, densityMultiplier, useColorOverride, overrideColor);
        }

        public void ClearDensityWithMask(Vector2 uvPosition, Texture2D mask, float blackLuminanceThreshold = 0.2f)
        {
            if (ctx.Density == null || mask == null) return;
            operationQueue.EnqueueClearDensityMask(uvPosition, mask, blackLuminanceThreshold);
        }

        /// <summary>
        /// Stamps obstacles and clears particles at obstacle positions.
        /// Kept inline (CPU-heavy, doesn't fit cleanly in a module).
        /// </summary>
        public void StampObstacles(Vector2 uvPosition, Texture2D stamp)
        {
            if (ctx.Obstacles == null || stamp == null || ctx.ParticlesBuffer == null) return;

            int stampWidth = stamp.width;
            int stampHeight = stamp.height;
            Color[] stampPixels = stamp.GetPixels();

            int centerX = Mathf.RoundToInt(uvPosition.x * resolution);
            int centerY = Mathf.RoundToInt(uvPosition.y * resolution);
            int startX = centerX - stampWidth / 2;
            int startY = centerY - stampHeight / 2;

            RenderTexture.active = ctx.Obstacles;
            Texture2D tempObstacles = new Texture2D(resolution, resolution, TextureFormat.RFloat, false);
            tempObstacles.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            tempObstacles.Apply();
            Color[] obstaclePixels = tempObstacles.GetPixels();
            RenderTexture.active = null;

            int particleCount = resolution * resolution;
            bool particlesModified = false;

            if (ctx.GpuPromotesHalf)
            {
                var particles = new SimulationDisplay_iparticle_gpu[particleCount];
                ctx.ParticlesBuffer[ctx.ParticleReadIndex].GetData(particles);
                for (int y = 0; y < stampHeight; y++)
                    for (int x = 0; x < stampWidth; x++)
                    {
                        int targetX = startX + x;
                        int targetY = startY + y;
                        if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution) continue;
                        Color sc = stampPixels[y * stampWidth + x];
                        if (sc.a < 0.01f) continue;
                        int idx = targetY * resolution + targetX;
                        obstaclePixels[idx] = new Color(1f, 0, 0, 0);
                        particles[idx] = new SimulationDisplay_iparticle_gpu();
                        particlesModified = true;
                    }
                if (particlesModified)
                    ctx.ParticlesBuffer[ctx.ParticleWriteIndex].SetData(particles);
            }
            else
            {
                var particles = new iparticle[particleCount];
                ctx.ParticlesBuffer[ctx.ParticleReadIndex].GetData(particles);
                for (int y = 0; y < stampHeight; y++)
                    for (int x = 0; x < stampWidth; x++)
                    {
                        int targetX = startX + x;
                        int targetY = startY + y;
                        if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution) continue;
                        Color sc = stampPixels[y * stampWidth + x];
                        if (sc.a < 0.01f) continue;
                        int idx = targetY * resolution + targetX;
                        obstaclePixels[idx] = new Color(1f, 0, 0, 0);
                        particles[idx] = new iparticle();
                        particlesModified = true;
                    }
                if (particlesModified)
                    ctx.ParticlesBuffer[ctx.ParticleWriteIndex].SetData(particles);
            }

            tempObstacles.SetPixels(obstaclePixels);
            tempObstacles.Apply();
            Graphics.Blit(tempObstacles, ctx.Obstacles);
            Destroy(tempObstacles);
        }

        /// <summary>
        /// Stamps creature ink texture (non-persistent, composited for display only).
        /// Kept inline (CPU-heavy, doesn't fit cleanly in a module).
        /// </summary>
        public void StampTexture(Vector2 uvPosition, Texture2D stamp, float scale = 1.0f)
        {
            if (ctx.CreatureInkBuffer == null || stamp == null) return;

            int stampWidth = stamp.width;
            int stampHeight = stamp.height;
            Color[] stampPixels = stamp.GetPixels();

            int centerX = Mathf.RoundToInt(uvPosition.x * resolution);
            int centerY = Mathf.RoundToInt(uvPosition.y * resolution);
            int startX = centerX - stampWidth / 2;
            int startY = centerY - stampHeight / 2;

            Texture2D tempTex = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
            Color[] tempPixels = new Color[resolution * resolution];

            RenderTexture.active = ctx.CreatureInkBuffer;
            Texture2D currentCreature = new Texture2D(resolution, resolution, TextureFormat.RGBAHalf, false);
            currentCreature.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            currentCreature.Apply();
            Color[] currentPixels = currentCreature.GetPixels();
            RenderTexture.active = null;

            System.Array.Copy(currentPixels, tempPixels, currentPixels.Length);

            for (int y = 0; y < stampHeight; y++)
            {
                for (int x = 0; x < stampWidth; x++)
                {
                    int targetX = startX + x;
                    int targetY = startY + y;
                    if (targetX < 0 || targetX >= resolution || targetY < 0 || targetY >= resolution) continue;

                    Color stampColor = stampPixels[y * stampWidth + x];
                    if (stampColor.a < 0.01f) continue;

                    int targetIdx = targetY * resolution + targetX;
                    tempPixels[targetIdx] += stampColor;
                }
            }

            tempTex.SetPixels(tempPixels);
            tempTex.Apply();
            Graphics.Blit(tempTex, ctx.CreatureInkBuffer);
            Destroy(tempTex);
            Destroy(currentCreature);
        }

        // ── ISimulationReader ───────────────────────────────────────────────

        public RenderTexture GetDensityTexture()
        {
            if (ctx == null) return null;
            if (ctx.UseParticleRenderPass && ctx.DisplayRT != null) return ctx.DisplayRT;
            if (ctx.Density != null && !ctx.UseParticleDisplay) return ctx.Density.Read;
            return null;
        }

        public RenderTexture GetVelocityTexture() => ctx?.Velocity?.Read;
        public RenderTexture GetDisplayTexture() => ctx?.DisplayRT;
        public RenderTexture GetObstacleTexture() => ctx?.Obstacles;

        public ComputeBuffer GetParticleBuffer()
        {
            if (ctx?.ParticlesBuffer == null || ctx.ParticlesBuffer.Length == 0) return null;
            int readIndex = Mathf.Clamp(ctx.ParticleReadIndex, 0, ctx.ParticlesBuffer.Length - 1);
            return ctx.ParticlesBuffer[readIndex];
        }

        public float GetLastFrameMs() => lastFrameMs;
        public (float advection, float diffusion, float pressure, float projection, float vorticity) GetDetailedTimings()
        {
            if (fluidSolver == null) return (0f, 0f, 0f, 0f, 0f);
            return (fluidSolver.AdvectionMs, fluidSolver.DiffusionMs,
                fluidSolver.PressureMs, fluidSolver.ProjectionMs, fluidSolver.VorticityMs);
        }

        // ── Service accessors ───────────────────────────────────────────────

        public ISimulationService AsService() => this;
        public ISimulationReader AsReader() => this;
        public ISimulationWriter AsWriter() => this;

        // ── Hotkeys ─────────────────────────────────────────────────────────

        private void UpdateHotkeys()
        {
            int hotkey = GetInkHotkeyIndex();
            if (hotkey >= 0)
            {
                currentInkType = hotkey;
                // CP8w: name it, not just the index — "10" alone reads as a bug rather than a feature.
                string label = SimulationContext.IsColdSource(currentInkType)
                    ? "ColdAir (temperature probe, no mass)"
                    : ((InkTypeId)currentInkType).ToString();
                Debug.Log($"[SimDriver] Switched to ink type: {currentInkType} ({label})");
            }

            // V key: toggle velocity display (shows raw velocity texture, bypasses gradient)
            if (Keyboard.current != null && Keyboard.current.vKey.wasPressedThisFrame)
            {
                displayVelocity = !displayVelocity;
                Debug.Log($"[SimDriver] displayVelocity = {displayVelocity}");
            }
        }

        private static int GetInkHotkeyIndex()
        {
            if (Keyboard.current != null)
            {
                if (Keyboard.current.digit1Key.wasPressedThisFrame || Keyboard.current.numpad1Key.wasPressedThisFrame) return 0;
                if (Keyboard.current.digit2Key.wasPressedThisFrame || Keyboard.current.numpad2Key.wasPressedThisFrame) return 1;
                if (Keyboard.current.digit3Key.wasPressedThisFrame || Keyboard.current.numpad3Key.wasPressedThisFrame) return 2;
                if (Keyboard.current.digit4Key.wasPressedThisFrame || Keyboard.current.numpad4Key.wasPressedThisFrame) return 3;
                if (Keyboard.current.digit5Key.wasPressedThisFrame || Keyboard.current.numpad5Key.wasPressedThisFrame) return 4;
                if (Keyboard.current.digit6Key.wasPressedThisFrame || Keyboard.current.numpad6Key.wasPressedThisFrame) return 5;
                if (Keyboard.current.digit7Key.wasPressedThisFrame || Keyboard.current.numpad7Key.wasPressedThisFrame) return 6;
                if (Keyboard.current.digit8Key.wasPressedThisFrame || Keyboard.current.numpad8Key.wasPressedThisFrame) return 7;
                if (Keyboard.current.digit9Key.wasPressedThisFrame || Keyboard.current.numpad9Key.wasPressedThisFrame) return 8;
                if (Keyboard.current.digit0Key.wasPressedThisFrame || Keyboard.current.numpad0Key.wasPressedThisFrame) return 9;

                // CP8w: C = ColdAir, the temperature-only probe. The digit row is full (1..0 -> 0..9),
                // so it needs a letter. C was verified unbound across the project — the only existing
                // letter bindings are R, V and Space.
                if (Keyboard.current.cKey.wasPressedThisFrame) return SimulationContext.ColdSourceInkIndex;
            }

            #if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1)) return 0;
            if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) return 1;
            if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) return 2;
            if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4)) return 3;
            if (Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5)) return 4;
            if (Input.GetKeyDown(KeyCode.Alpha6) || Input.GetKeyDown(KeyCode.Keypad6)) return 5;
            if (Input.GetKeyDown(KeyCode.Alpha7) || Input.GetKeyDown(KeyCode.Keypad7)) return 6;
            if (Input.GetKeyDown(KeyCode.Alpha8) || Input.GetKeyDown(KeyCode.Keypad8)) return 7;
            if (Input.GetKeyDown(KeyCode.Alpha9) || Input.GetKeyDown(KeyCode.Keypad9)) return 8;
            if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0)) return 9;
            #endif

            return -1;
        }

        // ── Cleanup ─────────────────────────────────────────────────────────

        private void OnDestroy()
        {
            resources?.Dispose(ctx);
        }

        private void OnGUI()
        {
            display?.DrawOnGUI(lastFrameMs, fluidSolver.AdvectionMs, fluidSolver.DiffusionMs,
                fluidSolver.PressureMs, fluidSolver.ProjectionMs, fluidSolver.VorticityMs);
        }
    }
}
