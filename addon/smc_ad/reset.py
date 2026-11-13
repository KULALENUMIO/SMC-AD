# SPDX-License-Identifier: GPL-3.0-or-later
"""Back to the state right after import: materials, textures, slot layout, override - for repeatable tests.

What the importer records:
  object["smcad_import_slots"]       {"0": Material, "1": Material, ...}  the material slots after import
  object["smcad_import_slot_count"]  number of slots after import
  root["smcad_import_options"]       {"specular": .., "glow": ..}         import settings used for rebuilding

Reset does, per scope:
  1. extensions clear their own state (api.before_reset)
  2. faces go back to the imported materials and the slot layout is restored exactly
     (copies made for parts are mapped to their original through api.original_material)
  3. the materials are rebuilt from the source .glb (fresh nodes and textures; hand-made nodes are
     removed unless keep_user_nodes), and the colour override is reset to its defaults
  4. copies and textures that nothing uses any more are deleted
  5. extensions restore their import-time state (api.after_reset)
Joint poses (object transforms) are not touched.
"""

import os

import bpy
import numpy as np

from . import api, batch, importer

SLOTS_PROP = "smcad_import_slots"
SLOT_COUNT_PROP = "smcad_import_slot_count"
OPTIONS_PROP = "smcad_import_options"

# The scope of the reset that is running ("SELECTED", "BLUEPRINT", "SCENE" or "MATERIAL"), for extensions
CURRENT_SCOPE = None


# --------------------------------------------------------------------------------------------
# Recording (at import)
# --------------------------------------------------------------------------------------------

def record_import(objects, root, specular, glow):
    for obj in objects:
        if obj.type != "MESH":
            continue
        obj[SLOTS_PROP] = {str(i): s.material for i, s in enumerate(obj.material_slots) if s.material is not None}
        obj[SLOT_COUNT_PROP] = len(obj.material_slots)
    if root is not None:
        options = {"specular": float(specular), "glow": float(glow)}
        root[OPTIONS_PROP] = options
        # every object keeps the source too, so deleting the root Empty doesn't cut the link to the .glb
        for obj in objects:
            if obj != root:
                obj["smcad_source_file"] = root.get("smcad_source", "")
                obj[OPTIONS_PROP] = options


def recorded_slots(obj):
    """The material per slot right after import, or None for objects imported before recording existed."""
    rec = obj.get(SLOTS_PROP)
    count = obj.get(SLOT_COUNT_PROP)
    if rec is None or count is None:
        return None
    return [rec.get(str(i)) for i in range(int(count))]


def original_of(material):
    """The imported material a copy was made from (asks the extensions), or the material itself."""
    if material is None:
        return None
    for hook in list(api.original_material):
        try:
            found = hook(material)
        except Exception:  # noqa: BLE001
            found = None
        if found is not None:
            return found
    return material


# --------------------------------------------------------------------------------------------
# Slots
# --------------------------------------------------------------------------------------------

def restore_slots(obj):
    """Faces back on their imported materials, slots exactly as after import. Returns the materials dropped."""
    mesh = obj.data
    current = [s.material for s in obj.material_slots]
    target = recorded_slots(obj)
    if target is None:  # older import: map copies to their original and merge duplicate slots
        target = []
        for mat in current:
            orig = original_of(mat)
            if orig not in target:
                target.append(orig)

    lut = []
    for mat in current:
        orig = original_of(mat)
        if orig in target:
            lut.append(target.index(orig))
        elif mat in target:
            lut.append(target.index(mat))
        else:  # a material the user added by hand: keep it in an extra slot
            target.append(mat)
            lut.append(len(target) - 1)

    n = len(mesh.polygons)
    idx = np.zeros(n, dtype=np.int32)
    if n:
        mesh.polygons.foreach_get("material_index", idx)
    if lut:
        lut_arr = np.array(lut, dtype=np.int32)
        idx = lut_arr[np.clip(idx, 0, len(lut) - 1)]

    dropped = [m for m in current if m is not None and m not in target]
    while len(mesh.materials) > len(target):
        mesh.materials.pop()
    while len(mesh.materials) < len(target):
        mesh.materials.append(None)
    for i, mat in enumerate(target):
        if obj.material_slots[i].link == "OBJECT":
            obj.material_slots[i].material = mat
        else:
            mesh.materials[i] = mat
    if n:
        mesh.polygons.foreach_set("material_index", idx)
    mesh.update()
    return dropped


# --------------------------------------------------------------------------------------------
# Materials
# --------------------------------------------------------------------------------------------

