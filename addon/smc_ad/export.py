# SPDX-License-Identifier: GPL-3.0-or-later
"""Export for Unity: the source .glb plus what was changed in Blender (requirements U1-U3).

The geometry, textures, joints and paint of the SMConverter .glb are copied unchanged. Added:
  material extras "smcad":
    override   the colour override (the core)
    surface    surface type values (from extensions, via api.export_material)
    layers     hand-made nodes on the layer wires that can be expressed as parameters (U2 stage 1):
               a constant, layer x constant, layer + constant, or Mix(mask or constant, A, B) with
               A / B = the layer or a constant, mask = an SMC_Masks output / the paint mask
  materials for part copies (faces moved to a copy of their material in Blender): the primitives are
  split per part, so Unity gets the same material assignment
  root extras "smcad_export": source file, versions, and the list of what could not be carried over (U3)

Everything that cannot be expressed is listed in the report (never dropped silently).
"""

import copy
import json
import os
import struct

import bpy
import numpy as np

from . import api, reset
from .glb_reader import CHUNK_BIN, CHUNK_JSON, GLB_MAGIC, GlbFile

EXPORT_VERSION = 1
SHAPE_ATTRIBUTE = "_SM_SHAPE"

# channel id -> (Principled BSDF input, kind)
CHANNELS = {
    "base_color": ("Base Color", "color"),
    "metallic": ("Metallic", "float"),
    "roughness": ("Roughness", "float"),
    "specular": ("Specular IOR Level", "float"),
    "emission": ("Emission Strength", "float"),
}
# BSDF inputs the export does not carry when a hand-made node drives them
UNSUPPORTED_LINKED = ("Normal", "Alpha", "Emission Color", "Coat Weight", "Coat Roughness", "Coat Normal",
                      "Subsurface Weight", "Transmission Weight", "Sheen Weight", "Anisotropic", "IOR",
                      "Specular Tint", "Coat Tint", "Sheen Tint", "Thin Film Thickness")
MASKS = ("Paint Mask", "Texture Mask", "Part Random", "Body Random", "Up Facing")


class Report:
    """What could not be carried over (U3). level: "warning" (lost) or "info" (approximated)."""

    def __init__(self):
        self.items = []
        self._keys = set()

    def add(self, level, material, message, key=None):
        """key: report the same thing only once (e.g. per surface type instead of per material)."""
        if key is not None:
            if key in self._keys:
                return
            self._keys.add(key)
        self.items.append({"level": level, "material": material or "", "message": message})

    def warnings(self):
        return [i for i in self.items if i["level"] == "warning"]


# --------------------------------------------------------------------------------------------
# glTF buffer helpers
# --------------------------------------------------------------------------------------------

_COMPONENTS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
_DTYPES = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}


def read_accessor(js, binary, index):
    acc = js["accessors"][index]
    view = js["bufferViews"][acc["bufferView"]]
    dtype = np.dtype(_DTYPES[acc["componentType"]])
    comps = _COMPONENTS[acc["type"]]
    start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
    stride = view.get("byteStride", 0) or dtype.itemsize * comps
    count = acc["count"]
    if stride == dtype.itemsize * comps:
        arr = np.frombuffer(binary, dtype=dtype, count=count * comps, offset=start)
    else:
        rows = np.frombuffer(binary, dtype=np.uint8, count=stride * (count - 1) + dtype.itemsize * comps, offset=start)
        arr = np.lib.stride_tricks.as_strided(rows, shape=(count, dtype.itemsize * comps), strides=(stride, 1))
        arr = np.frombuffer(arr.copy().tobytes(), dtype=dtype)
    return arr.reshape(count, comps) if comps > 1 else arr


def _append(binary, data, js, target=None):
    """Appends bytes (4-byte aligned) as a new bufferView. Returns its index."""
    while len(binary) % 4:
        binary.append(0)
    offset = len(binary)
    binary.extend(data)
    view = {"buffer": 0, "byteOffset": offset, "byteLength": len(data)}
    if target is not None:
        view["target"] = target
    js["bufferViews"].append(view)
    return len(js["bufferViews"]) - 1


