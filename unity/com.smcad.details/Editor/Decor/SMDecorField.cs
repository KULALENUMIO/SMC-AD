// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: builds the vehicle's distance field on the GPU (SMDecorField.compute) and stores it on
// the SMDecorSet of the blueprint root (Texture3D RHalf, metres, in the root's local space).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    public static class SMDecorField
    {
        public static string LastReport = "";
        const int MaxDim = 512;
        const int TriChunk = 16384;

        /// <summary>Collects the vehicle's triangles in the root's local space (SM parts + attached parts such as tracks).</summary>
        public static (List<Vector3> verts, List<int> tris) Triangles(Transform root)
        {
            var renderers = SMDetailsBuilder.SMRenderers(root);
            foreach (var r in SMDetailsBuilder.AttachedRenderers(root)) if (!renderers.Contains(r)) renderers.Add(r);
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var toRoot = root.worldToLocalMatrix;
            foreach (var r in renderers)
            {
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null) continue;
                var m = toRoot * r.transform.localToWorldMatrix;
                int b = verts.Count;
                foreach (var v in mesh.vertices) verts.Add(m.MultiplyPoint3x4(v));
                foreach (var i in mesh.triangles) tris.Add(b + i);
            }
            return (verts, tris);
        }

        /// <summary>Builds (or rebuilds) the distance field. voxel in metres; maxDistance = where the field is clamped.</summary>
        public static SMDecorSet Build(Transform root, float voxel = 0.025f, float maxDistance = 0.4f, float padding = 0.3f)
        {
            var sw = Stopwatch.StartNew();
            var log = new List<string>();
            var set = root.GetComponent<SMDecorSet>() ?? Undo.AddComponent<SMDecorSet>(root.gameObject);
            Undo.RecordObject(set, "SMC-DETAILS 装備");

            var (verts, tris) = Triangles(root);
            if (tris.Count == 0) throw new InvalidOperationException("SM Lit のメッシュが見つかりません");
            Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
            foreach (var v in verts) { lo = Vector3.Min(lo, v); hi = Vector3.Max(hi, v); }
            lo -= Vector3.one * padding; hi += Vector3.one * padding;
            float vx = voxel;
            Vector3 size = hi - lo;
            while (Mathf.CeilToInt(size.x / vx) > MaxDim || Mathf.CeilToInt(size.y / vx) > MaxDim || Mathf.CeilToInt(size.z / vx) > MaxDim) vx *= 1.05f;
            var dims = new Vector3Int(Mathf.CeilToInt(size.x / vx), Mathf.CeilToInt(size.y / vx), Mathf.CeilToInt(size.z / vx));
            Vector3 gmin = (lo + hi) * 0.5f - (Vector3)dims * (vx * 0.5f);
            log.Add($"三角形 {tris.Count / 3:N0}、格子 {dims.x}×{dims.y}×{dims.z}（{vx * 100f:F2} cm）");

            var dist = Compute(verts, tris, gmin, dims, vx, maxDistance);
            log.Add($"距離場（GPU）: {sw.ElapsedMilliseconds} ms");
            int inside = SignInside(dist, dims, vx);
            log.Add($"内側（外から届かない所、負の距離）: {inside * 100.0 / dist.Length:F1} %: {sw.ElapsedMilliseconds} ms");

            var halves = new ushort[dist.Length];
            for (int i = 0; i < dist.Length; i++) halves[i] = Mathf.FloatToHalf(dist[i]);
            var tex = new Texture3D(dims.x, dims.y, dims.z, TextureFormat.RHalf, false)
            {
                name = root.name + "_DecorField", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            tex.SetPixelData(halves, 0);
            tex.Apply(false, false);

            string folder = Folder(set, root);
            tex = SMDetailsBuilder.SaveInPlace(tex, folder + "/DistanceField.asset");
            set.field = tex;
            set.gridMin = gmin;
            set.dims = dims;
            set.voxel = vx;
            set.maxDistance = maxDistance;
            set.down = root.InverseTransformDirection(Vector3.down).normalized;
            set.floor = verts.Max(v => Vector3.Dot(v, set.down));
            set.poses = SMDecorParts.All(root).Select(p => new SMDecorSet.PartPose
            {
                part = p.transform, rootFromPart = root.worldToLocalMatrix * p.transform.localToWorldMatrix,
            }).ToList();
            set.items.RemoveAll(x => x == null);
            EditorUtility.SetDirty(set);
            EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
            AssetDatabase.SaveAssets();
            log.Add($"部位: {string.Join("、", SMDecorParts.All(root).Select(p => p.label))}");
            log.Add($"合計: {sw.ElapsedMilliseconds} ms");
            LastReport = string.Join("\n", log);
            UnityEngine.Debug.Log("[SMC-DETAILS 装備] " + root.name + "\n" + LastReport);
            return set;
        }

        /// <summary>The distance field on the GPU: exact distances in a thin band around the triangles, then jump flooding.</summary>
        public static float[] Compute(List<Vector3> verts, List<int> tris, Vector3 gmin, Vector3Int dims, float vx, float maxDistance)
        {
            var cs = AssetDatabase.LoadAssetAtPath<ComputeShader>(SMDetailsBuilder.PackageRoot + "/Editor/Shaders/SMDecorField.compute");
            if (cs == null) throw new InvalidOperationException("SMDecorField.compute が読み込めません");
            int count = dims.x * dims.y * dims.z;
            int triCount = tris.Count / 3;
            var vb = new ComputeBuffer(verts.Count, 12);
            var ib = new ComputeBuffer(tris.Count, 4);
            var db = new ComputeBuffer(count, 4);
            var pa = new ComputeBuffer(count, 16);
            var pb = new ComputeBuffer(count, 16);
            var ob = new ComputeBuffer(count, 4);
            try
            {
                vb.SetData(verts);
                ib.SetData(tris);
                cs.SetInts("_Dims", dims.x, dims.y, dims.z);
                cs.SetVector("_GridMin", gmin);
                cs.SetFloat("_Voxel", vx);
                cs.SetFloat("_Band", vx * 1.5f);
                cs.SetFloat("_MaxDist", maxDistance);
                int gx = (dims.x + 3) / 4, gy = (dims.y + 3) / 4, gz = (dims.z + 3) / 4;

                int kClear = cs.FindKernel("Clear");
                cs.SetBuffer(kClear, "_Dist", db);
                cs.SetBuffer(kClear, "_Pnt", pa);
                cs.Dispatch(kClear, gx, gy, gz);

                foreach (var name in new[] { "SeedDist", "SeedPoint" })
                {
                    int k = cs.FindKernel(name);
                    cs.SetBuffer(k, "_Verts", vb);
                    cs.SetBuffer(k, "_Tris", ib);
                    cs.SetBuffer(k, "_Dist", db);
                    if (name == "SeedPoint") cs.SetBuffer(k, "_Pnt", pa);
                    // in chunks of triangles so one dispatch never runs long (GPU watchdog)
                    for (int start = 0; start < triCount; start += TriChunk)
                    {
                        int n = Mathf.Min(TriChunk, triCount - start);
                        var sub = new ComputeBuffer(n * 3, 4);
                        sub.SetData(tris, start * 3, 0, n * 3);
                        cs.SetBuffer(k, "_Tris", sub);
                        cs.SetInt("_TriCount", n);
                        cs.Dispatch(k, (n + 63) / 64, 1, 1);
                        GL.Flush();
                        sub.Release();
                    }
                }

                int kJump = cs.FindKernel("Jump");
                int steps = 1;
                int cells = Mathf.CeilToInt(maxDistance / vx) + 2;
                while (steps * 2 <= cells) steps *= 2;
                var src = pa; var dst = pb;
                var schedule = new List<int>();
                for (int s = steps; s >= 1; s /= 2) schedule.Add(s);
                schedule.Add(1);   // JFA+1: fixes most of the remaining errors
                foreach (int s in schedule)
                {
                    cs.SetInt("_Step", s);
                    cs.SetBuffer(kJump, "_PntIn", src);
                    cs.SetBuffer(kJump, "_PntOut", dst);
                    cs.Dispatch(kJump, gx, gy, gz);
                    (src, dst) = (dst, src);
                }
                int kRes = cs.FindKernel("Resolve");
                cs.SetBuffer(kRes, "_PntIn", src);
                cs.SetBuffer(kRes, "_Out", ob);
                cs.Dispatch(kRes, gx, gy, gz);
                var data = new float[count];
                ob.GetData(data);
                return data;
            }
            finally
            {
                vb.Release(); ib.Release(); db.Release(); pa.Release(); pb.Release(); ob.Release();
            }
        }

        /// <summary>Makes the field signed: voxels that cannot be reached from the grid's border without crossing the
        /// surface (closer than about half a voxel) are inside the body and get a negative distance. SM parts are closed
        /// shells once the hidden faces between blocks are removed, so this works for solid blocks; real openings let
        /// the outside in, which is also right. Returns the number of inside voxels.</summary>
        public static int SignInside(float[] dist, Vector3Int dims, float vx)
        {
            int nx = dims.x, ny = dims.y, nz = dims.z, n = dist.Length;
            float wall = vx * 0.55f;
            var seen = new bool[n];
            var queue = new int[n];
            int head = 0, tail = 0;
            void Push(int i) { if (!seen[i] && dist[i] > wall) { seen[i] = true; queue[tail++] = i; } }
            for (int z = 0; z < nz; z++)
                for (int y = 0; y < ny; y++)
                    for (int x = 0; x < nx; x++)
                        if (x == 0 || y == 0 || z == 0 || x == nx - 1 || y == ny - 1 || z == nz - 1) Push(x + nx * (y + ny * z));
            int sxy = nx * ny;
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % nx, y = (i / nx) % ny, z = i / sxy;
                if (x > 0) Push(i - 1);
                if (x < nx - 1) Push(i + 1);
                if (y > 0) Push(i - nx);
                if (y < ny - 1) Push(i + nx);
                if (z > 0) Push(i - sxy);
                if (z < nz - 1) Push(i + sxy);
            }
            int inside = 0;
            for (int i = 0; i < n; i++)
                if (!seen[i] && dist[i] > wall) { dist[i] = -dist[i]; inside++; }
            return inside;
        }

        /// <summary>Decoration files live in "Decor" under the blueprint's SMC-DETAILS folder (or a folder of their own).</summary>
        public static string Folder(SMDecorSet set, Transform root)
        {
            string folder = set.assetFolder;
            if (string.IsNullOrEmpty(folder))
            {
                var details = root.GetComponent<SMDetails>();
                string baseFolder = details != null && !string.IsNullOrEmpty(details.assetFolder)
                    ? details.assetFolder
                    : SMDetailsBuilder.OutputRoot + "/" + Safe(root.name) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                folder = baseFolder + "/Decor";
            }
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();
            set.assetFolder = folder;
            return folder;
        }

        static string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
    }
}
