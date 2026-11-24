// SPDX-License-Identifier: MIT
// SMC-DETAILS for Unity: the window (Tools > SMC-AD > 汚し（SMC-DETAILS）). Same sections as the Blender add-on:
// 自動生成 / 見た目 / 手描き / デカール / 保存. Every slider is live (no baking).
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SMCAD.Details.Editor
{
    public class SMDetailsWindow : EditorWindow
    {
        const string Menu = "Tools/SMC-AD/汚し（SMC-DETAILS）";
        static readonly string[] Views = new[] { "仕上がり" }.Concat(SMDetailsSettings.Labels.Select(l => l + "だけ"))
            .Concat(new[] { "形の情報：角", "形の情報：くぼみ", "焦げ・すすだけ", "雨だれだけ", "スミ入れだけ" }).ToArray();
        static readonly string[] Modes = { "足す", "消す", "元に戻す" };

        Vector2 scroll;
        int tab;
        static readonly string[] Tabs = { "汚し", "装備（チェーン・布）" };
        float voxelCm = 3f;
        int directions = 96;
        bool fBase = true, fGen = true, fLook, fFine, fPaint, fDecal, fSave;
        Texture2D decalTex;
        float decalSize = 0.4f;
        bool placingDecal;
        string presetName = "マイ汚し";

        [MenuItem(Menu, priority = 20)]
        public static void Open() => GetWindow<SMDetailsWindow>("SMC-DETAILS");

        void OnEnable() { Selection.selectionChanged += Repaint; }
        void OnDisable()
        {
            Selection.selectionChanged -= Repaint;
            SceneView.duringSceneGui -= OnDecalScene;
        }

        void OnGUI()
        {
            var root = SMDetailsBuilder.FindRoot(Selection.activeGameObject);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (root == null)
            {
                EditorGUILayout.HelpBox("SMC-AD で取り込んだ機体（SM Lit のメッシュ）を選んでください。", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }
            EditorGUILayout.LabelField(root.name, EditorStyles.boldLabel);
            tab = GUILayout.Toolbar(tab, Tabs);
            EditorGUILayout.Space(4);
            if (tab == 1)
            {
                Decor.SMDecorGUI.Draw(root);
                EditorGUILayout.EndScrollView();
                return;
            }
            var d = root.GetComponent<SMDetails>();
            if (d == null || !d.IsReady)
            {
                voxelCm = EditorGUILayout.Slider(new GUIContent("格子の細かさ (cm)", "角・くぼみを調べる細かさ。小さいほど正確（GPU の時間が増えます）"), voxelCm, 1f, 10f);
                directions = EditorGUILayout.IntSlider(new GUIContent("調べる方向の数"), directions, 32, 256);
                if (GUILayout.Button("汚しを付ける（形を解析）", GUILayout.Height(30))) RunSetup(root);
                Report();
                EditorGUILayout.EndScrollView();
                return;
            }

            var s = d.settings;
            // the values before this frame's edits: the undo record has to be taken before the change
            string before = JsonUtility.ToJson(s);
            int viewBefore = d.view;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (EditorGUILayout.DropdownButton(new GUIContent("プリセット"), FocusType.Passive)) PresetMenu(d);
                d.view = EditorGUILayout.Popup(d.view, Views);
            }

            fBase = EditorGUILayout.BeginFoldoutHeaderGroup(fBase, "ベースのウェザリング（塗装の質感）");
            if (fBase)
            {
                s.modulation = EditorGUILayout.Slider(new GUIContent("色の変調", "上や高い所を明るく、下や下面を暗く"), s.modulation, 0f, 1f);
                s.fading = EditorGUILayout.Slider(new GUIContent("退色・日焼け", "上面が白っぽく色あせる"), s.fading, 0f, 1f);
                s.partTone = EditorGUILayout.Slider(new GUIContent("装甲ごとの色ムラ", "部品ごとに少しずつ明るさが違う"), s.partTone, 0f, 1f);
                s.filter = EditorGUILayout.Slider(new GUIContent("フィルター（色味のゆらぎ）", "暖色・寒色のまだら（ドットフィルター）"), s.filter, 0f, 1f);
                EditorGUI.indentLevel++;
                s.filterScale = EditorGUILayout.Slider("まだらの細かさ", s.filterScale, 0.2f, 10f);
                EditorGUI.indentLevel--;
                s.postShade = EditorGUILayout.Slider(new GUIContent("ポストシェーディング", "板の縁に向かって暗く"), s.postShade, 0f, 1f);
                s.pinWash = EditorGUILayout.Slider(new GUIContent("スミ入れ", "へこんだ角・ボルトのまわりの暗い線"), s.pinWash, 0f, 1f);
                s.edgeHighlight = EditorGUILayout.Slider(new GUIContent("エッジハイライト", "角の縁を明るく（ドライブラシ）"), s.edgeHighlight, 0f, 1f);
                s.streaks = EditorGUILayout.Slider(new GUIContent("雨だれ・ストリーキング", "側面を流れ落ちる筋"), s.streaks, 0f, 1f);
                EditorGUI.indentLevel++;
                s.streakDensity = EditorGUILayout.Slider("筋の多さ", s.streakDensity, 2f, 40f);
                s.streakLength = EditorGUILayout.Slider("筋の長さ", s.streakLength, 0.1f, 2f);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            fGen = EditorGUILayout.BeginFoldoutHeaderGroup(fGen, "自動生成");
            if (fGen)
            {
                for (int i = 0; i < 8; i++)
                {
                    EditorGUILayout.LabelField(SMDetailsSettings.Labels[i], EditorStyles.miniBoldLabel);
                    EditorGUI.indentLevel++;
                    s.amount[i] = EditorGUILayout.Slider("量", s.amount[i], 0f, 1f);
                    if (i != 7) s.scale[i] = EditorGUILayout.Slider(i == 6 ? "濃淡のムラの細かさ" : "模様の細かさ", s.scale[i], 0.2f, 30f);
                    if (i >= 4 && i <= 5) s.height[i] = EditorGUILayout.Slider("地面からの高さ (m)", s.height[i], 0f, 5f);
                    if (i == 3) EditorGUILayout.LabelField(" ", "砲口・排煙のまわりだけ", EditorStyles.miniLabel);
                    if (i == 6) EditorGUILayout.LabelField(" ", "全体に薄くかかる砂ぼこり（上面と角が濃い）", EditorStyles.miniLabel);
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.LabelField("角", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                s.edgeWidth = EditorGUILayout.Slider(new GUIContent("錆・塗装ハゲの幅 (m)", "部品の角からこの幅に付きます"), s.edgeWidth, 0.002f, 0.15f);
                s.cornerWidth = EditorGUILayout.Slider(new GUIContent("すみに溜まる幅 (m)", "へこんだ角（部品と部品の間）に汚れが溜まる幅"), s.cornerWidth, 0.002f, 0.2f);
                EditorGUI.indentLevel--;
                if (d.trackMax != null && d.trackMax.Any(t => t.w > 0.5f))
                {
                    EditorGUILayout.LabelField("履帯の巻き上げ（泥・土）", EditorStyles.miniBoldLabel);
                    EditorGUI.indentLevel++;
                    s.kickAmount = EditorGUILayout.Slider("量", s.kickAmount, 0f, 1.5f);
                    s.kickRange = EditorGUILayout.Slider("届く範囲 (m)", s.kickRange, 0.05f, 3f);
                    EditorGUI.indentLevel--;
                }
                if (d.trackDirt.Count > 0)
                {
                    EditorGUILayout.LabelField("履帯（リンク）", EditorStyles.miniBoldLabel);
                    EditorGUI.indentLevel++;
                    s.beltDirt = (SMDBeltDirt)EditorGUILayout.Popup("汚れの種類", (int)s.beltDirt, new[] { "泥", "砂" });
                    s.beltSoil = EditorGUILayout.Slider("量", s.beltSoil, 0f, 1f);
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.LabelField("焦げ・すす（" + (d.burnSources.Count > 0 ? string.Join("、", d.burnSources.Select(b => b.name)) : "発生源なし") + "）", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                s.burnAmount = EditorGUILayout.Slider("量", s.burnAmount, 0f, 1.5f);
                s.burnSize = EditorGUILayout.Slider("広がり", s.burnSize, 0.2f, 4f);
                s.burnColor = EditorGUILayout.ColorField("色", s.burnColor);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            fLook = EditorGUILayout.BeginFoldoutHeaderGroup(fLook, "見た目");
            if (fLook)
            {
                for (int i = 0; i < 8; i++) s.strength[i] = EditorGUILayout.Slider(SMDetailsSettings.Labels[i] + "の強さ", s.strength[i], 0f, 2f);
                s.primer = EditorGUILayout.ColorField("下地（プライマー）", s.primer);
                s.metal = EditorGUILayout.ColorField("地金", s.metal);
                s.rustLight = EditorGUILayout.ColorField("錆（明るい）", s.rustLight);
                s.rustDark = EditorGUILayout.ColorField("錆（濃い）", s.rustDark);
                s.grime = EditorGUILayout.ColorField("くすみ・すす", s.grime);
                s.mud = EditorGUILayout.ColorField("泥", s.mud);
                s.soil = EditorGUILayout.ColorField("土", s.soil);
                s.sand = EditorGUILayout.ColorField("砂", s.sand);
                s.metalRoughness = EditorGUILayout.Slider("地金のざらつき", s.metalRoughness, 0f, 1f);
                s.rustRoughness = EditorGUILayout.Slider("錆のざらつき", s.rustRoughness, 0f, 1f);
                s.mudRoughness = EditorGUILayout.Slider("泥のざらつき", s.mudRoughness, 0f, 1f);
                s.chipOnTexture = EditorGUILayout.Slider("塗装していない所の塗装ハゲ", s.chipOnTexture, 0f, 1f);
                s.wetDarken = EditorGUILayout.Slider("濡れた所の明るさ", s.wetDarken, 0f, 1f);
                s.bumpStrength = EditorGUILayout.Slider("凹凸の強さ", s.bumpStrength, 0f, 1f);
                s.bumpDistance = EditorGUILayout.Slider("凹凸の深さ (m)", s.bumpDistance, 0f, 0.05f);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            fFine = EditorGUILayout.BeginFoldoutHeaderGroup(fFine, "細かさ・形の検出");
            if (fFine)
            {
                s.fineScale = EditorGUILayout.Slider("細かい模様の倍率", s.fineScale, 0.2f, 4f);
                s.softness = EditorGUILayout.Slider("境目のぼかし", s.softness, 0.005f, 0.3f);
                s.edgeGain = EditorGUILayout.Slider("角の検出の強さ", s.edgeGain, 0f, 12f);
                s.cavityGain = EditorGUILayout.Slider("くぼみの検出の強さ", s.cavityGain, 0f, 12f);
                s.seed = EditorGUILayout.IntSlider("乱数", s.seed, 0, 99);
                s.groundOffset = EditorGUILayout.Slider("地面の高さの調整 (m)", s.groundOffset, -2f, 2f);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            if (EditorGUI.EndChangeCheck())
            {
                string after = JsonUtility.ToJson(s);
                int viewAfter = d.view;
                JsonUtility.FromJsonOverwrite(before, s);
                d.view = viewBefore;
                Undo.RecordObject(d, "SMC-DETAILS の設定");
                JsonUtility.FromJsonOverwrite(after, s);
                d.view = viewAfter;
                d.Apply();
                EditorUtility.SetDirty(d);
                SceneView.RepaintAll();
            }

            fPaint = EditorGUILayout.BeginFoldoutHeaderGroup(fPaint, "手描き");
            if (fPaint) PaintGUI(d);
            EditorGUILayout.EndFoldoutHeaderGroup();

            fDecal = EditorGUILayout.BeginFoldoutHeaderGroup(fDecal, "デカール");
            if (fDecal) DecalGUI(d);
            EditorGUILayout.EndFoldoutHeaderGroup();

            fSave = EditorGUILayout.BeginFoldoutHeaderGroup(fSave, "保存・やり直し");
            if (fSave)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    presetName = EditorGUILayout.TextField(presetName);
                    if (GUILayout.Button("プリセットとして保存", GUILayout.Width(150))) SavePreset(presetName, s);
                }
                voxelCm = EditorGUILayout.Slider("格子の細かさ (cm)", voxelCm, 1f, 10f);
                if (GUILayout.Button("形を解析し直す（パーツを動かした・足した後に）")) RunSetup(root);
                if (GUILayout.Button("汚しを外す（元のマテリアルに戻す）"))
                {
                    SMDetailsPaint.End(false);
                    SMDetailsBuilder.Remove(d);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
            Report();
            EditorGUILayout.EndScrollView();
        }

        void RunSetup(Transform root)
        {
            SMDetailsPaint.End(true);
            try
            {
                EditorUtility.DisplayProgressBar("SMC-DETAILS", "形を解析しています（GPU）…", 0.5f);
                SMDetailsBuilder.Setup(root, voxelCm / 100f, directions);
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                SMDetailsBuilder.LastReport = "失敗: " + ex.Message;
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        static void Report()
        {
            if (!string.IsNullOrEmpty(SMDetailsBuilder.LastReport))
                EditorGUILayout.HelpBox(SMDetailsBuilder.LastReport, MessageType.None);
        }

        // ------------------------------------------------------------------ paint

        void PaintGUI(SMDetails d)
        {
            bool painting = SMDetailsPaint.Target == d;
            int ch = GUILayout.SelectionGrid(SMDetailsPaint.Channel, SMDetailsSettings.Labels, 4);
            if (ch != SMDetailsPaint.Channel) { SMDetailsPaint.Channel = ch; d.view = 0; d.Apply(); }
            SMDetailsPaint.Mode = GUILayout.Toolbar(SMDetailsPaint.Mode, Modes);
            SMDetailsPaint.Radius = EditorGUILayout.Slider("ブラシの大きさ (m)", SMDetailsPaint.Radius, 0.02f, 2f);
            SMDetailsPaint.Strength = EditorGUILayout.Slider("ブラシの強さ", SMDetailsPaint.Strength, 0.02f, 1f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (!painting)
                {
                    if (GUILayout.Button("描く（シーンビューでドラッグ）", GUILayout.Height(26))) SMDetailsPaint.Begin(d);
                }
                else
                {
                    if (GUILayout.Button("描き終える（保存）", GUILayout.Height(26))) SMDetailsPaint.End(true);
                    if (GUILayout.Button("この汚れの手描きを全部戻す", GUILayout.Height(26))) SMDetailsPaint.ClearChannelAll();
                }
            }
            if (painting) EditorGUILayout.HelpBox("ドラッグで描く / Shift+ドラッグで消す / Alt+ドラッグで視点を回す", MessageType.None);
        }

        // ------------------------------------------------------------------ decals (HDRP Decal Projector)

        void DecalGUI(SMDetails d)
        {
            var hd = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (hd != null && !hd.currentPlatformRenderPipelineSettings.supportDecals)
            {
                EditorGUILayout.HelpBox("デカールを表示するには、HDRP アセットの「Decals」を有効にしてください（Rendering → Decals）。", MessageType.Warning);
                if (GUILayout.Button("HDRP アセットを開く")) { Selection.activeObject = hd; EditorGUIUtility.PingObject(hd); }
            }
            decalTex = (Texture2D)EditorGUILayout.ObjectField("画像", decalTex, typeof(Texture2D), false);
            decalSize = EditorGUILayout.Slider("大きさ (m)", decalSize, 0.05f, 3f);
            using (new EditorGUI.DisabledScope(decalTex == null))
            {
                bool now = GUILayout.Toggle(placingDecal, placingDecal ? "貼るのをやめる（右クリックでも）" : "クリックして貼る", "Button", GUILayout.Height(26));
                if (now != placingDecal)
                {
                    placingDecal = now;
                    SceneView.duringSceneGui -= OnDecalScene;
                    if (placingDecal) { SMDetailsPaint.End(true); SceneView.duringSceneGui += OnDecalScene; }
                }
            }
            EditorGUILayout.HelpBox("貼ったデカールは当たったボディの子になります。動かす・回す・大きさは普通のオブジェクトと同じです。", MessageType.None);
        }

        void OnDecalScene(SceneView view)
        {
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);
            if (e.type == EventType.MouseDown && e.button == 1) { placingDecal = false; SceneView.duringSceneGui -= OnDecalScene; Repaint(); return; }
            if (e.type != EventType.MouseDown || e.button != 0 || e.alt || decalTex == null) return;
            var root = SMDetailsBuilder.FindRoot(Selection.activeGameObject);
            var go = HandleUtility.PickGameObject(e.mousePosition, false);
            if (go == null || root == null || !go.transform.IsChildOf(root)) return;
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null) return;
            var col = go.AddComponent<MeshCollider>();
            col.hideFlags = HideFlags.HideAndDontSave;
            col.sharedMesh = mf.sharedMesh;
            bool hit = col.Raycast(HandleUtility.GUIPointToWorldRay(e.mousePosition), out var h, 10000f);
            DestroyImmediate(col);
            if (!hit) return;
            PlaceDecal(go.transform, h.point, h.normal, view.camera.transform.up, decalTex, decalSize);
            e.Use();
        }

        public static GameObject PlaceDecal(Transform body, Vector3 point, Vector3 normal, Vector3 up, Texture2D tex, float size)
        {
            string folder = SMDetailsBuilder.OutputRoot + "/Decals";
            Directory.CreateDirectory(folder);
            string matPath = folder + "/" + tex.name + "_Decal.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                // HDRP/Decal: colour (and its alpha) only, so the panel lines / normal map underneath stay
                mat = new Material(Shader.Find("HDRP/Decal"));
                mat.SetTexture("_BaseColorMap", tex);
                mat.SetFloat("_AffectAlbedo", 1f);
                mat.SetFloat("_AffectNormal", 0f);
                mat.SetFloat("_AffectMetal", 0f);
                mat.SetFloat("_AffectAO", 0f);
                mat.SetFloat("_AffectSmoothness", 0f);
                mat.SetFloat("_AffectEmission", 0f);
                mat.enableInstancing = true;
                HDMaterial.ValidateMaterial(mat);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            var go = new GameObject("Decal_" + tex.name);
            Undo.RegisterCreatedObjectUndo(go, "デカールを貼る");
            go.transform.SetParent(body, false);
            go.transform.position = point;
            go.transform.rotation = Quaternion.LookRotation(-normal, Vector3.ProjectOnPlane(up, normal).sqrMagnitude > 1e-4f ? up : Vector3.forward);
            var p = go.AddComponent<DecalProjector>();
            p.material = mat;
            float aspect = tex.height / (float)tex.width;
            p.size = new Vector3(size, size * aspect, 0.1f);
            p.pivot = Vector3.zero;
            Selection.activeGameObject = go;
            return go;
        }

        // ------------------------------------------------------------------ presets

        static string PresetFolder => SMDetailsBuilder.OutputRoot + "/Presets";

        static Dictionary<string, SMDetailsSettings> Builtin()
        {
            SMDetailsSettings P(float[] amount, System.Action<SMDetailsSettings> extra = null)
            {
                var s = new SMDetailsSettings { amount = amount };
                extra?.Invoke(s);
                return s;
            }
            Color G(float r, float g, float b) => new Color(r, g, b).gamma;
            return new Dictionary<string, SMDetailsSettings>
            {
                { "新品（ほぼきれい）", P(new[] { 0.12f, 0.05f, 0f, 0.2f, 0f, 0.1f, 0f, 0f }, s => { s.height[5] = 0.4f; s.scale[0] = 8f; }) },
                { "使い込み", P(new[] { 0.35f, 0.2f, 0.15f, 0.4f, 0.3f, 0.3f, 0.25f, 0f }, s => { s.height[4] = 0.5f; s.height[5] = 1.2f; s.height[6] = 0.3f; }) },
                { "錆だらけ", P(new[] { 0.55f, 0.25f, 0.6f, 0.55f, 0.1f, 0.2f, 0f, 0f }, s => { s.scale[2] = 3f; s.height[4] = 0.3f; s.height[5] = 0.8f; s.rustLight = G(0.42f, 0.14f, 0.035f); s.rustDark = G(0.1f, 0.04f, 0.02f); }) },
                { "泥まみれ", P(new[] { 0.3f, 0.15f, 0.1f, 0.45f, 0.75f, 0.55f, 0f, 0.6f }, s => { s.height[4] = 1.0f; s.scale[4] = 4f; s.height[5] = 2.0f; }) },
                { "砂漠", P(new[] { 0.3f, 0.3f, 0f, 0.25f, 0f, 0.3f, 0.65f, 0f }, s => { s.height[5] = 1.0f; s.height[6] = 0.6f; s.scale[6] = 5f; s.soil = G(0.42f, 0.32f, 0.2f); s.sand = G(0.62f, 0.5f, 0.34f); }) },
            };
        }

        static void PresetMenu(SMDetails d)
        {
            var menu = new GenericMenu();
            foreach (var kv in Builtin()) menu.AddItem(new GUIContent(kv.Key), false, () => ApplyPreset(d, kv.Value));
            if (Directory.Exists(PresetFolder))
            {
                menu.AddSeparator("");
                foreach (var f in Directory.GetFiles(PresetFolder, "*.json"))
                {
                    string path = f;
                    menu.AddItem(new GUIContent("自分: " + Path.GetFileNameWithoutExtension(f)), false, () =>
                    {
                        var s = new SMDetailsSettings();
                        EditorJsonUtility.FromJsonOverwrite(File.ReadAllText(path), s);
                        ApplyPreset(d, s);
                    });
                }
            }
            menu.ShowAsContext();
        }

        public static void ApplyPreset(SMDetails d, string name) => ApplyPreset(d, Builtin()[name]);

        static void ApplyPreset(SMDetails d, SMDetailsSettings s)
        {
            Undo.RecordObject(d, "SMC-DETAILS プリセット");
            d.settings = s.Clone();
            d.Apply();
            EditorUtility.SetDirty(d);
            SceneView.RepaintAll();
        }

        static void SavePreset(string name, SMDetailsSettings s)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            Directory.CreateDirectory(PresetFolder);
            File.WriteAllText(Path.Combine(PresetFolder, name.Trim() + ".json"), EditorJsonUtility.ToJson(s, true));
            AssetDatabase.Refresh();
        }
    }
}
