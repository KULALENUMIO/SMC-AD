# SPDX-License-Identifier: GPL-3.0-or-later
"""SMC-AD: Scrap Mechanic blueprints in Blender.

Imports the .glb files written by the SMC-AD build of SMConverter
(SMConverterCLI -gltf [-cullhidden] [-optimizeblocks] -septype joints) and:
  - rebuilds the materials as Scrap Mechanic shaders (paint colour from the "Color" attribute,
    ASG specular / gloss / glow, normal maps, cut-out and glass materials)
  - keeps the joint hierarchy: every joint-connected body pivots on its joint, local Z = joint axis

Shading model ported from SM-Converter-Plugin by QuestionableM (MIT License).
"""

import bpy

from . import api, batch, bodies, convert, export, export_fbx, export_ops, importer, mirror, override_ops, overrides, reset, ui  # noqa: F401 (api: for the SMC-AD extension add-ons)

_classes = (
    *convert.classes,
    overrides.SMCAD_OverrideSettings,
    importer.SMCAD_OT_import_glb,
    importer.SMCAD_OT_rebuild_materials,
    override_ops.SMCAD_OT_pick_paint_color,
    override_ops.SMCAD_OT_copy_override,
    override_ops.SMCAD_OT_clear_override,
    override_ops.SMCAD_OT_select_bodies,
    override_ops.SMCAD_OT_select_by_paint,
    override_ops.SMCAD_OT_reset_to_import,
    export_ops.SMCAD_OT_export_unity,
    export_ops.SMCAD_OT_export_unity_fbx,
    bodies.SMCAD_OT_detach_body,
    bodies.SMCAD_OT_delete_body,
    bodies.SMCAD_OT_restore_body,
    *mirror.classes,
    *ui.classes,
)


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)
    bpy.types.Material.smcad_override = bpy.props.PointerProperty(type=overrides.SMCAD_OverrideSettings, name="SMC-AD 色の差し替え")
    bpy.types.Scene.smcad_batch_scope = batch.scope_property()
    # name of the blueprint being converted in the background ("" = idle); not saved in .blend files
    bpy.types.WindowManager.smcad_converting = bpy.props.StringProperty(options={"SKIP_SAVE"})
    bpy.types.TOPBAR_MT_file_import.append(convert.menu_import)
    bpy.types.TOPBAR_MT_file_import.append(importer.menu_import)
    bpy.types.TOPBAR_MT_file_export.append(export_ops.menu_export)


def unregister():
    bpy.types.TOPBAR_MT_file_import.remove(convert.menu_import)
    bpy.types.TOPBAR_MT_file_import.remove(importer.menu_import)
    bpy.types.TOPBAR_MT_file_export.remove(export_ops.menu_export)
    del bpy.types.WindowManager.smcad_converting
    del bpy.types.Scene.smcad_batch_scope
    del bpy.types.Material.smcad_override
    for cls in reversed(_classes):
        bpy.utils.unregister_class(cls)
