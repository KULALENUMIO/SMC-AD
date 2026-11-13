// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SMCAD.Editor
{
    /// <summary>
    /// Material inspector for "SMC-AD/SM Lit" and "SMC-AD/SM Glass" with Japanese section headings and choices.
    /// (ShaderLab attributes such as [Header] / [Enum] only accept ASCII, so they are drawn here instead.)
    /// </summary>
    public class SMLitGUI : ShaderGUI
    {
        static readonly Dictionary<string, string> Sections = new Dictionary<string, string>
        {
            { "_DifMap", "Scrap Mechanic のテクスチャ" },
            { "_OvEnabled", "色の差し替え" },
            { "_SfEnabled", "材質（SMC-AD+ Surfaces）" },
            { "_LBaseOp", "Blender の手作業ノード（演算 = 種類・マスク・Aはレイヤー・Bはレイヤー）" },
        };

        static readonly Dictionary<string, string[]> Choices = new Dictionary<string, string[]>
        {
            { "_AlphaMode", new[] { "不透明", "Dif の A で切り抜き", "Asg の R で切り抜き" } },
            { "_OvProjection", new[] { "6方向から投影", "UV" } },
        };

        static bool showLayers;

        public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            bool inLayers = false;
            foreach (var p in properties)
            {
                if ((p.propertyFlags & UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) != 0) continue;

                if (Sections.TryGetValue(p.name, out string heading))
                {
                    EditorGUILayout.Space();
                    inLayers = p.name == "_LBaseOp";
                    if (inLayers)
                    {
                        showLayers = EditorGUILayout.Foldout(showLayers, heading, true);
                    }
                    else
                    {
                        EditorGUILayout.LabelField(heading, EditorStyles.boldLabel);
                    }
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

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("描画の設定", EditorStyles.boldLabel);
            editor.RenderQueueField();
            editor.EnableInstancingField();

            // Clear coat keyword follows the clear coat amount (Lit only)
            foreach (Material m in editor.targets)
            {
                if (!m.HasProperty("_SfCoat")) continue;
                bool on = m.GetFloat("_SfEnabled") > 0.5f && m.GetFloat("_SfCoat") > 0f;
                if (on != m.IsKeywordEnabled("_CLEARCOAT"))
                {
                    if (on) m.EnableKeyword("_CLEARCOAT"); else m.DisableKeyword("_CLEARCOAT");
                }
            }
        }
    }
}
