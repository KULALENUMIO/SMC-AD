# SPDX-License-Identifier: GPL-3.0-or-later
"""Scopes and paint lookups for the batch Diffuse Override operations.

Scope
  SELECTED   the selected objects
  BLUEPRINT  every body of the blueprint(s) the selected / active objects belong to
             (the import root that carries smcad_source, and all its children)
  SCENE      every object in the current view layer (excluded collections are left alone)
"""

import bpy
import numpy as np
from bpy.props import EnumProperty

from . import materials

SCOPE_ITEMS = [
    ("SELECTED", "選択中", "選択しているオブジェクト"),
    ("BLUEPRINT", "機体全体", "選択中（またはアクティブ）のオブジェクトが属する機体のすべてのボディ"),
    ("SCENE", "シーン", "今のビューレイヤーにあるすべてのオブジェクト"),
]


def scope_property(default="BLUEPRINT"):
    return EnumProperty(name="範囲", items=SCOPE_ITEMS, default=default)


# --------------------------------------------------------------------------------------------
# Objects and materials in a scope
# --------------------------------------------------------------------------------------------

def blueprint_root(obj):
    """The import root of an SMC-AD blueprint (carries smcad_source / sm_shapes), or None.
    Bodies re-parented outside the blueprint (e.g. to bones) are found through their blueprint id."""
    start = obj
    root = _root_by_parents(obj)
    if root is None and start is not None and start.get("smcad_blueprint_id"):
        bp_id = start["smcad_blueprint_id"]
        members = [o for o in bpy.data.objects if o.get("smcad_blueprint_id") == bp_id]
        root = next((o for o in members if o.get("smcad_source")), None)
        if root is None:
            # the import root (Empty) was deleted: the topmost object of the blueprint stands in for it
            tops = [o for o in members if o.parent is None or o.parent.get("smcad_blueprint_id") != bp_id]
            tops.sort(key=lambda o: (not o.get("sm_root"), o.name))
            root = tops[0] if tops else None
    return root


SOURCE_PROP = "smcad_source_file"      # on every object of an import: the .glb it came from
OPTIONS_PROP = "smcad_import_options"


def source_of(obj):
    """(source .glb path, import options) of an object's blueprint - also when the import root was deleted."""
    o = obj
    while o is not None:
        if o.get("smcad_source"):
            return str(o["smcad_source"]), dict(o.get(OPTIONS_PROP, {}))
        o = o.parent
    if obj is not None and obj.get(SOURCE_PROP):
        return str(obj[SOURCE_PROP]), dict(obj.get(OPTIONS_PROP, {}))
    bp_id = obj.get("smcad_blueprint_id") if obj is not None else None
    if bp_id:
        for o in bpy.data.objects:
            if o.get("smcad_blueprint_id") == bp_id and (o.get("smcad_source") or o.get(SOURCE_PROP)):
                return str(o.get("smcad_source") or o[SOURCE_PROP]), dict(o.get(OPTIONS_PROP, {}))
    return "", {}


def blueprint_name(root):
    """The blueprint's own name (also when the import root was deleted and a body stands in for it)."""
    if root is None:
        return ""
    name = root.get("smcad_blueprint_name") or root.get("sm_blueprint_name")
    if name:
        return str(name)
    if root.get("smcad_source"):
        return root.name
    base = root.name.rsplit("_", 1)
    return base[0] if len(base) == 2 and base[1].isdigit() else root.name


def blueprint_objects(root):
    """Every object of a blueprint: the root's hierarchy plus objects tagged with its id (re-parented bodies)."""
    if root is None:
        return []
    result = [root] + list(root.children_recursive)
    bp_id = root.get("smcad_blueprint_id")
    if bp_id:
        seen = set(result)
        for o in bpy.data.objects:
            if o not in seen and o.get("smcad_blueprint_id") == bp_id:
                result.append(o)
                seen.add(o)
    return result


def _root_by_parents(obj):
    while obj is not None:
        if obj.get("smcad_source") or obj.get("sm_shapes") is not None:
            return obj
        obj = obj.parent
    return None


def _seed_objects(context):
    seeds = list(context.selected_objects)
    if context.active_object is not None and context.active_object not in seeds:
        seeds.append(context.active_object)
    return seeds


