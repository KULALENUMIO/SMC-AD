# SPDX-License-Identifier: GPL-3.0-or-later
"""Scrap Mechanic materials, laid out for hand shading.

Shading model ported from SM-Converter-Plugin by QuestionableM
(MIT License, https://github.com/QuestionableM/SM-Converter-Plugin):
  - base colour = mix(paint colour, Dif.rgb, Dif.a)          (Dif.a = 1 on texture, 0 on paint)
  - ASG: R = cut-out alpha, G = specular, B = glow, A = gloss (roughness = 1 - A)
  - tangent space normal map
Paint colour comes from the per-vertex "Color" attribute written by SMConverter (COLOR_0, linear).

Material layout (docs/REQUIREMENTS_SHADING.md, N1-N8), left to right, one frame each:

  [1 SM textures] -> [2 paint & masks] -> [3 SM decode] -> [4 layers: add yours here] -> [5 output]
   SMC.Tex.*          SMC.Paint, SMC.Masks   SMC.Decode       SMC.Layer.* reroutes          SMC.BSDF
                      (colour override)                                                   SMC.Output

Every node SMC-AD owns is named "SMC.*". Rebuilding only replaces those nodes and restores the links
between them and the user's own nodes, so hand-made layers survive a rebuild.
"""

import os
import tempfile

import bpy

GROUP_VERSION = 3
LAYOUT_VERSION = 1
PAINT_ATTRIBUTE = "Color"
SHAPE_ATTRIBUTE = "_SM_SHAPE"
OWNED_PREFIX = "SMC."

# SMConverter shader ids (Resources/MaterialIds.json)
SHADER_DIFASGNOR = 1
SHADER_GLASS = 2
SHADER_GRASS = 3
SHADER_LEAVES = 4
SHADER_DIFASGNOR_ALPHA = 5
SHADER_WATER = 6
SHADER_LIGHTCONE = 7
SHADER_LIQUID = 8
SHADER_CHEMICALS = 9
SHADER_OIL = 10
SHADER_CLUTTER = 11

FOLIAGE_SHADERS = {SHADER_GRASS, SHADER_LEAVES, SHADER_CLUTTER}
GLASS_SHADERS = {SHADER_GLASS, SHADER_WATER, SHADER_LIQUID, SHADER_CHEMICALS, SHADER_OIL}

# (reroute name, label, decode output, BSDF input)
LAYER_CHANNELS = [
    ("SMC.Layer.BaseColor", "Base Color（色）", "Base Color", "Base Color"),
    ("SMC.Layer.Metallic", "Metallic（金属感）", "Metallic", "Metallic"),
    ("SMC.Layer.Roughness", "Roughness（粗さ）", "Roughness", "Roughness"),
    ("SMC.Layer.Specular", "Specular（反射）", "Specular", "Specular IOR Level"),
    ("SMC.Layer.Normal", "Normal（凹凸）", "Normal", "Normal"),
    ("SMC.Layer.Emission", "Emission（発光）", "Emission Strength", "Emission Strength"),
    ("SMC.Layer.Alpha", "Alpha（切り抜き）", "Alpha", "Alpha"),
]

# x positions of the five columns
COL_TEX, COL_PAINT, COL_DECODE, COL_LAYER, COL_OUT = -1500, -1100, -700, -330, 0
FRAME_COLORS = {
    "SMC.Frame.Textures": (0.20, 0.27, 0.40),
    "SMC.Frame.Paint": (0.40, 0.30, 0.18),
    "SMC.Frame.Decode": (0.27, 0.27, 0.27),
    "SMC.Frame.Layers": (0.18, 0.40, 0.22),
    "SMC.Frame.Output": (0.33, 0.22, 0.40),
}


# --------------------------------------------------------------------------------------------
# Node helpers
# --------------------------------------------------------------------------------------------

def _socket(sockets, name, socket_type=None):
    """Finds a socket by name (and type, for nodes like Mix that reuse names)."""
    for s in sockets:
        if s.name == name and s.enabled and (socket_type is None or s.type == socket_type):
            return s
    for s in sockets:
        if s.name == name and (socket_type is None or s.type == socket_type):
            return s
    raise KeyError(name)


