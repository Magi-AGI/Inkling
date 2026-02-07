using System.Runtime.InteropServices;
using UnityEngine;
using Magi.InkTools;
using Magi.InkTools.Simulation;

namespace Magi.Inkling.Systems.SimulationLOD0
{
    /// <summary>
    /// Handles display rendering: gradient rendering, creature compositing,
    /// particle-to-color conversion, and IMGUI debug overlay.
    /// Called by SimDriver in LateUpdate().
    /// </summary>
    public class SimulationDisplay
    {
        private readonly SimulationContext ctx;
        private bool loggedDisplayDiagnostic;

        /// <summary>
        /// Float-field mirror of iparticle for GPU buffer marshaling on platforms
        /// where half is promoted to float in StructuredBuffers (DX11/FXC).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct iparticle_gpu
        {
            public float fire, water, plantSeeded, plantGrown;
            public float steam, glitter, blackBody;
            public float electricitySeeded, electricityGrown;
            public float ice;
            public float red, green, blue, alpha;
        }

        public SimulationDisplay(SimulationContext context)
        {
            ctx = context;
        }

        public void UpdateDisplay()
        {
            RenderTexture sourceTexture;

            if (ctx.UseParticleRenderPass && ctx.ParticlesBuffer != null && ctx.ParticleToColorCompute != null)
            {
                RenderParticlesToDisplay();
                sourceTexture = ctx.DisplayRT;
            }
            else if (ctx.DisplayVelocity)
            {
                sourceTexture = ctx.Velocity.Read;
            }
            else
            {
                sourceTexture = (!ctx.UseParticleDisplay && ctx.Density != null)
                    ? ctx.Density.Read
                    : ConvertParticlesToTexture();
            }

            // Composite creature ink for display only
            RenderTexture compositeRT = null;
            if (ctx.CreatureInkBuffer != null && !ctx.DisplayVelocity && ctx.UseCpuCreatureComposite)
            {
                compositeRT = RenderTexture.GetTemporary(ctx.Resolution, ctx.Resolution, 0, RenderTextureFormat.ARGBHalf);

                RenderTexture.active = sourceTexture;
                Texture2D tempDensity = new Texture2D(ctx.Resolution, ctx.Resolution, TextureFormat.RGBAHalf, false);
                tempDensity.ReadPixels(new Rect(0, 0, ctx.Resolution, ctx.Resolution), 0, 0);
                tempDensity.Apply();
                Color[] densityPixels = tempDensity.GetPixels();

                RenderTexture.active = ctx.CreatureInkBuffer;
                Texture2D tempCreature = new Texture2D(ctx.Resolution, ctx.Resolution, TextureFormat.RGBAHalf, false);
                tempCreature.ReadPixels(new Rect(0, 0, ctx.Resolution, ctx.Resolution), 0, 0);
                tempCreature.Apply();
                Color[] creaturePixels = tempCreature.GetPixels();
                RenderTexture.active = null;

                for (int i = 0; i < densityPixels.Length; i++)
                {
                    densityPixels[i] += creaturePixels[i];
                }

                Texture2D composite = new Texture2D(ctx.Resolution, ctx.Resolution, TextureFormat.RGBAHalf, false);
                composite.SetPixels(densityPixels);
                composite.Apply();
                Graphics.Blit(composite, compositeRT);

                sourceTexture = compositeRT;

                Object.Destroy(tempDensity);
                Object.Destroy(tempCreature);
                Object.Destroy(composite);
            }

            // One-time diagnostic for rendering path
            if (!loggedDisplayDiagnostic)
            {
                loggedDisplayDiagnostic = true;
                bool canSplat = ctx.UseParticleSimulation && ctx.ParticlesBuffer != null
                    && ctx.ChannelRT0 != null && ctx.ChannelRT1 != null && ctx.ChannelRT2 != null;
                Debug.Log($"[SimDriver Display] gradient={ctx.UseGradientRendering}, " +
                    $"canSplat={canSplat}, " +
                    $"source={sourceTexture?.name ?? "null"}");

                if (ctx.GradientPreset != null && ctx.GradientMaterial != null)
                {
                    ctx.GradientPreset.ApplyToMaterial(ctx.GradientMaterial);
                    string[] texNames = { "_FireGradientTex", "_WaterGradientTex", "_IceGradientTex" };
                    foreach (var tname in texNames)
                    {
                        var tex = ctx.GradientMaterial.GetTexture(tname);
                        Debug.Log($"[SimDriver Gradient] {tname}: {(tex != null ? $"{tex.width}x{tex.height}" : "NULL")}");
                    }

                    float ac = ctx.GradientMaterial.HasProperty("_AlphaCutoff") ? ctx.GradientMaterial.GetFloat("_AlphaCutoff") : -1f;
                    if (ac > 0.1f)
                    {
                        Debug.LogWarning($"[SimDriver] _AlphaCutoff={ac} is high — most ink detail will be clipped. " +
                            "Set to 0.01 in the gradient material Inspector for best results.");
                    }
                }
            }

            // Clear to opaque black every frame before rendering
            RenderTexture.active = ctx.DisplayRT;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = null;

            // Apply gradient rendering if enabled
            if (ctx.UseGradientRendering && ctx.GradientMaterial != null && ctx.GradientPreset != null && !ctx.DisplayVelocity)
            {
                if (ctx.GradientRT == null || ctx.GradientRT.width != ctx.EffectiveDisplayRes)
                {
                    if (ctx.GradientRT != null) ctx.GradientRT.Release();
                    ctx.GradientRT = new RenderTexture(ctx.EffectiveDisplayRes, ctx.EffectiveDisplayRes, 0, RenderTextureFormat.ARGBHalf);
                    ctx.GradientRT.enableRandomWrite = false;
                    ctx.GradientRT.filterMode = FilterMode.Bilinear;
                    ctx.GradientRT.Create();
                }

                ctx.GradientPreset.ApplyToMaterial(ctx.GradientMaterial);

                float showChannels = ctx.GradientMaterial.HasProperty("_ShowChannels")
                    ? ctx.GradientMaterial.GetFloat("_ShowChannels")
                    : 0f;

                if (showChannels > 0.5f)
                    ctx.GradientMaterial.EnableKeyword("_SHOWCHANNELS_ON");
                else
                    ctx.GradientMaterial.DisableKeyword("_SHOWCHANNELS_ON");

                if (ctx.UseParticleSimulation && ctx.ChannelRT0 != null && ctx.ChannelRT1 != null && ctx.ChannelRT2 != null)
                {
                    ctx.GradientMaterial.EnableKeyword("_PARTICLEBUFFER_ON");

                    var ch0 = ctx.ChannelRT0Down ?? ctx.ChannelRT0Mipped ?? ctx.ChannelRT0;
                    var ch1 = ctx.ChannelRT1Down ?? ctx.ChannelRT1Mipped ?? ctx.ChannelRT1;
                    var ch2 = ctx.ChannelRT2Down ?? ctx.ChannelRT2Mipped ?? ctx.ChannelRT2;
                    ctx.GradientMaterial.SetTexture("_Channels0", ch0);
                    ctx.GradientMaterial.SetTexture("_Channels1", ch1);
                    ctx.GradientMaterial.SetTexture("_Channels2", ch2);
                }
                else
                {
                    ctx.GradientMaterial.DisableKeyword("_PARTICLEBUFFER_ON");
                }

                RenderTexture.active = ctx.GradientRT;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = null;

                Graphics.Blit(sourceTexture, ctx.GradientRT, ctx.GradientMaterial);
                Graphics.Blit(ctx.GradientRT, ctx.DisplayRT);
            }
            else
            {
                RenderTexture.active = ctx.DisplayRT;
                GL.Clear(true, true, Color.black);
                RenderTexture.active = null;
                Graphics.Blit(sourceTexture, ctx.DisplayRT);
            }

            if (compositeRT != null)
            {
                RenderTexture.ReleaseTemporary(compositeRT);
            }

            if (ctx.CreatureInkBuffer != null)
            {
                RenderTexture.active = ctx.CreatureInkBuffer;
                GL.Clear(true, true, Color.clear);
                RenderTexture.active = null;
            }

            if (ctx.DisplayRenderer != null)
            {
                ctx.DisplayRenderer.material.mainTexture = ctx.DisplayRT;
            }
        }

