// SPDX-License-Identifier: MIT
// SMC-DETAILS: depth of the blueprint seen from one direction (orthographic), for the visibility grid.
// Output = distance along _SmdFwd from the near plane; BlendOp Min keeps the nearest surface (no depth buffer).
Shader "Hidden/SMCAD/DetailsDepth"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            BlendOp Min
            Blend One One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4x4 _SmdObjToRoot;
            float3 _SmdCenter, _SmdRight, _SmdUp, _SmdFwd;
            float _SmdRadius;
            struct A { float4 pos : POSITION; };
            struct V { float4 pos : SV_POSITION; float d : TEXCOORD0; };
            V vert(A i)
            {
                V o;
                float3 p = mul(_SmdObjToRoot, float4(i.pos.xyz, 1.0)).xyz - _SmdCenter;
                o.pos = float4(dot(p, _SmdRight) / _SmdRadius, dot(p, _SmdUp) / _SmdRadius, 0.5, 1.0);
                o.d = dot(p, _SmdFwd) + _SmdRadius;
                return o;
            }
            float4 frag(V i) : SV_Target { return i.d.xxxx; }
            ENDHLSL
        }
    }
}
