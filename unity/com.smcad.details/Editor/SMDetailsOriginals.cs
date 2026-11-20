// SPDX-License-Identifier: MIT
// SMC-DETAILS: what each weathered renderer had before (mesh + materials), kept next to the generated assets
// ("Originals.asset" in the blueprint's folder). Lets a set-up recover the true originals when the SMDetails
// component was lost (removed by hand, prefab override reverted) while the renderers still show weathered copies.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SMCAD.Details.Editor
{
    public class SMDetailsOriginals : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public Mesh soup;           // the weathered mesh copy the renderer shows
            public Mesh original;
            public Material[] materials;
        }

        public List<Entry> entries = new List<Entry>();
    }
}
