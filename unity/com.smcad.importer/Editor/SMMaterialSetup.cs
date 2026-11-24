// SPDX-License-Identifier: MIT
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace SMCAD.Editor
{
    /// <summary>
    /// Keeps an SM Lit / SM Glass material (HDRP Shader Graphs) in a valid HDRP state after its SM settings change:
    /// alpha clipping follows the SM alpha mode, the SMC-DETAILS keyword follows its toggle, then HDRP sets its own
    /// keywords, passes and render queue.
    /// </summary>
    public static class SMMaterialSetup
    {
        public const string LitShader = "SMC-AD/SM Lit";
        public const string GlassShader = "SMC-AD/SM Glass";

        public const string DetailsShader = "SMC-AD/Details Lit";

        static readonly System.Collections.Generic.Dictionary<string, string> GraphPaths = new System.Collections.Generic.Dictionary<string, string>
        {
            { LitShader, "Packages/com.smcad.importer/Runtime/Shaders/SM Lit.shadergraph" },
            { GlassShader, "Packages/com.smcad.importer/Runtime/Shaders/SM Glass.shadergraph" },
            { DetailsShader, "Packages/com.smcad.details/Runtime/Shaders/Details Lit.shadergraph" },
        };

        /// <summary>Shader.Find only sees shaders that are already loaded; a package Shader Graph nothing has used yet
        /// (fresh project, batch mode) is not found by name, so it is loaded from its file.</summary>
        public static Shader FindShader(string name)
        {
            var s = Shader.Find(name);
            if (s == null && GraphPaths.TryGetValue(name, out var path)) s = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(path);
            return s;
        }

        public static bool IsSM(Material m) => m != null && m.shader != null && (m.shader.name == LitShader || m.shader.name == GlassShader);

        /// <summary>The SM side only (no HDRP validation): used by the material inspector before HDRP validates.</summary>
        public static void SyncSM(Material m)
        {
            if (m == null) return;
            bool lit = m.shader != null && m.shader.name == LitShader;
            if (lit && m.HasProperty("_AlphaMode"))
                HDMaterial.SetAlphaClipping(m, m.GetFloat("_AlphaMode") > 0.5f);
            if (m.HasProperty("_SmdEnabled"))
            {
                bool on = m.GetFloat("_SmdEnabled") > 0.5f;
                if (on != m.IsKeywordEnabled("_SMCAD_DETAILS"))
                {
                    if (on) m.EnableKeyword("_SMCAD_DETAILS"); else m.DisableKeyword("_SMCAD_DETAILS");
                }
            }
        }

        /// <summary>SM side, then HDRP's own validation (keywords, passes, render queue).</summary>
        public static void Validate(Material m)
        {
            if (m == null) return;
            SyncSM(m);
            EnsureHdrpShaderTag();
            if (!HDMaterial.ValidateMaterial(m))
                Debug.LogWarning("[SMC-AD] HDRP の設定ができませんでした（HDRP が有効か確認してください）: " + m.name, m);
        }

        // Before HDRP has rendered once (batch mode, first import after opening), Shader.globalRenderPipeline is empty,
        // so the graph's HDRP subshader is not active and HDRP's validation sees only the fallback (and skips the
        // material). HDRP sets the same tag when it starts.
        static void EnsureHdrpShaderTag()
        {
            if (string.IsNullOrEmpty(Shader.globalRenderPipeline) && GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset)
                Shader.globalRenderPipeline = "HDRenderPipeline";
        }
    }
}