        private RenderTexture ConvertParticlesToTexture()
        {
            if (ctx.ParticlesBuffer == null || !ctx.UseParticleDisplay) return null;

            RenderTexture tempRT = RenderTexture.GetTemporary(ctx.Resolution, ctx.Resolution, 0, RenderTextureFormat.ARGBHalf);
            int particleCount = ctx.Resolution * ctx.Resolution;
            Color[] colors = new Color[particleCount];

            if (ctx.GpuPromotesHalf)
            {
                var particles = new iparticle_gpu[particleCount];
                ctx.ParticlesBuffer[ctx.ParticleReadIndex].GetData(particles);
                for (int i = 0; i < particleCount; i++)
                {
                    var p = particles[i];
                    float r = p.fire, g = p.water, b = p.ice;
                    if (p.blackBody > 0) { float d = 1f - Mathf.Clamp01(p.blackBody); r *= d; g *= d; b *= d; }
                    colors[i] = new Color(r, g, b, Mathf.Max(p.fire, Mathf.Max(p.water, Mathf.Max(p.ice, p.blackBody))));
                }
            }
            else
            {
                var particles = new iparticle[particleCount];
                ctx.ParticlesBuffer[ctx.ParticleReadIndex].GetData(particles);
                for (int i = 0; i < particleCount; i++)
                {
                    var p = particles[i];
                    float r = p.fire, g = p.water, b = p.ice;
                    if (p.blackBody > 0) { float d = 1f - Mathf.Clamp01(p.blackBody); r *= d; g *= d; b *= d; }
                    colors[i] = new Color(r, g, b, Mathf.Max(p.fire, p.water, p.ice, p.blackBody));
                }
            }

            Texture2D temp = new Texture2D(ctx.Resolution, ctx.Resolution, TextureFormat.RGBAHalf, false);
            temp.SetPixels(colors);
            temp.Apply();
            Graphics.Blit(temp, tempRT);
            Object.Destroy(temp);

            return tempRT;
        }

