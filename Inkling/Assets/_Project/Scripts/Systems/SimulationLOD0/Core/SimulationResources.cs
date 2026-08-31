using System;
using System.Runtime.InteropServices;
using UnityEngine;
using Magi.UnityTools.Core;
using Magi.InkTools;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Manages allocation and disposal of all GPU resources (render textures, compute buffers)
    /// used by the fluid simulation. SimDriver calls Allocate() in Start and Dispose() in OnDestroy.
    /// </summary>
    public class SimulationResources : IDisposable
    {
        public void Allocate(SimulationContext ctx)
        {
            int resolution = ctx.Resolution;

            // Ping-pong render textures
            ctx.Velocity = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.RGHalf, "Velocity");
            ctx.Pressure = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.RHalf, "Pressure");
            ctx.Density = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.ARGBHalf, "Density");
            // Heat: scalar environment/temperature layer. RHalf is sufficient for CP1; bump to
            // RFloat only if latent-heat accumulation later needs the precision/range.
            ctx.Heat = new PingPongRenderTexture(resolution, resolution, RenderTextureFormat.RHalf, "Heat");

            // Single render textures
            ctx.Divergence = CreateRT(resolution, RenderTextureFormat.RHalf, "Divergence");
            // Velocity snapshot taken BEFORE ApplyObstacleBoundary clips it.
            // CP8z: allocated ONLY to support the legacy advective A/B mode (HeatObstacleMode == 1). The
            // default strict conduction-only model never binds it — AdvectHeat uses the clipped field,
            // because fluid cannot enter a solid and heat may not advect where mass cannot. Retained so
            // the Fire-vs-Ice harness can still compare the two models; it costs one RGHalf RT.
            ctx.VelocityThermal = CreateRT(resolution, RenderTextureFormat.RGHalf, "VelocityThermal");
            ctx.VorticityTex = CreateRT(resolution, RenderTextureFormat.RHalf, "Vorticity");
            ctx.Obstacles = CreateRT(resolution, RenderTextureFormat.RFloat, "Obstacles");
            ctx.CreatureInkBuffer = CreateRT(resolution, RenderTextureFormat.ARGBHalf, "CreatureInk");
            // Reaction impulse VECTOR accumulator (RG = xy impulse direction*intensity). Always
            // allocated so the InkInteractions kernel's UAV binding is valid even when the feature is off.
            ctx.ReactionImpulseTex = CreateRT(resolution, RenderTextureFormat.RGFloat, "ReactionImpulse");

            // Particle buffers
            AllocateParticleBuffers(ctx);

            // Thermal rule buffers (CP7d): fixed capacity, allocated once. Always created (even when
            // thermal interactions are off) so the kernel's StructuredBuffer bindings are always valid.
            ctx.ThermalTransitionBuffer = new ComputeBuffer(
                ThermalRuleBaker.MaxTransitions, GpuThermalTransition.Stride, ComputeBufferType.Structured);
            ctx.ThermalSourceBuffer = new ComputeBuffer(
                ThermalRuleBaker.MaxSources, GpuThermalSource.Stride, ComputeBufferType.Structured);

            // Channel textures for particle-authoritative gradient rendering
            ctx.ChannelRT0 = CreateChannelRT(resolution, RenderTextureFormat.ARGBFloat, "Channels0_fire_water_plant");
            ctx.ChannelRT1 = CreateChannelRT(resolution, RenderTextureFormat.ARGBFloat, "Channels1_steam_glitter_bb_ice");
            ctx.ChannelRT2 = CreateChannelRT(resolution, RenderTextureFormat.ARGBFloat, "Channels2_electricity");
            ctx.ChannelRT0Mipped = CreateChannelMippedRT(resolution, RenderTextureFormat.ARGBFloat, "Channels0_fire_water_plant_mipped");
            ctx.ChannelRT1Mipped = CreateChannelMippedRT(resolution, RenderTextureFormat.ARGBFloat, "Channels1_steam_glitter_bb_ice_mipped");
            ctx.ChannelRT2Mipped = CreateChannelMippedRT(resolution, RenderTextureFormat.ARGBFloat, "Channels2_electricity_mipped");

            // Display resolution
            ctx.EffectiveDisplayRes = ctx.EffectiveDisplayRes > 0
                ? ctx.EffectiveDisplayRes
                : Mathf.Max(32, resolution);

            // Downsampled copies at display resolution for 1:1 sampling
            if (ctx.EffectiveDisplayRes < resolution)
            {
                ctx.ChannelRT0Down = CreateChannelDownRT(ctx.EffectiveDisplayRes, RenderTextureFormat.ARGBFloat, "Channels0_fire_water_plant_down");
                ctx.ChannelRT1Down = CreateChannelDownRT(ctx.EffectiveDisplayRes, RenderTextureFormat.ARGBFloat, "Channels1_steam_glitter_bb_ice_down");
                ctx.ChannelRT2Down = CreateChannelDownRT(ctx.EffectiveDisplayRes, RenderTextureFormat.ARGBFloat, "Channels2_electricity_down");
            }

            // Display RT
            ctx.DisplayRT = new RenderTexture(ctx.EffectiveDisplayRes, ctx.EffectiveDisplayRes, 0, RenderTextureFormat.ARGBHalf);
            ctx.DisplayRT.filterMode = FilterMode.Bilinear;
            ctx.DisplayRT.wrapMode = TextureWrapMode.Clamp;
            ctx.DisplayRT.name = "DisplayRT";
            ctx.DisplayRT.Create();

            Debug.Log($"[SimulationResources] Allocated {resolution}x{resolution} simulation, " +
                $"display {ctx.EffectiveDisplayRes}x{ctx.EffectiveDisplayRes}");
        }

        private void AllocateParticleBuffers(SimulationContext ctx)
        {
            int resolution = ctx.Resolution;
            int particleCount = resolution * resolution;

            // Force float stride for all platforms to avoid half/float promotion mismatch.
            // D2/M0: iparticle now uses `ifloat`, which DEFAULTS TO FLOAT (see InkTools Types.cs /
            // InkToolsTypes.hlsl); M0 grew it to 15 fields = 60 bytes and the "force float stride" contract
            // holds. The float readback mirrors (SimulationDisplay.iparticle_gpu,
            // FluidSolver.SimulationDisplay_iparticle_gpu) are the 60-byte layout this must match.
            ctx.GpuPromotesHalf = true;
            ctx.GpuParticleStride = Marshal.SizeOf<iparticle>();

            // LAYOUT GUARD (D2/M0): the particle StructuredBuffer contract assumes a 60-byte float layout,
            // and the readback mirrors are hand-declared as 15 floats. If a future half-storage experiment
            // (D3) flips `ifloat` to half WITHOUT reworking the stride/mirror path, iparticle becomes
            // 30 bytes here while shaders/mirrors still expect 60 -> silent particle corruption. FAIL FAST
            // BEFORE allocating the buffer — the failure mode is silent corruption, so a logged-and-continue
            // guard is not safe. When D3 legitimately moves to half, update this guard deliberately.
            const int ExpectedFloatStride = 15 * sizeof(float); // 60 (M0: Metal field added at index 10)
            int mirrorStride = Marshal.SizeOf<SimulationDisplay_iparticle_gpu>();
            if (ctx.GpuParticleStride != ExpectedFloatStride || mirrorStride != ExpectedFloatStride)
            {
                throw new InvalidOperationException(
                    "[SimulationResources] iparticle layout guard FAILED: " +
                    $"stride={ctx.GpuParticleStride}, mirror={mirrorStride}, expected {ExpectedFloatStride}. " +
                    "`ifloat` must resolve to float for the 60-byte particle buffer contract; a half flip " +
                    "needs the D3 per-backend stride/mirror rework before the buffer can be allocated safely.");
            }

            ctx.ParticlesBuffer = new ComputeBuffer[2];
            for (int i = 0; i < 2; i++)
            {
                ctx.ParticlesBuffer[i] = new ComputeBuffer(particleCount, ctx.GpuParticleStride, ComputeBufferType.Default);
            }

            Debug.Log($"[SimulationResources] Allocated particle buffer: {particleCount} particles, " +
                $"stride {ctx.GpuParticleStride} bytes (float stride), " +
                $"API={SystemInfo.graphicsDeviceType}");
        }

        public void Dispose()
        {
            Dispose(null);
        }

        public void Dispose(SimulationContext ctx)
        {
            if (ctx == null) return;

            ctx.Velocity?.Dispose();
            ctx.Pressure?.Dispose();
            ctx.Density?.Dispose();
            ctx.Heat?.Dispose();

            ReleaseRT(ctx.Divergence);
            ReleaseRT(ctx.VelocityThermal);
            ReleaseRT(ctx.VorticityTex);
            ReleaseRT(ctx.Obstacles);
            ReleaseRT(ctx.ReactionImpulseTex);
            ReleaseRT(ctx.DisplayRT);
            ReleaseRT(ctx.GradientRT);
            ReleaseRT(ctx.CreatureInkBuffer);

            ReleaseRT(ctx.ChannelRT0);
            ReleaseRT(ctx.ChannelRT1);
            ReleaseRT(ctx.ChannelRT2);
            ReleaseRT(ctx.ChannelRT0Mipped);
            ReleaseRT(ctx.ChannelRT1Mipped);
            ReleaseRT(ctx.ChannelRT2Mipped);
            ReleaseRT(ctx.ChannelRT0Down);
            ReleaseRT(ctx.ChannelRT1Down);
            ReleaseRT(ctx.ChannelRT2Down);

            if (ctx.ParticlesBuffer != null)
            {
                for (int i = 0; i < ctx.ParticlesBuffer.Length; i++)
                {
                    ctx.ParticlesBuffer[i]?.Release();
                }
            }

            ctx.ThermalTransitionBuffer?.Release();
            ctx.ThermalTransitionBuffer = null;
            ctx.ThermalSourceBuffer?.Release();
            ctx.ThermalSourceBuffer = null;

            if (ctx.DensityStampMaterial != null)
            {
                UnityEngine.Object.Destroy(ctx.DensityStampMaterial);
                ctx.DensityStampMaterial = null;
            }
        }

        // ── RT creation helpers ─────────────────────────────────────────────

        private static RenderTexture CreateRT(int resolution, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(resolution, resolution, 0, format);
            rt.enableRandomWrite = true;
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = name;
            rt.Create();
            return rt;
        }

        private static RenderTexture CreateChannelRT(int resolution, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(resolution, resolution, 0, format);
            rt.enableRandomWrite = true;
            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.filterMode = FilterMode.Trilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = name;
            rt.Create();
            return rt;
        }

        private static RenderTexture CreateChannelMippedRT(int resolution, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(resolution, resolution, 0, format);
            rt.enableRandomWrite = false;
            rt.useMipMap = true;
            rt.autoGenerateMips = true;
            rt.filterMode = FilterMode.Trilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = name;
            rt.Create();
            return rt;
        }

        private static RenderTexture CreateChannelDownRT(int displayRes, RenderTextureFormat format, string name)
        {
            var rt = new RenderTexture(displayRes, displayRes, 0, format);
            rt.enableRandomWrite = false;
            rt.useMipMap = false;
            rt.autoGenerateMips = false;
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.name = name;
            rt.Create();
            return rt;
        }

        private static void ReleaseRT(RenderTexture rt)
        {
            if (rt != null) rt.Release();
        }
    }
}
