# SPDX-License-Identifier: GPL-3.0-or-later
"""Surface types (painted metal, bare metal, rubber, plastic, wood, stone) on top of the SMC-AD layout.

How it connects to the core material (SMC-AD, docs/REQUIREMENTS_SHADING.md):

  ③ SMC.Decode ──> ④ SMC.Layer.* reroutes ──> [SMC+.Surface] ──> (your layers) ──> ⑤ SMC.BSDF
        └── Paint Mask ────────────────────────────┘        └── Coat Weight / Coat Roughness ──┘

- One group node per material, "SMC+.Surface", placed right after the layer reroutes (S2).
  Everything downstream (hand-made Mix nodes, the BSDF) keeps its place: the node takes over the
  reroute's outgoing links, and removing it gives them back (S2: remove = back to the SM look).
- The node's group is shared per surface type (SMC+_Surface_<id>) and holds the adjustable values,
  so adjusting "painted metal" changes every material of that type. The maths lives in one group,
  SMC+_SurfaceCore, that hand-shaders can open.
- Connection points are found by node name (SMC.Layer.*, SMC.Decode, SMC.BSDF), so a core layout
  change does not break it (S4). The node is not named SMC.*, so "Rebuild SM Materials" keeps it.
- The colour override (②) and the batch tools of the core work unchanged alongside it.

Values per surface (shared JSON with SMConverter and the Unity package):
  base colour  = base * mix(1, tint, tint_strength)
  metallic     = max(input, mix(metallic, paint_metallic, paint mask))
  roughness    = clamp(input * roughness_scale + roughness_offset + (noise - 0.5) * noise)
  specular     = input * specular
  normal       = normalize(Ng + (input - Ng) * normal_strength)
  coat         = coat, coat_roughness
"""

import json
import os

import bmesh
import bpy

NODE_NAME = "SMC+.Surface"
GROUP_PREFIX = "SMC+_Surface_"
CORE_GROUP = "SMC+_SurfaceCore"
CORE_VERSION = 1
PRESET_FILE = os.path.join(os.path.dirname(__file__), "surface_presets.json")
NO_SURFACE = "standard"          # the plain SM look = no surface node
HIDDEN_SURFACES = {"glass"}      # glass keeps the core glass shader

# (socket, core reroute, socket type)
CHANNELS = [
    ("Base Color", "SMC.Layer.BaseColor", "NodeSocketColor"),
    ("Metallic", "SMC.Layer.Metallic", "NodeSocketFloat"),
    ("Roughness", "SMC.Layer.Roughness", "NodeSocketFloat"),
    ("Specular", "SMC.Layer.Specular", "NodeSocketFloat"),
    ("Normal", "SMC.Layer.Normal", "NodeSocketVector"),
]
# (socket, Principled BSDF input)
EXTRA_OUTPUTS = [("Coat Weight", "Coat Weight"), ("Coat Roughness", "Coat Roughness")]

# (key, socket, label, kind, soft min, soft max)
PARAMS = [
    ("metallic", "Texture Metallic", "金属っぽさ（地の部分）", "float", 0.0, 1.0),
    ("paint_metallic", "Paint Metallic", "金属っぽさ（塗装部分）", "float", 0.0, 1.0),
    ("roughness_scale", "Roughness Scale", "ざらつきの倍率", "float", 0.0, 2.0),
    ("roughness_offset", "Roughness Offset", "ざらつきの底上げ", "float", -0.5, 1.0),
    ("specular", "Specular Multiplier", "反射の強さ", "float", 0.0, 2.0),
    ("coat", "Coat", "塗装の厚み（クリアコート）", "float", 0.0, 1.0),
    ("coat_roughness", "Coat Roughness", "クリアコートのざらつき", "float", 0.0, 1.0),
    ("normal_strength", "Normal Strength", "凹凸の強さ", "float", 0.0, 3.0),
    ("tint", "Tint", "色味", "color", 0.0, 1.0),
    ("tint_strength", "Tint Strength", "色味の強さ", "float", 0.0, 1.0),
    ("noise", "Noise", "ざらつきのムラ", "float", 0.0, 1.0),
    ("noise_scale", "Noise Scale", "ムラの細かさ", "float", 0.1, 50.0),
]


