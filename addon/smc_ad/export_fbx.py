# SPDX-License-Identifier: GPL-3.0-or-later
"""Export for Unity as FBX: the blueprint exactly as it is in Blender.

What goes into the FBX (Blender's FBX exporter):
  objects      the blueprint root, its bodies (detached ones left out), and every armature the bodies hang
               on (bone parent or Armature modifier) with everything under it: positions, rotations,
               scale, moved origins, re-parenting, bones, weights. No animation (that is made in Unity).
  paint        the "Color" attribute as linear vertex colours
  part ids     _SM_SHAPE as a second UV channel "SM_Part" (u = part id); Unity moves it to UV2 for the shader
  SM data      one JSON string on the root object (custom property "smcad_unity"): materials (shader, textures,
               colour override, surface type, layer nodes), bodies (body index, joint, axis), report

Textures go next to the FBX into "<name>_textures/<dif|asg|nor|override>/", referenced by the JSON.
The Unity package (SMFbxPostprocessor) turns the FBX materials into SM materials and adds the SM components.
"""

import json
import os
import re

import bpy
import numpy as np
from mathutils import Vector

from . import api, batch, bodies, export

PROPERTY = "smcad_unity"
PART_UV = "SM_Part"
FORMAT_VERSION = 1


def _safe(name):
    return re.sub(r"[^0-9A-Za-z_.-]+", "_", name).strip("_") or "item"


# --------------------------------------------------------------------------------------------
# What to export
# --------------------------------------------------------------------------------------------

def collect_objects(root):
    """Root + bodies + the armatures they use (with everything under those), without detached bodies."""
    objs = [o for o in batch.blueprint_objects(root) if not bodies.is_detached(o)]
    armatures = []
    for o in list(objs):
        p = o.parent
        while p is not None:
            if p.type == "ARMATURE" and p not in armatures:
                armatures.append(p)
            p = p.parent
        for mod in getattr(o, "modifiers", []):
            if mod.type == "ARMATURE" and mod.object is not None and mod.object not in armatures:
                armatures.append(mod.object)
    result = list(objs)
    for arm in armatures:
        for o in [arm] + list(arm.children_recursive):
            if o not in result and not bodies.is_detached(o):
                result.append(o)
    # parents up to the top, so the hierarchy is complete
    for o in list(result):
        p = o.parent
        while p is not None:
            if p not in result:
                result.append(p)
            p = p.parent
    return [o for o in result if o.type in {"MESH", "EMPTY", "ARMATURE"}], armatures


# --------------------------------------------------------------------------------------------
# SM data
# --------------------------------------------------------------------------------------------

def _write_texture(image, role, tex_dir, folder_name, cache):
    if image is None:
        return None
    if image in cache:
        return cache[image]
    data = export.image_bytes(image)
    if data is None:
        return None
    ext = ".jpg" if data[:2] == b"\xff\xd8" else ".png"
    rel = "%s/%s/%s%s" % (folder_name, role, _safe(os.path.splitext(image.name)[0]), ext)
    path = os.path.join(os.path.dirname(tex_dir), rel.replace("/", os.sep))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)
    cache[image] = rel
    return rel


def _override_data(material, tex_dir, folder_name, cache, report):
    ov = material.smcad_override
    if not ov.enabled:
        return None
    data = {
        "target": ov.target, "source": ov.source, "color": export._plain(ov.color), "projection": ov.projection,
        "scale": export._plain(ov.scale), "strength": export._plain(ov.strength),
        "match_enabled": bool(ov.match_enabled), "match_color": export._plain(ov.match_color),
        "match_tolerance": export._plain(ov.match_tolerance),
    }
    if ov.source == "IMAGE":
        rel = _write_texture(ov.image, "override", tex_dir, folder_name, cache)
        if rel is None:
            data["source"] = "COLOR"
            if ov.image is not None:
                report.add("warning", material.name, "色の差し替えの画像「%s」を読めないため、色で書き出しました" % ov.image.name)
        else:
            data["image_path"] = rel
    return data


def material_data(material, tex_dir, folder_name, cache, report):
    tree = material.node_tree
    nodes = tree.nodes if tree is not None else {}
    textures = {}
    for role, node_name in (("dif", "SMC.Tex.Dif"), ("asg", "SMC.Tex.Asg"), ("nor", "SMC.Tex.Nor")):
        node = nodes.get(node_name) if tree is not None else None
        rel = _write_texture(node.image if node is not None else None, role, tex_dir, folder_name, cache)
        if rel:
            textures[role] = rel
    smcad = {}
    ov = _override_data(material, tex_dir, folder_name, cache, report)
    if ov:
        smcad["override"] = ov
    layers = export.layer_extras(material, report)
    if layers:
        smcad["layers"] = layers
    for hook in list(api.export_material):
        try:
            hook(material, smcad, report)
        except Exception as ex:  # noqa: BLE001
            report.add("warning", material.name, "拡張機能の書き出しでエラー: %s" % ex)
    return {"sm_shader": int(material.get("sm_shader", 1)), "textures": textures, "smcad": smcad}


def _to_unity(v):
    """Blender world vector -> Unity world vector for FBX exported with forward -Z, up Y (Unity mirrors X)."""
    return [round(-float(v[0]), 6), round(float(v[2]), 6), round(-float(v[1]), 6)]


