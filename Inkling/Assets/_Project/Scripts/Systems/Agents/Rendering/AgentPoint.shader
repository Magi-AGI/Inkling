// AgentPoint.shader - Renders agents as screen-space points
// Uses DrawProcedural with agent buffer bound directly

Shader "Inkling/AgentPoint"
{
    Properties
    {
        _PointSize ("Point Size", Float) = 4.0
        _AgentColor ("Agent Color", Color) = (1, 1, 1, 1)
        _InactiveColor ("Inactive Color", Color) = (1, 1, 1, 0.2)
        _ShowInactive ("Show Inactive", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        LOD 100

        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #pragma target 5.0

            #include "UnityCG.cginc"

            // Agent struct - must match Agent.cs and AgentCommon.hlsl
            struct Agent
            {
                float2 position;
                float2 velocity;
                float2 flockForce;
                float advectionWeight;
                float flockWeight;
                uint flags;
            };

            StructuredBuffer<Agent> _Agents;
            uint _AgentCount;
            float _PointSize;
            float4 _AgentColor;
            float4 _InactiveColor;
            float _ShowInactive;

            struct v2g
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float size : PSIZE;
            };

            struct g2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            bool IsActive(Agent a) { return (a.flags & 1) != 0; }

            v2g vert(uint id : SV_VertexID)
            {
                v2g o;

                Agent agent = _Agents[id];
                bool active = IsActive(agent);

                // Skip inactive agents unless debug mode
                if (!active && _ShowInactive < 0.5)
                {
                    o.pos = float4(0, 0, -2, 1); // Behind camera, will be clipped
                    o.color = float4(0, 0, 0, 0);
                    o.size = 0;
                    return o;
                }

                // Convert UV position to clip space
                // UV [0,1] -> NDC [-1,1]
                float2 ndc = agent.position * 2.0 - 1.0;

                o.pos = float4(ndc.x, ndc.y, 0, 1);
                o.color = active ? _AgentColor : _InactiveColor;
                o.size = _PointSize;

                return o;
            }

            // Geometry shader expands points into quads
            [maxvertexcount(4)]
            void geom(point v2g input[1], inout TriangleStream<g2f> stream)
            {
                if (input[0].size < 0.001)
                    return; // Skip zero-size points

                // Screen-space point size
                float2 size = input[0].size / _ScreenParams.xy;

                float4 center = input[0].pos;
                float4 color = input[0].color;

                // Emit quad vertices
                g2f o;
                o.color = color;

                // Bottom-left
                o.pos = center + float4(-size.x, -size.y, 0, 0);
                o.uv = float2(0, 0);
                stream.Append(o);

                // Top-left
                o.pos = center + float4(-size.x, size.y, 0, 0);
                o.uv = float2(0, 1);
                stream.Append(o);

                // Bottom-right
                o.pos = center + float4(size.x, -size.y, 0, 0);
                o.uv = float2(1, 0);
                stream.Append(o);

                // Top-right
                o.pos = center + float4(size.x, size.y, 0, 0);
                o.uv = float2(1, 1);
                stream.Append(o);
            }

            fixed4 frag(g2f i) : SV_Target
            {
                // Circular point with soft edge
                float2 center = i.uv - 0.5;
                float dist = length(center) * 2.0;

                // Soft circle falloff
                float alpha = saturate(1.0 - dist);
                alpha *= alpha; // Softer falloff

                float4 color = i.color;
                color.a *= alpha;

                return color;
            }
            ENDCG
        }
    }

    FallBack Off
}
