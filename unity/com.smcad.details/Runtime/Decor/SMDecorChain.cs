// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: a chain hung along a ridge of the vehicle. The path (the ridge, possibly several joined at
// corners) is stored in the part's local space; hang points are spread along it at "spacing", the chain sags between
// them by "slack" and is settled against the body with the distance field (SMDecorChainGen).
using System.Collections.Generic;
using UnityEngine;

namespace SMCAD.Details.Decor
{
    public enum SMDecorHanger { None, Ring, Shackle }
    public enum SMDecorFinish { Steel, Rust, Paint }
    public enum SMDecorChainStyle { Lace, Curtain }

    public class SMDecorChain : SMDecoration
    {
        public override SMDecorKind Kind => SMDecorKind.Chain;

        [Tooltip("レース: 吊り点の間を垂れる　カーテン: 吊り点から下へ何本も垂れ下がり、先に鉄球")] public SMDecorChainStyle style = SMDecorChainStyle.Lace;
        [Tooltip("経路（部位のローカル座標）。角で折れる")] public List<Vector3> path = new List<Vector3>();
        [Tooltip("経路の点ごとの外向き（部位のローカル座標）")] public List<Vector3> outward = new List<Vector3>();
        [Tooltip("始点側を短くする長さ (m)")] [Min(0)] public float trimStart;
        [Tooltip("終点側を短くする長さ (m)")] [Min(0)] public float trimEnd;
        [Tooltip("吊り点の間隔 (m)：密度")] [Range(0.04f, 1.5f)] public float spacing = 0.5f;
        [Tooltip("たるみ：吊り点の間の距離に対する鎖の余り")] [Range(0f, 0.6f)] public float slack = 0.15f;
        [Tooltip("線径 (m)")] [Range(0.004f, 0.02f)] public float wire = 0.01f;
        [Tooltip("カーテン: 垂れ下がる長さ (m)")] [Range(0.05f, 2f)] public float dropLength = 0.5f;
        [Tooltip("カーテン: 長さのばらつき")] [Range(0f, 0.6f)] public float dropVariation = 0.15f;
        [Tooltip("カーテン: 先の鉄球の半径 (m)。0 = なし")] [Range(0f, 0.06f)] public float ballRadius = 0.025f;
        [Tooltip("吊り点の金具")] public SMDecorHanger hanger = SMDecorHanger.Ring;
        [Tooltip("仕上げ")] public SMDecorFinish finish = SMDecorFinish.Steel;
        [Tooltip("リンクのねじれの乱れ")] [Range(0f, 1f)] public float jitter = 0.3f;
        [Tooltip("機体に当たる所を計算で沿わせる")] public bool settle = true;

        [Header("結果（自動）")]
        public float length;
        public int links;
        public int hangPoints;
        [Tooltip("機体へのめり込みの最大 (m)")] public float maxPenetration;

        // run-time motion (SMDecorMotion): the settled particles in the part's space, links between them
        [HideInInspector] public Vector3[] motionRest;
        [HideInInspector] public bool[] motionPin;
        [HideInInspector] public int[] motionPrev, motionNext;
        [HideInInspector] public float[] motionLenPrev, motionLenNext, motionRadius;
        [HideInInspector] public Matrix4x4 fieldFromPart = Matrix4x4.identity;

        public override string Params()
        {
            var sb = new System.Text.StringBuilder(base.Params());
            sb.Append('|').Append(style).Append('|').Append(trimStart).Append('|').Append(trimEnd).Append('|').Append(spacing).Append('|').Append(slack)
              .Append('|').Append(wire).Append('|').Append(dropLength).Append('|').Append(dropVariation).Append('|').Append(ballRadius)
              .Append('|').Append(hanger).Append('|').Append(finish).Append('|').Append(jitter).Append('|').Append(settle);
            foreach (var p in path) sb.Append('|').Append(p.x).Append(',').Append(p.y).Append(',').Append(p.z);
            foreach (var o in outward) sb.Append('|').Append(o.x).Append(',').Append(o.y).Append(',').Append(o.z);
            return sb.ToString();
        }

        /// <summary>Length of the path (m) before trimming.</summary>
        public float PathLength
        {
            get
            {
                float L = 0f;
                for (int i = 1; i < path.Count; i++) L += Vector3.Distance(path[i - 1], path[i]);
                return L;
            }
        }

        /// <summary>The point at a distance s (m) along the path (part-local).</summary>
        public Vector3 PointAt(float s)
        {
            if (path.Count == 0) return Vector3.zero;
            for (int i = 1; i < path.Count; i++)
            {
                float seg = Vector3.Distance(path[i - 1], path[i]);
                if (s <= seg || i == path.Count - 1) return Vector3.Lerp(path[i - 1], path[i], seg > 0 ? Mathf.Clamp01(s / seg) : 0f);
                s -= seg;
            }
            return path[path.Count - 1];
        }
    }
}
