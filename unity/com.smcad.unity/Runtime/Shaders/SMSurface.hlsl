// SPDX-License-Identifier: MIT
// SM surface for SM Lit / SM Glass, pipeline-neutral: everything up to the BSDF inputs, no lighting.
// The render pipeline side (SMGraph.hlsl for the HDRP Shader Graphs) feeds the inputs and lights the result.
// Specular set-up throughout (Blender's Principled BSDF -> diffuse colour + F0 colour), as in the URP version.
#ifndef SMCAD_SURFACE_INCLUDED
#define SMCAD_SURFACE_INCLUDED

#include "SMCommon.hlsl"
#if defined(_SMCAD_DETAILS)
#include "SMDetails.hlsl"
#endif

struct SMSurfaceInput
{
    float2 uv;              // UV0 (the caller applies _DifMap_ST)
    float3 positionOS;
    float3 normalOS;
    float4 color;           // vertex colour = SM paint colour (linear)
    float3 shape;           // part id (UV2.x), body random (UV2.y), atlas row (UV3.x)
    float3 positionWS;      // world position as the pipeline uses it (HDRP: camera relative)
    float3 normalWS;        // geometric normal, already turned towards the viewer on back faces
    float3 tangentWS;
    float3 bitangentWS;     // tangent sign included
    float facing;           // 1 front face, -1 back face
    // SMC-DETAILS (_SMCAD_DETAILS): rest position, rest normal, convex / concave edge distances (HDRP: UV5, UV6, UV1, UV7)
    float3 restPos;
    float3 restNormal;
    float3 edgeConvex;
    float3 edgeConcave;
};

struct SMSurfaceOutput
{
    float3 diffuse;         // diffuse colour (base colour without the metal part)
    float3 specular;        // F0 colour
    float smoothness;
    float3 normalWS;
    float3 emission;        // linear, "LDR" strength as in Blender / URP (the pipeline scales it)
    float alpha;
    float coatMask;
    float coatSmoothness;
};

// Normal map in the given tangent frame
float3 SMNormalWS(SMSurfaceInput i)
{
    float3 n = normalize(i.normalWS);
    if (_HasNor > 0.5)
    {
        float3 ts = UnpackNormal(SAMPLE_TEXTURE2D(_NorMap, sampler_NorMap, i.uv));
        n = normalize(i.tangentWS * ts.x + i.bitangentWS * ts.y + n * ts.z);
    }
    return n;
}

// Cut-out alpha only (cheap: used alone by the depth / shadow passes)
float SMAlphaLit(float2 uv)
{
    return _AlphaMode > 0.5 ? SMCutoutAlpha(SMSampleDif(uv), SMSampleAsg(uv)) : 1.0;
}

void SMAlbedo(SMSurfaceInput i, SMParams p, half4 dif, out float3 albedo, out half texMask)
{
    half3 paint = i.color.rgb;
    half3 difRgb = dif.rgb;
    SMApplyOverride(paint, difRgb, i.positionOS, i.normalOS, i.uv, p);
    texMask = SMPaintMask(dif);
    albedo = lerp(paint, difRgb, texMask);
}

