// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: a camouflage net / cloth sheet / tarpaulin thrown over the vehicle. Four corner points on
// the body (part-local) define it; the sheet is a bit larger than the quad between them ("extra"), falls onto the
// body and settles (SMDecorClothGen). The cut mask (D3) is kept with it so it can be draped again.
using System.Collections.Generic;
using UnityEngine;

namespace SMCAD.Details.Decor
{
    public enum SMDecorClothType { CamoNet, Cloth, Tarp }
    public enum SMDecorPalette { Forest, Desert, Snow, Olive }

    public class SMDecorCloth : SMDecoration
    {
        public override SMDecorKind Kind => type == SMDecorClothType.CamoNet ? SMDecorKind.Net : SMDecorKind.Cloth;
        public override string Label => type == SMDecorClothType.CamoNet ? "カモネット" : type == SMDecorClothType.Tarp ? "防水布" : "布";

        public SMDecorClothType type = SMDecorClothType.CamoNet;
        [Tooltip("四隅（部位のローカル座標、クリックした順）")] public List<Vector3> corners = new List<Vector3>();
        [Tooltip("四隅の面の向き（部位のローカル座標）")] public List<Vector3> cornerNormals = new List<Vector3>();
        [Tooltip("四隅を固定する（紐で留めた感じ）。外すと範囲を決めるだけで、自由に垂れる")] public bool pinCorners = true;
        [Tooltip("余り: 四隅の間より、布をこれだけ大きくする（ゆったり垂れる）")] [Range(0f, 0.8f)] public float extra = 0.25f;
        [Tooltip("固さ（曲がりにくさ）")] [Range(0f, 1f)] public float stiffness = 0.5f;
        [Tooltip("厚み (m)。機体からこれだけ浮く")] [Range(0.002f, 0.05f)] public float thickness = 0.012f;
        [Tooltip("格子の細かさ (m)。小さいほど細かく垂れる（重くなる）")] [Range(0.02f, 0.12f)] public float cell = 0.05f;
        [Tooltip("色")] public SMDecorPalette palette = SMDecorPalette.Forest;
        [Tooltip("カモネット: 葉の飾りの量")] [Range(0f, 1f)] public float garnish = 0.7f;
        [Tooltip("カモネット: 網目の大きさ (m)")] [Range(0.04f, 0.2f)] public float meshSize = 0.1f;

        [Header("切り抜き")]
        [Tooltip("布の格子ごとの切り抜き（1 = 切った）。幅 cutW × 高さ cutH")] public byte[] cut = new byte[0];
        public int cutW, cutH;
        [Tooltip("切り口のほつれ（ぎざぎざ）")] [Range(0f, 1f)] public float fray = 0.4f;
        [Tooltip("布の外周もほつれさせる")] public bool frayBorder;

        [Header("格子（自動）")]
        public int gridU, gridV;          // points
        public float stepU, stepV;        // m between points
        public float uvTile = 1f;         // m per UV unit of the sheet
        [System.NonSerialized] public Vector3[] lastShape;   // root-local points of the last drape (quick re-mesh while cutting)

        public override string Params()
        {
            var sb = new System.Text.StringBuilder(base.Params());
            sb.Append('|').Append(type).Append('|').Append(pinCorners).Append('|').Append(extra).Append('|').Append(stiffness).Append('|').Append(thickness)
              .Append('|').Append(cell).Append('|').Append(palette).Append('|').Append(garnish).Append('|').Append(meshSize).Append('|').Append(fray).Append('|').Append(frayBorder)
              .Append('|').Append(cutW).Append('x').Append(cutH);
            foreach (var p in corners) sb.Append('|').Append(p.x).Append(',').Append(p.y).Append(',').Append(p.z);
            uint h = 2166136261;
            if (cut != null) foreach (var b in cut) { h ^= b; h *= 16777619; }
            sb.Append('|').Append(h);
            return sb.ToString();
        }

        [Header("結果（自動）")]
        public Vector2 size;
        public int particles;
        public float maxPenetration;
        public float buildSeconds;
    }
}
