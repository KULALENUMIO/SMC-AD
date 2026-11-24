// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: drapes a sheet (camouflage net / cloth / tarpaulin) over the vehicle.
//   1 the four corners (root-local), ordered around their centre
//   2 a grid larger than the quad between them by "extra", started as a dome above it and lifted clear of the body
//   3 position-based dynamics: stretch, shear and bend constraints, gravity, contact with the signed distance field
//     (thickness), friction, the floor under the vehicle, corners pinned (optional); cut cells (D3) are left out.
//     SM vehicles are hollow shells, so every point keeps the last place where it was clear of the body and is never
//     let through a surface from there (else the sheet falls through the roof into the vehicle). Contact is kept at
//     least ~ the field's voxel (finer thicknesses are below what the field can tell) and pulled in to the asked
//     thickness at the end where the field allows it. Thin upright things (antennas) go through the sheet.
//   4 mesh: the sheet (UV in metres, UV1.x = how free the vertex hangs, for the wind in D4) and, for a net,
//     leaf strips ("garnish") as a second sub-mesh
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SMCAD.Details.Decor
{
    public static class SMDecorClothGen
    {
        public struct Stats { public Vector2 size; public int particles; public float maxPenetration; }
        const int MaxParticlesCpu = 6000, MaxParticlesGpu = 40000;

        /// <summary>gpu: the SMDecorCloth.compute shader (null = on the CPU). drape = false re-meshes the last shape
        /// (cutting preview) when it fits the grid.</summary>
        public static Stats Build(SMDecorCloth c, Matrix4x4 rootFromPart, Mesh mesh, ComputeShader gpu = null, bool drape = true)
        {
            var st = new Stats();
            var set = c.set;
            if (set == null || !set.HasField || c.corners.Count < 4) { mesh.Clear(); return st; }
            var down = set.down.sqrMagnitude > 0.5f ? set.down.normalized : Vector3.down;
            var up = -down;
            var rng = new System.Random(c.seed);
            float thick = Mathf.Max(0.002f, c.thickness);
            // a camouflage net is bulky (the leaf strips stand off it): it rests on things instead of dropping into gaps
            if (c.type == SMDecorClothType.CamoNet) thick = Mathf.Max(thick, 0.015f + 0.025f * c.garnish);
            float visual = Mathf.Max(thick, set.voxel * 0.7f); // how far the finished sheet stands off the body (closer than
                                                               // this, small details of the vehicle poke through it)
            thick = Mathf.Max(thick, set.voxel * 0.7f);        // contact while settling: what the field can resolve
            poleR = c.poleWidth > 0f ? c.poleWidth * 0.5f + set.voxel * 0.5f + thick : 0f;

            // 1 corners, counter-clockwise around their centre (seen from above the sheet)
            var K = new Vector3[4]; var Kn = new Vector3[4];
            Vector3 centre = Vector3.zero, nsum = Vector3.zero;
            for (int i = 0; i < 4; i++)
            {
                K[i] = rootFromPart.MultiplyPoint3x4(c.corners[i]);
                Kn[i] = (i < c.cornerNormals.Count ? rootFromPart.MultiplyVector(c.cornerNormals[i]) : up).normalized;
                centre += K[i] * 0.25f; nsum += Kn[i];
            }
            var axisN = (nsum.normalized + up).normalized;
            var t1 = Vector3.Cross(axisN, Mathf.Abs(Vector3.Dot(axisN, Vector3.right)) < 0.9f ? Vector3.right : Vector3.forward).normalized;
            var t2 = Vector3.Cross(axisN, t1);
            var order = new[] { 0, 1, 2, 3 };
            System.Array.Sort(order, (a, b) => Mathf.Atan2(Vector3.Dot(K[a] - centre, t2), Vector3.Dot(K[a] - centre, t1))
                .CompareTo(Mathf.Atan2(Vector3.Dot(K[b] - centre, t2), Vector3.Dot(K[b] - centre, t1))));
            Vector3 A = K[order[0]], B = K[order[1]], C = K[order[2]], D = K[order[3]];
            Vector3 nA = Kn[order[0]], nB = Kn[order[1]], nC = Kn[order[2]], nD = Kn[order[3]];

            // 2 grid
            float cw = (Vector3.Distance(A, B) + Vector3.Distance(D, C)) * 0.5f;
            float ch = (Vector3.Distance(A, D) + Vector3.Distance(B, C)) * 0.5f;
            float W = cw * (1f + c.extra), H = ch * (1f + c.extra);
            float cell = Mathf.Max(0.01f, c.cell);
            int maxP = gpu != null && SystemInfo.supportsComputeShaders ? MaxParticlesGpu : MaxParticlesCpu;
            while ((W / cell + 1) * (H / cell + 1) > maxP) cell *= 1.1f;
            int nu = Mathf.Clamp(Mathf.RoundToInt(W / cell) + 1, 3, 400), nv = Mathf.Clamp(Mathf.RoundToInt(H / cell) + 1, 3, 400);
            float du = W / (nu - 1), dv = H / (nv - 1);
            float tile = c.type == SMDecorClothType.CamoNet ? Mathf.Max(0.02f, c.meshSize) : 0.5f;   // one texture repeat (m)
            c.gridU = nu; c.gridV = nv; c.stepU = du; c.stepV = dv; c.uvTile = tile;
            bool reuse = !drape && c.lastShape != null && c.lastShape.Length == nu * nv;
            int N = nu * nv;
            int Idx(int i, int j) => i + nu * j;

            // cut mask (cells), resampled when the resolution changed
            var alive = new bool[(nu - 1) * (nv - 1)];
            for (int j = 0; j < nv - 1; j++)
                for (int i = 0; i < nu - 1; i++)
                {
                    bool cutHere = false;
                    if (c.cut != null && c.cutW > 0 && c.cutH > 0 && c.cut.Length == c.cutW * c.cutH)
                    {
                        int ci = Mathf.Clamp((int)((i + 0.5f) / (nu - 1) * c.cutW), 0, c.cutW - 1);
                        int cj = Mathf.Clamp((int)((j + 0.5f) / (nv - 1) * c.cutH), 0, c.cutH - 1);
                        cutHere = c.cut[ci + c.cutW * cj] != 0;
                    }
                    alive[i + (nu - 1) * j] = !cutHere;
                }
            // frayed edges: cells next to a cut (and on the border, if asked) are cut at random
            if (c.fray > 0f)
            {
                var cutNow = new bool[alive.Length];
                for (int j = 0; j < nv - 1; j++)
                    for (int i = 0; i < nu - 1; i++)
                    {
                        if (!alive[i + (nu - 1) * j]) continue;
                        bool edge = c.frayBorder && (i == 0 || j == 0 || i == nu - 2 || j == nv - 2);
                        for (int dj = -1; dj <= 1 && !edge; dj++)
                            for (int di = -1; di <= 1 && !edge; di++)
                            {
                                int a = i + di, b = j + dj;
                                if (a >= 0 && b >= 0 && a < nu - 1 && b < nv - 1 && !alive[a + (nu - 1) * b]) edge = true;
                            }
                        if (!edge) continue;
                        uint h = (uint)(i * 73856093 ^ j * 19349663 ^ c.seed * 83492791);
                        h = (h ^ (h >> 13)) * 1274126177u;
                        if (((h >> 8) & 0xFFFF) / 65535f < c.fray * 0.55f) cutNow[i + (nu - 1) * j] = true;
                    }
                for (int q = 0; q < alive.Length; q++) if (cutNow[q]) alive[q] = false;
            }
            bool Cell(int i, int j) => i >= 0 && j >= 0 && i < nu - 1 && j < nv - 1 && alive[i + (nu - 1) * j];

            float Hu = Mathf.Sqrt(Mathf.Max(0f, 3f * cw * (W - cw) / 8f));
            float Hv = Mathf.Sqrt(Mathf.Max(0f, 3f * ch * (H - ch) / 8f));
            var x = new Vector3[N];
            var used = new bool[N];
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    if (reuse) { x[Idx(i, j)] = c.lastShape[Idx(i, j)]; used[Idx(i, j)] = Cell(i - 1, j - 1) || Cell(i, j - 1) || Cell(i - 1, j) || Cell(i, j); continue; }
                    float u = (float)i / (nu - 1), v = (float)j / (nv - 1);
                    var p = Vector3.Lerp(Vector3.Lerp(A, B, u), Vector3.Lerp(D, C, u), v);
                    var n = Vector3.Lerp(Vector3.Lerp(nA, nB, u), Vector3.Lerp(nD, nC, u), v).normalized;
                    p += (n + up).normalized * (Hu * 4f * u * (1f - u) + Hv * 4f * v * (1f - v) + thick * 2f);
                    int k = Idx(i, j);
                    // start above everything in this column (the sheet then settles on the highest points, like a tent)
                    float step = set.voxel;
                    for (float hgt = 2.5f; hgt > 0f; hgt -= step)
                        if (set.Distance(p + up * hgt) < thick * 1.5f && !Pole(set, p + up * hgt, down, thick)) { p += up * (hgt + thick * 3f); break; }
                    for (int s = 0; s < 400 && set.Distance(p) < thick * 1.5f && !Pole(set, p, down, thick); s++) p += up * step;
                    x[k] = p;
                    used[k] = Cell(i - 1, j - 1) || Cell(i, j - 1) || Cell(i - 1, j) || Cell(i, j);
                }

            // constraints
            var ca = new List<int>(); var cb = new List<int>(); var cr = new List<float>(); var ck = new List<float>();
            void Con(int a, int b, float rest, float k) { ca.Add(a); cb.Add(b); cr.Add(rest); ck.Add(k); }
            float diag = Mathf.Sqrt(du * du + dv * dv);
            float bend = Mathf.Lerp(0.02f, 0.6f, c.stiffness);
            for (int j = 0; j < nv; j++)
                for (int i = 0; i < nu; i++)
                {
                    if (i + 1 < nu && (Cell(i, j - 1) || Cell(i, j))) Con(Idx(i, j), Idx(i + 1, j), du, 1f);
                    if (j + 1 < nv && (Cell(i - 1, j) || Cell(i, j))) Con(Idx(i, j), Idx(i, j + 1), dv, 1f);
                    if (Cell(i, j)) { Con(Idx(i, j), Idx(i + 1, j + 1), diag, 0.6f); Con(Idx(i + 1, j), Idx(i, j + 1), diag, 0.6f); }
                    if (i + 2 < nu && (Cell(i, j - 1) || Cell(i, j)) && (Cell(i + 1, j - 1) || Cell(i + 1, j))) Con(Idx(i, j), Idx(i + 2, j), du * 2f, bend);
                    if (j + 2 < nv && (Cell(i - 1, j) || Cell(i, j)) && (Cell(i - 1, j + 1) || Cell(i, j + 1))) Con(Idx(i, j), Idx(i, j + 2), dv * 2f, bend);
                }
            int M = ca.Count;
            var CA = ca.ToArray(); var CB = cb.ToArray(); var CR = cr.ToArray(); var CK = ck.ToArray();

            var pin = new bool[N];
            if (c.pinCorners && !reuse)
            {
                var pins = new[] { (Idx(0, 0), A, nA), (Idx(nu - 1, 0), B, nB), (Idx(nu - 1, nv - 1), C, nC), (Idx(0, nv - 1), D, nD) };
                foreach (var (k, p, n) in pins) { pin[k] = true; x[k] = p + n * thick; }
            }

            // 3 settle
            float floor = set.floor - thick;
            var safe = (Vector3[])x.Clone();
            if (!reuse && gpu != null && SystemInfo.supportsComputeShaders)
                GpuSettle(gpu, set, x, safe, pin, used, alive, nu, nv, du, dv, bend, thick, down, floor);
            else if (!reuse) CpuSettle(set, x, safe, pin, used, CA, CB, CR, CK, thick, down, floor);
            if (reuse) goto BuildMesh;
            // final push out of the body (a few points can end up in thin gaps), to the side each point came from, then in
            // to the asked thickness where the field allows it
            for (int k = 0; k < N; k++)
            {
                if (pin[k] || !used[k]) continue;
                if (Pole(set, x[k], down, thick)) continue;
                Vector3 Out(Vector3 p, Vector3 grad) { var to = safe[k] - p; return to.sqrMagnitude > 1e-8f && Vector3.Dot(grad, to) < 0f ? -grad : grad; }
                for (int s = 0; s < 40; s++)
                {
                    float dist = set.Sample(x[k], out var grad);
                    if (dist >= thick * 0.9f) break;
                    x[k] += Out(x[k], grad) * (thick - dist + 0.002f);
                }
                if (visual < thick)
                {
                    float dist = set.Sample(x[k], out var grad);
                    if (dist < thick * 1.4f)
                    {
                        var target = x[k] - Out(x[k], grad) * Mathf.Min(thick - visual, Mathf.Max(0f, dist - visual));
                        if (set.Distance(target) > visual * 0.5f) x[k] = target;
                    }
                }
            }
            if (!reuse) c.lastShape = (Vector3[])x.Clone();

        BuildMesh:
            // stats
            float pen = 0f;
            for (int k = 0; k < N; k++) if (used[k] && !pin[k] && !Pole(set, x[k], down, thick)) pen = Mathf.Max(pen, visual * 0.5f - set.Distance(x[k]));
            st.size = new Vector2(W, H); st.particles = N; st.maxPenetration = Mathf.Max(0f, pen);

            // 4 mesh in the part's space
            var partFromRoot = rootFromPart.inverse;
            var V = new List<Vector3>(N); var UV = new List<Vector2>(N); var UV1 = new List<Vector2>(N);
            var remap = new int[N];
            for (int k = 0; k < N; k++)
            {
                remap[k] = -1;
                if (!used[k]) continue;
                remap[k] = V.Count;
                V.Add(partFromRoot.MultiplyPoint3x4(x[k]));
                UV.Add(new Vector2((k % nu) * du / tile, (k / nu) * dv / tile));
                UV1.Add(new Vector2(Mathf.Clamp01((set.Distance(x[k]) - visual) / 0.25f), 0f));
            }
            var tris = new List<int>();
            for (int j = 0; j < nv - 1; j++)
                for (int i = 0; i < nu - 1; i++)
                {
                    if (!Cell(i, j)) continue;
                    int a = remap[Idx(i, j)], b = remap[Idx(i + 1, j)], cc = remap[Idx(i + 1, j + 1)], d = remap[Idx(i, j + 1)];
                    tris.Add(a); tris.Add(d); tris.Add(cc);
                    tris.Add(a); tris.Add(cc); tris.Add(b);
                }
            int sheetVerts = V.Count;
            var garnish = new List<int>();
            if (c.type == SMDecorClothType.CamoNet && c.garnish > 0f) Garnish(c, set, x, nu, nv, du, dv, Cell, Idx, thick, rng, partFromRoot, V, UV, UV1, garnish);

            mesh.Clear();
            mesh.indexFormat = V.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(V);
            mesh.SetUVs(0, UV);
            mesh.SetUVs(1, UV1);
            mesh.subMeshCount = garnish.Count > 0 ? 2 : 1;
            mesh.SetTriangles(tris, 0);
            if (garnish.Count > 0) mesh.SetTriangles(garnish, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return st;
        }


        static void CpuSettle(SMDecorSet set, Vector3[] x, Vector3[] safe, bool[] pin, bool[] used, int[] CA, int[] CB, float[] CR, float[] CK, float thick, Vector3 down, float floor)
        {
            int N = x.Length, M = CA.Length;
            var prev = (Vector3[])x.Clone();
            float dt = 1f / 60f;
            var g = down * (9.81f * dt * dt);
            for (int it = 0; it < 240; it++)
            {
                for (int k = 0; k < N; k++)
                {
                    if (pin[k] || !used[k]) continue;
                    var v = (x[k] - prev[k]) * 0.96f;
                    prev[k] = x[k];
                    // swept move: thin plates (decks, hulls) must not be skipped in one step
                    var stepV = v + g;
                    float len = stepV.magnitude;
                    float probe = thick * 0.5f;
                    if (len > probe)
                    {
                        int n = Mathf.Min(48, Mathf.CeilToInt(len / probe));
                        for (int s = 1; s <= n; s++)
                            if (set.Distance(x[k] + stepV * ((float)s / n)) < thick && !Pole(set, x[k] + stepV * ((float)s / n), down, thick))
                            {
                                stepV *= (float)(s - 1) / n;
                                prev[k] = x[k] + stepV;   // stop there
                                break;
                            }
                    }
                    x[k] += stepV;
                }
                for (int pass = 0; pass < 4; pass++)
                    for (int m = 0; m < M; m++)
                    {
                        int a = CA[m], b = CB[m];
                        var d = x[b] - x[a];
                        float len = d.magnitude;
                        if (len < 1e-7f) continue;
                        float wa = pin[a] ? 0f : 1f, wb = pin[b] ? 0f : 1f;
                        if (wa + wb == 0f) continue;
                        float diff = (len - CR[m]) / len;
                        if (CK[m] < 1f && diff < 0f) diff *= CK[m];        // bending / shear resist compression only weakly
                        var corr = d * (diff * CK[m] / (wa + wb));
                        x[a] += corr * wa;
                        x[b] -= corr * wb;
                    }
                for (int k = 0; k < N; k++)
                {
                    if (pin[k] || !used[k]) continue;
                    // a surface crossed since the last safe place (hollow shells): back to just before it
                    var dv = x[k] - safe[k];
                    float len = dv.magnitude, probe = thick * 0.25f;
                    if (len > probe)
                    {
                        int n = Mathf.Min(64, Mathf.CeilToInt(len / probe));
                        for (int s = 1; s <= n; s++)
                            if (set.Distance(safe[k] + dv * ((float)s / n)) < thick * 0.5f && !Pole(set, safe[k] + dv * ((float)s / n), down, thick))
                            {
                                x[k] = safe[k] + dv * ((float)(s - 1) / n);
                                prev[k] = Vector3.Lerp(prev[k], x[k], 0.7f);
                                break;
                            }
                    }
                    float dist = set.Sample(x[k], out var grad);
                    bool pole = dist < thick && Pole(set, x[k], down, thick);
                    if (dist < thick && !pole)
                    {
                        var to = safe[k] - x[k];
                        if (to.sqrMagnitude > 1e-8f && Vector3.Dot(grad, to) < 0f) grad = -grad;   // to the side it came from
                        x[k] += grad * (thick - dist);
                        prev[k] = Vector3.Lerp(prev[k], x[k], 0.7f);   // friction
                    }
                    float depth = Vector3.Dot(x[k], down);
                    if (depth > floor) { x[k] -= down * (depth - floor); prev[k] = Vector3.Lerp(prev[k], x[k], 0.8f); }
                    if (pole || set.Distance(x[k]) >= thick * 0.7f) safe[k] = x[k];
                }
            }

        }

        // how far from a pole's middle it must be open: half the width, the field's blur and the contact
        [System.ThreadStatic] static float poleR;
        static float PoleRadius(SMDecorSet set) => poleR;

        /// <summary>A thin upright thing (antenna, rod, pole, up to the cloth's poleWidth across): the sheet does not rest on it (it would
        /// hang in a tall cone); the pole goes through instead. Its middle is found under the point (touching its top) or
        /// behind it (touching its side); from there it must be open on all four sides, there and 12 cm lower. Walls,
        /// plates and bars are long one way, so they still hold the sheet. Same as Pole() in SMDecorCloth.compute.</summary>
        public static bool Pole(SMDecorSet set, Vector3 p, Vector3 down, float thick)
        {
            float r = PoleRadius(set);
            if (r <= 0f) return false;
            float d = set.Sample(p, out var g);
            var gh = g - down * Vector3.Dot(g, down);
            Vector3 c;
            if (gh.magnitude < 0.5f) c = p + down * (d + thick);
            else
            {
                var n = gh.normalized;
                float w = -1f;
                for (int s = 1; s <= 12; s++)
                {
                    float a = d + 2f * r * s / 12f;
                    if (set.Distance(p - n * a) > thick) { w = a - d; break; }
                }
                if (w < 0f) return false;
                c = p - n * (d + w * 0.5f);
            }
            var h1 = Vector3.Cross(down, Mathf.Abs(down.x) < 0.9f ? Vector3.right : Vector3.forward).normalized;
            var h2 = Vector3.Cross(down, h1);
            for (int k = 0; k < 2; k++)
            {
                var q = c + down * (k == 0 ? 0f : 0.12f);
                if (set.Distance(q + h1 * r) < thick || set.Distance(q - h1 * r) < thick ||
                    set.Distance(q + h2 * r) < thick || set.Distance(q - h2 * r) < thick) return false;
            }
            return true;
        }

        /// <summary>The same on the GPU (SMDecorCloth.compute): Jacobi constraint passes, many more of them.</summary>
        static void GpuSettle(ComputeShader cs, SMDecorSet set, Vector3[] x, Vector3[] safe, bool[] pin, bool[] used, bool[] alive, int nu, int nv,
                              float du, float dv, float bend, float thick, Vector3 down, float floor)
        {
            int N = x.Length;
            var pos = new Vector4[N];
            var flags = new uint[N];
            for (int k = 0; k < N; k++) { pos[k] = x[k]; flags[k] = (pin[k] ? 1u : 0u) | (used[k] ? 2u : 0u); }
            var aliveU = new uint[alive.Length];
            for (int q = 0; q < alive.Length; q++) aliveU[q] = alive[q] ? 1u : 0u;
            var bPos = new ComputeBuffer(N, 16); var bPrev = new ComputeBuffer(N, 16); var bTmp = new ComputeBuffer(N, 16); var bSafe = new ComputeBuffer(N, 16);
            var bFlags = new ComputeBuffer(N, 4); var bAlive = new ComputeBuffer(Mathf.Max(1, aliveU.Length), 4);
            try
            {
                bPos.SetData(pos); bPrev.SetData(pos); bTmp.SetData(pos); bSafe.SetData(pos);
                bFlags.SetData(flags); bAlive.SetData(aliveU);
                float dt = 1f / 60f;
                cs.SetInts("_N", nu, nv);
                cs.SetFloat("_Du", du); cs.SetFloat("_Dv", dv); cs.SetFloat("_Diag", Mathf.Sqrt(du * du + dv * dv)); cs.SetFloat("_Bend", bend);
                cs.SetFloat("_Thick", thick); cs.SetFloat("_PoleR", PoleRadius(set)); cs.SetFloat("_Damp", 0.96f); cs.SetFloat("_Floor", floor); cs.SetFloat("_Omega", 0.9f);
                cs.SetVector("_Down", down); cs.SetVector("_Gstep", down * (9.81f * dt * dt));
                cs.SetVector("_GridMin", set.gridMin); cs.SetVector("_GridSize", set.GridSize);
                cs.SetFloat("_Voxel", set.voxel); cs.SetFloat("_MaxDist", set.maxDistance);
                int kI = cs.FindKernel("Integrate"), kS = cs.FindKernel("Solve"), kC = cs.FindKernel("Collide");
                foreach (int k in new[] { kI, kS, kC })
                {
                    cs.SetTexture(k, "_Field", set.field);
                    cs.SetBuffer(k, "_Flags", bFlags);
                    cs.SetBuffer(k, "_Alive", bAlive);
                }
                cs.SetBuffer(kI, "_Pos", bPos); cs.SetBuffer(kI, "_Prev", bPrev);
                cs.SetBuffer(kC, "_Pos", bPos); cs.SetBuffer(kC, "_Prev", bPrev); cs.SetBuffer(kC, "_Safe", bSafe);
                int g1 = (N + 63) / 64, gx = (nu + 7) / 8, gy = (nv + 7) / 8;
                for (int it = 0; it < 300; it++)
                {
                    cs.Dispatch(kI, g1, 1, 1);
                    for (int pass = 0; pass < 12; pass++)
                    {
                        // ping-pong: pos -> tmp -> pos
                        cs.SetBuffer(kS, "_PosIn", (pass & 1) == 0 ? bPos : bTmp);
                        cs.SetBuffer(kS, "_PosOut", (pass & 1) == 0 ? bTmp : bPos);
                        cs.Dispatch(kS, gx, gy, 1);
                    }
                    cs.Dispatch(kC, g1, 1, 1);
                }
                bPos.GetData(pos);
                for (int k = 0; k < N; k++) x[k] = pos[k];
                bSafe.GetData(pos);
                for (int k = 0; k < N; k++) safe[k] = pos[k];
            }
            finally { bPos.Release(); bPrev.Release(); bTmp.Release(); bSafe.Release(); bFlags.Release(); bAlive.Release(); }
        }

        /// <summary>Leaf strips on the net: small double-sided cards with a slight tilt, a little off the sheet.</summary>
        static void Garnish(SMDecorCloth c, SMDecorSet set, Vector3[] x, int nu, int nv, float du, float dv,
                            System.Func<int, int, bool> cell, System.Func<int, int, int> idx, float thick, System.Random rng,
                            Matrix4x4 m, List<Vector3> V, List<Vector2> UV, List<Vector2> UV1, List<int> T)
        {
            float perCell = 170f * c.garnish * du * dv;
            float R() => (float)rng.NextDouble();
            for (int j = 0; j < nv - 1; j++)
                for (int i = 0; i < nu - 1; i++)
                {
                    if (!cell(i, j)) continue;
                    int count = Mathf.FloorToInt(perCell) + (R() < perCell - Mathf.Floor(perCell) ? 1 : 0);
                    for (int q = 0; q < count; q++)
                    {
                        float u = R(), v = R();
                        Vector3 p00 = x[idx(i, j)], p10 = x[idx(i + 1, j)], p01 = x[idx(i, j + 1)], p11 = x[idx(i + 1, j + 1)];
                        var p = Vector3.Lerp(Vector3.Lerp(p00, p10, u), Vector3.Lerp(p01, p11, u), v);
                        var n = Vector3.Cross(p10 - p00, p01 - p00).normalized;
                        if (set.Distance(p + n * 0.03f) < set.Distance(p - n * 0.03f)) n = -n;   // away from the body
                        var t = Vector3.Cross(n, Random3(rng)).normalized;
                        if (t.sqrMagnitude < 0.5f) t = (p10 - p00).normalized;
                        var b = Vector3.Cross(n, t);
                        float len = Mathf.Lerp(0.07f, 0.17f, R()), wid = Mathf.Lerp(0.025f, 0.055f, R());
                        var tilt = Quaternion.AngleAxis(Mathf.Lerp(-35f, 35f, R()), t);
                        var bw = tilt * b; var nn = tilt * n;
                        var cpos = p + n * (thick * 0.5f + 0.004f + 0.02f * R());
                        int k = rng.Next(4);
                        int s = V.Count;
                        V.Add(m.MultiplyPoint3x4(cpos - t * len * 0.5f - bw * wid * 0.5f));
                        V.Add(m.MultiplyPoint3x4(cpos + t * len * 0.5f - bw * wid * 0.5f));
                        V.Add(m.MultiplyPoint3x4(cpos + t * len * 0.5f + bw * wid * 0.5f));
                        V.Add(m.MultiplyPoint3x4(cpos - t * len * 0.5f + bw * wid * 0.5f));
                        UV.Add(new Vector2(k * 0.25f, 0f)); UV.Add(new Vector2(k * 0.25f + 0.25f, 0f));
                        UV.Add(new Vector2(k * 0.25f + 0.25f, 1f)); UV.Add(new Vector2(k * 0.25f, 1f));
                        float free = Mathf.Clamp01((set.Distance(p) - thick) / 0.25f);
                        for (int z = 0; z < 4; z++) UV1.Add(new Vector2(free, 1f));
                        T.Add(s); T.Add(s + 2); T.Add(s + 1);
                        T.Add(s); T.Add(s + 3); T.Add(s + 2);
                        _ = nn;
                    }
                }
        }

        static Vector3 Random3(System.Random rng) =>
            new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 2f - 1f);
    }
}