# --------------------------------------------------------------------------------------------
# Presets
# --------------------------------------------------------------------------------------------

_DEFAULT_PRESETS = None


def default_presets():
    global _DEFAULT_PRESETS
    if _DEFAULT_PRESETS is None:
        with open(PRESET_FILE, encoding="utf-8") as f:
            _DEFAULT_PRESETS = json.load(f)["surfaces"]
    return _DEFAULT_PRESETS


def surface_ids(include_standard=True):
    """Surface ids: the preset file first, then any extra groups found in this .blend."""
    ids = [sid for sid in default_presets() if sid not in HIDDEN_SURFACES]
    for group in bpy.data.node_groups:
        sid = group.get("smcadp_surface")
        if sid and sid not in ids and sid not in HIDDEN_SURFACES:
            ids.append(sid)
    if not include_standard:
        ids = [sid for sid in ids if sid != NO_SURFACE]
    return ids


def label_of(sid):
    group = bpy.data.node_groups.get(GROUP_PREFIX + sid)
    if group is not None and group.get("smcadp_label"):
        return group["smcadp_label"]
    return default_presets().get(sid, {}).get("label", sid)


# --------------------------------------------------------------------------------------------
# Node helpers
# --------------------------------------------------------------------------------------------

def _socket(sockets, name, socket_type=None):
    for s in sockets:
        if s.name == name and s.enabled and (socket_type is None or s.type == socket_type):
            return s
    for s in sockets:
        if s.name == name and (socket_type is None or s.type == socket_type):
            return s
    raise KeyError(name)


def _connect(links, value, sock):
    if isinstance(value, (int, float)):
        sock.default_value = value
    else:
        links.new(value, sock)


def _math(nodes, links, op, a, b=None, c=None, location=(0, 0), clamp=False):
    node = nodes.new("ShaderNodeMath")
    node.operation = op
    node.use_clamp = clamp
    node.location = location
    for sock, value in zip(node.inputs, (a, b, c)):
        if value is not None:
            _connect(links, value, sock)
    return node.outputs[0]


def _vmath(nodes, links, op, a, b=None, location=(0, 0)):
    node = nodes.new("ShaderNodeVectorMath")
    node.operation = op
    node.location = location
    links.new(a, node.inputs[0])
    if b is not None:
        if isinstance(b, (int, float)):
            # SCALE uses the "Scale" input
            node.inputs["Scale"].default_value = b
        elif op == "SCALE":
            links.new(b, node.inputs["Scale"])
        else:
            links.new(b, node.inputs[1])
    return _socket(node.outputs, "Vector")


def _new_socket(group, name, in_out, socket_type, default=None, lo=None, hi=None):
    sock = group.interface.new_socket(name, in_out=in_out, socket_type=socket_type)
    if default is not None and hasattr(sock, "default_value"):
        sock.default_value = default
    if lo is not None and hasattr(sock, "min_value"):
        sock.min_value = lo
    if hi is not None and hasattr(sock, "max_value"):
        sock.max_value = hi
    return sock


# --------------------------------------------------------------------------------------------
# Node groups
# --------------------------------------------------------------------------------------------

