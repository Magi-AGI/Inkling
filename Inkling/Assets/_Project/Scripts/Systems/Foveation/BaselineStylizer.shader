Shader "Inkling/BaselineStylizer"
{
    Properties
    {
        _MainTex ("Simulation Texture", 2D) = "white" {}
        _StyleIntensity ("Style Intensity", Range(0, 1)) = 0.5
        _EdgeThreshold ("Edge Threshold", Range(0, 1)) = 0.1
        _ColorRampTex ("Color Ramp", 2D) = "white" {}

        [Header(Watercolor Settings)]
        _WatercolorBleed ("Color Bleed", Range(0, 1)) = 0.3
        _PaperTexture ("Paper Texture", 2D) = "white" {}
        _PaperInfluence ("Paper Influence", Range(0, 1)) = 0.2

        [Header(Performance)]
        [Toggle] _UseMobileOptimization ("Mobile Optimization", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma shader_feature _USEMOBILEOPTIMIZATION_ON

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;

            sampler2D _ColorRampTex;
            sampler2D _PaperTexture;

            float _StyleIntensity;
            float _EdgeThreshold;
            float _WatercolorBleed;
            float _PaperInfluence;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            // Self-contained Sobel edge detection
            float SobelEdge(float2 uv)
            {
                float2 texelSize = _MainTex_TexelSize.xy;

                // Sample neighboring pixels
                float tl = length(tex2D(_MainTex, uv + float2(-texelSize.x, texelSize.y)).rgb);
                float tm = length(tex2D(_MainTex, uv + float2(0, texelSize.y)).rgb);
                float tr = length(tex2D(_MainTex, uv + float2(texelSize.x, texelSize.y)).rgb);
                float ml = length(tex2D(_MainTex, uv + float2(-texelSize.x, 0)).rgb);
                float mm = length(tex2D(_MainTex, uv).rgb);
                float mr = length(tex2D(_MainTex, uv + float2(texelSize.x, 0)).rgb);
                float bl = length(tex2D(_MainTex, uv + float2(-texelSize.x, -texelSize.y)).rgb);
                float bm = length(tex2D(_MainTex, uv + float2(0, -texelSize.y)).rgb);
                float br = length(tex2D(_MainTex, uv + float2(texelSize.x, -texelSize.y)).rgb);

                // Sobel X kernel: [[-1, 0, 1], [-2, 0, 2], [-1, 0, 1]]
                float sobelX = -1.0 * tl + 0.0 * tm + 1.0 * tr +
                              -2.0 * ml + 0.0 * mm + 2.0 * mr +
                              -1.0 * bl + 0.0 * bm + 1.0 * br;

                // Sobel Y kernel: [[1, 2, 1], [0, 0, 0], [-1, -2, -1]]
                float sobelY = 1.0 * tl + 2.0 * tm + 1.0 * tr +
                              0.0 * ml + 0.0 * mm + 0.0 * mr +
                              -1.0 * bl + -2.0 * bm + -1.0 * br;

                return length(float2(sobelX, sobelY));
            }

            // Self-contained color bleeding for watercolor effect
            float4 WatercolorBleed(float2 uv, float4 baseColor)
            {
                float2 texelSize = _MainTex_TexelSize.xy;
                float4 bleedColor = baseColor;

                #ifdef _USEMOBILEOPTIMIZATION_ON
                // Simplified 4-tap for mobile
                bleedColor += tex2D(_MainTex, uv + float2(texelSize.x, 0)) * 0.25;
                bleedColor += tex2D(_MainTex, uv + float2(-texelSize.x, 0)) * 0.25;
                bleedColor += tex2D(_MainTex, uv + float2(0, texelSize.y)) * 0.25;
                bleedColor += tex2D(_MainTex, uv + float2(0, -texelSize.y)) * 0.25;
                bleedColor *= 0.5;
                #else
                // Full 9-tap for desktop
                float kernel[9] = {0.077847, 0.123317, 0.077847,
                                  0.123317, 0.195346, 0.123317,
                                  0.077847, 0.123317, 0.077847};

                int index = 0;
                for (int y = -1; y <= 1; y++)
                {
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 offset = float2(x, y) * texelSize;
                        bleedColor += tex2D(_MainTex, uv + offset) * kernel[index];
                        index++;
                    }
                }
                #endif

                return lerp(baseColor, bleedColor, _WatercolorBleed);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Sample simulation texture
                float4 simColor = tex2D(_MainTex, i.uv);

                // Edge detection
                float edges = SobelEdge(i.uv);
                edges = smoothstep(0.0, _EdgeThreshold, edges);

                // Color ramp mapping for artistic style
                float intensity = length(simColor.rgb);
                float4 rampedColor = tex2D(_ColorRampTex, float2(intensity, 0.5));

                // Apply watercolor bleeding
                float4 styledColor = WatercolorBleed(i.uv, rampedColor);

                // Paper texture overlay
                float4 paperColor = tex2D(_PaperTexture, i.uv * 4.0); // Tile paper texture
                styledColor = lerp(styledColor, styledColor * paperColor, _PaperInfluence);

                // Edge darkening for ink-like appearance
                styledColor = lerp(styledColor, float4(0.1, 0.05, 0.0, 1.0), edges * 0.8);

                // Blend between original and stylized based on intensity
                float4 finalColor = lerp(simColor, styledColor, _StyleIntensity);

                return finalColor;
            }
            ENDCG
        }
    }

    // Fallback for older hardware
    Fallback "Unlit/Texture"
}