def _add_indices(binary, js, indices):
    indices = np.asarray(indices, dtype=np.uint32)
    view = _append(binary, indices.tobytes(), js, target=34963)
    js["accessors"].append({"bufferView": view, "componentType": 5125, "count": int(indices.size), "type": "SCALAR",
                            "min": [int(indices.min())] if indices.size else [0],
                            "max": [int(indices.max())] if indices.size else [0]})
    return len(js["accessors"]) - 1


def write_glb(path, js, binary):
    while len(binary) % 4:
        binary.append(0)
    js["buffers"] = [{"byteLength": len(binary)}]
    text = json.dumps(js, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    text += b" " * ((4 - len(text) % 4) % 4)
    total = 12 + 8 + len(text) + 8 + len(binary)
    with open(path, "wb") as f:
        f.write(struct.pack("<III", GLB_MAGIC, 2, total))
        f.write(struct.pack("<II", len(text), CHUNK_JSON))
        f.write(text)
        f.write(struct.pack("<II", len(binary), CHUNK_BIN))
        f.write(binary)


def add_image(js, binary, name, data):
    """Embeds an image (PNG / JPEG bytes) and returns the glTF texture index."""
    mime = "image/jpeg" if data[:2] == b"\xff\xd8" else "image/png"
    view = _append(binary, data, js)
    js.setdefault("images", []).append({"name": name, "mimeType": mime, "bufferView": view})
    if not js.get("samplers"):
        js["samplers"] = [{"magFilter": 9729, "minFilter": 9987, "wrapS": 10497, "wrapT": 10497}]
    js.setdefault("textures", []).append({"source": len(js["images"]) - 1, "sampler": 0})
    return len(js["textures"]) - 1


def image_bytes(image):
    """PNG / JPEG bytes of a Blender image (packed or on disk), or None."""
    if image is None:
        return None
    if image.packed_file is not None:
        return bytes(image.packed_file.data)
    path = bpy.path.abspath(image.filepath)
    if path and os.path.isfile(path):
        with open(path, "rb") as f:
            data = f.read()
        if data[:4] == b"\x89PNG" or data[:2] == b"\xff\xd8":
            return data
    return None


# --------------------------------------------------------------------------------------------
# Materials
# --------------------------------------------------------------------------------------------

def _plain(value):
    try:
        return [round(float(v), 6) for v in value]
    except TypeError:
        return round(float(value), 6)


def override_extras(material, js, binary, report, image_cache):
    ov = material.smcad_override
    if not ov.enabled:
        return None
    data = {
        "target": ov.target, "source": ov.source, "color": _plain(ov.color), "projection": ov.projection,
        "scale": _plain(ov.scale), "strength": _plain(ov.strength), "match_enabled": bool(ov.match_enabled),
        "match_color": _plain(ov.match_color), "match_tolerance": _plain(ov.match_tolerance),
    }
    if ov.source == "IMAGE":
        if ov.image is None:
            data["source"] = "COLOR"
        elif ov.image in image_cache:
            data["image_texture"] = image_cache[ov.image]
        else:
            raw = image_bytes(ov.image)
            if raw is None:
                report.add("warning", material.name, "色の差し替えの画像「%s」を読めないため、色で書き出しました" % ov.image.name)
                data["source"] = "COLOR"
            else:
                image_cache[ov.image] = add_image(js, binary, "override_" + ov.image.name, raw)
                data["image_texture"] = image_cache[ov.image]
    return data


def _is_layer_source(node):
    return node.name.startswith("SMC.Layer.") or node.name.startswith("SMC+.")


def _mask_of(socket):
    node = socket.node
    if node.name == "SMC.Masks" and socket.name in MASKS:
        return socket.name
    if node.name == "SMC.Decode" and socket.name == "Paint Mask":
        return "Paint Mask"
    return None


def _const(socket_or_node_output):
    v = socket_or_node_output.default_value
    try:
        return [round(float(c), 6) for c in list(v)[:4]]
    except TypeError:
        return round(float(v), 6)


def _operand(sock):
    """An input of a hand-made node: ("layer",) | ("const", v) | ("mask", name) | None."""
    if not sock.is_linked:
        return ("const", _const(sock))
    link = sock.links[0]
    src = link.from_node
    if _is_layer_source(src):
        return ("layer",)
    if src.bl_idname in ("ShaderNodeValue", "ShaderNodeRGB"):
        return ("const", _const(src.outputs[0]))
    mask = _mask_of(link.from_socket)
    if mask:
        return ("mask", mask)
    return None


def _named_inputs(node, kind):
    """Factor / A / B of a Mix node for the data type in use."""
    stype = "RGBA" if kind == "color" else "VALUE"
    def pick(name, t):
        for s in node.inputs:
            if s.name == name and s.type == t and s.enabled:
                return s
        return None
    return pick("Factor", "VALUE"), pick("A", stype), pick("B", stype)


def classify_channel(bsdf_input, kind):
    """None (nothing hand-made) | dict (expressible) | str (why it cannot be carried over)."""
    if not bsdf_input.is_linked:
        return {"op": "constant", "a": _const(bsdf_input)}
    link = bsdf_input.links[0]
    node = link.from_node
    # extension nodes that only modify the wire (e.g. SMC-DETAILS): look through them
    seen = 0
    while node.get("smcad_passthrough") and seen < 8:
        inp = next((s for s in node.inputs if s.name == link.from_socket.name), None)
        if inp is None:
            break
        if not inp.is_linked:
            return {"op": "constant", "a": _const(inp)}
        link = inp.links[0]
        node = link.from_node
        seen += 1
    if _is_layer_source(node):
        return None
    if node.bl_idname in ("ShaderNodeValue", "ShaderNodeRGB"):
        return {"op": "constant", "a": _const(node.outputs[0])}
    if node.bl_idname == "ShaderNodeMath" and kind == "color":
        return "「%s」(Math) は色を1つの値に変えてしまうため、そのままは変換できません" % node.name
    if node.bl_idname == "ShaderNodeMath" and node.operation in ("MULTIPLY", "ADD"):
        a, b = _operand(node.inputs[0]), _operand(node.inputs[1])
        if a and b:
            if a[0] == "const" and b[0] == "layer":
                a, b = b, a
            if a[0] == "layer" and b[0] == "const":
                return {"op": node.operation.lower(), "a": b[1], "clamp": bool(node.use_clamp)}
        return "「%s」(%s) は、片方がレイヤー、もう片方が定数の形ではありません" % (node.name, node.operation)
    if node.bl_idname == "ShaderNodeMix" and node.blend_type == "MIX" and \
            node.data_type == ("RGBA" if kind == "color" else "FLOAT"):
        f, a, b = _named_inputs(node, kind)
        fo, ao, bo = _operand(f), _operand(a), _operand(b)
        if fo and ao and bo and fo[0] in ("const", "mask") and ao[0] in ("layer", "const") and bo[0] in ("layer", "const"):
            return {"op": "mix",
                    "mask": fo[1] if fo[0] == "mask" else None,
                    "factor": fo[1] if fo[0] == "const" else 1.0,
                    "a": None if ao[0] == "layer" else ao[1],
                    "b": None if bo[0] == "layer" else bo[1]}
        return "「%s」(Mix) の入力が、マスク／定数／レイヤーの組み合わせではありません" % node.name
    return "「%s」(%s) は自動では変換できません" % (node.name, node.bl_idname.replace("ShaderNode", ""))


def layer_extras(material, report):
    tree = material.node_tree
    bsdf = tree.nodes.get("SMC.BSDF") if tree else None
    if bsdf is None or bsdf.bl_idname != "ShaderNodeBsdfPrincipled":
        return None
    layers = {}
    for channel, (input_name, kind) in CHANNELS.items():
        result = classify_channel(bsdf.inputs[input_name], kind)
        if isinstance(result, dict):
            layers[channel] = result
            if result.get("mask") == "Body Random":
                report.add("info", material.name, "%s: Body Random は Unity では別の乱数になります（ボディごとに違う値になる点は同じ）" % input_name)
        elif isinstance(result, str):
            report.add("warning", material.name, "%s: %s。Unityには反映されません（焼き込みは今後対応）" % (input_name, result))
    for input_name in UNSUPPORTED_LINKED:
        sock = bsdf.inputs.get(input_name)
        if sock is None or not sock.is_linked:
            continue
        src = sock.links[0].from_node
        if src.name.startswith(("SMC.", "SMC+.")):
            continue  # the core / an extension wires it (handled by them)
        report.add("warning", material.name, "%s: 手作業のノード「%s」は Unity には反映されません" % (input_name, src.name))
    # hand-made nodes between the decode and the layer reroutes are not read
    for reroute in [n for n in tree.nodes if n.name.startswith("SMC.Layer.")]:
        if reroute.inputs[0].is_linked and reroute.inputs[0].links[0].from_node.name != "SMC.Decode":
            report.add("warning", material.name, "%s の手前にある手作業のノードは Unity には反映されません" % reroute.name)
    return layers or None


# --------------------------------------------------------------------------------------------
# Material identity: Blender material <-> glTF material
# --------------------------------------------------------------------------------------------

def gltf_material_index(material, js):
    orig = reset.original_of(material)
    idx = orig.get("smcad_gltf_material")
    if idx is not None:
        return int(idx)
    names = [m.get("name", "") for m in js["materials"]]
    name = orig.name
    base = name.rsplit(".", 1)[0] if len(name) > 4 and name[-4] == "." and name[-3:].isdigit() else name
    candidates = [i for i, n in enumerate(names) if n in (name, base)]
    # several glTF materials can share a name (e.g. same Dif / Asg, with and without a normal map):
    # the full texture set and the shader tell them apart
    key = tuple(str(orig.get(k, "")) for k in ("sm_dif", "sm_asg", "sm_nor", "sm_shader"))
    for i in candidates:
        extras = js["materials"][i].get("extras", {})
        if tuple(str(extras.get(k, "")) for k in ("sm_dif", "sm_asg", "sm_nor", "sm_shader")) == key:
            return i
    return candidates[0] if candidates else None


def record_gltf_materials(materials, js):
    """At import: remember which glTF material each Blender material came from."""
    for mat in materials:
        idx = gltf_material_index(mat, js)
        if idx is not None:
            mat["smcad_gltf_material"] = idx


# --------------------------------------------------------------------------------------------
# Geometry: part copies -> split primitives
# --------------------------------------------------------------------------------------------

def _poly_parts(mesh):
    attr = mesh.attributes.get(SHAPE_ATTRIBUTE)
    if attr is None or len(mesh.polygons) == 0:
        return None
    values = np.empty(len(attr.data), dtype=np.float32)
    attr.data.foreach_get("value", values)
    loop_start = np.empty(len(mesh.polygons), dtype=np.int64)
    mesh.polygons.foreach_get("loop_start", loop_start)
    if attr.domain == "POINT":
        loop_vert = np.empty(len(mesh.loops), dtype=np.int64)
        mesh.loops.foreach_get("vertex_index", loop_vert)
        values = values[loop_vert[loop_start]]
    elif attr.domain == "CORNER":
        values = values[loop_start]
    return np.rint(values).astype(np.int64)


def glb_part_materials(js, binary, mesh_index):
    """{part id: set of glTF materials} of one glTF mesh."""
    result = {}
    for prim in js["meshes"][mesh_index]["primitives"]:
        if "indices" not in prim or SHAPE_ATTRIBUTE not in prim.get("attributes", {}):
            continue
        idx = read_accessor(js, binary, prim["indices"]).astype(np.int64).reshape(-1, 3)
        shape = np.rint(read_accessor(js, binary, prim["attributes"][SHAPE_ATTRIBUTE])).astype(np.int64)
        for part in np.unique(shape[idx[:, 0]]).tolist():
            result.setdefault(part, set()).add(prim.get("material"))
    return result


def part_assignment(obj, js, export_index_of, report, glb_parts=None):
    """{(glTF material, part id): export material index} where Blender differs from the import."""
    mesh = obj.data
    parts = _poly_parts(mesh)
    if parts is None:
        return {}
    mi = np.empty(len(mesh.polygons), dtype=np.int32)
    mesh.polygons.foreach_get("material_index", mi)
    slot_export, slot_orig = [], []
    for slot in obj.material_slots:
        mat = slot.material
        slot_export.append(export_index_of.get(mat) if mat is not None else None)
        slot_orig.append(gltf_material_index(mat, js) if mat is not None else None)
    result, partial = {}, 0
    keys = {}
    for si in np.unique(mi).tolist():
        if si >= len(slot_export) or slot_export[si] is None or slot_orig[si] is None:
            continue
        sel = mi == si
        for part in np.unique(parts[sel]).tolist():
            keys.setdefault((slot_orig[si], part), {})
            keys[(slot_orig[si], part)][slot_export[si]] = int((sel & (parts == part)).sum())
    if glb_parts is not None:
        # faces put on another imported material: key them by the part's material in the .glb
        rekeyed = {}
        for (orig, part), counts in keys.items():
            mats = glb_parts.get(part, set())
            if orig not in mats and len(mats) == 1:
                orig = next(iter(mats))
            bucket = rekeyed.setdefault((orig, part), {})
            for k, v in counts.items():
                bucket[k] = bucket.get(k, 0) + v
        keys = rekeyed
    for key, counts in keys.items():
        target = max(counts, key=counts.get)
        if len(counts) > 1:
            partial += 1
        if target != key[0]:
            result[key] = target
    if partial:
        report.add("warning", obj.name, "%d 個のパーツで、パーツの一部の面だけ別のマテリアルになっていました。"
                                        "Unity ではパーツ全体を多い方のマテリアルにしました" % partial)
    return result


def split_primitives(js, binary, mesh_index, assignment):
    if not assignment:
        return 0
    mesh = js["meshes"][mesh_index]
    new_prims, added = [], 0
    for prim in mesh["primitives"]:
        mat = prim.get("material")
        if mat is None or "indices" not in prim or SHAPE_ATTRIBUTE not in prim["attributes"] \
                or not any(k[0] == mat for k in assignment):
            new_prims.append(prim)
            continue
        idx = read_accessor(js, binary, prim["indices"]).astype(np.int64).reshape(-1, 3)
        shape = np.rint(read_accessor(js, binary, prim["attributes"][SHAPE_ATTRIBUTE])).astype(np.int64)
        tri_part = shape[idx[:, 0]]
        tri_mat = np.array([assignment.get((mat, int(p)), mat) for p in tri_part.tolist()], dtype=np.int64)
        for target in np.unique(tri_mat).tolist():
            p = copy.deepcopy(prim)
            p["material"] = int(target)
            p["indices"] = _add_indices(binary, js, idx[tri_mat == target].reshape(-1))
            new_prims.append(p)
            added += 1
        added -= 1
    mesh["primitives"] = new_prims
    return added


# --------------------------------------------------------------------------------------------
# Bodies taken out in Blender (detached / deleted): drop their nodes, children go to the parent
# --------------------------------------------------------------------------------------------

def _node_matrix(node):
    if "matrix" in node:
        return np.array(node["matrix"], dtype=np.float64).reshape(4, 4).T  # glTF is column-major
    t = node.get("translation", [0.0, 0.0, 0.0])
    x, y, z, w = node.get("rotation", [0.0, 0.0, 0.0, 1.0])
    s = node.get("scale", [1.0, 1.0, 1.0])
    r = np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])
    m = np.eye(4)
    m[:3, :3] = r * np.array(s)[None, :]
    m[:3, 3] = t
    return m


