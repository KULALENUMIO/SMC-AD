// SPDX-License-Identifier: MIT
// SMC-DETAILS decorations: keeping decorations and their generated meshes in step in the editor.
//   - Undo / Redo: a decoration whose settings no longer match its mesh (or whose mesh file is gone) is rebuilt
//   - duplicates (Ctrl+D, copy / paste): a decoration sharing its mesh with another one gets a mesh of its own
//   - removing: the object goes with Undo, its mesh file is deleted once nothing uses it (Undo then rebuilds it)
using System.Collections.Generic;
using System.Linq;
using SMCAD.Details.Decor;
using UnityEditor;
using UnityEngine;

namespace SMCAD.Details.Editor.Decor
{
    [InitializeOnLoad]
    public static class SMDecorEditing
    {
        static SMDecorEditing()
        {
            Undo.undoRedoPerformed += () => Sync();
        }

        static IEnumerable<SMDecoration> All() =>
            Object.FindObjectsByType<SMDecoration>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        /// <summary>Rebuilds every decoration that is out of date (after Undo / Redo). Returns how many.</summary>
        public static int Sync()
        {
            int n = 0;
            foreach (var d in All().ToList())
            {
                if (d == null || d.set == null || !d.OutOfDate) continue;
                Rebuild(d);
                n++;
            }
            return n;
        }

        public static void Rebuild(SMDecoration d)
        {
            if (d is SMDecorChain ch) SMDecorChains.Rebuild(ch);
            else if (d is SMDecorCloth cl) SMDecorCloths.Rebuild(cl);
        }

        /// <summary>Drapes / hangs every decoration of a vehicle again (after the distance field was rebuilt).</summary>
        public static void RebuildAll(SMDecorSet set)
        {
            var list = set.items.Where(x => x != null).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                EditorUtility.DisplayProgressBar("SMC-DETAILS 装備", $"{list[i].name}（{i + 1}/{list.Count}）", (float)i / list.Count);
                try { Rebuild(list[i]); }
                finally { EditorUtility.ClearProgressBar(); }
            }
        }

        /// <summary>Called by the rebuild functions before they write the mesh: a duplicated decoration must not
        /// overwrite the original's mesh file. Returns true when the mesh has to be (re)created.</summary>
        public static bool NeedsOwnMesh(SMDecoration d)
        {
            if (d.mesh == null || !AssetDatabase.Contains(d.mesh)) return true;
            return All().Any(o => o != null && o != d && o.mesh == d.mesh);
        }

        /// <summary>Removes a decoration (Undo-able) and deletes its mesh file when no other decoration uses it.</summary>
        public static void Remove(SMDecoration d)
        {
            if (d == null) return;
            var mesh = d.mesh;
            if (d.set != null)
            {
                Undo.RecordObject(d.set, "装備を外す");
                d.set.items.Remove(d);
            }
            if (SMDecorTool.Current == d) SMDecorTool.Current = null;
            if (SMDecorTool.CurrentCloth == d) SMDecorTool.CurrentCloth = null;
            Undo.DestroyObjectImmediate(d.gameObject);
            if (mesh != null && AssetDatabase.Contains(mesh) && !All().Any(o => o != null && o.mesh == mesh))
                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(mesh));
        }
    }
}
