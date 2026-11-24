// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: creating / rebuilding chains in the editor (mesh asset per chain, material presets).
using System.Collections.Generic;
using System.Linq;
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SMCAD.Details.Editor.Decor
{
    public static class SMDecorChains
    {
        /// <summary>A new chain along a ridge (root-local a -> b), on the ridge's part.</summary>
        public static SMDecorChain Create(SMDecorSet set, SMDecorRidge ridge, Vector3 nearRoot)
        {
            var root = set.transform;
            var part = ridge.part != null ? ridge.part : root;
            // start at the end nearer to the click
            Vector3 a = ridge.a, b = ridge.b;
            if (Vector3.Distance(nearRoot, b) < Vector3.Distance(nearRoot, a)) (a, b) = (b, a);
            var go = new GameObject("チェーン " + (set.items.Count(x => x is SMDecorChain) + 1));
            Undo.RegisterCreatedObjectUndo(go, "チェーンを付ける");
            go.transform.SetParent(part, false);
            go.layer = part.gameObject.layer;
            var chain = go.AddComponent<SMDecorChain>();
            chain.set = set;
            chain.part = part;
            chain.seed = Random.Range(0, 100000);
            var partFromRoot = part.worldToLocalMatrix * root.localToWorldMatrix;
            chain.path = new List<Vector3> { partFromRoot.MultiplyPoint3x4(a), partFromRoot.MultiplyPoint3x4(b) };
            var o = partFromRoot.MultiplyVector(ridge.outward).normalized;
            chain.outward = new List<Vector3> { o, o };
            Undo.RecordObject(set, "チェーンを付ける");
            set.items.Add(chain);
            Rebuild(chain);
            return chain;
        }

        /// <summary>Joins a ridge to the end of the chain when one of its ends touches the chain's last point.</summary>
        public static bool Append(SMDecorChain chain, SMDecorRidge ridge)
        {
            var root = chain.set.transform;
            var partFromRoot = chain.part.worldToLocalMatrix * root.localToWorldMatrix;
            Vector3 a = partFromRoot.MultiplyPoint3x4(ridge.a), b = partFromRoot.MultiplyPoint3x4(ridge.b);
            var o = partFromRoot.MultiplyVector(ridge.outward).normalized;
            const float tol = 0.06f;
            Undo.RecordObject(chain, "チェーンをつなぐ");
            var end = chain.path[chain.path.Count - 1];
            var start = chain.path[0];
            if (Vector3.Distance(end, a) < tol || Vector3.Distance(end, b) < tol)
            {
                var far = Vector3.Distance(end, a) < tol ? b : a;
                chain.outward[chain.outward.Count - 1] = (chain.outward[chain.outward.Count - 1] + o).normalized;
                chain.path.Add(far); chain.outward.Add(o);
            }
            else if (Vector3.Distance(start, a) < tol || Vector3.Distance(start, b) < tol)
            {
                var far = Vector3.Distance(start, a) < tol ? b : a;
                chain.outward[0] = (chain.outward[0] + o).normalized;
                chain.path.Insert(0, far); chain.outward.Insert(0, o);
            }
            else return false;
            Rebuild(chain);
            return true;
        }

        public static void Rebuild(SMDecorChain chain)
        {
            var set = chain.set;
            if (set == null || chain.part == null) return;
            var root = set.transform;
            string folder = SMDecorField.Folder(set, root);
            if (SMDecorEditing.NeedsOwnMesh(chain))
            {
                var m = new Mesh { name = "Chain_" + chain.seed };
                string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/Chain_" + chain.seed + ".asset");
                AssetDatabase.CreateAsset(m, path);
                chain.mesh = m;
            }
            var rootFromPart = root.worldToLocalMatrix * chain.part.localToWorldMatrix;
            var down = root.InverseTransformDirection(Vector3.down);
            var st = SMDecorChainGen.Build(chain, rootFromPart, down, chain.mesh);
            chain.length = st.length;
            chain.links = st.links;
            chain.hangPoints = st.hangPoints;
            chain.maxPenetration = st.maxPenetration;
            var mf = chain.GetComponent<MeshFilter>();
            mf.sharedMesh = chain.mesh;
            chain.GetComponent<MeshRenderer>().sharedMaterial = Material(folder, chain.finish);
            EditorUtility.SetDirty(chain.mesh);
            EditorUtility.SetDirty(chain);
            chain.buildHash = chain.Params();
            var motion = chain.GetComponent<SMDecorMotion>();
            if (motion == null) motion = chain.gameObject.AddComponent<SMDecorMotion>();
            motion.Invalidate();
            EditorSceneManager.MarkSceneDirty(chain.gameObject.scene);
        }

        static readonly string[] FinishNames = { "Steel", "Rust", "Paint" };

        /// <summary>HDRP/Lit material per finish, shared by the vehicle's decorations (Decor_Chain_*.mat).</summary>
        public static Material Material(string folder, SMDecorFinish finish)
        {
            string path = folder + "/Decor_Chain_" + FinishNames[(int)finish] + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            mat = new Material(SMCAD.Editor.SMMaterialSetup.FindShader("HDRP/Lit"));
            switch (finish)
            {
                case SMDecorFinish.Steel:
                    mat.SetColor("_BaseColor", new Color(0.13f, 0.13f, 0.13f)); mat.SetFloat("_Metallic", 0.85f); mat.SetFloat("_Smoothness", 0.45f); break;
                case SMDecorFinish.Rust:
                    mat.SetColor("_BaseColor", new Color(0.30f, 0.13f, 0.06f)); mat.SetFloat("_Metallic", 0.25f); mat.SetFloat("_Smoothness", 0.22f); break;
                default:
                    mat.SetColor("_BaseColor", new Color(0.22f, 0.24f, 0.17f)); mat.SetFloat("_Metallic", 0.1f); mat.SetFloat("_Smoothness", 0.3f); break;
            }
            mat.SetFloat("_DoubleSidedEnable", 1f);
            HDMaterial.ValidateMaterial(mat);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
