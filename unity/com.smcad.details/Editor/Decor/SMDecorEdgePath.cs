// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: following the vehicle's edges for a chain. The mouse snaps to the nearest feature edge
// (any fold or open rim, convex or concave, also the short facets of round parts); between two clicked points the
// path follows the edges (shortest way through the edge graph, Dijkstra).
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    public static class SMDecorEdgePath
    {
        public struct Snap
        {
            public int edge;
            public Vector3 point;     // root-local, on the edge
            public Vector3 outward;   // root-local
        }

        /// <summary>The feature edge nearest to the mouse on screen (within maxPx) that is not hidden behind the vehicle.</summary>
        public static bool Nearest(SMDecorEdgeGraph g, Transform root, Ray ray, out Snap snap, float maxPx = 10f)
        {
            snap = default;
            if (g == null || g.Count == 0) return false;
            var cands = new List<(float px, int e)>();
            var hidden = SMWorkView.TransformTest();   // parts left out of the work view
            var memo = hidden != null ? new Dictionary<Transform, bool>() : null;
            for (int e = 0; e < g.Count; e++)
            {
                if (hidden != null)
                {
                    var pt = g.part[e] != null ? g.part[e] : root;
                    if (!memo.TryGetValue(pt, out bool hid)) memo[pt] = hid = hidden(pt);
                    if (hid) continue;
                }
                float px = HandleUtility.DistanceToLine(root.TransformPoint(g.pos[g.ea[e]]), root.TransformPoint(g.pos[g.eb[e]]));
                if (px < maxPx) cands.Add((px, e));
            }
            cands.Sort((a, b) => a.px.CompareTo(b.px));
            for (int k = 0; k < cands.Count && k < 8; k++)
            {
                int e = cands[k].e;
                Vector3 a = root.TransformPoint(g.pos[g.ea[e]]), b = root.TransformPoint(g.pos[g.eb[e]]);
                var p = ClosestOnSegmentToRay(a, b, ray);
                var toP = p - ray.origin;
                if (SMDecorPick.Raycast(root, new Ray(ray.origin, toP.normalized), out var h) && h.distance < toP.magnitude - 0.02f) continue;
                snap = new Snap { edge = e, point = root.InverseTransformPoint(p), outward = g.outward[e] };
                return true;
            }
            return false;
        }

        public static Vector3 ClosestOnSegmentToRay(Vector3 a, Vector3 b, Ray ray)
        {
            var u = b - a; var v = ray.direction; var w0 = a - ray.origin;
            float A = Vector3.Dot(u, u), B = Vector3.Dot(u, v), C = Vector3.Dot(v, v), D = Vector3.Dot(u, w0), E = Vector3.Dot(v, w0);
            float den = A * C - B * B;
            float s = den > 1e-9f ? Mathf.Clamp01((B * E - C * D) / den) : 0f;
            return a + u * s;
        }

        /// <summary>The way along the edges from one snapped point to another: points (root-local) with their outward
        /// directions, both ends included. Null when the two are not connected through the edges.</summary>
        public static List<(Vector3 p, Vector3 o)> Between(SMDecorEdgeGraph g, Snap from, Snap to)
        {
            var result = new List<(Vector3, Vector3)>();
            if (from.edge == to.edge)
            {
                result.Add((from.point, from.outward)); result.Add((to.point, to.outward));
                return result;
            }
            // Dijkstra from the start point (virtual node -1) over the graph's points to the end point (virtual -2)
            int n = g.pos.Length;
            var dist = new Dictionary<int, float>();
            var prev = new Dictionary<int, int>();
            var heap = new Heap();
            void Relax(int v, float d, int from_)
            {
                if (dist.TryGetValue(v, out float old) && old <= d) return;
                dist[v] = d; prev[v] = from_;
                heap.Push(v, d);
            }
            Relax(g.ea[from.edge], Vector3.Distance(from.point, g.pos[g.ea[from.edge]]), -1);
            Relax(g.eb[from.edge], Vector3.Distance(from.point, g.pos[g.eb[from.edge]]), -1);
            int endA = g.ea[to.edge], endB = g.eb[to.edge];
            float best = float.MaxValue; int bestEnd = -1;
            while (heap.Count > 0)
            {
                var (v, d) = heap.Pop();
                if (dist.TryGetValue(v, out float cur) && d > cur) continue;
                if (d >= best) break;
                if (v == endA || v == endB)
                {
                    float total = d + Vector3.Distance(g.pos[v], to.point);
                    if (total < best) { best = total; bestEnd = v; }
                }
                if (g.byVertex.TryGetValue(v, out var es))
                    foreach (int e in es)
                    {
                        int w = g.Other(e, v);
                        Relax(w, d + Vector3.Distance(g.pos[v], g.pos[w]), v);
                    }
                if (g.links.TryGetValue(v, out var ls))
                    foreach (int w in ls)
                        Relax(w, d + Vector3.Distance(g.pos[v], g.pos[w]) + 0.002f, v);   // a bridge costs a little extra
                if (dist.Count > 400000) break;   // far apart: give up
            }
            if (bestEnd < 0) return null;
            var chainV = new List<int>();
            for (int v = bestEnd; v >= 0; v = prev[v]) chainV.Add(v);
            chainV.Reverse();
            result.Add((from.point, from.outward));
            for (int i = 0; i < chainV.Count; i++)
            {
                int v = chainV[i];
                result.Add((g.pos[v], VertexOutward(g, v, i > 0 ? chainV[i - 1] : -1, i + 1 < chainV.Count ? chainV[i + 1] : -1, from, to)));
            }
            result.Add((to.point, to.outward));
            return result;
        }

        static Vector3 VertexOutward(SMDecorEdgeGraph g, int v, int before, int after, Snap from, Snap to)
        {
            Vector3 sum = Vector3.zero;
            if (g.byVertex.TryGetValue(v, out var es))
                foreach (int e in es)
                {
                    int w = g.Other(e, v);
                    if (w == before || w == after) sum += g.outward[e];
                }
            if (sum.sqrMagnitude < 1e-8f && es != null)   // reached over a bridge: all of the point's own edges
                foreach (int e in es) sum += g.outward[e];
            if (before < 0) sum += from.outward;
            if (after < 0) sum += to.outward;
            return sum.sqrMagnitude > 1e-8f ? sum.normalized : from.outward;
        }

        /// <summary>A small binary min-heap (the .NET in Unity has no PriorityQueue).</summary>
        class Heap
        {
            readonly List<(int v, float d)> a = new List<(int, float)>();
            public int Count => a.Count;
            public void Push(int v, float d)
            {
                a.Add((v, d));
                int i = a.Count - 1;
                while (i > 0) { int p = (i - 1) / 2; if (a[p].d <= a[i].d) break; (a[p], a[i]) = (a[i], a[p]); i = p; }
            }
            public (int v, float d) Pop()
            {
                var top = a[0];
                a[0] = a[a.Count - 1]; a.RemoveAt(a.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < a.Count && a[l].d < a[m].d) m = l;
                    if (r < a.Count && a[r].d < a[m].d) m = r;
                    if (m == i) break;
                    (a[m], a[i]) = (a[i], a[m]); i = m;
                }
                return top;
            }
        }
    }
}
