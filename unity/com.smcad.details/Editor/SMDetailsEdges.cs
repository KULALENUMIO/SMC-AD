// SPDX-License-Identifier: MIT
// SMC-DETAILS for Unity: exact part edges.
// Every triangle edge is tested with two short rays against the whole blueprint (rest pose, temporary colliders):
//   - along the surface, just above it, outwards across the edge: hits a wall  -> concave edge (a corner)
//   - down onto the surface just beyond the edge: hits a surface              -> the surface continues (no edge)
//   - neither                                                                 -> convex edge (the surface drops away)
// The meshes are copied with one vertex per triangle corner, so each triangle can carry, per edge, the distance to
// that edge as a value that is linear over the triangle (corner k: height h_k for the edge opposite it, 0 for the
// others). UV4.xyz = convex edges, UV7.xyz = concave edges, 10 = not an edge. UV5 / UV6 = rest position / normal.
// Which side of a triangle is "outside" comes from the shape grid (the side that sees more directions), because SM
// meshes have unreliable normals and winding.
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace SMCAD.Details.Editor
{
    public static class SMDetailsEdges
    {
        public const float NoEdge = 10f;
        const float Across = 0.012f;   // how far beyond the edge the surface is looked for (m)
        const float Above = 0.006f;    // height of the probes above the surface (m)

        static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return 31;
        }

        public static List<Mesh> Build(List<(Mesh mesh, Matrix4x4 m, Vector3[] pos)> bodies, List<Vector3[]> restNormals,
                                       Texture3D shape, Vector3 gridMin, Vector3 gridSize, out string info)
        {
            int layer = FreeLayer();
            var temp = new List<Object>();
            bool backfaces = Physics.queriesHitBackfaces;
            var soups = new List<Mesh>();
            int convex = 0, concave = 0, flat = 0;
            float voxel = gridSize.x / Mathf.Max(1, shape.width);
            float Vis(Vector3 p)
            {
                var u = new Vector3((p.x - gridMin.x) / gridSize.x, (p.y - gridMin.y) / gridSize.y, (p.z - gridMin.z) / gridSize.z);
                return shape.GetPixelBilinear(u.x, u.y, u.z).r;
            }
            try
            {
                // temporary colliders in the rest frame, on a layer of their own
                foreach (var b in bodies)
                {
                    var cm = new Mesh { indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
                    cm.vertices = b.pos;
                    cm.triangles = b.mesh.triangles;
                    var go = new GameObject("SMD_EdgeProbe") { hideFlags = HideFlags.HideAndDontSave, layer = layer };
                    go.AddComponent<MeshCollider>().sharedMesh = cm;
                    temp.Add(go); temp.Add(cm);
                }
                Physics.SyncTransforms();
                Physics.queriesHitBackfaces = true;
                var query = new QueryParameters(1 << layer, false, QueryTriggerInteraction.Ignore, true);

                foreach (var (b, bodyIndex) in bodies.Select((b, i) => (b, i)))
                {
                    var mesh = b.mesh;
                    var pos = b.pos;
                    int subCount = mesh.subMeshCount;
                    var subTris = new List<int[]>();
                    for (int sm = 0; sm < subCount; sm++) subTris.Add(mesh.GetTriangles(sm));
                    int triCount = subTris.Sum(t => t.Length) / 3;

                    // probes: 2 rays per edge, 3 edges per triangle
                    var cmds = new NativeArray<RaycastCommand>(triCount * 6, Allocator.TempJob);
                    var hits = new NativeArray<RaycastHit>(triCount * 6, Allocator.TempJob);
                    var h = new float[triCount * 3];
                    var valid = new bool[triCount];
                    int t = 0;
                    foreach (var tris in subTris)
                    {
                        for (int i = 0; i < tris.Length; i += 3, t++)
                        {
                            var P = new[] { pos[tris[i]], pos[tris[i + 1]], pos[tris[i + 2]] };
                            var n = Vector3.Cross(P[1] - P[0], P[2] - P[0]);
                            float area2 = n.magnitude;
                            valid[t] = area2 > 1e-9f;
                            for (int k = 0; k < 6; k++) cmds[t * 6 + k] = new RaycastCommand(Vector3.zero, Vector3.up, query, 0f);
                            if (!valid[t]) continue;
                            n /= area2;
                            var c = (P[0] + P[1] + P[2]) / 3f;
                            if (Vis(c - n * voxel * 1.6f) > Vis(c + n * voxel * 1.6f)) n = -n;   // outside = the side that sees more
                            for (int k = 0; k < 3; k++)
                            {
                                var a = P[(k + 1) % 3];
                                var e = P[(k + 2) % 3] - a;
                                float len = e.magnitude;
                                h[t * 3 + k] = len > 1e-6f ? Vector3.Cross(e, P[k] - a).magnitude / len : 0f;
                                var mid = a + e * 0.5f;
                                var o = Vector3.Cross(e, n).normalized;
                                if (Vector3.Dot(o, P[k] - mid) > 0f) o = -o;   // away from the triangle
                                cmds[t * 6 + k * 2] = new RaycastCommand(mid + n * Above - o * 0.002f, o, query, Across * 1.6f);
                                cmds[t * 6 + k * 2 + 1] = new RaycastCommand(mid + o * Across + n * Above, -n, query, Above * 2f);
                            }
                        }
                    }
                    RaycastCommand.ScheduleBatch(cmds, hits, 256, 1).Complete();

                    // flags: 0 no edge, 1 convex, 2 concave
                    var flags = new byte[triCount * 3];
                    for (t = 0; t < triCount; t++)
                    {
                        if (!valid[t]) continue;
                        for (int k = 0; k < 3; k++)
                        {
                            bool wall = hits[t * 6 + k * 2].colliderInstanceID != 0;
                            bool cont = hits[t * 6 + k * 2 + 1].colliderInstanceID != 0;
                            byte f = wall ? (byte)2 : cont ? (byte)0 : (byte)1;
                            flags[t * 3 + k] = f;
                            if (f == 1) convex++; else if (f == 2) concave++; else flat++;
                        }
                    }
                    cmds.Dispose();
                    hits.Dispose();
                    soups.Add(MakeSoup(mesh, subTris, pos, restNormals[bodyIndex], flags, h));
                }
            }
            finally
            {
                Physics.queriesHitBackfaces = backfaces;
                foreach (var o in temp) if (o != null) Object.DestroyImmediate(o);
            }
            int total = Mathf.Max(1, convex + concave + flat);
            info = $"出っ張った角 {convex * 100 / total}%・へこんだ角 {concave * 100 / total}%・平ら {flat * 100 / total}%";
            return soups;
        }

        static Mesh MakeSoup(Mesh src, List<int[]> subTris, Vector3[] rest, Vector3[] restN, byte[] flags, float[] h)
        {
            int corners = subTris.Sum(t => t.Length);
            var v = src.vertices;
            var n = src.normals;
            var tg = src.tangents;
            var col = src.colors;
            var uvs = new List<(int ch, List<Vector4> data)>();
            for (int ch = 0; ch < 4; ch++)
            {
                if (!src.HasVertexAttribute(VertexAttribute.TexCoord0 + ch)) continue;
                var l = new List<Vector4>();
                src.GetUVs(ch, l);
                uvs.Add((ch, l));
            }
            var ov = new Vector3[corners];
            var on = n.Length > 0 ? new Vector3[corners] : null;
            var ot = tg.Length > 0 ? new Vector4[corners] : null;
            var oc = col.Length > 0 ? new Color[corners] : null;
            var ouv = uvs.Select(u => (u.ch, data: new List<Vector4>(corners))).ToList();
            var rp = new List<Vector3>(corners);
            var rn = new List<Vector3>(corners);
            var cx = new List<Vector4>(corners);
            var cc = new List<Vector3>(corners);
            var subIdx = new List<int[]>();
            int w = 0, t = 0;
            foreach (var tris in subTris)
            {
                var idx = new int[tris.Length];
                for (int i = 0; i < tris.Length; i += 3, t++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int s = tris[i + c];
                        ov[w] = v[s];
                        if (on != null) on[w] = n[s];
                        if (ot != null) ot[w] = tg[s];
                        if (oc != null) oc[w] = col[s];
                        for (int u = 0; u < ouv.Count; u++) ouv[u].data.Add(uvs[u].data[s]);
                        rp.Add(rest[s]);
                        rn.Add(restN[s]);
                        Vector3 vx = Vector3.zero, vc = Vector3.zero;
                        for (int k = 0; k < 3; k++)
                        {
                            byte f = flags[t * 3 + k];
                            float hk = h[t * 3 + k];
                            vx[k] = f == 1 ? (c == k ? hk : 0f) : NoEdge;
                            vc[k] = f == 2 ? (c == k ? hk : 0f) : NoEdge;
                        }
                        cx.Add(new Vector4(vx.x, vx.y, vx.z, 0f));
                        cc.Add(vc);
                        idx[i + c] = w;
                        w++;
                    }
                }
                subIdx.Add(idx);
            }
            var m = new Mesh { indexFormat = IndexFormat.UInt32 };
            m.vertices = ov;
            if (on != null) m.normals = on;
            if (ot != null) m.tangents = ot;
            if (oc != null) m.colors = oc;
            foreach (var u in ouv) m.SetUVs(u.ch, u.data);
            m.SetUVs(4, cx);
            m.SetUVs(5, rp);
            m.SetUVs(6, rn);
            m.SetUVs(7, cc);
            m.subMeshCount = subIdx.Count;
            for (int sm = 0; sm < subIdx.Count; sm++) m.SetTriangles(subIdx[sm], sm, false);
            m.RecalculateBounds();
            m.UploadMeshData(false);
            return m;
        }
    }
}
