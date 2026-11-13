// SPDX-License-Identifier: MIT
Shader "SMC-AD/SM Glass"
{
    Properties
    {
        _DifMap ("Dif（RGB＝色、A＝塗装マスク）", 2D) = "white" {}
        _AsgMap ("Asg（G＝反射、A＝つや）", 2D) = "gray" {}
        [Normal] _NorMap ("Nor（ノーマルマップ）", 2D) = "bump" {}
        [Toggle] _HasDif ("Dif あり", Float) = 0
        [Toggle] _HasAsg ("Asg あり", Float) = 0
        [Toggle] _HasNor ("Nor あり", Float) = 0
        _Transparency ("透明度", Range(0, 1)) = 0.7
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

        [HideInInspector] _AlphaMode ("透明の扱い", Float) = 0
        [HideInInspector] _Cutoff ("切り抜きのしきい値", Float) = 0.5
        [HideInInspector] _SpecularMul ("反射の強さ", Float) = 1
        [HideInInspector] _GlowMul ("発光の強さ", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "IgnoreProjector" = "True" }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SMForwardVert
            #pragma fragment SMForwardFrag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #define _SPECULAR_SETUP 1
            #define _SURFACE_TYPE_TRANSPARENT 1
            #define SMCAD_GLASS 1
            #include "SMForward.hlsl"
            ENDHLSL
        }
    }

    CustomEditor "SMCAD.Editor.SMLitGUI"
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