def _new_group(name):
    group = bpy.data.node_groups.get(name)
    if group is not None and group.get("smcad_version") == GROUP_VERSION:
        return group, False
    if group is None:
        group = bpy.data.node_groups.new(name, "ShaderNodeTree")
    else:
        group.nodes.clear()
        group.interface.clear()
    group["smcad_version"] = GROUP_VERSION
    return group, True


def _add_input(group, name, socket_type, default=None, min_value=None, max_value=None):
    sock = group.interface.new_socket(name, in_out="INPUT", socket_type=socket_type)
    if default is not None:
        sock.default_value = default
    if min_value is not None:
        sock.min_value = min_value
    if max_value is not None:
        sock.max_value = max_value
    return sock


def _add_output(group, name, socket_type):
    return group.interface.new_socket(name, in_out="OUTPUT", socket_type=socket_type)


def _mix_rgb(nodes, links, factor, a, b, location):
    mix = nodes.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.location = location
    links.new(factor, _socket(mix.inputs, "Factor", "VALUE"))
    links.new(a, _socket(mix.inputs, "A", "RGBA"))
    links.new(b, _socket(mix.inputs, "B", "RGBA"))
    return _socket(mix.outputs, "Result", "RGBA")


def _math(nodes, links, operation, a, b, location):
    node = nodes.new("ShaderNodeMath")
    node.operation = operation
    node.location = location
    for sock, value in zip(node.inputs, (a, b)):
        if isinstance(value, (int, float)):
            sock.default_value = value
        else:
            links.new(value, sock)
    return node.outputs[0]


# --------------------------------------------------------------------------------------------
# Node groups
# --------------------------------------------------------------------------------------------

