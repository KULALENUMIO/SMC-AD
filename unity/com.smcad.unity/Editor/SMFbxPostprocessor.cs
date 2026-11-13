// SPDX-License-Identifier: MIT
// FBX written by SMC-AD in Blender ("Unity 向けに書き出し (FBX)"): the blueprint as it was edited and rigged in Blender.
// Unity's own FBX importer reads the objects, bones, weights and vertex colours; this postprocessor adds the SM side:
//   - the FBX materials become SM Lit / SM Glass materials (textures from "<name>_textures/", colour override,
//     surface type, layer nodes)
//   - the part id UV (FBX UV channel 1, "SM_Part") moves to UV2 where the SM shader reads it
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
            SMBlueprintImporter.ConfigureTexture((TextureImporter)assetImporter, role);
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

            int materials = ApplyMaterials(root, MiniJson.Obj(data, "materials"), folder);
            var bodies = MiniJson.Obj(data, "bodies") ?? new Dictionary<string, object>();
            int moved = 0, joints = 0;
            foreach (var kv in bodies)
            {
                var t = Find(kv.Key);
                if (t == null) continue;
                var body = kv.Value as Dictionary<string, object>;
                int bodyIndex = MiniJson.Int(body, "sm_body", -1);
                if (MiniJson.Bool(body, "part_uv") && MovePartUv(t, SMBlueprintImporter.BodyRandom(bodyIndex))) moved++;

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
                }
            }
            return done.Count;
        }

        static Mesh MeshOf(Transform t)
        {
            // (no ?. here: Unity objects compare to null with their own operator)
            if (t.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null) return mf.sharedMesh;
            if (t.TryGetComponent<SkinnedMeshRenderer>(out var smr) && smr.sharedMesh != null) return smr.sharedMesh;
            return null;
        }

        static bool MovePartUv(Transform t, float bodyRandom)
        {
            Mesh mesh = MeshOf(t);
            if (mesh == null) return false;
            var uv1 = new List<Vector2>();
            mesh.GetUVs(1, uv1);
            if (uv1.Count != mesh.vertexCount) return false;
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