SMSurfaceOutput SMEvaluateLit(SMSurfaceInput i)
{
    SMSurfaceOutput o = (SMSurfaceOutput)0;
    half4 dif = SMSampleDif(i.uv);
    half4 asg = SMSampleAsg(i.uv);
    SMParams p = SMGetParams(i.shape.z);

    float3 albedo; half texMask;
    SMAlbedo(i, p, dif, albedo, texMask);

    float3 geomNormalWS = normalize(i.normalWS);
    float3 normalWS = SMNormalWS(i);

    // ---- SM decode (the SMC_Decode node group)
    float3 baseColor = albedo;
    float metallic = 0.0;
    float roughness = 1.0 - asg.a;
    float specular = asg.g * p.specularMul;                 // Principled "Specular IOR Level"
    float emissionStrength = saturate((asg.b - 0.15) / 0.85) * p.glowMul;

    SMMasks masks;
    masks.tex = texMask;
    masks.paint = 1.0h - texMask;
    masks.partRandom = SMWhiteNoise1D(i.shape.x);
    masks.bodyRandom = i.shape.y;
    masks.upFacing = max(geomNormalWS.y, 0.0);

    // ---- surface type (SMC+_SurfaceCore)
    if (p.sfEnabled > 0.5h)
    {
        baseColor = lerp(baseColor, baseColor * p.sfTint, p.sfTintStrength);
        metallic = max(metallic, lerp(p.sfTexMetallic, p.sfPaintMetallic, masks.paint));
        float noise = p.sfNoise > 0.0h ? SMNoiseFac(SMBlenderObjectPos(i.positionOS) * p.sfNoiseScale) : 0.5;
        roughness = saturate(roughness * p.sfRoughScale + p.sfRoughOffset + (noise - 0.5) * p.sfNoise);
        specular *= p.sfSpecMul;
        normalWS = normalize(geomNormalWS + (normalWS - geomNormalWS) * p.sfNormalStrength);
        o.coatMask = p.sfCoat;
        o.coatSmoothness = 1.0 - p.sfCoatRough;
    }
    float3 emissionColor = baseColor;  // Blender: Emission Color is wired before the hand-made layers

    // ---- hand-made layers (exported from the layer wires in Blender)
    baseColor = SMLayer(float4(baseColor, 1.0), _LBaseOp, _LBaseA, _LBaseB, _LBaseF, masks).rgb;
    metallic = SMLayer(metallic.xxxx, _LMetalOp, _LMetalA, _LMetalB, _LMetalF, masks).x;
    roughness = SMLayer(roughness.xxxx, _LRoughOp, _LRoughA, _LRoughB, _LRoughF, masks).x;
    specular = SMLayer(specular.xxxx, _LSpecOp, _LSpecA, _LSpecB, _LSpecF, masks).x;
    emissionStrength = SMLayer(emissionStrength.xxxx, _LEmisOp, _LEmisA, _LEmisB, _LEmisF, masks).x;
    metallic = saturate(metallic);
    roughness = saturate(roughness);

    // ---- wear and dirt (SMC-DETAILS): last, right before the BSDF as in Blender
#if defined(_SMCAD_DETAILS)
    float3 detailNormal = normalWS;
    SMApplyDetails(i.restPos, i.restNormal, i.edgeConvex, i.edgeConcave, masks.paint, masks.partRandom, i.positionWS, (half)i.facing,
                   baseColor, metallic, roughness, detailNormal);
    normalWS = detailNormal;
    metallic = saturate(metallic);
    roughness = saturate(roughness);
#endif

    // ---- Principled BSDF -> specular set-up
    // Blender "Specular IOR Level" 0.5 == F0 0.04, so F0 = 0.08 * specular; metals take F0 from the base colour
    o.diffuse = baseColor * (1.0 - metallic);
    o.specular = lerp((0.08 * max(specular, 0.0)).xxx, baseColor, metallic);
    o.smoothness = 1.0 - roughness;
    o.normalWS = normalWS;
    o.emission = emissionColor * max(emissionStrength, 0.0);
    o.alpha = _AlphaMode > 0.5 ? SMCutoutAlpha(dif, asg) : 1.0;
    return o;
}

SMSurfaceOutput SMEvaluateGlass(SMSurfaceInput i)
{
    SMSurfaceOutput o = (SMSurfaceOutput)0;
    half4 dif = SMSampleDif(i.uv);
    half4 asg = SMSampleAsg(i.uv);
    SMParams p = SMGetParams(i.shape.z);
    float3 albedo; half texMask;
    SMAlbedo(i, p, dif, albedo, texMask);
    // SM glass: low roughness tinted glass, mostly transparent
    o.diffuse = albedo;
    o.specular = 0.04.xxx;
    o.smoothness = 1.0 - asg.g * asg.a * 0.3;
    o.normalWS = SMNormalWS(i);
    o.alpha = saturate(1.0 - _Transparency);
    return o;
}

#endif
