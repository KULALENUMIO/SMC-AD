// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: builds a chain.
//   1 the path (root-local), trimmed at both ends
//   2 hang points every "spacing" along each straight run (corners always), pushed off the edge by half a link
//   3 lace: between hang points a catenary (parabola) of length chord x (1 + slack), one particle per link;
//     curtain: a strand hanging from every hang point (length with some spread), an iron ball at its end
//   4 settled with position-based dynamics against the signed distance field (gravity, link length, contact, friction)
//   5 links: stadium-shaped rings around the particles, every other one turned 90 degrees, lying flat on the body
//   6 fittings at the hang points (weld ring / shackle)
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SMCAD.Details.Decor
{
    public static class SMDecorChainGen
    {
        const int CentreSamples = 16;
        const int Sides = 6;

        public struct Stats { public float length; public int links, hangPoints; public float maxPenetration; }

        /// <summary>rootFromPart: the part's pose in the root's space now; down: gravity in the root's space.</summary>
        public static Stats Build(SMDecorChain c, Matrix4x4 rootFromPart, Vector3 down, Mesh mesh)
        {
            var st = new Stats();
            var set = c.set;
            float w = Mathf.Max(0.002f, c.wire);
            float pitch = 3.4f * w;          // inner length of a link = centre-to-centre distance
            float rad = 1.3f * w;            // contact radius of a link around its centre
            float half = 1.7f * w;           // half the outer width: the first link sits this far off the edge
            down = down.normalized;
            var rng = new System.Random(c.seed);

            // 1 path
            var P = new List<Vector3>();
            var O = new List<Vector3>();
            for (int i = 0; i < c.path.Count; i++)
            {
                P.Add(rootFromPart.MultiplyPoint3x4(c.path[i]));
                var o = i < c.outward.Count ? c.outward[i] : -down;
                O.Add(rootFromPart.MultiplyVector(o).normalized);
            }
            var tp = new List<Vector3>(); var to = new List<Vector3>();
            if (P.Count >= 2)
            {
                float total = 0f;
                for (int i = 1; i < P.Count; i++) total += Vector3.Distance(P[i - 1], P[i]);
                float s0 = Mathf.Clamp(c.trimStart, 0f, total), s1 = Mathf.Clamp(total - c.trimEnd, s0, total);
                if (s1 - s0 > pitch * 3f)
                {
                    float acc = 0f;
                    for (int i = 1; i < P.Count; i++)
                    {
                        float seg = Vector3.Distance(P[i - 1], P[i]);
                        float a = Mathf.Max(s0, acc), b = Mathf.Min(s1, acc + seg);
                        if (b > a)
                        {
                            var pa = Vector3.Lerp(P[i - 1], P[i], (a - acc) / seg);
                            var pb = Vector3.Lerp(P[i - 1], P[i], (b - acc) / seg);
                            if (tp.Count == 0) { tp.Add(pa); to.Add(O[i - 1]); }
                            tp.Add(pb); to.Add(i < P.Count - 1 && b >= acc + seg - 1e-5f ? (O[i - 1] + O[i]).normalized : O[i - 1]);
                        }
                        acc += seg;
                    }
                }
            }
            if (tp.Count < 2) { mesh.Clear(); return st; }

            // 2 hang points
            var H = new List<Vector3>(); var HO = new List<Vector3>(); var HE = new List<Vector3>();
            for (int i = 1; i < tp.Count; i++)
            {
                float seg = Vector3.Distance(tp[i - 1], tp[i]);
                int n = Mathf.Max(1, Mathf.RoundToInt(seg / Mathf.Max(0.03f, c.spacing)));
                var e = (tp[i] - tp[i - 1]).normalized;
                for (int j = 0; j < n; j++)
                {
                    float t = (float)j / n;
                    var o = j == 0 ? to[i - 1] : Vector3.Slerp(to[i - 1], to[i], t);
                    H.Add(Vector3.Lerp(tp[i - 1], tp[i], t) + o * half); HO.Add(o); HE.Add(e);
                }
                if (i == tp.Count - 1) { H.Add(tp[i] + to[i] * half); HO.Add(to[i]); HE.Add(e); }
            }

            // 3 particles: lace = catenaries between the hang points; curtain = a strand hanging from every hang point
            var x = new List<Vector3>(); var pin = new List<bool>(); var prevI = new List<int>(); var nextI = new List<int>();
            var cons = new List<(int a, int b, float rest)>();
            var balls = new List<(int end, float r)>();
            void Add(Vector3 p, bool pinned, bool chained)
            {
                int i = x.Count;
                x.Add(p); pin.Add(pinned); prevI.Add(chained ? i - 1 : -1); nextI.Add(-1);
                if (chained) nextI[i - 1] = i;
            }
            if (c.style == SMDecorChainStyle.Lace)
            {
                float restPrev = pitch;
                for (int k = 0; k + 1 < H.Count; k++)
                {
                    var chord = H[k + 1] - H[k];
                    float cl = chord.magnitude;
                    float L = cl * (1f + Mathf.Max(0f, c.slack));
                    int n = Mathf.Max(2, Mathf.RoundToInt(L / pitch));
                    float sag = Mathf.Sqrt(Mathf.Max(0f, 3f * cl * (L - cl) / 8f));
                    for (int j = 0; j < n; j++)
                    {
                        float t = (float)j / n;
                        Add(H[k] + chord * t + down * (sag * 4f * t * (1f - t)), j == 0, x.Count > 0);
                        if (x.Count > 1) cons.Add((x.Count - 2, x.Count - 1, j == 0 ? restPrev : L / n));
                    }
                    restPrev = L / n;
                }
                Add(H[H.Count - 1], true, true);
                cons.Add((x.Count - 2, x.Count - 1, restPrev));
            }
            else
            {
                for (int k = 0; k < H.Count; k++)
                {
                    float L = Mathf.Max(pitch * 2f, c.dropLength * (1f + ((float)rng.NextDouble() * 2f - 1f) * c.dropVariation));
                    int n = Mathf.Max(2, Mathf.RoundToInt(L / pitch));
                    var hang = (HO[k] * 0.4f + down).normalized;   // first off the edge, then down
                    for (int j = 0; j <= n; j++)
                    {
                        Add(H[k] + hang * (j * pitch), j == 0, j > 0);
                        if (j > 0) cons.Add((x.Count - 2, x.Count - 1, pitch));
                    }
                    if (c.ballRadius > 0f) balls.Add((x.Count - 1, c.ballRadius));
                }
            }
            int N = x.Count;
            var radius = new float[N];
            for (int i = 0; i < N; i++) radius[i] = rad;
            foreach (var bl in balls) radius[bl.end] = Mathf.Max(rad, bl.r + 1.2f * w);

            // 4 settle
            if (c.settle && set != null && set.HasField)
            {
                var prev = new List<Vector3>(x);
                float dt = 1f / 60f;
                var g = down * (9.81f * dt * dt);
                int iterations = c.style == SMDecorChainStyle.Lace ? 400 : 300;
                for (int it = 0; it < iterations; it++)
                {
                    for (int i = 0; i < N; i++)
                    {
                        if (pin[i]) continue;
                        var v = (x[i] - prev[i]) * 0.9f;
                        prev[i] = x[i];
                        x[i] += v + g;
                    }
                    for (int pass = 0; pass < 8; pass++)
                        foreach (var (a, b, r0) in cons)
                        {
                            var d = x[b] - x[a];
                            float len = d.magnitude;
                            if (len < 1e-7f) continue;
                            float wa = pin[a] ? 0f : 1f, wb = pin[b] ? 0f : 1f;
                            if (wa + wb == 0f) continue;
                            var corr = d * ((len - r0) / (len * (wa + wb)));
                            x[a] += corr * wa;
                            x[b] -= corr * wb;
                        }
                    for (int i = 0; i < N; i++)
                    {
                        if (pin[i]) continue;
                        float d = set.Sample(x[i], out var grad);
                        if (d >= radius[i]) continue;
                        x[i] += grad * (radius[i] - d);
                        prev[i] = Vector3.Lerp(prev[i], x[i], 0.6f);   // friction
                    }
                }
            }

            // stats
            float length = 0f, pen = 0f;
            foreach (var (a, b, _) in cons) length += Vector3.Distance(x[a], x[b]);
            if (set != null && set.HasField)
                for (int i = 0; i < N; i++)
                    if (!pin[i]) pen = Mathf.Max(pen, (radius[i] > rad ? radius[i] - 1.2f * w : 0.5f * w) - set.Distance(x[i]));
            st.length = length; st.links = N; st.hangPoints = H.Count; st.maxPenetration = Mathf.Max(0f, pen);

            // 5 links + 6 fittings + balls, in the part's space
            var partFromRoot = rootFromPart.inverse;
            var V = new List<Vector3>(); var Nn = new List<Vector3>(); var I = new List<int>();
            var L2 = new List<Vector2>();   // UV2.x = the link (particle) a vertex moves with, -1 = fixed (D4 motion)
            void Tag(int from, int link) { while (L2.Count < V.Count) L2.Add(new Vector2(link, 0)); }
            Vector3 zPrev = Vector3.zero;
            for (int i = 0; i < N; i++)
            {
                int ia = prevI[i] >= 0 ? prevI[i] : i, ib = nextI[i] >= 0 ? nextI[i] : i;
                var axis = (x[ib] - x[ia]).normalized;
                if (axis.sqrMagnitude < 0.5f) axis = c.style == SMDecorChainStyle.Lace ? HE[0] : down;
                if (prevI[i] < 0) zPrev = Vector3.zero;   // a new strand
                var refN = set != null && set.HasField && set.Distance(x[i]) < 4f * rad ? set.Gradient(x[i]) : -down;
                var z = refN - axis * Vector3.Dot(refN, axis);
                if (z.sqrMagnitude < 1e-6f) z = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right);
                z.Normalize();
                if (zPrev != Vector3.zero && Vector3.Dot(z, zPrev) < 0f && (i & 1) == 0) z = -z;
                if ((i & 1) == 1) z = Vector3.Cross(axis, z);
                float jit = ((float)rng.NextDouble() - 0.5f) * 2f * c.jitter * 25f;
                z = Quaternion.AngleAxis(jit, axis) * z;
                if ((i & 1) == 0) zPrev = z;
                Link(V, Nn, I, x[i], axis, z, w, partFromRoot);
                Tag(0, i);
            }
            if (c.hanger != SMDecorHanger.None)
                for (int k = 0; k < H.Count; k++) Fitting(V, Nn, I, c.hanger, H[k] - HO[k] * half, HO[k], HE[k], w, partFromRoot);
            Tag(0, -1);
            foreach (var (end, r) in balls)
            {
                int p0 = prevI[end] >= 0 ? prevI[end] : end;
                var dir = (x[end] - x[p0]).sqrMagnitude > 1e-10f ? (x[end] - x[p0]).normalized : down;
                var side = Vector3.Cross(dir, Mathf.Abs(dir.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                Ring(V, Nn, I, new[] { x[end] + dir * (1.0f * w), x[end] + dir * (1.9f * w) }, false, 0.7f * w, side, partFromRoot);   // neck
                Ball(V, Nn, I, x[end] + dir * (1.6f * w + r), r, partFromRoot);
                Tag(0, end);
            }
            // rest state for the run-time motion (part space): the settled shape is the rest shape
            c.motionRest = new Vector3[N]; c.motionPin = new bool[N]; c.motionPrev = new int[N]; c.motionNext = new int[N];
            c.motionLenPrev = new float[N]; c.motionLenNext = new float[N]; c.motionRadius = new float[N];
            for (int i = 0; i < N; i++)
            {
                c.motionRest[i] = partFromRoot.MultiplyPoint3x4(x[i]);
                c.motionPin[i] = pin[i]; c.motionPrev[i] = prevI[i]; c.motionNext[i] = nextI[i];
                c.motionLenPrev[i] = prevI[i] >= 0 ? Vector3.Distance(x[i], x[prevI[i]]) : 0f;
                c.motionLenNext[i] = nextI[i] >= 0 ? Vector3.Distance(x[i], x[nextI[i]]) : 0f;
                c.motionRadius[i] = radius[i];
            }
            c.fieldFromPart = rootFromPart;

            mesh.Clear();
            mesh.indexFormat = V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(V);
            mesh.SetNormals(Nn);
            mesh.SetUVs(2, L2);
            mesh.SetTriangles(I, 0);
            mesh.RecalculateBounds();
            return st;
        }

        /// <summary>One stadium-shaped link around centre p: long axis "axis", lying in the plane whose normal is z.</summary>
        static void Link(List<Vector3> V, List<Vector3> Nn, List<int> I, Vector3 p, Vector3 axis, Vector3 z, float w, Matrix4x4 m)
        {
            float a = w * 1.0f, rc = w * 1.2f, r = w * 0.5f;
            var y = Vector3.Cross(z, axis);
            var cl = new Vector3[CentreSamples];
            for (int k = 0; k < CentreSamples; k++)
            {
                float th = k * Mathf.PI * 2f / CentreSamples;
                float cs = Mathf.Cos(th), sn = Mathf.Sin(th);
                float sx = cs > 1e-4f ? a : cs < -1e-4f ? -a : 0f;
                cl[k] = p + axis * (sx + rc * cs) + y * (rc * sn);
            }
            Ring(V, Nn, I, cl, true, r, z, m);
        }

        /// <summary>A tube along a closed / open centre line (sides around, frame from refN).</summary>
        static void Ring(List<Vector3> V, List<Vector3> Nn, List<int> I, IList<Vector3> cl, bool closed, float r, Vector3 refN, Matrix4x4 m)
        {
            int n = cl.Count, b = V.Count;
            for (int k = 0; k < n; k++)
            {
                var t = closed ? (cl[(k + 1) % n] - cl[(k - 1 + n) % n]) : (cl[Mathf.Min(k + 1, n - 1)] - cl[Mathf.Max(k - 1, 0)]);
                t.Normalize();
                var n1 = refN - t * Vector3.Dot(refN, t);
                if (n1.sqrMagnitude < 1e-8f) n1 = Vector3.Cross(t, Vector3.up);
                n1.Normalize();
                var n2 = Vector3.Cross(t, n1);
                for (int s = 0; s < Sides; s++)
                {
                    float ph = s * Mathf.PI * 2f / Sides;
                    var dir = n1 * Mathf.Cos(ph) + n2 * Mathf.Sin(ph);
                    V.Add(m.MultiplyPoint3x4(cl[k] + dir * r));
                    Nn.Add(m.MultiplyVector(dir).normalized);
                }
            }
            int segs = closed ? n : n - 1;
            for (int k = 0; k < segs; k++)
            {
                int k2 = (k + 1) % n;
                for (int s = 0; s < Sides; s++)
                {
                    int s2 = (s + 1) % Sides;
                    int a0 = b + k * Sides + s, a1 = b + k * Sides + s2, b0 = b + k2 * Sides + s, b1 = b + k2 * Sides + s2;
                    I.Add(a0); I.Add(b0); I.Add(a1);
                    I.Add(a1); I.Add(b0); I.Add(b1);
                }
            }
        }

        /// <summary>The fitting at a hang point on the edge p (o = away from the body, e = along the edge).</summary>
        static void Fitting(List<Vector3> V, List<Vector3> Nn, List<int> I, SMDecorHanger kind, Vector3 p, Vector3 o, Vector3 e, float w, Matrix4x4 m)
        {
            var u = Vector3.Cross(e, o).normalized;
            if (kind == SMDecorHanger.Ring)
            {
                // weld ring standing on the edge, the chain runs through it
                float R = 1.8f * w;
                var c = p + o * (R * 0.7f);
                var cl = new Vector3[20];
                for (int k = 0; k < cl.Length; k++)
                {
                    float th = k * Mathf.PI * 2f / cl.Length;
                    cl[k] = c + o * (R * Mathf.Cos(th)) + u * (R * Mathf.Sin(th));
                }
                Ring(V, Nn, I, cl, true, 0.55f * w, e, m);
                Box(V, Nn, I, p + o * (0.3f * w), e * (2.4f * w), u * (2.4f * w), o * (0.6f * w), m);   // weld pad
            }
            else
            {
                // shackle: a U over the chain, legs down to a lug, a pin across
                float R = 1.7f * w, leg = 2.2f * w;
                var cl = new List<Vector3>();
                cl.Add(p + u * R);
                cl.Add(p + u * R + o * leg);
                for (int k = 1; k < 10; k++)
                {
                    float th = k * Mathf.PI / 10f;
                    cl.Add(p + o * leg + u * (R * Mathf.Cos(th)) + o * (R * Mathf.Sin(th)));
                }
                cl.Add(p - u * R + o * leg);
                cl.Add(p - u * R);
                Ring(V, Nn, I, cl, false, 0.55f * w, e, m);
                Ring(V, Nn, I, new[] { p + u * (R + 0.9f * w) + o * (0.6f * w), p - u * (R + 0.9f * w) + o * (0.6f * w) }, false, 0.45f * w, e, m);
                Box(V, Nn, I, p + o * (0.4f * w), e * (1.2f * w), u * (1.6f * R), o * (0.9f * w), m);   // lug
            }
        }

        static void Ball(List<Vector3> V, List<Vector3> Nn, List<int> I, Vector3 c, float r, Matrix4x4 m)
        {
            const int lat = 8, lon = 12;
            int b = V.Count;
            for (int i = 0; i <= lat; i++)
            {
                float th = Mathf.PI * i / lat;
                for (int j = 0; j < lon; j++)
                {
                    float ph = 2f * Mathf.PI * j / lon;
                    var n = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                    V.Add(m.MultiplyPoint3x4(c + n * r)); Nn.Add(m.MultiplyVector(n).normalized);
                }
            }
            for (int i = 0; i < lat; i++)
                for (int j = 0; j < lon; j++)
                {
                    int j2 = (j + 1) % lon;
                    int a0 = b + i * lon + j, a1 = b + i * lon + j2, b0 = b + (i + 1) * lon + j, b1 = b + (i + 1) * lon + j2;
                    I.Add(a0); I.Add(a1); I.Add(b0);
                    I.Add(a1); I.Add(b1); I.Add(b0);
                }
        }

        static void Box(List<Vector3> V, List<Vector3> Nn, List<int> I, Vector3 c, Vector3 ax, Vector3 ay, Vector3 az, Matrix4x4 m)
        {
            var axes = new[] { ax, ay, az };
            for (int f = 0; f < 3; f++)
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    var n = axes[f].normalized * sgn;
                    var a1 = axes[(f + 1) % 3]; var a2 = axes[(f + 2) % 3];
                    var fc = c + axes[f] * (0.5f * sgn);
                    int b = V.Count;
                    var corners = new[] { fc - a1 * 0.5f - a2 * 0.5f, fc + a1 * 0.5f - a2 * 0.5f, fc + a1 * 0.5f + a2 * 0.5f, fc - a1 * 0.5f + a2 * 0.5f };
                    foreach (var q in corners) { V.Add(m.MultiplyPoint3x4(q)); Nn.Add(m.MultiplyVector(n).normalized); }
                    bool flip = Vector3.Dot(Vector3.Cross(a1, a2), n) < 0f;
                    if (!flip) { I.Add(b); I.Add(b + 2); I.Add(b + 1); I.Add(b); I.Add(b + 3); I.Add(b + 2); }
                    else { I.Add(b); I.Add(b + 1); I.Add(b + 2); I.Add(b); I.Add(b + 2); I.Add(b + 3); }
                }
        }
    }
}