def _reset_override(material):
    ov = material.smcad_override
    for key in ov.bl_rna.properties.keys():
        if key != "rna_type":
            ov.property_unset(key)


def reset_materials(materials, keep_user_nodes=False):
    """Rebuilds materials from their source .glb. Returns (rebuilt, missing_sources)."""
    from . import overrides

    by_source = {}
    for mat in materials:
        src, options = None, {}
        for obj in bpy.data.objects:
            if obj.type == "MESH" and any(s.material == mat for s in obj.material_slots):
                path, opts = batch.source_of(obj)
                if path:
                    src, options = path, opts
                    break
        by_source.setdefault((src, options.get("specular", 1.0), options.get("glow", 1.0)), []).append(mat)

    old_images = {n.image for m in materials if m.node_tree for n in m.node_tree.nodes
                  if n.bl_idname == "ShaderNodeTexImage" and n.image is not None}
    rebuilt, missing = 0, []
    for (src, specular, glow), mats in by_source.items():
        if not src or not os.path.isfile(src):
            missing.append(src or "?")
            continue
        for mat in mats:
            with overrides.suspend_updates():
                _reset_override(mat)
            if not keep_user_nodes and mat.node_tree is not None:
                mat.node_tree.nodes.clear()
        rebuilt += importer.rebuild_materials(mats, src, specular, glow)

    for image in old_images:
        if image.users == 0:
            bpy.data.images.remove(image)
    return rebuilt, missing


def _remove_unused_core_groups():
    """Node groups made on demand (e.g. the colour override) that nothing uses after the reset."""
    for name in ("SMC_DiffuseOverride",):
        group = bpy.data.node_groups.get(name)
        if group is not None and group.users == 0:
            bpy.data.node_groups.remove(group)


def reset_to_import(context, objects, keep_user_nodes=False, scope="BLUEPRINT"):
    """Everything in `objects` back to the import state. Returns a summary dict."""
    global CURRENT_SCOPE
    CURRENT_SCOPE = scope
    try:
        return _reset_objects(context, objects, keep_user_nodes)
    finally:
        CURRENT_SCOPE = None


def _reset_objects(context, objects, keep_user_nodes):
    objects = [o for o in objects if o.type == "MESH"]
    before = {s.material for o in objects for s in o.material_slots if s.material is not None}
    materials = [m for m in {original_of(m) for m in before} if m is not None and "sm_shader" in m]

    api.call(api.before_reset, context, objects, materials)

    dropped = set()
    for obj in objects:
        legacy = recorded_slots(obj) is None
        dropped.update(restore_slots(obj))
        if legacy:  # imported before recording existed: the restored layout is the import layout from now on
            obj[SLOTS_PROP] = {str(i): s.material for i, s in enumerate(obj.material_slots) if s.material is not None}
            obj[SLOT_COUNT_PROP] = len(obj.material_slots)

    rebuilt, missing = reset_materials(materials, keep_user_nodes)

    removed = 0
    for mat in dropped:
        if mat.users == 0 and "sm_shader" in mat:
            bpy.data.materials.remove(mat)
            removed += 1
    _remove_unused_core_groups()

    api.call(api.after_reset, context, objects, materials)
    return {"materials": rebuilt, "copies_removed": removed, "missing_sources": missing}


def reset_material(context, material, keep_user_nodes=False):
    """One material (and the copies made from it) back to the import state, on every object."""
    global CURRENT_SCOPE
    CURRENT_SCOPE = "MATERIAL"
    try:
        return _reset_one(context, material, keep_user_nodes)
    finally:
        CURRENT_SCOPE = None


def _reset_one(context, material, keep_user_nodes):
    orig = original_of(material)
    if orig is None or "sm_shader" not in orig:
        return None
    objects = [o for o in bpy.data.objects if o.type == "MESH"
               and any(original_of(s.material) == orig for s in o.material_slots if s.material is not None)]
    api.call(api.before_reset, context, objects, [orig])
    copies = set()
    for obj in objects:
        for slot in obj.material_slots:
            mat = slot.material
            if mat is not None and mat != orig and original_of(mat) == orig:
                copies.add(mat)
                slot.material = orig  # the faces keep their slot, now showing the original
    rebuilt, missing = reset_materials([orig], keep_user_nodes)
    removed = 0
    for mat in copies:
        if mat.users == 0:
            bpy.data.materials.remove(mat)
            removed += 1
    _remove_unused_core_groups()
    api.call(api.after_reset, context, objects, [orig])
    return {"materials": rebuilt, "copies_removed": removed, "missing_sources": missing}
