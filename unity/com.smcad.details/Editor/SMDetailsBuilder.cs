// SPDX-License-Identifier: MIT
// SMC-DETAILS for Unity: set-up and GPU analysis.
//   1. rest-pose position / normal of every vertex (UV5 / UV6) and the distance to real part edges (UV1 / UV7,
//      SMDetailsEdges) in a copy of each mesh, swapped in on the renderer (the original comes back on removal)
//   2. shape grid: the blueprint is drawn as depth from N directions (orthographic, GPU); for each grid point the
//      directions from which it is in front of the nearest surface are counted (compute shader) -> Texture3D
//   3. a tiling 3D noise (CPU, once per project) for patterns and crisp borders
//   4. weathered copies of the SM Lit materials on the renderers (the imported ones stay untouched)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace SMCAD.Details.Editor
{
    public static class SMDetailsBuilder
    {
        public const string PackageRoot = "Packages/com.smcad.details";
        public const string OutputRoot = "Assets/SMCAD_Details";
        public const string LitShader = "SMC-AD/SM Lit";
        public static string LastReport = "";

        // ------------------------------------------------------------------ target

        public static Transform FindRoot(GameObject go)
        {
            if (go == null) return null;
            var d = go.GetComponentInParent<SMDetails>(true);
            if (d != null) return d.transform;
            foreach (var other in UnityEngine.Object.FindObjectsByType<SMDetails>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (other.slots.Any(sl => sl.renderer != null && (sl.renderer.transform == go.transform || sl.renderer.transform.IsChildOf(go.transform) || go.transform.IsChildOf(sl.renderer.transform))))
                    return other.transform;
                if (other.trackDirt.Any(td => td != null && go.transform.IsChildOf(td.transform)))
                    return other.transform;
            }
            Transform best = null;
            for (var t = go.transform; t != null; t = t.parent)
                if (t.GetComponents<Component>().Any(c => c != null && c.GetType().Name == "SMBlueprint")) best = t;
            if (best != null) return best;
            // no SMBlueprint: the highest parent that still has SM Lit renderers
            for (var t = go.transform; t != null; t = t.parent)
                if (SMRenderers(t).Count > 0) best = t;
            return best;
        }

        public static List<MeshRenderer> SMRenderers(Transform root)
        {
            return root.GetComponentsInChildren<MeshRenderer>(true)
                .Where(r => r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null &&
                            r.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name == LitShader))
                .ToList();
        }

        public const string HdrpLit = "HDRP/Lit";
        public const string DetailsLit = "SMC-AD/Details Lit";

        /// <summary>Parts that come with the blueprint but are not SM parts: siblings named "Track_*" or carrying a
        /// component with a "linkPrefab" field (Tank Rigging plugin). Their own meshes are weathered like the body;
        /// the generated track links (hidden, rebuilt by the track) are handled through the link prefab.</summary>
        public static List<Transform> AttachedRoots(Transform root)
        {
            var list = new List<Transform>();
            var parent = OwnRig(root);
            if (parent == root) return list;
            foreach (Transform c in parent)
                if (c != root && (c.name.StartsWith("Track_") || TrackComponents(c).Any())) list.Add(c);
            return list;
        }

        /// <summary>The rig the blueprint belongs to: its parent, unless the parent also holds other vehicles
        /// (then only the blueprint itself, so other vehicles' tracks / muzzles are never picked up).</summary>
        static Transform OwnRig(Transform root)
        {
            var parent = root.parent;
            if (parent == null) return root;
            foreach (Transform c in parent)
            {
                if (c == root || c.name.StartsWith("Track_") || TrackComponents(c).Any()) continue;
                bool vehicle = c.GetComponentsInChildren<Component>(true).Any(x => x != null && (x.GetType().Name == "SMBlueprint" || x is SMDetails))
                               || SMRenderers(c).Count > 0;
                if (vehicle) return root;
            }
            return parent;
        }

        /// <summary>Whether a renderer / track of a component's slots belongs to this blueprint: under it, under one of its
        /// attached parts, or outside but listed by no other SMDetails (a duplicated body still lists the first vehicle's
        /// tracks; those are never touched).</summary>
        static bool Owns(SMDetails comp, Transform root, Component c)
        {
            if (c == null) return false;
            var t = c.transform;
            if (t.IsChildOf(root) || AttachedRoots(root).Any(a => t.IsChildOf(a))) return true;
            return !UnityEngine.Object.FindObjectsByType<SMDetails>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Any(o => o != comp && (o.slots.Any(x => x.renderer == c) || o.trackDirt.Contains(c as SMDetailsTrackDirt)));
        }

        static IEnumerable<Component> TrackComponents(Transform t) =>
            t.GetComponents<Component>().Where(c => c != null && c.GetType().GetField("linkPrefab") != null);

        public static List<MeshRenderer> AttachedRenderers(Transform root)
        {
            return AttachedRoots(root).SelectMany(a => a.GetComponentsInChildren<MeshRenderer>(true))
                .Where(r => (r.gameObject.hideFlags & HideFlags.DontSave) == 0 && !r.transform.GetComponentsInParent<Transform>(true).Any(p => p.name.StartsWith("__"))
                            && r.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null
                            && r.sharedMaterials.Any(m => m != null && m.shader != null && (m.shader.name == HdrpLit || m.shader.name == DetailsLit)))
                .ToList();
        }

        /// <summary>The weathered copy of an SM Lit / HDRP Lit material. Its file name is fixed per original material
        /// (name + a short hash of where it comes from), so set-up again — also after "汚しを外す" — reuses the same
        /// file instead of leaving "… 1.mat" copies behind.</summary>
        static Material MakeCopy(Material o, string folder, bool belt)
        {
            string key = AssetDatabase.GetAssetPath(o) + "|" + o.name + "|" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(o));
            uint hash = 2166136261;
            foreach (char ch in key) { hash ^= ch; hash *= 16777619; }
            string path = folder + "/" + Safe(o.name) + (belt ? "_SMD_Belt_" : "_SMD_") + (hash & 0xFFFF).ToString("x4") + ".mat";
            var c = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool create = c == null;
            if (o.shader.name == LitShader && !belt)
            {
                if (create) c = new Material(o);
                else { c.shader = o.shader; c.CopyPropertiesFromMaterial(o); }
            }
            else
            {
                // HDRP/Lit, and every track-link material (SM Lit has no belt mode): the small Details Lit shader
                if (create) c = new Material(SMCAD.Editor.SMMaterialSetup.FindShader(DetailsLit));
                else c.shader = SMCAD.Editor.SMMaterialSetup.FindShader(DetailsLit);
                CopyLitLook(o, c);
            }
            SMCAD.Editor.SMMaterialSetup.Validate(c);   // HDRP keywords / passes / queue (+ the SMC-DETAILS keyword)
            if (create) AssetDatabase.CreateAsset(c, path);
            else EditorUtility.SetDirty(c);
            return c;
        }

        /// <summary>Removes files this tool generated in the blueprint's folder that nothing uses any more
        /// (left over from earlier set-ups or versions). Only "*_SMD*" materials / prefabs and old mesh files.</summary>
        static int CleanUp(SMDetails comp, string folder)
        {
            var used = new HashSet<string>();
            void Use(UnityEngine.Object o) { if (o != null) used.Add(AssetDatabase.GetAssetPath(o)); }
            foreach (var m in comp.materials) Use(m);
            foreach (var m in comp.beltMaterials) Use(m);
            foreach (var s in comp.slots) Use(s.stream);
            Use(comp.shape); Use(comp.paint0); Use(comp.paint1);
            used.Add(folder + "/Originals.asset");
            int removed = 0;
            foreach (var guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (used.Contains(path) || AssetDatabase.IsValidFolder(path)) continue;
                string file = System.IO.Path.GetFileName(path);
                bool generated = (file.Contains("_SMD") && (file.EndsWith(".mat") || file.EndsWith(".prefab")))
                                 || file == "VertexStreams.asset" || file == "MudMeshes.asset" || file == "SMD_Mud.mat"
                                 || file == "Paint0.asset" || file == "Paint1.asset";
                if (!generated) continue;
                if (AssetDatabase.DeleteAsset(path)) removed++;
            }
            return removed;
        }

        static bool Weatherable(Material o) => o != null && o.shader != null && (o.shader.name == LitShader || o.shader.name == HdrpLit);

        /// <summary>Base colour, metal, smoothness, mask and normal map of an HDRP/Lit (or SM Lit / Details Lit) material onto
        /// a Details Lit material (same property names as HDRP/Lit).</summary>
        public static void CopyLitLook(Material o, Material c)
        {
            if (o.HasProperty("_DifMap") && !o.HasProperty("_BaseColorMap"))
            {
                c.SetTexture("_BaseColorMap", o.GetTexture("_DifMap"));
                c.SetColor("_BaseColor", Color.white);
            }
            foreach (var t in new[] { "_BaseColorMap", "_NormalMap", "_MaskMap" })
                if (o.HasProperty(t)) { c.SetTexture(t, o.GetTexture(t)); c.SetTextureScale(t, o.GetTextureScale(t)); c.SetTextureOffset(t, o.GetTextureOffset(t)); }
            if (o.HasProperty("_BaseColor")) c.SetColor("_BaseColor", o.GetColor("_BaseColor"));
            foreach (var f in new[] { "_Metallic", "_Smoothness", "_NormalScale", "_MetallicRemapMin", "_MetallicRemapMax",
                                      "_SmoothnessRemapMin", "_SmoothnessRemapMax", "_AORemapMin", "_AORemapMax" })
                if (o.HasProperty(f)) c.SetFloat(f, o.GetFloat(f));
            c.SetFloat("_HasMask", o.HasProperty("_MaskMap") && o.GetTexture("_MaskMap") != null ? 1f : 0f);
        }

        /// <summary>Track links: the tracks rebuild their links by themselves (and the Tank Rigging window may change
        /// the link prefab), so a SMDetailsTrackDirt on each track swaps the link materials for belt materials whenever
        /// links appear. Belt materials are made here for the current link design; other designs get copies of them.</summary>
        static void SwapLinks(SMDetails comp, Transform root, string folder, List<string> log)
        {
            // earlier versions swapped the link prefab: put it back
            foreach (var ls in comp.linkSwaps)
            {
                if (ls.track == null || ls.original == null) continue;
                var so0 = new SerializedObject(ls.track);
                var p0 = so0.FindProperty("linkPrefab");
                if (p0 != null && p0.objectReferenceValue == ls.weathered) { p0.objectReferenceValue = ls.original; so0.ApplyModifiedProperties(); }
            }
            comp.linkSwaps = new List<SMDetails.LinkSwap>();
            var belt = new List<Material>();
            var beltCopies = new Dictionary<Material, Material>();
            var dirt = new List<SMDetailsTrackDirt>();
            foreach (var track in AttachedRoots(root).SelectMany(TrackComponents))
            {
                var prefab = new SerializedObject(track).FindProperty("linkPrefab")?.objectReferenceValue as GameObject;
                var from = new List<Material>();
                var to = new List<Material>();
                if (prefab != null)
                    foreach (var o in prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Distinct())
                    {
                        if (!Weatherable(o)) continue;
                        if (!beltCopies.TryGetValue(o, out var c))
                            beltCopies[o] = c = MakeCopy(o, folder, true);
                        from.Add(o); to.Add(c);
                    }
                var td = track.GetComponent<SMDetailsTrackDirt>() ?? Undo.AddComponent<SMDetailsTrackDirt>(track.gameObject);
                Undo.RecordObject(td, "SMC-DETAILS");
                td.from = from.ToArray();
                td.to = to.ToArray();
                td.template = to.FirstOrDefault() ?? comp.beltMaterials.FirstOrDefault();
                EditorUtility.SetDirty(td);
                dirt.Add(td);
            }
            belt.AddRange(beltCopies.Values);
            if (belt.Count == 0) belt.AddRange(comp.beltMaterials.Where(m => m != null));
            comp.beltMaterials = belt;
            comp.trackDirt = dirt;
            foreach (var td in dirt) if (td.template == null) td.template = belt.FirstOrDefault();
            if (dirt.Count > 0) log.Add($"履帯 {dirt.Count} 本のリンクに汚れ（マテリアル {belt.Count}）");
        }

        /// <summary>The component's own folder for generated assets. A new one (name + id) when it has none yet or when
        /// another SMDetails uses the same folder (a duplicated vehicle, or two blueprints with the same name).</summary>
        static string Folder(SMDetails comp, Transform root)
        {
            string folder = comp.assetFolder;
            if (string.IsNullOrEmpty(folder) && comp.shape != null)
            {
                string shapePath = AssetDatabase.GetAssetPath(comp.shape);   // earlier versions: the folder of the shape grid
                if (!string.IsNullOrEmpty(shapePath)) folder = shapePath.Substring(0, shapePath.LastIndexOf('/'));
            }
            bool shared = !string.IsNullOrEmpty(folder) && UnityEngine.Object
                .FindObjectsByType<SMDetails>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Any(o => o != comp && (o.assetFolder == folder || (string.IsNullOrEmpty(o.assetFolder) && o.shape != null && AssetDatabase.GetAssetPath(o.shape).StartsWith(folder + "/"))));
            if (string.IsNullOrEmpty(folder) || shared)
                folder = OutputRoot + "/" + Safe(root.name) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Directory.CreateDirectory(folder);
            comp.assetFolder = folder;
            return folder;
        }

        /// <summary>Writes an asset to a path, updating an existing one in place (references and undo history stay
        /// valid). Returns the asset that now lives at the path.</summary>
        public static T SaveInPlace<T>(T obj, string path) where T : UnityEngine.Object
        {
            var old = AssetDatabase.LoadAssetAtPath<T>(path);
            if (old != null && old != obj)
            {
                string name = obj.name;
                EditorUtility.CopySerialized(obj, old);
                old.name = name;
                EditorUtility.SetDirty(old);
                UnityEngine.Object.DestroyImmediate(obj);
                return old;
            }
            if (old == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);   // another type
                AssetDatabase.CreateAsset(obj, path);
            }
            return obj;
        }

        /// <summary>Puts the meshes into one asset file, reusing sub-assets of the same name in place.</summary>
        static List<Mesh> SaveMeshes(List<Mesh> meshes, string path)
        {
            if (AssetDatabase.LoadMainAssetAtPath(path) == null) AssetDatabase.CreateAsset(new Mesh { name = "Meshes" }, path);
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            var existing = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().Where(m => m != main)
                .GroupBy(m => m.name).ToDictionary(g => g.Key, g => g.First());
            var result = new List<Mesh>();
            var keep = new HashSet<Mesh>();
            foreach (var m in meshes)
            {
                if (existing.TryGetValue(m.name, out var old) && !keep.Contains(old))
                {
                    old.Clear();
                    EditorUtility.CopySerialized(m, old);
                    EditorUtility.SetDirty(old);
                    UnityEngine.Object.DestroyImmediate(m);
                    keep.Add(old);
                    result.Add(old);
                }
                else
                {
                    AssetDatabase.AddObjectToAsset(m, path);
                    keep.Add(m);
                    result.Add(m);
                }
            }
            foreach (var old in existing.Values) if (!keep.Contains(old)) UnityEngine.Object.DestroyImmediate(old, true);
            return result;
        }

        /// <summary>For a renderer without a slot: its true original mesh and materials. If it shows weathered copies
        /// (component lost), they come from the Originals records; null when they cannot be recovered.</summary>
        static (Mesh mesh, Material[] materials)? Originals(MeshRenderer r)
        {
            var mf = r.GetComponent<MeshFilter>();
            bool weatheredMesh = mf.sharedMesh != null && AssetDatabase.GetAssetPath(mf.sharedMesh).StartsWith(OutputRoot + "/");
            bool weatheredMats = r.sharedMaterials.Any(m => m != null && AssetDatabase.GetAssetPath(m).StartsWith(OutputRoot + "/"));
            if (!weatheredMesh && !weatheredMats) return (mf.sharedMesh, r.sharedMaterials);
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(SMDetailsOriginals), new[] { OutputRoot }))
            {
                var rec = AssetDatabase.LoadAssetAtPath<SMDetailsOriginals>(AssetDatabase.GUIDToAssetPath(guid));
                var e = rec != null ? rec.entries.FirstOrDefault(x => x.soup == mf.sharedMesh && x.soup != null) : null;
                if (e != null && e.original != null) return (e.original, e.materials);
            }
            return null;
        }

        // ------------------------------------------------------------------ set-up

        /// <summary>Adds (or refreshes) the weathering on a blueprint. Returns the component.</summary>
        public static SMDetails Setup(Transform root, float voxel = 0.03f, int directions = 96)
        {
            var sw = Stopwatch.StartNew();
            var log = new List<string>();
            SMDetailsPaint.End(true);
            var comp = root.GetComponent<SMDetails>() ?? Undo.AddComponent<SMDetails>(root.gameObject);
            Undo.RecordObject(comp, "SMC-DETAILS");
            string folder = Folder(comp, root);

            // renderers: the ones already weathered + new SM Lit ones + attached parts (tracks)
            var renderers = comp.slots.Where(s => s.renderer != null && Owns(comp, root, s.renderer)).Select(s => (MeshRenderer)s.renderer).ToList();
            foreach (var r in SMRenderers(root)) if (!renderers.Contains(r)) renderers.Add(r);
            foreach (var r in AttachedRenderers(root)) if (!renderers.Contains(r)) renderers.Add(r);
            if (renderers.Count == 0) throw new InvalidOperationException("SM Lit のメッシュが見つかりません");
            // originals: from the slots, else from the renderers (or the Originals records when they show weathered copies)
            var origMesh = new Dictionary<MeshRenderer, Mesh>();
            var origMats = new Dictionary<MeshRenderer, Material[]>();
            foreach (var r in renderers)
            {
                var slot = comp.slots.FirstOrDefault(x => x.renderer == r);
                if (slot != null && slot.originalMesh != null) { origMesh[r] = slot.originalMesh; origMats[r] = slot.original; continue; }
                var o = Originals(r);
                if (o == null)
                    throw new InvalidOperationException($"「{r.name}」は汚し用のメッシュ・マテリアルのままですが、元に戻す情報が見つかりません。"
                                                        + "元のメッシュとマテリアルに戻してから、もう一度「汚しを付ける」を押してください");
                origMesh[r] = o.Value.mesh;
                origMats[r] = o.Value.materials;
            }

            // 1 rest pose. The rest frame = world axes at the root's position at analysis time: y is "up" for
            //    mud / sand / streaks even when the root is rotated (FBX from a Z-up Blender scene). It is written
            //    into the vertices, so moving or rotating the blueprint (or its joints) later does not matter.
            var toRest = Matrix4x4.Translate(-root.position);
            var bodies = new List<(Mesh mesh, Matrix4x4 m, Vector3[] pos)>();
            var restNormals = new List<Vector3[]>();
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            foreach (var r in renderers)
            {
                var mesh = origMesh[r];
                var m = toRest * r.transform.localToWorldMatrix;
                var nm = m.inverse.transpose;
                var v = mesh.vertices;
                var n = mesh.normals;
                var pos = new Vector3[v.Length];
                var nrm = new Vector3[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    pos[i] = m.MultiplyPoint3x4(v[i]);
                    nrm[i] = n.Length == v.Length ? nm.MultiplyVector(n[i]).normalized : Vector3.up;
                    lo = Vector3.Min(lo, pos[i]); hi = Vector3.Max(hi, pos[i]);
                }
                bodies.Add((mesh, m, pos));
                restNormals.Add(nrm);
            }
            log.Add($"頂点 {bodies.Sum(b => b.pos.Length):N0}（メッシュ {bodies.Count}、うち履帯など {renderers.Count(r => !r.transform.IsChildOf(root))}）: {sw.ElapsedMilliseconds} ms");

            // 2 grid + GPU visibility
            Vector3 size = hi - lo;
            float vx = voxel;
            const int MaxDim = 320;
            while (Mathf.CeilToInt(size.x / vx) + 6 > MaxDim || Mathf.CeilToInt(size.y / vx) + 6 > MaxDim || Mathf.CeilToInt(size.z / vx) + 6 > MaxDim) vx *= 1.1f;
            Vector3Int dims = new Vector3Int(Even(size.x / vx + 6), Even(size.y / vx + 6), Even(size.z / vx + 6));
            Vector3 gmin = (lo + hi) * 0.5f - (Vector3)dims * (vx * 0.5f);
            long t0 = sw.ElapsedMilliseconds;
            var shape = AnalyzeShape(bodies, gmin, dims, vx, directions);
            shape.name = root.name + "_Shape";
            log.Add($"形の解析（GPU、{directions} 方向、格子 {dims.x}×{dims.y}×{dims.z}、{vx * 100f:F1} cm）: {sw.ElapsedMilliseconds - t0} ms");

            // 3 part edges: every triangle edge is tested with short rays (convex / concave / flat continuation)
            t0 = sw.ElapsedMilliseconds;
            var soups = SMDetailsEdges.Build(bodies, restNormals, shape, gmin, (Vector3)dims * vx, out string edgeInfo);
            var names = new HashSet<string>();   // unique: the mesh sub-assets are matched by name on the next set-up
            for (int i = 0; i < soups.Count; i++)
            {
                string n = renderers[i].name + "_SMD";
                for (int k = 2; !names.Add(n); k++) n = renderers[i].name + "_SMD_" + k;
                soups[i].name = n;
            }
            log.Add($"角の判定（{edgeInfo}）: {sw.ElapsedMilliseconds - t0} ms");
            // written in place: undo of an earlier set-up keeps pointing at live assets
            shape = SaveInPlace(shape, folder + "/Shape.asset");
            soups = SaveMeshes(soups, folder + "/Meshes.asset");

            // 4 noise (shared)
            var noise = EnsureNoise();

            // 5 materials + meshes on the renderers (keep earlier copies)
            var copies = new Dictionary<Material, Material>();   // copies live in this component's folder (fixed names)
            var newSlots = new List<SMDetails.Slot>();
            for (int k = 0; k < renderers.Count; k++)
            {
                var r = renderers[k];
                var originals = origMats[r];
                var mats = new Material[originals.Length];
                for (int i = 0; i < originals.Length; i++)
                {
                    var o = originals[i];
                    if (!Weatherable(o)) { mats[i] = o; continue; }
                    if (!copies.TryGetValue(o, out var c))
                    {
                        c = MakeCopy(o, folder, false);
                        copies[o] = c;
                    }
                    mats[i] = c;
                }
                var mf = r.GetComponent<MeshFilter>();
                Undo.RecordObject(r, "SMC-DETAILS");
                Undo.RecordObject(mf, "SMC-DETAILS");
                r.sharedMaterials = mats;
                r.additionalVertexStreams = null;
                mf.sharedMesh = soups[k];
                newSlots.Add(new SMDetails.Slot { renderer = r, original = originals, stream = soups[k], originalMesh = bodies[k].mesh, restMatrix = bodies[k].m });
            }
            // (7) hand paint is only valid for the same grid and the same pose of every part
            bool samePose = comp.paint0 != null && Vector3.Distance(comp.gridMin, gmin) < 1e-3f && Mathf.Abs(comp.voxel - vx) < 1e-5f
                            && newSlots.All(ns => { var os = comp.slots.FirstOrDefault(x => x.renderer == ns.renderer); return os != null && Near(os.restMatrix, ns.restMatrix); });
            comp.slots = newSlots;
            comp.materials = copies.Values.Distinct().ToList();
            comp.shape = shape;
            comp.noise = noise;
            comp.gridMin = gmin;
            comp.gridSize = (Vector3)dims * vx;
            comp.voxel = vx;
            comp.groundY = lo.y;
            comp.hasEdgeData = true;

            // 6 tracks (kick-up boxes), soot sources, track links
            FindTracksAndSources(comp, root, renderers, bodies, log);
            SwapLinks(comp, root, folder, log);
            if (comp.paint0 != null && (!samePose || comp.paint0.width != dims.x / 2 || comp.paint0.height != dims.y / 2 || comp.paint0.depth != dims.z / 2))
            {
                comp.paint0 = comp.paint1 = null;
                log.Add("機体の形・向き・部品の位置が変わったので、手描きは消えました");
            }
            // originals record (recovery when the component is lost)
            var rec = ScriptableObject.CreateInstance<SMDetailsOriginals>();
            rec.name = "Originals";
            rec.entries = comp.slots.Select(x => new SMDetailsOriginals.Entry { soup = x.stream, original = x.originalMesh, materials = x.original }).ToList();
            SaveInPlace(rec, folder + "/Originals.asset");
            comp.Apply();
            EditorUtility.SetDirty(comp);
            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            AssetDatabase.SaveAssets();
            int cleaned = CleanUp(comp, folder);
            if (cleaned > 0) log.Add($"使われていない生成ファイル {cleaned} 個を削除");
            log.Add($"合計: {sw.ElapsedMilliseconds} ms");
            LastReport = string.Join("\n", log);
            UnityEngine.Debug.Log("[SMC-DETAILS] " + root.name + "\n" + LastReport);
            return comp;
        }

        /// <summary>Track boxes (rest frame) for the kick-up, and soot sources: transforms named Muzzle / MachineGun /
        /// Exhaust under the rig (their forward = the shot / plume direction).</summary>
        static void FindTracksAndSources(SMDetails comp, Transform root, List<MeshRenderer> renderers,
                                         List<(Mesh mesh, Matrix4x4 m, Vector3[] pos)> bodies, List<string> log)
        {
            comp.trackMin = new Vector4[2];
            comp.trackMax = new Vector4[2];
            int t = 0;
            foreach (var track in AttachedRoots(root))
            {
                if (t >= 2) break;
                Vector3 mn = Vector3.positiveInfinity, mx = Vector3.negativeInfinity;
                for (int i = 0; i < renderers.Count; i++)
                {
                    if (!renderers[i].transform.IsChildOf(track)) continue;
                    foreach (var p in bodies[i].pos) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                }
                if (mn.x > mx.x) continue;
                comp.trackMin[t] = new Vector4(mn.x, mn.y, mn.z, 1f);
                comp.trackMax[t] = new Vector4(mx.x, mx.y, mx.z, 1f);
                t++;
            }
            var rig = OwnRig(root);
            var found = new List<SMDetails.BurnSource>();
            foreach (var tr in rig.GetComponentsInChildren<Transform>(true))
            {
                string n = tr.name;
                if (n.StartsWith("FX_")) continue;
                int type; float radius;
                if (n.IndexOf("Muzzle", StringComparison.OrdinalIgnoreCase) >= 0) { type = 0; radius = 0.25f; }
                else if (n.IndexOf("MachineGun", StringComparison.OrdinalIgnoreCase) >= 0) { type = 0; radius = 0.1f; }
                else if (n.IndexOf("Exhaust", StringComparison.OrdinalIgnoreCase) >= 0) { type = 1; radius = 0.35f; }
                else continue;
                found.Add(new SMDetails.BurnSource
                {
                    name = n, restPosition = tr.position - root.position, restDirection = tr.forward, radius = radius, type = type,
                });
            }
            // muzzles first, then exhausts, then machine guns (the shader takes 4)
            comp.burnSources = found.OrderBy(b => b.type == 0 && b.radius > 0.2f ? 0 : b.type == 1 ? 1 : 2).Take(4).ToList();
            log.Add($"履帯の巻き上げ {t} 本、焦げの発生源: {string.Join("、", comp.burnSources.Select(b => b.name))}");
        }

        static bool Near(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-3f) return false;
            return true;
        }

        static int Even(float f) { int i = Mathf.CeilToInt(f); return i + (i & 1); }
        static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());

        /// <summary>Puts the original materials back and removes the component (the generated assets stay).</summary>
        public static void Remove(SMDetails comp)
        {
            if (SMDetailsPaint.Target == comp) SMDetailsPaint.End(false);
            foreach (var s in comp.slots)
            {
                if (s.renderer == null || !Owns(comp, comp.transform, s.renderer)) continue;
                Undo.RecordObject(s.renderer, "SMC-DETAILS");
                s.renderer.sharedMaterials = s.original;
                if (s.renderer is MeshRenderer mr) mr.additionalVertexStreams = null;
                var mf = s.renderer.GetComponent<MeshFilter>();
                if (mf != null && s.originalMesh != null) { Undo.RecordObject(mf, "SMC-DETAILS"); mf.sharedMesh = s.originalMesh; }
            }
            foreach (var ls in comp.linkSwaps)
            {
                if (ls.track == null || ls.original == null) continue;
                var so = new SerializedObject(ls.track);
                var prop = so.FindProperty("linkPrefab");
                if (prop == null) continue;
                prop.objectReferenceValue = ls.original;
                so.ApplyModifiedProperties();
            }
            foreach (var td in comp.trackDirt) if (td != null && Owns(comp, comp.transform, td)) Undo.DestroyObjectImmediate(td);
            Undo.DestroyObjectImmediate(comp);
        }

        // ------------------------------------------------------------------ GPU visibility

        static Texture3D AnalyzeShape(List<(Mesh mesh, Matrix4x4 m, Vector3[] pos)> bodies, Vector3 gmin, Vector3Int dims, float voxel, int directions)
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(PackageRoot + "/Editor/Shaders/SMDetails.compute");
            var shader = Shader.Find("Hidden/SMCAD/DetailsDepth");
            if (cs == null || shader == null) throw new InvalidOperationException("SMC-DETAILS のシェーダが読み込めません");
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            Vector3 size = (Vector3)dims * voxel;
            Vector3 center = gmin + size * 0.5f;
            float radius = size.magnitude * 0.5f + voxel;
            int res = Mathf.Clamp(Mathf.CeilToInt(2f * radius / voxel), 64, 2048);
            var rt = new RenderTexture(res, res, 0, RenderTextureFormat.RFloat) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
            int count = dims.x * dims.y * dims.z;
            var acc = new ComputeBuffer(count, 4);
            acc.SetData(new float[count]);
            int k = cs.FindKernel("Accumulate");
            var cb = new CommandBuffer { name = "SMC-DETAILS shape" };
            var mpb = new MaterialPropertyBlock();
            try
            {
                for (int i = 0; i < directions; i++)
                {
                    // Fibonacci sphere
                    float y = 1f - 2f * (i + 0.5f) / directions;
                    float rr = Mathf.Sqrt(1f - y * y);
                    float phi = i * 2.39996323f;
                    var fwd = new Vector3(Mathf.Cos(phi) * rr, y, Mathf.Sin(phi) * rr).normalized;
                    var right = Vector3.Cross(Mathf.Abs(fwd.y) < 0.95f ? Vector3.up : Vector3.right, fwd).normalized;
                    var up = Vector3.Cross(fwd, right).normalized;
                    cb.Clear();
                    cb.SetRenderTarget(rt);
                    cb.ClearRenderTarget(false, true, new Color(1e7f, 1e7f, 1e7f, 1e7f));
                    cb.SetGlobalVector("_SmdCenter", center);
                    cb.SetGlobalVector("_SmdRight", right);
                    cb.SetGlobalVector("_SmdUp", up);
                    cb.SetGlobalVector("_SmdFwd", fwd);
                    cb.SetGlobalFloat("_SmdRadius", radius);
                    foreach (var b in bodies)
                    {
                        mpb.Clear();
                        mpb.SetMatrix("_SmdObjToRoot", b.m);
                        for (int sm = 0; sm < b.mesh.subMeshCount; sm++)
                            cb.DrawMesh(b.mesh, Matrix4x4.identity, mat, sm, 0, mpb);
                    }
                    cb.SetComputeTextureParam(cs, k, "_Depth", rt);
                    cb.SetComputeBufferParam(cs, k, "_Acc", acc);
                    cb.SetComputeIntParams(cs, "_Dims", dims.x, dims.y, dims.z);
                    cb.SetComputeVectorParam(cs, "_GridMin", gmin);
                    cb.SetComputeFloatParam(cs, "_Voxel", voxel);
                    cb.SetComputeVectorParam(cs, "_Center", center);
                    cb.SetComputeVectorParam(cs, "_Right", right);
                    cb.SetComputeVectorParam(cs, "_Up", up);
                    cb.SetComputeVectorParam(cs, "_Fwd", fwd);
                    cb.SetComputeFloatParam(cs, "_Radius", radius);
                    cb.SetComputeIntParam(cs, "_Res", res);
                    cb.SetComputeFloatParam(cs, "_Eps", voxel * 0.2f);
                    cb.SetComputeFloatParam(cs, "_FlipY", SystemInfo.graphicsUVStartsAtTop ? 1f : 0f);
                    cb.DispatchCompute(cs, k, (dims.x + 3) / 4, (dims.y + 3) / 4, (dims.z + 3) / 4);
                    Graphics.ExecuteCommandBuffer(cb);
                }
                var data = new float[count];
                acc.GetData(data);
                var bytes = new byte[count];
                float inv = 255f / directions;
                for (int i = 0; i < count; i++) bytes[i] = (byte)Mathf.Clamp(Mathf.RoundToInt(data[i] * inv), 0, 255);
                var tex = new Texture3D(dims.x, dims.y, dims.z, TextureFormat.R8, false)
                {
                    wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                };
                tex.SetPixelData(bytes, 0);
                tex.Apply(false, false);
                return tex;
            }
            finally
            {
                cb.Release();
                acc.Release();
                rt.Release();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        // ------------------------------------------------------------------ noise

        public const int NoiseSize = 96;
        public const int NoiseCells = 12;   // keep in step with SMD_NOISE_CELLS in SMDetails.hlsl

        public static Texture3D EnsureNoise()
        {
            string path = OutputRoot + "/SMD_Noise.asset";
            var tex = AssetDatabase.LoadAssetAtPath<Texture3D>(path);
            if (tex != null) return tex;
            Directory.CreateDirectory(OutputRoot);
            tex = MakeNoise();
            AssetDatabase.CreateAsset(tex, path);
            return tex;
        }

        /// <summary>Tiling fBm value noise, 4 independent channels, each equalised to an even spread 0..1.</summary>
        static Texture3D MakeNoise()
        {
            int N = NoiseSize;
            int count = N * N * N;
            var ch = new float[4][];
            // octaves per channel: (lattice cells, weight)
            var octaves = new[]
            {
                new[] { (12, 1f), (24, 0.5f), (48, 0.25f), (96, 0.12f) },  // R: general fBm
                new[] { (12, 1f), (24, 0.7f), (48, 0.5f), (96, 0.35f) },   // G: rougher
                new[] { (12, 1f), (24, 0.3f) },                            // B: smooth (scratch lines)
                new[] { (24, 1f), (48, 0.6f), (96, 0.4f) },                // A: fine grain
            };
            for (int c = 0; c < 4; c++)
            {
                var outp = new float[count];
                var rnd = new System.Random(1234 + c * 7919);
                foreach (var (cells, weight) in octaves[c])
                {
                    var lat = new float[cells * cells * cells];
                    for (int i = 0; i < lat.Length; i++) lat[i] = (float)rnd.NextDouble();
                    float step = (float)cells / N;
                    Parallel.For(0, N, z =>
                    {
                        for (int y = 0; y < N; y++)
                        for (int x = 0; x < N; x++)
                        {
                            float fx = x * step, fy = y * step, fz = z * step;
                            int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
                            float tx = S(fx - x0), ty = S(fy - y0), tz = S(fz - z0);
                            int x1 = (x0 + 1) % cells, y1 = (y0 + 1) % cells, z1 = (z0 + 1) % cells;
                            float L(int a, int b, int d) => lat[a + cells * (b + cells * d)];
                            float v = Mathf.Lerp(
                                Mathf.Lerp(Mathf.Lerp(L(x0, y0, z0), L(x1, y0, z0), tx), Mathf.Lerp(L(x0, y1, z0), L(x1, y1, z0), tx), ty),
                                Mathf.Lerp(Mathf.Lerp(L(x0, y0, z1), L(x1, y0, z1), tx), Mathf.Lerp(L(x0, y1, z1), L(x1, y1, z1), tx), ty), tz);
                            outp[x + N * (y + N * z)] += v * weight;
                        }
                    });
                }
                // equalise: rank -> 0..1
                var idx = Enumerable.Range(0, count).ToArray();
                Array.Sort(outp.ToArray(), idx);
                var eq = new float[count];
                for (int r = 0; r < count; r++) eq[idx[r]] = (r + 0.5f) / count;
                ch[c] = eq;
            }
            var bytes = new byte[count * 4];
            for (int i = 0; i < count; i++)
                for (int c = 0; c < 4; c++) bytes[i * 4 + c] = (byte)(ch[c][i] * 255f + 0.5f);
            var tex = new Texture3D(N, N, N, TextureFormat.RGBA32, true)
            {
                name = "SMD_Noise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear,
            };
            tex.SetPixelData(bytes, 0);
            tex.Apply(true, false);
            return tex;
        }

        static float S(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);
    }
}