def scope_objects(context, scope):
    """Mesh objects of the scope, in a stable order."""
    if scope == "SELECTED":
        objects = list(context.selected_objects)
    elif scope == "BLUEPRINT":
        objects, seen_roots = [], set()
        for obj in _seed_objects(context):
            root = blueprint_root(obj)
            if root is None:
                objects.append(obj)  # not part of an import: treat the object itself as its scope
            elif root not in seen_roots:
                seen_roots.add(root)
                objects.extend(blueprint_objects(root))
    else:
        objects = list(context.view_layer.objects)

    seen, result = set(), []
    for obj in objects:
        if obj.type == "MESH" and obj not in seen:
            seen.add(obj)
            result.append(obj)
    return result


def is_sm_material(material):
    return material is not None and "sm_shader" in material


def scope_materials(objects):
    """SM materials used by the objects, in first-seen order."""
    seen, result = set(), []
    for obj in objects:
        for slot in obj.material_slots:
            mat = slot.material
            if is_sm_material(mat) and mat not in seen:
                seen.add(mat)
                result.append(mat)
    return result


# --------------------------------------------------------------------------------------------
# Paint colours
# --------------------------------------------------------------------------------------------

def _linear_to_srgb(c):
    c = np.clip(c, 0.0, 1.0)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(c, 1.0 / 2.4) - 0.055)


def object_paint(obj):
    """{material_slot_index: {hex: vertex_count}} of the paint colours on an object's faces.
    hex is the sRGB colour as shown in the game ("RRGGBB")."""
    mesh = obj.data
    attr = mesh.color_attributes.get(materials.PAINT_ATTRIBUTE)
    if attr is None or len(attr.data) == 0 or len(mesh.polygons) == 0:
        return {}

    colors = np.empty(len(attr.data) * 4, dtype=np.float32)
    attr.data.foreach_get("color", colors)
    colors = colors.reshape(-1, 4)[:, :3]

    mat_idx = np.empty(len(mesh.polygons), dtype=np.int64)
    mesh.polygons.foreach_get("material_index", mat_idx)
    loop_total = np.empty(len(mesh.polygons), dtype=np.int64)
    mesh.polygons.foreach_get("loop_total", loop_total)
    loop_mat = np.repeat(mat_idx, loop_total)

    if attr.domain == "CORNER":
        picked, picked_mat = colors, loop_mat
    else:
        # Point domain: count each (vertex, material) once
        loop_vert = np.empty(len(mesh.loops), dtype=np.int64)
        mesh.loops.foreach_get("vertex_index", loop_vert)
        pairs = np.unique(np.stack([loop_vert, loop_mat], axis=1), axis=0)
        picked, picked_mat = colors[pairs[:, 0]], pairs[:, 1]

    srgb = np.round(_linear_to_srgb(picked) * 255).astype(np.int64)
    keys = (picked_mat << 24) | (srgb[:, 0] << 16) | (srgb[:, 1] << 8) | srgb[:, 2]
    uniq, counts = np.unique(keys, return_counts=True)

    result = {}
    for key, n in zip(uniq.tolist(), counts.tolist()):
        result.setdefault(key >> 24, {})["%06X" % (key & 0xFFFFFF)] = n
    return result


def paint_by_material(objects):
    """{material: {hex: vertex_count}} over the objects."""
    result = {}
    for obj in objects:
        for slot_index, colors in object_paint(obj).items():
            if slot_index >= len(obj.material_slots):
                continue
            mat = obj.material_slots[slot_index].material
            if mat is None:
                continue
            bucket = result.setdefault(mat, {})
            for hx, n in colors.items():
                bucket[hx] = bucket.get(hx, 0) + n
    return result


def paint_totals(objects):
    """[(hex, vertex_count)] over the objects, most used first."""
    totals = {}
    for colors in paint_by_material(objects).values():
        for hx, n in colors.items():
            totals[hx] = totals.get(hx, 0) + n
    return sorted(totals.items(), key=lambda kv: -kv[1])


def object_has_paint(obj, hex_str):
    return any(hex_str in colors for colors in object_paint(obj).values())
