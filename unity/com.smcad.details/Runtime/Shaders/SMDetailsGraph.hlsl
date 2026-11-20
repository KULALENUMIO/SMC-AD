// SPDX-License-Identifier: MIT
// Custom Function of the Details Lit Shader Graph (HDRP Lit, metallic): a small HDRP/Lit replacement (base colour
// map / colour, metallic, smoothness, mask map, normal map; same property names as HDRP/Lit) with the same wear and
// dirt layer as SM Lit (SMDetails.hlsl of com.smcad.unity). Used for parts that come with the blueprint but are not
// SM parts, e.g. the wheels of the Tank Rigging plugin.
// _SMD_BELT: track links (they move along the belt) — soil only, computed in the link's own object space.
// Vertex data as SM Lit on HDRP: UV1 convex edges, UV5 rest position, UV6 rest normal, UV7 concave edges.
#ifndef SMCAD_DETAILS_GRAPH_INCLUDED
#define SMCAD_DETAILS_GRAPH_INCLUDED

#define SMCAD_GRAPH 1   // the graph's blackboard declares every property
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
#if defined(_SMCAD_DETAILS)
#include "Packages/com.smcad.unity/Runtime/Shaders/SMDetails.hlsl"
#endif

void DetailsLit_float(float4 UV0, float4 UV1, float4 UV5, float4 UV6, float4 UV7,
                      float3 PositionOS, float3 NormalOS, float3 PositionWS, float3 NormalWS, float3 TangentWS, float3 BitangentWS, bool IsFront,
                      out float3 BaseColor, out float3 Normal, out float Metallic, out float Smoothness, out float Occlusion)
{
    float2 uv = UV0.xy * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw;
    float3 baseColor = SAMPLE_TEXTURE2D(_BaseColorMap, sampler_BaseColorMap, uv).rgb * _BaseColor.rgb;
    float metallic = _Metallic;
    float smoothness = _Smoothness;
    Occlusion = 1.0;
    if (_HasMask > 0.5)
    {
        float4 m = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uv);
        metallic = lerp(_MetallicRemapMin, _MetallicRemapMax, m.r);
        smoothness = lerp(_SmoothnessRemapMin, _SmoothnessRemapMax, m.a);
        Occlusion = lerp(_AORemapMin, _AORemapMax, m.g);
    }
    float3 n = normalize(NormalWS);
    float3 ts = UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, uv), _NormalScale);
    float3 normalWS = normalize(TangentWS * ts.x + BitangentWS * ts.y + n * ts.z);
    float roughness = 1.0 - smoothness;
#if defined(_SMCAD_DETAILS)
    half facing = IsFront ? 1.0h : -1.0h;
    #if defined(_SMD_BELT)
    SMApplyDetails(PositionOS, NormalOS, float3(10.0, 10.0, 10.0), float3(10.0, 10.0, 10.0), 1.0h, 0.5, PositionWS, facing,
                   baseColor, metallic, roughness, normalWS);
    #else
    SMApplyDetails(UV5.xyz, UV6.xyz, UV1.xyz, UV7.xyz, 1.0h, 0.5, PositionWS, facing, baseColor, metallic, roughness, normalWS);
    #endif
    metallic = saturate(metallic);
    roughness = saturate(roughness);
#endif
    BaseColor = baseColor;
    Normal = normalWS;
    Metallic = metallic;
    Smoothness = 1.0 - roughness;
}

#endif
