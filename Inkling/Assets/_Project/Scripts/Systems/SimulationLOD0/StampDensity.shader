Shader "Hidden/Magi/StampDensity"
{
    Properties
    {
        _MainTex ("Base Density", 2D) = "black" {}
        _StampTex ("Stamp Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _StampTex;

            float4 _StampCenterUV;          // xy = center in UV
            float4 _StampSizeUV;            // xy = stamp size in UV (width/resolution, height/resolution)
            float _AlphaThreshold;
            float _StampMode;               // 0 = additive, 1 = clear density where black, 2 = write obstacles where black
            float _BlackLuminanceThreshold; // used when _StampMode != 0

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // Base density
                fixed4 baseCol = tex2D(_MainTex, uv);

                // Map screen UV into stamp local UV (0-1)
                float2 halfSize = _StampSizeUV.xy * 0.5f;
                float2 rel = float2(0.0, 0.0);

                // Avoid division by zero
                if (_StampSizeUV.x > 0.0 && _StampSizeUV.y > 0.0)
                {
                    rel = (uv - _StampCenterUV.xy) / _StampSizeUV.xy + 0.5f;
                }

                // Outside stamp region -> no change
                if (rel.x < 0.0 || rel.x > 1.0 || rel.y < 0.0 || rel.y > 1.0)
                {
                    return baseCol;
                }

                fixed4 stamp = tex2D(_StampTex, rel);
                if (stamp.a < _AlphaThreshold)
                {
                    return baseCol;
                }

                // Mode 0: additive stamp into density (colored inks)
                if (_StampMode < 0.5)
                {
                    return baseCol + stamp;
                }

                // Modes 1 & 2: operate on "black" regions
                float luminance = dot(stamp.rgb, float3(0.299, 0.587, 0.114));
                if (luminance < _BlackLuminanceThreshold)
                {
                    if (_StampMode < 1.5)
                    {
                        // Mode 1: clear density at this pixel
                        return 0;
                    }
                    else
                    {
                        // Mode 2: mark obstacle (R channel = 1)
                        return fixed4(1.0, 0.0, 0.0, 0.0);
                    }
                }

                // Outside black regions, keep original density
                return baseCol;
            }
            ENDCG
        }
    }
}
