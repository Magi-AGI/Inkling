Shader "Inkling/InkGradientRenderer"
{
    Properties
    {
        [Header(Input)]
        _MainTex ("Simulation/Stylized Texture", 2D) = "white" {}

        [Header(Ink Type Gradients)]
        _FireGradientTex ("Fire Gradient", 2D) = "white" {}
        _WaterGradientTex ("Water Gradient", 2D) = "white" {}
        _MetalGradientTex ("Metal Gradient", 2D) = "white" {}
        _ElectricityGradientTex ("Electricity Gradient", 2D) = "white" {}
        _IceGradientTex ("Ice Gradient", 2D) = "white" {}
        _PlantGradientTex ("Plant Gradient", 2D) = "white" {}
        _SteamGradientTex ("Steam Gradient", 2D) = "white" {}
        _DustGradientTex ("Dust Gradient", 2D) = "white" {}

        [Header(Gradient Mapping)]
        _GradientIntensity ("Gradient Intensity", Range(0, 1)) = 1.0
        _ValueRemap ("Value Remap", Vector) = (0, 1, 0, 1)
        _SaturationBoost ("Saturation Boost", Range(0, 2)) = 1.0

        [Header(Visual Effects)]
        _EdgeGlow ("Edge Glow", Range(0, 1)) = 0.2
        _EmissionStrength ("Emission Strength", Range(0, 3)) = 1.0
        _AlphaCutoff ("Alpha Cutoff", Range(0, 1)) = 0.01

        [Header(Background)]
        _BackgroundColor ("Background Color", Color) = (1, 1, 1, 1)
        [Toggle] _UseBackgroundColor ("Use Background Color", Float) = 1

        [Header(Debug)]
        [Toggle] _ShowChannels ("Show Raw Channels", Float) = 0
        [KeywordEnum(Combined, Fire, Water, Metal, Electric)] _DebugMode ("Debug Mode", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
            "RenderPipeline"="UniversalPipeline"
        }

        // Blend Off: this shader is used exclusively via Graphics.Blit as a
        // fullscreen post-process.  Every pixel must be fully overwritten;
        // SrcAlpha blending would leak stale gradientRT content through
        // density alpha < 1, causing per-frame flickering.
        Blend Off
        ZWrite Off
        Cull Off

        Pass
        {
            Name "InkGradientRender"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma shader_feature _SHOWCHANNELS_ON
            #pragma multi_compile _ _PARTICLEBUFFER_ON
            #pragma multi_compile _DEBUGMODE_COMBINED _DEBUGMODE_FIRE _DEBUGMODE_WATER _DEBUGMODE_METAL _DEBUGMODE_ELECTRIC

            #include "UnityCG.cginc"

            #ifdef _PARTICLEBUFFER_ON
            // Channel textures written by ParticleChannelSplat.compute.
            // Avoids reading StructuredBuffer<iparticle> in a fragment shader
            // where CGPROGRAM promotes half → float, causing a stride mismatch
            // (28 bytes in C# vs 56 bytes expected by the shader).
            //   _Channels0: fire, water, plantSeeded, plantGrown
            //   _Channels1: steam, glitter, blackBody, ice
            //   _Channels2: electricitySeeded, electricityGrown, 0, 0
            sampler2D _Channels0;
            sampler2D _Channels1;
            sampler2D _Channels2;
            #endif

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // Textures and Samplers
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            sampler2D _FireGradientTex;
            sampler2D _WaterGradientTex;
            sampler2D _MetalGradientTex;
            sampler2D _ElectricityGradientTex;
            sampler2D _IceGradientTex;
            sampler2D _PlantGradientTex;
            sampler2D _SteamGradientTex;
            sampler2D _DustGradientTex;

            // Parameters
            float _GradientIntensity;
            float4 _ValueRemap;
            float _SaturationBoost;
            float _EdgeGlow;
            float _EmissionStrength;
            float _AlphaCutoff;
            float4 _BackgroundColor;
            float _UseBackgroundColor;

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = UnityObjectToClipPos(input.positionOS);
                output.uv = input.uv;

                return output;
            }

            // Helper: Sample gradient based on intensity and optional secondary parameter
            float4 SampleGradient(sampler2D gradientTex, float intensity, float secondaryAxis = 0.5)
            {
                // Use X axis for intensity, Y axis for variation (temperature, age, etc.)
                float2 gradientUV = float2(saturate(intensity), secondaryAxis);
                return tex2D(gradientTex, gradientUV);
            }

            // Helper: Remap value range
            float RemapValue(float value, float2 fromRange, float2 toRange)
            {
                float t = (value - fromRange.x) / (fromRange.y - fromRange.x);
                return lerp(toRange.x, toRange.y, saturate(t));
            }

            // Helper: Boost saturation
            float3 BoostSaturation(float3 color, float boost)
            {
                float luminance = dot(color, float3(0.299, 0.587, 0.114));
                return lerp(float3(luminance, luminance, luminance), color, boost);
            }

            // Helper: Calculate edge glow
            float CalculateEdgeGlow(float2 uv)
            {
                float2 texelSize = _MainTex_TexelSize.xy;

                // Sample neighbors for edge detection
                float center = tex2D(_MainTex, uv).a;
                float left = tex2D(_MainTex, uv - float2(texelSize.x, 0)).a;
                float right = tex2D(_MainTex, uv + float2(texelSize.x, 0)).a;
                float up = tex2D(_MainTex, uv - float2(0, texelSize.y)).a;
                float down = tex2D(_MainTex, uv + float2(0, texelSize.y)).a;

                // Simple edge detection
                float edge = abs(center - left) + abs(center - right) + abs(center - up) + abs(center - down);
                return smoothstep(0.0, 0.5, edge);
            }

            float4 frag(Varyings input) : SV_Target
            {
                float4 finalColor = float4(0, 0, 0, 0);

            #ifdef _PARTICLEBUFFER_ON
                // ── Particle-authoritative path ─────────────────────────────
                // Sample channel textures written by ParticleChannelSplat.compute.
                // Anti-aliasing is handled by channel RT mipmaps: the gradient shader
                // runs at display resolution, and tex2D auto-selects the right mip
                // for hardware-filtered minification of sim-resolution data.
                float4 ch0 = tex2D(_Channels0, input.uv); // fire, water, plantSeeded, plantGrown
                float4 ch1 = tex2D(_Channels1, input.uv); // steam, glitter, blackBody, ice
                float4 ch2 = tex2D(_Channels2, input.uv); // electricitySeeded, electricityGrown, 0, 0

                #ifdef _SHOWCHANNELS_ON
                    return float4(ch0.r, ch0.g, ch1.a, 1.0); // fire, water, ice
                #endif

                // Per-channel gradient lookups, weighted by channel intensity.
                // Weighting is essential: gradient textures may return non-zero at
                // intensity=0 (especially unassigned textures defaulting to white),
                // so zero-intensity channels must contribute zero color.
                float fireI   = saturate(ch0.r);
                float waterI  = saturate(ch0.g);
                float plantSI = saturate(ch0.b);
                float plantGI = saturate(ch0.a);
                float steamI  = saturate(ch1.r);
                float dustI   = saturate(ch1.g);
                float iceI    = saturate(ch1.a);
                float elecI   = saturate(max(ch2.r, ch2.g));

                float totalInk = fireI + waterI + plantSI + plantGI
                    + steamI + dustI + elecI + iceI + 0.001;

                finalColor =
                    ( SampleGradient(_FireGradientTex,        fireI)   * fireI
                    + SampleGradient(_WaterGradientTex,       waterI)  * waterI
                    + SampleGradient(_PlantGradientTex,       plantSI) * plantSI
                    + SampleGradient(_MetalGradientTex,       plantGI) * plantGI
                    + SampleGradient(_SteamGradientTex,       steamI)  * steamI
                    + SampleGradient(_DustGradientTex,        dustI)   * dustI
                    + SampleGradient(_ElectricityGradientTex, elecI)   * elecI
                    + SampleGradient(_IceGradientTex,         iceI)    * iceI
                    ) / totalInk;

                // BlackBody darkening (subtractive — no gradient lookup)
                finalColor.rgb *= (1.0 - saturate(ch1.b));

                // Alpha from total ink presence
                finalColor.a = saturate(fireI + waterI + plantSI + plantGI
                    + steamI + dustI + saturate(ch1.b) + iceI
                    + elecI);

                // Gradient intensity: lerp between raw particle RGB and gradient output
                float4 rawColor = float4(ch0.r, ch0.g, ch1.a, finalColor.a); // fire, water, ice
                finalColor = lerp(rawColor, finalColor, _GradientIntensity);

            #else
                // ── Density RT fallback path ────────────────────────────────
                float4 simData = tex2D(_MainTex, input.uv);

                #ifdef _SHOWCHANNELS_ON
                    return simData;
                #endif

                float fireIntensity = RemapValue(simData.r, _ValueRemap.xy, _ValueRemap.zw);
                float waterIntensity = RemapValue(simData.g, _ValueRemap.xy, _ValueRemap.zw);
                float metalIntensity = RemapValue(simData.b, _ValueRemap.xy, _ValueRemap.zw);

                float4 fireColor = SampleGradient(_FireGradientTex, fireIntensity, 0.5);
                float4 waterColor = SampleGradient(_WaterGradientTex, waterIntensity, 0.5);
                float4 metalColor = SampleGradient(_MetalGradientTex, metalIntensity, 0.5);

                #if _DEBUGMODE_FIRE
                    finalColor = fireColor * simData.r;
                    finalColor.a = simData.r;
                #elif _DEBUGMODE_WATER
                    finalColor = waterColor * simData.g;
                    finalColor.a = simData.g;
                #elif _DEBUGMODE_METAL
                    finalColor = metalColor * simData.b;
                    finalColor.a = simData.b;
                #elif _DEBUGMODE_ELECTRIC
                    float4 electricColor = SampleGradient(_ElectricityGradientTex, metalIntensity, sin(_Time.y * 10));
                    finalColor = electricColor * simData.b;
                    finalColor.a = simData.b;
                #else
                    float totalConcentration = simData.r + simData.g + simData.b + 0.001;
                    finalColor = (fireColor * simData.r +
                                 waterColor * simData.g +
                                 metalColor * simData.b) / totalConcentration;
                    // Calculate alpha as total ink presence for background blending
                    finalColor.a = saturate(simData.r + simData.g + simData.b);
                #endif

                finalColor = lerp(simData, finalColor, _GradientIntensity);

                // Edge glow (density RT path only — particle path skips this)
                float edge = CalculateEdgeGlow(input.uv);
                finalColor.rgb += finalColor.rgb * edge * _EdgeGlow;
            #endif

                // Common post-processing
                finalColor.rgb = BoostSaturation(finalColor.rgb, _SaturationBoost);
                finalColor.rgb *= _EmissionStrength;

                // Blend with background color based on ink presence
                // This ensures inks dissipate to the background color, not black
                if (_UseBackgroundColor > 0.5)
                {
                    // Use the alpha we calculated (ink presence) to blend with background
                    float inkPresence = saturate(finalColor.a);
                    finalColor.rgb = lerp(_BackgroundColor.rgb, finalColor.rgb, inkPresence);
                }

                // Output opaque since we've already composited with background
                finalColor.a = 1.0;

                return finalColor;
            }
            ENDCG
        }
    }

    FallBack "Unlit/Texture"
}