def body_data(obj):
    data = {"sm_body": int(obj["sm_body"]), "root": bool(obj.get("sm_root", False)),
            "connected": bool(obj.get("sm_connected", True)), "part_uv": PART_UV in obj.data.uv_layers}
    joint = obj.get("sm_joint")
    if joint is not None:
        axis = (obj.matrix_world.to_3x3() @ Vector((0, 0, 1))).normalized()
        data["joint"] = {"type": str(joint.get("type", "")), "id": int(joint.get("id", -1)),
                         "uuid": str(joint.get("uuid", "")), "parent_body": int(joint.get("parent_body", -1)),
                         "axis_world": _to_unity(axis)}
    return data


# --------------------------------------------------------------------------------------------
# Part ids as UVs (temporary, removed after the export)
# --------------------------------------------------------------------------------------------

def _add_part_uv(mesh):
    attr = mesh.attributes.get(export.SHAPE_ATTRIBUTE)
    if attr is None or PART_UV in mesh.uv_layers or len(mesh.loops) == 0:
        return False
    values = np.empty(len(attr.data), dtype=np.float32)
    attr.data.foreach_get("value", values)
    if attr.domain == "POINT":
        loop_vert = np.empty(len(mesh.loops), dtype=np.int64)
        mesh.loops.foreach_get("vertex_index", loop_vert)
        per_loop = values[loop_vert]
    elif attr.domain == "CORNER":
        per_loop = values
    else:
        return False
    active = mesh.uv_layers.active
    uv = mesh.uv_layers.new(name=PART_UV, do_init=False)
    if uv is None:
        return False
    data = np.zeros(len(mesh.loops) * 2, dtype=np.float32)
    data[0::2] = per_loop
    uv.data.foreach_set("uv", data)
    if active is not None:
        mesh.uv_layers.active = active
        active.active_render = True
    return True


def _remove_part_uv(mesh):
    uv = mesh.uv_layers.get(PART_UV)
    if uv is not None:
        mesh.uv_layers.remove(uv)


# --------------------------------------------------------------------------------------------
# Export
# --------------------------------------------------------------------------------------------

def export_fbx(context, root, filepath):
    """Writes the FBX + textures. Returns the Report."""
    report = export.Report()
    objs, armatures = collect_objects(root)
    meshes = [o for o in objs if o.type == "MESH"]
    stem = os.path.splitext(os.path.basename(filepath))[0]
    folder_name = stem + "_textures"
    tex_dir = os.path.join(os.path.dirname(filepath), folder_name)

    detached = bodies.detached_in(root)
    if detached:
        report.add("info", "", "外したボディ（%s）は書き出しに含めていません" % "、".join(o.name for o in detached))
    for o in meshes:
        others = [m.type for m in o.modifiers if m.type != "ARMATURE" and m.show_viewport]
        if others:
            report.add("info", o.name, "モディファイア（%s）は適用した形で書き出しました" % "、".join(others))
    if armatures:
        bones = sum(len(a.data.bones) for a in armatures)
        report.add("info", "", "アーマチュア %d 個（骨 %d 本）を書き出しました。アニメーションは含めません" % (len(armatures), bones))

    # temporary data: part id UVs and the SM JSON on the root
    added_uv = []
    for o in meshes:
        if o.data.users == 1 and _add_part_uv(o.data):
            added_uv.append(o.data)

    cache = {}
    materials = {}
    for o in meshes:
        for slot in o.material_slots:
            mat = slot.material
            if mat is not None and "sm_shader" in mat and mat.name not in materials:
                materials[mat.name] = material_data(mat, tex_dir, folder_name, cache, report)

    payload = {
        "version": FORMAT_VERSION,
        "blueprint": batch.blueprint_name(root),
        "root_object": root.name,
        "source": os.path.basename(batch.source_of(root)[0]),
        "blender": bpy.app.version_string,
        "texture_folder": folder_name,
        "materials": materials,
        "bodies": {o.name: body_data(o) for o in meshes if o.get("sm_body") is not None},
        "report": report.items,
    }
    root[PROPERTY] = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))

    try:
        view_layer = context.view_layer
        previous_selection = [o for o in context.selected_objects]
        previous_active = view_layer.objects.active
        for o in previous_selection:
            o.select_set(False)
        hidden = []
        for o in objs:
            if view_layer.objects.get(o.name) is None:
                continue
            if o.hide_get():
                hidden.append(o)
                o.hide_set(False)
            o.select_set(True)
        view_layer.objects.active = root if view_layer.objects.get(root.name) else objs[0]
        result = bpy.ops.export_scene.fbx(
            filepath=filepath, use_selection=True, object_types={"EMPTY", "ARMATURE", "MESH"},
            use_mesh_modifiers=True, mesh_smooth_type="FACE", colors_type="LINEAR", prioritize_active_color=False,
            add_leaf_bones=False, bake_anim=False, axis_forward="-Z", axis_up="Y",
            apply_scale_options="FBX_SCALE_ALL", use_custom_props=True, path_mode="STRIP", embed_textures=False)
        if "FINISHED" not in result:
            raise RuntimeError("FBX の書き出しに失敗しました")
    finally:
        for o in objs:
            if view_layer.objects.get(o.name) is not None:
                o.select_set(False)
        for o in hidden:
            o.hide_set(True)
        for o in previous_selection:
            if view_layer.objects.get(o.name) is not None:
                o.select_set(True)
        view_layer.objects.active = previous_active
        if PROPERTY in root:
            del root[PROPERTY]
        for mesh in added_uv:
            _remove_part_uv(mesh)
    return report, len(objs), len(materials), len(cache)
