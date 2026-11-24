// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: cutting cloths and nets. The cut lives in a mask over the sheet's grid cells, in the
// sheet's own (flat) metres, so it stays put when the sheet is draped again. Brush and polygon both write the mask.
using System.Collections.Generic;
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    public static class SMDecorCut
    {
        /// <summary>Ray against the sheet (not the leaf strips): the hit in world space and on the flat sheet (m).</summary>
        public static bool RaycastSheet(SMDecorCloth c, Ray ray, out Vector3 world, out Vector2 sheet)
        {
            world = default; sheet = default;
            if (c == null || c.mesh == null || c.mesh.subMeshCount == 0) return false;
            var m = c.transform.localToWorldMatrix;
            var v = c.mesh.vertices;
            var uv = new List<Vector2>(); c.mesh.GetUVs(0, uv);
            var t = c.mesh.GetTriangles(0);
            float best = float.PositiveInfinity;
            var w = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) w[i] = m.MultiplyPoint3x4(v[i]);
            for (int k = 0; k < t.Length; k += 3)
            {
                Vector3 a = w[t[k]], b = w[t[k + 1]], cc = w[t[k + 2]];
                Vector3 e1 = b - a, e2 = cc - a, p = Vector3.Cross(ray.direction, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-12f) continue;
                float inv = 1f / det;
                var s = ray.origin - a;
                float bu = Vector3.Dot(s, p) * inv;
                if (bu < 0f || bu > 1f) continue;
                var q = Vector3.Cross(s, e1);
                float bv = Vector3.Dot(ray.direction, q) * inv;
                if (bv < 0f || bu + bv > 1f) continue;
                float d = Vector3.Dot(e2, q) * inv;
                if (d <= 0f || d >= best) continue;
                best = d;
                world = ray.origin + ray.direction * d;
                sheet = (uv[t[k]] * (1f - bu - bv) + uv[t[k + 1]] * bu + uv[t[k + 2]] * bv) * c.uvTile;
            }
            return best < float.PositiveInfinity;
        }

        /// <summary>The mask at the sheet's current grid (resampled when the grid changed).</summary>
        static void EnsureMask(SMDecorCloth c)
        {
            int w = Mathf.Max(1, c.gridU - 1), h = Mathf.Max(1, c.gridV - 1);
            if (c.cut != null && c.cutW == w && c.cutH == h && c.cut.Length == w * h) return;
            var m = new byte[w * h];
            if (c.cut != null && c.cutW > 0 && c.cutH > 0 && c.cut.Length == c.cutW * c.cutH)
                for (int j = 0; j < h; j++)
                    for (int i = 0; i < w; i++)
                    {
                        int ci = Mathf.Clamp((int)((i + 0.5f) / w * c.cutW), 0, c.cutW - 1);
                        int cj = Mathf.Clamp((int)((j + 0.5f) / h * c.cutH), 0, c.cutH - 1);
                        m[i + w * j] = c.cut[ci + c.cutW * cj];
                    }
            c.cut = m; c.cutW = w; c.cutH = h;
        }

        static Vector2 CellCentre(SMDecorCloth c, int i, int j) => new Vector2((i + 0.5f) * c.stepU, (j + 0.5f) * c.stepV);

        /// <summary>Cut (or restore) the cells within radius (m) of a point on the flat sheet. Returns whether anything changed.</summary>
        public static bool Circle(SMDecorCloth c, Vector2 centre, float radius, bool cut)
        {
            EnsureMask(c);
            bool changed = false;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((centre.x - radius) / c.stepU) - 1), i1 = Mathf.Min(c.cutW - 1, Mathf.CeilToInt((centre.x + radius) / c.stepU) + 1);
            int j0 = Mathf.Max(0, Mathf.FloorToInt((centre.y - radius) / c.stepV) - 1), j1 = Mathf.Min(c.cutH - 1, Mathf.CeilToInt((centre.y + radius) / c.stepV) + 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    if ((CellCentre(c, i, j) - centre).sqrMagnitude > radius * radius) continue;
                    byte v = (byte)(cut ? 1 : 0);
                    if (c.cut[i + c.cutW * j] != v) { c.cut[i + c.cutW * j] = v; changed = true; }
                }
            return changed;
        }

        /// <summary>Cut (or restore) the cells inside a polygon on the flat sheet (m).</summary>
        public static bool Polygon(SMDecorCloth c, IList<Vector2> poly, bool cut)
        {
            if (poly.Count < 3) return false;
            EnsureMask(c);
            bool changed = false;
            for (int j = 0; j < c.cutH; j++)
                for (int i = 0; i < c.cutW; i++)
                {
                    if (!Inside(poly, CellCentre(c, i, j))) continue;
                    byte v = (byte)(cut ? 1 : 0);
                    if (c.cut[i + c.cutW * j] != v) { c.cut[i + c.cutW * j] = v; changed = true; }
                }
            return changed;
        }

        public static void Clear(SMDecorCloth c)
        {
            Undo.RecordObject(c, "切り抜きを戻す");
            c.cut = new byte[0]; c.cutW = c.cutH = 0;
            EditorUtility.SetDirty(c);
        }

        public static int CutCells(SMDecorCloth c)
        {
            int n = 0;
            if (c.cut != null) foreach (var b in c.cut) if (b != 0) n++;
            return n;
        }

        static bool Inside(IList<Vector2> poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }
    }
}
