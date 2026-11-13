// SPDX-License-Identifier: MIT
// Imports a .glb written by SMConverter (-gltf) as native Unity assets.
// glTF (right handed, +Y up) -> Unity (left handed, +Y up): mirror X.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SMCAD.Editor
{
    public static class SMBlueprintImporter
    {
        public const string DefaultOutputRoot = "Assets/SMCAD";
        public const string ImportMenuPath = "Tools/SMC-AD/ブループリントを取り込む (.glb)...";
        public const string ReimportMenuPath = "Tools/SMC-AD/選択中のブループリントを取り込み直す";
        const string LitShader = "SMC-AD/SM Lit";
        const string GlassShader = "SMC-AD/SM Glass";

        internal enum TexRole { Dif, Asg, Nor }

        // ------------------------------------------------------------------ menu

        [MenuItem(ImportMenuPath, priority = 0)]
        static void ImportMenu()
        {
            string path = EditorUtility.OpenFilePanel("Scrap Mechanic のブループリントを取り込む（SMConverter の .glb）", "", "glb");
            if (string.IsNullOrEmpty(path)) return;
            var prefab = Import(path, DefaultOutputRoot);
            if (prefab != null)
            {
                Selection.activeObject = prefab;
                EditorGUIUtility.PingObject(prefab);
            }
        }

        [MenuItem(ReimportMenuPath, priority = 1)]
        static void ReimportMenu()
        {
            var bp = SelectedBlueprint();
            if (bp == null) return;
            if (!File.Exists(bp.sourcePath))
            {
                EditorUtility.DisplayDialog("SMC-AD", "元のファイルが見つかりません:\n" + bp.sourcePath, "OK");
                return;
            }
            Import(bp.sourcePath, OutputRootOf(bp));
        }

        [MenuItem(ReimportMenuPath, true)]
        static bool ReimportMenuValidate() => SelectedBlueprint() != null;

        static SMBlueprint SelectedBlueprint()
        {
            var go = Selection.activeGameObject;
            if (go == null) return null;
            var bp = go.GetComponentInParent<SMBlueprint>();
            if (bp != null) return bp;
            var src = PrefabUtility.GetCorrespondingObjectFromSource(go);
            return src != null ? src.GetComponentInParent<SMBlueprint>() : null;
        }

        static string OutputRootOf(SMBlueprint bp)
        {
            var asset = PrefabUtility.IsPartOfPrefabAsset(bp) ? bp.gameObject : PrefabUtility.GetCorrespondingObjectFromSource(bp.gameObject);
            string prefabPath = asset != null ? AssetDatabase.GetAssetPath(asset) : null;
            if (string.IsNullOrEmpty(prefabPath)) return DefaultOutputRoot;
            return Path.GetDirectoryName(Path.GetDirectoryName(prefabPath)).Replace('\\', '/');
        }

        // ------------------------------------------------------------------ import

        public static GameObject Import(string glbPath, string outputRoot = DefaultOutputRoot)
        {
            try
            {
                EditorUtility.DisplayProgressBar("SMC-AD", Path.GetFileName(glbPath) + " を読み込み中", 0f);
                return ImportInternal(Path.GetFullPath(glbPath), outputRoot);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static GameObject ImportInternal(string glbPath, string outputRoot)
        {
            var glb = GlbFile.Load(glbPath);
            if (!glb.IsSMConverter)
                Debug.LogWarning("[SMC-AD] " + glbPath + " は SMConverter で書き出したものではありません。SMの情報が足りない場合があります");
            if (PlayerSettings.colorSpace == ColorSpace.Gamma)
                Debug.LogWarning("[SMC-AD] このプロジェクトはガンマ色空間です。SMのマテリアルはリニア色空間を前提にしています" +
                                 "（Project Settings > Player > Other Settings > Color Space）。塗装色は sRGB で保存しました");

            string name = Sanitize(Path.GetFileNameWithoutExtension(glbPath));
            string folder = outputRoot.TrimEnd('/') + "/" + name;
            EnsureFolder(folder + "/Textures");
            EnsureFolder(folder + "/Materials");
            EnsureFolder(folder + "/Meshes");

            var materialsJson = glb.List("materials");

            EditorUtility.DisplayProgressBar("SMC-AD", "テクスチャ", 0.1f);
            var textures = ImportTextures(glb, materialsJson, folder + "/Textures");

            EditorUtility.DisplayProgressBar("SMC-AD", "マテリアル", 0.4f);
            var materials = new List<Material>();
            // glTF material names are not unique (e.g. the same textures with and without a normal map):
            // later duplicates get their index appended, so every material keeps its own asset
            var nameCount = new Dictionary<string, int>();
            for (int i = 0; i < materialsJson.Count; i++)
            {
                string matName = Sanitize(MiniJson.Str(materialsJson[i], "name", "Material_" + i));
                nameCount[matName] = nameCount.TryGetValue(matName, out int seen) ? seen + 1 : 1;
                string assetName = nameCount[matName] == 1 ? matName : matName + "_" + i;
                materials.Add(BuildMaterial(materialsJson[i], i, textures, folder + "/Materials", assetName));
            }

            EditorUtility.DisplayProgressBar("SMC-AD", "メッシュ", 0.6f);
            var meshes = new Dictionary<int, Mesh>();
            var meshesJson = glb.List("meshes");
            var bodyOfMesh = new Dictionary<int, int>();
            foreach (var n in glb.List("nodes"))
            {
                int m = MiniJson.Int(n, "mesh");
                int b = MiniJson.Int(MiniJson.Obj(n, "extras"), "sm_body");
                if (m >= 0 && b >= 0) bodyOfMesh[m] = b;
            }
            for (int i = 0; i < meshesJson.Count; i++)
                meshes[i] = BuildMesh(glb, meshesJson[i], i, folder + "/Meshes",
                                      bodyOfMesh.TryGetValue(i, out int body) ? BodyRandom(body) : 0f);

            EditorUtility.DisplayProgressBar("SMC-AD", "プレハブ", 0.85f);
            var nodesJson = glb.List("nodes");
            var scene = glb.List("scenes")[Math.Max(0, MiniJson.Int(glb.Json, "scene", 0))];
            var rootNodes = MiniJson.Arr(scene, "nodes") ?? new List<object>();

            GameObject root;
            if (rootNodes.Count == 1)
                root = BuildNode(glb, nodesJson, (int)(double)rootNodes[0], null, meshes, materials, glbPath);
            else
            {
                root = new GameObject(name);
                foreach (var n in rootNodes)
                    BuildNode(glb, nodesJson, (int)(double)n, root.transform, meshes, materials, glbPath);
            }
            root.name = name;
            if (root.GetComponent<SMBlueprint>() == null)
            {
                var bp = root.AddComponent<SMBlueprint>();
                bp.sourcePath = glbPath;
                bp.blueprintName = name;
            }

            // What the Blender export could not carry over (U3)
            var bpc = root.GetComponent<SMBlueprint>();
            var exportInfo = glb.List("nodes").Select(n => MiniJson.Obj(MiniJson.Obj(n, "extras"), "smcad_export"))
                                              .FirstOrDefault(e => e != null);
            if (bpc != null)
            {
                bpc.exportReport.Clear();
                foreach (var item in MiniJson.Arr(exportInfo, "report") ?? new List<object>())
                {
                    string line = $"[{MiniJson.Str(item, "level")}] [{MiniJson.Str(item, "material")}] {MiniJson.Str(item, "message")}";
                    bpc.exportReport.Add(line);
                    if (MiniJson.Str(item, "level") == "warning") Debug.LogWarning("[SMC-AD] Blender から持ってこられなかったもの: " + line);
                    else Debug.Log("[SMC-AD] Blender からの近似: " + line);
                }
            }

            string prefabPath = folder + "/" + name + ".prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();

            Debug.Log($"[SMC-AD] {Path.GetFileName(glbPath)} を取り込みました -> {prefabPath} " +
                      $"（メッシュ {meshes.Count}、マテリアル {materials.Count}、テクスチャ {textures.Count}）");
            return prefab;
        }

        // ------------------------------------------------------------------ textures

        static Dictionary<int, Texture2D> ImportTextures(GlbFile glb, List<object> materialsJson, string folder)
        {
            var roles = new Dictionary<int, TexRole>();
            foreach (var m in materialsJson)
            {
                var extras = MiniJson.Obj(m, "extras");
                void Add(string key, TexRole role)
                {
                    int t = MiniJson.Int(extras, key);
                    if (t >= 0 && !roles.ContainsKey(t)) roles[t] = role;
                }
                if (extras != null)
                {
                    Add("sm_dif_texture", TexRole.Dif);
                    Add("sm_asg_texture", TexRole.Asg);
                    Add("sm_nor_texture", TexRole.Nor);
                    int ovTex = MiniJson.Int(MiniJson.Obj(MiniJson.Obj(extras, "smcad"), "override"), "image_texture");
                    if (ovTex >= 0 && !roles.ContainsKey(ovTex)) roles[ovTex] = TexRole.Dif;
                }
                else
                {
                    // Plain glTF fallback: base colour / normal
                    int bc = MiniJson.Int(MiniJson.Obj(MiniJson.Obj(m, "pbrMetallicRoughness"), "baseColorTexture"), "index");
                    if (bc >= 0 && !roles.ContainsKey(bc)) roles[bc] = TexRole.Dif;
                    int nt = MiniJson.Int(MiniJson.Obj(m, "normalTexture"), "index");
                    if (nt >= 0 && !roles.ContainsKey(nt)) roles[nt] = TexRole.Nor;
                }
            }

            var paths = new Dictionary<int, string>();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var kv in roles)
                {
                    if (!glb.TryGetTexture(kv.Key, out string texName, out byte[] png)) continue;
                    string path = folder + "/" + Sanitize(texName) + ".png";
                    if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(png))
                        File.WriteAllBytes(path, png);
                    paths[kv.Key] = path;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            AssetDatabase.Refresh();

            var result = new Dictionary<int, Texture2D>();
            foreach (var kv in paths)
            {
                var importer = AssetImporter.GetAtPath(kv.Value) as TextureImporter;
                if (importer != null && ConfigureTexture(importer, roles[kv.Key]))
                    importer.SaveAndReimport();
                result[kv.Key] = AssetDatabase.LoadAssetAtPath<Texture2D>(kv.Value);
            }
            return result;
        }

        internal static bool ConfigureTexture(TextureImporter ti, TexRole role)
        {
            var type = role == TexRole.Nor ? TextureImporterType.NormalMap : TextureImporterType.Default;
            bool srgb = role == TexRole.Dif;
            bool changed = ti.textureType != type || ti.sRGBTexture != srgb || ti.alphaIsTransparency
                           || ti.wrapMode != TextureWrapMode.Repeat || ti.alphaSource != TextureImporterAlphaSource.FromInput && role != TexRole.Nor;
            ti.textureType = type;
            ti.sRGBTexture = srgb;
            ti.alphaIsTransparency = false; // Dif alpha is the paint mask, Asg alpha is gloss
            if (role != TexRole.Nor) ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.mipmapEnabled = true;
            return changed;
        }

        // ------------------------------------------------------------------ materials

        static readonly HashSet<int> GlassShaders = new HashSet<int> { 2, 6, 7, 8, 9, 10 };
        static readonly HashSet<int> FoliageShaders = new HashSet<int> { 3, 4, 11 };

        static Material BuildMaterial(object mJson, int index, Dictionary<int, Texture2D> textures, string folder,
                                      string assetName = null)
        {
            string name = assetName ?? Sanitize(MiniJson.Str(mJson, "name", "Material_" + index));
            string path = folder + "/" + name + ".mat";
            var extras = MiniJson.Obj(mJson, "extras");
            int smShader = extras != null ? MiniJson.Int(extras, "sm_shader", 1) : 1;
            bool glass = GlassShaders.Contains(smShader);
            var shader = Shader.Find(glass ? GlassShader : LitShader);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool created = mat == null;
            if (created)
            {
                mat = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader; // keeps matching properties (diffuse override settings)
            }

            Texture2D Tex(string key, string fallbackPath = null)
            {
                int t = extras != null ? MiniJson.Int(extras, key) : -1;
                if (t < 0 && fallbackPath != null)
                    t = MiniJson.Int(MiniJson.Obj(fallbackPath == "base" ? MiniJson.Obj(mJson, "pbrMetallicRoughness") : mJson,
                                                  fallbackPath == "base" ? "baseColorTexture" : "normalTexture"), "index");
                return t >= 0 && textures.TryGetValue(t, out var tex) ? tex : null;
            }

            var dif = Tex("sm_dif_texture", "base");
            var asg = Tex("sm_asg_texture");
            var nor = Tex("sm_nor_texture", "normal");

            SetupTextures(mat, smShader, dif, asg, nor, created);

            var smcad = extras != null ? MiniJson.Obj(extras, "smcad") : null;
            if (smcad != null)
                ApplyBlenderSettings(mat, smcad, ov =>
                {
                    int t = MiniJson.Int(ov, "image_texture");
                    return t >= 0 && textures.TryGetValue(t, out var tx) ? tx : null;
                }, glass);

            EditorUtility.SetDirty(mat);
            return mat;
        }

        internal static bool IsGlass(int smShader) => GlassShaders.Contains(smShader);

        internal static Shader ShaderFor(int smShader) => Shader.Find(IsGlass(smShader) ? GlassShader : LitShader);

        /// <summary>SM textures, alpha mode and render queue of an SM material.</summary>
        internal static void SetupTextures(Material mat, int smShader, Texture2D dif, Texture2D asg, Texture2D nor, bool created)
        {
            mat.SetTexture("_DifMap", dif);
            mat.SetTexture("_AsgMap", asg);
            mat.SetTexture("_NorMap", nor);
            mat.SetFloat("_HasDif", dif != null ? 1f : 0f);
            mat.SetFloat("_HasAsg", asg != null ? 1f : 0f);
            mat.SetFloat("_HasNor", nor != null ? 1f : 0f);

            if (IsGlass(smShader))
            {
                if (created) mat.SetFloat("_Transparency", smShader == 7 ? 0.85f : 0.7f);
                mat.renderQueue = (int)RenderQueue.Transparent;
            }
            else
            {
                float alphaMode = FoliageShaders.Contains(smShader) ? 1f : smShader == 5 ? 2f : 0f;
                mat.SetFloat("_AlphaMode", alphaMode);
                mat.renderQueue = alphaMode > 0f ? (int)RenderQueue.AlphaTest : (int)RenderQueue.Geometry;
            }
        }

        // ------------------------------------------------------------------ settings made in Blender (SMC-AD export)

        static float F(object o, string key, float def = 0f) => (float)MiniJson.Num(o, key, def);

        static Vector4 V(object value, Vector4 def)
        {
            if (value is double d) return new Vector4((float)d, (float)d, (float)d, (float)d);
            if (value is List<object> l && l.Count > 0)
            {
                float At(int i, float fallback) => i < l.Count ? (float)(double)l[i] : fallback;
                return new Vector4(At(0, 0f), At(1, At(0, 0f)), At(2, At(0, 0f)), At(3, 1f));
            }
            return def;
        }

        // Colours in the export are linear. Material colours are given in sRGB (Unity converts them in Linear
        // projects); in Gamma projects the vertex colours are stored as sRGB too, so the same value fits both.
        static Color LinearToMaterialColor(Vector4 v) => new Color(v.x, v.y, v.z, v.w).gamma;

        static readonly string[] MaskNames = { "Paint Mask", "Texture Mask", "Part Random", "Body Random", "Up Facing" };
        static readonly (string channel, string prefix)[] LayerChannels =
        {
            ("base_color", "_LBase"), ("metallic", "_LMetal"), ("roughness", "_LRough"),
            ("specular", "_LSpec"), ("emission", "_LEmis"),
        };

        internal static void ApplyBlenderSettings(Material mat, Dictionary<string, object> smcad,
                                                  Func<Dictionary<string, object>, Texture2D> overrideTexture, bool glass)
        {
            // Colour override (Blender = source of truth for exported files)
            var ov = MiniJson.Obj(smcad, "override");
            mat.SetFloat("_OvEnabled", ov != null ? 1f : 0f);
            if (ov != null)
            {
                mat.SetFloat("_OvWhole", MiniJson.Str(ov, "target") == "SURFACE" ? 1f : 0f);
                mat.SetColor("_OvColor", LinearToMaterialColor(V(MiniJson.Arr(ov, "color"), Vector4.one)));
                var ovTex = MiniJson.Str(ov, "source") == "IMAGE" ? overrideTexture(ov) : null;
                bool useTex = ovTex != null;
                mat.SetFloat("_OvUseTex", useTex ? 1f : 0f);
                if (useTex) mat.SetTexture("_OvTex", ovTex);
                mat.SetFloat("_OvProjection", MiniJson.Str(ov, "projection") == "UV" ? 1f : 0f);
                mat.SetFloat("_OvScale", F(ov, "scale", 1f));
                mat.SetFloat("_OvStrength", F(ov, "strength", 1f));
                mat.SetFloat("_OvUseMatch", MiniJson.Bool(ov, "match_enabled") ? 1f : 0f);
                var match = V(MiniJson.Arr(ov, "match_color"), new Vector4(0.5f, 0.5f, 0.5f, 1f));
                match.w = 1f;
                mat.SetColor("_OvMatch", LinearToMaterialColor(match));
                mat.SetFloat("_OvTolerance", F(ov, "match_tolerance", 0.02f));
            }
            if (glass) return;

            // Surface type (SMC-AD+ Surfaces)
            var sf = MiniJson.Obj(smcad, "surface");
            var values = sf != null ? MiniJson.Obj(sf, "values") : null;
            mat.SetFloat("_SfEnabled", values != null ? 1f : 0f);
            float coat = 0f;
            if (values != null)
            {
                mat.SetFloat("_SfTexMetallic", F(values, "metallic"));
                mat.SetFloat("_SfPaintMetallic", F(values, "paint_metallic"));
                mat.SetFloat("_SfRoughScale", F(values, "roughness_scale", 1f));
                mat.SetFloat("_SfRoughOffset", F(values, "roughness_offset"));
                mat.SetFloat("_SfSpecMul", F(values, "specular", 1f));
                coat = F(values, "coat");
                mat.SetFloat("_SfCoat", coat);
                mat.SetFloat("_SfCoatRough", F(values, "coat_roughness", 0.1f));
                mat.SetFloat("_SfNormalStrength", F(values, "normal_strength", 1f));
                mat.SetVector("_SfTint", V(MiniJson.Arr(values, "tint"), Vector4.one));
                mat.SetFloat("_SfTintStrength", F(values, "tint_strength"));
                mat.SetFloat("_SfNoise", F(values, "noise"));
                mat.SetFloat("_SfNoiseScale", F(values, "noise_scale", 10f));
            }
            if (coat > 0f) mat.EnableKeyword("_CLEARCOAT");
            else mat.DisableKeyword("_CLEARCOAT");

            // Hand-made layers (U2 stage 1)
            var layers = MiniJson.Obj(smcad, "layers");
            foreach (var (channel, prefix) in LayerChannels)
            {
                var layer = layers != null ? MiniJson.Obj(layers, channel) : null;
                var op = new Vector4(0f, -1f, 0f, 0f);
                Vector4 a = Vector4.zero, b = Vector4.zero;
                float f = 0f;
                if (layer != null)
                {
                    object Raw(string key) => layer.TryGetValue(key, out var v) ? v : null;
                    switch (MiniJson.Str(layer, "op"))
                    {
                        case "constant":
                            op.x = 1f; a = V(Raw("a"), Vector4.zero);
                            break;
                        case "multiply":
                        case "add":
                            op.x = MiniJson.Str(layer, "op") == "multiply" ? 2f : 3f;
                            a = V(Raw("a"), Vector4.zero);
                            f = MiniJson.Bool(layer, "clamp") ? 1f : 0f;
                            break;
                        case "mix":
                            op.x = 4f;
                            string mask = MiniJson.Str(layer, "mask");
                            op.y = mask != null ? Array.IndexOf(MaskNames, mask) : -1f;
                            op.z = Raw("a") == null ? 1f : 0f;
                            op.w = Raw("b") == null ? 1f : 0f;
                            a = V(Raw("a"), Vector4.zero);
                            b = V(Raw("b"), Vector4.zero);
                            f = F(layer, "factor", 1f);
                            break;
                    }
                }
                mat.SetVector(prefix + "Op", op);
                mat.SetVector(prefix + "A", a);
                mat.SetVector(prefix + "B", b);
                mat.SetFloat(prefix + "F", f);
            }
        }

        // ------------------------------------------------------------------ meshes

        // Same hash as the shader's Part Random (Blender White Noise 1D), applied to the body index
        internal static float BodyRandom(int body)
        {
            uint Rot(uint x, int k) => (x << k) | (x >> (32 - k));
            uint kx = BitConverter.ToUInt32(BitConverter.GetBytes((float)body), 0);
            uint a, b, c;
            a = b = c = 0xdeadbeefu + (1u << 2) + 13u;
            a += kx;
            c ^= b; c -= Rot(b, 14); a ^= c; a -= Rot(c, 11); b ^= a; b -= Rot(a, 25);
            c ^= b; c -= Rot(b, 16); a ^= c; a -= Rot(c, 4); b ^= a; b -= Rot(a, 14);
            c ^= b; c -= Rot(b, 24);
            return c / 4294967295f;
        }

        static Mesh BuildMesh(GlbFile glb, object meshJson, int index, string folder, float bodyRandom = 0f)
        {
            string meshName = Sanitize(MiniJson.Str(meshJson, "name", "Mesh_" + index));
            string path = folder + "/" + meshName + ".asset";

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var colors = new List<Color>();
            var shapes = new List<Vector2>();
            var submeshes = new List<int[]>();

            foreach (var prim in MiniJson.Arr(meshJson, "primitives") ?? new List<object>())
            {
                var attr = MiniJson.Obj(prim, "attributes");
                int baseVertex = positions.Count;

                var pos = glb.ReadFloats(MiniJson.Int(attr, "POSITION"), out _);
                int count = pos.Length / 3;
                for (int i = 0; i < count; i++)
                    positions.Add(new Vector3(-pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]));

                if (MiniJson.Has(attr, "NORMAL"))
                {
                    var n = glb.ReadFloats(MiniJson.Int(attr, "NORMAL"), out _);
                    for (int i = 0; i < count; i++) normals.Add(new Vector3(-n[i * 3], n[i * 3 + 1], n[i * 3 + 2]));
                }
                else for (int i = 0; i < count; i++) normals.Add(Vector3.up);

                if (MiniJson.Has(attr, "TEXCOORD_0"))
                {
                    var t = glb.ReadFloats(MiniJson.Int(attr, "TEXCOORD_0"), out _);
                    for (int i = 0; i < count; i++) uvs.Add(new Vector2(t[i * 2], 1f - t[i * 2 + 1]));
                }
                else for (int i = 0; i < count; i++) uvs.Add(Vector2.zero);

                if (MiniJson.Has(attr, "COLOR_0"))
                {
                    // COLOR_0 is linear. Unity passes vertex colours to shaders unconverted, while material colours
                    // are converted only in Linear projects, so store sRGB values in Gamma projects.
                    bool gammaProject = PlayerSettings.colorSpace == ColorSpace.Gamma;
                    var c = glb.ReadFloats(MiniJson.Int(attr, "COLOR_0"), out int comps);
                    for (int i = 0; i < count; i++)
                    {
                        var col = new Color(c[i * comps], c[i * comps + 1], c[i * comps + 2], comps > 3 ? c[i * comps + 3] : 1f);
                        colors.Add(gammaProject ? col.gamma : col);
                    }
                }
                else for (int i = 0; i < count; i++) colors.Add(Color.white);

                if (MiniJson.Has(attr, "_SM_SHAPE"))
                {
                    var s = glb.ReadFloats(MiniJson.Int(attr, "_SM_SHAPE"), out _);
                    for (int i = 0; i < count; i++) shapes.Add(new Vector2(s[i], bodyRandom));
                }
                else for (int i = 0; i < count; i++) shapes.Add(new Vector2(-1f, bodyRandom));

                int[] idx = MiniJson.Has(prim, "indices")
                    ? glb.ReadIndices(MiniJson.Int(prim, "indices"))
                    : Enumerable.Range(0, count).ToArray();

                // Mirroring flips the winding
                var tris = new int[idx.Length];
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    tris[i] = baseVertex + idx[i];
                    tris[i + 1] = baseVertex + idx[i + 2];
                    tris[i + 2] = baseVertex + idx[i + 1];
                }
                submeshes.Add(tris);
            }

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool created = mesh == null;
            if (created) mesh = new Mesh();
            mesh.Clear();
            mesh.name = meshName;
            mesh.indexFormat = positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(2, shapes);
            mesh.SetColors(colors);
            mesh.subMeshCount = submeshes.Count;
            for (int i = 0; i < submeshes.Count; i++)
                mesh.SetTriangles(submeshes[i], i, false);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            if (created) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);
            return mesh;
        }

        // ------------------------------------------------------------------ nodes

        static GameObject BuildNode(GlbFile glb, List<object> nodesJson, int index, Transform parent,
                                    Dictionary<int, Mesh> meshes, List<Material> materials, string glbPath)
        {
            var node = nodesJson[index];
            var go = new GameObject(MiniJson.Str(node, "name", "Node_" + index));
            go.transform.SetParent(parent, false);

            var t = MiniJson.Arr(node, "translation");
            if (t != null) go.transform.localPosition = new Vector3(-(float)(double)t[0], (float)(double)t[1], (float)(double)t[2]);
            var r = MiniJson.Arr(node, "rotation");
            if (r != null) go.transform.localRotation = new Quaternion((float)(double)r[0], -(float)(double)r[1], -(float)(double)r[2], (float)(double)r[3]);
            var s = MiniJson.Arr(node, "scale");
            if (s != null) go.transform.localScale = new Vector3((float)(double)s[0], (float)(double)s[1], (float)(double)s[2]);

            int meshIndex = MiniJson.Int(node, "mesh");
            if (meshIndex >= 0 && meshes.TryGetValue(meshIndex, out var mesh))
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                var prims = MiniJson.Arr(glb.List("meshes")[meshIndex], "primitives");
                renderer.sharedMaterials = prims.Select(p =>
                {
                    int mi = MiniJson.Int(p, "material");
                    return mi >= 0 && mi < materials.Count ? materials[mi] : null;
                }).ToArray();
            }

            ApplyExtras(go, MiniJson.Obj(node, "extras"), glbPath);

            foreach (var c in MiniJson.Arr(node, "children") ?? new List<object>())
                BuildNode(glb, nodesJson, (int)(double)c, go.transform, meshes, materials, glbPath);
            return go;
        }

        static void ApplyExtras(GameObject go, Dictionary<string, object> extras, string glbPath)
        {
            if (extras == null) return;

            if (MiniJson.Has(extras, "sm_shapes") || MiniJson.Has(extras, "sm_blueprint"))
            {
                var bp = go.AddComponent<SMBlueprint>();
                bp.sourcePath = glbPath;
                bp.blueprintName = MiniJson.Str(extras, "sm_blueprint", go.name);
                bp.rootBody = MiniJson.Int(extras, "sm_root_body", -1);
                foreach (var sh in MiniJson.Arr(extras, "sm_shapes") ?? new List<object>())
                    bp.shapes.Add(new SMBlueprint.Shape
                    {
                        uuid = MiniJson.Str(sh, "uuid"),
                        color = MiniJson.Str(sh, "color"),
                        type = MiniJson.Int(sh, "type", 0),
                        child = MiniJson.Int(sh, "child", -1)
                    });
                foreach (var j in MiniJson.Arr(extras, "sm_joints") ?? new List<object>())
                    bp.joints.Add(new SMBlueprint.JointInfo
                    {
                        id = MiniJson.Int(j, "id", -1),
                        type = MiniJson.Str(j, "type"),
                        uuid = MiniJson.Str(j, "uuid"),
                        bodyA = MiniJson.Int(j, "body_a", -1),
                        bodyB = MiniJson.Int(j, "body_b", -1)
                    });
                return;
            }

            if (MiniJson.Has(extras, "sm_body"))
            {
                var body = go.AddComponent<SMBody>();
                body.bodyIndex = MiniJson.Int(extras, "sm_body", -1);
                body.isRoot = MiniJson.Bool(extras, "sm_root");
                body.connected = MiniJson.Bool(extras, "sm_connected", true);
            }

            var jointJson = MiniJson.Obj(extras, "sm_joint");
            if (jointJson != null)
            {
                var joint = go.AddComponent<SMJoint>();
                switch (MiniJson.Str(jointJson, "type"))
                {
                    case "bearing": joint.type = SMJointType.Bearing; break;
                    case "piston": joint.type = SMJointType.Piston; break;
                    case "suspension": joint.type = SMJointType.Suspension; break;
                    default: joint.type = SMJointType.Other; break;
                }
                joint.jointId = MiniJson.Int(jointJson, "id", -1);
                joint.uuid = MiniJson.Str(jointJson, "uuid");
                joint.parentBody = MiniJson.Int(jointJson, "parent_body", -1);
                // SMConverter puts the joint axis on the node's local +Z in SM space, which is local +Y in glTF / Unity
                joint.localAxis = Vector3.up;
            }
        }

        // ------------------------------------------------------------------ utils

        static string Sanitize(string s)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = s.Select(c => invalid.Contains(c) || c == '#' ? '_' : c).ToArray();
            string r = new string(chars).Trim();
            return string.IsNullOrEmpty(r) ? "unnamed" : r;
        }

        static void EnsureFolder(string path)
        {
            path = path.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
