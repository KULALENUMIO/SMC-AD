// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Rendering.HighDefinition;
using UnityEngine;

namespace SMCAD.Editor
{
    /// <summary>
    /// Material inspector for "SMC-AD/SM Lit" and "SMC-AD/SM Glass" (HDRP Shader Graphs) with Japanese section
    /// headings and choices. The HDRP surface settings (double-sided, alpha clipping, render queue) come from the
    /// graph and the SM alpha mode, so only the SM properties are shown; HDRP validation runs on every change.
    /// </summary>
    public class SMLitGUI : LightingShaderGraphGUI
    {
        static readonly Dictionary<string, string> Sections = new Dictionary<string, string>
        {
            { "_DifMap", "Scrap Mechanic のテクスチャ" },
            { "_OvEnabled", "色の差し替え" },
            { "_SfEnabled", "材質（SMC-AD+ Surfaces）" },
            { "_SmdEnabled", "汚し（SMC-DETAILS）" },
            { "_LBaseOp", "Blender の手作業ノード（演算 = 種類・マスク・Aはレイヤー・Bはレイヤー）" },
        };

        static readonly Dictionary<string, string[]> Choices = new Dictionary<string, string[]>
        {
            { "_AlphaMode", new[] { "不透明", "Dif の A で切り抜き", "Asg の R で切り抜き" } },
            { "_OvProjection", new[] { "6方向から投影", "UV" } },
        };

        static bool showLayers, showHdrp;

        protected override void OnMaterialGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            EditorGUI.BeginChangeCheck();
            bool inLayers = false;
            foreach (var p in properties)
            {
                if ((p.propertyFlags & UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) != 0) continue;
                if (!p.name.StartsWith("_") || IsHdrpProperty(p.name)) continue;

                if (Sections.TryGetValue(p.name, out string heading))
                {
                    EditorGUILayout.Space();
                    inLayers = p.name == "_LBaseOp";
                    if (inLayers)
                        showLayers = EditorGUILayout.Foldout(showLayers, heading, true);
                    else
                        EditorGUILayout.LabelField(heading, EditorStyles.boldLabel);
                }
                if (inLayers && !showLayers) continue;

                if (Choices.TryGetValue(p.name, out string[] names))
                {
                    EditorGUI.showMixedValue = p.hasMixedValue;
                    EditorGUI.BeginChangeCheck();
                    int value = EditorGUILayout.Popup(p.displayName, Mathf.RoundToInt(p.floatValue), names);
                    if (EditorGUI.EndChangeCheck())
                    {
                        editor.RegisterPropertyChangeUndo(p.displayName);
                        p.floatValue = value;
                    }
                    EditorGUI.showMixedValue = false;
                    continue;
                }
                editor.ShaderProperty(p, p.displayName);
            }
            if (EditorGUI.EndChangeCheck())
            {
                foreach (Material m in editor.targets) ValidateMaterial(m);
            }

            EditorGUILayout.Space();
            showHdrp = EditorGUILayout.Foldout(showHdrp, "HDRP の設定（通常は変更不要）", true);
            if (showHdrp) base.OnMaterialGUI(editor, properties);
        }

        /// <summary>SM side first (alpha mode -> alpha clipping, details toggle -> keyword), then HDRP.</summary>
        public override void ValidateMaterial(Material material)
        {
            SMMaterialSetup.SyncSM(material);
            base.ValidateMaterial(material);
        }

        // properties HDRP adds to every Lit graph (drawn by the HDRP section instead)
        static bool IsHdrpProperty(string name) =>
            name.StartsWith("_Stencil") || name.StartsWith("_Alpha") && name != "_AlphaMode" || name.StartsWith("_Blend")
            || name.StartsWith("_Src") || name.StartsWith("_Dst") || name.StartsWith("_ZTest") || name.StartsWith("_ZWrite")
            || name.StartsWith("_Cull") || name.StartsWith("_Transparent") || name.StartsWith("_DoubleSided")
            || name == "_SurfaceType" || name == "_RenderQueueType" || name == "_UseShadowThreshold" || name == "_ReceivesSSR"
            || name == "_ReceivesSSRTransparent" || name == "_SupportDecals" || name == "_EnableFogOnTransparent"
            || name == "_EnableBlendModePreserveSpecularLighting" || name == "_ConservativeDepthOffsetEnable"
            || name == "_DiffusionProfileHash" || name == "_DiffusionProfileAsset" || name == "_EmissionColor"
            || name == "_RayTracing" || name == "_RefractionModel" || name == "_TransmissionEnable" || name == "_MaterialID"
            || name == "_MaterialTypeMask" || name == "_DepthOffsetEnable" || name == "_OpaqueCullMode" || name == "_CoatMask"
            || name == "_EnableGeometricSpecularAA" || name == "_SpecularAAScreenSpaceVariance" || name == "_SpecularAAThreshold"
            || name == "_PerPixelSorting" || name == "_TransparentBackfaceEnable";
    }
}
