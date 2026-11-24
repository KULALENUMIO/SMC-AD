// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: the 「装備（チェーン・布）」 tab of the SMC-DETAILS window.
//   距離場 / 置く / 風と動き / 付けた装備 (list: edit, select, remove; all again; tidy files)
using System.IO;
using System.Linq;
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    public static class SMDecorGUI
    {
        static float voxelCm = 2.5f;
        static float maxCm = 40f;
        static bool fField = true, fPlace = true, fWind = true, fList = true;

        public static void Draw(Transform root)
        {
            var set = root.GetComponent<SMDecorSet>();

            // ------------------------------------------------ distance field
            fField = EditorGUILayout.BeginFoldoutHeaderGroup(fField, "距離場（布・チェーンが機体に当たる計算に使う）");
            if (fField)
            {
                voxelCm = EditorGUILayout.Slider(new GUIContent("格子の細かさ (cm)", "小さいほど正確（GPU の時間とファイルが増えます）"), voxelCm, 1f, 6f);
                maxCm = EditorGUILayout.Slider(new GUIContent("調べる距離 (cm)", "機体からこれより遠い所は同じ値になります"), maxCm, 10f, 100f);
                if (set != null && set.HasField)
                {
                    EditorGUILayout.LabelField("状態", $"{set.dims.x}×{set.dims.y}×{set.dims.z}（{set.voxel * 100f:F2} cm）");
                    var moved = set.poses.Where(p => p.part != null && set.PoseChanged(p.part)).Select(p => SMDecorParts.Label(root, p.part)).ToList();
                    if (moved.Count > 0)
                        EditorGUILayout.HelpBox($"距離場を作った後に動いた部位があります: {string.Join("、", moved)}。作り直して、装備も掛け直してください", MessageType.Warning);
                }
                if (GUILayout.Button(set != null && set.HasField ? "距離場を作り直す" : "距離場を作る（形を解析）", GUILayout.Height(26)))
                {
                    try { SMDecorField.Build(root, voxelCm / 100f, maxCm / 100f); }
                    catch (System.Exception ex) { EditorUtility.DisplayDialog("SMC-DETAILS 装備", ex.Message, "OK"); }
                    GUIUtility.ExitGUI();
                }
                if (!string.IsNullOrEmpty(SMDecorField.LastReport))
                    EditorGUILayout.HelpBox(SMDecorField.LastReport, MessageType.None);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            set = root.GetComponent<SMDecorSet>();
            if (set == null) return;

            // ------------------------------------------------ placing
            fPlace = EditorGUILayout.BeginFoldoutHeaderGroup(fPlace, "置く");
            if (fPlace)
            {
                EditorGUILayout.HelpBox("シーンビューの道具で付けます。\n" +
                                        "チェーン: 稜線をクリック（Shift+クリックでつなぐ）、辺に沿う、自由\n" +
                                        "布・ネット: 表面を 4 回クリック\n" +
                                        "切り抜き: 布の上をなぞる / 多角形で囲んで Enter", MessageType.None);
                using (new EditorGUI.DisabledScope(!set.HasField))
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (int m = 0; m < SMDecorTool.Modes.Length; m++)
                        if (GUILayout.Button(SMDecorTool.Modes[m], GUILayout.Height(26))) OpenTool(set, m);
                }
                if (GUILayout.Button(new GUIContent("作業ビューを開く", "車両だけを映す別のウィンドウ。明るく表示し、砲塔などを隠して下に付けたり、正面・側面から見たりできる。道具はそのまま使える")))
                    SMWorkView.Open(set.transform);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ------------------------------------------------ wind and motion
            fWind = EditorGUILayout.BeginFoldoutHeaderGroup(fWind, "風と動き（再生中に動く）");
            if (fWind)
            {
                var wind = Object.FindAnyObjectByType<SMDecorWind>();
                if (wind == null)
                {
                    EditorGUILayout.LabelField("シーンに風がありません（弱い風で動きます）", EditorStyles.miniLabel);
                    if (GUILayout.Button("風を置く"))
                    {
                        var go = new GameObject("装備の風");
                        Undo.RegisterCreatedObjectUndo(go, "風を置く");
                        Selection.activeGameObject = go.AddComponent<SMDecorWind>().gameObject;
                    }
                }
                else
                {
                    var so = new SerializedObject(wind);
                    EditorGUILayout.PropertyField(so.FindProperty("direction"), new GUIContent("向き"));
                    EditorGUILayout.PropertyField(so.FindProperty("speed"), new GUIContent("風速 (m/s)"));
                    EditorGUILayout.PropertyField(so.FindProperty("gust"), new GUIContent("突風"));
                    EditorGUILayout.PropertyField(so.FindProperty("blastScale"), new GUIContent("発砲の爆風"));
                    EditorGUILayout.PropertyField(so.FindProperty("inEditor"), new GUIContent("エディターでも動かす"));
                    so.ApplyModifiedProperties();
                }
                var motions = set.items.Where(x => x != null).Select(x => x.GetComponent<SMDecorMotion>()).Where(m => m != null).ToList();
                if (motions.Count > 0)
                {
                    float amount = motions[0].amount;
                    EditorGUI.BeginChangeCheck();
                    amount = EditorGUILayout.Slider(new GUIContent("動きの大きさ（全部）", "1 つずつは各装備の「装備の動き」で"), amount, 0f, 3f);
                    if (EditorGUI.EndChangeCheck())
                        foreach (var m in motions) { Undo.RecordObject(m, "動きの大きさ"); m.amount = amount; }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ------------------------------------------------ list
            var items = set.items.Where(x => x != null).ToList();
            fList = EditorGUILayout.BeginFoldoutHeaderGroup(fList, $"付けた装備（{items.Count}）");
            if (fList)
            {
                foreach (var d in items)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string state = d.OutOfDate ? "（要作り直し）" : "";
                        EditorGUILayout.LabelField(d.name + state, $"{d.Label}・{SMDecorParts.Label(root, d.part)}");
                        if (GUILayout.Button("編集", GUILayout.Width(44)))
                        {
                            if (d is SMDecorChain ch) { SMDecorTool.Current = ch; OpenTool(set, 0); }
                            else if (d is SMDecorCloth cl) { SMDecorTool.CurrentCloth = cl; OpenTool(set, 1); }
                        }
                        if (GUILayout.Button("選ぶ", GUILayout.Width(44))) Selection.activeGameObject = d.gameObject;
                        if (GUILayout.Button("外す", GUILayout.Width(44))) { SMDecorEditing.Remove(d); GUIUtility.ExitGUI(); }
                    }
                }
                if (items.Count == 0) EditorGUILayout.LabelField("まだありません", EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(items.Count == 0 || !set.HasField))
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(new GUIContent("全部を掛け直す", "距離場を作り直した後や、部位を動かした後に"))) { SMDecorEditing.RebuildAll(set); GUIUtility.ExitGUI(); }
                    if (GUILayout.Button(new GUIContent("使っていないファイルを消す", "装備のフォルダーにある、どの装備も使っていないメッシュ"))) { Tidy(set); GUIUtility.ExitGUI(); }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        static void OpenTool(SMDecorSet set, int mode)
        {
            SMDecorTool.Mode = mode;
            Selection.activeGameObject = set.gameObject;
            ToolManager.SetActiveTool<SMDecorTool>();
            SceneView.lastActiveSceneView?.Focus();
        }

        /// <summary>Deletes Chain_*.asset / Cloth_*.asset in the vehicle's Decor folder that no decoration in the open
        /// scenes uses (asks first; other scenes may still use some, so the list is shown).</summary>
        static void Tidy(SMDecorSet set)
        {
            string folder = set.assetFolder;
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder)) return;
            var used = Object.FindObjectsByType<SMDecoration>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(d => d.mesh != null).Select(d => AssetDatabase.GetAssetPath(d.mesh)).ToHashSet();
            var unused = AssetDatabase.FindAssets("t:Mesh", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => (Path.GetFileName(p).StartsWith("Chain_") || Path.GetFileName(p).StartsWith("Cloth_")) && !used.Contains(p)).ToList();
            if (unused.Count == 0) { EditorUtility.DisplayDialog("SMC-DETAILS 装備", "使っていないファイルはありません", "OK"); return; }
            if (!EditorUtility.DisplayDialog("SMC-DETAILS 装備",
                    $"開いているシーンのどの装備も使っていないメッシュが {unused.Count} 個あります。\n" +
                    "別のシーンで使っているものがあれば、そちらは壊れます。消しますか？\n\n" + string.Join("\n", unused.Take(12).Select(Path.GetFileName)),
                    "消す", "やめる")) return;
            foreach (var p in unused) AssetDatabase.DeleteAsset(p);
        }
    }
}
