// SPDX-License-Identifier: MIT
using UnityEngine;

namespace SMCAD
{
    /// <summary>One physical body of the blueprint (a joint separated object).</summary>
    [DisallowMultipleComponent]
    public class SMBody : MonoBehaviour
    {
        public int bodyIndex = -1;
        public bool isRoot;
        [Tooltip("オフのときは、どのジョイントでも機体の他の部分とつながっていません")]
        public bool connected = true;
    }
}
