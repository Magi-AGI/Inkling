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

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            Name "InkGradientRender"

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma shader_feature _SHOWCHANNELS_ON
            #pragma multi_compile _DEBUGMODE_COMBINED _DEBUGMODE_FIRE _DEBUGMODE_WATER _DEBUGMODE_METAL _DEBUGMODE_ELECTRIC

            #include "UnityCG.cginc"

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
                // Sample the simulation/stylized texture
                // Expecting RGBA where RGB contains ink type concentrations or colors
                float4 simData = tex2D(_MainTex, input.uv);

                #ifdef _SHOWCHANNELS_ON
                    // Debug: Show raw channels
                    return simData;
                #endif

                // For this example, let's assume the simulation data encodes:
                // R channel: Fire/Heat concentration
                // G channel: Water/Fluid concentration
                // B channel: Metal/Electric concentration
                // A channel: Overall density/alpha

                float4 finalColor = float4(0, 0, 0, 0);

                // Remap values
                float fireIntensity = RemapValue(simData.r, _ValueRemap.xy, _ValueRemap.zw);
                float waterIntensity = RemapValue(simData.g, _ValueRemap.xy, _ValueRemap.zw);
                float metalIntensity = RemapValue(simData.b, _ValueRemap.xy, _ValueRemap.zw);

                // Sample gradients based on concentrations
                float4 fireColor = SampleGradient(_FireGradientTex, fireIntensity, 0.5);
                float4 waterColor = SampleGradient(_WaterGradientTex, waterIntensity, 0.5);
                float4 metalColor = SampleGradient(_MetalGradientTex, metalIntensity, 0.5);

                // Combine colors based on concentrations
                #if _DEBUGMODE_FIRE
                    finalColor = fireColor * simData.r;
                #elif _DEBUGMODE_WATER
                    finalColor = waterColor * simData.g;
                #elif _DEBUGMODE_METAL
                    finalColor = metalColor * simData.b;
                #elif _DEBUGMODE_ELECTRIC
                    float4 electricColor = SampleGradient(_ElectricityGradientTex, metalIntensity, sin(_Time.y * 10));
                    finalColor = electricColor * simData.b;
                #else
                    // Combined mode - blend all active ink types
                    float totalConcentration = simData.r + simData.g + simData.b + 0.001;

                    finalColor = (fireColor * simData.r +
                                 waterColor * simData.g +
                                 metalColor * simData.b) / totalConcentration;

                    // Preserve alpha from simulation
                    finalColor.a = simData.a;
                #endif

                // Apply gradient intensity
                finalColor = lerp(simData, finalColor, _GradientIntensity);

                // Boost saturation
                finalColor.rgb = BoostSaturation(finalColor.rgb, _SaturationBoost);

                // Add edge glow for emphasis
                float edge = CalculateEdgeGlow(input.uv);
                finalColor.rgb += finalColor.rgb * edge * _EdgeGlow;

                // Apply emission
                finalColor.rgb *= _EmissionStrength;

                // Alpha cutoff for cleaner edges
                finalColor.a = smoothstep(_AlphaCutoff, _AlphaCutoff + 0.05, finalColor.a);

                return finalColor;
            }
            ENDCG
        }
    }

    FallBack "Unlit/Texture"
}