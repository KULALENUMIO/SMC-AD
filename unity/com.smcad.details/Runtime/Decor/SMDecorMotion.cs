// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: run-time motion of one decoration on the GPU (SMDecorMotion.compute), written into the
// mesh's own vertex buffer, so the usual HDRP/Lit materials work unchanged.
//   cloth / net: sway with the wind (gusts, flutter), lean against the vehicle's acceleration, blown by gun blasts
//   chain:       a rope simulation (pins follow the part, gravity, wind, blasts, contact with the distance field)
// With the Tank Rigging plugin, the vehicle's gun shots (TankController.GunFired) become blasts (reflection only).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace SMCAD.Details.Decor
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("SMC-AD/SMC-DETAILS 装備の動き")]
    public class SMDecorMotion : MonoBehaviour
    {
        [Tooltip("動きの大きさ（倍率）")] [Range(0f, 3f)] public float amount = 1f;
        [Tooltip("布: 風でなびく大きさ (秒: 風速 1 m/s あたりの変位 m ×)")] [Range(0f, 0.05f)] public float sway = 0.02f;
        [Tooltip("布: はためき (m)")] [Range(0f, 0.05f)] public float flutter = 0.008f;
        [Tooltip("布: 動きの上限 (m)")] [Range(0.01f, 0.5f)] public float maxOffset = 0.15f;
        [Tooltip("布・鎖: 車体の加速度への反応")] [Range(0f, 0.05f)] public float inertia = 0.012f;
        [Tooltip("鎖: 減衰（大きいほど早く止まる）")] [Range(0f, 0.2f)] public float chainDamping = 0.03f;

        SMDecoration decor;
        Mesh mesh;
        GraphicsBuffer vb;
        int count, stride, posOff, nrmOff;
        bool ready, chain;
        float clock;
        Vector3 lastPos, lastVel, smoothAcc;
        bool hasLast;
        readonly List<ComputeBuffer> buffers = new List<ComputeBuffer>();
        ComputeBuffer bPos, bPrev, bTmp, bFrameA, bFrameZ;
        static ComputeShader cs;

        public static ComputeShader Shader => cs != null ? cs : cs = Resources.Load<ComputeShader>("SMCAD_Decor/SMDecorMotion");
        bool ShouldRun => Application.isPlaying || SMDecorWind.RunInEditor;

        void OnEnable() { decor = GetComponent<SMDecoration>(); HookGuns(); }
        void OnDisable() { Release(true); }

        /// <summary>Call after the decoration's mesh was rebuilt.</summary>
        public void Invalidate() { Release(false); }

        void LateUpdate()
        {
            if (!ShouldRun) { if (ready) Release(true); return; }
            Step(Application.isPlaying ? Time.deltaTime : 1f / 60f);
        }

        /// <summary>Advances the motion by dt seconds (also for tests / recording).</summary>
        public void Step(float dt)
        {
            if (!ready && !Init()) return;
            dt = Mathf.Clamp(dt, 1e-4f, 1f / 20f);
            clock += dt;
            var part = decor.part != null ? decor.part : transform;
            // vehicle acceleration (world), smoothed
            var pos = transform.position;
            if (hasLast)
            {
                var vel = (pos - lastPos) / dt;
                var acc = (vel - lastVel) / dt;
                smoothAcc = Vector3.Lerp(smoothAcc, acc, Mathf.Clamp01(dt * 8f));
                lastVel = vel;
            }
            lastPos = pos; hasLast = true;
            var c = Shader;
            var toWorld = transform.localToWorldMatrix;
            var toPart = transform.worldToLocalMatrix;
            c.SetMatrix("_PartToWorld", toWorld);
            c.SetMatrix("_WorldToPart", toPart);
            c.SetInt("_Count", count); c.SetInt("_Stride", stride); c.SetInt("_PosOff", posOff); c.SetInt("_NrmOff", nrmOff);
            var wind = SMDecorWind.Velocity(SMDecorWind.Now);
            var blasts = SMDecorWind.Blasts().Take(4).ToList();
            if (!chain)
            {
                int k = c.FindKernel("Sway");
                c.SetFloat("_Time", clock);
                c.SetFloat("_Sway", sway); c.SetFloat("_FlutterAmp", flutter); c.SetFloat("_FlutterFreq", 7f);
                c.SetFloat("_MaxOff", maxOffset); c.SetFloat("_Amount", amount);
                c.SetVector("_WindLocal", toPart.MultiplyVector(wind));
                c.SetVector("_InertiaLocal", toPart.MultiplyVector(-smoothAcc * inertia));
                var bp = new Vector4[4]; var bd = new Vector4[4];
                for (int i = 0; i < blasts.Count; i++)
                {
                    var (b, left) = blasts[i];
                    var lp = toPart.MultiplyPoint3x4(b.position);
                    bp[i] = new Vector4(lp.x, lp.y, lp.z, 0.25f * b.strength * left);
                    var ld = toPart.MultiplyVector(b.direction);
                    bd[i] = new Vector4(ld.x, ld.y, ld.z, 4f);
                }
                c.SetVectorArray("_BlastPos", bp); c.SetVectorArray("_BlastDir", bd);
                c.SetBuffer(k, "_VB", vb);
                Bind(c, k, "_RestPos", "_RestNrm", "_Free");
                c.Dispatch(k, (count + 63) / 64, 1, 1);
                return;
            }
            // chain
            var ch = (SMDecorChain)decor;
            int n = ch.motionRest.Length;
            var set = ch.set;
            var fieldFromWorld = ch.fieldFromPart * toPart;
            c.SetInt("_N", n);
            c.SetFloat("_Dt", dt);
            c.SetFloat("_Damp", 1f - chainDamping);
            c.SetFloat("_Omega", 0.9f);
            c.SetVector("_Gravity", Physics.gravity);
            c.SetVector("_WindAcc", wind * 0.35f - smoothAcc * 0f);
            var bw = new Vector4[4]; var bwd = new Vector4[4];
            for (int i = 0; i < blasts.Count; i++)
            {
                var (b, left) = blasts[i];
                bw[i] = new Vector4(b.position.x, b.position.y, b.position.z, 25f * b.strength * left);
                bwd[i] = new Vector4(b.direction.x, b.direction.y, b.direction.z, 3f);
            }
            c.SetVectorArray("_BlastW", bw); c.SetVectorArray("_BlastWDir", bwd);
            bool field = set != null && set.HasField;
            c.SetMatrix("_FieldFromWorld", fieldFromWorld);
            c.SetMatrix("_WorldFromField", fieldFromWorld.inverse);
            c.SetVector("_GridMin", field ? set.gridMin : Vector3.zero);
            c.SetVector("_GridSize", field ? set.GridSize : Vector3.one);
            c.SetFloat("_Voxel", field ? set.voxel : 1f);
            c.SetFloat("_MaxDist", field ? set.maxDistance : 1f);
            int kI = c.FindKernel("RopeIntegrate"), kS = c.FindKernel("RopeSolve"), kC = c.FindKernel("RopeCollide");
            int kF = c.FindKernel("RopeFrames"), kK = c.FindKernel("RopeSkin");
            int g = (n + 63) / 64;
            Bind(c, kI, "_RestLocal", "_Pin"); c.SetBuffer(kI, "_Pos", bPos); c.SetBuffer(kI, "_Prev", bPrev);
            c.Dispatch(kI, g, 1, 1);
            Bind(c, kS, "_Nbr", "_Len", "_Pin");
            for (int pass = 0; pass < 16; pass++)
            {
                c.SetBuffer(kS, "_PosIn", (pass & 1) == 0 ? bPos : bTmp);
                c.SetBuffer(kS, "_PosOut", (pass & 1) == 0 ? bTmp : bPos);
                c.Dispatch(kS, g, 1, 1);
            }
            int kT = c.FindKernel("RopeTether");
            Bind(c, kT, "_Pin", "_Tether", "_TetherLen"); c.SetBuffer(kT, "_Pos", bPos);
            c.Dispatch(kT, g, 1, 1);
            if (field)
            {
                Bind(c, kC, "_Radius", "_Pin"); c.SetBuffer(kC, "_Pos", bPos); c.SetBuffer(kC, "_Prev", bPrev); c.SetTexture(kC, "_Field", set.field);
                c.Dispatch(kC, g, 1, 1);
            }
            Bind(c, kF, "_Nbr", "_RestAxis", "_RestZ"); c.SetBuffer(kF, "_Pos", bPos); c.SetBuffer(kF, "_FrameA", bFrameA); c.SetBuffer(kF, "_FrameZ", bFrameZ);
            c.Dispatch(kF, g, 1, 1);
            Bind(c, kK, "_VLink", "_VLocal", "_VNrmLocal", "_VRest", "_VRestNrm"); c.SetBuffer(kK, "_Pos", bPos); c.SetBuffer(kK, "_FrameA", bFrameA); c.SetBuffer(kK, "_FrameZ", bFrameZ); c.SetBuffer(kK, "_VB", vb);
            c.Dispatch(kK, (count + 63) / 64, 1, 1);
        }

        /// <summary>Largest distance (m) of a vertex from its rest position right now (reads the GPU; for tests).</summary>
        public float DebugMaxOffset()
        {
            if (!ready || vb == null) return 0f;
            var raw = new uint[count * stride / 4];
            vb.GetData(raw);
            var rest = mesh.vertices;
            float best = 0f;
            for (int v = 0; v < count; v++)
            {
                int o = (v * stride + posOff) / 4;
                var p = new Vector3(BitConverter.ToSingle(BitConverter.GetBytes(raw[o]), 0), BitConverter.ToSingle(BitConverter.GetBytes(raw[o + 1]), 0), BitConverter.ToSingle(BitConverter.GetBytes(raw[o + 2]), 0));
                best = Mathf.Max(best, Vector3.Distance(p, rest[v]));
            }
            return best;
        }

        // named buffers bound to every kernel that uses them
        readonly Dictionary<string, ComputeBuffer> named = new Dictionary<string, ComputeBuffer>();
        void Bind(ComputeShader c, int k, params string[] names)
        {
            foreach (var n in names) if (named.TryGetValue(n, out var b)) c.SetBuffer(k, n, b);
        }

        ComputeBuffer Make<T>(string name, T[] data, int strideBytes) where T : struct
        {
            var b = new ComputeBuffer(Mathf.Max(1, data.Length), strideBytes);
            if (data.Length > 0) b.SetData(data);
            buffers.Add(b);
            if (name != null) named[name] = b;
            return b;
        }

        bool Init()
        {
            if (decor == null) decor = GetComponent<SMDecoration>();
            var mf = GetComponent<MeshFilter>();
            if (decor == null || mf == null || mf.sharedMesh == null || Shader == null || !SystemInfo.supportsComputeShaders) return false;
            mesh = mf.sharedMesh;
            chain = decor is SMDecorChain;
            var chd = decor as SMDecorChain;
            if (chain && (chd.motionRest == null || chd.motionRest.Length == 0)) return false;
            var verts = mesh.vertices; var norms = mesh.normals;
            count = verts.Length;
            if (count == 0 || norms.Length != count) return false;
            mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            vb = mesh.GetVertexBuffer(0);
            stride = mesh.GetVertexBufferStride(0);
            posOff = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            nrmOff = mesh.GetVertexAttributeStream(VertexAttribute.Normal) == 0 ? mesh.GetVertexAttributeOffset(VertexAttribute.Normal) : posOff;
            var b = mesh.bounds; b.Expand(chain ? 1f : maxOffset * 2f + 0.1f); mesh.bounds = b;
            if (!chain)
            {
                var uv1 = new List<Vector2>(); mesh.GetUVs(1, uv1);
                if (uv1.Count != count) { uv1.Clear(); for (int i = 0; i < count; i++) uv1.Add(new Vector2(0.5f, 0f)); }
                Make("_RestPos", verts, 12);
                Make("_RestNrm", norms, 12);
                Make("_Free", uv1.ToArray(), 8);
            }
            else
            {
                int n = chd.motionRest.Length;
                var rest = chd.motionRest;
                var nbr = new Vector2Int[n]; var len = new Vector2[n]; var pin = new uint[n];
                var restAxis = new Vector3[n]; var restZ = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    nbr[i] = new Vector2Int(chd.motionPrev[i], chd.motionNext[i]);
                    len[i] = new Vector2(chd.motionLenPrev[i], chd.motionLenNext[i]);
                    pin[i] = chd.motionPin[i] ? 1u : 0u;
                    var a = rest[nbr[i].y >= 0 ? nbr[i].y : i] - rest[nbr[i].x >= 0 ? nbr[i].x : i];
                    restAxis[i] = a.sqrMagnitude > 1e-12f ? a.normalized : Vector3.down;
                    var z = Vector3.Cross(restAxis[i], Mathf.Abs(restAxis[i].y) < 0.95f ? Vector3.up : Vector3.right).normalized;
                    restZ[i] = z;
                }
                // vertices relative to their link's rest frame
                var uv2 = new List<Vector2>(); mesh.GetUVs(2, uv2);
                var vlink = new int[count]; var vlocal = new Vector3[count]; var vnl = new Vector3[count];
                for (int v = 0; v < count; v++)
                {
                    int l = v < uv2.Count ? Mathf.RoundToInt(uv2[v].x) : -1;
                    if (l >= n) l = -1;
                    vlink[v] = l;
                    if (l < 0) continue;
                    Vector3 ax = restAxis[l], z = restZ[l], y = Vector3.Cross(z, ax);
                    var d = verts[v] - rest[l];
                    vlocal[v] = new Vector3(Vector3.Dot(d, ax), Vector3.Dot(d, y), Vector3.Dot(d, z));
                    vnl[v] = new Vector3(Vector3.Dot(norms[v], ax), Vector3.Dot(norms[v], y), Vector3.Dot(norms[v], z));
                }
                // tethers: the nearest pin both ways along the chain and the length to it
                var tether = new Vector2Int[n]; var tlen = new Vector2[n];
                for (int i = 0; i < n; i++)
                {
                    int a = i; float la = 0f;
                    while (a >= 0 && !chd.motionPin[a]) { la += chd.motionLenPrev[a]; a = chd.motionPrev[a]; }
                    int b2 = i; float lb = 0f;
                    while (b2 >= 0 && !chd.motionPin[b2]) { lb += chd.motionLenNext[b2]; b2 = chd.motionNext[b2]; }
                    tether[i] = new Vector2Int(a, b2); tlen[i] = new Vector2(la, lb);
                }
                Make("_Tether", tether, 8);
                Make("_TetherLen", tlen, 8);
                Make("_RestLocal", rest, 12);
                Make("_Nbr", nbr, 8);
                Make("_Len", len, 8);
                Make("_Radius", chd.motionRadius, 4);
                Make("_Pin", pin, 4);
                Make("_RestAxis", restAxis, 12);
                Make("_RestZ", restZ, 12);
                Make("_VLink", vlink, 4);
                Make("_VLocal", vlocal, 12);
                Make("_VNrmLocal", vnl, 12);
                Make("_VRest", verts, 12);
                Make("_VRestNrm", norms, 12);
                var world = rest.Select(p => (Vector4)transform.TransformPoint(p)).ToArray();
                bPos = Make<Vector4>(null, world, 16);
                bPrev = Make<Vector4>(null, world, 16);
                bTmp = Make<Vector4>(null, world, 16);
                bFrameA = Make<Vector4>(null, new Vector4[n], 16);
                bFrameZ = Make<Vector4>(null, new Vector4[n], 16);
            }
            hasLast = false; clock = 0f;
            ready = true;
            return true;
        }

        /// <summary>Frees the GPU data; restore = put the rest shape back into the mesh.</summary>
        void Release(bool restore)
        {
            if (ready && restore && mesh != null)
            {
                // re-upload the mesh's own (rest) data
                mesh.SetVertices(mesh.vertices);
                mesh.SetNormals(mesh.normals);
                mesh.UploadMeshData(false);
            }
            foreach (var b in buffers) b?.Release();
            buffers.Clear(); named.Clear();
            vb?.Dispose(); vb = null;
            bPos = bPrev = bTmp = bFrameA = bFrameZ = null;
            ready = false;
        }

        // ------------------------------------------------------------------ gun blasts from the Tank Rigging plugin

        static readonly HashSet<UnityEngine.Object> hooked = new HashSet<UnityEngine.Object>();

        void HookGuns()
        {
            foreach (var comp in GetComponentsInParent<Component>(true))
            {
                if (comp == null || hooked.Contains(comp)) continue;
                var ev = comp.GetType().GetEvent("GunFired", BindingFlags.Public | BindingFlags.Instance);
                if (ev == null) continue;
                try
                {
                    var t = ev.EventHandlerType;
                    var ps = t.GetMethod("Invoke").GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
                    var call = Expression.Call(typeof(SMDecorMotion).GetMethod(nameof(OnGunFired), BindingFlags.Static | BindingFlags.NonPublic),
                                               Expression.Convert(ps[ps.Length - 1], typeof(object)));
                    ev.AddEventHandler(comp, Expression.Lambda(t, call, ps).Compile());
                    hooked.Add(comp);
                }
                catch (Exception e) { Debug.LogWarning("[SMC-DETAILS 装備] 発砲の爆風をつなげませんでした: " + e.Message); }
            }
        }

        static void OnGunFired(object shot)
        {
            if (shot == null) return;
            var tp = shot.GetType();
            var muzzle = tp.GetField("muzzle")?.GetValue(shot) as Transform;
            float power = tp.GetField("power")?.GetValue(shot) is float p ? p : 1f;
            if (muzzle != null) SMDecorWind.AddBlast(muzzle.position, muzzle.forward, Mathf.Max(0.2f, power));
        }
    }
}
