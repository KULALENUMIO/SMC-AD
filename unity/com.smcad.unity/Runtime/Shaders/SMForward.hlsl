// SPDX-License-Identifier: MIT
// Forward lighting for SM Lit / SM Glass
#ifndef SMCAD_FORWARD_INCLUDED
#define SMCAD_FORWARD_INCLUDED

#include "SMCommon.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#if defined(_SMCAD_DETAILS)
#include "SMDetails.hlsl"
#endif

struct SMAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 tangentOS  : TANGENT;
    float2 uv         : TEXCOORD0;
    float2 shape      : TEXCOORD2;   // x = SM part id (_SM_SHAPE), y = body random
    float2 atlas      : TEXCOORD3;   // x = row of the material table (atlas mode)
#if defined(_SMCAD_DETAILS)
    float4 edgeConvex : TEXCOORD4;   // SMC-DETAILS: distance to the triangle's convex edges
    float3 restPos    : TEXCOORD5;   // SMC-DETAILS: rest-pose position
    float3 restNormal : TEXCOORD6;   // SMC-DETAILS: rest-pose normal
    float3 edgeConcave : TEXCOORD7;  // SMC-DETAILS: distance to the triangle's concave edges
#endif
    half4  color      : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct SMVaryings
{
    float4 positionCS : SV_POSITION;
    float2 uv         : TEXCOORD0;
    float3 positionWS : TEXCOORD1;
    half3  normalWS   : TEXCOORD2;
    half4  tangentWS  : TEXCOORD3;
    half4  color      : TEXCOORD4;
    float3 positionOS : TEXCOORD5;
    half3  normalOS   : TEXCOORD6;
    half   fogFactor  : TEXCOORD7;
    nointerpolation float3 shape : TEXCOORD8;   // part id, body random, atlas row
#if defined(_SMCAD_DETAILS)
    float3 restPos    : TEXCOORD9;
    float3 restNormal : TEXCOORD10;
    float3 edgeConvex : TEXCOORD11;
    float3 edgeConcave : TEXCOORD12;
#endif
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

SMVaryings SMForwardVert(SMAttributes input)
{
    SMVaryings o = (SMVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, o);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    VertexPositionInputs vpi = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs vni = GetVertexNormalInputs(input.normalOS, input.tangentOS);

    o.positionCS = vpi.positionCS;
    o.positionWS = vpi.positionWS;
    o.normalWS = vni.normalWS;
    o.tangentWS = half4(vni.tangentWS, input.tangentOS.w * GetOddNegativeScale());
    o.uv = TRANSFORM_TEX(input.uv, _DifMap);
    o.color = input.color;
    o.positionOS = input.positionOS.xyz;
    o.normalOS = input.normalOS;
    o.fogFactor = ComputeFogFactor(vpi.positionCS.z);
    o.shape = float3(input.shape, input.atlas.x);
#if defined(_SMCAD_DETAILS)
    o.restPos = input.restPos;
    o.restNormal = input.restNormal;
    o.edgeConvex = input.edgeConvex.xyz;
    o.edgeConcave = input.edgeConcave;
#endif
    return o;
}

half4 SMForwardFrag(SMVaryings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    half4 dif = SMSampleDif(input.uv);
    half4 asg = SMSampleAsg(input.uv);

    half cutout = SMCutoutAlpha(dif, asg);
    if (_AlphaMode > 0.5h) clip(cutout - _Cutoff);

    SMParams p = SMGetParams(input.shape.z);
    half3 paint = input.color.rgb;
    half3 difRgb = dif.rgb;
    SMApplyOverride(paint, difRgb, input.positionOS, input.normalOS, input.uv, p);
    half texMask = SMPaintMask(dif);
    half3 albedo = lerp(paint, difRgb, texMask);

    // Normal (double sided: flip for back faces)
    half facing = IS_FRONT_VFACE(face, 1.0h, -1.0h);
    float3 geomNormalWS = normalize(input.normalWS * facing);
    float3 normalWS = geomNormalWS;
    if (_HasNor > 0.5h)
    {
        half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NorMap, sampler_NorMap, input.uv));
        float3 tangentWS = input.tangentWS.xyz * facing;
        float3 bitangentWS = input.tangentWS.w * cross(geomNormalWS, tangentWS);
        normalWS = TransformTangentToWorld(normalTS, half3x3(tangentWS, bitangentWS, geomNormalWS));
    }
    normalWS = NormalizeNormalPerPixel(normalWS);

    SurfaceData surface = (SurfaceData)0;
    surface.occlusion = 1.0h;
    surface.normalTS = half3(0.0h, 0.0h, 1.0h);

