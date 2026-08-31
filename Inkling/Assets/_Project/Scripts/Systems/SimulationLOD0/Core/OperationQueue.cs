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

        // CP8w: a heat-only injection (ColdAir). Carries a temperature and NO ink index, because there
        // is no ink — this is the type-level guarantee that the ColdAir path cannot write mass.
        private struct PendingHeatInjection
        {
            public Vector2 position;
            public float targetTemperature;
        }

        // ── Queues ──────────────────────────────────────────────────────────

        private readonly List<PendingDensityStamp> pendingDensityStamps = new List<PendingDensityStamp>();
        private readonly List<PendingForceInjection> pendingForceInjections = new List<PendingForceInjection>();
        private readonly List<PendingDensityInjection> pendingDensityInjections = new List<PendingDensityInjection>();
        private readonly List<PendingClearDensityMask> pendingClearDensityMasks = new List<PendingClearDensityMask>();
        private readonly List<PendingHeatInjection> pendingHeatInjections = new List<PendingHeatInjection>();

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
        private int kernelApplyReactionImpulse;
        private bool applyReactionImpulseReady;
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
        public bool ApplyReactionImpulseReady => applyReactionImpulseReady;
        public int KernelApplyReactionImpulse => kernelApplyReactionImpulse;
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

        /// <summary>
        /// CP8w: queues a HEAT-ONLY injection (ColdAir). Never touches density or particles.
        /// </summary>
        public void EnqueueHeatInjection(Vector2 position, float targetTemperature)
        {
            pendingHeatInjections.Add(new PendingHeatInjection
            {
                position = position,
                targetTemperature = targetTemperature
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

                if (TryFindKernel(ctx.InkInteractionsCompute, "ApplyReactionImpulse", out kernelApplyReactionImpulse))
                {
                    applyReactionImpulseReady = true;
                }
                else
                {
                    applyReactionImpulseReady = false;
                    Debug.LogWarning("[SimDriver] ApplyReactionImpulse kernel missing; fire/plant reaction impulse disabled.");
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
            // Count-sized (ColdSourceInkIndex == (int)InkTypeId.Count == 11) so index 10 (Metal) is in-range.
            // Metal has no authored key color in M0: its default tolerance is 0 so it NEVER matches a stamp
            // pixel (no accidental metal, no fallback to another ink). M1's Metal asset supplies a real
            // inputKeyColor/tolerance via the ctx.InkDefinitions override below.
            int count = SimulationContext.ColdSourceInkIndex; // == (int)InkTypeId.Count == 11
            var palette = new Vector4[count];
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
                new Color(0.6f, 0.6f, 0.65f), // 10 = Metal (placeholder; inactive via tolerance 0 in M0)
            };

            for (int i = 0; i < count; i++)
            {
                Color keyColor = i < defaults.Length ? defaults[i] : Color.black;
                // Metal (index count-1) is present-but-inactive in M0: tolerance 0 => never matches.
                float tolerance = (i == count - 1) ? 0f : 0.3f;

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
            ProcessHeatInjections(threadGroups);
        }

        /// <summary>
        /// CP8w: applies queued ColdAir stamps. Deliberately NOT folded into ProcessDensityInjections —
        /// that method early-returns when ctx.Density is null, and a pure temperature probe must still
        /// work in a heat-only harness with no density buffer allocated.
        /// </summary>
        private void ProcessHeatInjections(int threadGroups)
        {
            if (pendingHeatInjections.Count == 0) return;

            foreach (var h in pendingHeatInjections)
                StampHeatAt(h.position, h.targetTemperature, threadGroups);

            pendingHeatInjections.Clear();
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
                ctx.StampParticlesCompute.SetInt("_NumActiveInks", SimulationContext.ColdSourceInkIndex); // == InkTypeId.Count (11)
                ctx.StampParticlesCompute.SetInt("_UsePaletteLookup", 1);

                if (!hasLoggedFirstParticleStamp && pendingDensityStamps.Count > 0)
                {
                    hasLoggedFirstParticleStamp = true;
                    var firstStamp = pendingDensityStamps[0];
                    Debug.Log($"[SimDriver] First particle stamp: texture={firstStamp.stamp?.name}, " +
                              $"pos={firstStamp.uvPosition}, mul={firstStamp.multiplier}, " +
                              $"useOverride={firstStamp.useColorOverride}, override={firstStamp.overrideColor}");
                    for (int i = 0; i < inkPalette.Length; i++)
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
        private bool hasLoggedFirstForceInjection;
        private bool hasLoggedClampedForceInjection;

        private void ProcessForceInjections(int threadGroups)
        {
            if (pendingForceInjections.Count == 0 || ctx.FluidCompute == null) return;

            foreach (var f in pendingForceInjections)
            {
                Vector2 pixelPos = f.position * ctx.Resolution;

                // Convert UV-space force to pixel-space so forceStrength operates at
                // intuitive values (1-5 range) and results are resolution-independent.
                // Before this fix, force magnitudes of ~0.05 UV produced sub-pixel
                // velocity (~0.02 px/frame displacement), making all dynamics invisible.
                Vector2 pixelForce = f.force * ctx.Resolution;
                float requestedStrength = ctx.ForceStrength * pixelForce.magnitude;
                float finalStrength = Mathf.Min(requestedStrength, 500f);

                if (!hasLoggedClampedForceInjection && requestedStrength > 500f)
                {
                    hasLoggedClampedForceInjection = true;
                    Debug.LogWarning($"[ForceInjection] Strength clamped from {requestedStrength:F1} to 500. " +
                        $"Lower SimDriver.forceStrength for less aggressive motion.");
                }

                if (!hasLoggedFirstForceInjection)
                {
                    hasLoggedFirstForceInjection = true;
                    Debug.Log($"[ForceInjection] First force: pos={pixelPos}, dir={f.force.normalized:F3}, " +
                        $"strength={finalStrength:F3} (base={ctx.ForceStrength}, " +
                        $"uvMag={f.force.magnitude:F4}, pxMag={pixelForce.magnitude:F1}), " +
                        $"radius={ctx.ForceRadius}");
                }

                if (DebugLogForces && forceLogCounter++ % 60 == 0)
                {
                    Debug.Log($"[ForceInjection] pos={pixelPos}, dir={f.force.normalized:F3}, " +
                        $"strength={finalStrength:F3} (base={ctx.ForceStrength}, " +
                        $"uvMag={f.force.magnitude:F4}, pxMag={pixelForce.magnitude:F1}), " +
                        $"radius={ctx.ForceRadius}");
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

        /// <summary>
        /// CP8b/CP8f: stamps the injected ink's characteristic temperature into the heat field, using the
        /// injection's own centre/radius/falloff — Fire at the ceiling, Steam hot (between water and
        /// fire, so painted steam sits above the condense threshold instead of collapsing straight back
        /// into water), Water at the neutral baseline, Ice at the floor (so painting ice immediately
        /// creates genuinely sub-neutral cold, which was the user-reported gap). The mapping itself
        /// lives in SimulationContext.TryGetInjectionTemperature; every other ink leaves heat untouched.
        ///
        /// This is a ONE-SHOT INITIAL CONDITION per queued injection, not a per-frame source, so it does
        /// NOT revive the free continuous fire heat that CP7b/CP7d removed — fire's ongoing emission
        /// stays owned by the thermal-interactions pass, with its fuel cost.
        ///
        /// Runs inside ProcessPending(), which is BEFORE FluidSolver.Step() uploads SetConstants(), so
        /// the clamp bounds must be uploaded here or the kernel would clamp against stale/zero uniforms
        /// (on the very first frame, to [0,0] — driving the whole field to zero).
        ///
        /// Read -> Write -> Swap per injection, so queued injections compose deterministically: a later
        /// injection sees the temperature stamped by an earlier one.
        /// </summary>
        private void StampInjectionHeat(Vector2 uvPosition, int inkTypeIndex, int threadGroups)
        {
            if (!ctx.TryGetInjectionTemperature(inkTypeIndex, out float target))
                return;   // this ink has no characteristic temperature: leave the heat field alone

            StampHeatAt(uvPosition, target, threadGroups);
        }

        /// <summary>
        /// Drives the heat field toward <paramref name="target"/> at a point, using the same centre,
        /// radius and falloff as density injection. CP8w split this out of StampInjectionHeat so the
        /// ColdAir probe can stamp an EXPLICIT temperature — it has no InkTypeId, so it cannot go
        /// through TryGetInjectionTemperature (which rejects anything past the enum).
        /// </summary>
        private void StampHeatAt(Vector2 uvPosition, float target, int threadGroups)
        {
            if (ctx.Heat == null || ctx.FluidKernelStampInjectionHeat < 0 || ctx.FluidCompute == null)
                return;

            int k = ctx.FluidKernelStampInjectionHeat;
            var fc = ctx.FluidCompute;

            fc.SetVector("_SimulationSize", new Vector2(ctx.Resolution, ctx.Resolution));
            fc.SetVector("_ForcePosition", uvPosition * ctx.Resolution);
            fc.SetFloat("_ForceRadius", ctx.ForceRadius);
            fc.SetFloat("_InjectionTargetHeat", target);
            fc.SetFloat("_MinTemperature", ctx.SanitizedMinTemperature);
            fc.SetFloat("_MaxHeat", ctx.SanitizedMaxTemperature);

            fc.SetTexture(k, "_HeatRead", ctx.Heat.Read);
            fc.SetTexture(k, "_HeatWrite", ctx.Heat.Write);
            fc.Dispatch(k, threadGroups, threadGroups, 1);
            ctx.Heat.Swap();
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

                // CP8b: heat stamping is per-injection (the batched injection buffers carry no heat RT),
                // so dispatch it once per pending injection. Same skip rule as the batch above.
                foreach (var d in pendingDensityInjections)
                {
                    float intensity = Mathf.Max(d.color.r, Mathf.Max(d.color.g, d.color.b));
                    if (intensity <= 0f) continue;
                    StampInjectionHeat(d.position, d.inkTypeIndex, threadGroups);
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

                    // CP8b: stamp this injection's characteristic temperature. Must come AFTER the
                    // density/particle dispatches above, which overwrite _ForcePosition/_ForceRadius/
                    // _SimulationSize on the same compute shader.
                    StampInjectionHeat(d.position, d.inkTypeIndex, threadGroups);
                }
            }
            pendingDensityInjections.Clear();
        }
    }
}
