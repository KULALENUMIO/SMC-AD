// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations (chains, camouflage nets, cloth): the set on the blueprint root.
// Holds the distance field of the vehicle (unsigned distance to the nearest surface, in the root's local space),
// used to drape cloth / chains in the editor and to keep them off the body when they move at run time.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SMCAD.Details.Decor
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("SMC-AD/SMC-DETAILS/装備")]
    public class SMDecorSet : MonoBehaviour
    {
        [Header("距離場（自動）")]
        [Tooltip("機体の表面までの距離 (m)。機体の内側は負。root のローカル座標の格子")] public Texture3D field;
        public Vector3 gridMin;
        public Vector3Int dims;
        public float voxel = 0.025f;
        [Tooltip("これより遠い所は、この値で打ち切り (m)")] public float maxDistance = 0.4f;
        [Tooltip("距離場を作ったときの各部位の姿勢（root から見た行列）。砲塔を回した後は作り直しが要る")]
        public List<PartPose> poses = new List<PartPose>();
        [Tooltip("距離場を作ったときの重力の向き（root のローカル座標）")] public Vector3 down = Vector3.down;
        [Tooltip("機体の一番下（down 方向の深さ）。布・鎖はこれより下へ落ちない")] public float floor;
        [Tooltip("生成したファイルの置き場（機体ごと）")] public string assetFolder;

        public List<SMDecoration> items = new List<SMDecoration>();

        [Serializable]
        public class PartPose
        {
            public Transform part;
            public Matrix4x4 rootFromPart;
        }

        public bool HasField => field != null && dims.x > 0;
        public Vector3 GridSize => (Vector3)dims * voxel;

        [NonSerialized] float[] cache;
        [NonSerialized] Texture3D cacheOf;

        /// <summary>Signed distance (m) to the vehicle at a point in the root's local space (negative inside the body; trilinear;
        /// maxDistance outside the grid).</summary>
        public float Distance(Vector3 local)
        {
            if (cache == null || !ReferenceEquals(cacheOf, field))
            {
                if (!HasField) return maxDistance;
                var raw = field.GetPixelData<ushort>(0);
                cache = new float[raw.Length];
                for (int i = 0; i < raw.Length; i++) cache[i] = Mathf.HalfToFloat(raw[i]);
                cacheOf = field;
            }
            Vector3 g = (local - gridMin) / voxel - Vector3.one * 0.5f;
            int x0 = Mathf.FloorToInt(g.x), y0 = Mathf.FloorToInt(g.y), z0 = Mathf.FloorToInt(g.z);
            if (x0 < 0 || y0 < 0 || z0 < 0 || x0 + 1 >= dims.x || y0 + 1 >= dims.y || z0 + 1 >= dims.z) return maxDistance;
            float fx = g.x - x0, fy = g.y - y0, fz = g.z - z0;
            float V(int x, int y, int z) => cache[x + dims.x * (y + dims.y * z)];
            float c00 = Mathf.Lerp(V(x0, y0, z0), V(x0 + 1, y0, z0), fx);
            float c10 = Mathf.Lerp(V(x0, y0 + 1, z0), V(x0 + 1, y0 + 1, z0), fx);
            float c01 = Mathf.Lerp(V(x0, y0, z0 + 1), V(x0 + 1, y0, z0 + 1), fx);
            float c11 = Mathf.Lerp(V(x0, y0 + 1, z0 + 1), V(x0 + 1, y0 + 1, z0 + 1), fx);
            return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
        }

        /// <summary>Distance and the direction away from the surface in one lookup (trilinear value + its analytic
        /// gradient; falls back to central differences where the cell is flat). Much cheaper than Distance + Gradient.</summary>
        public float Sample(Vector3 local, out Vector3 grad)
        {
            grad = Vector3.up;
            if (cache == null || !ReferenceEquals(cacheOf, field)) { Distance(local); if (cache == null) return maxDistance; }
            Vector3 g = (local - gridMin) / voxel - Vector3.one * 0.5f;
            int x0 = Mathf.FloorToInt(g.x), y0 = Mathf.FloorToInt(g.y), z0 = Mathf.FloorToInt(g.z);
            if (x0 < 0 || y0 < 0 || z0 < 0 || x0 + 1 >= dims.x || y0 + 1 >= dims.y || z0 + 1 >= dims.z) return maxDistance;
            float fx = g.x - x0, fy = g.y - y0, fz = g.z - z0;
            int sx = 1, sy = dims.x, sz = dims.x * dims.y;
            int i0 = x0 + sy * y0 + sz * z0;
            float v000 = cache[i0], v100 = cache[i0 + sx], v010 = cache[i0 + sy], v110 = cache[i0 + sx + sy];
            float v001 = cache[i0 + sz], v101 = cache[i0 + sx + sz], v011 = cache[i0 + sy + sz], v111 = cache[i0 + sx + sy + sz];
            float c00 = v000 + (v100 - v000) * fx, c10 = v010 + (v110 - v010) * fx;
            float c01 = v001 + (v101 - v001) * fx, c11 = v011 + (v111 - v011) * fx;
            float c0 = c00 + (c10 - c00) * fy, c1 = c01 + (c11 - c01) * fy;
            float d = c0 + (c1 - c0) * fz;
            float gx = Mathf.Lerp(Mathf.Lerp(v100 - v000, v110 - v010, fy), Mathf.Lerp(v101 - v001, v111 - v011, fy), fz);
            float gy = Mathf.Lerp(c10 - c00, c11 - c01, fz);
            float gz = c1 - c0;
            var gv = new Vector3(gx, gy, gz);
            grad = gv.sqrMagnitude > 1e-12f ? gv.normalized : Gradient(local);
            return d;
        }

        /// <summary>Direction away from the nearest surface (gradient of the field) at a local point.</summary>
        public Vector3 Gradient(Vector3 local)
        {
            float h = voxel;
            var g = new Vector3(Distance(local + Vector3.right * h) - Distance(local - Vector3.right * h),
                                Distance(local + Vector3.up * h) - Distance(local - Vector3.up * h),
                                Distance(local + Vector3.forward * h) - Distance(local - Vector3.forward * h));
            return g.sqrMagnitude > 1e-12f ? g.normalized : Vector3.up;
        }

        /// <summary>Whether a part has moved / turned since the field was built (the field is then out of date there).</summary>
        public bool PoseChanged(Transform part)
        {
            foreach (var p in poses)
            {
                if (p.part != part) continue;
                var now = transform.worldToLocalMatrix * part.localToWorldMatrix;
                for (int i = 0; i < 16; i++) if (Mathf.Abs(now[i] - p.rootFromPart[i]) > 1e-3f) return true;
                return false;
            }
            return false;
        }

        void OnValidate() { cacheOf = null; }
    }
}
