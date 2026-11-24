// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: the chain's settings (inspector and the scene-view panel share DrawSettings); every change
// rebuilds the chain at once.
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    [CustomEditor(typeof(SMDecorChain))]
    public class SMDecorChainEditor : UnityEditor.Editor
    {
        static readonly string[] Hangers = { "なし", "溶接リング", "シャックル" };
        static readonly string[] Finishes = { "黒い鋼", "錆", "塗装" };
        static readonly string[] Styles = { "レース", "カーテン" };

        public override void OnInspectorGUI()
        {
            var chain = (SMDecorChain)target;
            DrawSettings(chain, false);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox($"長さ {chain.length:F2} m、リンク {chain.links}、吊り点 {chain.hangPoints}、" +
                                    $"めり込み 最大 {chain.maxPenetration * 1000f:F1} mm", MessageType.None);
            if (GUILayout.Button("作り直す")) SMDecorChains.Rebuild(chain);
        }

        /// <summary>The settings; rebuilds the chain when something changed. compact = the scene-view panel.</summary>
        public static void DrawSettings(SMDecorChain chain, bool compact)
        {
            float wl = EditorGUIUtility.labelWidth;
            if (compact) EditorGUIUtility.labelWidth = 92;
            EditorGUI.BeginChangeCheck();
            float total = chain.PathLength;
            var style = (SMDecorChainStyle)EditorGUILayout.Popup(new GUIContent("タイプ", "レース: 吊り点の間を垂れる　カーテン: 吊り点から下へ垂れ下がり、先に鉄球"), (int)chain.style, Styles);
            bool curtain = style == SMDecorChainStyle.Curtain;
            float spacing = EditorGUILayout.Slider(new GUIContent("密度（間隔 m）", curtain ? "垂れ下がる鎖の間隔" : "吊り点の間隔"), chain.spacing, curtain ? 0.04f : 0.15f, 1.5f);
            float slack = chain.slack, drop = chain.dropLength, vary = chain.dropVariation, ball = chain.ballRadius;
            if (!curtain)
                slack = EditorGUILayout.Slider(new GUIContent("長さ（たるみ）", "吊り点の間の距離に対する鎖の余り（0 = ぴんと張る）"), chain.slack, 0f, 0.6f);
            else
            {
                drop = EditorGUILayout.Slider(new GUIContent("長さ (m)", "垂れ下がる長さ"), chain.dropLength, 0.05f, 2f);
                vary = EditorGUILayout.Slider(new GUIContent("長さのばらつき"), chain.dropVariation, 0f, 0.6f);
                ball = EditorGUILayout.Slider(new GUIContent("鉄球の直径 (mm)", "0 = なし"), chain.ballRadius * 2000f, 0f, 120f) / 2000f;
            }
            float wire = EditorGUILayout.Slider(new GUIContent("線径 (mm)"), chain.wire * 1000f, 4f, 20f) / 1000f;
            float ts = EditorGUILayout.Slider(new GUIContent("始点を詰める (m)"), chain.trimStart, 0f, Mathf.Max(0.01f, total));
            float te = EditorGUILayout.Slider(new GUIContent("終点を詰める (m)"), chain.trimEnd, 0f, Mathf.Max(0.01f, total));
            var hanger = (SMDecorHanger)EditorGUILayout.Popup("金具", (int)chain.hanger, Hangers);
            var finish = (SMDecorFinish)EditorGUILayout.Popup("仕上げ", (int)chain.finish, Finishes);
            float jitter = compact ? chain.jitter : EditorGUILayout.Slider(new GUIContent("ねじれの乱れ"), chain.jitter, 0f, 1f);
            bool settle = compact ? chain.settle : EditorGUILayout.Toggle(new GUIContent("機体に沿わせる"), chain.settle);
            EditorGUIUtility.labelWidth = wl;
            if (!EditorGUI.EndChangeCheck()) return;
            Undo.RecordObject(chain, "チェーンの設定");
            if (style != chain.style && style == SMDecorChainStyle.Curtain && spacing > 0.3f) spacing = 0.15f;   // a curtain needs dense strands
            chain.style = style; chain.dropLength = drop; chain.dropVariation = vary; chain.ballRadius = ball;
            chain.spacing = spacing; chain.slack = slack; chain.wire = wire;
            chain.trimStart = ts; chain.trimEnd = te;
            chain.hanger = hanger; chain.finish = finish; chain.jitter = jitter; chain.settle = settle;
            SMDecorChains.Rebuild(chain);
        }
    }
}
