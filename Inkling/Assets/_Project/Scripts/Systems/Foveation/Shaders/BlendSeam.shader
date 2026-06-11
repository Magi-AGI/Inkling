Shader "Inkling/BlendSeam"
{
    Properties
    {
        _CenterTex ("Center Texture", 2D) = "white" {}
        _PeripheryTex ("Periphery Texture", 2D) = "white" {}
        _CenterRect ("Center Rect (x,y,width,height)", Vector) = (0.25, 0.25, 0.5, 0.5)
        _FeatherSize ("Feather Size", Range(0.01, 0.2)) = 0.05
        _BlendPower ("Blend Power", Range(0.5, 4.0)) = 2.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        LOD 100

        Pass
        {
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _CenterTex;
            sampler2D _PeripheryTex;
            float4 _CenterRect;
            float _FeatherSize;
            float _BlendPower;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float CalculateFeatherMask(float2 uv)
            {
                // Calculate distance from UV to center rect edges
                float2 centerMin = _CenterRect.xy;
                float2 centerMax = _CenterRect.xy + _CenterRect.zw;

                // Distance to rect edges (negative inside, positive outside)
                float2 d = max(centerMin - uv, max(uv - centerMax, 0.0));
                float dist = length(d);

                // If inside the rect, check distance to nearest edge
                if (uv.x >= centerMin.x && uv.x <= centerMax.x &&
                    uv.y >= centerMin.y && uv.y <= centerMax.y)
                {
                    float4 edgeDist = float4(
                        uv.x - centerMin.x,
                        centerMax.x - uv.x,
                        uv.y - centerMin.y,
                        centerMax.y - uv.y
                    );
                    dist = -min(min(edgeDist.x, edgeDist.y), min(edgeDist.z, edgeDist.w));
                }

                // Create smooth feather
                float mask = saturate((dist + _FeatherSize) / (_FeatherSize * 2.0));

                // Apply power curve for smoother blend
                return pow(mask, _BlendPower);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Sample both textures
                fixed4 periphery = tex2D(_PeripheryTex, i.uv);

                // Calculate UV for center texture (remapped to center rect)
                float2 centerUV = (i.uv - _CenterRect.xy) / _CenterRect.zw;
                fixed4 center = tex2D(_CenterTex, centerUV);

                // Calculate blend mask
                float mask = CalculateFeatherMask(i.uv);

                // Only sample center if within valid UV range
                if (centerUV.x < 0.0 || centerUV.x > 1.0 ||
                    centerUV.y < 0.0 || centerUV.y > 1.0)
                {
                    return periphery;
                }

                // Blend based on feather mask
                return lerp(periphery, center, 1.0 - mask);
            }
            ENDCG
        }
    }

    FallBack "Unlit/Texture"
}