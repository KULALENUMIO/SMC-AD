# SPDX-License-Identifier: GPL-3.0-or-later
"""SMC-AD Surfaces: surface types (painted metal, bare metal, rubber, plastic, wood, stone) for SMC-AD.

An extension of the SMC-AD core add-on (enable SMC-AD first). It adds one node per material,
"SMC+.Surface", after the core's layer reroutes (frame ④), and a panel to pick and adjust surface
types without opening the shader editor. Without this extension the materials still render: the
node groups stay in the .blend file.
"""

import bpy

from . import core_link, ops, parts, previews, surfaces, ui, values

_classes = ops.classes + ui.classes


# --------------------------------------------------------------------------------------------
# Hooks into the core (smc_ad.api)
# --------------------------------------------------------------------------------------------

def _on_material_built(material):
    surfaces.refresh(material)


def _on_imported(context, objects, materials, filepath):
    auto = getattr(context.scene, "smcadp_auto_assign", True)
    for mat in materials:
        sid = surfaces.converter_surface(mat) if auto else None
        if sid and surfaces.set_surface(mat, sid):
            mat[surfaces.IMPORT_SURFACE] = sid
        else:
            mat[surfaces.IMPORT_SURFACE] = surfaces.NO_SURFACE


def _before_reset(context, objects, materials):
    for obj in objects:
        parts.set_picked(obj, None)
    copies = [s.material for o in objects for s in o.material_slots
              if s.material is not None and s.material.get("smcadp_base")]
    surfaces.before_reset(list(materials) + copies)


def _after_reset(context, objects, materials):
    core = core_link.find()
    scope = getattr(getattr(core, "reset", None), "CURRENT_SCOPE", None) or "BLUEPRINT"
    surfaces.after_reset(materials, getattr(context.scene, "smcadp_auto_assign", True), scope)
    for sid in surfaces.surface_ids():
        previews.refresh(sid)


def _draw_import_options(layout, context):
    layout.prop(context.scene, "smcadp_auto_assign")


_HOOKS = (("material_built", _on_material_built), ("imported", _on_imported), ("import_options", _draw_import_options),
          ("original_material", surfaces.original_material), ("before_reset", _before_reset),
          ("after_reset", _after_reset), ("export_material", surfaces.export_material))


def register():
    core = core_link.find()
    if core is None:
        raise RuntimeError("SMC-AD Surfaces には SMC-AD アドオンが必要です。先に SMC-AD を有効にしてください")

    bpy.types.Scene.smcadp_auto_assign = bpy.props.BoolProperty(
        name="取り込み時に材質を自動で割り当て",
        description="SMConverterの判定（物理マテリアル・金属シェーダ）から、取り込み時に材質を割り当てます",
        default=True)
    wm = bpy.types.WindowManager
    wm.smcadp_picking = bpy.props.BoolProperty(name="パーツを選んでいるところ", default=False, options={"SKIP_SAVE"})
    wm.smcadp_show_more = bpy.props.BoolProperty(name="もっと細かく", default=False)
    wm.smcadp_assign_to = bpy.props.EnumProperty(
        name="割り当て先",
        items=[("PARTS", "選んだパーツだけ", "サムネイルを押すと、選んだパーツだけに割り当てます"),
               ("MATERIAL", "マテリアル全体", "サムネイルを押すと、マテリアル全体（それを使う全パーツ）に割り当てます")],
        default="PARTS")
    values.register()
    for cls in _classes:
        bpy.utils.register_class(cls)
    parts.register()
    if getattr(core.api, "API_VERSION", 1) < 3:
        raise RuntimeError("SMC-AD Surfaces には SMC-AD 0.5.0 以降が必要です。先に SMC-AD を更新してください")
    for hook_list, fn in _HOOKS:
        hooks = getattr(core.api, hook_list)
        if fn not in hooks:
            hooks.append(fn)


def unregister():
    core = core_link.find()
    if core is not None:
        for hook_list, fn in _HOOKS:
            hooks = getattr(core.api, hook_list)
            if fn in hooks:
                hooks.remove(fn)
    parts.unregister()
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
    values.unregister()
    previews.remove_all()
    wm = bpy.types.WindowManager
    del wm.smcadp_assign_to, wm.smcadp_show_more, wm.smcadp_picking
    del bpy.types.Scene.smcadp_auto_assign
