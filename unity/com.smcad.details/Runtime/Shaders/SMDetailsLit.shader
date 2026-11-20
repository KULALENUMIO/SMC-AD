// SPDX-License-Identifier: MIT
// SMC-DETAILS: a small URP Lit replacement (base colour / texture, metallic, smoothness, normal map) with the same
// wear and dirt layer as SM Lit (SMDetails.hlsl). Used for parts that come with the blueprint but are not SM parts,
// e.g. the wheels of the Tank Rigging plugin. _SMD_BELT: track links (they move along the belt) — soil only,
// computed in the link's own object space.
Shader "SMC-AD/Details Lit"
{
    Properties
    {
        _BaseMap ("基本の色（画像）", 2D) = "white" {}
        _BaseColor ("基本の色", Color) = (1, 1, 1, 1)
        _Metallic ("金属っぽさ", Range(0, 1)) = 0
        _Smoothness ("なめらかさ", Range(0, 1)) = 0.5
        [Normal] _BumpMap ("ノーマルマップ", 2D) = "bump" {}
        _BumpScale ("ノーマルの強さ", Float) = 1

        [Toggle(_SMCAD_DETAILS)] _SmdEnabled ("汚しを使う", Float) = 0
        [Toggle(_SMD_BELT)] _SmdBelt ("履帯（土汚れだけ、部品の中の座標）", Float) = 0
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
        _SmdGen0 ("角の強さ、くぼみの強さ、近い/遠い調べ位置(m)", Vector) = (3, 2.5, 0.05, 0.18)
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
        _SmdParams1 ("濡れた所の明るさ、凹凸の強さ、凹凸の深さ(m)", Vector) = (0.55, 0.6, 0.006, 0)
    }

    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
    TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
    CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST;
        half4 _BaseColor;
        half _Metallic;
        half _Smoothness;
        half _BumpScale;
        float4 _SmdGridMin;   float4 _SmdGridInv;
        float4 _SmdAmount0;   float4 _SmdAmount1;   float4 _SmdScale0;  float4 _SmdScale1;
        float4 _SmdHeights;   float4 _SmdGen0;      float4 _SmdGen1;
        float _SmdView;
        float4 _SmdStrength0;  float4 _SmdStrength1;
        float4 _SmdPrimer; float4 _SmdMetal; float4 _SmdRustA; float4 _SmdRustB;
        float4 _SmdGrime;  float4 _SmdMud;   float4 _SmdSoil;  float4 _SmdSand;
        float4 _SmdParams0; float4 _SmdParams1;
    float4 _SmdEdge;      // edge width (m), corner width (m), has edge data, -
    float4 _SmdKick;      // track kick-up range (m), amount
    float4 _SmdBurn;      // soot amount
    float4 _SmdBurnColor;
    float4 _SmdTrackMin0; float4 _SmdTrackMax0; float4 _SmdTrackMin1; float4 _SmdTrackMax1;   // w = used
    float4 _SmdSrcP0; float4 _SmdSrcP1; float4 _SmdSrcP2; float4 _SmdSrcP3;   // soot sources: pos, radius
    float4 _SmdSrcD0; float4 _SmdSrcD1; float4 _SmdSrcD2; float4 _SmdSrcD3;   // direction, type
    float _SmdLinkSeed;
    float4 _SmdBase0;     // base weathering: colour modulation, fading, per-part tone, filter
    float4 _SmdBase1;     // post-shading, pin wash, edge highlight, streaks
    float4 _SmdBase2;     // filter scale (1/m), streak density (1/m), streak length, model height (m)
    CBUFFER_END
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma shader_feature_local _SMCAD_DETAILS
            #pragma shader_feature_local _SMD_BELT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #if defined(_SMCAD_DETAILS)
            #include "Packages/com.smcad.unity/Runtime/Shaders/SMDetails.hlsl"
            #endif

            struct A
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            #if defined(_SMCAD_DETAILS) && !defined(_SMD_BELT)
                float4 edgeConvex : TEXCOORD4;
                float3 restPos : TEXCOORD5;
                float3 restNormal : TEXCOORD6;
                float3 edgeConcave : TEXCOORD7;
            #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct V
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half4 tangentWS : TEXCOORD3;
                float3 restPos : TEXCOORD4;
                float3 restNormal : TEXCOORD5;
                half fogFactor : TEXCOORD6;
                float3 edgeConvex : TEXCOORD7;
                float3 edgeConcave : TEXCOORD8;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            V vert(A input)
            {
                V o = (V)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs vpi = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs vni = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                o.positionCS = vpi.positionCS;
                o.positionWS = vpi.positionWS;
                o.normalWS = vni.normalWS;
                o.tangentWS = half4(vni.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
            #if defined(_SMCAD_DETAILS) && !defined(_SMD_BELT)
                o.restPos = input.restPos;
                o.restNormal = input.restNormal;
                o.edgeConvex = input.edgeConvex.xyz;
                o.edgeConcave = input.edgeConcave;
            #else
                o.edgeConvex = o.edgeConcave = float3(10.0, 10.0, 10.0);
                o.restPos = input.positionOS.xyz;   // track links: their own space (they travel along the belt)
                o.restNormal = input.normalOS;
            #endif
                o.fogFactor = ComputeFogFactor(vpi.positionCS.z);
                return o;
            }

            half4 frag(V input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half facing = IS_FRONT_VFACE(face, 1.0h, -1.0h);
                float3 baseColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
                float metallic = _Metallic;
                float roughness = 1.0 - _Smoothness;
                float3 n = normalize(input.normalWS * facing);
                half3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv), _BumpScale);
                float3 t = input.tangentWS.xyz * facing;
                float3 b = input.tangentWS.w * cross(n, t);
                float3 normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, half3x3(t, b, n)));
            #if defined(_SMCAD_DETAILS)
                SMApplyDetails(input.restPos, input.restNormal, input.edgeConvex, input.edgeConcave, 1.0h, 0.5, input.positionWS, facing, baseColor, metallic, roughness, normalWS);
                metallic = saturate(metallic);
                roughness = saturate(roughness);
            #endif
                SurfaceData s = (SurfaceData)0;
                s.albedo = baseColor;
                s.metallic = metallic;
                s.smoothness = 1.0 - roughness;
                s.occlusion = 1.0h;
                s.alpha = 1.0h;
                s.normalTS = half3(0, 0, 1);
                InputData d = (InputData)0;
                d.positionWS = input.positionWS;
                d.positionCS = input.positionCS;
                d.normalWS = normalWS;
                d.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                d.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                d.fogCoord = input.fogFactor;
                d.bakedGI = SampleSH(normalWS);
                d.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                d.shadowMask = half4(1, 1, 1, 1);
                half4 c = UniversalFragmentPBR(d, s);
                c.rgb = MixFog(c.rgb, d.fogCoord);
                return c;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;
            float3 _LightPosition;
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            float4 vert(A input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 p = TransformObjectToWorld(input.positionOS.xyz);
                float3 n = TransformObjectToWorldNormal(input.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 l = normalize(_LightPosition - p);
            #else
                float3 l = _LightDirection;
            #endif
                float4 c = TransformWorldToHClip(ApplyShadowBias(p, n, l));
            #if UNITY_REVERSED_Z
                c.z = min(c.z, UNITY_NEAR_CLIP_VALUE);
            #else
                c.z = max(c.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return c;
            }
            half4 frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            struct A { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            float4 vert(A input) : SV_POSITION { UNITY_SETUP_INSTANCE_ID(input); return TransformObjectToHClip(input.positionOS.xyz); }
            half frag(float4 p : SV_POSITION) : SV_Target { return p.z; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            V vert(A input)
            {
                V o;
                UNITY_SETUP_INSTANCE_ID(input);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return o;
            }
            half4 frag(V input) : SV_Target
            {
                float3 n = normalize(input.normalWS);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 oct = PackNormalOctQuadEncode(n);
                return half4(PackFloat2To888(saturate(oct * 0.5 + 0.5)), 0.0);
            #else
                return half4(n, 0.0);
            #endif
            }
            ENDHLSL
        }
    }
}
