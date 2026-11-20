// SPDX-License-Identifier: MIT
// FBX written by SMC-AD in Blender ("Unity 向けに書き出し (FBX)"): the blueprint as it was edited and rigged in Blender.
// Unity's own FBX importer reads the objects, bones, weights and vertex colours; this postprocessor adds the SM side:
//   - the FBX materials become SM Lit / SM Glass materials (textures from "<name>_textures/", colour override,
//     surface type, layer nodes)
//   - the part id UV (FBX UV channel 1, "SM_Part") moves to UV2 where the SM shader reads it
//   - "量産向け" exports: one atlas material for most SM materials; their settings go into a small table
//     texture (sub-asset of the model) and each face's row (SM_Part v) moves to UV3
//   - SMBlueprint / SMBody / SMJoint components with the data from Blender
// The SM data is one JSON string on the root object (FBX user property "smcad_unity").
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SMCAD.Editor
{
    public class SMFbxPostprocessor : AssetPostprocessor
    {
        public const string Property = "smcad_unity";
        public const string MenuPath = "Tools/SMC-AD/Blender から書き出した FBX を取り込む...";
        static readonly Dictionary<string, string> Pending = new Dictionary<string, string>();
        // The object that carried the SM data (Unity renames a single top object to the file name)
        static readonly Dictionary<string, GameObject> PendingOwner = new Dictionary<string, GameObject>();
        static readonly Regex TextureFolder = new Regex(@"_textures/(dif|asg|nor|override)/[^/]+$", RegexOptions.IgnoreCase);

        // ------------------------------------------------------------------ textures next to the FBX

        void OnPreprocessTexture()
        {
            var m = TextureFolder.Match(assetPath.Replace('\\', '/'));
            if (!m.Success) return;
            var role = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "asg" => SMBlueprintImporter.TexRole.Asg,
                "nor" => SMBlueprintImporter.TexRole.Nor,
                _ => SMBlueprintImporter.TexRole.Dif, // dif, override: colour textures
            };
            var ti = (TextureImporter)assetImporter;
            SMBlueprintImporter.ConfigureTexture(ti, role);
            // atlases written by the "量産向け" export can be 4096
            if (Path.GetFileNameWithoutExtension(assetPath).EndsWith("_atlas", StringComparison.OrdinalIgnoreCase)
                && ti.maxTextureSize < 4096)
                ti.maxTextureSize = 4096;
        }

        // ------------------------------------------------------------------ model

        static readonly byte[] Marker = System.Text.Encoding.ASCII.GetBytes(Property);

        static bool IsSmcadFbx(string path)
        {
            if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            for (int i = 0; i + Marker.Length <= bytes.Length; i++)
            {
                int k = 0;
                while (k < Marker.Length && bytes[i + k] == Marker[k]) k++;
                if (k == Marker.Length) return true;
            }
            return false;
        }

        void OnPreprocessModel()
        {
            // First import only (no settings yet), so a rig type changed in Unity (e.g. Humanoid) is kept
            if (!assetImporter.importSettingsMissing || !IsSmcadFbx(assetPath)) return;
            var mi = (ModelImporter)assetImporter;
            mi.animationType = ModelImporterAnimationType.Generic;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.importAnimation = false; // animation is made in Unity
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }

        void OnPostprocessGameObjectWithUserProperties(GameObject go, string[] names, object[] values)
        {
            int i = Array.IndexOf(names, Property);
            if (i >= 0 && values[i] is string json)
            {
                Pending[assetPath] = json;
                PendingOwner[assetPath] = go;
            }
        }

        void OnPostprocessModel(GameObject root)
        {
            if (!Pending.TryGetValue(assetPath, out string json)) return;
            Pending.Remove(assetPath);
            PendingOwner.TryGetValue(assetPath, out GameObject owner);
            PendingOwner.Remove(assetPath);
            var data = MiniJson.Parse(json) as Dictionary<string, object>;
            if (data == null)
            {
                Debug.LogWarning("[SMC-AD] " + assetPath + ": SM の情報を読めませんでした");
                return;
            }
            string folder = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            var transforms = root.GetComponentsInChildren<Transform>(true);
            string ownerName = MiniJson.Str(data, "root_object");
            Transform Find(string name) => transforms.FirstOrDefault(t => t.name == name)
                ?? (owner != null && name == ownerName ? owner.transform : null);

            var materialsJson = MiniJson.Obj(data, "materials");
            bool atlas = materialsJson != null && materialsJson.Values.Any(v => MiniJson.Obj(v, "atlas") != null);
            int materials = ApplyMaterials(root, materialsJson, folder);
            var bodies = MiniJson.Obj(data, "bodies") ?? new Dictionary<string, object>();
            int moved = 0, joints = 0;
            foreach (var kv in bodies)
            {
                var t = Find(kv.Key);
                if (t == null) continue;
                var body = kv.Value as Dictionary<string, object>;
                int bodyIndex = MiniJson.Int(body, "sm_body", -1);
                if (MiniJson.Bool(body, "part_uv") && MovePartUv(t, SMBlueprintImporter.BodyRandom(bodyIndex), atlas)) moved++;

                var smBody = t.gameObject.GetComponent<SMBody>() ?? t.gameObject.AddComponent<SMBody>();
                smBody.bodyIndex = bodyIndex;
                smBody.isRoot = MiniJson.Bool(body, "root");
                smBody.connected = MiniJson.Bool(body, "connected", true);

                var joint = MiniJson.Obj(body, "joint");
                if (joint == null) continue;
                var j = t.gameObject.GetComponent<SMJoint>() ?? t.gameObject.AddComponent<SMJoint>();
                j.type = MiniJson.Str(joint, "type") switch
                {
                    "bearing" => SMJointType.Bearing,
                    "piston" => SMJointType.Piston,
                    "suspension" => SMJointType.Suspension,
                    _ => SMJointType.Other,
                };
                j.jointId = MiniJson.Int(joint, "id", -1);
                j.uuid = MiniJson.Str(joint, "uuid");
                j.parentBody = MiniJson.Int(joint, "parent_body", -1);
                var axis = MiniJson.Arr(joint, "axis_world");
                if (axis != null && axis.Count == 3)
                {
                    var world = new Vector3((float)(double)axis[0], (float)(double)axis[1], (float)(double)axis[2]);
                    j.localAxis = t.InverseTransformDirection(world).normalized;
                }
                joints++;
            }

            var bp = root.GetComponent<SMBlueprint>() ?? root.AddComponent<SMBlueprint>();
            bp.sourcePath = assetPath;
            bp.blueprintName = MiniJson.Str(data, "blueprint", root.name);
            bp.exportReport.Clear();
            foreach (var item in MiniJson.Arr(data, "report") ?? new List<object>())
            {
                string line = $"[{MiniJson.Str(item, "level")}] [{MiniJson.Str(item, "material")}] {MiniJson.Str(item, "message")}";
                bp.exportReport.Add(line);
                if (MiniJson.Str(item, "level") == "warning")
                    Debug.LogWarning("[SMC-AD] Blender から持ってこられなかったもの: " + line);
            }
            Debug.Log($"[SMC-AD] {Path.GetFileName(assetPath)}: SMマテリアル {materials}、ボディ {bodies.Count}、ジョイント {joints}、パーツ番号 {moved}");
        }

        int ApplyMaterials(GameObject root, Dictionary<string, object> materials, string folder)
        {
            if (materials == null) return 0;
            var done = new HashSet<Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || done.Contains(mat)) continue;
                    done.Add(mat);
                    if (!(MiniJson.Obj(materials, mat.name) is Dictionary<string, object> info)) continue;

                    int smShader = MiniJson.Int(info, "sm_shader", 1);
                    mat.shader = SMBlueprintImporter.ShaderFor(smShader);
                    var textures = MiniJson.Obj(info, "textures") ?? new Dictionary<string, object>();
                    Texture2D Load(string rel)
                    {
                        if (string.IsNullOrEmpty(rel)) return null;
                        string path = folder + "/" + rel;
                        context.DependsOnArtifact(path);
                        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                        if (tex == null) Debug.LogWarning("[SMC-AD] テクスチャが見つかりません（FBX の横の「_textures」フォルダも Unity に入れてください）: " + path);
                        return tex;
                    }
                    SMBlueprintImporter.SetupTextures(mat, smShader, Load(MiniJson.Str(textures, "dif")),
                                                      Load(MiniJson.Str(textures, "asg")), Load(MiniJson.Str(textures, "nor")), true);
                    var smcad = MiniJson.Obj(info, "smcad");
                    if (smcad != null)
                        SMBlueprintImporter.ApplyBlenderSettings(mat, smcad, ov => Load(MiniJson.Str(ov, "image_path")),
                                                                 SMBlueprintImporter.IsGlass(smShader));
                    var atlas = MiniJson.Obj(info, "atlas");
                    if (atlas != null) SetupAtlas(mat, atlas);
                    else mat.DisableKeyword("_SMCAD_ATLAS");
                }
            }
            return done.Count;
        }

        // ------------------------------------------------------------------ atlas ("量産向け")

        static float F(object o, string key, float def = 0f) => (float)MiniJson.Num(o, key, def);

        static Vector4 Vec(object o, string key, Vector4 def)
        {
            var l = MiniJson.Arr(o, key);
            if (l == null || l.Count < 3) return def;
            return new Vector4((float)(double)l[0], (float)(double)l[1], (float)(double)l[2], l.Count > 3 ? (float)(double)l[3] : def.w);
        }

        /// <summary>One row of settings per original material (layout in SMCommon.hlsl, values linear).</summary>
        void SetupAtlas(Material mat, Dictionary<string, object> atlas)
        {
            var rows = MiniJson.Arr(atlas, "rows") ?? new List<object>();
            const int Width = 7;
            var tex = new Texture2D(Width, Mathf.Max(1, rows.Count), TextureFormat.RGBAFloat, false, true)
            {
                name = mat.name + "_Params", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
            };
            var px = new Color[Width * Mathf.Max(1, rows.Count)];
            bool coat = false;
            for (int r = 0; r < rows.Count; r++)
            {
                var smcad = MiniJson.Obj(rows[r], "smcad") ?? new Dictionary<string, object>();
                var ov = MiniJson.Obj(smcad, "override");
                var sf = MiniJson.Obj(MiniJson.Obj(smcad, "surface"), "values");
                Vector4 ovColor = Vec(ov, "color", Vector4.one);
                Vector4 ovMatch = Vec(ov, "match_color", new Vector4(0.5f, 0.5f, 0.5f, 1f));
                Vector4 tint = Vec(sf, "tint", Vector4.one);
                float c = sf != null ? F(sf, "coat") : 0f;
                coat |= c > 0f;
                int o = r * Width;
                px[o + 0] = new Color(ov != null ? 1f : 0f, MiniJson.Str(ov, "target") == "SURFACE" ? 1f : 0f,
                                      F(ov, "strength", 1f), MiniJson.Bool(ov, "match_enabled") ? 1f : 0f);
                px[o + 1] = new Color(ovColor.x, ovColor.y, ovColor.z, F(ov, "match_tolerance", 0.02f));
                px[o + 2] = new Color(ovMatch.x, ovMatch.y, ovMatch.z, sf != null ? 1f : 0f);
                px[o + 3] = new Color(F(sf, "metallic"), F(sf, "paint_metallic"), F(sf, "roughness_scale", 1f), F(sf, "roughness_offset"));
                px[o + 4] = new Color(F(sf, "specular", 1f), c, F(sf, "coat_roughness", 0.1f), F(sf, "normal_strength", 1f));
                px[o + 5] = new Color(tint.x, tint.y, tint.z, F(sf, "tint_strength"));
                px[o + 6] = new Color(F(sf, "noise"), F(sf, "noise_scale", 10f), 1f, 1f);
            }
            tex.SetPixels(px);
            tex.Apply(false, false);
            context.AddObjectToAsset(tex.name, tex);
            mat.SetTexture("_SMParams", tex);
            mat.SetFloat("_SMAtlas", 1f);
            mat.EnableKeyword("_SMCAD_ATLAS");
            mat.SetFloat("_OvEnabled", 0f);
            mat.SetFloat("_SfEnabled", 0f);
            if (coat) mat.EnableKeyword("_CLEARCOAT");
            else mat.DisableKeyword("_CLEARCOAT");
        }

        static Mesh MeshOf(Transform t)
        {
            // (no ?. here: Unity objects compare to null with their own operator)
            if (t.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null) return mf.sharedMesh;
            if (t.TryGetComponent<SkinnedMeshRenderer>(out var smr) && smr.sharedMesh != null) return smr.sharedMesh;
            return null;
        }

        static bool MovePartUv(Transform t, float bodyRandom, bool atlasRows)
        {
            Mesh mesh = MeshOf(t);
            if (mesh == null) return false;
            var uv1 = new List<Vector2>();
            mesh.GetUVs(1, uv1);
            if (uv1.Count != mesh.vertexCount) return false;
            if (atlasRows) mesh.SetUVs(3, uv1.Select(uv => new Vector2(uv.y, 0f)).ToList());
            mesh.SetUVs(2, uv1.Select(uv => new Vector2(uv.x, bodyRandom)).ToList());
            mesh.SetUVs(1, (List<Vector2>)null);
            return true;
        }

        // ------------------------------------------------------------------ menu: FBX + textures into the project

        [MenuItem(MenuPath, priority = 2)]
        static void ImportMenu()
        {
            string path = EditorUtility.OpenFilePanel("Blender（SMC-AD）から書き出した FBX を取り込む", "", "fbx");
            if (string.IsNullOrEmpty(path)) return;
            var model = Import(path);
            if (model != null)
            {
                Selection.activeObject = model;
                EditorGUIUtility.PingObject(model);
            }
        }

        /// <summary>Copies the FBX and its "_textures" folder into Assets/SMCAD/&lt;name&gt;/ and imports them (textures first).</summary>
        public static GameObject Import(string fbxPath, string outputRoot = SMBlueprintImporter.DefaultOutputRoot)
        {
            fbxPath = Path.GetFullPath(fbxPath);
            string stem = Path.GetFileNameWithoutExtension(fbxPath);
            string folder = outputRoot.TrimEnd('/') + "/" + stem;
            Directory.CreateDirectory(folder);
            string srcTex = Path.Combine(Path.GetDirectoryName(fbxPath), stem + "_textures");
            var copied = new List<string>();
            if (Directory.Exists(srcTex))
            {
                foreach (var file in Directory.GetFiles(srcTex, "*", SearchOption.AllDirectories))
                {
                    string rel = file.Substring(srcTex.Length + 1).Replace('\\', '/');
                    string dst = folder + "/" + stem + "_textures/" + rel;
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    if (!File.Exists(dst) || !File.ReadAllBytes(dst).SequenceEqual(File.ReadAllBytes(file)))
                        File.Copy(file, dst, true);
                    copied.Add(dst);
                }
            }
            else
            {
                Debug.LogWarning("[SMC-AD] テクスチャのフォルダがありません: " + srcTex);
            }
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var t in copied) AssetDatabase.ImportAsset(t, ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            string dstFbx = folder + "/" + Path.GetFileName(fbxPath);
            File.Copy(fbxPath, dstFbx, true);
            AssetDatabase.ImportAsset(dstFbx, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<GameObject>(dstFbx);
        }
    }
}