def _set_node_matrix(node, m):
    node.pop("matrix", None)
    scale = np.linalg.norm(m[:3, :3], axis=0)
    r = m[:3, :3] / np.where(scale > 1e-12, scale, 1.0)[None, :]
    tr = np.trace(r)
    if tr > 0:
        k = 0.5 / np.sqrt(tr + 1.0)
        q = [(r[2, 1] - r[1, 2]) * k, (r[0, 2] - r[2, 0]) * k, (r[1, 0] - r[0, 1]) * k, 0.25 / k]
    elif r[0, 0] > r[1, 1] and r[0, 0] > r[2, 2]:
        k = 2.0 * np.sqrt(1.0 + r[0, 0] - r[1, 1] - r[2, 2])
        q = [0.25 * k, (r[0, 1] + r[1, 0]) / k, (r[0, 2] + r[2, 0]) / k, (r[2, 1] - r[1, 2]) / k]
    elif r[1, 1] > r[2, 2]:
        k = 2.0 * np.sqrt(1.0 + r[1, 1] - r[0, 0] - r[2, 2])
        q = [(r[0, 1] + r[1, 0]) / k, 0.25 * k, (r[1, 2] + r[2, 1]) / k, (r[0, 2] - r[2, 0]) / k]
    else:
        k = 2.0 * np.sqrt(1.0 + r[2, 2] - r[0, 0] - r[1, 1])
        q = [(r[0, 2] + r[2, 0]) / k, (r[1, 2] + r[2, 1]) / k, 0.25 * k, (r[1, 0] - r[0, 1]) / k]
    q = np.array(q) / np.linalg.norm(q)
    node["translation"] = [float(v) for v in m[:3, 3]]
    node["rotation"] = [float(v) for v in q]
    if np.allclose(scale, 1.0, atol=1e-6):
        node.pop("scale", None)
    else:
        node["scale"] = [float(v) for v in scale]


