// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SMCAD.Editor
{
    /// <summary>
    /// Moves materials made with the URP versions over to HDRP (the SMC-AD line is HDRP only from 0.4, when this package was still com.smcad.unity).
    /// The URP shaders are gone, so such materials show as pink; their saved values are still in the file and are kept:
    ///   SM Lit / SM Glass (URP .shader)  -> the SM Lit / SM Glass Shader Graphs (same property names)
    ///   Details Lit (URP, SMC-DETAILS)   -> Details Lit graph (_BaseMap / _BumpMap / _BumpScale renamed as in HDRP/Lit)
    ///   Universal Render Pipeline/Lit    -> HDRP/Lit (base map / colour, normal map, metallic, smoothness, emission, alpha clip)
    /// Materials are found by the shader GUID written in the .mat file, so it works while the shader is missing.
    /// SMC-DETAILS meshes made with the URP version keep their edge data in UV4: run 汚しを付ける again (HDRP reads UV1).
    /// </summary>
    public static class SMUrpToHdrp
    {
        const string SmLitGuid = "795f1ea75e634fd49808f621682afd7a";       // SMLit.shader (com.smcad.unity <= 0.3)
        const string SmGlassGuid = "7fd14735c68bd354081b6e0f3ee3ba55";     // SMGlass.shader
        const string DetailsLitGuid = "3bca763b11bd35f49bf57dbfe826df52";  // SMDetailsLit.shader (com.smcad.details 0.1)
        const string UrpLitGuid = "933532a4fcc9baf4fa0491de14d08ed7";      // Universal Render Pipeline/Lit

        [MenuItem("Tools/SMC-AD/URP のマテリアルを HDRP に変換")]
        static void Menu()
        {
            string report = ConvertAll("Assets");
            Debug.Log("[SMC-AD] " + report);
            EditorUtility.DisplayDialog("SMC-AD", report, "OK");
        }

        /// <summary>Converts every URP-era SM / Details / URP Lit material under a folder. Returns a short report.</summary>
        public static string ConvertAll(string folder)
        {
            int sm = 0, glass = 0, details = 0, lit = 0;
            var notes = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".mat")) continue;
                string text;
                try { text = File.ReadAllText(path); } catch { continue; }
                string shaderLine = text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("m_Shader:")) ?? "";
                var m = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (m == null) continue;
                if (shaderLine.Contains(SmLitGuid)) { Switch(m, SMMaterialSetup.LitShader, null); sm++; }
                else if (shaderLine.Contains(SmGlassGuid)) { Switch(m, SMMaterialSetup.GlassShader, null); glass++; }
                else if (shaderLine.Contains(DetailsLitGuid))
                {
                    if (SMMaterialSetup.FindShader(SMMaterialSetup.DetailsShader) == null) { notes.Add("Details Lit がありません（com.smcad.details を入れてください）: " + path); continue; }
                    Switch(m, "SMC-AD/Details Lit", Renames.Details); details++;
                }
                else if (shaderLine.Contains(UrpLitGuid)) { Switch(m, "HDRP/Lit", Renames.UrpLit); lit++; }
            }
            AssetDatabase.SaveAssets();
            var sb = new StringBuilder();
            sb.Append($"HDRP に変換: SM Lit {sm}、SM Glass {glass}、Details Lit {details}、URP Lit → HDRP/Lit {lit}");
            if (details > 0) sb.Append("\n汚し（SMC-DETAILS）を使っていた機体は「汚しを付ける」をやり直してください（頂点データの場所が変わりました）。");
            foreach (var n in notes) sb.Append("\n" + n);
            return sb.ToString();
        }

        enum Renames { Details, UrpLit }

        static void Switch(Material m, string shaderName, Renames? renames)
        {
            var shader = SMMaterialSetup.FindShader(shaderName);
            if (shader == null) { Debug.LogWarning("[SMC-AD] シェーダーが見つかりません: " + shaderName); return; }
            // read the saved values first: with a missing shader the Material API cannot see them
            var so = new SerializedObject(m);
            Texture Tex(string name) => Saved(so, "m_TexEnvs", name)?.FindPropertyRelative("m_Texture").objectReferenceValue as Texture;
            Vector2 Scale(string name) => Saved(so, "m_TexEnvs", name)?.FindPropertyRelative("m_Scale").vector2Value ?? Vector2.one;
            Vector2 Offset(string name) => Saved(so, "m_TexEnvs", name)?.FindPropertyRelative("m_Offset").vector2Value ?? Vector2.zero;
            float? Float(string name) => Saved(so, "m_Floats", name)?.floatValue;
            Color? Col(string name) => Saved(so, "m_Colors", name)?.colorValue;
            var baseMap = Tex("_BaseMap"); var bump = Tex("_BumpMap");
            var baseScale = Scale("_BaseMap"); var baseOffset = Offset("_BaseMap");
            float? bumpScale = Float("_BumpScale"), metallic = Float("_Metallic"), smooth = Float("_Smoothness");
            float? alphaClip = Float("_AlphaClip"), cutoff = Float("_Cutoff"), surface = Float("_Surface");
            Color? baseColor = Col("_BaseColor"), emission = Col("_EmissionColor");
            var emissionMap = Tex("_EmissionMap");

            Undo.RecordObject(m, "URP → HDRP");
            m.shader = shader;   // properties with the same names keep their values
            if (renames.HasValue)
            {
                if (baseMap) { m.SetTexture("_BaseColorMap", baseMap); m.SetTextureScale("_BaseColorMap", baseScale); m.SetTextureOffset("_BaseColorMap", baseOffset); }
                if (bump) m.SetTexture("_NormalMap", bump);
                if (bumpScale.HasValue) m.SetFloat("_NormalScale", bumpScale.Value);
                if (baseColor.HasValue) m.SetColor("_BaseColor", baseColor.Value);
                if (metallic.HasValue) m.SetFloat("_Metallic", metallic.Value);
                if (smooth.HasValue) m.SetFloat("_Smoothness", smooth.Value);
            }
            if (renames == Renames.UrpLit)
            {
                if (emission.HasValue && emission.Value.maxColorComponent > 0f)
                {
                    m.SetColor("_EmissiveColor", emission.Value);
                    if (emissionMap) m.SetTexture("_EmissiveColorMap", emissionMap);
                }
                if (alphaClip.HasValue && alphaClip.Value > 0.5f)
                {
                    HDMaterial.SetAlphaClipping(m, true);
                    if (cutoff.HasValue) m.SetFloat("_AlphaCutoff", cutoff.Value);
                }
                if (surface.HasValue && surface.Value > 0.5f) m.SetFloat("_SurfaceType", 1f);
                if (Saved(so, "m_TexEnvs", "_MetallicGlossMap")?.FindPropertyRelative("m_Texture").objectReferenceValue != null)
                    Debug.LogWarning("[SMC-AD] URP の金属マップは HDRP のマスクマップと形式が違うため移していません: " + m.name, m);
            }
            SMMaterialSetup.Validate(m);
            EditorUtility.SetDirty(m);
        }

        static SerializedProperty Saved(SerializedObject so, string list, string name)
        {
            var arr = so.FindProperty("m_SavedProperties." + list);
            if (arr == null) return null;
            for (int i = 0; i < arr.arraySize; i++)
            {
                var e = arr.GetArrayElementAtIndex(i);
                if (e.FindPropertyRelative("first").stringValue == name) return e.FindPropertyRelative("second");
            }
            return null;
        }
    }
}
