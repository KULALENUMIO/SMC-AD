// SPDX-License-Identifier: MIT
// SMC-DETAILS for Unity: 3D brush. Paint lives in two grids (half the shape grid's resolution) in root rest space:
// Paint0 = chip, scratch, rust, grime; Paint1 = mud, soil, sand, wet; 0.5 = no change. While painting they are
// RenderTextures written by a compute shader (the materials read them directly); "保存" writes Texture3D assets.
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace SMCAD.Details.Editor
{
    [InitializeOnLoad]
    public static class SMDetailsPaint
    {
        // painting never survives a script reload or a play-mode change (the RenderTextures and colliders would leak)
        static SMDetailsPaint()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => End(true);
            EditorApplication.playModeStateChanged += _ => End(true);
            EditorApplication.quitting += () => End(false);
        }

        public static SMDetails Target { get; private set; }
        public static int Channel;              // 0..7
        public static float Radius = 0.15f;
        public static float Strength = 0.35f;
        public static int Mode;                 // 0 add, 1 remove, 2 reset
        public static bool Dirty;

        static RenderTexture rt0, rt1;
        static ComputeShader cs;
        static readonly List<(MeshCollider col, SMDetails.Slot slot, int[] tris, Vector3[] rest)> colliders = new List<(MeshCollider, SMDetails.Slot, int[], Vector3[])>();
        static Vector3 lastHit, lastNormal;
        static bool hasHit;

        static bool running;                    // separate from Target: the target can be destroyed while painting
        public static bool Active => running && Target != null;

        public static Vector3Int Dims(SMDetails d) => new Vector3Int(
            Mathf.RoundToInt(d.gridSize.x / d.voxel) / 2, Mathf.RoundToInt(d.gridSize.y / d.voxel) / 2, Mathf.RoundToInt(d.gridSize.z / d.voxel) / 2);

        public static void Begin(SMDetails d)
        {
            if (running && Target == d) return;
            End(true);
            cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(SMDetailsBuilder.PackageRoot + "/Editor/Shaders/SMDetails.compute");
            var dims = Dims(d);
            rt0 = Make(dims, d.paint0);
            rt1 = Make(dims, d.paint1);
            d.livePaint0 = rt0;
            d.livePaint1 = rt1;
            d.Apply();
            foreach (var s in d.slots)
            {
                if (s.renderer == null || s.stream == null) continue;
                var mesh = s.renderer.GetComponent<MeshFilter>().sharedMesh;
                var col = s.renderer.gameObject.AddComponent<MeshCollider>();
                col.hideFlags = HideFlags.HideAndDontSave;
                col.sharedMesh = mesh;
                var rest = new List<Vector3>();
                s.stream.GetUVs(5, rest);
                colliders.Add((col, s, mesh.triangles, rest.ToArray()));
            }
            Target = d;
            running = true;
            Dirty = false;
            SceneView.duringSceneGui += OnScene;
            SceneView.RepaintAll();
        }

        /// <summary>Stops painting; saves the grids into Texture3D assets when save is true.</summary>
        public static void End(bool save)
        {
            if (!running) return;
            running = false;
            SceneView.duringSceneGui -= OnScene;
            var d = Target;   // may be destroyed (then only the GPU / collider resources are released)
            if (d != null && save && Dirty && rt0 != null && rt1 != null)
            {
                string folder = d.assetFolder;
                if (string.IsNullOrEmpty(folder) && d.shape != null)
                {
                    folder = AssetDatabase.GetAssetPath(d.shape);
                    folder = folder.Substring(0, folder.LastIndexOf('/'));
                }
                if (!string.IsNullOrEmpty(folder))
                {
                    Undo.RecordObject(d, "SMC-DETAILS 手描き");
                    var p0 = Store(rt0, folder + "/Paint0.asset", d.name + "_Paint0");
                    var p1 = Store(rt1, folder + "/Paint1.asset", d.name + "_Paint1");
                    if (p0 != null && p1 != null)
                    {
                        d.paint0 = p0;
                        d.paint1 = p1;
                        EditorUtility.SetDirty(d);
                        EditorSceneManager.MarkSceneDirty(d.gameObject.scene);
                        AssetDatabase.SaveAssets();
                    }
                }
            }
            foreach (var c in colliders) if (c.col != null) Object.DestroyImmediate(c.col);
            colliders.Clear();
            if (d != null)
            {
                d.livePaint0 = d.livePaint1 = null;
                d.Apply();
            }
            if (rt0 != null) { rt0.Release(); Object.DestroyImmediate(rt0); }
            if (rt1 != null) { rt1.Release(); Object.DestroyImmediate(rt1); }
            rt0 = rt1 = null;
            Target = null;
            SceneView.RepaintAll();
        }

        public static void ClearChannelAll()
        {
            if (!Active) return;
            var dims = Dims(Target);
            var mask = Vector4.zero;
            mask[Channel % 4] = 1f;
            int k = cs.FindKernel("ResetChannel");
            cs.SetTexture(k, "_Paint", Channel < 4 ? rt0 : rt1);
            cs.SetInts("_PaintDims", dims.x, dims.y, dims.z);
            cs.SetVector("_ChannelMask", mask);
            cs.Dispatch(k, (dims.x + 3) / 4, (dims.y + 3) / 4, (dims.z + 3) / 4);
            Dirty = true;
            SceneView.RepaintAll();
        }

        static RenderTexture Make(Vector3Int dims, Texture3D from)
        {
            var rt = new RenderTexture(dims.x, dims.y, 0, RenderTextureFormat.ARGBHalf)
            {
                dimension = TextureDimension.Tex3D, volumeDepth = dims.z, enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            bool load = from != null && from.width == dims.x && from.height == dims.y && from.depth == dims.z;
            int k = cs.FindKernel(load ? "LoadPaint" : "ClearPaint");
            cs.SetTexture(k, "_Paint", rt);
            if (load) cs.SetTexture(k, "_Source", from);
            cs.SetInts("_PaintDims", dims.x, dims.y, dims.z);
            cs.Dispatch(k, (dims.x + 3) / 4, (dims.y + 3) / 4, (dims.z + 3) / 4);
            return rt;
        }

        static Texture3D Store(RenderTexture rt, string path, string name)
        {
            var req = AsyncGPUReadback.Request(rt, 0, 0, rt.width, 0, rt.height, 0, rt.volumeDepth, TextureFormat.RGBA32);
            req.WaitForCompletion();
            if (req.hasError)
            {
                Debug.LogError("[SMC-DETAILS] 手描きの読み出しに失敗したので保存しませんでした: " + path);
                return null;
            }
            var tex = new Texture3D(rt.width, rt.height, rt.volumeDepth, TextureFormat.RGBA32, false)
            {
                name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            var bytes = new byte[rt.width * rt.height * rt.volumeDepth * 4];
            int layer = rt.width * rt.height * 4;
            for (int z = 0; z < rt.volumeDepth; z++)
                Unity.Collections.NativeArray<byte>.Copy(req.GetData<byte>(z), 0, bytes, z * layer, layer);
            tex.SetPixelData(bytes, 0);
            tex.Apply(false, false);
            return SMDetailsBuilder.SaveInPlace(tex, path);   // in place: undo keeps pointing at a live asset
        }

        // ------------------------------------------------------------------ scene view

        static void OnScene(SceneView view)
        {
            var d = Target;
            if (d == null) { End(false); return; }
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            if (e.type == EventType.Layout) HandleUtility.AddDefaultControl(id);

            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag || e.type == EventType.MouseDown)
            {
                hasHit = Raycast(HandleUtility.GUIPointToWorldRay(e.mousePosition), out var rest, out lastHit, out lastNormal);
                if (hasHit && e.button == 0 && !e.alt && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag))
                {
                    bool remove = Mode == 1 || e.shift;
                    StampAt(rest, Radius, remove ? -Strength : Strength, Mode == 2);
                    e.Use();
                }
                view.Repaint();
            }
            if (e.type == EventType.MouseUp && e.button == 0) GUIUtility.hotControl = 0;
            if (hasHit && e.type == EventType.Repaint)
            {
                Handles.color = Mode == 0 && !e.shift ? new Color(1f, 0.6f, 0.2f, 0.9f) : Mode == 2 ? Color.gray : new Color(0.3f, 0.7f, 1f, 0.9f);
                Handles.DrawWireDisc(lastHit, lastNormal, Radius * d.transform.lossyScale.x);
                Handles.DrawWireDisc(lastHit, lastNormal, Radius * 0.5f * d.transform.lossyScale.x);
            }
            Handles.BeginGUI();
            GUI.Label(new Rect(10, 10, 520, 22), $"SMC-DETAILS 手描き: {SMDetailsSettings.Labels[Channel]}（{(Mode == 0 ? "足す" : Mode == 1 ? "消す" : "元に戻す")}）  Shift で消す / Alt で視点", EditorStyles.helpBox);
            Handles.EndGUI();
        }

        /// <summary>Nearest hit on the weathered meshes; rest = the hit point in root rest space.</summary>
        static bool Raycast(Ray ray, out Vector3 rest, out Vector3 point, out Vector3 normal)
        {
            rest = point = normal = Vector3.zero;
            float best = float.MaxValue;
            bool found = false;
            foreach (var c in colliders)
            {
                if (c.col == null || !c.col.Raycast(ray, out var hit, 10000f) || hit.distance >= best) continue;
                int t = hit.triangleIndex * 3;
                if (t + 2 >= c.tris.Length) continue;
                var b = hit.barycentricCoordinate;
                rest = c.rest[c.tris[t]] * b.x + c.rest[c.tris[t + 1]] * b.y + c.rest[c.tris[t + 2]] * b.z;
                point = hit.point; normal = hit.normal; best = hit.distance; found = true;
            }
            return found;
        }

        static void StampAt(Vector3 rest, float radius, float amount, bool reset)
        {
            var d = Target;
            var dims = Dims(d);
            float pv = d.voxel * 2f;
            var rt = Channel < 4 ? rt0 : rt1;
            var mask = Vector4.zero;
            mask[Channel % 4] = 1f;
            int k = cs.FindKernel("Stamp");
            Vector3 rel = (rest - d.gridMin) / pv;
            int r = Mathf.CeilToInt(radius / pv) + 1;
            var off = new Vector3Int(Mathf.FloorToInt(rel.x) - r, Mathf.FloorToInt(rel.y) - r, Mathf.FloorToInt(rel.z) - r);
            int n = 2 * r + 2;
            cs.SetTexture(k, "_Paint", rt);
            cs.SetInts("_PaintDims", dims.x, dims.y, dims.z);
            cs.SetVector("_PaintMin", d.gridMin);
            cs.SetFloat("_PaintVoxel", pv);
            cs.SetInts("_Offset", off.x, off.y, off.z);
            cs.SetVector("_BrushPos", rest);
            cs.SetFloat("_BrushRadius", radius);
            cs.SetFloat("_BrushAmount", amount);
            cs.SetFloat("_BrushReset", reset ? 1f : 0f);
            cs.SetVector("_ChannelMask", mask);
            if (n >= Mathf.Max(dims.x, Mathf.Max(dims.y, dims.z)))
            {
                cs.SetInts("_Offset", 0, 0, 0);   // brush bigger than the grid: everything
                cs.Dispatch(k, (dims.x + 3) / 4, (dims.y + 3) / 4, (dims.z + 3) / 4);
            }
            else
            {
                cs.Dispatch(k, (n + 3) / 4, (n + 3) / 4, (n + 3) / 4);
            }
            Dirty = true;
        }

        /// <summary>For tests: one stamp at a rest-space point.</summary>
        public static void StampForTest(Vector3 rest, float radius, float amount) { if (Active) StampAt(rest, radius, amount, false); }
    }
}