def remove_bodies(js, removed_ids, report):
    """Drops the nodes of bodies taken out in Blender; their children move to the parent, keeping their
    world transform, and their joint info names the new parent. Returns the names of the removed nodes."""
    nodes = js["nodes"]
    removed = [i for i, n in enumerate(nodes) if n.get("mesh") is not None
               and n.get("extras", {}).get("sm_body") is not None and int(n["extras"]["sm_body"]) in removed_ids]
    if not removed:
        return []
    parent = {c: i for i, n in enumerate(nodes) for c in n.get("children", [])}
    scene = js["scenes"][js.get("scene", 0)]
    for i in removed:
        p = parent.get(i)
        mi = _node_matrix(nodes[i])
        kids = list(nodes[i].get("children", []))
        new_parent_body = nodes[p].get("extras", {}).get("sm_body", -1) if p is not None else -1
        for c in kids:
            _set_node_matrix(nodes[c], mi @ _node_matrix(nodes[c]))
            parent[c] = p
            joint = nodes[c].get("extras", {}).get("sm_joint")
            if joint is not None:
                joint.setdefault("parent_body_original", joint.get("parent_body", -1))
                joint["parent_body"] = new_parent_body
        siblings = nodes[p].setdefault("children", []) if p is not None else scene["nodes"]
        pos = siblings.index(i)
        siblings[pos:pos + 1] = kids
        nodes[i]["children"] = []
    gone = set(removed)
    keep = [i for i in range(len(nodes)) if i not in gone]
    remap = {old: new for new, old in enumerate(keep)}
    js["nodes"] = [nodes[i] for i in keep]
    for n in js["nodes"]:
        if "children" in n:
            n["children"] = [remap[c] for c in n["children"]]
            if not n["children"]:
                del n["children"]
    for sc in js.get("scenes", []):
        sc["nodes"] = [remap[c] for c in sc["nodes"] if c in remap]
    names = [nodes[i].get("name", str(i)) for i in removed]
    report.add("info", "", "Blender で外した・削除したボディ（%s）は書き出しに含めず、その子を親につなぎ直しました"
               % "、".join(names))
    return names


