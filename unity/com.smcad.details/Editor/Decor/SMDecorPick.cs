// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: picking a point on the vehicle with the mouse (no colliders needed).
// Uses the editor's own ray-mesh test (HandleUtility.IntersectRayMesh, internal) and falls back to a plain
// ray-triangle loop. SM meshes are thin shells whose vertex normals are unreliable, so the hit normal is the face
// normal turned towards the ray.
using System.Collections.Generic;
using System.Reflection;
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    public struct SMDecorHit
    {
        public Vector3 point;        // world
        public Vector3 normal;       // world, facing the viewer
        public Renderer renderer;
        public float distance;       // along the ray
        public SMDecorParts.Part part;
    }

    public static class SMDecorPick
    {
        static MethodInfo intersect;
        static bool looked;

        public static bool Raycast(Transform root, Ray ray, out SMDecorHit hit)
        {
            hit = default;
            hit.distance = float.PositiveInfinity;
            var renderers = SMDetailsBuilder.SMRenderers(root);
            foreach (var r in SMDetailsBuilder.AttachedRenderers(root)) if (!renderers.Contains(r)) renderers.Add(r);
            foreach (var r in renderers)
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (!r.bounds.IntersectRay(ray, out float near) || near > hit.distance) continue;
                var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                if (mesh == null) continue;
                if (MeshRay(ray, mesh, r.transform.localToWorldMatrix, out var h) && h.distance < hit.distance)
                {
                    hit.point = h.point;
                    hit.normal = h.normal;
                    hit.distance = h.distance;
                    hit.renderer = r;
                }
            }
            if (hit.renderer == null) return false;
            if (Vector3.Dot(hit.normal, ray.direction) > 0f) hit.normal = -hit.normal;
            hit.part = SMDecorParts.Resolve(root, hit.renderer.transform);
            return true;
        }

        static bool MeshRay(Ray ray, Mesh mesh, Matrix4x4 m, out RaycastHit h)
        {
            if (!looked)
            {
                looked = true;
                intersect = typeof(HandleUtility).GetMethod("IntersectRayMesh", BindingFlags.Static | BindingFlags.NonPublic);
            }
            if (intersect != null)
            {
                var args = new object[] { ray, mesh, m, null };
                bool ok = (bool)intersect.Invoke(null, args);
                h = (RaycastHit)args[3];
                if (ok)
                {
                    // face normal (the mesh normals of SM parts may point inwards)
                    var tris = mesh.triangles;
                    int t = h.triangleIndex;
                    if (t >= 0 && t * 3 + 2 < tris.Length)
                    {
                        var v = mesh.vertices;
                        Vector3 a = m.MultiplyPoint3x4(v[tris[t * 3]]), b = m.MultiplyPoint3x4(v[tris[t * 3 + 1]]), c = m.MultiplyPoint3x4(v[tris[t * 3 + 2]]);
                        h.normal = Vector3.Cross(b - a, c - a).normalized;
                    }
                }
                return ok;
            }
            return Brute(ray, mesh, m, out h);
        }

        /// <summary>Plain ray-triangle test (Möller–Trumbore) in world space.</summary>
        public static bool Brute(Ray ray, Mesh mesh, Matrix4x4 m, out RaycastHit h)
        {
            h = default;
            var v = mesh.vertices;
            var tris = mesh.triangles;
            var w = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) w[i] = m.MultiplyPoint3x4(v[i]);
            float best = float.PositiveInfinity;
            for (int t = 0; t < tris.Length; t += 3)
            {
                Vector3 a = w[tris[t]], b = w[tris[t + 1]], c = w[tris[t + 2]];
                Vector3 e1 = b - a, e2 = c - a;
                Vector3 p = Vector3.Cross(ray.direction, e2);
                float det = Vector3.Dot(e1, p);
                if (Mathf.Abs(det) < 1e-10f) continue;
                float inv = 1f / det;
                Vector3 s = ray.origin - a;
                float u = Vector3.Dot(s, p) * inv;
                if (u < 0f || u > 1f) continue;
                Vector3 q = Vector3.Cross(s, e1);
                float vv = Vector3.Dot(ray.direction, q) * inv;
                if (vv < 0f || u + vv > 1f) continue;
                float d = Vector3.Dot(e2, q) * inv;
                if (d <= 0f || d >= best) continue;
                best = d;
                h.point = ray.origin + ray.direction * d;
                h.normal = Vector3.Cross(e1, e2).normalized;
                h.distance = d;
            }
            return best < float.PositiveInfinity;
        }
    }
}
