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
        //   and ice actually modulate the temperature AROUND them, not just their own cell.
        //
        // CP8a — these two were ONE field (`AmbientTemperature`) and must stay separate:
        //   NeutralTemperature: room temperature. The value heat RELAXES TOWARD, and what the heat
        //     field is initialised/cleared to. Water is the stable phase here.
        //   MinTemperature: the absolute clamp FLOOR. Must NOT be the neutral, or nothing could ever
        //     get colder than room temperature and ice could never form.
        public float ThermalDissipationHalfLife = 1000f;
        public float ThermalDiffusion = 0.05f;
        public float NeutralTemperature = 0.5f;
        public float MinTemperature = 0f;

        // Sanitized temperature bounds, guaranteeing min <= neutral <= max. These live on the context
        // (not just FluidSolver) because OperationQueue also needs them: injection heat stamping runs
        // during ProcessPending(), which is BEFORE FluidSolver.Step() uploads SetConstants() — so the
        // queue must upload the bounds itself or the clamp would use stale/zero uniforms.
        public float SanitizedMinTemperature => Mathf.Min(MinTemperature, MaxHeat);
        public float SanitizedMaxTemperature => Mathf.Max(SanitizedMinTemperature, MaxHeat);
        public float SanitizedNeutralTemperature =>
            Mathf.Clamp(NeutralTemperature, SanitizedMinTemperature, SanitizedMaxTemperature);

        /// <summary>
        /// The temperature an injection of this ink stamps into the heat field (CP8b).
        /// Fire injects at the ceiling, Water at the neutral/room baseline, Ice at the floor — so ice
        /// can create genuinely sub-neutral cold the moment it is painted. Every other ink returns
        /// false and leaves the heat field untouched.
        /// </summary>
        public bool TryGetInjectionTemperature(int inkTypeIndex, out float targetTemperature)
        {
            switch ((InkTypeId)inkTypeIndex)
            {
                case InkTypeId.Fire:  targetTemperature = SanitizedMaxTemperature;     return true;
                case InkTypeId.Water: targetTemperature = SanitizedNeutralTemperature; return true;
                case InkTypeId.Ice:   targetTemperature = SanitizedMinTemperature;     return true;
                default:              targetTemperature = 0f;                          return false;
            }
        }
        // Heat sources (CP3): fire emits heat (add-only, diagnostic — heat drives nothing yet).
        public bool EnableHeatSources = true;
        public float FireHeatEmissionRate = 1f;
        public float MaxHeat = 1f;

        // Thermal interactions (CP5): heat-driven LOCAL phase changes. Default OFF (opt-in) —
        // this is the first pass that alters ink state, so baseline stays unchanged until enabled.
        public bool EnableThermalInteractions = false;
        // CP8a thermal layout, placed around the NEUTRAL (room) temperature of 0.5:
        //   min 0 .. freeze .15 .. melt .35 .. [NEUTRAL .5] .. condense .65 .. boil .85 .. max 1
        // At neutral water is stable, ice melts, steam condenses. Condense sits ABOVE melt — required,
        // and legal because the baker validates per-inverse-pair, not with a global cold<=hot ladder.
        // Sanitized per-cycle before upload: freeze <= melt, condense <= boil.
        public float FreezeThreshold = 0.15f;
        public float MeltThreshold = 0.35f;
        public float CondenseThreshold = 0.65f;
        public float BoilThreshold = 0.85f;
        public float MeltRate = 1f;
        public float BoilRate = 1f;
        public float CondenseRate = 1f;
        public float FreezeRate = 1f;
        public float MeltHeatCost = 0.5f;
        public float BoilHeatCost = 0.5f;
        public float CondenseHeatRelease = 0f;
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