# --------------------------------------------------------------------------------------------
# Export
# --------------------------------------------------------------------------------------------

def export_blueprint(context, root, out_path):
    """Writes the Unity .glb for one imported blueprint. Returns the Report."""
    report = Report()
    from . import batch as _b
    src = _b.source_of(root)[0]
    if not src or not os.path.isfile(src):
        raise FileNotFoundError("元の .glb が見つかりません: %s" % src)
    glb = GlbFile(src)
    js = copy.deepcopy(glb.json)
    binary = bytearray(glb.bin)

    from . import batch as _batch
    bodies = [o for o in _batch.blueprint_objects(root) if o.type == "MESH"]
    used = []
    for obj in bodies:
        for slot in obj.material_slots:
            if slot.material is not None and "sm_shader" in slot.material and slot.material not in used:
                used.append(slot.material)

    # glTF material per Blender material (copies become new glTF materials)
    export_index_of = {}
    for mat in used:
        orig_idx = gltf_material_index(mat, js)
        if orig_idx is None:
            report.add("warning", mat.name, "元の .glb に対応するマテリアルがないため、書き出せませんでした")
            continue
        if reset.original_of(mat) == mat and orig_idx not in export_index_of.values():
            export_index_of[mat] = orig_idx
        else:
            entry = copy.deepcopy(js["materials"][orig_idx])
            entry["name"] = mat.name
            js["materials"].append(entry)
            export_index_of[mat] = len(js["materials"]) - 1

    image_cache = {}
    for mat, index in export_index_of.items():
        extras = js["materials"][index].setdefault("extras", {})
        smcad = {}
        ov = override_extras(mat, js, binary, report, image_cache)
        if ov:
            smcad["override"] = ov
        layers = layer_extras(mat, report)
        if layers:
            smcad["layers"] = layers
        for hook in list(api.export_material):
            try:
                hook(mat, smcad, report)
            except Exception as ex:  # noqa: BLE001
                report.add("warning", mat.name, "拡張機能の書き出しでエラー: %s" % ex)
        extras["smcad"] = smcad
        extras["smcad_blender_name"] = mat.name

    # part copies: split the primitives so every part keeps its material
    nodes_by_body = {}
    for i, node in enumerate(js["nodes"]):
        body = node.get("extras", {}).get("sm_body")
        if body is not None and node.get("mesh") is not None:
            nodes_by_body[int(body)] = node
    # Blender names (blueprint name + number) for the nodes and meshes
    for obj in bodies:
        body = obj.get("sm_body")
        node = nodes_by_body.get(int(body)) if body is not None else None
        if node is not None:
            node["name"] = obj.name
            js["meshes"][node["mesh"]]["name"] = obj.name
    for node in js["nodes"]:
        if "sm_shapes" in node.get("extras", {}):
            node["name"] = root.name
    split = 0
    for obj in bodies:
        body = obj.get("sm_body")
        node = nodes_by_body.get(int(body)) if body is not None else None
        if node is None:
            continue
        glb_parts = glb_part_materials(js, binary, node["mesh"])
        split += split_primitives(js, binary, node["mesh"],
                                  part_assignment(obj, js, export_index_of, report, glb_parts))

    from . import bodies as _bodies
    _detached, present = _bodies.removed_body_ids(root)
    all_ids = {int(n["extras"]["sm_body"]) for n in js["nodes"]
               if n.get("mesh") is not None and n.get("extras", {}).get("sm_body") is not None}
    removed_nodes = remove_bodies(js, all_ids - present, report)

    root_node = next((n for n in js["nodes"] if "sm_shapes" in n.get("extras", {})), js["nodes"][0])
    root_node.setdefault("extras", {})["smcad_export"] = {
        "version": EXPORT_VERSION,
        "source": os.path.basename(src),
        "blender": bpy.app.version_string,
        "materials": len(export_index_of),
        "split_primitives": split,
        "removed_bodies": removed_nodes,
        "report": report.items,
    }
    js["asset"]["generator"] = js["asset"].get("generator", "") + " + SMC-AD Blender export"
    write_glb(out_path, js, binary)
    return report


def report_text(report, out_path):
    lines = ["SMC-AD: Unity 向け書き出し", out_path, ""]
    warnings = report.warnings()
    infos = [i for i in report.items if i["level"] == "info"]
    lines.append("反映されないもの: %d 件" % len(warnings))
    lines += ["  - [%s] %s" % (i["material"], i["message"]) for i in warnings]
    lines.append("")
    lines.append("近似になるもの: %d 件" % len(infos))
    lines += ["  - [%s] %s" % (i["material"], i["message"]) for i in infos]
    return "\n".join(lines)
