# SPDX-License-Identifier: GPL-3.0-or-later
"""Diffuse override: replace the colour of an SM material with any colour or image.

Settings live on the material (Material.smcad_override), so they survive "Rebuild SM Materials".
An override can target
  - the paint only   (painted areas change, unpainted texture detail stays), or
  - the whole surface (texture and paint are replaced),
and can be limited to one paint colour (e.g. replace only the grey paint).

The nodes are inserted between the SM sources (Dif image / paint attribute) and the SM shader group.
"""

import bpy
import numpy as np
from bpy.props import (BoolProperty, EnumProperty, FloatProperty, FloatVectorProperty,
                       PointerProperty)

from . import batch, materials

NODE_PREFIX = "SMC.Override."
LEGACY_PREFIX = "SMCAD_OV_"
OVERRIDE_GROUP_VERSION = 1


# --------------------------------------------------------------------------------------------
# Node group
# --------------------------------------------------------------------------------------------

def ensure_override_group():
    group = bpy.data.node_groups.get("SMC_DiffuseOverride")
    if group is not None and group.get("smcad_version") == OVERRIDE_GROUP_VERSION:
        return group
    if group is None:
        group = bpy.data.node_groups.new("SMC_DiffuseOverride", "ShaderNodeTree")
    else:
        group.nodes.clear()
        group.interface.clear()
    group["smcad_version"] = OVERRIDE_GROUP_VERSION

    add = materials._add_input
    add(group, "Paint Color", "NodeSocketColor", (1, 1, 1, 1))
    add(group, "Diffuse", "NodeSocketColor", (0.8, 0.8, 0.8, 1))
    add(group, "Override Color", "NodeSocketColor", (0.8, 0.1, 0.1, 1))
    add(group, "Texture", "NodeSocketColor", (1, 1, 1, 1))
    add(group, "Use Texture", "NodeSocketFloat", 0.0, 0.0, 1.0)
    add(group, "Strength", "NodeSocketFloat", 1.0, 0.0, 1.0)
    add(group, "Whole Surface", "NodeSocketFloat", 0.0, 0.0, 1.0)
    add(group, "Match Color", "NodeSocketColor", (0.5, 0.5, 0.5, 1))
    add(group, "Use Match", "NodeSocketFloat", 0.0, 0.0, 1.0)
    add(group, "Tolerance", "NodeSocketFloat", 0.02, 0.0, 1.0)
    group.interface.new_socket("Paint Color", in_out="OUTPUT", socket_type="NodeSocketColor")
    group.interface.new_socket("Diffuse", in_out="OUTPUT", socket_type="NodeSocketColor")

    nodes, links = group.nodes, group.links
    gin = nodes.new("NodeGroupInput"); gin.location = (-900, 0)
    gout = nodes.new("NodeGroupOutput"); gout.location = (500, 0)
    mix, math = materials._mix_rgb, materials._math

    # Replacement colour: flat colour or texture
    replacement = mix(nodes, links, gin.outputs["Use Texture"], gin.outputs["Override Color"], gin.outputs["Texture"], (-600, 300))

    # Selection: 1 everywhere, or only where the paint matches the chosen colour
    dist = nodes.new("ShaderNodeVectorMath"); dist.operation = "DISTANCE"; dist.location = (-700, -150)
    links.new(gin.outputs["Paint Color"], dist.inputs[0])
    links.new(gin.outputs["Match Color"], dist.inputs[1])
    inside = math(nodes, links, "LESS_THAN", dist.outputs["Value"], gin.outputs["Tolerance"], (-500, -150))
    matched = math(nodes, links, "MULTIPLY", inside, gin.outputs["Use Match"], (-350, -150))
    everywhere = math(nodes, links, "SUBTRACT", 1.0, gin.outputs["Use Match"], (-350, -300))
    selection = math(nodes, links, "ADD", matched, everywhere, (-200, -200))
    factor = math(nodes, links, "MULTIPLY", selection, gin.outputs["Strength"], (-50, -200))
    surface_factor = math(nodes, links, "MULTIPLY", factor, gin.outputs["Whole Surface"], (100, -300))

    paint_out = mix(nodes, links, factor, gin.outputs["Paint Color"], replacement, (200, 150))
    diffuse_out = mix(nodes, links, surface_factor, gin.outputs["Diffuse"], replacement, (250, -100))
    links.new(paint_out, gout.inputs["Paint Color"])
    links.new(diffuse_out, gout.inputs["Diffuse"])
    return group


