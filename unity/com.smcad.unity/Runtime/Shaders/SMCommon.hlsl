// SPDX-License-Identifier: MIT
// SMC-AD shared shader code (URP).
// Shading model ported from SM-Converter-Plugin by QuestionableM (MIT License):
//   base colour = lerp(paint colour, Dif.rgb, Dif.a)   (Dif.a = paint mask)
//   ASG: R = cut-out alpha, G = specular, B = glow, A = gloss
// Paint colour comes from the mesh vertex colour (linear), written by SMConverter (COLOR_0).
//
// Settings made in Blender arrive through the SMC-AD export (material extras "smcad"):
//   _Ov*   colour override            (SMC-AD core)
//   _Sf*   surface type values        (SMC-AD+ Surfaces), same maths as the SMC+_SurfaceCore node group
//   _L*    hand-made layer nodes      (U2 stage 1: constant / x / + / Mix with a mask)
#ifndef SMCAD_COMMON_INCLUDED
#define SMCAD_COMMON_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

TEXTURE2D(_DifMap); SAMPLER(sampler_DifMap);
TEXTURE2D(_AsgMap); SAMPLER(sampler_AsgMap);
TEXTURE2D(_NorMap); SAMPLER(sampler_NorMap);
TEXTURE2D(_OvTex);  SAMPLER(sampler_OvTex);

CBUFFER_START(UnityPerMaterial)
    float4 _DifMap_ST;
    half _HasDif;
    half _HasAsg;
    half _HasNor;
    half _AlphaMode;     // 0 opaque, 1 cut-out by Dif.a (foliage), 2 cut-out by Asg.r
    half _Cutoff;
    half _SpecularMul;
    half _GlowMul;
    half _Transparency;  // glass
    half _OvEnabled;
    half _OvUseTex;
    half _OvStrength;
    half _OvWhole;
    half _OvUseMatch;
    half _OvTolerance;
    half _OvScale;
    half _OvProjection;  // 0 box (object space), 1 uv
    half4 _OvColor;
    half4 _OvMatch;
    // surface type (SMC-AD+ Surfaces)
    half _SfEnabled;
    half _SfTexMetallic;
    half _SfPaintMetallic;
    half _SfRoughScale;
    half _SfRoughOffset;
    half _SfSpecMul;
    half _SfCoat;
    half _SfCoatRough;
    half _SfNormalStrength;
    half _SfTintStrength;
    half _SfNoise;
    float _SfNoiseScale;
    float4 _SfTint;       // linear rgb
    // layers: Op = (mode, mask, A is layer, B is layer); A / B = values (linear); F = factor or clamp flag
    float4 _LBaseOp;  float4 _LBaseA;  float4 _LBaseB;  float _LBaseF;
    float4 _LMetalOp; float4 _LMetalA; float4 _LMetalB; float _LMetalF;
    float4 _LRoughOp; float4 _LRoughA; float4 _LRoughB; float _LRoughF;
    float4 _LSpecOp;  float4 _LSpecA;  float4 _LSpecB;  float _LSpecF;
    float4 _LEmisOp;  float4 _LEmisA;  float4 _LEmisB;  float _LEmisF;
CBUFFER_END

half4 SMSampleDif(float2 uv)
{
    return _HasDif > 0.5h ? SAMPLE_TEXTURE2D(_DifMap, sampler_DifMap, uv) : half4(0.8h, 0.8h, 0.8h, 1.0h);
}

half4 SMSampleAsg(float2 uv)
{
    return _HasAsg > 0.5h ? SAMPLE_TEXTURE2D(_AsgMap, sampler_AsgMap, uv) : half4(0.5h, 0.5h, 0.0h, 0.5h);
}

half SMCutoutAlpha(half4 dif, half4 asg)
{
    if (_AlphaMode < 0.5h) return 1.0h;
    return _AlphaMode < 1.5h ? dif.a : asg.r;
}

// Texture mask (1 = texture, 0 = paint). Foliage uses Dif.a as cut-out instead, so it is never painted.
half SMPaintMask(half4 dif)
{
    return (_AlphaMode > 0.5h && _AlphaMode < 1.5h) ? 1.0h : dif.a;
}

half3 SMOverrideTexture(float3 positionOS, half3 normalOS, float2 uv)
{
    half3 result;
    if (_OvProjection > 0.5h)
    {
        result = SAMPLE_TEXTURE2D(_OvTex, sampler_OvTex, uv * _OvScale).rgb;
    }
    else
    {
        // Triplanar in object space (metres)
        float3 p = positionOS * _OvScale;
        half3 n = abs(normalOS);
        half3 w = n * n * n * n;
        w /= max(w.x + w.y + w.z, 1e-4h);
        half3 cx = SAMPLE_TEXTURE2D(_OvTex, sampler_OvTex, p.zy).rgb;
        half3 cy = SAMPLE_TEXTURE2D(_OvTex, sampler_OvTex, p.xz).rgb;
        half3 cz = SAMPLE_TEXTURE2D(_OvTex, sampler_OvTex, p.xy).rgb;
        result = cx * w.x + cy * w.y + cz * w.z;
    }
    return result;
}

