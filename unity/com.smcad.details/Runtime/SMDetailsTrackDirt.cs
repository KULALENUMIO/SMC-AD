// SPDX-License-Identifier: MIT
// SMC-DETAILS: dirt on track links that a track component rebuilds by itself (e.g. the Tank Rigging plugin's
// "__TrackLinks"). Whenever the links are (re)created, their materials are swapped for weathered belt materials
// (mud or sand only) and every link gets its own pattern (_SmdLinkSeed). Works whatever link design is chosen:
// materials not known at set-up get a copy of the template belt material at run time (not saved as assets).
using System.Collections.Generic;
using UnityEngine;

namespace SMCAD.Details
{
    [ExecuteAlways]
    [AddComponentMenu("")]
    public class SMDetailsTrackDirt : MonoBehaviour
    {
        public string containerName = "__TrackLinks";
        public Material[] from = new Material[0];
        public Material[] to = new Material[0];
        public Material template;

        static MaterialPropertyBlock block;
        readonly Dictionary<Material, Material> extra = new Dictionary<Material, Material>();
        Transform lastContainer;
        int lastCount = -1;

        void OnEnable() { lastContainer = null; lastCount = -1; }

        void OnDisable()
        {
            var c = transform.Find(containerName);
            if (c != null) Swap(c, true);
            foreach (var m in extra.Values) if (m != null) DestroyImmediate(m);
            extra.Clear();
            lastContainer = null;
        }

        void LateUpdate()
        {
            if (refresh)
            {
                refresh = false;
                var c0 = transform.Find(containerName);
                if (c0 != null) Swap(c0, true);   // back to the originals before the run-time copies go
                foreach (var m in extra.Values) if (m != null) DestroyImmediate(m);
                extra.Clear();
                lastContainer = null;
            }
            var c = transform.Find(containerName);
            if (c == null) { lastContainer = null; return; }
            if (c == lastContainer && c.childCount == lastCount) return;
            lastContainer = c;
            lastCount = c.childCount;
            Swap(c, false);
        }

        /// <summary>Re-applies to the current links (after the settings changed).</summary>
        public void Refresh() => refresh = true;   // done in LateUpdate (this is called from OnValidate)
        bool refresh;

        Material Weathered(Material o)
        {
            if (o == null) return null;
            for (int i = 0; i < from.Length; i++) if (from[i] == o) return to[i];
            for (int i = 0; i < to.Length; i++) if (to[i] == o) return o;
            if (extra.ContainsValue(o)) return o;
            if (template == null || !o.HasProperty("_BaseColor")) return o;
            if (!extra.TryGetValue(o, out var c))
            {
                c = new Material(template) { name = o.name + " (汚し・履帯)", hideFlags = HideFlags.DontSave };
                if (o.HasProperty("_BaseMap")) c.SetTexture("_BaseMap", o.GetTexture("_BaseMap"));
                c.SetColor("_BaseColor", o.GetColor("_BaseColor"));
                if (o.HasProperty("_Metallic")) c.SetFloat("_Metallic", o.GetFloat("_Metallic"));
                if (o.HasProperty("_Smoothness")) c.SetFloat("_Smoothness", o.GetFloat("_Smoothness"));
                extra[o] = c;
            }
            return c;
        }

        Material Original(Material w)
        {
            for (int i = 0; i < to.Length; i++) if (to[i] == w) return from[i];
            foreach (var kv in extra) if (kv.Value == w) return kv.Key;
            return w;
        }

        void Swap(Transform container, bool restore)
        {
            block ??= new MaterialPropertyBlock();
            for (int i = 0; i < container.childCount; i++)
            {
                var link = container.GetChild(i);
                float seed = Mathf.Repeat(i * 0.6180339f, 1f) * 23f;
                foreach (var r in link.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    for (int k = 0; k < mats.Length; k++) mats[k] = restore ? Original(mats[k]) : Weathered(mats[k]);
                    r.sharedMaterials = mats;
                    if (restore)
                    {
                        r.SetPropertyBlock(null);   // the track plugin sets no property blocks of its own
                        continue;
                    }
                    r.GetPropertyBlock(block);
                    block.SetFloat("_SmdLinkSeed", seed);
                    r.SetPropertyBlock(block);
                }
            }
        }
    }
}