# --------------------------------------------------------------------------------------------
# Applying to a material
# --------------------------------------------------------------------------------------------

def _find(tree, predicate):
    return next((n for n in tree.nodes if predicate(n)), None)


def _sm_group_node(tree):
    """The node that takes Diffuse / Paint Color: SMC.Decode (current layout) or the v0.2 shader groups."""
    node = tree.nodes.get("SMC.Decode")
    if node is not None:
        return node
    return _find(tree, lambda n: n.bl_idname == "ShaderNodeGroup" and n.node_tree is not None
                 and n.node_tree.name in ("SMC_DifAsgNor", "SMC_Glass"))


def apply_override(material):
    """(Re)creates the override nodes of a material from its settings. Safe to call repeatedly."""
    tree = material.node_tree
    if tree is None:
        return False
    sm = _sm_group_node(tree)
    if sm is None:
        return False

    # Remove the previous override and restore the original wiring
    for node in [n for n in tree.nodes if n.name.startswith((NODE_PREFIX, LEGACY_PREFIX))]:
        tree.nodes.remove(node)

    dif = tree.nodes.get("SMC.Tex.Dif") or _find(tree, lambda n: n.bl_idname == "ShaderNodeTexImage" and n.label == "Dif")
    frame = tree.nodes.get("SMC.Frame.Paint")
    paint = _find(tree, lambda n: n.bl_idname == "ShaderNodeAttribute" and n.attribute_name == materials.PAINT_ATTRIBUTE)
    links = tree.links
    if dif is not None:
        links.new(dif.outputs["Color"], sm.inputs["Diffuse"])
    if paint is not None:
        links.new(paint.outputs["Color"], sm.inputs["Paint Color"])

    ov = material.smcad_override
    if not ov.enabled:
        return True

    node = tree.nodes.new("ShaderNodeGroup")
    node.node_tree = ensure_override_group()
    node.name = NODE_PREFIX + "Group"
    node.label = "色の差し替え（Diffuse Override）"
    if frame is not None:
        node.parent = frame
        node.location = (-1100, 520)  # above the paint attribute, inside frame ②
    else:
        node.location = (sm.location.x - 260, sm.location.y + 220)
    node.width = 220

    if paint is not None:
        links.new(paint.outputs["Color"], node.inputs["Paint Color"])
    if dif is not None:
        links.new(dif.outputs["Color"], node.inputs["Diffuse"])
    else:
        node.inputs["Diffuse"].default_value = sm.inputs["Diffuse"].default_value

    node.inputs["Override Color"].default_value = ov.color
    node.inputs["Strength"].default_value = ov.strength
    node.inputs["Whole Surface"].default_value = 1.0 if ov.target == "SURFACE" else 0.0
    node.inputs["Use Match"].default_value = 1.0 if ov.match_enabled else 0.0
    node.inputs["Match Color"].default_value = (*ov.match_color, 1.0)
    node.inputs["Tolerance"].default_value = ov.match_tolerance

    use_texture = ov.source == "IMAGE" and ov.image is not None
    node.inputs["Use Texture"].default_value = 1.0 if use_texture else 0.0
    if use_texture:
        coord = tree.nodes.new("ShaderNodeTexCoord")
        coord.name = NODE_PREFIX + "Coord"
        mapping = tree.nodes.new("ShaderNodeMapping")
        mapping.name = NODE_PREFIX + "Mapping"
        mapping.inputs["Scale"].default_value = (ov.scale, ov.scale, ov.scale)
        image = tree.nodes.new("ShaderNodeTexImage")
        image.name = NODE_PREFIX + "Image"
        image.label = "差し替え用テクスチャ"
        image.image = ov.image
        for i, n in enumerate((coord, mapping, image)):
            if frame is not None:
                n.parent = frame
            n.location = (node.location.x - 760 + i * 230, node.location.y + 260)
        if ov.projection == "BOX":
            image.projection = "BOX"
            image.projection_blend = 0.2
            links.new(coord.outputs["Object"], mapping.inputs["Vector"])
        else:
            links.new(coord.outputs["UV"], mapping.inputs["Vector"])
        links.new(mapping.outputs["Vector"], image.inputs["Vector"])
        links.new(image.outputs["Color"], node.inputs["Texture"])

    links.new(node.outputs["Paint Color"], sm.inputs["Paint Color"])
    links.new(node.outputs["Diffuse"], sm.inputs["Diffuse"])
    return True


