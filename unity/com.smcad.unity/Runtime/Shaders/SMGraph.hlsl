// SPDX-License-Identifier: MIT
// Custom Function entry points for the SM Lit / SM Glass Shader Graphs (HDRP Lit, Specular Color).
// The graphs' blackboards declare all material properties; this file only adds the SM maths (SMSurface.hlsl).
//   SMLit   -> Base Color, Normal (World Space), Specular Color, Smoothness, Emission, Coat Mask
//   SMAlpha -> Alpha, Alpha Clip Threshold  (kept apart so depth / shadow passes only sample two textures)
//   SMGlass -> Base Color, Normal (World Space), Specular Color, Smoothness, Alpha
// SMC-DETAILS vertex data: UV1 convex edges (UV4 in the URP version), UV5 rest position, UV6 rest normal, UV7 concave edges.
// Double-sided is the graph's "Flipped Normals" mode: the normal / tangent frame arrives already flipped.
#ifndef SMCAD_GRAPH_INCLUDED
#define SMCAD_GRAPH_INCLUDED

#define SMCAD_GRAPH 1
#include "SMSurface.hlsl"

float2 SMGraphUV(float4 uv0) { return uv0.xy * _DifMap_ST.xy + _DifMap_ST.zw; }

SMSurfaceInput SMGraphInput(float4 UV0, float4 UV2, float4 UV3, float4 VertexColor, float3 PositionOS, float3 NormalOS,
                            float3 PositionWS, float3 NormalWS, float3 TangentWS, float3 BitangentWS, bool IsFront)
{
    SMSurfaceInput i = (SMSurfaceInput)0;
    i.uv = SMGraphUV(UV0);
    i.positionOS = PositionOS;
    i.normalOS = NormalOS;
    i.color = VertexColor;
    i.shape = float3(UV2.xy, UV3.x);
    i.positionWS = PositionWS;
    i.normalWS = NormalWS;
    i.tangentWS = TangentWS;
    i.bitangentWS = BitangentWS;
    i.facing = IsFront ? 1.0 : -1.0;
    return i;
}

// SM Glow is authored for "LDR" brightness (Blender / URP): cancel HDRP's exposure so it looks the same in any light.
float3 SMGraphEmission(float3 e)
{
#if defined(SHADERGRAPH_PREVIEW)
    return e;
#else
    return e * GetInverseCurrentExposureMultiplier();
#endif
}

void SMLit_float(float4 UV0, float4 UV1, float4 UV2, float4 UV3, float4 UV5, float4 UV6, float4 UV7, float4 VertexColor,
                 float3 PositionOS, float3 NormalOS, float3 PositionWS, float3 NormalWS, float3 TangentWS, float3 BitangentWS, bool IsFront,
                 out float3 BaseColor, out float3 Normal, out float3 Specular, out float Smoothness, out float3 Emission, out float CoatMask)
{
    SMSurfaceInput i = SMGraphInput(UV0, UV2, UV3, VertexColor, PositionOS, NormalOS, PositionWS, NormalWS, TangentWS, BitangentWS, IsFront);
    i.edgeConvex = UV1.xyz;   // HDRP: TEXCOORD4 is the previous position of the motion vector pass
    i.restPos = UV5.xyz;
    i.restNormal = UV6.xyz;
    i.edgeConcave = UV7.xyz;
    SMSurfaceOutput o = SMEvaluateLit(i);
    BaseColor = o.diffuse;
    Normal = o.normalWS;
    Specular = o.specular;
    Smoothness = o.smoothness;
    Emission = SMGraphEmission(o.emission);
    CoatMask = o.coatMask;
}

void SMAlpha_float(float4 UV0, out float Alpha, out float ClipThreshold)
{
    Alpha = SMAlphaLit(SMGraphUV(UV0));
    ClipThreshold = _Cutoff;
}

void SMGlass_float(float4 UV0, float4 UV3, float4 VertexColor, float3 PositionOS, float3 NormalOS, float3 PositionWS,
                   float3 NormalWS, float3 TangentWS, float3 BitangentWS, bool IsFront,
                   out float3 BaseColor, out float3 Normal, out float3 Specular, out float Smoothness, out float Alpha)
{
    SMSurfaceInput i = SMGraphInput(UV0, float4(0, 0, 0, 0), UV3, VertexColor, PositionOS, NormalOS, PositionWS, NormalWS, TangentWS, BitangentWS, IsFront);
    SMSurfaceOutput o = SMEvaluateGlass(i);
    BaseColor = o.diffuse;
    Normal = o.normalWS;
    Specular = o.specular;
    Smoothness = o.smoothness;
    Alpha = o.alpha;
}

#endif