def ensure_decode_group():
    """SM textures + paint -> PBR channels (no BSDF inside, so every channel can be edited)."""
    group, rebuilt = _new_group("SMC_Decode")
    if not rebuilt:
        return group

    _add_input(group, "Diffuse", "NodeSocketColor", (0.8, 0.8, 0.8, 1.0))
    _add_input(group, "Diffuse Alpha", "NodeSocketFloat", 1.0, 0.0, 1.0)
    _add_input(group, "Paint Color", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    _add_input(group, "Asg", "NodeSocketColor", (0.5, 0.5, 0.0, 1.0))
    _add_input(group, "Asg Alpha", "NodeSocketFloat", 0.5, 0.0, 1.0)
    _add_input(group, "Normal Map", "NodeSocketColor", (0.5, 0.5, 1.0, 1.0))
    _add_input(group, "Use Paint Mask", "NodeSocketFloat", 1.0, 0.0, 1.0)
    _add_input(group, "Alpha Mode", "NodeSocketFloat", 0.0, 0.0, 2.0)  # 0 none, 1 Dif.a, 2 Asg.r
    _add_input(group, "Specular Multiplier", "NodeSocketFloat", 1.0, 0.0)
    _add_input(group, "Glow Multiplier", "NodeSocketFloat", 1.0, 0.0)
    _add_input(group, "Normal Strength", "NodeSocketFloat", 1.0, 0.0)
    _add_output(group, "Base Color", "NodeSocketColor")
    _add_output(group, "Metallic", "NodeSocketFloat")
    _add_output(group, "Roughness", "NodeSocketFloat")
    _add_output(group, "Specular", "NodeSocketFloat")
    _add_output(group, "Normal", "NodeSocketVector")
    _add_output(group, "Emission Strength", "NodeSocketFloat")
    _add_output(group, "Alpha", "NodeSocketFloat")
    _add_output(group, "Paint Mask", "NodeSocketFloat")

    nodes, links = group.nodes, group.links
    gin = nodes.new("NodeGroupInput"); gin.location = (-900, 0)
    gout = nodes.new("NodeGroupOutput"); gout.location = (400, 0)

    # Texture mask: Dif.a, or 1 when the material has no paint mask (foliage)
    no_mask = _math(nodes, links, "SUBTRACT", 1.0, gin.outputs["Use Paint Mask"], (-650, 450))
    tex_mask = _math(nodes, links, "MAXIMUM", gin.outputs["Diffuse Alpha"], no_mask, (-500, 450))
    base = _mix_rgb(nodes, links, tex_mask, gin.outputs["Paint Color"], gin.outputs["Diffuse"], (-300, 350))
    links.new(base, gout.inputs["Base Color"])
    paint_mask = _math(nodes, links, "SUBTRACT", 1.0, tex_mask, (-300, 500))
    links.new(paint_mask, gout.inputs["Paint Mask"])

    metal = nodes.new("ShaderNodeValue"); metal.location = (-300, 200); metal.label = "Metallic (SM: 0)"
    metal.outputs[0].default_value = 0.0
    links.new(metal.outputs[0], gout.inputs["Metallic"])

    sep = nodes.new("ShaderNodeSeparateColor"); sep.location = (-650, -50)
    links.new(gin.outputs["Asg"], sep.inputs[0])
    spec = _math(nodes, links, "MULTIPLY", _socket(sep.outputs, "Green"), gin.outputs["Specular Multiplier"], (-300, 50))
    links.new(spec, gout.inputs["Specular"])
    rough = _math(nodes, links, "SUBTRACT", 1.0, gin.outputs["Asg Alpha"], (-300, -100))
    links.new(rough, gout.inputs["Roughness"])

    ramp = nodes.new("ShaderNodeValToRGB"); ramp.location = (-450, -250)
    ramp.color_ramp.elements[0].position = 0.15
    ramp.color_ramp.elements[0].color = (0, 0, 0, 1)
    ramp.color_ramp.elements[1].position = 1.0
    ramp.color_ramp.elements[1].color = (1, 1, 1, 1)
    links.new(_socket(sep.outputs, "Blue"), ramp.inputs[0])
    glow = _math(nodes, links, "MULTIPLY", ramp.outputs[0], gin.outputs["Glow Multiplier"], (-150, -250))
    links.new(glow, gout.inputs["Emission Strength"])

    nmap = nodes.new("ShaderNodeNormalMap"); nmap.location = (-300, -420)
    links.new(gin.outputs["Normal Map"], _socket(nmap.inputs, "Color"))
    links.new(gin.outputs["Normal Strength"], _socket(nmap.inputs, "Strength"))
    links.new(nmap.outputs[0], gout.inputs["Normal"])

    # Alpha: mode 0 -> 1, mode 1 -> Dif.a, mode 2 -> Asg.r
    is1 = _math(nodes, links, "COMPARE", gin.outputs["Alpha Mode"], 1.0, (-500, -600)); is1.node.inputs[2].default_value = 0.5
    is2 = _math(nodes, links, "COMPARE", gin.outputs["Alpha Mode"], 2.0, (-500, -750)); is2.node.inputs[2].default_value = 0.5
    a = _mix_float(nodes, links, is1, 1.0, gin.outputs["Diffuse Alpha"], (-300, -620))
    a = _mix_float(nodes, links, is2, a, _socket(sep.outputs, "Red"), (-150, -700))
    links.new(a, gout.inputs["Alpha"])
    return group


def _mix_float(nodes, links, factor, a, b, location):
    mix = nodes.new("ShaderNodeMix")
    mix.data_type = "FLOAT"
    mix.location = location
    for sock, value in ((_socket(mix.inputs, "Factor", "VALUE"), factor),
                        (_socket(mix.inputs, "A", "VALUE"), a),
                        (_socket(mix.inputs, "B", "VALUE"), b)):
        if isinstance(value, (int, float)):
            sock.default_value = value
        else:
            links.new(value, sock)
    return _socket(mix.outputs, "Result", "VALUE")


def ensure_masks_group():
    """Masks for hand shading: paint / texture areas, per part / per body random, up facing, part id."""
    group, rebuilt = _new_group("SMC_Masks")
    if not rebuilt:
        return group

    _add_input(group, "Diffuse Alpha", "NodeSocketFloat", 1.0, 0.0, 1.0)
    _add_output(group, "Paint Mask", "NodeSocketFloat")
    _add_output(group, "Texture Mask", "NodeSocketFloat")
    _add_output(group, "Part Random", "NodeSocketFloat")
    _add_output(group, "Body Random", "NodeSocketFloat")
    _add_output(group, "Up Facing", "NodeSocketFloat")
    _add_output(group, "Part ID", "NodeSocketFloat")

    nodes, links = group.nodes, group.links
    gin = nodes.new("NodeGroupInput"); gin.location = (-700, 0)
    gout = nodes.new("NodeGroupOutput"); gout.location = (300, 0)

    links.new(gin.outputs["Diffuse Alpha"], gout.inputs["Texture Mask"])
    links.new(_math(nodes, links, "SUBTRACT", 1.0, gin.outputs["Diffuse Alpha"], (0, 250)), gout.inputs["Paint Mask"])

    shape = nodes.new("ShaderNodeAttribute"); shape.location = (-450, 50)
    shape.attribute_type = "GEOMETRY"; shape.attribute_name = SHAPE_ATTRIBUTE
    links.new(shape.outputs["Fac"], gout.inputs["Part ID"])
    noise = nodes.new("ShaderNodeTexWhiteNoise"); noise.location = (-200, 50)
    noise.noise_dimensions = "1D"
    links.new(shape.outputs["Fac"], noise.inputs["W"])
    links.new(noise.outputs["Value"], gout.inputs["Part Random"])

    info = nodes.new("ShaderNodeObjectInfo"); info.location = (-200, -150)
    links.new(info.outputs["Random"], gout.inputs["Body Random"])

    geo = nodes.new("ShaderNodeNewGeometry"); geo.location = (-450, -300)
    sep = nodes.new("ShaderNodeSeparateXYZ"); sep.location = (-250, -300)
    links.new(geo.outputs["Normal"], sep.inputs[0])
    up = _math(nodes, links, "MAXIMUM", sep.outputs["Z"], 0.0, (0, -300))
    links.new(up, gout.inputs["Up Facing"])
    return group


def ensure_glass_shader_group():
    """Glass output stage (see requirements N8: kept on Glass BSDF until decided)."""
    group, rebuilt = _new_group("SMC_GlassShader")
    if not rebuilt:
        return group
    _add_input(group, "Color", "NodeSocketColor", (0.8, 0.8, 0.8, 1.0))
    _add_input(group, "Roughness", "NodeSocketFloat", 0.0, 0.0, 1.0)
    _add_input(group, "Normal", "NodeSocketVector")
    _add_input(group, "Roughness Multiplier", "NodeSocketFloat", 0.3, 0.0)
    _add_input(group, "IOR", "NodeSocketFloat", 1.15, 1.0)
    _add_input(group, "Transparency", "NodeSocketFloat", 0.7, 0.0, 1.0)
    _add_output(group, "BSDF", "NodeSocketShader")

    nodes, links = group.nodes, group.links
    gin = nodes.new("NodeGroupInput"); gin.location = (-600, 0)
    gout = nodes.new("NodeGroupOutput"); gout.location = (400, 0)
    rough = _math(nodes, links, "MULTIPLY", gin.outputs["Roughness"], gin.outputs["Roughness Multiplier"], (-350, -100))
    glass = nodes.new("ShaderNodeBsdfGlass"); glass.location = (-100, 50)
    links.new(gin.outputs["Color"], _socket(glass.inputs, "Color"))
    links.new(rough, _socket(glass.inputs, "Roughness"))
    links.new(gin.outputs["IOR"], _socket(glass.inputs, "IOR"))
    links.new(gin.outputs["Normal"], _socket(glass.inputs, "Normal"))
    transparent = nodes.new("ShaderNodeBsdfTransparent"); transparent.location = (-100, 250)
    links.new(gin.outputs["Color"], _socket(transparent.inputs, "Color"))
    mix = nodes.new("ShaderNodeMixShader"); mix.location = (150, 100)
    links.new(gin.outputs["Transparency"], mix.inputs[0])
    links.new(glass.outputs[0], mix.inputs[1])
    links.new(transparent.outputs[0], mix.inputs[2])
    links.new(mix.outputs[0], gout.inputs[0])
    return group


def ensure_lightcone_shader_group():
    group, rebuilt = _new_group("SMC_LightConeShader")
    if not rebuilt:
        return group
    _add_input(group, "Color", "NodeSocketColor", (1.0, 1.0, 1.0, 1.0))
    _add_input(group, "Strength", "NodeSocketFloat", 1.0, 0.0)
    _add_input(group, "Opacity", "NodeSocketFloat", 0.15, 0.0, 1.0)
    _add_output(group, "BSDF", "NodeSocketShader")
    nodes, links = group.nodes, group.links
    gin = nodes.new("NodeGroupInput"); gin.location = (-500, 0)
    gout = nodes.new("NodeGroupOutput"); gout.location = (300, 0)
    emission = nodes.new("ShaderNodeEmission"); emission.location = (-200, 100)
    links.new(gin.outputs["Color"], _socket(emission.inputs, "Color"))
    links.new(gin.outputs["Strength"], _socket(emission.inputs, "Strength"))
    transparent = nodes.new("ShaderNodeBsdfTransparent"); transparent.location = (-200, -100)
    mix = nodes.new("ShaderNodeMixShader"); mix.location = (50, 0)
    links.new(gin.outputs["Opacity"], mix.inputs[0])
    links.new(transparent.outputs[0], mix.inputs[1])
    links.new(emission.outputs[0], mix.inputs[2])
    links.new(mix.outputs[0], gout.inputs[0])
    return group


# --------------------------------------------------------------------------------------------
# Images
# --------------------------------------------------------------------------------------------

class ImageCache:
    """Creates packed Blender images from textures embedded in a glb (each texture once)."""

    def __init__(self, glb, prefix):
        self.glb = glb
        self.prefix = prefix
        self.cache = {}
        self.tmpdir = tempfile.mkdtemp(prefix="smcad_")

    def get(self, texture_index, non_color):
        if texture_index is None or texture_index < 0:
            return None
        key = (texture_index, non_color)
        if key in self.cache:
            return self.cache[key]

        result = self.glb.texture_image(texture_index)
        image = None
        if result is not None:
            name, data = result
            # Same texture already in the file (re-import, rebuild, reset): reuse it instead of adding ".001"
            existing = bpy.data.images.get("%s%s" % (self.prefix, name))
            if existing is not None and existing.packed_file is not None and not existing.is_dirty \
                    and (existing.colorspace_settings.name == "Non-Color") == non_color \
                    and bytes(existing.packed_file.data) == data:
                self.cache[key] = existing
                return existing
            safe = "".join(c if c.isalnum() or c in "-_." else "_" for c in name)
            path = os.path.join(self.tmpdir, "%d_%s.png" % (texture_index, safe))
            with open(path, "wb") as f:
                f.write(data)
            image = bpy.data.images.load(path)
            image.pack()
            image.name = "%s%s" % (self.prefix, name)
            image.filepath = ""
            # Alpha is data (paint mask / cut-out / gloss), never premultiplied transparency
            image.alpha_mode = "CHANNEL_PACKED"
            if non_color:
                image.colorspace_settings.name = "Non-Color"
            try:
                os.remove(path)
            except OSError:
                pass

        self.cache[key] = image
        return image

    def close(self):
        try:
            os.rmdir(self.tmpdir)
        except OSError:
            pass


# --------------------------------------------------------------------------------------------
# Material assembly
# --------------------------------------------------------------------------------------------

def _owned(node):
    return node.name.startswith(OWNED_PREFIX)


def _remember_user_links(tree):
    """Links between SMC.* nodes and user nodes, by node name + socket identifier."""
    kept = []
    for link in tree.links:
        a, b = link.from_node, link.to_node
        if _owned(a) != _owned(b):
            kept.append((a.name, link.from_socket.identifier, b.name, link.to_socket.identifier))
    return kept


def _restore_user_links(tree, kept):
    restored = 0
    for from_name, from_id, to_name, to_id in kept:
        a, b = tree.nodes.get(from_name), tree.nodes.get(to_name)
        if a is None or b is None:
            continue
        out = next((s for s in a.outputs if s.identifier == from_id), None)
        inp = next((s for s in b.inputs if s.identifier == to_id), None)
        if out is None or inp is None:
            continue
        # A user node now feeding a core input replaces the core's own link (e.g. Mix on a layer wire)
        for old in [l for l in tree.links if l.to_socket == inp]:
            tree.links.remove(old)
        tree.links.new(out, inp)
        restored += 1
    return restored


def _remember_user_parents(tree):
    """User nodes placed inside an SMC.* frame (e.g. a Mix in ④): frame name + absolute position."""
    kept = []
    for node in tree.nodes:
        if not _owned(node) and node.parent is not None and _owned(node.parent):
            kept.append((node.name, node.parent.name, tuple(node.location_absolute)))
    return kept


def _restore_user_parents(tree, kept):
    for node_name, frame_name, location in kept:
        node, frame = tree.nodes.get(node_name), tree.nodes.get(frame_name)
        if node is None or frame is None:
            continue
        node.parent = frame
        node.location_absolute = location


def _clear_owned(tree):
    """Removes SMC.* nodes; on materials from older SMC-AD versions (no SMC.* names) removes everything."""
    if not any(_owned(n) for n in tree.nodes):
        tree.nodes.clear()
        return
    for node in [n for n in tree.nodes if _owned(n)]:
        tree.nodes.remove(node)


def _frame(nodes, name, label, x, y):
    frame = nodes.new("NodeFrame")
    frame.name = name
    frame.label = label
    frame.label_size = 18
    frame.use_custom_color = True
    frame.color = FRAME_COLORS[name]
    frame.shrink = True
    frame.location = (x, y)
    return frame


def _node(nodes, idname, name, label, x, y, frame=None):
    node = nodes.new(idname)
    node.name = name
    node.label = label
    if frame is not None:
        node.parent = frame
    node.location = (x, y)
    return node


def _image_node(nodes, name, label, image, x, y, frame):
    node = _node(nodes, "ShaderNodeTexImage", name, label, x, y, frame)
    node.image = image
    node.width = 220
    return node


def build_material(material, images, specular=1.0, glow=1.0):
    """(Re)builds an imported glTF material as an SM material. Returns False if it isn't an SM material."""
    if "sm_shader" not in material:
        return False

    shader = int(material["sm_shader"])
    dif = images.get(material.get("sm_dif_texture"), non_color=False) if images else None
    asg = images.get(material.get("sm_asg_texture"), non_color=True) if images else None
    nor = images.get(material.get("sm_nor_texture"), non_color=True) if images else None

    # Blender 5+ materials always use nodes (use_nodes is deprecated there)
    if bpy.app.version < (5, 0, 0) or material.node_tree is None:
        material.use_nodes = True
    tree = material.node_tree
    nodes, links = tree.nodes, tree.links

    # Keep the images of an earlier build when rebuilding without new ones
    if images is None:
        prev = {n.name: n.image for n in nodes if n.bl_idname == "ShaderNodeTexImage" and _owned(n)}
        dif, asg, nor = prev.get("SMC.Tex.Dif"), prev.get("SMC.Tex.Asg"), prev.get("SMC.Tex.Nor")

    kept = _remember_user_links(tree)
    kept_parents = _remember_user_parents(tree)
    _clear_owned(tree)

    is_glass = shader in GLASS_SHADERS
    is_cone = shader == SHADER_LIGHTCONE

    # ---- frames
    f_tex = _frame(nodes, "SMC.Frame.Textures", "① SMテクスチャ", COL_TEX, 0)
    f_paint = _frame(nodes, "SMC.Frame.Paint", "② 塗装・マスク", COL_PAINT, 0)
    f_dec = _frame(nodes, "SMC.Frame.Decode", "③ SMデコード", COL_DECODE, 0)
    f_layer = _frame(nodes, "SMC.Frame.Layers", "④ レイヤー（ここに追加）", COL_LAYER, 0)
    f_out = _frame(nodes, "SMC.Frame.Output", "⑤ 出力", COL_OUT, 0)

    # ---- 1 textures
    dif_node = _image_node(nodes, "SMC.Tex.Dif", "Dif（色 / A=塗装マスク）", dif, COL_TEX, 200, f_tex) if dif else None
    asg_node = _image_node(nodes, "SMC.Tex.Asg", "Asg（R切り抜き / G反射 / B発光 / Aつや）", asg, COL_TEX, -100, f_tex) if asg else None
    nor_node = _image_node(nodes, "SMC.Tex.Nor", "Nor（ノーマル）", nor, COL_TEX, -400, f_tex) if nor else None

    # ---- 2 paint & masks
    paint = _node(nodes, "ShaderNodeAttribute", "SMC.Paint", "塗装色（頂点カラー）", COL_PAINT, 250, f_paint)
    paint.attribute_type = "GEOMETRY"
    paint.attribute_name = PAINT_ATTRIBUTE
    masks = _node(nodes, "ShaderNodeGroup", "SMC.Masks", "マスク（自由に使ってください）", COL_PAINT, -100, f_paint)
    masks.node_tree = ensure_masks_group()
    masks.width = 200
    if dif_node is not None:
        links.new(dif_node.outputs["Alpha"], masks.inputs["Diffuse Alpha"])

    # ---- 3 decode
    dec = _node(nodes, "ShaderNodeGroup", "SMC.Decode", "SMデコード", COL_DECODE, 150, f_dec)
    dec.node_tree = ensure_decode_group()
    dec.width = 220
    links.new(paint.outputs["Color"], dec.inputs["Paint Color"])
    if dif_node is not None:
        links.new(dif_node.outputs["Color"], dec.inputs["Diffuse"])
        links.new(dif_node.outputs["Alpha"], dec.inputs["Diffuse Alpha"])
    if asg_node is not None:
        links.new(asg_node.outputs["Color"], dec.inputs["Asg"])
        links.new(asg_node.outputs["Alpha"], dec.inputs["Asg Alpha"])
    if nor_node is not None:
        links.new(nor_node.outputs["Color"], dec.inputs["Normal Map"])
    dec.inputs["Specular Multiplier"].default_value = specular
    dec.inputs["Glow Multiplier"].default_value = glow
    if shader in FOLIAGE_SHADERS:
        dec.inputs["Use Paint Mask"].default_value = 0.0
        dec.inputs["Alpha Mode"].default_value = 1.0
    elif shader == SHADER_DIFASGNOR_ALPHA:
        dec.inputs["Alpha Mode"].default_value = 2.0

    # ---- 4 layer insertion points (one reroute per channel)
    reroutes = {}
    for i, (name, label, dec_out, _bsdf_in) in enumerate(LAYER_CHANNELS):
        r = _node(nodes, "NodeReroute", name, label, COL_LAYER, 300 - i * 90, f_layer)
        links.new(dec.outputs[dec_out], r.inputs[0])
        reroutes[name] = r

    # ---- 5 output
    out = _node(nodes, "ShaderNodeOutputMaterial", "SMC.Output", "", COL_OUT + 380, 150, f_out)
    if is_glass:
        bsdf = _node(nodes, "ShaderNodeGroup", "SMC.BSDF", "ガラス", COL_OUT, 150, f_out)
        bsdf.node_tree = ensure_glass_shader_group()
        links.new(reroutes["SMC.Layer.BaseColor"].outputs[0], bsdf.inputs["Color"])
        links.new(reroutes["SMC.Layer.Roughness"].outputs[0], bsdf.inputs["Roughness"])
        links.new(reroutes["SMC.Layer.Normal"].outputs[0], bsdf.inputs["Normal"])
    elif is_cone:
        bsdf = _node(nodes, "ShaderNodeGroup", "SMC.BSDF", "ライトコーン", COL_OUT, 150, f_out)
        bsdf.node_tree = ensure_lightcone_shader_group()
        links.new(reroutes["SMC.Layer.BaseColor"].outputs[0], bsdf.inputs["Color"])
    else:
        bsdf = _node(nodes, "ShaderNodeBsdfPrincipled", "SMC.BSDF", "", COL_OUT, 150, f_out)
        for name, _label, _dec_out, bsdf_in in LAYER_CHANNELS:
            links.new(reroutes[name].outputs[0], _socket(bsdf.inputs, bsdf_in))
        links.new(reroutes["SMC.Layer.BaseColor"].outputs[0], _socket(bsdf.inputs, "Emission Color"))
    links.new(bsdf.outputs[0], out.inputs["Surface"])

    restored = _restore_user_links(tree, kept)
    _restore_user_parents(tree, kept_parents)

    material.use_backface_culling = False
    material.surface_render_method = "BLENDED" if (is_glass or is_cone) else "DITHERED"
    material["smcad_built"] = True
    material["smcad_layout"] = LAYOUT_VERSION
    material["smcad_restored_links"] = restored

    # Colour override (②) uses the decode node; re-apply after a rebuild
    from . import api, overrides
    if material.smcad_override.enabled:
        overrides.apply_override(material)
    api.call(api.material_built, material)
    return True