_suspended = 0


class suspend_updates:
    """Batch edits: set many override properties without rebuilding the nodes after each one."""

    def __enter__(self):
        global _suspended
        _suspended += 1

    def __exit__(self, *exc):
        global _suspended
        _suspended -= 1


def _on_update(self, context):
    if _suspended:
        return
    material = self.id_data
    if isinstance(material, bpy.types.Material):
        apply_override(material)


# --------------------------------------------------------------------------------------------
# Paint colours used by a material
# --------------------------------------------------------------------------------------------

def srgb_hex_to_linear(hex_str):
    rgb = np.array([int(hex_str[i:i + 2], 16) / 255.0 for i in (0, 2, 4)])
    lin = np.where(rgb <= 0.04045, rgb / 12.92, np.power((rgb + 0.055) / 1.055, 2.4))
    return tuple(float(v) for v in lin)


def paint_colors(material):
    """Returns [(hex, vertex_count)] of the paint colours on faces that use the material."""
    users = [o for o in bpy.data.objects if o.type == "MESH"
             and any(s.material == material for s in o.material_slots)]
    colors = batch.paint_by_material(users).get(material, {})
    return sorted(colors.items(), key=lambda kv: -kv[1])


# --------------------------------------------------------------------------------------------
# Settings
# --------------------------------------------------------------------------------------------

class SMCAD_OverrideSettings(bpy.types.PropertyGroup):
    enabled: BoolProperty(name="色を差し替える", default=False, update=_on_update)
    target: EnumProperty(
        name="対象",
        items=[
            ("PAINT", "塗装だけ", "塗装の色だけを差し替えます。塗装されていないテクスチャ部分はそのまま"),
            ("SURFACE", "表面全体", "テクスチャも塗装も含めて、表面の色全体を差し替えます"),
        ],
        default="PAINT", update=_on_update)
    source: EnumProperty(
        name="差し替えるもの",
        items=[
            ("COLOR", "色", "1色で塗ります"),
            ("IMAGE", "画像", "画像テクスチャを使います"),
        ],
        default="COLOR", update=_on_update)
    color: FloatVectorProperty(name="色", subtype="COLOR", size=4, min=0.0, max=1.0,
                               default=(0.8, 0.1, 0.1, 1.0), update=_on_update)
    image: PointerProperty(name="画像", type=bpy.types.Image, update=_on_update)
    projection: EnumProperty(
        name="貼り方",
        items=[
            ("BOX", "6方向から投影", "オブジェクトの6方向から画像を投影します（どのパーツにも使えます）"),
            ("UV", "UV", "パーツのUVを使います（SMのアトラスのUV。主にブロック向け）"),
        ],
        default="BOX", update=_on_update)
    scale: FloatProperty(name="大きさ", description="1メートル（6方向）または UV 1つ分あたりの繰り返し回数",
                         default=1.0, min=0.001, soft_max=50.0, update=_on_update)
    strength: FloatProperty(name="強さ", default=1.0, min=0.0, max=1.0, subtype="FACTOR", update=_on_update)
    match_enabled: BoolProperty(name="1つの塗装色だけ",
                                description="選んだ色で塗られた面だけを差し替えます",
                                default=False, update=_on_update)
    match_color: FloatVectorProperty(name="差し替える塗装色", subtype="COLOR", size=3, min=0.0, max=1.0,
                                     default=(0.5, 0.5, 0.5), update=_on_update)
    match_hex: bpy.props.StringProperty(name="塗装色（16進）", default="")
    match_tolerance: FloatProperty(name="許容差", default=0.02, min=0.0, max=1.0, update=_on_update)


def copy_settings(src, dst):
    """Copies every override setting. Call apply_override(dst material) afterwards when suspended."""
    for key in ("enabled", "target", "source", "color", "image", "projection", "scale",
                "strength", "match_enabled", "match_color", "match_hex", "match_tolerance"):
        setattr(dst, key, getattr(src, key))