def ensure_core_group():
    """The surface maths, shared by every surface type."""
    group = bpy.data.node_groups.get(CORE_GROUP)
    if group is not None and group.get("smcadp_version") == CORE_VERSION:
        return group
    if group is None:
        group = bpy.data.node_groups.new(CORE_GROUP, "ShaderNodeTree")
    else:
        group.nodes.clear()
        group.interface.clear()
    group["smcadp_version"] = CORE_VERSION

    for name, _reroute, stype in CHANNELS:
        _new_socket(group, name, "INPUT", stype)
    _new_socket(group, "Paint Mask", "INPUT", "NodeSocketFloat", 0.0, 0.0, 1.0)
    for key, name, _label, kind, lo, hi in PARAMS:
        if kind == "color":
            _new_socket(group, name, "INPUT", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
        else:
            _new_socket(group, name, "INPUT", "NodeSocketFloat", default_presets()[NO_SURFACE].get(key, 0.0))
    for name, _reroute, stype in CHANNELS:
        _new_socket(group, name, "OUTPUT", stype)
    for name, _bsdf in EXTRA_OUTPUTS:
        _new_socket(group, name, "OUTPUT", "NodeSocketFloat")

    nodes, links = group.nodes, group.links
    gin = nodes.new("NodeGroupInput"); gin.location = (-900, 0)
    gout = nodes.new("NodeGroupOutput"); gout.location = (500, 0)
    i = gin.outputs

    # Base colour: multiply by the tint
    tint = nodes.new("ShaderNodeMix"); tint.data_type = "RGBA"; tint.blend_type = "MULTIPLY"; tint.location = (100, 400)
    links.new(i["Tint Strength"], _socket(tint.inputs, "Factor", "VALUE"))
    links.new(i["Base Color"], _socket(tint.inputs, "A", "RGBA"))
    links.new(i["Tint"], _socket(tint.inputs, "B", "RGBA"))
    links.new(_socket(tint.outputs, "Result", "RGBA"), gout.inputs["Base Color"])

    # Metallic: texture areas / painted areas
    metal = nodes.new("ShaderNodeMix"); metal.data_type = "FLOAT"; metal.location = (-100, 200)
    metal.label = "地 / 塗装"
    # A = texture-area metallic, B = paint-area metallic; the incoming metallic is kept with max()
    links.new(i["Paint Mask"], _socket(metal.inputs, "Factor", "VALUE"))
    links.new(i["Texture Metallic"], _socket(metal.inputs, "A", "VALUE"))
    links.new(i["Paint Metallic"], _socket(metal.inputs, "B", "VALUE"))
    links.new(_math(nodes, links, "MAXIMUM", i["Metallic"], _socket(metal.outputs, "Result", "VALUE"), location=(100, 200)),
              gout.inputs["Metallic"])

    # Roughness: scale + offset + noise
    coord = nodes.new("ShaderNodeTexCoord"); coord.location = (-700, -150)
    noise = nodes.new("ShaderNodeTexNoise"); noise.location = (-500, -150)
    links.new(coord.outputs["Object"], noise.inputs["Vector"])
    links.new(i["Noise Scale"], noise.inputs["Scale"])
    centred = _math(nodes, links, "SUBTRACT", noise.outputs["Fac"], 0.5, location=(-300, -150))
    wobble = _math(nodes, links, "MULTIPLY", centred, i["Noise"], location=(-150, -150))
    scaled = _math(nodes, links, "MULTIPLY_ADD", i["Roughness"], i["Roughness Scale"], i["Roughness Offset"], location=(-150, 0))
    links.new(_math(nodes, links, "ADD", scaled, wobble, location=(100, 0), clamp=True), gout.inputs["Roughness"])

    # Specular
    links.new(_math(nodes, links, "MULTIPLY", i["Specular"], i["Specular Multiplier"], location=(100, -250)), gout.inputs["Specular"])

    # Normal strength around the geometry normal
    geo = nodes.new("ShaderNodeNewGeometry"); geo.location = (-500, -450)
    delta = _vmath(nodes, links, "SUBTRACT", i["Normal"], geo.outputs["Normal"], (-300, -400))
    delta = _vmath(nodes, links, "SCALE", delta, i["Normal Strength"], (-100, -400))
    bent = _vmath(nodes, links, "ADD", geo.outputs["Normal"], delta, (100, -400))
    links.new(_vmath(nodes, links, "NORMALIZE", bent, None, (300, -400)), gout.inputs["Normal"])

    links.new(i["Coat"], gout.inputs["Coat Weight"])
    links.new(i["Coat Roughness"], gout.inputs["Coat Roughness"])
    return group


def _param_node_kind(kind):
    return "ShaderNodeRGB" if kind == "color" else "ShaderNodeValue"


def set_param_node(node, kind, value):
    if value is None:
        return
    if kind == "color":
        rgb = list(value)[:3]
        node.outputs[0].default_value = (rgb[0], rgb[1], rgb[2], 1.0)
    else:
        node.outputs[0].default_value = float(value)


def group_values(group):
    values = {"label": group.get("smcadp_label", group.name)}
    for key, _socket_name, _label, kind, _lo, _hi in PARAMS:
        node = group.nodes.get("p_" + key)
        if node is None:
            continue
        v = node.outputs[0].default_value
        values[key] = [round(float(c), 4) for c in list(v)[:3]] if kind == "color" else round(float(v), 4)
    return values


def ensure_surface_group(sid, values=None):
    """SMC+_Surface_<sid>: the values of one surface type around the shared maths.
    An existing group keeps its (possibly user-edited) values, also when its layout is upgraded."""
    name = GROUP_PREFIX + sid
    group = bpy.data.node_groups.get(name)
    if group is not None and group.get("smcadp_version") == CORE_VERSION:
        return group

    if group is not None:
        values = dict(group_values(group), **(values or {}))
        group.nodes.clear()
        group.interface.clear()
    else:
        group = bpy.data.node_groups.new(name, "ShaderNodeTree")
    values = dict(default_presets().get(NO_SURFACE, {}), **(values or default_presets().get(sid, {})))
    group["smcadp_version"] = CORE_VERSION
    group["smcadp_surface"] = sid
    group["smcadp_label"] = values.get("label", sid)

    for sock_name, _reroute, stype in CHANNELS:
        _new_socket(group, sock_name, "INPUT", stype)
    _new_socket(group, "Paint Mask", "INPUT", "NodeSocketFloat", 0.0, 0.0, 1.0)
    for sock_name, _reroute, stype in CHANNELS:
        _new_socket(group, sock_name, "OUTPUT", stype)
    for sock_name, _bsdf in EXTRA_OUTPUTS:
        _new_socket(group, sock_name, "OUTPUT", "NodeSocketFloat")

    nodes, links = group.nodes, group.links
    gin = nodes.new("NodeGroupInput"); gin.location = (-500, 300)
    gout = nodes.new("NodeGroupOutput"); gout.location = (300, 0)
    core = nodes.new("ShaderNodeGroup"); core.location = (0, 0); core.width = 200
    core.node_tree = ensure_core_group()
    core.name = core.label = "Surface Core"
    for sock_name, _reroute, _stype in CHANNELS:
        links.new(gin.outputs[sock_name], core.inputs[sock_name])
        links.new(core.outputs[sock_name], gout.inputs[sock_name])
    links.new(gin.outputs["Paint Mask"], core.inputs["Paint Mask"])
    for sock_name, _bsdf in EXTRA_OUTPUTS:
        links.new(core.outputs[sock_name], gout.inputs[sock_name])

    # The adjustable values: one Value / RGB node each, labelled in plain words
    for n, (key, sock_name, label, kind, _lo, _hi) in enumerate(PARAMS):
        node = nodes.new(_param_node_kind(kind))
        node.name = "p_" + key
        node.label = label
        node.location = (-500, -n * 70)
        if kind == "color":
            node.location = (-700, -n * 70)
        set_param_node(node, kind, values.get(key))
        links.new(node.outputs[0], core.inputs[sock_name])
    return group


def apply_presets(presets, overwrite):
    """Creates (or with overwrite=True updates) surface groups from a {id: values} table."""
    for sid, values in presets.items():
        group = bpy.data.node_groups.get(GROUP_PREFIX + sid)
        if group is None:
            ensure_surface_group(sid, values)
        elif overwrite:
            ensure_surface_group(sid)
            group["smcadp_label"] = values.get("label", sid)
            for key, _sock, _label, kind, _lo, _hi in PARAMS:
                node = group.nodes.get("p_" + key)
                if node is not None:
                    set_param_node(node, kind, values.get(key))


# --------------------------------------------------------------------------------------------
# One material
# --------------------------------------------------------------------------------------------

def _anchors(tree):
    """The core nodes the surface connects to, or None when the material has no Principled layout."""
    if tree is None:
        return None
    reroutes = {name: tree.nodes.get(reroute) for name, reroute, _stype in CHANNELS}
    bsdf = tree.nodes.get("SMC.BSDF")
    decode = tree.nodes.get("SMC.Decode")
    if None in reroutes.values() or bsdf is None or decode is None:
        return None
    if bsdf.bl_idname != "ShaderNodeBsdfPrincipled":
        return None  # glass / light cone
    return reroutes, bsdf, decode


def supports(material):
    return material is not None and "sm_shader" in material and _anchors(material.node_tree) is not None


def surface_of(material):
    """The assigned surface id ("standard" = none)."""
    if material is None or material.node_tree is None:
        return NO_SURFACE
    node = material.node_tree.nodes.get(NODE_NAME)
    if node is None or node.node_tree is None:
        return NO_SURFACE
    return node.node_tree.get("smcadp_surface", NO_SURFACE)


def place(tree, node):
    """Right of the layer reroutes, inside frame ④."""
    anchors = _anchors(tree)
    if anchors is None:
        return
    reroutes = anchors[0]
    top = reroutes["Base Color"]
    frame = tree.nodes.get("SMC.Frame.Layers")
    x, y = top.location_absolute
    if frame is not None and node.parent != frame:
        node.parent = frame
    node.width = 190
    node.location_absolute = (x + 50, y + 40)


def _wire(tree, node, anchors):
    reroutes, bsdf, decode = anchors
    links = tree.links
    for name, _reroute, _stype in CHANNELS:
        r = reroutes[name]
        downstream = [l.to_socket for l in tree.links if l.from_node == r and l.to_node != node]
        links.new(r.outputs[0], node.inputs[name])
        for sock in downstream:
            links.new(node.outputs[name], sock)  # replaces the reroute's link into that socket
    links.new(decode.outputs["Paint Mask"], node.inputs["Paint Mask"])
    for name, bsdf_in in EXTRA_OUTPUTS:
        links.new(node.outputs[name], _socket(bsdf.inputs, bsdf_in))


def set_surface(material, sid):
    """Assigns a surface type to a material (all its faces). "standard" removes it. Returns True on success."""
    if sid in (None, "", NO_SURFACE):
        remove_surface(material)
        return True
    if not supports(material):
        return False
    tree = material.node_tree
    anchors = _anchors(tree)
    node = tree.nodes.get(NODE_NAME)
    group = ensure_surface_group(sid)
    if node is None:
        node = tree.nodes.new("ShaderNodeGroup")
        node.name = NODE_NAME
        node.node_tree = group
        _wire(tree, node, anchors)
    else:
        previous = node.node_tree
        node.node_tree = group
        if previous is not group:
            _drop_if_unused(previous)
    node.label = "材質: " + label_of(sid)
    node.use_custom_color = True
    node.color = (0.22, 0.45, 0.30)
    place(tree, node)
    material["smcad_surface"] = sid
    return True


def remove_surface(material):
    """Takes the surface node out and gives its links back to the reroutes (= the plain SM look)."""
    if material is None or material.node_tree is None:
        return False
    tree = material.node_tree
    node = tree.nodes.get(NODE_NAME)
    if "smcad_surface" in material:
        del material["smcad_surface"]
    if node is None:
        return False
    anchors = _anchors(tree)
    if anchors is not None:
        reroutes = anchors[0]
        for name, _reroute, _stype in CHANNELS:
            out = node.outputs.get(name)
            if out is None:
                continue
            downstream = [l.to_socket for l in tree.links if l.from_socket == out]
            for sock in downstream:
                tree.links.new(reroutes[name].outputs[0], sock)
    group = node.node_tree
    tree.nodes.remove(node)
    _drop_if_unused(group)
    return True


def refresh(material):
    """After a core rebuild: put the node back in frame ④ and rewire it if a link was lost."""
    tree = material.node_tree
    node = tree.nodes.get(NODE_NAME) if tree is not None else None
    if node is None:
        sid = material.get("smcad_surface")
        if sid:
            set_surface(material, sid)
        return
    anchors = _anchors(tree)
    if anchors is None:
        return
    if not node.inputs["Base Color"].is_linked or not any(o.is_linked for o in node.outputs):
        _wire(tree, node, anchors)
    place(tree, node)


# --------------------------------------------------------------------------------------------
# Materials per surface (assigning to some faces / parts only)
# --------------------------------------------------------------------------------------------

def _base_of(material):
    return material.get("smcadp_base", material.name)


def variant_for(material, sid):
    """The material that looks like `material` but has surface `sid` (made by copying it if needed)."""
    if surface_of(material) == (sid or NO_SURFACE):
        return material
    base = _base_of(material)
    for m in bpy.data.materials:
        if _base_of(m) == base and m.get("sm_shader") == material.get("sm_shader") and surface_of(m) == sid                 and not is_local(group_of(m)):
            return m
    new = material.copy()
    new["smcadp_base"] = base
    new.name = "%s+%s" % (base, sid or NO_SURFACE)
    if not set_surface(new, sid):
        bpy.data.materials.remove(new)
        return None
    return new


def assign_faces(obj, sid):
    """Edit mode: moves the selected faces of `obj` to the `sid` variants of their materials."""
    mesh = obj.data
    bm = bmesh.from_edit_mesh(mesh)
    slot_of = {}
    for i, slot in enumerate(obj.material_slots):
        if slot.material is not None and slot.material not in slot_of:
            slot_of[slot.material] = i
    cache = {}
    changed = 0
    for face in bm.faces:
        if not face.select or face.material_index >= len(obj.material_slots):
            continue
        mat = obj.material_slots[face.material_index].material
        if mat is None or "sm_shader" not in mat:
            continue
        if mat not in cache:
            cache[mat] = variant_for(mat, sid) if supports(mat) else None
        target = cache[mat]
        if target is None or target == mat:
            continue
        if target not in slot_of:
            mesh.materials.append(target)
            slot_of[target] = len(obj.material_slots) - 1
        face.material_index = slot_of[target]
        changed += 1
    bmesh.update_edit_mesh(mesh)
    return changed


def select_parts(obj):
    """Edit mode: grows the face selection to whole parts (same _SM_SHAPE value)."""
    bm = bmesh.from_edit_mesh(obj.data)
    layer = bm.verts.layers.float.get("_SM_SHAPE")
    if layer is None:
        return 0
    shapes = {round(f.verts[0][layer]) for f in bm.faces if f.select}
    count = 0
    for f in bm.faces:
        if round(f.verts[0][layer]) in shapes:
            f.select_set(True)
            count += 1
    bm.select_flush_mode()
    bmesh.update_edit_mesh(obj.data)
    return count


def users_of(sid):
    """Materials that currently use a surface type (shared or with their own values)."""
    return [m for m in bpy.data.materials if m.node_tree is not None and surface_of(m) == sid]


# --------------------------------------------------------------------------------------------
# Who an adjustment affects: the shared surface type, or one material only
# --------------------------------------------------------------------------------------------

def group_of(material):
    """The surface group a material uses (shared type group or its own copy), or None."""
    if material is None or material.node_tree is None:
        return None
    node = material.node_tree.nodes.get(NODE_NAME)
    return node.node_tree if node is not None else None


def is_local(group):
    return group is not None and bool(group.get("smcadp_local"))


def materials_using(group):
    return [m for m in bpy.data.materials if group is not None and group_of(m) == group]


def _drop_if_unused(group):
    """Removes a material's own surface copy once nothing uses it."""
    if group is not None and is_local(group) and not materials_using(group):
        bpy.data.node_groups.remove(group)


def make_local(material):
    """Gives the material its own copy of the surface values (adjusting it no longer changes other materials)."""
    group = group_of(material)
    if group is None or is_local(group):
        return group
    sid = group.get("smcadp_surface", "")
    local = group.copy()
    local.name = "%s%s@%s" % (GROUP_PREFIX, sid, material.name)
    local["smcadp_local"] = material.name
    material.node_tree.nodes[NODE_NAME].node_tree = local
    return local


def make_shared(material):
    """Back to the shared values of the surface type (the material's own copy is discarded)."""
    group = group_of(material)
    if group is None or not is_local(group):
        return group
    shared = ensure_surface_group(group.get("smcadp_surface", NO_SURFACE))
    material.node_tree.nodes[NODE_NAME].node_tree = shared
    _drop_if_unused(group)
    return shared


# --------------------------------------------------------------------------------------------
# Moving faces to other materials (object mode) and back
# --------------------------------------------------------------------------------------------

def move_polys(obj, mask, target_for):
    """Moves the polygons in `mask` (numpy bool per polygon) to target_for(material).
    target_for returns the new material, the same material, or None (= leave). Returns polygons moved."""
    import numpy as np
    mesh = obj.data
    n = len(mesh.polygons)
    if n == 0 or not mask.any():
        return 0
    original = np.empty(n, dtype=np.int32)
    mesh.polygons.foreach_get("material_index", original)
    result = original.copy()
    slot_of = {}
    for i, slot in enumerate(obj.material_slots):
        if slot.material is not None and slot.material not in slot_of:
            slot_of[slot.material] = i
    moved = 0
    for si in np.unique(original[mask]).tolist():
        if si >= len(obj.material_slots):
            continue
        mat = obj.material_slots[si].material
        if mat is None or "sm_shader" not in mat:
            continue
        target = target_for(mat)
        if target is None or target == mat:
            continue
        if target not in slot_of:
            mesh.materials.append(target)
            slot_of[target] = len(obj.material_slots) - 1
        sel = mask & (original == si)
        result[sel] = slot_of[target]
        moved += int(sel.sum())
    if moved:
        mesh.polygons.foreach_set("material_index", result)
        mesh.update()
    return moved


def part_copy_factory():
    """target_for() that gives every source material one new copy with its own values ("for these parts")."""
    made = {}

    def target_for(mat):
        if mat in made:
            return made[mat]
        new = mat.copy()
        new["smcadp_base"] = _base_of(mat)
        new.name = "%s+パーツ" % _base_of(mat)
        if group_of(new) is not None:
            make_local(new)
        made[mat] = new
        return new
    return target_for, made


def merge_back(objects):
    """Parts that were split off into their own materials go back to the original material.
    Returns the number of materials removed."""
    variants = set()
    for obj in objects:
        for slot in obj.material_slots:
            mat = slot.material
            if mat is not None and mat.get("smcadp_base"):
                base = bpy.data.materials.get(mat["smcadp_base"])
                if base is not None and base != mat:
                    slot.material = base
                    variants.add(mat)
    removed = 0
    for mat in variants:
        if mat.users == 0:
            group = group_of(mat)
            bpy.data.materials.remove(mat)
            _drop_if_unused(group)
            removed += 1
    return removed


def merge_material_back(material):
    """One split-off material back into its original, on every object. Returns the original or None."""
    base_name = material.get("smcadp_base")
    base = bpy.data.materials.get(base_name) if base_name else None
    if base is None or base == material:
        return None
    for obj in bpy.data.objects:
        for slot in getattr(obj, "material_slots", []):
            if slot.material == material:
                slot.material = base
    if material.users == 0:
        group = group_of(material)
        bpy.data.materials.remove(material)
        _drop_if_unused(group)
    return base


def converter_surface(material):
    """The surface SMConverter guessed for the material (glb extras sm_surface), if we can show it."""
    sid = material.get("sm_surface")
    if not sid or sid in HIDDEN_SURFACES or sid == NO_SURFACE:
        return None
    return sid if sid in default_presets() or bpy.data.node_groups.get(GROUP_PREFIX + sid) else None


# --------------------------------------------------------------------------------------------
# Back to the import state (called by the core's "取り込み直後に戻す")
# --------------------------------------------------------------------------------------------

IMPORT_SURFACE = "smcadp_import_surface"


def original_material(material):
    """The material a part copy was made from (core hook api.original_material)."""
    base = material.get("smcadp_base")
    if not base:
        return None
    found = bpy.data.materials.get(base)
    return found if found is not None and found != material else None


def import_surface(material, auto_assign=True):
    """The surface the material had right after import."""
    sid = material.get(IMPORT_SURFACE)
    if sid is not None:
        return sid
    if material.get("smcad_gltf_material") is not None:
        # imported by SMC-AD 0.5+ (which records the import) while this extension was off: no surface
        return NO_SURFACE
    # imported before anything was recorded: what the import would have assigned
    return (converter_surface(material) if auto_assign else None) or NO_SURFACE


_used_before_reset = set()


def before_reset(materials):
    """Takes every surface node off (the core then rebuilds the materials).
    Remembers the shared surface types they used, so the reset can put those back to the preset too."""
    _used_before_reset.clear()
    for mat in materials:
        group = group_of(mat)
        if group is not None and not is_local(group):
            _used_before_reset.add(group.name)
        remove_surface(mat)


def after_reset(materials, auto_assign=True, scope="BLUEPRINT"):
    """Import-time surfaces back with the preset values.

    Blueprint / scene / selection: the shared surface types they use go back to the preset values
    (that is their state at import - also for other blueprints that use the same type).
    One material: the shared values are left alone; if they were adjusted, the material gets its own
    copy with the preset values, so it looks exactly as imported without changing anything else."""
    from . import flavors
    reset_set = set(materials)
    touched = set()
    for mat in materials:
        sid = import_surface(mat, auto_assign)
        if sid == NO_SURFACE or not set_surface(mat, sid):
            continue
        shared = ensure_surface_group(sid)
        adjusted = flavors.is_modified(shared) or bool(shared.get("smcadp_flavor"))
        others = [m for m in materials_using(shared) if m not in reset_set]
        if scope != "MATERIAL" or not others:
            if shared not in touched and adjusted:
                flavors.apply(shared, None)
            touched.add(shared)
        elif adjusted:
            flavors.apply(make_local(mat), None)
    if scope == "SCENE":
        for group in [g for g in bpy.data.node_groups if g.get("smcadp_surface") and not is_local(g)]:
            flavors.apply(group, None)
    elif scope != "MATERIAL":
        # shared types the scope used before the reset and nothing outside it uses now: back to the preset
        for name in _used_before_reset:
            group = bpy.data.node_groups.get(name)
            if group is not None and group not in touched and not [m for m in materials_using(group) if m not in reset_set]:
                flavors.apply(group, None)
    _used_before_reset.clear()
    for group in [g for g in bpy.data.node_groups if is_local(g) and not materials_using(g)]:
        bpy.data.node_groups.remove(group)


# --------------------------------------------------------------------------------------------
# Unity export (core hook api.export_material)
# --------------------------------------------------------------------------------------------

def export_material(material, smcad, report):
    """Adds {"surface": {id, label, local, values}} to the material's export data."""
    tree = material.node_tree
    node = tree.nodes.get(NODE_NAME) if tree is not None else None
    if node is None or node.node_tree is None:
        return
    group = node.node_tree
    sid = group.get("smcadp_surface", NO_SURFACE)
    values = group_values(group)
    values.pop("label", None)
    smcad["surface"] = {"id": sid, "label": label_of(sid), "local": is_local(group),
                        "flavor": group.get("smcadp_flavor"), "values": values}
    for name, reroute, _stype in CHANNELS:
        sock = node.inputs.get(name)
        if sock is not None and sock.is_linked and sock.links[0].from_node.name != reroute:
            report.add("warning", material.name, "材質ノードの手前（%s）にある手作業のノードは Unity には反映されません" % name)
    if group.name != GROUP_PREFIX + sid and not is_local(group):
        report.add("warning", material.name, "材質グループ「%s」は SMC-AD Surfaces のものではないため、値だけを書き出しました" % group.name)
    if float(values.get("noise", 0.0)) > 0.0:
        owner = ("このマテリアル専用の" if is_local(group) else "") + "「%s」" % label_of(sid)
        report.add("info", owner, "「ざらつきのムラ」の模様は Unity では別のノイズになります（強さ・細かさは同じ）",
                   key=("noise", group.name))