#if defined(SMCAD_GLASS)
    surface.albedo = albedo;
    surface.metallic = 0.0h;
    // SM glass: low roughness tinted glass, mostly transparent
    half roughnessGlass = asg.g * asg.a * 0.3h;
    surface.smoothness = 1.0h - roughnessGlass;
    surface.specular = half3(0.04h, 0.04h, 0.04h);
    surface.alpha = saturate(1.0h - _Transparency);
#else
    // ---- SM decode (the SMC_Decode node group)
    float3 baseColor = albedo;
    float metallic = 0.0;
    float roughness = 1.0 - asg.a;
    float specular = asg.g * p.specularMul;                 // Principled "Specular IOR Level"
    float emissionStrength = saturate((asg.b - 0.15h) / 0.85h) * p.glowMul;

    SMMasks masks;
    masks.tex = texMask;
    masks.paint = 1.0h - texMask;
    masks.partRandom = SMWhiteNoise1D(input.shape.x);
    masks.bodyRandom = input.shape.y;
    masks.upFacing = max(geomNormalWS.y, 0.0);

    // ---- surface type (SMC+_SurfaceCore)
    if (p.sfEnabled > 0.5h)
    {
        baseColor = lerp(baseColor, baseColor * p.sfTint, p.sfTintStrength);
        metallic = max(metallic, lerp(p.sfTexMetallic, p.sfPaintMetallic, masks.paint));
        float noise = p.sfNoise > 0.0h ? SMNoiseFac(SMBlenderObjectPos(input.positionOS) * p.sfNoiseScale) : 0.5;
        roughness = saturate(roughness * p.sfRoughScale + p.sfRoughOffset + (noise - 0.5) * p.sfNoise);
        specular *= p.sfSpecMul;
        normalWS = normalize(geomNormalWS + (normalWS - geomNormalWS) * p.sfNormalStrength);
        surface.clearCoatMask = p.sfCoat;
        surface.clearCoatSmoothness = 1.0h - p.sfCoatRough;
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
    SMApplyDetails(input.restPos, input.restNormal, input.edgeConvex, input.edgeConcave, masks.paint, masks.partRandom, input.positionWS, facing, baseColor, metallic, roughness, detailNormal);
    normalWS = detailNormal;
    metallic = saturate(metallic);
    roughness = saturate(roughness);
#endif

    // ---- Principled BSDF -> URP specular setup
    // Blender "Specular IOR Level" 0.5 == F0 0.04, so F0 = 0.08 * specular; metals take F0 from the base colour
    surface.albedo = baseColor * (1.0 - metallic);
    surface.specular = lerp((0.08 * max(specular, 0.0)).xxx, baseColor, metallic);
    surface.metallic = 0.0h;
    surface.smoothness = 1.0 - roughness;
    surface.emission = emissionColor * max(emissionStrength, 0.0);
    surface.alpha = 1.0h;
#endif

    InputData inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    inputData.positionCS = input.positionCS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    inputData.fogCoord = input.fogFactor;
    inputData.vertexLighting = half3(0.0h, 0.0h, 0.0h);
    inputData.bakedGI = SampleSH(normalWS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    inputData.shadowMask = half4(1.0h, 1.0h, 1.0h, 1.0h);

    half4 color = UniversalFragmentPBR(inputData, surface);
    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    return color;
}

#endif
