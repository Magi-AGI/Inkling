using System.Collections.Generic;
using UnityEngine;
using Magi.InkTools;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Manages pending GPU operations (density stamps, force injections, density injections,
    /// clear-density masks) queued by external callers. ProcessPending() drains all queues
    /// at the top of SimulateFrame so GPU work runs in a single contiguous command stream.
    /// </summary>
    public class OperationQueue
    {
        private readonly SimulationContext ctx;

        // ── Pending operation structs ───────────────────────────────────────

        private struct PendingDensityStamp
        {
            public Vector2 uvPosition;
            public Texture2D stamp;
            public float multiplier;
            public bool useColorOverride;
            public Color overrideColor;
        }

        private struct PendingForceInjection
        {
            public Vector2 position;
            public Vector2 force;
        }

        private struct PendingDensityInjection
        {
            public Vector2 position;
            public Color color;
            public int inkTypeIndex;
        }

        private struct PendingClearDensityMask
        {
            public Vector2 uvPosition;
            public Texture2D mask;
            public float blackLuminanceThreshold;
        }

        // ── Queues ──────────────────────────────────────────────────────────

        private readonly List<PendingDensityStamp> pendingDensityStamps = new List<PendingDensityStamp>();
        private readonly List<PendingForceInjection> pendingForceInjections = new List<PendingForceInjection>();
        private readonly List<PendingDensityInjection> pendingDensityInjections = new List<PendingDensityInjection>();
        private readonly List<PendingClearDensityMask> pendingClearDensityMasks = new List<PendingClearDensityMask>();

        // ── Kernel indices ──────────────────────────────────────────────────

        private int kernelStampDensity;
        private int kernelClearBlackDensity;
        private bool stampComputeReady;
        private int kernelStampParticles;
        private bool stampParticlesComputeReady;
        private int kernelChannelSplat;
        private bool channelSplatReady;
        private int kernelInkInteractions;
        private bool inkInteractionsReady;
        private int kernelStampDensityBatched;
        private int kernelStampParticlesBatched;
        private bool batchedStampReady;
        private int kernelClearMaskBatched;
        private bool batchedMaskReady;
        private int kernelAddDensityBatched;
        private int kernelAddParticlesBatched;
        private bool batchedInjectionReady;

        // ── One-time warning flags ──────────────────────────────────────────

        private bool loggedStampBatchMixedTextures;
        private bool loggedStampBatchUnavailable;
        private bool loggedMaskBatchUnavailable;
        private bool hasLoggedFirstParticleStamp;

        public bool ChannelSplatReady => channelSplatReady;
        public int KernelChannelSplat => kernelChannelSplat;
        public bool InkInteractionsReady => inkInteractionsReady;
        public int KernelInkInteractions => kernelInkInteractions;
        public bool BatchedInjectionReady => batchedInjectionReady;
        public int KernelAddDensityBatched => kernelAddDensityBatched;
        public int KernelAddParticlesBatched => kernelAddParticlesBatched;

        public OperationQueue(SimulationContext context)
        {
            ctx = context;
        }

        // ── Public enqueue API ──────────────────────────────────────────────

        public void EnqueueDensityStamp(Vector2 uvPosition, Texture2D stamp, float multiplier, bool useColorOverride, Color overrideColor)
        {
            pendingDensityStamps.Add(new PendingDensityStamp
            {
                uvPosition = uvPosition,
                stamp = stamp,
                multiplier = multiplier,
                useColorOverride = useColorOverride,
                overrideColor = overrideColor
            });
        }

        public void EnqueueForceInjection(Vector2 position, Vector2 force)
        {
            pendingForceInjections.Add(new PendingForceInjection
            {
                position = position,
                force = force
            });
        }

        public void EnqueueDensityInjection(Vector2 position, Color color, int inkTypeIndex)
        {
            pendingDensityInjections.Add(new PendingDensityInjection
            {
                position = position,
                color = color,
                inkTypeIndex = inkTypeIndex
            });
        }

        public void EnqueueClearDensityMask(Vector2 uvPosition, Texture2D mask, float blackLuminanceThreshold)
        {
            pendingClearDensityMasks.Add(new PendingClearDensityMask
            {
                uvPosition = uvPosition,
                mask = mask,
                blackLuminanceThreshold = blackLuminanceThreshold
            });
        }

        // ── Kernel initialization ───────────────────────────────────────────

        public void InitializeKernels()
        {
            InitializeStampCompute();
            InitializeBatchedInjection();
        }

        private static bool TryFindKernel(ComputeShader compute, string kernelName, out int kernel)
        {
            kernel = -1;
            if (compute == null) return false;
            if (!compute.HasKernel(kernelName)) return false;

            try
            {
                kernel = compute.FindKernel(kernelName);
                return kernel >= 0;
            }
            catch
            {
                return false;
            }
        }

        private void InitializeStampCompute()
        {
            // Stamp compute
            if (ctx.StampCompute != null)
            {
                if (TryFindKernel(ctx.StampCompute, "StampDensity", out kernelStampDensity) &&
                    TryFindKernel(ctx.StampCompute, "ClearBlackDensity", out kernelClearBlackDensity))
                {
                    stampComputeReady = true;
                    Debug.Log("[SimDriver] Stamp compute shader ready - density stamps will use compute pipeline.");
                }
                else
                {
                    Debug.LogWarning("[SimDriver] Stamp compute init failed (missing StampDensity/ClearBlackDensity kernel). Falling back to Blit-based stamps.");
                    stampComputeReady = false;
                }
            }

            // Stamp particles compute
            if (ctx.StampParticlesCompute != null)
            {
                if (TryFindKernel(ctx.StampParticlesCompute, "StampParticles", out kernelStampParticles))
                {
                    stampParticlesComputeReady = true;
                    Debug.Log("[SimDriver] Stamp particles compute shader ready - particle stamps will use GPU pipeline.");
                }
                else
                {
                    Debug.LogWarning("[SimDriver] Stamp particles compute init failed (missing StampParticles kernel). Falling back to CPU particle stamps.");
                    stampParticlesComputeReady = false;
                }
            }

            // Channel splat compute
            if (ctx.ParticleChannelSplatCompute != null)
            {
                if (TryFindKernel(ctx.ParticleChannelSplatCompute, "ChannelSplat", out kernelChannelSplat))
                {
                    channelSplatReady = true;
                    Debug.Log("[SimDriver] Channel splat compute shader ready - gradient rendering will use particle channel textures.");
                }
                else
                {
                    Debug.LogWarning("[SimDriver] Channel splat compute init failed (missing ChannelSplat kernel). Gradient rendering will fall back to density RT.");
                    channelSplatReady = false;
                }
            }

            // Ink interactions compute
            if (ctx.InkInteractionsCompute != null)
            {
                if (TryFindKernel(ctx.InkInteractionsCompute, "InkInteractions", out kernelInkInteractions))
                {
                    inkInteractionsReady = true;
                    Debug.Log("[SimDriver] Ink interactions compute shader ready - cellular automata reactions enabled.");
                }
                else
                {
                    Debug.LogWarning("[SimDriver] Ink interactions compute init failed (missing InkInteractions kernel). Ink reactions disabled.");
                    inkInteractionsReady = false;
                }
            }

            // Batched stamp compute
            if (ctx.BatchedStampCompute != null)
            {
                if (TryFindKernel(ctx.BatchedStampCompute, "StampDensityBatched", out kernelStampDensityBatched) &&
                    TryFindKernel(ctx.BatchedStampCompute, "StampParticlesBatched", out kernelStampParticlesBatched))
                {
                    batchedStampReady = true;
                    Debug.Log("[SimDriver] Batched stamp compute ready.");
                }
                else
                {
                    batchedStampReady = false;
                    Debug.LogWarning("[SimDriver] Batched stamp compute init failed (missing StampDensityBatched/StampParticlesBatched kernel). Falling back to non-batched stamping.");
                }
            }

            // Batched mask compute
            if (ctx.BatchedMaskCompute != null)
            {
                if (TryFindKernel(ctx.BatchedMaskCompute, "ClearMaskBatched", out kernelClearMaskBatched))
                {
                    batchedMaskReady = true;
                    Debug.Log("[SimDriver] Batched mask compute ready.");
                }
                else
                {
                    batchedMaskReady = false;
                    Debug.LogWarning("[SimDriver] Batched mask compute init failed (missing ClearMaskBatched kernel). Falling back to non-batched mask clears.");
                }
            }
        }

        private void InitializeBatchedInjection()
        {
            if (ctx.BatchedInjectionCompute != null)
            {
                if (TryFindKernel(ctx.BatchedInjectionCompute, "AddDensityBatched", out kernelAddDensityBatched) &&
                    TryFindKernel(ctx.BatchedInjectionCompute, "AddParticlesBatched", out kernelAddParticlesBatched))
                {
                    batchedInjectionReady = true;
                    Debug.Log("[SimDriver] Batched injection compute ready (density + particles).");
                }
                else
                {
                    batchedInjectionReady = false;
                    Debug.LogWarning("[SimDriver] Batched injection compute init failed (missing AddDensityBatched/AddParticlesBatched kernel). Falling back to per-injection dispatch.");
                }
            }
        }
        // ── Stamp material ──────────────────────────────────────────────────

        private void EnsureStampMaterial()
        {
            if (ctx.DensityStampMaterial != null) return;

            Shader shader = ctx.DensityStampShader != null
                ? ctx.DensityStampShader
                : Shader.Find("Hidden/Magi/StampDensity");

            if (shader == null)
            {
                Debug.LogWarning("[SimDriver] Could not find stamp shader 'Hidden/Magi/StampDensity'. Creature stamping will be disabled.");
                return;
            }

            ctx.DensityStampMaterial = new Material(shader);
        }

        // ── Ink key color palette ───────────────────────────────────────────

        private Vector4[] BuildInkKeyColorPalette()
        {
            var palette = new Vector4[10];
            var defaults = new Color[]
            {
                new Color(1f, 0.3f, 0f),
                new Color(0f, 0.5f, 1f),
                new Color(0.2f, 0.8f, 0.2f),
                new Color(0f, 0.5f, 0f),
                new Color(0.8f, 0.8f, 0.9f),
                new Color(1f, 0.8f, 0f),
                new Color(0.1f, 0.1f, 0.1f),
                new Color(1f, 1f, 0f),
                new Color(0.5f, 0f, 1f),
                new Color(0.5f, 0.8f, 1f),
            };

            for (int i = 0; i < 10; i++)
            {
                Color keyColor = defaults[i];
                float tolerance = 0.3f;

                if (ctx.InkDefinitions != null && i < ctx.InkDefinitions.Length && ctx.InkDefinitions[i] != null)
                {
                    keyColor = ctx.InkDefinitions[i].inputKeyColor;
                    tolerance = ctx.InkDefinitions[i].colorMatchTolerance;
                }

                palette[i] = new Vector4(keyColor.r, keyColor.g, keyColor.b, tolerance);
            }

            return palette;
        }

        // ── Process all pending operations ──────────────────────────────────

        public void ProcessPending()
        {
            int threadGroups = Mathf.CeilToInt(ctx.Resolution / 8f);

            ProcessDensityStamps(threadGroups);
            ProcessClearDensityMasks(threadGroups);
            ProcessForceInjections(threadGroups);
            ProcessDensityInjections(threadGroups);
        }

        private void ProcessDensityStamps(int threadGroups)
        {
            if (pendingDensityStamps.Count == 0 || ctx.Density == null) return;

            if (stampComputeReady)
            {
                bool canBatch = ctx.UseBatchedStamping && batchedStampReady;
                if (canBatch)
                {
                    Texture2D firstTex = pendingDensityStamps[0].stamp;
                    for (int i = 0; i < pendingDensityStamps.Count; i++)
                    {
                        if (pendingDensityStamps[i].stamp != firstTex || firstTex == null)
                        {
                            canBatch = false;
                            break;
                        }
                    }
                    if (canBatch)
                    {
                        int count = pendingDensityStamps.Count;
                        var payloadA = new Vector4[count];
                        var payloadB = new Vector4[count];
                        var payloadC = new Vector4[count];
                        var payloadD = new Vector4[count];
                        for (int i = 0; i < count; i++)
                        {
                            var s = pendingDensityStamps[i];
                            float stampWidthUV = (float)s.stamp.width / ctx.Resolution;
                            float stampHeightUV = (float)s.stamp.height / ctx.Resolution;
                            payloadA[i] = new Vector4(s.uvPosition.x, s.uvPosition.y, stampWidthUV, stampHeightUV);
                            payloadB[i] = Vector4.zero;
                            payloadC[i] = new Vector4(0.01f, s.multiplier, s.useColorOverride ? 1f : 0f, 0f);
                            payloadD[i] = (Vector4)s.overrideColor;
                        }

                        using (var bufA = new ComputeBuffer(count, sizeof(float) * 4))
                        using (var bufB = new ComputeBuffer(count, sizeof(float) * 4))
                        using (var bufC = new ComputeBuffer(count, sizeof(float) * 4))
                        using (var bufD = new ComputeBuffer(count, sizeof(float) * 4))
                        {
                            bufA.SetData(payloadA);
                            bufB.SetData(payloadB);
                            bufC.SetData(payloadC);
                            bufD.SetData(payloadD);

                            ctx.BatchedStampCompute.SetInt("_StampCount", count);
                            ctx.BatchedStampCompute.SetVector("_Resolution", new Vector2(ctx.Resolution, ctx.Resolution));
                            ctx.BatchedStampCompute.SetBuffer(kernelStampDensityBatched, "_StampPayloadA", bufA);
                            ctx.BatchedStampCompute.SetBuffer(kernelStampDensityBatched, "_StampPayloadB", bufB);
                            ctx.BatchedStampCompute.SetBuffer(kernelStampDensityBatched, "_StampPayloadC", bufC);
                            ctx.BatchedStampCompute.SetBuffer(kernelStampDensityBatched, "_StampPayloadD", bufD);
                            ctx.BatchedStampCompute.SetTexture(kernelStampDensityBatched, "_StampTex", firstTex);
                            ctx.BatchedStampCompute.SetTexture(kernelStampDensityBatched, "_DensityRead", ctx.Density.Read);
                            ctx.BatchedStampCompute.SetTexture(kernelStampDensityBatched, "_DensityWrite", ctx.Density.Write);
                            ctx.BatchedStampCompute.Dispatch(kernelStampDensityBatched, threadGroups, threadGroups, 1);
                            ctx.Density.Swap();
                        }
                    }
                }

                if (!canBatch)
                {
                    if (ctx.UseBatchedStamping && !batchedStampReady && !loggedStampBatchUnavailable)
                    {
                        loggedStampBatchUnavailable = true;
                        Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal("[SimDriver] Stamp batching requested but batchedStampCompute is not ready; falling back to per-stamp dispatch.");
                    }
                    if (ctx.UseBatchedStamping && batchedStampReady && !loggedStampBatchMixedTextures && pendingDensityStamps.Count > 1)
                    {
                        loggedStampBatchMixedTextures = true;
                        Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal("[SimDriver] Stamp batching skipped because stamps use different textures; running per-stamp dispatch.");
                    }

                    foreach (var s in pendingDensityStamps)
                    {
                        if (s.stamp == null) continue;

                        float stampWidthUV  = (float)s.stamp.width  / ctx.Resolution;
                        float stampHeightUV = (float)s.stamp.height / ctx.Resolution;

                        ctx.StampCompute.SetTexture(kernelStampDensity, "_DensityRead",  ctx.Density.Read);
                        ctx.StampCompute.SetTexture(kernelStampDensity, "_DensityWrite", ctx.Density.Write);
                        ctx.StampCompute.SetTexture(kernelStampDensity, "_StampTex",     s.stamp);
                        ctx.StampCompute.SetVector("_StampCenter", new Vector4(s.uvPosition.x, s.uvPosition.y, 0f, 0f));
                        ctx.StampCompute.SetVector("_StampSize",   new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                        ctx.StampCompute.SetFloat("_AlphaThreshold", 0.01f);
                        ctx.StampCompute.SetFloat("_DensityMul",     s.multiplier);
                        ctx.StampCompute.SetFloat("_UseOverride",    s.useColorOverride ? 1f : 0f);
                        ctx.StampCompute.SetVector("_OverrideColor", (Vector4)s.overrideColor);
                        ctx.StampCompute.SetVector("_Resolution",    new Vector2(ctx.Resolution, ctx.Resolution));

                        ctx.StampCompute.Dispatch(kernelStampDensity, threadGroups, threadGroups, 1);
                        ctx.Density.Swap();
                    }
                }
            }
            else
            {
                // Blit fallback
                EnsureStampMaterial();
                if (ctx.DensityStampMaterial != null)
                {
                    foreach (var s in pendingDensityStamps)
                    {
                        if (s.stamp == null) continue;

                        float stampWidthUV  = (float)s.stamp.width  / ctx.Resolution;
                        float stampHeightUV = (float)s.stamp.height / ctx.Resolution;

                        ctx.DensityStampMaterial.SetTexture("_StampTex", s.stamp);
                        ctx.DensityStampMaterial.SetVector("_StampCenterUV", new Vector4(s.uvPosition.x, s.uvPosition.y, 0f, 0f));
                        ctx.DensityStampMaterial.SetVector("_StampSizeUV", new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                        ctx.DensityStampMaterial.SetFloat("_AlphaThreshold", 0.01f);
                        ctx.DensityStampMaterial.SetFloat("_StampMode", 0f);
                        ctx.DensityStampMaterial.SetFloat("_BlackLuminanceThreshold", 0.2f);
                        ctx.DensityStampMaterial.SetFloat("_DensityMultiplier", s.multiplier);
                        ctx.DensityStampMaterial.SetFloat("_UseColorOverride", s.useColorOverride ? 1f : 0f);
                        ctx.DensityStampMaterial.SetColor("_ColorOverride", s.overrideColor);

                        Graphics.Blit(ctx.Density.Read, ctx.Density.Write, ctx.DensityStampMaterial);
                        ctx.Density.Swap();
                    }
                }
            }

            // GPU particle stamps
            if (stampParticlesComputeReady && ctx.ParticlesBuffer != null)
            {
                Vector4[] inkPalette = BuildInkKeyColorPalette();
                ctx.StampParticlesCompute.SetVectorArray("_InkKeyColors", inkPalette);
                ctx.StampParticlesCompute.SetInt("_NumActiveInks", 10);
                ctx.StampParticlesCompute.SetInt("_UsePaletteLookup", 1);

                if (!hasLoggedFirstParticleStamp && pendingDensityStamps.Count > 0)
                {
                    hasLoggedFirstParticleStamp = true;
                    var firstStamp = pendingDensityStamps[0];
                    Debug.Log($"[SimDriver] First particle stamp: texture={firstStamp.stamp?.name}, " +
                              $"pos={firstStamp.uvPosition}, mul={firstStamp.multiplier}, " +
                              $"useOverride={firstStamp.useColorOverride}, override={firstStamp.overrideColor}");
                    for (int i = 0; i < 10; i++)
                    {
                        Debug.Log($"[SimDriver] Ink palette[{i}]: RGB=({inkPalette[i].x:F2},{inkPalette[i].y:F2},{inkPalette[i].z:F2}), tolerance={inkPalette[i].w:F2}");
                    }
                }

                foreach (var s in pendingDensityStamps)
                {
                    if (s.stamp == null) continue;

                    float stampWidthUV  = (float)s.stamp.width  / ctx.Resolution;
                    float stampHeightUV = (float)s.stamp.height / ctx.Resolution;

                    ctx.StampParticlesCompute.SetBuffer(kernelStampParticles, "_ParticlesRW",
                        ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                    ctx.StampParticlesCompute.SetTexture(kernelStampParticles, "_StampTex", s.stamp);
                    ctx.StampParticlesCompute.SetVector("_StampCenter", new Vector4(s.uvPosition.x, s.uvPosition.y, 0f, 0f));
                    ctx.StampParticlesCompute.SetVector("_StampSize",   new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                    ctx.StampParticlesCompute.SetFloat("_AlphaThreshold", 0.01f);
                    ctx.StampParticlesCompute.SetFloat("_DensityMul",     s.multiplier);
                    ctx.StampParticlesCompute.SetFloat("_UseOverride",    s.useColorOverride ? 1f : 0f);
                    ctx.StampParticlesCompute.SetVector("_OverrideColor", (Vector4)s.overrideColor);
                    ctx.StampParticlesCompute.SetVector("_Resolution",    new Vector2(ctx.Resolution, ctx.Resolution));

                    ctx.StampParticlesCompute.Dispatch(kernelStampParticles, threadGroups, threadGroups, 1);
                }
            }
            else if (!hasLoggedFirstParticleStamp && pendingDensityStamps.Count > 0)
            {
                hasLoggedFirstParticleStamp = true;
                Debug.LogWarning($"[SimDriver] Particle stamps SKIPPED - stampParticlesComputeReady={stampParticlesComputeReady}, " +
                                 $"particlesBuffer={(ctx.ParticlesBuffer != null ? "valid" : "null")}. " +
                                 "Assign StampParticlesCompute in Inspector for creature visibility.");
            }

            pendingDensityStamps.Clear();
        }

        private void ProcessClearDensityMasks(int threadGroups)
        {
            if (pendingClearDensityMasks.Count == 0 || ctx.Density == null) return;

            bool canBatchMasks = ctx.UseBatchedMasks && batchedMaskReady;
            if (canBatchMasks)
            {
                Texture2D firstMask = pendingClearDensityMasks[0].mask;
                for (int i = 0; i < pendingClearDensityMasks.Count; i++)
                {
                    if (pendingClearDensityMasks[i].mask != firstMask || firstMask == null)
                    {
                        canBatchMasks = false;
                        break;
                    }
                }
            }

            if (canBatchMasks)
            {
                int count = pendingClearDensityMasks.Count;
                var payloadA = new Vector4[count];
                var payloadB = new Vector4[count];
                for (int i = 0; i < count; i++)
                {
                    var m = pendingClearDensityMasks[i];
                    float stampWidthUV = (float)m.mask.width / ctx.Resolution;
                    float stampHeightUV = (float)m.mask.height / ctx.Resolution;
                    payloadA[i] = new Vector4(m.uvPosition.x, m.uvPosition.y, stampWidthUV, stampHeightUV);
                    payloadB[i] = new Vector4(0.01f, m.blackLuminanceThreshold, 0f, 0f);
                }

                using (var bufA = new ComputeBuffer(count, sizeof(float) * 4))
                using (var bufB = new ComputeBuffer(count, sizeof(float) * 4))
                {
                    bufA.SetData(payloadA);
                    bufB.SetData(payloadB);

                    ctx.BatchedMaskCompute.SetInt("_MaskCount", count);
                    ctx.BatchedMaskCompute.SetVector("_Resolution", new Vector2(ctx.Resolution, ctx.Resolution));
                    ctx.BatchedMaskCompute.SetBuffer(kernelClearMaskBatched, "_MaskPayloadA", bufA);
                    ctx.BatchedMaskCompute.SetBuffer(kernelClearMaskBatched, "_MaskPayloadB", bufB);
                    ctx.BatchedMaskCompute.SetTexture(kernelClearMaskBatched, "_MaskTex", pendingClearDensityMasks[0].mask);
                    ctx.BatchedMaskCompute.SetTexture(kernelClearMaskBatched, "_DensityRead", ctx.Density.Read);
                    ctx.BatchedMaskCompute.SetTexture(kernelClearMaskBatched, "_DensityWrite", ctx.Density.Write);
                    ctx.BatchedMaskCompute.Dispatch(kernelClearMaskBatched, threadGroups, threadGroups, 1);
                    ctx.Density.Swap();
                }
            }
            else
            {
                if (ctx.UseBatchedMasks && !batchedMaskReady && !loggedMaskBatchUnavailable)
                {
                    loggedMaskBatchUnavailable = true;
                    Magi.Inkling.Services.Diagnostics.LogSink.AddGlobal("[SimDriver] Mask batching requested but batchedMaskCompute is not ready; falling back to per-mask dispatch.");
                }

                foreach (var c in pendingClearDensityMasks)
                {
                    if (c.mask == null) continue;

                    float stampWidthUV  = (float)c.mask.width  / ctx.Resolution;
                    float stampHeightUV = (float)c.mask.height / ctx.Resolution;

                    if (stampComputeReady)
                    {
                        ctx.StampCompute.SetTexture(kernelClearBlackDensity, "_DensityRead",  ctx.Density.Read);
                        ctx.StampCompute.SetTexture(kernelClearBlackDensity, "_DensityWrite", ctx.Density.Write);
                        ctx.StampCompute.SetTexture(kernelClearBlackDensity, "_StampTex",     c.mask);
                        ctx.StampCompute.SetVector("_StampCenter", new Vector4(c.uvPosition.x, c.uvPosition.y, 0f, 0f));
                        ctx.StampCompute.SetVector("_StampSize",   new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                        ctx.StampCompute.SetFloat("_AlphaThreshold", 0.01f);
                        ctx.StampCompute.SetFloat("_BlackLumThreshold", c.blackLuminanceThreshold);
                        ctx.StampCompute.SetVector("_Resolution", new Vector2(ctx.Resolution, ctx.Resolution));

                        ctx.StampCompute.Dispatch(kernelClearBlackDensity, threadGroups, threadGroups, 1);
                        ctx.Density.Swap();
                    }
                    else
                    {
                        EnsureStampMaterial();
                        if (ctx.DensityStampMaterial != null)
                        {
                            ctx.DensityStampMaterial.SetTexture("_StampTex", c.mask);
                            ctx.DensityStampMaterial.SetVector("_StampCenterUV", new Vector4(c.uvPosition.x, c.uvPosition.y, 0f, 0f));
                            ctx.DensityStampMaterial.SetVector("_StampSizeUV", new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                            ctx.DensityStampMaterial.SetFloat("_AlphaThreshold", 0.01f);
                            ctx.DensityStampMaterial.SetFloat("_StampMode", 1f);
                            ctx.DensityStampMaterial.SetFloat("_BlackLuminanceThreshold", c.blackLuminanceThreshold);

                            Graphics.Blit(ctx.Density.Read, ctx.Density.Write, ctx.DensityStampMaterial);
                            ctx.Density.Swap();
                        }
                    }

                    // Obstacle map update
                    if (ctx.Obstacles != null)
                    {
                        EnsureStampMaterial();
                        if (ctx.DensityStampMaterial != null)
                        {
                            RenderTexture tmpObs = RenderTexture.GetTemporary(
                                ctx.Obstacles.width, ctx.Obstacles.height, 0, ctx.Obstacles.format);
                            Graphics.Blit(ctx.Obstacles, tmpObs);

                            ctx.DensityStampMaterial.SetTexture("_MainTex", tmpObs);
                            ctx.DensityStampMaterial.SetTexture("_StampTex", c.mask);
                            ctx.DensityStampMaterial.SetVector("_StampCenterUV", new Vector4(c.uvPosition.x, c.uvPosition.y, 0f, 0f));
                            ctx.DensityStampMaterial.SetVector("_StampSizeUV", new Vector4(stampWidthUV, stampHeightUV, 0f, 0f));
                            ctx.DensityStampMaterial.SetFloat("_AlphaThreshold", 0.01f);
                            ctx.DensityStampMaterial.SetFloat("_StampMode", 2f);
                            ctx.DensityStampMaterial.SetFloat("_BlackLuminanceThreshold", c.blackLuminanceThreshold);

                            Graphics.Blit(tmpObs, ctx.Obstacles, ctx.DensityStampMaterial);
                            RenderTexture.ReleaseTemporary(tmpObs);
                        }
                    }
                }
            }
            pendingClearDensityMasks.Clear();
        }

        /// <summary>
        /// Enable to log force injection details every 60 frames.
        /// Set from SimDriver.debugLogForces via reflection or direct access.
        /// </summary>
        public bool DebugLogForces { get; set; }
        private int forceLogCounter;

        private void ProcessForceInjections(int threadGroups)
        {
            if (pendingForceInjections.Count == 0 || ctx.FluidCompute == null) return;

            foreach (var f in pendingForceInjections)
            {
                Vector2 pixelPos = f.position * ctx.Resolution;

                // Scale force by resolution so inspector values feel consistent across grid sizes.
                // Reference resolution 512: at 1024 forces are 2x, at 256 forces are 0.5x.
                float resScale = ctx.Resolution / 512f;
                float finalStrength = ctx.ForceStrength * f.force.magnitude * resScale;

                if (DebugLogForces && forceLogCounter++ % 60 == 0)
                {
                    Debug.Log($"[ForceInjection] pos={pixelPos}, dir={f.force.normalized:F3}, " +
                        $"strength={finalStrength:F3} (base={ctx.ForceStrength}, mag={f.force.magnitude:F4}, " +
                        $"resScale={resScale:F2}), radius={ctx.ForceRadius}");
                }

                ctx.FluidCompute.SetVector("_ForcePosition", pixelPos);
                ctx.FluidCompute.SetVector("_ForceDirection", f.force.normalized);
                ctx.FluidCompute.SetFloat("_ForceRadius", ctx.ForceRadius);
                ctx.FluidCompute.SetFloat("_ForceStrength", finalStrength);
                ctx.FluidCompute.SetFloat("_DeltaTime", ctx.Timestep);
                ctx.FluidCompute.SetVector("_SimulationSize", new Vector2(ctx.Resolution, ctx.Resolution));

                ctx.FluidCompute.SetTexture(ctx.FluidKernelAddForce, "_VelocityRead", ctx.Velocity.Read);
                ctx.FluidCompute.SetTexture(ctx.FluidKernelAddForce, "_VelocityWrite", ctx.Velocity.Write);
                ctx.FluidCompute.Dispatch(ctx.FluidKernelAddForce, threadGroups, threadGroups, 1);
                ctx.Velocity.Swap();
            }
            pendingForceInjections.Clear();
        }

        private void ProcessDensityInjections(int threadGroups)
        {
            if (pendingDensityInjections.Count == 0 || ctx.FluidCompute == null || ctx.Density == null) return;

            if (ctx.UseBatchedDensityInjection && batchedInjectionReady)
            {
                int count = pendingDensityInjections.Count;
                var injA = new Vector4[count];
                var injB = new Vector4[count];
                for (int i = 0; i < count; i++)
                {
                    var d = pendingDensityInjections[i];
                    float colorIntensity = Mathf.Max(d.color.r, Mathf.Max(d.color.g, d.color.b));
                    if (colorIntensity <= 0f) continue;
                    injA[i] = new Vector4(d.position.x, d.position.y, d.color.r, d.color.g);
                    injB[i] = new Vector4(d.color.b, d.inkTypeIndex, 0f, 0f);
                }

                using (var bufferA = new ComputeBuffer(count, sizeof(float) * 4))
                using (var bufferB = new ComputeBuffer(count, sizeof(float) * 4))
                {
                    bufferA.SetData(injA);
                    bufferB.SetData(injB);

                    ctx.BatchedInjectionCompute.SetInt("_InjectionCount", count);
                    ctx.BatchedInjectionCompute.SetFloat("_ForceRadius", ctx.ForceRadius);
                    ctx.BatchedInjectionCompute.SetFloat("_DensityAmount", ctx.DensityAmount);
                    ctx.BatchedInjectionCompute.SetVector("_Resolution", new Vector2(ctx.Resolution, ctx.Resolution));
                    ctx.BatchedInjectionCompute.SetBuffer(kernelAddDensityBatched, "_Injections", bufferA);
                    ctx.BatchedInjectionCompute.SetBuffer(kernelAddDensityBatched, "_Injections2", bufferB);
                    ctx.BatchedInjectionCompute.SetTexture(kernelAddDensityBatched, "_DensityRead", ctx.Density.Read);
                    ctx.BatchedInjectionCompute.SetTexture(kernelAddDensityBatched, "_DensityWrite", ctx.Density.Write);
                    ctx.BatchedInjectionCompute.Dispatch(kernelAddDensityBatched, threadGroups, threadGroups, 1);
                    ctx.Density.Swap();

                    if (ctx.UseParticleSimulation && ctx.ParticlesBuffer != null && kernelAddParticlesBatched != 0)
                    {
                        ctx.BatchedInjectionCompute.SetBuffer(kernelAddParticlesBatched, "_Injections", bufferA);
                        ctx.BatchedInjectionCompute.SetBuffer(kernelAddParticlesBatched, "_Injections2", bufferB);
                        ctx.BatchedInjectionCompute.SetBuffer(kernelAddParticlesBatched, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                        ctx.BatchedInjectionCompute.SetBuffer(kernelAddParticlesBatched, "_ParticlesWrite", ctx.ParticlesBuffer[ctx.ParticleWriteIndex]);
                        ctx.BatchedInjectionCompute.SetVector("_Resolution", new Vector2(ctx.Resolution, ctx.Resolution));
                        ctx.BatchedInjectionCompute.Dispatch(kernelAddParticlesBatched, threadGroups, threadGroups, 1);
                        ctx.SwapParticleBuffers();
                    }
                }
            }
            else
            {
                foreach (var d in pendingDensityInjections)
                {
                    float colorIntensity = Mathf.Max(d.color.r, Mathf.Max(d.color.g, d.color.b));
                    if (colorIntensity <= 0f) continue;

                    Vector2 pixelPos = d.position * ctx.Resolution;

                    ctx.FluidCompute.SetVector("_ForcePosition", pixelPos);
                    ctx.FluidCompute.SetFloat("_ForceRadius", ctx.ForceRadius);
                    ctx.FluidCompute.SetFloat("_DensityAmount", ctx.DensityAmount);
                    ctx.FluidCompute.SetVector("_DensityColor", (Vector4)d.color);
                    ctx.FluidCompute.SetVector("_SimulationSize", new Vector2(ctx.Resolution, ctx.Resolution));

                    ctx.FluidCompute.SetTexture(ctx.FluidKernelAddDensity, "_DensityRead", ctx.Density.Read);
                    ctx.FluidCompute.SetTexture(ctx.FluidKernelAddDensity, "_DensityWrite", ctx.Density.Write);
                    ctx.FluidCompute.Dispatch(ctx.FluidKernelAddDensity, threadGroups, threadGroups, 1);
                    ctx.Density.Swap();

                    if (ctx.UseParticleSimulation && ctx.ParticlesBuffer != null && ctx.FluidKernelAddParticlesGaussian != 0)
                    {
                        ctx.FluidCompute.SetInt("_InkTypeIndex", d.inkTypeIndex);
                        ctx.FluidCompute.SetBuffer(ctx.FluidKernelAddParticlesGaussian, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
                        ctx.FluidCompute.SetBuffer(ctx.FluidKernelAddParticlesGaussian, "_ParticlesWrite", ctx.ParticlesBuffer[ctx.ParticleWriteIndex]);
                        ctx.FluidCompute.Dispatch(ctx.FluidKernelAddParticlesGaussian, threadGroups, threadGroups, 1);
                        ctx.SwapParticleBuffers();
                    }
                }
            }
            pendingDensityInjections.Clear();
        }
    }
}
