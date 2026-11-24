// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: the scene-view tool (active when an SMDecorSet is selected).
//   チェーン: the vehicle's ridge lines are shown; the one under the mouse lights up; click = a new chain along it,
//            Shift+click = join the ridge to the end of the chain being edited, Ctrl+click = edit an existing chain.
//            The chain being edited has handles at both ends (trim) and a settings panel.
//   布・ネット / 切り抜き: D2 / D3 (for now: the point, part and distance under the mouse).
using System.Collections.Generic;
using System.Linq;
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    [EditorTool("SMC-AD 装備（チェーン・布）", typeof(SMDecorSet))]
    public class SMDecorTool : EditorTool
    {
        public static readonly string[] Modes = { "チェーン", "布・ネット", "切り抜き" };
        public static int Mode;
        public static SMDecorChain Current;
        static readonly string[] Hints =
        {
            "稜線をクリック: チェーンを付ける　Shift+クリック: 編集中のチェーンの端につなぐ　Ctrl+クリック: 付けたチェーンを選ぶ",
            "機体の表面を 4 回クリック: 布・ネットを掛ける（Esc / 右クリックでやり直し）　角の球: 四隅を動かす　Ctrl+クリック: 掛けた布を選ぶ",
            "ブラシ: 布の上をなぞって切る（Shift で戻す）　多角形: 布の上を順にクリック → Enter で切る（Shift+Enter で戻す、Esc でやり直し）　Ctrl+クリック: 布を選ぶ",
        };

        GUIContent icon;
        public override GUIContent toolbarIcon => icon ??= new GUIContent(EditorGUIUtility.IconContent("d_Cloth Icon").image, "SMC-AD 装備（チェーン・布）");

        bool hasHit;
        SMDecorHit hit;
        SMDecorRidge hover;

        // ridge lines of the selected vehicle (found again when the field is rebuilt)
        static List<SMDecorRidge> ridges;
        static Texture3D ridgesOf;
        static string ridgeInfo = "";

        public static List<SMDecorRidge> Ridges(SMDecorSet set, bool force = false)
        {
            if (force || ridges == null || ridgesOf != set.field)
            {
                ridges = set.HasField ? SMDecorRidges.Find(set) : new List<SMDecorRidge>();
                ridgesOf = set.field;
                ridgeInfo = SMDecorRidges.LastInfo;
            }
            return ridges;
        }

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView view) || !(target is SMDecorSet set)) return;
            var root = set.transform;
            var e = Event.current;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));   // keep clicks for the tool
            if (Current != null && Current.set != set) Current = null;

            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                hasHit = SMDecorPick.Raycast(root, ray, out hit);
                hover = Mode == 0 && set.HasField ? NearestRidge(set, ray) : null;
                view.Repaint();
            }

            if (CurrentCloth != null && CurrentCloth.set != set) CurrentCloth = null;
            if (Mode == 0 && set.HasField) ChainMode(set, view, e);
            else if (Mode == 1 && set.HasField) ClothMode(set, view, e);
            else if (Mode == 2 && set.HasField) CutMode(set, view, e);
            else PointInfo(set, e);

            Panel(set);
        }

        // ------------------------------------------------------------------ cloths / nets

        public static SMDecorCloth CurrentCloth;
        public static SMDecorClothType NewClothType = SMDecorClothType.CamoNet;
        static readonly List<SMDecorHit> picks = new List<SMDecorHit>();
        static bool cornerDragged;

        void ClothMode(SMDecorSet set, SceneView view, Event e)
        {
            var root = set.transform;
            // picking the four corners
            if (e.type == EventType.Repaint)
            {
                Handles.color = new Color(1f, 0.8f, 0.15f);
                for (int i = 0; i < picks.Count; i++)
                {
                    Handles.DrawWireDisc(picks[i].point, picks[i].normal, HandleUtility.GetHandleSize(picks[i].point) * 0.06f, 3f);
                    Handles.Label(picks[i].point, (i + 1).ToString(), EditorStyles.whiteBoldLabel);
                    if (i > 0) Handles.DrawLine(picks[i - 1].point, picks[i].point, 2f);
                }
                if (picks.Count > 0 && hasHit) Handles.DrawDottedLine(picks[picks.Count - 1].point, hit.point, 4f);
                if (picks.Count == 3 && hasHit) Handles.DrawDottedLine(hit.point, picks[0].point, 4f);
                if (hasHit)
                {
                    Handles.DrawWireDisc(hit.point, hit.normal, HandleUtility.GetHandleSize(hit.point) * 0.05f, 2f);
                    Handles.Label(hit.point + hit.normal * HandleUtility.GetHandleSize(hit.point) * 0.2f, hit.part.label, EditorStyles.whiteMiniLabel);
                }
            }
            if (CurrentCloth != null && picks.Count == 0) ClothHandles(CurrentCloth, root);

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (e.control || e.command)
                {
                    var c = NearestCloth(set);
                    if (c != null) { CurrentCloth = c; picks.Clear(); }
                    e.Use();
                }
                else if (hasHit)
                {
                    picks.Add(hit);
                    if (picks.Count == 4)
                    {
                        if (picks.Any(p => p.part.transform != picks[0].part.transform))
                            Debug.Log($"[SMC-DETAILS 装備] 四隅が別の部位にまたがっています。{picks[0].part.label}に付けます（他の部位が動くと合わなくなります）");
                        CurrentCloth = SMDecorCloths.Create(set, picks, NewClothType);
                        picks.Clear();
                    }
                    e.Use();
                }
            }
            if ((e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) || (e.type == EventType.MouseDown && e.button == 1 && picks.Count > 0))
            {
                if (picks.Count > 0) { picks.Clear(); e.Use(); }
            }
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && CurrentCloth != null && picks.Count == 0)
            {
                RemoveCloth(CurrentCloth);
                e.Use();
            }
        }

        static void RemoveCloth(SMDecorCloth c) => SMDecorEditing.Remove(c);

        /// <summary>Corner handles: drag on the body (snapped with a ray from the camera); drape again when let go.</summary>
        static void ClothHandles(SMDecorCloth cloth, Transform root)
        {
            var m = cloth.part.localToWorldMatrix;
            if (Event.current.type == EventType.Repaint)
            {
                Handles.color = new Color(0.2f, 1f, 0.5f, 0.8f);
                for (int i = 0; i < cloth.corners.Count; i++)
                    Handles.DrawDottedLine(m.MultiplyPoint3x4(cloth.corners[i]), m.MultiplyPoint3x4(cloth.corners[(i + 1) % cloth.corners.Count]), 4f);
            }
            for (int i = 0; i < cloth.corners.Count; i++)
            {
                var p = m.MultiplyPoint3x4(cloth.corners[i]);
                Handles.color = new Color(0.25f, 1f, 0.45f);
                EditorGUI.BeginChangeCheck();
                var q = Handles.FreeMoveHandle(p, HandleUtility.GetHandleSize(p) * 0.07f, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    var cam = SceneView.currentDrawingSceneView != null ? SceneView.currentDrawingSceneView.camera : null;
                    if (cam != null && SMDecorPick.Raycast(root, new Ray(cam.transform.position, (q - cam.transform.position).normalized), out var h))
                    {
                        Undo.RecordObject(cloth, "布の四隅");
                        cloth.corners[i] = cloth.part.InverseTransformPoint(h.point);
                        cloth.cornerNormals[i] = cloth.part.InverseTransformDirection(h.normal).normalized;
                        cornerDragged = true;
                    }
                }
            }
            if (cornerDragged && GUIUtility.hotControl == 0)
            {
                cornerDragged = false;
                EditorApplication.delayCall += () => { if (cloth != null) SMDecorCloths.Rebuild(cloth); };
            }
        }

        static SMDecorCloth NearestCloth(SMDecorSet set)
        {
            SMDecorCloth best = null; float bestPx = 40f;
            foreach (var c in set.items.OfType<SMDecorCloth>())
            {
                if (c == null || c.part == null) continue;
                var m = c.part.localToWorldMatrix;
                for (int i = 0; i < c.corners.Count; i++)
                {
                    float px = HandleUtility.DistanceToLine(m.MultiplyPoint3x4(c.corners[i]), m.MultiplyPoint3x4(c.corners[(i + 1) % c.corners.Count]));
                    if (px < bestPx) { bestPx = px; best = c; }
                }
                var r = c.GetComponent<Renderer>();
                if (r != null && HandleUtility.DistanceToCircle(r.bounds.center, r.bounds.extents.magnitude * 0.5f) < 1f && best == null) best = c;
            }
            return best;
        }

        // ------------------------------------------------------------------ cutting

        public static int CutTool;               // 0 brush, 1 polygon
        public static float BrushRadius = 0.15f;
        public static bool RedrapeAfterCut = true;
        static readonly List<(Vector3 world, Vector2 sheet)> poly = new List<(Vector3, Vector2)>();
        static bool stroke;
        bool cutHit;
        Vector3 cutWorld;
        Vector2 cutSheet;

        void CutMode(SMDecorSet set, SceneView view, Event e)
        {
            var cloth = CurrentCloth;
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && (e.control || e.command))
            {
                var c = NearestCloth(set);
                if (c != null) { CurrentCloth = c; poly.Clear(); }
                e.Use();
                return;
            }
            if (cloth == null) return;
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag)
            {
                cutHit = SMDecorCut.RaycastSheet(cloth, HandleUtility.GUIPointToWorldRay(e.mousePosition), out cutWorld, out cutSheet);
                view.Repaint();
            }
            if (e.type == EventType.Repaint)
            {
                if (cutHit && CutTool == 0)
                {
                    Handles.color = e.shift ? new Color(0.3f, 1f, 0.4f) : new Color(1f, 0.3f, 0.2f);
                    var n = SceneView.currentDrawingSceneView != null ? -SceneView.currentDrawingSceneView.camera.transform.forward : Vector3.up;
                    Handles.DrawWireDisc(cutWorld, n, BrushRadius, 2f);
                }
                if (CutTool == 1)
                {
                    Handles.color = new Color(1f, 0.35f, 0.2f);
                    for (int i = 0; i < poly.Count; i++)
                    {
                        Handles.DrawWireDisc(poly[i].world, Vector3.up, HandleUtility.GetHandleSize(poly[i].world) * 0.03f, 2f);
                        if (i > 0) Handles.DrawLine(poly[i - 1].world, poly[i].world, 2f);
                    }
                    if (poly.Count > 0 && cutHit) Handles.DrawDottedLine(poly[poly.Count - 1].world, cutWorld, 4f);
                    if (poly.Count > 2) Handles.DrawDottedLine(poly[poly.Count - 1].world, poly[0].world, 4f);
                }
            }
            if (CutTool == 0)
            {
                // brush: cut (or restore with Shift) while dragging, re-mesh at once, drape again when let go
                if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0 && !e.alt && cutHit)
                {
                    if (e.type == EventType.MouseDown) Undo.RecordObject(cloth, "切り抜き");
                    stroke = true;
                    if (SMDecorCut.Circle(cloth, cutSheet, BrushRadius, !e.shift)) SMDecorCloths.Rebuild(cloth, false);
                    e.Use();
                }
                if (e.type == EventType.MouseUp && e.button == 0 && stroke)
                {
                    stroke = false;
                    EditorUtility.SetDirty(cloth);
                    if (RedrapeAfterCut) SMDecorCloths.Rebuild(cloth, true);
                    e.Use();
                }
            }
            else
            {
                if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && cutHit) { poly.Add((cutWorld, cutSheet)); e.Use(); }
                if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && poly.Count >= 3)
                {
                    Undo.RecordObject(cloth, "切り抜き");
                    var pts = new List<Vector2>(); foreach (var p in poly) pts.Add(p.sheet);
                    if (SMDecorCut.Polygon(cloth, pts, !e.shift)) SMDecorCloths.Rebuild(cloth, RedrapeAfterCut);
                    EditorUtility.SetDirty(cloth);
                    poly.Clear();
                    e.Use();
                }
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && poly.Count > 0) { poly.Clear(); e.Use(); }
            }
        }

        static void CutPanel(SMDecorSet set)
        {
            if (CurrentCloth == null) { GUILayout.Label("切る布・ネットを Ctrl+クリックで選んでください", EditorStyles.miniLabel); return; }
            var c = CurrentCloth;
            GUILayout.Label($"編集中: {c.name}　切った格子 {SMDecorCut.CutCells(c)}", EditorStyles.boldLabel);
            CutTool = GUILayout.Toolbar(CutTool, new[] { "ブラシ", "多角形" });
            float wl = EditorGUIUtility.labelWidth; EditorGUIUtility.labelWidth = 110;
            if (CutTool == 0) BrushRadius = EditorGUILayout.Slider("ブラシの半径 (cm)", BrushRadius * 100f, 2f, 80f) / 100f;
            RedrapeAfterCut = EditorGUILayout.Toggle(new GUIContent("切ったら掛け直す", "切り口が垂れ下がる（少し時間がかかる）"), RedrapeAfterCut);
            EditorGUI.BeginChangeCheck();
            float fray = EditorGUILayout.Slider("切り口のほつれ", c.fray, 0f, 1f);
            bool border = EditorGUILayout.Toggle("外周もほつれさせる", c.frayBorder);
            EditorGUIUtility.labelWidth = wl;
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(c, "ほつれ");
                c.fray = fray; c.frayBorder = border;
                SMDecorCloths.Rebuild(c, false);
            }
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button("掛け直す")) SMDecorCloths.Rebuild(c, true);
                if (GUILayout.Button("切り抜きを全部戻す")) { SMDecorCut.Clear(c); SMDecorCloths.Rebuild(c, RedrapeAfterCut); }
            }
        }

        // ------------------------------------------------------------------ chains

        void ChainMode(SMDecorSet set, SceneView view, Event e)
        {
            var root = set.transform;
            var list = Ridges(set);
            if (e.type == EventType.Repaint)
            {
                var segs = new Vector3[list.Count * 2];
                for (int i = 0; i < list.Count; i++) { segs[i * 2] = root.TransformPoint(list[i].a); segs[i * 2 + 1] = root.TransformPoint(list[i].b); }
                Handles.color = new Color(0.3f, 0.85f, 1f, 0.35f);
                Handles.DrawLines(segs);
                if (hover != null)
                {
                    Handles.color = new Color(1f, 0.8f, 0.1f);
                    Handles.DrawLine(root.TransformPoint(hover.a), root.TransformPoint(hover.b), 4f);
                    var mid = root.TransformPoint((hover.a + hover.b) * 0.5f);
                    Handles.Label(mid, $"{SMDecorParts.Label(root, hover.part)}　{hover.Length:F2} m{(hover.rim ? "（板の縁）" : "")}", EditorStyles.whiteBoldLabel);
                }
            }
            if (Current != null) CurrentHandles(Current);

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                if (e.control || e.command)
                {
                    var c = NearestChain(set);
                    if (c != null) Current = c;
                    e.Use();
                }
                else if (hover != null)
                {
                    var near = hasHit ? root.InverseTransformPoint(hit.point) : hover.a;
                    if (e.shift && Current != null)
                    {
                        if (!SMDecorChains.Append(Current, hover)) Debug.Log("[SMC-DETAILS 装備] つなげる稜線は、チェーンの端に接していません");
                    }
                    else Current = SMDecorChains.Create(set, hover, near);
                    e.Use();
                }
            }
            if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && Current != null)
            {
                Remove(Current);
                e.Use();
            }
        }

        static void Remove(SMDecorChain chain) => SMDecorEditing.Remove(chain);

        /// <summary>Path and trim handles of the chain being edited.</summary>
        static void CurrentHandles(SMDecorChain chain)
        {
            var m = chain.part.localToWorldMatrix;
            if (Event.current.type == EventType.Repaint)
            {
                Handles.color = new Color(0.2f, 1f, 0.5f, 0.8f);
                for (int i = 1; i < chain.path.Count; i++) Handles.DrawDottedLine(m.MultiplyPoint3x4(chain.path[i - 1]), m.MultiplyPoint3x4(chain.path[i]), 4f);
            }
            float total = chain.PathLength;
            for (int end = 0; end < 2; end++)
            {
                float s = end == 0 ? chain.trimStart : total - chain.trimEnd;
                var p = m.MultiplyPoint3x4(chain.PointAt(s));
                float size = HandleUtility.GetHandleSize(p) * 0.07f;
                Handles.color = end == 0 ? new Color(0.2f, 1f, 0.4f) : new Color(1f, 0.35f, 0.3f);
                EditorGUI.BeginChangeCheck();
                var q = Handles.FreeMoveHandle(p, size, Vector3.zero, Handles.SphereHandleCap);
                if (EditorGUI.EndChangeCheck())
                {
                    float ns = ClosestAlong(chain, chain.part.InverseTransformPoint(q));
                    Undo.RecordObject(chain, "チェーンの長さ");
                    if (end == 0) chain.trimStart = Mathf.Clamp(ns, 0f, total - chain.trimEnd - 0.05f);
                    else chain.trimEnd = Mathf.Clamp(total - ns, 0f, total - chain.trimStart - 0.05f);
                    SMDecorChains.Rebuild(chain);
                }
            }
        }

        static float ClosestAlong(SMDecorChain chain, Vector3 local)
        {
            float best = float.MaxValue, bestS = 0f, acc = 0f;
            for (int i = 1; i < chain.path.Count; i++)
            {
                var a = chain.path[i - 1]; var b = chain.path[i];
                var ab = b - a; float len = ab.magnitude;
                float t = Mathf.Clamp01(Vector3.Dot(local - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
                float d = Vector3.Distance(local, a + ab * t);
                if (d < best) { best = d; bestS = acc + t * len; }
                acc += len;
            }
            return bestS;
        }

        /// <summary>The ridge nearest to the mouse on screen (within 12 px) that is not hidden behind the vehicle.</summary>
        static SMDecorRidge NearestRidge(SMDecorSet set, Ray ray)
        {
            var root = set.transform;
            var cands = new List<(float px, SMDecorRidge r)>();
            foreach (var r in Ridges(set))
            {
                float px = HandleUtility.DistanceToLine(root.TransformPoint(r.a), root.TransformPoint(r.b));
                if (px < 12f) cands.Add((px, r));
            }
            foreach (var (px, r) in cands.OrderBy(c => c.px).Take(6))
            {
                // the point of the ridge nearest to the mouse ray, then: is something in front of it?
                Vector3 a = root.TransformPoint(r.a), b = root.TransformPoint(r.b);
                var p = ClosestOnSegmentToRay(a, b, ray);
                var toP = p - ray.origin;
                if (SMDecorPick.Raycast(root, new Ray(ray.origin, toP.normalized), out var h) && h.distance < toP.magnitude - 0.03f) continue;
                return r;
            }
            return null;
        }

        static Vector3 ClosestOnSegmentToRay(Vector3 a, Vector3 b, Ray ray)
        {
            var u = b - a; var v = ray.direction; var w0 = a - ray.origin;
            float A = Vector3.Dot(u, u), B = Vector3.Dot(u, v), C = Vector3.Dot(v, v), D = Vector3.Dot(u, w0), E = Vector3.Dot(v, w0);
            float den = A * C - B * B;
            float s = den > 1e-9f ? Mathf.Clamp01((B * E - C * D) / den) : 0f;
            return a + u * s;
        }

        static SMDecorChain NearestChain(SMDecorSet set)
        {
            SMDecorChain best = null; float bestPx = 15f;
            foreach (var c in set.items.OfType<SMDecorChain>())
            {
                if (c == null || c.part == null) continue;
                var m = c.part.localToWorldMatrix;
                for (int i = 1; i < c.path.Count; i++)
                {
                    float px = HandleUtility.DistanceToLine(m.MultiplyPoint3x4(c.path[i - 1]), m.MultiplyPoint3x4(c.path[i]));
                    if (px < bestPx) { bestPx = px; best = c; }
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ other modes (D0 info)

        void PointInfo(SMDecorSet set, Event e)
        {
            var root = set.transform;
            if (hasHit && e.type == EventType.Repaint)
            {
                Handles.color = new Color(1f, 0.75f, 0.1f);
                Handles.DrawWireDisc(hit.point, hit.normal, HandleUtility.GetHandleSize(hit.point) * 0.08f, 2f);
                Handles.DrawLine(hit.point, hit.point + hit.normal * HandleUtility.GetHandleSize(hit.point) * 0.3f, 2f);
                string info = hit.part.label;
                if (set.HasField)
                {
                    var local = root.InverseTransformPoint(hit.point + hit.normal * 0.05f);
                    info += $"\n5 cm 上の距離 {set.Distance(local) * 100f:F1} cm";
                    if (set.PoseChanged(hit.part.transform)) info += "\n（この部位は距離場を作った後に動いています）";
                }
                Handles.Label(hit.point + hit.normal * HandleUtility.GetHandleSize(hit.point) * 0.35f, info, EditorStyles.whiteBoldLabel);
            }
            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && hasHit) e.Use();
        }

        // ------------------------------------------------------------------ panel

        static void Panel(SMDecorSet set)
        {
            Handles.BeginGUI();
            float h = 74 + (!set.HasField ? 40 : 0) + (Mode == 0 && Current != null ? 300 : 0)
                      + (Mode == 1 ? 26 + (CurrentCloth != null ? 270 : 0) : 0)
                      + (Mode == 2 ? (CurrentCloth != null ? 180 : 22) : 0);
            GUILayout.BeginArea(new Rect(10, 10, 400, h), EditorStyles.helpBox);
            Mode = GUILayout.Toolbar(Mode, Modes);
            GUILayout.Label(Hints[Mode], EditorStyles.wordWrappedMiniLabel);
            if (!set.HasField) EditorGUILayout.HelpBox("先に SMC-DETAILS ウィンドウの「装備」で距離場を作ってください", MessageType.Warning);
            else if (Mode == 0) GUILayout.Label("稜線: " + ridgeInfo, EditorStyles.miniLabel);
            if (Mode == 2 && set.HasField) CutPanel(set);
            if (Mode == 1 && set.HasField)
            {
                NewClothType = (SMDecorClothType)GUILayout.Toolbar((int)NewClothType, new[] { "カモネット", "布", "防水布" });
                if (CurrentCloth != null)
                {
                    EditorGUILayout.Space(2);
                    GUILayout.Label($"編集中: {CurrentCloth.name}（{SMDecorParts.Label(set.transform, CurrentCloth.part)}）", EditorStyles.boldLabel);
                    GUILayout.Label(SMDecorClothEditor.Info(CurrentCloth), EditorStyles.miniLabel);
                    SMDecorClothEditor.DrawSettings(CurrentCloth, true);
                    using (new GUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("掛け直す")) SMDecorCloths.Rebuild(CurrentCloth);
                        if (GUILayout.Button("外す")) RemoveCloth(CurrentCloth);
                        if (GUILayout.Button("編集を終える")) CurrentCloth = null;
                    }
                }
            }
            if (Mode == 0 && Current != null)
            {
                EditorGUILayout.Space(2);
                GUILayout.Label($"編集中: {Current.name}（{SMDecorParts.Label(set.transform, Current.part)}）　長さ {Current.length:F2} m、リンク {Current.links}", EditorStyles.boldLabel);
                SMDecorChainEditor.DrawSettings(Current, true);
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("外す")) Remove(Current);
                    if (GUILayout.Button("編集を終える")) Current = null;
                }
            }
            GUILayout.EndArea();
            Handles.EndGUI();
        }
    }
}
