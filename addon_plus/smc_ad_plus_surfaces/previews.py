# SPDX-License-Identifier: GPL-3.0-or-later
"""Sphere thumbnails of the surface types (S6).

One small preview material per surface type, "SMC+.Preview.<id>", that uses the shared surface group,
so the thumbnail follows the current values. Blender renders the icon itself (material preview).
The materials have no users and are not saved with the file; they are re-created when needed.

So that the type reads at a glance, the sphere wears a small texture of that material (wood grain, tyre
tread, brushed steel, ...): preview_textures/<id>_color.png, <id>_height.png (bump) and, for painted
metal, <id>_mask.png (paint / chipped). Made by tools/make_surface_previews.py. Surface types without
textures (made by the user) keep the plain grey sphere.
"""

import os

import bpy

from . import surfaces

PREFIX = "SMC+.Preview."
VERSION = 3
BASE_COLOR = (0.42, 0.45, 0.50, 1.0)
BASE_ROUGHNESS = 0.4
TEXTURE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "preview_textures")
# bump strength per texture (the height maps are all normalised to 0..1)
BUMP = {"standard": 0.35, "metal_painted": 0.3, "metal_bare": 0.08, "rubber": 0.7, "plastic": 0.04,
        "wood": 0.3, "stone": 0.5}
# how many times the texture repeats (rubber: several tread rows, so it reads as a tyre)
REPEAT = {"rubber": 2.5}


def _image(sid, kind):
    """The preview texture <sid>_<kind>.png as an image datablock, or None when there is none."""
    path = os.path.join(TEXTURE_DIR, "%s_%s.png" % (sid, kind))
    if not os.path.isfile(path):
        return None
    name = "%s%s.%s" % (PREFIX, sid, kind)
    image = bpy.data.images.get(name)
    if image is None or os.path.normcase(bpy.path.abspath(image.filepath)) != os.path.normcase(path):
        image = bpy.data.images.load(path, check_existing=False)
        image.name = name
    if kind != "color":
        image.colorspace_settings.name = "Non-Color"
    return image


def _textures(tree, sid):
    """Texture nodes for the sphere: {"color": socket, "normal": socket, "mask": socket} (missing = None)."""
    color_img = _image(sid, "color")
    if color_img is None:
        return {}
    nodes, links = tree.nodes, tree.links
    coord = nodes.new("ShaderNodeTexCoord"); coord.location = (-1200, 0)
    scale = nodes.new("ShaderNodeVectorMath"); scale.location = (-1000, 0)
    scale.operation = "SCALE"
    scale.inputs["Scale"].default_value = REPEAT.get(sid, 1.0)
    links.new(coord.outputs["Object"], scale.inputs[0])

    def tex(image, y):
        node = nodes.new("ShaderNodeTexImage"); node.location = (-800, y)
        node.image = image
        node.projection = "BOX"
        node.projection_blend = 0.3
        links.new(scale.outputs["Vector"], node.inputs["Vector"])
        return node

    result = {"color": tex(color_img, 300).outputs["Color"]}
    height_img = _image(sid, "height")
    if height_img is not None:
        bump = nodes.new("ShaderNodeBump"); bump.location = (-500, -300)
        bump.inputs["Strength"].default_value = BUMP.get(sid, 0.3)
        bump.inputs["Distance"].default_value = 0.02
        links.new(tex(height_img, 0).outputs["Color"], bump.inputs["Height"])
        result["normal"] = bump.outputs["Normal"]
    mask_img = _image(sid, "mask")
    if mask_img is not None:
        result["mask"] = tex(mask_img, -300).outputs["Color"]
    return result


