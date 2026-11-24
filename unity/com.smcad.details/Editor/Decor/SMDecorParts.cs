// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: which moving part of the vehicle a decoration belongs to (hull / turret / gun ...).
// With the Tank Rigging plugin its TankController (hull / turret / gun / barrel / secondary turrets) is read by
// reflection, so there is no dependency on the plugin; without it, SMC-AD joints (SMJoint) split the parts.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    public static class SMDecorParts
    {
        public struct Part
        {
            public Transform transform;
            public string label;
        }

        /// <summary>The moving parts of the vehicle: the root (hull) first, then the rig's parts and SMC-AD joints.</summary>
        public static List<Part> All(Transform root)
        {
            var list = new List<Part> { new Part { transform = root, label = "車体" } };
            void Add(Transform t, string label)
            {
                if (t == null || list.Any(p => p.transform == t)) return;
                list.Add(new Part { transform = t, label = label });
            }
            foreach (var rig in Rigs(root))
            {
                Add(Field(rig, "turret"), "砲塔");
                Add(Field(rig, "gun"), "砲架");
                Add(Field(rig, "barrel"), "砲身");
                if (rig.GetType().GetField("secondaryTurrets", BindingFlags.Public | BindingFlags.Instance)?.GetValue(rig) is IEnumerable sts)
                {
                    int i = 1;
                    foreach (var st in sts)
                    {
                        if (st == null) continue;
                        string name = st.GetType().GetField("name")?.GetValue(st) as string;
                        string head = string.IsNullOrEmpty(name) ? $"副砲塔 {i}" : name;
                        Add(Field(st, "turret"), head);
                        Add(Field(st, "gun"), head + " 砲架");
                        Add(Field(st, "barrel"), head + " 砲身");
                        i++;
                    }
                }
            }
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == "SMJoint") Add(c.transform, "関節 " + c.name);
            return list;
        }

        /// <summary>The part a point on this transform belongs to: the nearest part among its ancestors (else the hull).</summary>
        public static Part Resolve(Transform root, Transform hit) => Resolve(All(root), root, hit);

        /// <summary>Same, with the part list made once (All is slow on big vehicles).</summary>
        public static Part Resolve(List<Part> parts, Transform root, Transform hit)
        {
            for (var t = hit; t != null; t = t.parent)
            {
                foreach (var p in parts.Skip(1)) if (p.transform == t) return p;
                if (t == root) break;
            }
            return parts[0];
        }

        public static string Label(Transform root, Transform part)
        {
            foreach (var p in All(root)) if (p.transform == part) return p.label;
            return part != null ? part.name : "—";
        }

        /// <summary>Components of the vehicle's rig (anything with a Transform field "turret"), on the root, its parents
        /// or the root's own children.</summary>
        static IEnumerable<Component> Rigs(Transform root)
        {
            var seen = new HashSet<Component>();
            IEnumerable<Component> Candidates()
            {
                for (var t = root; t != null; t = t.parent)
                    foreach (var c in t.GetComponents<Component>()) yield return c;
                foreach (var c in root.GetComponentsInChildren<Component>(true)) yield return c;
            }
            foreach (var c in Candidates())
            {
                if (c == null || !seen.Add(c)) continue;
                var f = c.GetType().GetField("turret", BindingFlags.Public | BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(Transform)) yield return c;
            }
        }

        static Transform Field(object o, string name) =>
            o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o) as Transform;
    }
}
