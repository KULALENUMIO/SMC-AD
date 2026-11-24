// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: the vehicle's ridge lines (convex edges and the rims of thin plates), where chains go.
// Vertices are welded by position (SM meshes repeat them per face), face normals are turned outwards with the signed
// distance field (SM vertex normals and windings are not reliable), and collinear ridge edges are merged into lines
// across block joints.
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SMCAD.Details.Decor;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    public class SMDecorRidge
    {
        public Vector3 a, b;          // root-local
        public Vector3 outward;       // root-local, away from the body (bisector of the two faces / outwards over a rim)
        public Transform part;
        public bool rim;
        public float Length => Vector3.Distance(a, b);
    }

    /// <summary>Every feature edge of the vehicle (convex and concave folds, open rims), for drawing a chain along
    /// any edge: welded points (root-local) and the edges between them.</summary>
    public class SMDecorEdgeGraph
    {
        public Vector3[] pos;
        public int[] ea, eb;
        public Vector3[] outward;     // away from the body
        public Transform[] part;
        public Dictionary<int, List<int>> byVertex = new Dictionary<int, List<int>>();
        /// <summary>Bridges where separate blocks meet: points a hair apart, and a point lying on another block's edge
        /// (T-junction) linked to that edge's ends. Without them the path would stop at every block.</summary>
        public Dictionary<int, List<int>> links = new Dictionary<int, List<int>>();
        public int Count => ea.Length;
        public int Other(int e, int v) => ea[e] == v ? eb[e] : ea[e];

        void Link(int a, int b)
        {
            if (a == b) return;
            if (!links.TryGetValue(a, out var l)) links[a] = l = new List<int>();
            if (!l.Contains(b)) l.Add(b);
            if (!links.TryGetValue(b, out l)) links[b] = l = new List<int>();
            if (!l.Contains(a)) l.Add(a);
        }

        /// <summary>Adds the bridges (gap = how far apart blocks may be, metres).</summary>
        public int BuildLinks(float gap = 0.012f)
        {
            links.Clear();
            float cell = 0.05f;
            Vector3Int C(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
            var pts = new Dictionary<Vector3Int, List<int>>();
            for (int v = 0; v < pos.Length; v++)
            {
                var c = C(pos[v]);
                if (!pts.TryGetValue(c, out var l)) pts[c] = l = new List<int>();
                l.Add(v);
            }
            var segs = new Dictionary<Vector3Int, List<int>>();
            for (int e = 0; e < Count; e++)
            {
                Vector3 a = pos[ea[e]], b = pos[eb[e]];
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / (cell * 0.5f)));
                Vector3Int last = new Vector3Int(int.MinValue, 0, 0);
                for (int i = 0; i <= steps; i++)
                {
                    var c = C(Vector3.Lerp(a, b, i / (float)steps));
                    if (c == last) continue;
                    last = c;
                    if (!segs.TryGetValue(c, out var l)) segs[c] = l = new List<int>();
                    if (l.Count == 0 || l[l.Count - 1] != e) l.Add(e);
                }
            }
            float g2 = gap * gap;
            for (int v = 0; v < pos.Length; v++)
            {
                var p = pos[v];
                var c = C(p);
                for (int x = -1; x <= 1; x++)
                    for (int y = -1; y <= 1; y++)
                        for (int z = -1; z <= 1; z++)
                        {
                            var k = new Vector3Int(c.x + x, c.y + y, c.z + z);
                            if (pts.TryGetValue(k, out var pl))
                                foreach (int w in pl)
                                    if (w > v && (pos[w] - p).sqrMagnitude < g2) Link(v, w);
                            if (segs.TryGetValue(k, out var sl))
                                foreach (int e in sl)
                                {
                                    int a = ea[e], b = eb[e];
                                    if (a == v || b == v) continue;
                                    Vector3 pa = pos[a], ab = pos[b] - pa;
                                    float L2 = ab.sqrMagnitude;
                                    if (L2 < 1e-10f) continue;
                                    float t = Vector3.Dot(p - pa, ab) / L2;
                                    if (t <= 0f || t >= 1f) continue;
                                    if ((pa + ab * t - p).sqrMagnitude < g2) { Link(v, a); Link(v, b); }
                                }
                        }
            }
            int n = 0;
            foreach (var l in links.Values) n += l.Count;
            return n / 2;
        }
    }

    public static class SMDecorRidges
    {
        public const float FeatureAngle = 15f;  // degrees: edges folding more than this can carry a chain
        public static SMDecorEdgeGraph LastGraph;
        public const float MinAngle = 25f;      // degrees between the faces
        public const float MinLength = 0.2f;    // metres
        public static string LastInfo = "";

        public static List<SMDecorRidge> Find(SMDecorSet set)
        {
            var sw = Stopwatch.StartNew();
            var root = set.transform;
            var renderers = SMDetailsBuilder.SMRenderers(root);
            foreach (var r in SMDetailsBuilder.AttachedRenderers(root)) if (!renderers.Contains(r)) renderers.Add(r);

            // 1 weld + faces
            var weld = new Dictionary<Vector3Int, int>();
            var pos = new List<Vector3>();
            var faceV = new List<int>();          // 3 per face
            var faceN = new List<Vector3>();
            var faceC = new List<Vector3>();
            var facePart = new List<Transform>();
            var toRoot = root.worldToLocalMatrix;
            var allParts = SMDecorParts.All(root);
            long tWeld = 0, tEdges = 0, tRidge = 0, tMerge = 0;
            foreach (var r in renderers)
            {
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null) continue;
                var m = toRoot * r.transform.localToWorldMatrix;
                var v = mesh.vertices;
                var ids = new int[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    var p = m.MultiplyPoint3x4(v[i]);
                    var key = new Vector3Int(Mathf.RoundToInt(p.x * 1000f), Mathf.RoundToInt(p.y * 1000f), Mathf.RoundToInt(p.z * 1000f));
                    if (!weld.TryGetValue(key, out int id)) { id = pos.Count; weld[key] = id; pos.Add(p); }
                    ids[i] = id;
                }
                var part = SMDecorParts.Resolve(allParts, root, r.transform).transform;
                var t = mesh.triangles;
                for (int i = 0; i < t.Length; i += 3)
                {
                    int a = ids[t[i]], b = ids[t[i + 1]], c = ids[t[i + 2]];
                    if (a == b || b == c || a == c) continue;
                    var n = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                    if (n.sqrMagnitude < 1e-12f) continue;
                    faceV.Add(a); faceV.Add(b); faceV.Add(c);
                    faceN.Add(n.normalized);
                    faceC.Add((pos[a] + pos[b] + pos[c]) / 3f);
                    facePart.Add(part);
                }
            }
            int faces = faceN.Count;
            tWeld = sw.ElapsedMilliseconds;

            // 2 edges -> faces
            var edges = new Dictionary<long, List<int>>(faceN.Count * 2, new MixHash());
            long Key(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            for (int f = 0; f < faces; f++)
                for (int k = 0; k < 3; k++)
                {
                    long key = Key(faceV[f * 3 + k], faceV[f * 3 + (k + 1) % 3]);
                    if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<int>(2);
                    list.Add(f);
                }

            tEdges = sw.ElapsedMilliseconds;
            // 3 outward normals (lazy): the side where the signed field is larger; plate = outside on both sides
            float h = set.voxel * 1.5f;
            var oriented = new sbyte[faces];   // 0 unknown, 1 kept, -1 flipped, 2 plate
            Vector3 OutN(int f, out bool plate)
            {
                if (oriented[f] == 0)
                {
                    float dp = set.Distance(faceC[f] + faceN[f] * h), dm = set.Distance(faceC[f] - faceN[f] * h);
                    oriented[f] = (sbyte)(dp > 0.5f * h && dm > 0.5f * h ? 2 : dp >= dm ? 1 : -1);
                }
                plate = oriented[f] == 2;
                return oriented[f] == -1 ? -faceN[f] : faceN[f];
            }

            // 4 ridge edges
            var ridgeEdges = new List<(int a, int b, Vector3 outward, Transform part, bool rim)>();
            var feat = new List<(int a, int b, Vector3 outward, Transform part)>();
            float cosMin = Mathf.Cos(MinAngle * Mathf.Deg2Rad);
            float cosFeat = Mathf.Cos(FeatureAngle * Mathf.Deg2Rad);
            foreach (var kv in edges)
            {
                int ia = (int)(kv.Key >> 32), ib = (int)(kv.Key & 0xFFFFFFFF);
                Vector3 pa = pos[ia], pb = pos[ib], mid = (pa + pb) * 0.5f, dir = (pb - pa).normalized;
                var fl = kv.Value;
                if (fl.Count == 2)
                {
                    var nA = OutN(fl[0], out bool plA); var nB = OutN(fl[1], out bool plB);
                    if (!plA && !plB && Vector3.Dot(nA, nB) < cosFeat && (nA + nB).sqrMagnitude > 1e-6f)
                        feat.Add((ia, ib, (nA + nB).normalized, facePart[fl[0]]));   // convex or concave fold
                    if (plA || plB) continue;
                    if (Vector3.Dot(nA, nB) > cosMin) continue;
                    if (Vector3.Dot(nA, faceC[fl[1]] - mid) > -1e-4f || Vector3.Dot(nB, faceC[fl[0]] - mid) > -1e-4f) continue;   // concave
                    var bis = (nA + nB).normalized;
                    if (set.Distance(mid + bis * h) < 0.5f * h) continue;   // something else right outside
                    ridgeEdges.Add((ia, ib, bis, facePart[fl[0]], false));
                }
                else if (fl.Count == 1)
                {
                    // rim of a thin plate: open on both sides of the face and beyond the edge
                    var n = OutN(fl[0], out bool plate);
                    var t = Vector3.Cross(dir, n).normalized;
                    if (Vector3.Dot(t, faceC[fl[0]] - mid) > 0f) t = -t;
                    if (set.Distance(mid + t * h) > 0.3f * h) feat.Add((ia, ib, (t + n * 0.5f).normalized, facePart[fl[0]]));   // open rim
                    if (!plate) continue;
                    if (set.Distance(mid + t * h) < 0.6f * h) continue;
                    ridgeEdges.Add((ia, ib, t, facePart[fl[0]], true));
                }
            }

            tRidge = sw.ElapsedMilliseconds;
            // 5 merge collinear neighbours into lines
            var byVertex = new Dictionary<int, List<int>>();
            for (int e = 0; e < ridgeEdges.Count; e++)
            {
                foreach (int v in new[] { ridgeEdges[e].a, ridgeEdges[e].b })
                {
                    if (!byVertex.TryGetValue(v, out var l)) byVertex[v] = l = new List<int>();
                    l.Add(e);
                }
            }
            var used = new bool[ridgeEdges.Count];
            var lines = new List<SMDecorRidge>();
            const float cosLine = 0.995f;   // ~6 degrees
            for (int e = 0; e < ridgeEdges.Count; e++)
            {
                if (used[e]) continue;
                used[e] = true;
                var re = ridgeEdges[e];
                Vector3 dir = (pos[re.b] - pos[re.a]).normalized;
                int endA = re.a, endB = re.b;
                Vector3 outSum = re.outward * Vector3.Distance(pos[re.a], pos[re.b]);
                for (int side = 0; side < 2; side++)
                {
                    int cur = side == 0 ? endB : endA;
                    Vector3 want = side == 0 ? dir : -dir;
                    while (true)
                    {
                        int next = -1;
                        if (byVertex.TryGetValue(cur, out var cand))
                            foreach (int c in cand)
                            {
                                if (used[c]) continue;
                                var ce = ridgeEdges[c];
                                if (ce.part != re.part || ce.rim != re.rim || Vector3.Dot(ce.outward, re.outward) < 0.8f) continue;
                                int other = ce.a == cur ? ce.b : ce.a;
                                if (Vector3.Dot((pos[other] - pos[cur]).normalized, want) < cosLine) continue;
                                next = c; break;
                            }
                        if (next < 0) break;
                        used[next] = true;
                        var ne = ridgeEdges[next];
                        int o = ne.a == cur ? ne.b : ne.a;
                        outSum += ne.outward * Vector3.Distance(pos[ne.a], pos[ne.b]);
                        cur = o;
                    }
                    if (side == 0) endB = cur; else endA = cur;
                }
                var line = new SMDecorRidge { a = pos[endA], b = pos[endB], outward = outSum.normalized, part = re.part, rim = re.rim };
                if (line.Length >= MinLength) lines.Add(line);
            }

            tMerge = sw.ElapsedMilliseconds;
            // 6 drop lines lying along a longer one (stacked blocks give duplicates): buckets by direction + where the
            //    infinite line passes the origin, so only near-identical lines are compared
            lines.Sort((x, y) => y.Length.CompareTo(x.Length));
            var keep = new List<SMDecorRidge>();
            var buckets = new Dictionary<(int, int, int, int, int, int), List<SMDecorRidge>>();
            (int, int, int, int, int, int) BucketKey(SMDecorRidge l, int ox, int oy, int oz)
            {
                var d = (l.b - l.a).normalized;
                if (d.x < -1e-4f || (Mathf.Abs(d.x) <= 1e-4f && (d.y < -1e-4f || (Mathf.Abs(d.y) <= 1e-4f && d.z < 0f)))) d = -d;
                var p0 = l.a - d * Vector3.Dot(l.a, d);
                return (Mathf.RoundToInt(d.x * 20f), Mathf.RoundToInt(d.y * 20f), Mathf.RoundToInt(d.z * 20f),
                        Mathf.FloorToInt(p0.x * 50f) + ox, Mathf.FloorToInt(p0.y * 50f) + oy, Mathf.FloorToInt(p0.z * 50f) + oz);
            }
            foreach (var l in lines)
            {
                bool dup = false;
                for (int ox = -1; ox <= 1 && !dup; ox++)
                    for (int oy = -1; oy <= 1 && !dup; oy++)
                        for (int oz = -1; oz <= 1 && !dup; oz++)
                            if (buckets.TryGetValue(BucketKey(l, ox, oy, oz), out var bl))
                                foreach (var k in bl)
                                    if (DistToSegment(l.a, k.a, k.b) < 0.01f && DistToSegment(l.b, k.a, k.b) < 0.01f) { dup = true; break; }
                if (dup) continue;
                keep.Add(l);
                var key = BucketKey(l, 0, 0, 0);
                if (!buckets.TryGetValue(key, out var list2)) buckets[key] = list2 = new List<SMDecorRidge>();
                list2.Add(l);
            }
            // the edge graph (only the points it uses)
            var remap = new Dictionary<int, int>();
            var gpos = new List<Vector3>();
            int Map(int v) { if (!remap.TryGetValue(v, out int m)) { m = gpos.Count; remap[v] = m; gpos.Add(pos[v]); } return m; }
            var graph = new SMDecorEdgeGraph
            {
                ea = new int[feat.Count], eb = new int[feat.Count], outward = new Vector3[feat.Count], part = new Transform[feat.Count],
            };
            for (int e = 0; e < feat.Count; e++)
            {
                graph.ea[e] = Map(feat[e].a); graph.eb[e] = Map(feat[e].b);
                graph.outward[e] = feat[e].outward; graph.part[e] = feat[e].part;
                foreach (int v in new[] { graph.ea[e], graph.eb[e] })
                {
                    if (!graph.byVertex.TryGetValue(v, out var l)) graph.byVertex[v] = l = new List<int>();
                    l.Add(e);
                }
            }
            graph.pos = gpos.ToArray();
            long tLinks = sw.ElapsedMilliseconds;
            int bridges = graph.BuildLinks();
            tLinks = sw.ElapsedMilliseconds - tLinks;
            LastGraph = graph;
            LastInfo = $"辺 {feat.Count:N0}（継ぎ {bridges:N0}, {tLinks} ms）、面 {faces:N0}、稜線の辺 {ridgeEdges.Count:N0} → 線 {keep.Count}（{sw.ElapsedMilliseconds} ms: 溶接 {tWeld}, 辺 {tEdges - tWeld}, 判定 {tRidge - tEdges}, 結合 {tMerge - tRidge}, 重複 {sw.ElapsedMilliseconds - tMerge}）";
            return keep;
        }

        /// <summary>long keys made of two vertex ids: the default hash (low ^ high) collides all the time.</summary>
        sealed class MixHash : IEqualityComparer<long>
        {
            public bool Equals(long a, long b) => a == b;
            public int GetHashCode(long k)
            {
                ulong z = (ulong)k + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return (int)(z ^ (z >> 31));
            }
        }

        public static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f));
            return Vector3.Distance(p, a + ab * t);
        }
    }
}