// Diffuse override: replace paint (and optionally the whole surface) with a colour or texture,
// optionally only where the paint matches one colour.
void SMApplyOverride(inout half3 paint, inout half3 difRgb, float3 positionOS, half3 normalOS, float2 uv)
{
    if (_OvEnabled < 0.5h) return;
    half3 replacement = _OvUseTex > 0.5h ? SMOverrideTexture(positionOS, normalOS, uv) : _OvColor.rgb;
    half selected = _OvUseMatch > 0.5h ? step(distance(paint, _OvMatch.rgb), _OvTolerance) : 1.0h;
    half f = selected * _OvStrength;
    paint = lerp(paint, replacement, f);
    difRgb = lerp(difRgb, replacement, f * _OvWhole);
}

// ---------------------------------------------------------------------------------------------
// Blender compatible random numbers
// ---------------------------------------------------------------------------------------------

uint SMRot(uint x, uint k) { return (x << k) | (x >> (32u - k)); }

// Jenkins lookup3 as in Blender (BLI_hash / Cycles hash_uint), so "Part Random" matches Blender exactly
uint SMHashUint(uint kx)
{
    uint a, b, c;
    a = b = c = 0xdeadbeefu + (1u << 2u) + 13u;
    a += kx;
    c ^= b; c -= SMRot(b, 14u);
    a ^= c; a -= SMRot(c, 11u);
    b ^= a; b -= SMRot(a, 25u);
    c ^= b; c -= SMRot(b, 16u);
    a ^= c; a -= SMRot(c, 4u);
    b ^= a; b -= SMRot(a, 14u);
    c ^= b; c -= SMRot(b, 24u);
    return c;
}

// Blender White Noise (1D) "Value"
float SMWhiteNoise1D(float w) { return (float)SMHashUint(asuint(w)) / 4294967295.0; }

// Gradient noise (fBm, 3 octaves) standing in for Blender's Noise Texture: same range and scale,
// different pattern (reported by the export).
float3 SMHash33(float3 p)
{
    uint3 q = asuint(floor(p) + 1024.0);
    float3 r;
    r.x = SMHashUint(q.x ^ SMHashUint(q.y ^ SMHashUint(q.z)));
    r.y = SMHashUint(q.y ^ SMHashUint(q.z ^ SMHashUint(q.x + 7u)));
    r.z = SMHashUint(q.z ^ SMHashUint(q.x ^ SMHashUint(q.y + 13u)));
    return r / 4294967295.0 * 2.0 - 1.0;
}

float SMGradNoise(float3 p)
{
    float3 i = floor(p), f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);
    float n000 = dot(SMHash33(i + float3(0, 0, 0)), f - float3(0, 0, 0));
    float n100 = dot(SMHash33(i + float3(1, 0, 0)), f - float3(1, 0, 0));
    float n010 = dot(SMHash33(i + float3(0, 1, 0)), f - float3(0, 1, 0));
    float n110 = dot(SMHash33(i + float3(1, 1, 0)), f - float3(1, 1, 0));
    float n001 = dot(SMHash33(i + float3(0, 0, 1)), f - float3(0, 0, 1));
    float n101 = dot(SMHash33(i + float3(1, 0, 1)), f - float3(1, 0, 1));
    float n011 = dot(SMHash33(i + float3(0, 1, 1)), f - float3(0, 1, 1));
    float n111 = dot(SMHash33(i + float3(1, 1, 1)), f - float3(1, 1, 1));
    return lerp(lerp(lerp(n000, n100, u.x), lerp(n010, n110, u.x), u.y),
                lerp(lerp(n001, n101, u.x), lerp(n011, n111, u.x), u.y), u.z);
}

float SMNoiseFac(float3 p)
{
    float sum = 0.0, amp = 1.0, norm = 0.0;
    for (int o = 0; o < 3; o++)
    {
        sum += SMGradNoise(p) * amp;
        norm += amp;
        amp *= 0.5;
        p *= 2.0;
    }
    return saturate(sum / norm * 0.75 + 0.5);
}

// Unity object space -> Blender object space of the same mesh (glTF: Blender (x, z, -y); Unity: mirror X)
float3 SMBlenderObjectPos(float3 positionOS) { return float3(-positionOS.x, -positionOS.z, positionOS.y); }

// ---------------------------------------------------------------------------------------------
// Masks (SMC_Masks) and layers
// ---------------------------------------------------------------------------------------------

struct SMMasks
{
    half paint;      // 1 = painted
    half tex;        // 1 = texture
    half partRandom;
    half bodyRandom;
    half upFacing;
};

half SMMaskValue(SMMasks m, float index)
{
    if (index < -0.5) return 0.0h;
    if (index < 0.5) return m.paint;
    if (index < 1.5) return m.tex;
    if (index < 2.5) return m.partRandom;
    if (index < 3.5) return m.bodyRandom;
    return m.upFacing;
}

// mode 0: nothing, 1: constant A, 2: layer * A.x, 3: layer + A.x, 4: mix(A or layer, B or layer, mask or F)
float4 SMLayer(float4 layer, float4 op, float4 a, float4 b, float f, SMMasks m)
{
    float mode = op.x;
    if (mode < 0.5) return layer;
    if (mode < 1.5) return a;
    if (mode < 3.5)
    {
        float4 r = mode < 2.5 ? layer * a.x : layer + a.x;
        return f > 0.5 ? saturate(r) : r;
    }
    float fac = op.y > -0.5 ? SMMaskValue(m, op.y) : saturate(f);
    float4 va = op.z > 0.5 ? layer : a;
    float4 vb = op.w > 0.5 ? layer : b;
    return lerp(va, vb, fac);
}

#endif