        private void RenderParticlesToDisplay()
        {
            if (ctx.ParticlesBuffer == null || ctx.ParticleToColorCompute == null || ctx.DisplayRT == null)
                return;

            if (ctx.EffectiveDisplayRes != ctx.Resolution)
            {
                Debug.LogWarning("[SimDriver] RenderParticlesToDisplay skipped: " +
                    $"displayRes ({ctx.EffectiveDisplayRes}) != simRes ({ctx.Resolution}). " +
                    "Use gradient rendering path instead.");
                return;
            }

            int kernel = ctx.ParticleToColorCompute.FindKernel("ParticleToColor");
            float brightness = ctx.GradientPreset != null ? ctx.GradientPreset.globalBrightness : 1.0f;

            ctx.ParticleToColorCompute.SetInt("_Resolution", ctx.Resolution);
            ctx.ParticleToColorCompute.SetFloat("_GlobalBrightness", brightness);
            ctx.ParticleToColorCompute.SetBuffer(kernel, "_ParticlesRead", ctx.ParticlesBuffer[ctx.ParticleReadIndex]);
            ctx.ParticleToColorCompute.SetTexture(kernel, "_Output", ctx.DisplayRT);

            int threadGroups = Mathf.CeilToInt(ctx.Resolution / 8f);
            ctx.ParticleToColorCompute.Dispatch(kernel, threadGroups, threadGroups, 1);
        }

        public void DrawOnGUI(float lastFrameMs, float advectionMs, float diffusionMs,
            float pressureMs, float projectionMs, float vorticityMs)
        {
            if (!Application.isEditor) return;

            int y = 10;

            if (ctx.MeasurePerformance)
            {
                GUI.Label(new Rect(10, y, 300, 20), $"=== SimDriver ({ctx.Resolution}x{ctx.Resolution}) ===");
                GUI.Label(new Rect(10, y + 20, 300, 20), $"Total: {lastFrameMs:F2}ms");
                GUI.Label(new Rect(10, y + 40, 300, 20), $"Advection: {advectionMs:F2}ms");
                if (diffusionMs > 0)
                    GUI.Label(new Rect(10, y + 60, 300, 20), $"Diffusion: {diffusionMs:F2}ms");
                GUI.Label(new Rect(10, y + 80, 300, 20), $"Pressure: {pressureMs:F2}ms");
                GUI.Label(new Rect(10, y + 100, 300, 20), $"Projection: {projectionMs:F2}ms");
                if (vorticityMs > 0)
                    GUI.Label(new Rect(10, y + 120, 300, 20), $"Vorticity: {vorticityMs:F2}ms");
            }
        }
    }
}
