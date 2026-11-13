// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SMCAD
{
    /// <summary>Root of an imported Scrap Mechanic blueprint.</summary>
    [DisallowMultipleComponent]
    public class SMBlueprint : MonoBehaviour
    {
        [Serializable]
        public struct Shape
        {
            public string uuid;
            public string color; // hex, sRGB
            public int type;     // SMConverter EntityType bit
            public int child;    // index of the child / joint in blueprint.json (-1 if none)
        }

        [Serializable]
        public struct JointInfo
        {
            public int id;
            public string type;
            public string uuid;
            public int bodyA;
            public int bodyB;
        }

        [Tooltip("このプレハブを取り込んだ .glb")]
        public string sourcePath;
        public string blueprintName;
        public int rootBody = -1;
        [Tooltip("パーツの一覧。メッシュの UV2（TEXCOORD2.x）に、頂点ごとにこの一覧の番号が入っています")]
        public List<Shape> shapes = new List<Shape>();
        [Tooltip("機体のすべてのジョイント（輪になっているものも含む）")]
        public List<JointInfo> joints = new List<JointInfo>();
        [Tooltip("SMC-AD の Blender 書き出しで、持ってこられなかったもの（warning）と近似になったもの（info）")]
        public List<string> exportReport = new List<string>();
    }
}