def _build(material, sid):
    if bpy.app.version < (5, 0, 0) or material.node_tree is None:
        material.use_nodes = True
    tree = material.node_tree
    tree.nodes.clear()
    nodes, links = tree.nodes, tree.links
    out = nodes.new("ShaderNodeOutputMaterial"); out.location = (500, 0)
    bsdf = nodes.new("ShaderNodeBsdfPrincipled"); bsdf.location = (200, 0)
    links.new(bsdf.outputs[0], out.inputs["Surface"])
    color = nodes.new("ShaderNodeRGB"); color.location = (-400, 200)
    color.outputs[0].default_value = BASE_COLOR
    rough = nodes.new("ShaderNodeValue"); rough.location = (-400, 0)
    rough.outputs[0].default_value = BASE_ROUGHNESS
    spec = nodes.new("ShaderNodeValue"); spec.location = (-400, -100)
    spec.outputs[0].default_value = 0.5
    zero = nodes.new("ShaderNodeValue"); zero.location = (-400, -200)
    zero.outputs[0].default_value = 0.0
    geo = nodes.new("ShaderNodeNewGeometry"); geo.location = (-400, -350)
    tex = _textures(tree, sid)
    color_out = tex.get("color", color.outputs[0])
    normal_out = tex.get("normal", geo.outputs["Normal"])

    if sid == surfaces.NO_SURFACE:
        links.new(color_out, bsdf.inputs["Base Color"])
        links.new(rough.outputs[0], bsdf.inputs["Roughness"])
        links.new(spec.outputs[0], bsdf.inputs["Specular IOR Level"])
        links.new(normal_out, bsdf.inputs["Normal"])
    else:
        node = nodes.new("ShaderNodeGroup"); node.location = (-100, 0)
        node.name = "SMC+.PreviewSurface"  # not NODE_NAME: previews never count as users of a surface
        node.node_tree = surfaces.ensure_surface_group(sid)
        links.new(color_out, node.inputs["Base Color"])
        links.new(zero.outputs[0], node.inputs["Metallic"])
        links.new(rough.outputs[0], node.inputs["Roughness"])
        links.new(spec.outputs[0], node.inputs["Specular"])
        links.new(normal_out, node.inputs["Normal"])
        links.new(tex.get("mask", zero.outputs[0]), node.inputs["Paint Mask"])
        for name, bsdf_in in (("Base Color", "Base Color"), ("Metallic", "Metallic"), ("Roughness", "Roughness"),
                              ("Specular", "Specular IOR Level"), ("Normal", "Normal"),
                              ("Coat Weight", "Coat Weight"), ("Coat Roughness", "Coat Roughness")):
            links.new(node.outputs[name], bsdf.inputs[bsdf_in])
    material["smcadp_preview"] = VERSION


def _stale(mat, sid):
    if mat is None or mat.get("smcadp_preview") != VERSION or mat.node_tree is None:
        return True
    if sid == surfaces.NO_SURFACE:
        return False
    group = bpy.data.node_groups.get(surfaces.GROUP_PREFIX + sid)
    return group is None or not any(n.bl_idname == "ShaderNodeGroup" and n.node_tree == group
                                    for n in mat.node_tree.nodes)


def material(sid):
    """The preview material (created / rebuilt when needed). Not callable from a panel's draw()."""
    name = PREFIX + sid
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name)
        _build(mat, sid)
    elif _stale(mat, sid):
        _build(mat, sid)
    return mat


_pending = False


def _build_all():
    global _pending
    _pending = False
    try:
        for sid in surfaces.surface_ids():
            material(sid)
    except (RuntimeError, AttributeError, ReferenceError):
        return 0.5  # data not writable yet (e.g. while loading a file): try again shortly
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            area.tag_redraw()
    return None


def _schedule():
    global _pending
    if not _pending:
        _pending = True
        bpy.app.timers.register(_build_all, first_interval=0.05)


def icon(sid):
    """Icon id of the thumbnail for a panel (0 while it is being made, or in background mode).
    Panels may not create data, so missing preview materials are made by a timer right after drawing."""
    if bpy.app.background:
        return 0
    mat = bpy.data.materials.get(PREFIX + sid)
    if _stale(mat, sid):
        _schedule()
        return 0
    try:
        preview = mat.preview_ensure()
        return preview.icon_id if preview is not None else 0
    except (RuntimeError, AttributeError):
        return 0


def refresh(sid):
    """Asks Blender to re-render a thumbnail after its values changed."""
    mat = bpy.data.materials.get(PREFIX + sid)
    if mat is not None and mat.node_tree is not None:
        mat.node_tree.update_tag()
        mat.update_tag()


def remove_all():
    for mat in [m for m in bpy.data.materials if m.name.startswith(PREFIX)]:
        bpy.data.materials.remove(mat)
    for image in [i for i in bpy.data.images if i.name.startswith(PREFIX)]:
        bpy.data.images.remove(image)
