// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: the common part of every decoration (chain, net, cloth). Each one sits on its own
// GameObject under the vehicle part it is attached to (hull / turret / gun ...), keeps the parameters it was made
// from (so it can be rebuilt at any time) and shows the generated mesh.
using UnityEngine;

namespace SMCAD.Details.Decor
{
    public enum SMDecorKind { Chain, Net, Cloth }

    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public abstract class SMDecoration : MonoBehaviour
    {
        [Tooltip("属する装備の組（機体の root）")] public SMDecorSet set;
        [Tooltip("付いている部位（車体・砲塔・砲身など）")] public Transform part;
        public int seed;
        [Tooltip("生成したメッシュ（自動）")] public Mesh mesh;

        [HideInInspector] [Tooltip("作ったときの設定の要約（元に戻す / やり直しで設定が変わったら作り直すため）")] public string buildHash;

        public abstract SMDecorKind Kind { get; }

        /// <summary>The settings the shape is made from, as text (compared with buildHash).</summary>
        public virtual string Params() => seed.ToString();
        public bool OutOfDate => mesh == null || buildHash != Params();
        public virtual string Label => Kind switch { SMDecorKind.Chain => "チェーン", SMDecorKind.Net => "カモネット", _ => "布" };

        protected virtual void OnEnable()
        {
            if (mesh != null && TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != mesh) mf.sharedMesh = mesh;
        }
    }
}
