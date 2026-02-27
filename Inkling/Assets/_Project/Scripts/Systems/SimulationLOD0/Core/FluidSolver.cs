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
            fc.SetFloat("_Viscosity", ctx.Viscosity);
            fc.SetFloat("_VorticityStrength", ctx.VorticityStrength);
            fc.SetFloat("_Dissipation", ctx.Dissipation);
            fc.SetVector("_SimulationSize", new Vector2(ctx.Resolution, ctx.Resolution));
            fc.SetFloat("_DebugZeroPressure", ctx.DebugZeroPressure ? 1f : 0f);
            fc.SetFloat("_DebugZeroVelocity", ctx.DebugZeroVelocity ? 1f : 0f);
            fc.SetFloat("_DebugSkipAir", ctx.DebugSkipAir ? 1f : 0f);

            float dx = 1.0f / ctx.Resolution;
            fc.SetFloat("_Alpha", dx * dx / (ctx.Viscosity * ctx.Timestep));
            fc.SetFloat("_InverseBeta", 1.0f / (4.0f + dx * dx / (ctx.Viscosity * ctx.Timestep)));

            fc.SetVector("_ForcePosition", Vector2.zero);
            fc.SetVector("_ForceDirection", Vector2.zero);
            fc.SetFloat("_ForceRadius", ctx.ForceRadius);
            fc.SetFloat("_ForceStrength", 0f);
            fc.SetFloat("_DensityAmount", 0f);

            // Per-ink properties
            SetInkProperties(fc);

            fc.SetVector("_TexelSize", new Vector4(1f / ctx.Resolution, 1f / ctx.Resolution, ctx.Resolution, ctx.Resolution));
        }

        private void SetInkProperties(ComputeShader fc)
        {
            // Dissipation
            fc.SetFloat("_DissipationFire", GetInkProp(InkTypeId.Fire, d => d.dissipation, 0.995f));
            fc.SetFloat("_DissipationWater", GetInkProp(InkTypeId.Water, d => d.dissipation, 0.998f));
            fc.SetFloat("_DissipationPlantSeeded", GetInkProp(InkTypeId.PlantSeeded, d => d.dissipation, 0.997f));
            fc.SetFloat("_DissipationPlantGrown", GetInkProp(InkTypeId.PlantGrown, d => d.dissipation, 0.997f));
            fc.SetFloat("_DissipationSteam", GetInkProp(InkTypeId.Steam, d => d.dissipation, 0.990f));
            fc.SetFloat("_DissipationGlitter", GetInkProp(InkTypeId.Glitter, d => d.dissipation, 0.999f));
            fc.SetFloat("_DissipationBlackBody", GetInkProp(InkTypeId.BlackBody, d => d.dissipation, 0.5f));
            fc.SetFloat("_DissipationElectricitySeeded", GetInkProp(InkTypeId.ElectricitySeeded, d => d.dissipation, 0.985f));
            fc.SetFloat("_DissipationElectricityGrown", GetInkProp(InkTypeId.ElectricityGrown, d => d.dissipation, 0.985f));
            fc.SetFloat("_DissipationIce", GetInkProp(InkTypeId.Ice, d => d.dissipation, 0.996f));

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

            // Pressure
            fc.SetFloat("_PressureFire", GetInkProp(InkTypeId.Fire, d => d.pressureWeight, 1.0f));
            fc.SetFloat("_PressureWater", GetInkProp(InkTypeId.Water, d => d.pressureWeight, 1.0f));
            fc.SetFloat("_PressurePlantSeeded", GetInkProp(InkTypeId.PlantSeeded, d => d.pressureWeight, 1.0f));
            fc.SetFloat("_PressurePlantGrown", GetInkProp(InkTypeId.PlantGrown, d => d.pressureWeight, 1.0f));
            fc.SetFloat("_PressureSteam", GetInkProp(InkTypeId.Steam, d => d.pressureWeight, 1.0f));
            fc.SetFloat("_PressureGlitter", GetInkProp(InkTypeId.Glitter, d => d.pressureWeight, 1.0f));
            fc.SetFloat("_PressureBlackBody", GetInkProp(InkTypeId.BlackBody, d => d.pressureWeight, 1.0f));
            fc.SetFloat("_PressureElectricitySeeded", GetInkProp(InkTypeId.ElectricitySeeded, d => d.pressureWeight, 1.0f));
            fc.SetFloat("_PressureElectricityGrown", GetInkProp(InkTypeId.ElectricityGrown, d => d.pressureWeight, 1.0f));
            fc.SetFloat("_PressureIce", GetInkProp(InkTypeId.Ice, d => d.pressureWeight, 1.0f));
        }

        private float GetInkProp(InkTypeId type, System.Func<InkTypeDef, float> getter, float defaultValue)
        {
            int idx = (int)type;
            if (ctx.InkDefinitions != null && idx < ctx.InkDefinitions.Length && ctx.InkDefinitions[idx] != null)
                return getter(ctx.InkDefinitions[idx]);
            return defaultValue;
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

            ctx.FluidCompute.SetTexture(ctx.FluidKernelClear, "_VelocityWrite", ctx.Velocity.Write);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelClear, "_DensityWrite", ctx.Density.Write);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelClear, "_PressureWrite", ctx.Pressure.Write);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelClear, "_DivergenceWrite", ctx.Divergence);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelClear, "_VorticityMag", ctx.VorticityTex);

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

            ctx.FluidCompute.Dispatch(ctx.FluidKernelClear, threadGroups, threadGroups, 1);
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

            // 1. Advection
            if (sw != null) sw.Restart();

            ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_VelocityRead", ctx.Velocity.Read);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_VelocityWrite", ctx.Velocity.Write);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_QuantityRead", ctx.Velocity.Read);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_QuantityWrite", ctx.Velocity.Write);
            ctx.FluidCompute.SetFloat("_Dissipation", ctx.VelocityDissipation);
            ctx.FluidCompute.Dispatch(ctx.FluidKernelAdvection, threadGroups, threadGroups, 1);
            ctx.Velocity.Swap();

            // Advect density
            if (ctx.Density != null)
            {
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_VelocityRead", ctx.Velocity.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_VelocityWrite", ctx.Velocity.Write);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_QuantityRead", ctx.Density.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAdvection, "_QuantityWrite", ctx.Density.Write);
                ctx.FluidCompute.SetFloat("_Dissipation", ctx.Dissipation);
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

                        foreach (var group in ctx.AffinityGroups)
                        {
                            if (group == null) continue;

                            int[] indices = group.GetInkIndices();
                            ctx.InkInteractionsCompute.SetInts("_InkIndices", indices);
                            ctx.InkInteractionsCompute.SetMatrix("_ProductMatrix", group.productMatrix);
                            ctx.InkInteractionsCompute.SetVector("_ProductCol4", group.productCol4);
                            ctx.InkInteractionsCompute.SetVector("_ProductCol5", group.productCol5);
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

            // 3. Vorticity confinement
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

                if (sw != null) VorticityMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 4. Pressure projection
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

            // 5. Subtract gradient
            if (sw != null) sw.Restart();

            ctx.FluidCompute.SetTexture(ctx.FluidKernelSubtractGradient, "_VelocityRead", ctx.Velocity.Read);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelSubtractGradient, "_VelocityWrite", ctx.Velocity.Write);
            ctx.FluidCompute.SetTexture(ctx.FluidKernelSubtractGradient, "_PressureRead", ctx.Pressure.Read);
            ctx.FluidCompute.Dispatch(ctx.FluidKernelSubtractGradient, threadGroups, threadGroups, 1);
            ctx.Velocity.Swap();

            // 6. Obstacle boundaries
            if (ctx.FluidKernelApplyObstacleBoundary != 0)
            {
                ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_VelocityRead", ctx.Velocity.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_VelocityWrite", ctx.Velocity.Write);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelApplyObstacleBoundary, "_ObstacleRead", ctx.Obstacles);
                ctx.FluidCompute.Dispatch(ctx.FluidKernelApplyObstacleBoundary, threadGroups, threadGroups, 1);
                ctx.Velocity.Swap();
            }

            if (sw != null) ProjectionMs = (float)sw.Elapsed.TotalMilliseconds;

            // 7. Channel splat
            if (ctx.UseParticleSimulation && opQueue.ChannelSplatReady && ctx.ParticlesBuffer != null
                && ctx.ChannelRT0 != null && ctx.ChannelRT1 != null && ctx.ChannelRT2 != null)
            {
                ctx.ParticleChannelSplatCompute.SetInt("_Resolution", ctx.Resolution);
                ctx.ParticleChannelSplatCompute.SetBuffer(opQueue.KernelChannelSplat, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                ctx.ParticleChannelSplatCompute.SetTexture(opQueue.KernelChannelSplat, "_Channels0", ctx.ChannelRT0);
                ctx.ParticleChannelSplatCompute.SetTexture(opQueue.KernelChannelSplat, "_Channels1", ctx.ChannelRT1);
                ctx.ParticleChannelSplatCompute.SetTexture(opQueue.KernelChannelSplat, "_Channels2", ctx.ChannelRT2);
                int splatGroups = Mathf.CeilToInt(ctx.Resolution / 8f);
                ctx.ParticleChannelSplatCompute.Dispatch(opQueue.KernelChannelSplat, splatGroups, splatGroups, 1);

                if (ctx.ChannelRT0Mipped != null && ctx.ChannelRT1Mipped != null && ctx.ChannelRT2Mipped != null)
                {
                    Graphics.Blit(ctx.ChannelRT0, ctx.ChannelRT0Mipped);
                    Graphics.Blit(ctx.ChannelRT1, ctx.ChannelRT1Mipped);
                    Graphics.Blit(ctx.ChannelRT2, ctx.ChannelRT2Mipped);
                }

                if (ctx.EffectiveDisplayRes < ctx.Resolution &&
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
