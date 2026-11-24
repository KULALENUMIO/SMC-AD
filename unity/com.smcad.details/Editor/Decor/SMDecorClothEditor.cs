// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: the cloth / net settings (inspector and the scene-view panel share DrawSettings).
// Draping takes about a second, so it runs when a slider is let go (or on 「掛け直す」), not on every drag step.
using System.Collections.Generic;
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    [CustomEditor(typeof(SMDecorCloth))]
    public class SMDecorClothEditor : UnityEditor.Editor
    {
        static readonly string[] Types = { "カモネット", "布", "防水布" };
        static readonly string[] Palettes = { "森林", "砂漠", "雪", "オリーブ" };
        static readonly HashSet<SMDecorCloth> pending = new HashSet<SMDecorCloth>();

        public override void OnInspectorGUI()
        {
            var cloth = (SMDecorCloth)target;
            DrawSettings(cloth, false);
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(Info(cloth), MessageType.None);
            if (GUILayout.Button("掛け直す")) SMDecorCloths.Rebuild(cloth);
        }

        public static string Info(SMDecorCloth c) =>
            $"{c.size.x:F2} × {c.size.y:F2} m、点 {c.particles:N0}、めり込み 最大 {c.maxPenetration * 1000f:F1} mm、{c.buildSeconds:F1} 秒";

        public static void DrawSettings(SMDecorCloth cloth, bool compact)
        {
            float wl = EditorGUIUtility.labelWidth;
            if (compact) EditorGUIUtility.labelWidth = 92;
            EditorGUI.BeginChangeCheck();
            var type = (SMDecorClothType)EditorGUILayout.Popup("種類", (int)cloth.type, Types);
            var pal = (SMDecorPalette)EditorGUILayout.Popup("色", (int)cloth.palette, Palettes);
            float extra = EditorGUILayout.Slider(new GUIContent("余り", "四隅の間より、布をどれだけ大きくするか（ゆったり垂れる）"), cloth.extra, 0f, 0.8f);
            float stiff = EditorGUILayout.Slider(new GUIContent("固さ"), cloth.stiffness, 0f, 1f);
            float thick = EditorGUILayout.Slider(new GUIContent("厚み (mm)"), cloth.thickness * 1000f, 2f, 50f) / 1000f;
            float garnish = cloth.garnish, meshSize = cloth.meshSize;
            if (type == SMDecorClothType.CamoNet)
            {
                garnish = EditorGUILayout.Slider(new GUIContent("葉の量"), cloth.garnish, 0f, 1f);
                meshSize = EditorGUILayout.Slider(new GUIContent("網目 (cm)"), cloth.meshSize * 100f, 4f, 20f) / 100f;
            }
            bool pin = EditorGUILayout.Toggle(new GUIContent("四隅を留める", "外すと、四隅は範囲を決めるだけで自由に垂れる"), cloth.pinCorners);
            float cell = compact ? cloth.cell : EditorGUILayout.Slider(new GUIContent("格子の細かさ (cm)", "小さいほど細かく垂れる（時間がかかる）"), cloth.cell * 100f, 2f, 12f) / 100f;
            EditorGUIUtility.labelWidth = wl;
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(cloth, "布・ネットの設定");
                cloth.type = type; cloth.palette = pal; cloth.extra = extra; cloth.stiffness = stiff; cloth.thickness = thick;
                cloth.garnish = garnish; cloth.meshSize = meshSize; cloth.pinCorners = pin; cloth.cell = cell;
                EditorUtility.SetDirty(cloth);
                pending.Add(cloth);
            }
            // drape again once the slider / field is let go
            if (pending.Contains(cloth) && GUIUtility.hotControl == 0)
            {
                pending.Remove(cloth);
                EditorApplication.delayCall += () => { if (cloth != null) SMDecorCloths.Rebuild(cloth); };
            }
        }
    }
}
