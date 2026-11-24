// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: creating / draping cloths and nets in the editor, and their textures + materials
// (made by code once per project: net cords, leaf-strip atlas per palette, canvas weave + normal map).
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SMCAD.Details.Editor.Decor
{
    public static class SMDecorCloths
    {
        public const string TextureFolder = SMDetailsBuilder.OutputRoot + "/DecorTextures";

        /// <summary>A new sheet from four picked points (world hits); it goes on the part of the first point.</summary>
        public static SMDecorCloth Create(SMDecorSet set, IList<SMDecorHit> hits, SMDecorClothType type)
        {
            var root = set.transform;
            var part = hits[0].part.transform != null ? hits[0].part.transform : root;
            var go = new GameObject((type == SMDecorClothType.CamoNet ? "カモネット " : type == SMDecorClothType.Tarp ? "防水布 " : "布 ")
                                    + (set.items.Count(x => x is SMDecorCloth) + 1));
            Undo.RegisterCreatedObjectUndo(go, "布・ネットを掛ける");
            go.transform.SetParent(part, false);
            go.layer = part.gameObject.layer;
            var cloth = go.AddComponent<SMDecorCloth>();
            cloth.set = set;
            cloth.part = part;
            cloth.type = type;
            cloth.seed = Random.Range(0, 100000);
            if (type != SMDecorClothType.CamoNet) { cloth.palette = SMDecorPalette.Olive; cloth.thickness = 0.006f; cloth.stiffness = 0.25f; }
            foreach (var h in hits)
            {
                cloth.corners.Add(part.InverseTransformPoint(h.point));
                cloth.cornerNormals.Add(part.InverseTransformDirection(h.normal).normalized);
            }
            Undo.RecordObject(set, "布・ネットを掛ける");
            set.items.Add(cloth);
            Rebuild(cloth);
            return cloth;
        }

        static ComputeShader gpu;
        public static ComputeShader Gpu => gpu != null ? gpu : gpu = AssetDatabase.LoadAssetAtPath<ComputeShader>(SMDetailsBuilder.PackageRoot + "/Editor/Shaders/SMDecorCloth.compute");

        /// <summary>drape = false: only re-mesh the last shape (while cutting), when it still fits.</summary>
        public static void Rebuild(SMDecorCloth cloth, bool drape = true)
        {
            var set = cloth.set;
            if (set == null || cloth.part == null) return;
            var root = set.transform;
            string folder = SMDecorField.Folder(set, root);
            if (SMDecorEditing.NeedsOwnMesh(cloth))
            {
                var m = new Mesh { name = "Cloth_" + cloth.seed };
                AssetDatabase.CreateAsset(m, AssetDatabase.GenerateUniqueAssetPath(folder + "/Cloth_" + cloth.seed + ".asset"));
                cloth.mesh = m;
            }
            var sw = Stopwatch.StartNew();
            if (drape) EditorUtility.DisplayProgressBar("SMC-DETAILS 装備", cloth.Label + "を掛けています…", 0.5f);
            try
            {
                var rootFromPart = root.worldToLocalMatrix * cloth.part.localToWorldMatrix;
                var st = SMDecorClothGen.Build(cloth, rootFromPart, cloth.mesh, Gpu, drape);
                cloth.size = st.size;
                cloth.particles = st.particles;
                cloth.maxPenetration = st.maxPenetration;
            }
            finally { EditorUtility.ClearProgressBar(); }
            if (drape) cloth.buildSeconds = sw.ElapsedMilliseconds / 1000f;
            cloth.GetComponent<MeshFilter>().sharedMesh = cloth.mesh;
            var mats = Materials(cloth);
            cloth.GetComponent<MeshRenderer>().sharedMaterials = cloth.mesh.subMeshCount > 1 ? mats : new[] { mats[0] };
            EditorUtility.SetDirty(cloth.mesh);
            EditorUtility.SetDirty(cloth);
            cloth.buildHash = cloth.Params();
            var motion = cloth.GetComponent<SMDecorMotion>();
            if (motion == null) motion = cloth.gameObject.AddComponent<SMDecorMotion>();
            motion.Invalidate();
            EditorSceneManager.MarkSceneDirty(cloth.gameObject.scene);
        }

        // ------------------------------------------------------------------ materials

        static readonly string[] Pal = { "Forest", "Desert", "Snow", "Olive" };

        /// <summary>[sheet, leaf strips] materials for a cloth (shared per type + palette).</summary>
        public static Material[] Materials(SMDecorCloth c)
        {
            string pal = Pal[(int)c.palette];
            EnsureTextures();
            if (c.type == SMDecorClothType.CamoNet)
                return new[]
                {
                    Mat("Decor_Net_" + pal, m =>
                    {
                        m.SetTexture("_BaseColorMap", Tex("Net_" + pal));
                        m.SetFloat("_AlphaCutoffEnable", 1f); m.SetFloat("_AlphaCutoff", 0.5f);
                        m.SetFloat("_Smoothness", 0.15f);
                    }),
                    Mat("Decor_NetLeaf_" + pal, m =>
                    {
                        m.SetTexture("_BaseColorMap", Tex("Leaf_" + pal));
                        m.SetFloat("_AlphaCutoffEnable", 1f); m.SetFloat("_AlphaCutoff", 0.45f);
                        m.SetFloat("_Smoothness", 0.2f);
                    }),
                };
            bool tarp = c.type == SMDecorClothType.Tarp;
            var sheet = Mat((tarp ? "Decor_Tarp_" : "Decor_Cloth_") + pal, m =>
            {
                m.SetTexture("_BaseColorMap", Tex("Canvas_" + pal));
                m.SetTexture("_NormalMap", Tex("CanvasNormal"));
                m.SetFloat("_NormalScale", tarp ? 0.3f : 1f);
                m.SetFloat("_Smoothness", tarp ? 0.5f : 0.12f);
            });
            return new[] { sheet, sheet };
        }

        static Material Mat(string name, System.Action<Material> setup)
        {
            string path = TextureFolder + "/" + name + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            mat = new Material(SMCAD.Editor.SMMaterialSetup.FindShader("HDRP/Lit"));
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_DoubleSidedEnable", 1f);
            setup(mat);
            HDMaterial.ValidateMaterial(mat);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static Texture2D Tex(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>(TextureFolder + "/" + name + ".png");

        // ------------------------------------------------------------------ textures (made by code, once)

        static readonly Color[][] Shades =
        {
            new[] { C(0.21f, 0.25f, 0.13f), C(0.31f, 0.29f, 0.16f), C(0.13f, 0.15f, 0.09f), C(0.36f, 0.34f, 0.21f) },   // forest
            new[] { C(0.62f, 0.52f, 0.36f), C(0.50f, 0.42f, 0.28f), C(0.70f, 0.62f, 0.46f), C(0.42f, 0.35f, 0.24f) },   // desert
            new[] { C(0.88f, 0.89f, 0.90f), C(0.78f, 0.80f, 0.82f), C(0.92f, 0.92f, 0.90f), C(0.55f, 0.57f, 0.55f) },   // snow
            new[] { C(0.27f, 0.29f, 0.19f), C(0.24f, 0.26f, 0.17f), C(0.30f, 0.31f, 0.21f), C(0.20f, 0.22f, 0.14f) },   // olive
        };
        static readonly Color[] Cord = { C(0.16f, 0.17f, 0.11f), C(0.42f, 0.36f, 0.25f), C(0.75f, 0.76f, 0.76f), C(0.19f, 0.20f, 0.13f) };
        static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        public static void EnsureTextures()
        {
            if (Tex("CanvasNormal") != null && Pal.All(p => Tex("Net_" + p) != null && Tex("Leaf_" + p) != null && Tex("Canvas_" + p) != null)) return;
            Directory.CreateDirectory(TextureFolder);
            var made = new List<(string name, bool alpha, bool normal, bool clamp)>();
            for (int p = 0; p < Pal.Length; p++)
            {
                Save("Net_" + Pal[p], NetTex(Cord[p], p), true, false, false, made);
                Save("Leaf_" + Pal[p], LeafTex(Shades[p], p), true, false, true, made);
                Save("Canvas_" + Pal[p], CanvasTex(Shades[p][p == 1 ? 1 : 0], p), false, false, false, made);
            }
            Save("CanvasNormal", CanvasNormal(), false, true, false, made);
            AssetDatabase.Refresh();
            foreach (var (name, alpha, normal, clamp) in made)
            {
                var imp = (TextureImporter)AssetImporter.GetAtPath(TextureFolder + "/" + name + ".png");
                if (imp == null) continue;
                imp.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                imp.alphaIsTransparency = alpha;
                imp.wrapMode = clamp ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
                imp.mipmapEnabled = true;
                imp.SaveAndReimport();
            }
        }

        static void Save(string name, Texture2D t, bool alpha, bool normal, bool clamp, List<(string, bool, bool, bool)> made)
        {
            File.WriteAllBytes(TextureFolder + "/" + name + ".png", t.EncodeToPNG());
            Object.DestroyImmediate(t);
            made.Add((name, alpha, normal, clamp));
        }

        static float Hash(int x, int y, int s)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + s * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / 16777215f;
        }

        static float Noise(float x, float y, int s)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            return Mathf.Lerp(Mathf.Lerp(Hash(xi, yi, s), Hash(xi + 1, yi, s), fx), Mathf.Lerp(Hash(xi, yi + 1, s), Hash(xi + 1, yi + 1, s), fx), fy);
        }

        /// <summary>One diamond of net per texture repeat: two diagonal cords with knots, alpha = cord.</summary>
        static Texture2D NetTex(Color cord, int seed)
        {
            const int S = 256;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, true);
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = (x + 0.5f) / S, v = (y + 0.5f) / S;
                    float a = Mathf.Repeat(u + v, 1f), b = Mathf.Repeat(u - v, 1f);
                    float da = Mathf.Min(a, 1f - a), db = Mathf.Min(b, 1f - b);
                    float wob = (Noise(u * 40f, v * 40f, seed) - 0.5f) * 0.012f;
                    float w = 0.022f + wob;
                    bool knot = da < 0.05f && db < 0.05f;
                    bool on = da < w || db < w || knot;
                    float shade = 0.75f + 0.35f * Noise(u * 64f, v * 64f, seed + 7);
                    px[x + S * y] = on ? new Color(cord.r * shade, cord.g * shade, cord.b * shade, 1f) : new Color(cord.r, cord.g, cord.b, 0f);
                }
            t.SetPixels(px); t.Apply();
            return t;
        }

        /// <summary>Four leaf strips side by side (incised, ragged edges, a few cuts), alpha = leaf.</summary>
        static Texture2D LeafTex(Color[] shades, int seed)
        {
            const int S = 256;
            var t = new Texture2D(S * 4, S, TextureFormat.RGBA32, true);
            var px = new Color[S * 4 * S];
            for (int k = 0; k < 4; k++)
            {
                var col = shades[k];
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        float u = (x + 0.5f) / S, v = (y + 0.5f) / S;      // u along the strip, v across
                        float along = 1f - Mathf.Pow(Mathf.Abs(2f * u - 1f), 2.5f);
                        float saw = Mathf.Repeat(u * (6f + 2f * k) + Hash(k, 0, seed) * 3f, 1f);
                        float notch = 0.62f + 0.38f * Mathf.Abs(2f * saw - 1f);
                        float edge = 0.42f * along * notch + (Noise(u * 30f, v * 6f, seed + k) - 0.5f) * 0.05f;
                        bool slit = Mathf.Abs(v - 0.5f) < 0.02f && Noise(u * 12f, k, seed + 3) > 0.6f;
                        bool on = Mathf.Abs(v - 0.5f) < edge && !slit;
                        float shade = 0.82f + 0.3f * Noise(u * 20f + k * 13f, v * 20f, seed + 11);
                        if (Mathf.Abs(v - 0.5f) < 0.015f) shade *= 0.85f;   // a crease down the middle
                        px[k * S + x + S * 4 * y] = new Color(col.r * shade, col.g * shade, col.b * shade, on ? 1f : 0f);
                    }
            }
            t.SetPixels(px); t.Apply();
            return t;
        }

        static Texture2D CanvasTex(Color baseCol, int seed)
        {
            const int S = 256;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, true);
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = (float)x / S, v = (float)y / S;
                    float blotch = Noise(u * 4f, v * 4f, seed) * 0.6f + Noise(u * 16f, v * 16f, seed + 1) * 0.4f;
                    float weave = 0.5f + 0.5f * Mathf.Sin(u * Mathf.PI * 2f * 64f) * Mathf.Sin(v * Mathf.PI * 2f * 64f);
                    float s = 0.8f + 0.3f * blotch + 0.06f * weave;
                    px[x + S * y] = new Color(baseCol.r * s, baseCol.g * s, baseCol.b * s, 1f);
                }
            t.SetPixels(px); t.Apply();
            return t;
        }

        static Texture2D CanvasNormal()
        {
            const int S = 256;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, true, true);
            var px = new Color[S * S];
            float Hh(int x, int y)
            {
                float u = (float)x / S, v = (float)y / S;
                // plain weave: warp and weft threads over / under
                float warp = Mathf.Sin(u * Mathf.PI * 2f * 32f), weft = Mathf.Sin(v * Mathf.PI * 2f * 32f);
                return 0.5f * (warp * Mathf.Sign(Mathf.Sin(v * Mathf.PI * 32f)) + weft * Mathf.Sign(Mathf.Sin(u * Mathf.PI * 32f))) * 0.5f
                       + (Noise(u * 8f, v * 8f, 5) - 0.5f) * 0.6f;
            }
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = Hh((x + 1) % S, y) - Hh((x - 1 + S) % S, y), dy = Hh(x, (y + 1) % S) - Hh(x, (y - 1 + S) % S);
                    var n = new Vector3(-dx * 0.6f, -dy * 0.6f, 1f).normalized;
                    px[x + S * y] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            t.SetPixels(px); t.Apply();
            return t;
        }
    }
}
