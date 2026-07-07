using System.Diagnostics;
using UnityEngine;
using Magi.InkTools;
using Magi.InkTools.Simulation;
using Debug = UnityEngine.Debug;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Core physics dispatch sequence for the fluid simulation.
    /// Handles advection, particle sim, ink interactions, diffusion, vorticity,
    /// pressure projection, gradient subtraction, obstacle boundaries, and channel splat.
    /// The dispatch order is preserved exactly from the original SimDriver.
    /// </summary>
    public class FluidSolver
    {
        private readonly SimulationContext ctx;
        private readonly OperationQueue opQueue;

        // Performance timing
        public float AdvectionMs { get; private set; }
        public float DiffusionMs { get; private set; }
        public float PressureMs { get; private set; }
        public float ProjectionMs { get; private set; }
        public float VorticityMs { get; private set; }

        public FluidSolver(SimulationContext context, OperationQueue operationQueue)
        {
            ctx = context;
            opQueue = operationQueue;
        }

        /// <summary>
        /// Initialize fluid compute kernel indices. Returns false if required kernels are missing.
        /// </summary>
        public bool InitializeKernels()
        {
            if (ctx.FluidCompute == null) return false;

            bool kernelsFound = true;
            try
            {
                ctx.FluidKernelAdvection = ctx.FluidCompute.FindKernel("Advection");
                ctx.FluidKernelDiffusion = ctx.FluidCompute.FindKernel("Diffusion");
                ctx.FluidKernelDivergence = ctx.FluidCompute.FindKernel("Divergence");
                ctx.FluidKernelPressure = ctx.FluidCompute.FindKernel("Pressure");
                ctx.FluidKernelSubtractGradient = ctx.FluidCompute.FindKernel("SubtractGradient");
                ctx.FluidKernelVorticity = ctx.FluidCompute.FindKernel("Vorticity");
                ctx.FluidKernelVorticityConfinement = ctx.FluidCompute.FindKernel("VorticityConfinement");
                ctx.FluidKernelAddForce = ctx.FluidCompute.FindKernel("AddForce");
                ctx.FluidKernelAddDensity = ctx.FluidCompute.FindKernel("AddDensity");
                ctx.FluidKernelClear = ctx.FluidCompute.FindKernel("Clear");

                try
                {
                    ctx.FluidKernelPressureRedBlack = ctx.FluidCompute.FindKernel("PressureRedBlack");
                    ctx.FluidKernelUpdateObstacles = ctx.FluidCompute.FindKernel("UpdateObstacles");
                    ctx.FluidKernelApplyObstacleBoundary = ctx.FluidCompute.FindKernel("ApplyObstacleBoundary");
                    ctx.FluidKernelAdvectParticles = ctx.FluidCompute.FindKernel("AdvectParticles");
                    ctx.FluidKernelDissipateParticles = ctx.FluidCompute.FindKernel("DissipateParticles");
                    ctx.FluidKernelDiffuseParticles = ctx.FluidCompute.FindKernel("DiffuseParticles");
                    ctx.FluidKernelAddParticlesGaussian = ctx.FluidCompute.FindKernel("AddParticlesGaussian");
                }
                catch
                {
                    ctx.UseRedBlackSolver = false;
                }

                try
                {
                    ctx.FluidKernelInkToObstacles = ctx.FluidCompute.FindKernel("InkToObstacles");
                }
                catch
                {
                    ctx.FluidKernelInkToObstacles = -1;
                }

                try
                {
                    ctx.FluidKernelAdvectHeat = ctx.FluidCompute.FindKernel("AdvectHeat");
                    ctx.FluidKernelDiffuseHeat = ctx.FluidCompute.FindKernel("DiffuseHeat");
                    ctx.FluidKernelAddHeatSources = ctx.FluidCompute.FindKernel("AddHeatSources");
                }
                catch
                {
                    ctx.FluidKernelAdvectHeat = -1;
                    ctx.FluidKernelDiffuseHeat = -1;
                    ctx.FluidKernelAddHeatSources = -1;
                }

                // ThermalInteractions is a separate Inkling compute shader (CP5). Always reset the
                // kernel index first so editor re-initialization can't leave a stale value when the
                // shader ref is missing or the kernel is absent.
                ctx.KernelThermalInteractions = -1;
                if (ctx.ThermalInteractionsCompute != null
                    && ctx.ThermalInteractionsCompute.HasKernel("ThermalInteractions"))
                {
                    try { ctx.KernelThermalInteractions = ctx.ThermalInteractionsCompute.FindKernel("ThermalInteractions"); }
                    catch { ctx.KernelThermalInteractions = -1; }
                }
            }
            catch (System.Exception)
            {
                Debug.LogWarning("[SimDriver] Compute shader doesn't have required kernels. Running in test pattern mode.");
                kernelsFound = false;
                ctx.FluidCompute = null;
            }

            return kernelsFound;
        }

        public void SetConstants()
        {
            var fc = ctx.FluidCompute;
            if (fc == null) return;

            fc.SetInt("_Resolution", ctx.Resolution);
            fc.SetFloat("_DeltaTime", ctx.Timestep);
            fc.SetFloat("_FrameDeltaTime", ctx.FrameDeltaTime);
            fc.SetFloat("_Viscosity", ctx.Viscosity);
            fc.SetFloat("_VorticityStrength", ctx.VorticityStrength);
            // Global velocity/density damping is also a per-frame retention; convert to per-second so the
            // dt-normalized advection shader makes it frame-rate independent. (Swapped per-pass below.)
            fc.SetFloat("_Dissipation", PerFrameToPerSecond(ctx.Dissipation));
            fc.SetVector("_SimulationSize", new Vector2(ctx.Resolution, ctx.Resolution));
            fc.SetFloat("_DebugZeroPressure", ctx.DebugZeroPressure ? 1f : 0f);
            fc.SetFloat("_DebugZeroVelocity", ctx.DebugZeroVelocity ? 1f : 0f);
            fc.SetFloat("_DebugSkipAir", ctx.DebugSkipAir ? 1f : 0f);

            float alpha = 0f;
            float inverseBeta = 1f;
            // dt-normalized: diffusion strength scales with the REAL frame dt (FrameDeltaTime), not the
            // fixed solver timestep, so viscous smoothing-per-second is frame-rate independent. The
            // implicit Jacobi form (inverseBeta = 1/(1+4a)) is unconditionally stable for any alpha;
            // substepping keeps alpha (and thus the fixed-iteration solve error) small at low framerates.
            // Byte-identical to the old fixed-timestep behavior when FrameDeltaTime == Timestep.
            if (ctx.Viscosity > 0f && ctx.FrameDeltaTime > 0f)
            {
                // Keep diffusion feel roughly resolution-independent by treating
                // inspector viscosity as tuned for a 256 baseline grid.
                float resScale = Mathf.Max(1f, ctx.Resolution / 256f);
                float effectiveViscosity = ctx.Viscosity / (resScale * resScale);
                float a = effectiveViscosity * ctx.FrameDeltaTime * ctx.Resolution * ctx.Resolution;
                alpha = a;
                inverseBeta = 1f / (1f + 4f * a);
            }

            fc.SetFloat("_Alpha", alpha);
            fc.SetFloat("_InverseBeta", inverseBeta);

            fc.SetVector("_ForcePosition", Vector2.zero);
            fc.SetVector("_ForceDirection", Vector2.zero);
            fc.SetFloat("_ForceRadius", ctx.ForceRadius);
            fc.SetFloat("_ForceStrength", 0f);
            fc.SetFloat("_DensityAmount", 0f);

            // Per-ink properties
            SetInkProperties(fc);

            // Heat layer (scalar environment field). Dissipation is a per-second retention toward
            // ambient (from a half-life), matching the ink dissipation convention; defaults are
            // inert (near-persistent, no diffusion, ambient 0) so CP1 has no observable effect.
            fc.SetFloat("_ThermalDissipation", HalfLifeToPerSecond(ctx.ThermalDissipationHalfLife));
            fc.SetFloat("_ThermalDiffusion", Mathf.Clamp01(ctx.ThermalDiffusion));
            fc.SetFloat("_AmbientTemperature", ctx.AmbientTemperature);

            // Heat sources (CP3): fire emits heat. Add-only; does not modify particles.
            fc.SetInt("_EnableHeatSources", ctx.EnableHeatSources ? 1 : 0);
            fc.SetFloat("_FireHeatEmissionRate", Mathf.Max(0f, ctx.FireHeatEmissionRate));
            fc.SetFloat("_MaxHeat", Mathf.Max(0f, ctx.MaxHeat));

            fc.SetVector("_TexelSize", new Vector4(1f / ctx.Resolution, 1f / ctx.Resolution, ctx.Resolution, ctx.Resolution));
        }

        private void SetInkProperties(ComputeShader fc)
        {
            // Dissipation. InkTypeDef.dissipationHalfLife is the seconds-to-50% half-life; convert to
            // per-second retention here. The shader applies pow(retention, frameDt), so the decay is
            // frame-rate independent and reaches exactly 50% after halfLife real seconds.
            fc.SetFloat("_DissipationFire", HalfLifeToPerSecond(GetInkProp(InkTypeId.Fire, d => d.dissipationHalfLife, 2.5f)));
            fc.SetFloat("_DissipationWater", HalfLifeToPerSecond(GetInkProp(InkTypeId.Water, d => d.dissipationHalfLife, 25f)));
            fc.SetFloat("_DissipationPlantSeeded", HalfLifeToPerSecond(GetInkProp(InkTypeId.PlantSeeded, d => d.dissipationHalfLife, 40f)));
            fc.SetFloat("_DissipationPlantGrown", HalfLifeToPerSecond(GetInkProp(InkTypeId.PlantGrown, d => d.dissipationHalfLife, 60f)));
            fc.SetFloat("_DissipationSteam", HalfLifeToPerSecond(GetInkProp(InkTypeId.Steam, d => d.dissipationHalfLife, 4f)));
            fc.SetFloat("_DissipationGlitter", HalfLifeToPerSecond(GetInkProp(InkTypeId.Glitter, d => d.dissipationHalfLife, 12f)));
            // BlackBody is intentionally fast-fading: it is re-stamped every frame so inklings read as
            // rigid bodies (crisp shape that follows the stamp rather than smearing/advecting). Do NOT
            // make it persistent like Plant/Ice. (To be revisited when plants/ice move to rigid bodies.)
            fc.SetFloat("_DissipationBlackBody", HalfLifeToPerSecond(GetInkProp(InkTypeId.BlackBody, d => d.dissipationHalfLife, 0.75f)));
            fc.SetFloat("_DissipationElectricitySeeded", HalfLifeToPerSecond(GetInkProp(InkTypeId.ElectricitySeeded, d => d.dissipationHalfLife, 2f)));
            fc.SetFloat("_DissipationElectricityGrown", HalfLifeToPerSecond(GetInkProp(InkTypeId.ElectricityGrown, d => d.dissipationHalfLife, 2f)));
            fc.SetFloat("_DissipationIce", HalfLifeToPerSecond(GetInkProp(InkTypeId.Ice, d => d.dissipationHalfLife, 45f)));

            // Viscosity
            fc.SetFloat("_ViscosityFire", GetInkProp(InkTypeId.Fire, d => d.viscosity, 0.05f));
            fc.SetFloat("_ViscosityWater", GetInkProp(InkTypeId.Water, d => d.viscosity, 0.2f));
            fc.SetFloat("_ViscosityPlantSeeded", GetInkProp(InkTypeId.PlantSeeded, d => d.viscosity, 0.0f));
            fc.SetFloat("_ViscosityPlantGrown", GetInkProp(InkTypeId.PlantGrown, d => d.viscosity, 0.0f));
            fc.SetFloat("_ViscositySteam", GetInkProp(InkTypeId.Steam, d => d.viscosity, 0.15f));
            fc.SetFloat("_ViscosityGlitter", GetInkProp(InkTypeId.Glitter, d => d.viscosity, 0.02f));
            fc.SetFloat("_ViscosityBlackBody", GetInkProp(InkTypeId.BlackBody, d => d.viscosity, 0.1f));
            fc.SetFloat("_ViscosityElectricitySeeded", GetInkProp(InkTypeId.ElectricitySeeded, d => d.viscosity, 0.0f));
            fc.SetFloat("_ViscosityElectricityGrown", GetInkProp(InkTypeId.ElectricityGrown, d => d.viscosity, 0.0f));
            fc.SetFloat("_ViscosityIce", GetInkProp(InkTypeId.Ice, d => d.viscosity, 0.0f));

            // Vorticity
            fc.SetFloat("_VorticityFire", GetInkProp(InkTypeId.Fire, d => d.vorticity, 1.5f));
            fc.SetFloat("_VorticityWater", GetInkProp(InkTypeId.Water, d => d.vorticity, 0.8f));
            fc.SetFloat("_VorticityPlantSeeded", GetInkProp(InkTypeId.PlantSeeded, d => d.vorticity, 0.0f));
            fc.SetFloat("_VorticityPlantGrown", GetInkProp(InkTypeId.PlantGrown, d => d.vorticity, 0.0f));
            fc.SetFloat("_VorticitySteam", GetInkProp(InkTypeId.Steam, d => d.vorticity, 1.2f));
            fc.SetFloat("_VorticityGlitter", GetInkProp(InkTypeId.Glitter, d => d.vorticity, 0.5f));
            fc.SetFloat("_VorticityBlackBody", GetInkProp(InkTypeId.BlackBody, d => d.vorticity, 0.3f));
            fc.SetFloat("_VorticityElectricitySeeded", GetInkProp(InkTypeId.ElectricitySeeded, d => d.vorticity, 0.0f));
            fc.SetFloat("_VorticityElectricityGrown", GetInkProp(InkTypeId.ElectricityGrown, d => d.vorticity, 0.0f));
            fc.SetFloat("_VorticityIce", GetInkProp(InkTypeId.Ice, d => d.vorticity, 0.2f));

            // Advection
            fc.SetFloat("_AdvectionFire", GetInkProp(InkTypeId.Fire, d => d.advectionWeight, 1.0f));
            fc.SetFloat("_AdvectionWater", GetInkProp(InkTypeId.Water, d => d.advectionWeight, 1.0f));
            fc.SetFloat("_AdvectionPlantSeeded", GetInkProp(InkTypeId.PlantSeeded, d => d.advectionWeight, 1.0f));
            fc.SetFloat("_AdvectionPlantGrown", GetInkProp(InkTypeId.PlantGrown, d => d.advectionWeight, 1.0f));
            fc.SetFloat("_AdvectionSteam", GetInkProp(InkTypeId.Steam, d => d.advectionWeight, 1.0f));
            fc.SetFloat("_AdvectionGlitter", GetInkProp(InkTypeId.Glitter, d => d.advectionWeight, 1.0f));
            fc.SetFloat("_AdvectionBlackBody", GetInkProp(InkTypeId.BlackBody, d => d.advectionWeight, 1.0f));
            fc.SetFloat("_AdvectionElectricitySeeded", GetInkProp(InkTypeId.ElectricitySeeded, d => d.advectionWeight, 1.0f));
            fc.SetFloat("_AdvectionElectricityGrown", GetInkProp(InkTypeId.ElectricityGrown, d => d.advectionWeight, 1.0f));
            fc.SetFloat("_AdvectionIce", GetInkProp(InkTypeId.Ice, d => d.advectionWeight, 1.0f));

            // Obstacle thresholds (0 = not an obstacle)
            fc.SetFloat("_ObstacleThresholdFire", GetObstacleThreshold(InkTypeId.Fire));
            fc.SetFloat("_ObstacleThresholdWater", GetObstacleThreshold(InkTypeId.Water));
            fc.SetFloat("_ObstacleThresholdPlantSeeded", GetObstacleThreshold(InkTypeId.PlantSeeded));
            fc.SetFloat("_ObstacleThresholdPlantGrown", GetObstacleThreshold(InkTypeId.PlantGrown));
            fc.SetFloat("_ObstacleThresholdSteam", GetObstacleThreshold(InkTypeId.Steam));
            fc.SetFloat("_ObstacleThresholdGlitter", GetObstacleThreshold(InkTypeId.Glitter));
            fc.SetFloat("_ObstacleThresholdBlackBody", GetObstacleThreshold(InkTypeId.BlackBody));
            fc.SetFloat("_ObstacleThresholdElectricitySeeded", GetObstacleThreshold(InkTypeId.ElectricitySeeded));
            fc.SetFloat("_ObstacleThresholdElectricityGrown", GetObstacleThreshold(InkTypeId.ElectricityGrown));
            fc.SetFloat("_ObstacleThresholdIce", GetObstacleThreshold(InkTypeId.Ice));

            // Pressure
            fc.SetFloat("_PressureFire", GetClampedPressureWeight(InkTypeId.Fire, 1.0f));
            fc.SetFloat("_PressureWater", GetClampedPressureWeight(InkTypeId.Water, 1.0f));
            fc.SetFloat("_PressurePlantSeeded", GetClampedPressureWeight(InkTypeId.PlantSeeded, 1.0f));
            fc.SetFloat("_PressurePlantGrown", GetClampedPressureWeight(InkTypeId.PlantGrown, 1.0f));
            fc.SetFloat("_PressureSteam", GetClampedPressureWeight(InkTypeId.Steam, 1.0f));
            fc.SetFloat("_PressureGlitter", GetClampedPressureWeight(InkTypeId.Glitter, 1.0f));
            fc.SetFloat("_PressureBlackBody", GetClampedPressureWeight(InkTypeId.BlackBody, 1.0f));
            fc.SetFloat("_PressureElectricitySeeded", GetClampedPressureWeight(InkTypeId.ElectricitySeeded, 1.0f));
            fc.SetFloat("_PressureElectricityGrown", GetClampedPressureWeight(InkTypeId.ElectricityGrown, 1.0f));
            fc.SetFloat("_PressureIce", GetClampedPressureWeight(InkTypeId.Ice, 1.0f));
        }

        private float GetInkProp(InkTypeId type, System.Func<InkTypeDef, float> getter, float defaultValue)
        {
            int idx = (int)type;
            if (ctx.InkDefinitions != null && idx < ctx.InkDefinitions.Length && ctx.InkDefinitions[idx] != null)
                return getter(ctx.InkDefinitions[idx]);
            return defaultValue;
        }

        /// <summary>
        /// Converts a half-life (seconds to fade to 50%) into per-second retention for the shader,
        /// which applies pow(retention, frameDt). After halfLife real seconds the value reaches 0.5,
        /// independent of framerate. retention = 0.5^(1/halfLife).
        /// </summary>
        private static float HalfLifeToPerSecond(float halfLifeSeconds)
        {
            halfLifeSeconds = Mathf.Max(halfLifeSeconds, 1e-3f);
            return Mathf.Pow(0.5f, 1f / halfLifeSeconds);
        }

        /// <summary>
        /// Converts a per-frame retention (authored against the fixed Timestep) to per-second retention
        /// for the dt-normalized shader. pow(PerFrameToPerSecond(v), Timestep) == v, so deterministic
        /// runs are unchanged while live play becomes frame-rate independent.
        /// </summary>
        private float PerFrameToPerSecond(float perFrame)
        {
            perFrame = Mathf.Clamp01(perFrame);
            return Mathf.Pow(perFrame, 1f / Mathf.Max(ctx.Timestep, 1e-4f));
        }

        private float GetClampedPressureWeight(InkTypeId type, float defaultValue)
        {
            // Keep pressure weighting in a stable range so projection does not over-damp
            // injected velocity (values > 1 can erase brush-driven motion quickly).
            float raw = GetInkProp(type, d => d.pressureWeight, defaultValue);
            return Mathf.Clamp(raw, 0.25f, 1.0f);
        }

        private float GetObstacleThreshold(InkTypeId type)
        {
            int idx = (int)type;
            if (ctx.InkDefinitions != null && idx < ctx.InkDefinitions.Length && ctx.InkDefinitions[idx] != null)
            {
                var def = ctx.InkDefinitions[idx];
                return def.actsAsObstacle ? def.obstacleThreshold : 0f;
            }
            return 0f;
        }

        private float GetInkInteractionThreshold(InkTypeId type, float defaultValue)
        {
            int idx = (int)type;
            if (ctx.InkDefinitions != null && idx < ctx.InkDefinitions.Length && ctx.InkDefinitions[idx] != null)
                return ctx.InkDefinitions[idx].interactionThreshold;
            return defaultValue;
        }

        private (bool enabled, float threshold, float rate) GetClearingParameters()
        {
            if (ctx.InkDefinitions != null)
            {
                foreach (var def in ctx.InkDefinitions)
                {
                    if (def != null && def.enableClearing)
                        return (true, def.clearingThreshold, def.clearingRate);
                }
            }
            return (ctx.EnableBlackBodyClearingFallback, ctx.BlackBodyThresholdFallback, ctx.BlackBodyClearingRateFallback);
        }

        public void ClearAll()
        {
            if (ctx.FluidCompute == null) return;

            int threadGroups = Mathf.CeilToInt(ctx.Resolution / 8f);
            int k = ctx.FluidKernelClear;

            // Clear BOTH ping-pong buffers (read AND write). Clearing only Write leaves the
            // stale Read buffer that the next Step() consumes — so a mid-run reset would inherit
            // the previous scenario's velocity/density (which barely dissipates per-frame). Two
            // passes zero both sides without needing a swap.
            for (int pass = 0; pass < 2; pass++)
            {
                var vel = pass == 0 ? ctx.Velocity.Write : ctx.Velocity.Read;
                var den = pass == 0 ? ctx.Density.Write : ctx.Density.Read;
                var pre = pass == 0 ? ctx.Pressure.Write : ctx.Pressure.Read;
                ctx.FluidCompute.SetTexture(k, "_VelocityWrite", vel);
                ctx.FluidCompute.SetTexture(k, "_DensityWrite", den);
                ctx.FluidCompute.SetTexture(k, "_PressureWrite", pre);
                ctx.FluidCompute.SetTexture(k, "_DivergenceWrite", ctx.Divergence);
                ctx.FluidCompute.SetTexture(k, "_VorticityMag", ctx.VorticityTex);
                ctx.FluidCompute.Dispatch(k, threadGroups, threadGroups, 1);
            }

            // Heat is a persistent field (not cleared per-step); zero BOTH ping-pong sides here so a
            // mid-run reset can't inherit stale temperature. Clear(Color) clears Read and Write.
            ctx.Heat?.Clear(Color.clear);

            int particleCount = ctx.Resolution * ctx.Resolution;
            if (ctx.GpuPromotesHalf)
            {
                var zero = new SimulationDisplay_iparticle_gpu[particleCount];
                ctx.ParticlesBuffer[ctx.ParticleReadIndex].SetData(zero);
                ctx.ParticlesBuffer[ctx.ParticleWriteIndex].SetData(zero);
            }
            else
            {
                var zero = new iparticle[particleCount];
                ctx.ParticlesBuffer[ctx.ParticleReadIndex].SetData(zero);
                ctx.ParticlesBuffer[ctx.ParticleWriteIndex].SetData(zero);
            }
        }

        public void InitializeObstacles()
        {
            if (ctx.FluidCompute == null || ctx.FluidKernelUpdateObstacles == 0) return;

            int threadGroups = Mathf.CeilToInt(ctx.Resolution / 8f);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelUpdateObstacles, "_ObstacleWrite", ctx.Obstacles);
            ctx.FluidCompute.Dispatch(ctx.FluidKernelUpdateObstacles, threadGroups, threadGroups, 1);
            Debug.Log("[SimDriver] Initialized obstacles");
        }

        /// <summary>
        /// Runs the full fluid simulation frame. Dispatch order is preserved exactly.
        /// </summary>
        public void Step(Stopwatch sw)
        {
            if (ctx.FluidCompute == null) return;

            int threadGroups = Mathf.CeilToInt(ctx.Resolution / 8f);

            SetConstants();

            // Clear creature buffers
            if (ctx.Obstacles != null)
            {
                RenderTexture.active = ctx.Obstacles;
                GL.Clear(true, true, new Color(0, 0, 0, 0));
                RenderTexture.active = null;
            }
            if (ctx.CreatureInkBuffer != null)
            {
                RenderTexture.active = ctx.CreatureInkBuffer;
                GL.Clear(true, true, Color.clear);
                RenderTexture.active = null;
            }
            // Reaction-impulse accumulator: clear unconditionally each step so a step that
            // skips ink interactions (or runs with the feature off) never applies stale motion.
            if (ctx.ReactionImpulseTex != null)
            {
                RenderTexture.active = ctx.ReactionImpulseTex;
                GL.Clear(true, true, Color.clear);
                RenderTexture.active = null;
            }

            // 0. Ink-to-obstacle pass (writes 1.0 into obstacle RT where solid inks exceed threshold)
            if (ctx.FluidKernelInkToObstacles >= 0 && ctx.UseParticleSimulation
                && ctx.ParticlesBuffer != null && ctx.Obstacles != null)
            {
                ctx.FluidCompute.SetBuffer(ctx.FluidKernelInkToObstacles, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelInkToObstacles, "_ObstacleWrite", ctx.Obstacles);
                ctx.FluidCompute.Dispatch(ctx.FluidKernelInkToObstacles, threadGroups, threadGroups, 1);
            }

            // 1. Advection
            if (sw != null) sw.Restart();

            ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_VelocityRead", ctx.Velocity.Read);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_VelocityWrite", ctx.Velocity.Write);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_QuantityRead", ctx.Velocity.Read);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_QuantityWrite", ctx.Velocity.Write);
            ctx.FluidCompute.SetFloat("_Dissipation", PerFrameToPerSecond(ctx.VelocityDissipation));
            ctx.FluidCompute.Dispatch(ctx.FluidKernelAdvection, threadGroups, threadGroups, 1);
            ctx.Velocity.Swap();

            // Advect density
            if (ctx.Density != null)
            {
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_VelocityRead", ctx.Velocity.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_VelocityWrite", ctx.Velocity.Write);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_QuantityRead", ctx.Density.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_QuantityWrite", ctx.Density.Write);
                ctx.FluidCompute.SetFloat("_Dissipation", PerFrameToPerSecond(ctx.Dissipation));
                ctx.FluidCompute.Dispatch(ctx.FluidKernelAdvection, threadGroups, threadGroups, 1);
                ctx.Density.Swap();
            }

            // Advect particles
            if (ctx.UseParticleSimulation && ctx.ParticlesBuffer != null)
            {
                if (ctx.Resolution <= ctx.MaxParticleSimResolution)
                {
                    if (ctx.UseParticleAdvection && ctx.FluidKernelAdvectParticles != 0)
                    {
                        ctx.FluidCompute.SetBuffer(ctx.FluidKernelAdvectParticles, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                        ctx.FluidCompute.SetBuffer(ctx.FluidKernelAdvectParticles, "_ParticlesWrite", ctx.ParticlesBuffer[ctx.ParticleWriteIndex]);
                        ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvectParticles, "_VelocityRead", ctx.Velocity.Read);
                        ctx.FluidCompute.Dispatch(ctx.FluidKernelAdvectParticles, threadGroups, threadGroups, 1);
                        ctx.SwapParticleBuffers();
                    }

                    // Ink interactions
                    if (ctx.UseInkInteractions && opQueue.InkInteractionsReady && ctx.AffinityGroups != null)
                    {
                        ctx.InkInteractionsCompute.SetInt("_Resolution", ctx.Resolution);
                        ctx.InkInteractionsCompute.SetFloat("_DeltaTime", ctx.Timestep);
                        ctx.InkInteractionsCompute.SetInt("_DebugMode", ctx.InkInteractionsDebugMode ? 1 : 0);

                        var clearing = GetClearingParameters();
                        ctx.InkInteractionsCompute.SetInt("_EnableBlackBodyClearing", clearing.enabled ? 1 : 0);
                        ctx.InkInteractionsCompute.SetFloat("_BlackBodyThreshold", clearing.threshold);
                        ctx.InkInteractionsCompute.SetFloat("_BlackBodyClearingRate", clearing.rate);

                        // Reaction-impulse accumulation setup. The UAV must be bound whenever the
                        // InkInteractions kernel dispatches (it declares _ReactionImpulseRW), so bind
                        // unconditionally when the texture exists; the write itself is gated by the
                        // _AccumulateReactionImpulse flag. Direction and intensity are fully data-driven
                        // from each group's reactionImpulseMatrix (uploaded per group below) — nothing
                        // hardcodes specific ink types.
                        bool accumulateImpulse = ctx.EnableReactionImpulse && ctx.ReactionImpulseTex != null;
                        if (ctx.ReactionImpulseTex != null)
                        {
                            ctx.InkInteractionsCompute.SetTexture(opQueue.KernelInkInteractions, "_ReactionImpulseRW", ctx.ReactionImpulseTex);
                        }
                        ctx.InkInteractionsCompute.SetInt("_AccumulateReactionImpulse", accumulateImpulse ? 1 : 0);
                        ctx.InkInteractionsCompute.SetFloat("_ReactionImpulseGain", ctx.ReactionImpulseGain);

                        foreach (var group in ctx.AffinityGroups)
                        {
                            if (group == null) continue;

                            int[] indices = group.GetInkIndices();
                            ctx.InkInteractionsCompute.SetInts("_InkIndices", indices);
                            ctx.InkInteractionsCompute.SetMatrix("_ProductMatrix", group.productMatrix);
                            ctx.InkInteractionsCompute.SetVector("_ProductCol4", group.productCol4);
                            ctx.InkInteractionsCompute.SetVector("_ProductCol5", group.productCol5);
                            // Parallel reaction-impulse matrix (motion, independent of concentration).
                            ctx.InkInteractionsCompute.SetMatrix("_ReactionImpulseMatrix", group.reactionImpulseMatrix);
                            ctx.InkInteractionsCompute.SetVector("_ReactionImpulseCol4", group.reactionImpulseCol4);
                            ctx.InkInteractionsCompute.SetVector("_ReactionImpulseCol5", group.reactionImpulseCol5);
                            Vector3 weights = group.GetWeights();
                            ctx.InkInteractionsCompute.SetFloats("_Weights", weights.x, weights.y, weights.z);
                            ctx.InkInteractionsCompute.SetFloat("_RateMultiplier", group.reactionRateMultiplier);

                            Vector4 thresholds = new Vector4(
                                GetInkInteractionThreshold((InkTypeId)indices[0], 0.01f),
                                GetInkInteractionThreshold((InkTypeId)indices[1], 0.01f),
                                GetInkInteractionThreshold((InkTypeId)indices[2], 0.01f),
                                GetInkInteractionThreshold((InkTypeId)indices[3], 0.01f)
                            );
                            ctx.InkInteractionsCompute.SetVector("_InteractionThresholds", thresholds);

                            if (Time.frameCount == 10)
                            {
                                var pm = group.productMatrix;
                                Debug.Log($"[InkInteractions] Group '{group.groupName}' dispatch:\n" +
                                    $"  Indices: [{indices[0]}, {indices[1]}, {indices[2]}, {indices[3]}]\n" +
                                    $"  ProductMatrix row0: [{pm.m00:F2}, {pm.m01:F2}, {pm.m02:F2}, {pm.m03:F2}]\n" +
                                    $"  ProductMatrix row1: [{pm.m10:F2}, {pm.m11:F2}, {pm.m12:F2}, {pm.m13:F2}]\n" +
                                    $"  ProductMatrix row2: [{pm.m20:F2}, {pm.m21:F2}, {pm.m22:F2}, {pm.m23:F2}]\n" +
                                    $"  ProductMatrix row3: [{pm.m30:F2}, {pm.m31:F2}, {pm.m32:F2}, {pm.m33:F2}]\n" +
                                    $"  Weights: [{weights.x}, {weights.y}, {weights.z}]\n" +
                                    $"  RateMultiplier: {group.reactionRateMultiplier}\n" +
                                    $"  Resolution: {ctx.Resolution}, DeltaTime: {ctx.Timestep}");
                            }

                            ctx.InkInteractionsCompute.SetBuffer(opQueue.KernelInkInteractions, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                            ctx.InkInteractionsCompute.SetBuffer(opQueue.KernelInkInteractions, "_ParticlesWrite", ctx.ParticlesBuffer[ctx.ParticleWriteIndex]);
                            ctx.InkInteractionsCompute.Dispatch(opQueue.KernelInkInteractions, threadGroups, threadGroups, 1);
                            ctx.SwapParticleBuffers();
                        }
                    }

                    if (ctx.UseParticleDissipation && ctx.FluidKernelDissipateParticles != 0)
                    {
                        ctx.FluidCompute.SetBuffer(ctx.FluidKernelDissipateParticles, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                        ctx.FluidCompute.SetBuffer(ctx.FluidKernelDissipateParticles, "_ParticlesWrite", ctx.ParticlesBuffer[ctx.ParticleWriteIndex]);
                        ctx.FluidCompute.Dispatch(ctx.FluidKernelDissipateParticles, threadGroups, threadGroups, 1);
                        ctx.SwapParticleBuffers();
                    }

                    if (ctx.UseParticleDiffusion && ctx.FluidKernelDiffuseParticles != 0)
                    {
                        ctx.FluidCompute.SetBuffer(ctx.FluidKernelDiffuseParticles, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                        ctx.FluidCompute.SetBuffer(ctx.FluidKernelDiffuseParticles, "_ParticlesWrite", ctx.ParticlesBuffer[ctx.ParticleWriteIndex]);
                        ctx.FluidCompute.Dispatch(ctx.FluidKernelDiffuseParticles, threadGroups, threadGroups, 1);
                        ctx.SwapParticleBuffers();
                    }
                }
            }

            if (sw != null) AdvectionMs = (float)sw.Elapsed.TotalMilliseconds;

            // 1b. Heat transport (scalar environment layer). Source (fire emits heat) first so it
            // reflects the current particle field, then advect by current velocity + decay, then
            // optional diffusion. Heat is diagnostic only in CP3 — it drives no other field.
            // CP4: AdvectHeat/DiffuseHeat unconditionally read _ObstacleRead, so require ctx.Obstacles
            // here (CP1 always allocates it; the guard just prevents an unbound SRV in test/abnormal setups).
            if (ctx.Heat != null && ctx.FluidKernelAdvectHeat >= 0 && ctx.Obstacles != null)
            {
                // Heat sources: fire emits heat (add-only; never writes the particle buffer).
                if (ctx.EnableHeatSources && ctx.FluidKernelAddHeatSources >= 0
                    && ctx.ParticlesBuffer != null && ctx.ParticlesBuffer[ctx.ParticleReadIndex] != null)
                {
                    ctx.FluidCompute.SetBuffer(ctx.FluidKernelAddHeatSources, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelAddHeatSources, "_HeatRead", ctx.Heat.Read);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelAddHeatSources, "_HeatWrite", ctx.Heat.Write);
                    ctx.FluidCompute.Dispatch(ctx.FluidKernelAddHeatSources, threadGroups, threadGroups, 1);
                    ctx.Heat.Swap();
                }

                // Obstacle mask for no-flux heat transport (CP4). ctx.Obstacles holds this frame's
                // ink/geometry solids; bound unconditionally (required by the guard above) so
                // AdvectHeat/DiffuseHeat don't leak heat across walls and never read an unbound SRV.
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvectHeat, "_VelocityRead", ctx.Velocity.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvectHeat, "_HeatRead", ctx.Heat.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvectHeat, "_HeatWrite", ctx.Heat.Write);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvectHeat, "_ObstacleRead", ctx.Obstacles);
                ctx.FluidCompute.Dispatch(ctx.FluidKernelAdvectHeat, threadGroups, threadGroups, 1);
                ctx.Heat.Swap();

                if (ctx.FluidKernelDiffuseHeat >= 0 && ctx.ThermalDiffusion > 0f)
                {
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelDiffuseHeat, "_HeatRead", ctx.Heat.Read);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelDiffuseHeat, "_HeatWrite", ctx.Heat.Write);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelDiffuseHeat, "_ObstacleRead", ctx.Obstacles);
                    ctx.FluidCompute.Dispatch(ctx.FluidKernelDiffuseHeat, threadGroups, threadGroups, 1);
                    ctx.Heat.Swap();
                }
            }

            // 1c. Thermal interactions (CP5): heat-driven LOCAL phase changes (ice->water->steam,
            // steam->water). Opt-in — default off, so not dispatched and baseline is byte-identical.
            // Runs after heat transport (heat is fresh) using the current particle field; reads/writes
            // both the particle buffer and the heat layer, and swaps both. LOCAL only (no neighbor
            // sampling) so it cannot mint mass. Sanitized thresholds enforce condense <= melt <= boil.
            if (ctx.EnableThermalInteractions && ctx.KernelThermalInteractions >= 0
                && ctx.ThermalInteractionsCompute != null && ctx.Heat != null
                && ctx.ParticlesBuffer != null && ctx.ParticlesBuffer[ctx.ParticleReadIndex] != null)
            {
                var tc = ctx.ThermalInteractionsCompute;
                int k = ctx.KernelThermalInteractions;

                float condenseT = Mathf.Max(0f, ctx.CondenseThreshold);
                float meltT = Mathf.Max(condenseT, ctx.MeltThreshold);
                float boilT = Mathf.Max(meltT, ctx.BoilThreshold);
                float ambient = ctx.AmbientTemperature;
                float maxHeat = Mathf.Max(ambient, ctx.MaxHeat);

                tc.SetInt("_Resolution", ctx.Resolution);
                tc.SetFloat("_FrameDeltaTime", ctx.FrameDeltaTime);
                tc.SetInt("_EnableThermalInteractions", 1);
                tc.SetFloat("_CondenseThreshold", condenseT);
                tc.SetFloat("_MeltThreshold", meltT);
                tc.SetFloat("_BoilThreshold", boilT);
                tc.SetFloat("_MeltRate", Mathf.Max(0f, ctx.MeltRate));
                tc.SetFloat("_BoilRate", Mathf.Max(0f, ctx.BoilRate));
                tc.SetFloat("_CondenseRate", Mathf.Max(0f, ctx.CondenseRate));
                tc.SetFloat("_MeltHeatCost", Mathf.Max(0f, ctx.MeltHeatCost));
                tc.SetFloat("_BoilHeatCost", Mathf.Max(0f, ctx.BoilHeatCost));
                tc.SetFloat("_CondenseHeatRelease", Mathf.Max(0f, ctx.CondenseHeatRelease));
                tc.SetFloat("_AmbientTemperature", ambient);
                tc.SetFloat("_MaxHeat", maxHeat);

                tc.SetBuffer(k, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                tc.SetBuffer(k, "_ParticlesWrite", ctx.ParticlesBuffer[ctx.ParticleWriteIndex]);
                tc.SetTexture(k, "_HeatRead", ctx.Heat.Read);
                tc.SetTexture(k, "_HeatWrite", ctx.Heat.Write);
                tc.Dispatch(k, threadGroups, threadGroups, 1);
                ctx.SwapParticleBuffers();
                ctx.Heat.Swap();
            }

            // 2. Diffusion
            if (ctx.Viscosity > 0f && ctx.DiffusionIterations > 0)
            {
                if (sw != null) sw.Restart();

                for (int i = 0; i < ctx.DiffusionIterations; i++)
                {
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelDiffusion, "_VelocityRead", ctx.Velocity.Read);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelDiffusion, "_VelocityWrite", ctx.Velocity.Write);
                    ctx.FluidCompute.Dispatch(ctx.FluidKernelDiffusion, threadGroups, threadGroups, 1);
                    ctx.Velocity.Swap();
                }

                if (sw != null) DiffusionMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 3. Pressure projection
            if (sw != null) sw.Restart();

            ctx.FluidCompute.SetTexture(ctx.FluidKernelDivergence, "_VelocityRead", ctx.Velocity.Read);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelDivergence, "_DivergenceWrite", ctx.Divergence);
            if (ctx.UseParticleSimulation && ctx.ParticlesBuffer != null && ctx.ParticlesBuffer[ctx.ParticleReadIndex] != null)
            {
                ctx.FluidCompute.SetBuffer(ctx.FluidKernelDivergence, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
            }
            ctx.FluidCompute.Dispatch(ctx.FluidKernelDivergence, threadGroups, threadGroups, 1);

            if (ctx.UseRedBlackSolver && ctx.FluidKernelPressureRedBlack != 0)
            {
                for (int i = 0; i < ctx.PressureIterations; i++)
                {
                    ctx.FluidCompute.SetFloat("_Alpha", 0f);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelPressureRedBlack, "_PressureRead", ctx.Pressure.Read);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelPressureRedBlack, "_DivergenceRead", ctx.Divergence);
                    ctx.FluidCompute.Dispatch(ctx.FluidKernelPressureRedBlack, threadGroups, threadGroups, 1);

                    ctx.FluidCompute.SetFloat("_Alpha", 1f);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelPressureRedBlack, "_PressureRead", ctx.Pressure.Read);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelPressureRedBlack, "_DivergenceRead", ctx.Divergence);
                    ctx.FluidCompute.Dispatch(ctx.FluidKernelPressureRedBlack, threadGroups, threadGroups, 1);
                }
            }
            else
            {
                for (int i = 0; i < ctx.PressureIterations; i++)
                {
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelPressure, "_PressureRead", ctx.Pressure.Read);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelPressure, "_DivergenceRead", ctx.Divergence);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelPressure, "_PressureWrite", ctx.Pressure.Write);
                    ctx.FluidCompute.Dispatch(ctx.FluidKernelPressure, threadGroups, threadGroups, 1);
                    ctx.Pressure.Swap();
                }
            }

            if (sw != null) PressureMs = (float)sw.Elapsed.TotalMilliseconds;

            // 4. Subtract gradient
            if (sw != null) sw.Restart();

            ctx.FluidCompute.SetTexture(ctx.FluidKernelSubtractGradient, "_VelocityRead", ctx.Velocity.Read);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelSubtractGradient, "_VelocityWrite", ctx.Velocity.Write);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelSubtractGradient, "_PressureRead", ctx.Pressure.Read);
            ctx.FluidCompute.Dispatch(ctx.FluidKernelSubtractGradient, threadGroups, threadGroups, 1);
            ctx.Velocity.Swap();

            // 5. Obstacle boundaries
            if (ctx.FluidKernelApplyObstacleBoundary != 0)
            {
                ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_VelocityRead", ctx.Velocity.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_VelocityWrite", ctx.Velocity.Write);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_ObstacleRead", ctx.Obstacles);
                ctx.FluidCompute.Dispatch(ctx.FluidKernelApplyObstacleBoundary, threadGroups, threadGroups, 1);
                ctx.Velocity.Swap();
            }

            if (sw != null) ProjectionMs = (float)sw.Elapsed.TotalMilliseconds;

            // 5b. Reaction impulse (fire replacing plant seeds motion).
            // Applied AFTER pressure projection + obstacle boundary so projection does not
            // immediately cancel it, and BEFORE vorticity confinement so swirl amplifies it.
            // Obstacle boundary is re-applied here so the injected motion respects solids even
            // when vorticity confinement is disabled (VorticityStrength == 0).
            if (ctx.EnableReactionImpulse && ctx.ReactionImpulseTex != null
                && opQueue.ApplyReactionImpulseReady && ctx.InkInteractionsCompute != null)
            {
                var ic = ctx.InkInteractionsCompute;
                int k = opQueue.KernelApplyReactionImpulse;
                // dt scale = subDt/timestep, matching the pipeline's other per-substep velocity
                // impulses so injected energy is framerate/substep independent (FrameDeltaTime is
                // the active substep real dt; see SimDriver.SimulateFrameSubstepped).
                float dtScale = ctx.Timestep > 1e-6f ? ctx.FrameDeltaTime / ctx.Timestep : 1f;
                ic.SetInt("_Resolution", ctx.Resolution);
                ic.SetFloat("_ReactionImpulseStrength", ctx.ReactionImpulseStrength);
                ic.SetFloat("_ReactionImpulseMax", ctx.ReactionImpulseMax);
                ic.SetFloat("_ReactionImpulseCurlBias", ctx.ReactionImpulseCurlBias);
                ic.SetFloat("_ReactionImpulseExpansionBias", ctx.ReactionImpulseExpansionBias);
                ic.SetFloat("_ReactionImpulseDtScale", dtScale);
                ic.SetTexture(k, "_ReactionImpulseRead", ctx.ReactionImpulseTex);
                ic.SetTexture(k, "_VelocityReadRI", ctx.Velocity.Read);
                ic.SetTexture(k, "_VelocityWriteRI", ctx.Velocity.Write);
                ic.Dispatch(k, threadGroups, threadGroups, 1);
                ctx.Velocity.Swap();

                if (ctx.FluidKernelApplyObstacleBoundary != 0)
                {
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_VelocityRead", ctx.Velocity.Read);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_VelocityWrite", ctx.Velocity.Write);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_ObstacleRead", ctx.Obstacles);
                    ctx.FluidCompute.Dispatch(ctx.FluidKernelApplyObstacleBoundary, threadGroups, threadGroups, 1);
                    ctx.Velocity.Swap();
                }
            }

            // 6. Vorticity confinement
            // Run this after projection so pressure solve doesn't immediately cancel the swirl impulse.
            if (ctx.VorticityStrength > 0)
            {
                if (sw != null) sw.Restart();

                ctx.FluidCompute.SetTexture(ctx.FluidKernelVorticity, "_VelocityRead", ctx.Velocity.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelVorticity, "_VorticityMag", ctx.VorticityTex);
                ctx.FluidCompute.Dispatch(ctx.FluidKernelVorticity, threadGroups, threadGroups, 1);

                ctx.FluidCompute.SetTexture(ctx.FluidKernelVorticityConfinement, "_VelocityRead", ctx.Velocity.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelVorticityConfinement, "_VelocityWrite", ctx.Velocity.Write);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelVorticityConfinement, "_VorticityMag", ctx.VorticityTex);
                if (ctx.UseParticleSimulation && ctx.ParticlesBuffer != null && ctx.ParticlesBuffer[ctx.ParticleReadIndex] != null)
                {
                    ctx.FluidCompute.SetBuffer(ctx.FluidKernelVorticityConfinement, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                }
                ctx.FluidCompute.Dispatch(ctx.FluidKernelVorticityConfinement, threadGroups, threadGroups, 1);
                ctx.Velocity.Swap();

                // Re-apply obstacle boundary after adding confinement force.
                if (ctx.FluidKernelApplyObstacleBoundary != 0)
                {
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_VelocityRead", ctx.Velocity.Read);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_VelocityWrite", ctx.Velocity.Write);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_ObstacleRead", ctx.Obstacles);
                    ctx.FluidCompute.Dispatch(ctx.FluidKernelApplyObstacleBoundary, threadGroups, threadGroups, 1);
                    ctx.Velocity.Swap();
                }

                if (sw != null) VorticityMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 7. Channel splat
            bool canRenderParticleChannels = ctx.UseParticleDisplay
                && ctx.UseParticleSimulation
                && ctx.Resolution <= ctx.MaxParticleSimResolution;
            if (canRenderParticleChannels && opQueue.ChannelSplatReady && ctx.ParticlesBuffer != null
                && ctx.ChannelRT0 != null && ctx.ChannelRT1 != null && ctx.ChannelRT2 != null
                && ctx.Heat != null)
            {
                ctx.ParticleChannelSplatCompute.SetInt("_Resolution", ctx.Resolution);
                ctx.ParticleChannelSplatCompute.SetBuffer(opQueue.KernelChannelSplat, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                // Heat scalar layer packed into _Channels2.z for debug viz (CP2). CP1 always allocates
                // ctx.Heat, so it's normally present — but the kernel unconditionally reads _HeatRead,
                // so require ctx.Heat in the guard above and bind it here to avoid an unbound SRV.
                ctx.ParticleChannelSplatCompute.SetTexture(opQueue.KernelChannelSplat, "_HeatRead", ctx.Heat.Read);
                ctx.ParticleChannelSplatCompute.SetTexture(opQueue.KernelChannelSplat, "_Channels0", ctx.ChannelRT0);
                ctx.ParticleChannelSplatCompute.SetTexture(opQueue.KernelChannelSplat, "_Channels1", ctx.ChannelRT1);
                ctx.ParticleChannelSplatCompute.SetTexture(opQueue.KernelChannelSplat, "_Channels2", ctx.ChannelRT2);
                int splatGroups = Mathf.CeilToInt(ctx.Resolution / 8f);
                ctx.ParticleChannelSplatCompute.Dispatch(opQueue.KernelChannelSplat, splatGroups, splatGroups, 1);

                // Only generate mipped copies when display is smaller than simulation
                // (needs mipmaps for minification). At 1:1, read directly from UAV RTs.
                bool needsMippedCopy = ctx.EffectiveDisplayRes < ctx.Resolution;
                if (needsMippedCopy
                    && ctx.ChannelRT0Mipped != null && ctx.ChannelRT1Mipped != null && ctx.ChannelRT2Mipped != null)
                {
                    Graphics.Blit(ctx.ChannelRT0, ctx.ChannelRT0Mipped);
                    Graphics.Blit(ctx.ChannelRT1, ctx.ChannelRT1Mipped);
                    Graphics.Blit(ctx.ChannelRT2, ctx.ChannelRT2Mipped);
                }

                if (needsMippedCopy &&
                    ctx.ChannelRT0Down != null && ctx.ChannelRT1Down != null && ctx.ChannelRT2Down != null)
                {
                    Graphics.Blit(ctx.ChannelRT0Mipped ?? ctx.ChannelRT0, ctx.ChannelRT0Down);
                    Graphics.Blit(ctx.ChannelRT1Mipped ?? ctx.ChannelRT1, ctx.ChannelRT1Down);
                    Graphics.Blit(ctx.ChannelRT2Mipped ?? ctx.ChannelRT2, ctx.ChannelRT2Down);
                }

                Graphics.SetRenderTarget(null);
            }
        }
    }

    // Internal struct used by ClearAll for zero-fill when GPU promotes half→float
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct SimulationDisplay_iparticle_gpu
    {
        public float fire, water, plantSeeded, plantGrown;
        public float steam, glitter, blackBody;
        public float electricitySeeded, electricityGrown;
        public float ice;
        public float red, green, blue, alpha;
    }
}
