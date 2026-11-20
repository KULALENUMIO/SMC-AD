// SPDX-License-Identifier: MIT
Shader "SMC-AD/SM Lit"
{
    Properties
    {
        _DifMap ("Dif（RGB＝色、A＝塗装マスク）", 2D) = "white" {}
        _AsgMap ("Asg（R＝切り抜き、G＝反射、B＝発光、A＝つや）", 2D) = "gray" {}
        [Normal] _NorMap ("Nor（ノーマルマップ）", 2D) = "bump" {}
        [Toggle] _HasDif ("Dif あり", Float) = 0
        [Toggle] _HasAsg ("Asg あり", Float) = 0
        [Toggle] _HasNor ("Nor あり", Float) = 0
        [Enum(Opaque,0,CutoutDifA,1,CutoutAsgR,2)] _AlphaMode ("透明の扱い", Float) = 0
        _Cutoff ("切り抜きのしきい値", Range(0, 1)) = 0.5
        _SpecularMul ("反射の強さ", Range(0, 4)) = 1
        _GlowMul ("発光の強さ", Range(0, 10)) = 1
        [Toggle] _OvEnabled ("色を差し替える", Float) = 0
        [Toggle] _OvWhole ("表面全体（オフ＝塗装だけ）", Float) = 0
        _OvColor ("差し替える色", Color) = (0.8, 0.1, 0.1, 1)
        [Toggle] _OvUseTex ("画像を使う", Float) = 0
        _OvTex ("差し替える画像", 2D) = "white" {}
        [Enum(Box,0,UV,1)] _OvProjection ("貼り方", Float) = 0
        _OvScale ("画像の大きさ（1メートルあたりの繰り返し）", Float) = 1
        _OvStrength ("強さ", Range(0, 1)) = 1
        [Toggle] _OvUseMatch ("1つの塗装色だけ", Float) = 0
        _OvMatch ("差し替える塗装色", Color) = (0.5, 0.5, 0.5, 1)
        _OvTolerance ("許容差", Range(0, 1)) = 0.02
        [Toggle] _SfEnabled ("材質を使う", Float) = 0
        _SfTexMetallic ("金属っぽさ（地）", Range(0, 1)) = 0
        _SfPaintMetallic ("金属っぽさ（塗装）", Range(0, 1)) = 0
        _SfRoughScale ("ざらつきの倍率", Range(0, 4)) = 1
        _SfRoughOffset ("ざらつき", Range(-1, 1)) = 0
        _SfSpecMul ("材質の反射の強さ", Range(0, 4)) = 1
        _SfCoat ("塗装の厚み（クリアコート）", Range(0, 1)) = 0
        _SfCoatRough ("塗装面のざらつき", Range(0, 1)) = 0.1
        _SfNormalStrength ("凹凸の強さ", Range(0, 5)) = 1
        _SfTint ("色味（リニアRGB）", Vector) = (1, 1, 1, 1)
        _SfTintStrength ("色味の強さ", Range(0, 1)) = 0
        _SfNoise ("ざらつきのムラ", Range(0, 1)) = 0
        _SfNoiseScale ("ムラの細かさ", Float) = 10
        _LBaseOp ("色：演算", Vector) = (0, -1, 0, 0)
        _LBaseA ("色：A", Vector) = (0, 0, 0, 1)
        _LBaseB ("色：B", Vector) = (0, 0, 0, 1)
        _LBaseF ("色：係数", Float) = 0
        _LMetalOp ("金属っぽさ：演算", Vector) = (0, -1, 0, 0)
        _LMetalA ("金属っぽさ：A", Vector) = (0, 0, 0, 0)
        _LMetalB ("金属っぽさ：B", Vector) = (0, 0, 0, 0)
        _LMetalF ("金属っぽさ：係数", Float) = 0
        _LRoughOp ("ざらつき：演算", Vector) = (0, -1, 0, 0)
        _LRoughA ("ざらつき：A", Vector) = (0, 0, 0, 0)
        _LRoughB ("ざらつき：B", Vector) = (0, 0, 0, 0)
        _LRoughF ("ざらつき：係数", Float) = 0
        _LSpecOp ("反射：演算", Vector) = (0, -1, 0, 0)
        _LSpecA ("反射：A", Vector) = (0, 0, 0, 0)
        _LSpecB ("反射：B", Vector) = (0, 0, 0, 0)
        _LSpecF ("反射：係数", Float) = 0
        _LEmisOp ("発光：演算", Vector) = (0, -1, 0, 0)
        _LEmisA ("発光：A", Vector) = (0, 0, 0, 0)
        _LEmisB ("発光：B", Vector) = (0, 0, 0, 0)
        _LEmisF ("発光：係数", Float) = 0

        [Toggle(_SMCAD_DETAILS)] _SmdEnabled ("汚しを使う", Float) = 0
        [NoScaleOffset] _SmdGeo ("形の情報（3D）", 3D) = "" {}
        [NoScaleOffset] _SmdNoise ("模様（3D）", 3D) = "" {}
        [NoScaleOffset] _SmdPaint0 ("手描き：塗装ハゲ・傷・錆・くすみ（3D）", 3D) = "" {}
        [NoScaleOffset] _SmdPaint1 ("手描き：泥・土・砂・濡れ（3D）", 3D) = "" {}
        _SmdGridMin ("格子の始点", Vector) = (0, 0, 0, 0)
        _SmdGridInv ("格子の大きさの逆数", Vector) = (1, 1, 1, 0)
        _SmdAmount0 ("量：塗装ハゲ・傷・錆・くすみ", Vector) = (0.35, 0.2, 0.15, 0.4)
        _SmdAmount1 ("量：泥・土・砂・濡れ", Vector) = (0.4, 0.3, 0.2, 0)
        _SmdScale0 ("模様の細かさ：塗装ハゲ・傷・錆・くすみ", Vector) = (6, 3, 4, 3)
        _SmdScale1 ("模様の細かさ：泥・土・砂", Vector) = (5, 2, 6, 1)
        _SmdHeights ("地面からの高さ：泥・土・砂、地面", Vector) = (0.6, 1.2, 0.3, 0)
        _SmdGen0 ("角の強さ、くぼみの強さ、近い/遠い調べ位置(m)", Vector) = (4, 4, 0.03, 0.12)
        _SmdGen1 ("細かい模様の倍率、境目のぼかし、手描きあり、乱数", Vector) = (1, 0.06, 0, 0)
        _SmdView ("表示（0 = 仕上がり）", Float) = 0
        _SmdEdge ("角の幅(m)、へこんだ角の幅(m)、角のデータあり", Vector) = (0.03, 0.04, 0, 0)
        _SmdKick ("履帯の巻き上げ：範囲(m)、量", Vector) = (0.6, 0.8, 0, 0)
        _SmdBurn ("焦げ・すすの量", Vector) = (1, 0, 0, 0)
        _SmdBurnColor ("焦げ・すす（リニアRGB）", Vector) = (0.015, 0.013, 0.012, 1)
        [HideInInspector] _SmdTrackMin0 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdTrackMax0 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdTrackMin1 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdTrackMax1 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdSrcP0 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdSrcP1 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdSrcP2 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdSrcP3 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdSrcD0 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdSrcD1 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdSrcD2 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdSrcD3 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdLinkSeed ("", Float) = 0
        [HideInInspector] _SmdBase0 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdBase1 ("", Vector) = (0, 0, 0, 0)
        [HideInInspector] _SmdBase2 ("", Vector) = (1.5, 12, 0.5, 3)
        _SmdStrength0 ("強さ：塗装ハゲ・傷・錆・くすみ", Vector) = (1, 1, 1, 1)
        _SmdStrength1 ("強さ：泥・土・砂・濡れ", Vector) = (1, 1, 1, 1)
        _SmdPrimer ("下地（プライマー、リニアRGB）", Vector) = (0.32, 0.12, 0.07, 1)
        _SmdMetal ("地金（リニアRGB）", Vector) = (0.45, 0.45, 0.46, 1)
        _SmdRustA ("錆・明るい（リニアRGB）", Vector) = (0.38, 0.13, 0.04, 1)
        _SmdRustB ("錆・濃い（リニアRGB）", Vector) = (0.12, 0.05, 0.025, 1)
        _SmdGrime ("くすみ・すす（リニアRGB）", Vector) = (0.18, 0.15, 0.12, 1)
        _SmdMud ("泥（リニアRGB）", Vector) = (0.09, 0.065, 0.045, 1)
        _SmdSoil ("土（リニアRGB）", Vector) = (0.25, 0.18, 0.12, 1)
        _SmdSand ("砂（リニアRGB）", Vector) = (0.55, 0.45, 0.32, 1)
        _SmdParams0 ("ざらつき：地金・錆・泥、塗装していない所の塗装ハゲ", Vector) = (0.35, 0.9, 0.7, 0.3)
        _SmdParams1 ("濡れた所の明るさ、凹凸の強さ、凹凸の深さ(m)", Vector) = (0.55, 0.6, 0.004, 0)

        [HideInInspector] _Transparency ("透明度", Float) = 0
        [HideInInspector][NoScaleOffset] _SMParams ("アトラスの設定表", 2D) = "black" {}
        [HideInInspector][Toggle(_SMCAD_ATLAS)] _SMAtlas ("アトラス", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" "IgnoreProjector" = "True" }
        LOD 300
        Cull Off

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SMForwardVert
            #pragma fragment SMForwardFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _CLEARCOAT
            #pragma shader_feature_local _SMCAD_ATLAS
            #pragma shader_feature_local _SMCAD_DETAILS

            #define _SPECULAR_SETUP 1
            #include "SMForward.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SMShadowVert
            #pragma fragment SMShadowFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing

            #include "SMCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            V SMShadowVert(A input)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(input);
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                o.positionCS = positionCS;
                o.uv = TRANSFORM_TEX(input.uv, _DifMap);
                return o;
            }

            half4 SMShadowFrag(V input) : SV_Target
            {
                if (_AlphaMode > 0.5h) clip(SMCutoutAlpha(SMSampleDif(input.uv), SMSampleAsg(input.uv)) - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SMDepthVert
            #pragma fragment SMDepthFrag
            #pragma multi_compile_instancing

            #include "SMCommon.hlsl"

            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            V SMDepthVert(A input)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = TRANSFORM_TEX(input.uv, _DifMap);
                return o;
            }

            half SMDepthFrag(V input) : SV_Target
            {
                if (_AlphaMode > 0.5h) clip(SMCutoutAlpha(SMSampleDif(input.uv), SMSampleAsg(input.uv)) - _Cutoff);
                return input.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SMDepthNormalsVert
            #pragma fragment SMDepthNormalsFrag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing

            #include "SMCommon.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };

            V SMDepthNormalsVert(A input)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                o.uv = TRANSFORM_TEX(input.uv, _DifMap);
                return o;
            }

            half4 SMDepthNormalsFrag(V input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                if (_AlphaMode > 0.5h) clip(SMCutoutAlpha(SMSampleDif(input.uv), SMSampleAsg(input.uv)) - _Cutoff);
                float3 normalWS = NormalizeNormalPerPixel(input.normalWS * IS_FRONT_VFACE(face, 1.0, -1.0));
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remapped = saturate(octNormalWS * 0.5 + 0.5);
                return half4(PackFloat2To888(remapped), 0.0);
            #else
                return half4(normalWS, 0.0);
            #endif
            }
            ENDHLSL
        }
    }

    CustomEditor "SMCAD.Editor.SMLitGUI"
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
