# SPDX-License-Identifier: GPL-3.0-or-later
"""Sidebar panels (tab "SMC-AD"), top to bottom in workflow order:
  SMC-AD <version>   bring a blueprint in (convert from the game folder, or an existing .glb)
  ボディの親子        detach / delete connector bodies before rigging
  色の差し替え        (closed) per-material colour override
  一括操作            (closed) the override for many bodies at once, select bodies
  書き出し・やり直し  FBX for Unity, back to the import state, rebuild materials
The sidebar is narrow (about 280 px by default): labels are kept short, details go into tooltips.
"""

import os

import bpy

from . import batch
from .importer import SMCAD_OT_import_glb, SMCAD_OT_rebuild_materials


def _read_version():
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "blender_manifest.toml")
    try:
        import tomllib
        with open(path, "rb") as f:
            return str(tomllib.load(f).get("version", ""))
    except Exception:  # noqa: BLE001  (no manifest / no tomllib: the panel just shows no version)
        return ""


VERSION = _read_version()

SCOPE_LABELS = {"SELECTED": "選択中", "BLUEPRINT": "機体全体", "SCENE": "シーン"}


class _Panel:
    bl_category = "SMC-AD"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"


class SMCAD_PT_main(_Panel, bpy.types.Panel):
    bl_idname = "SMCAD_PT_main"
    bl_order = 0   # first in the tab; SMC-AD Surfaces puts its panel right under it (bl_order 10)
    bl_label = ("SMC-AD  v" + VERSION) if VERSION else "SMC-AD"

    def draw(self, context):
        layout = self.layout
        col = layout.column(align=True)
        converting = getattr(context.window_manager, "smcad_converting", "")
        if converting:
            col.label(text="変換中: %s" % converting, icon="SORTTIME")
            col.label(text="Esc で中止できます")
        else:
            col.operator("smcad.convert_blueprint", text="ブループリントから取り込む…", icon="IMPORT")
        col.operator(SMCAD_OT_import_glb.bl_idname, text=".glb ファイルを取り込む…", icon="FILE_3D")


class SMCAD_PT_bodies(_Panel, bpy.types.Panel):
    bl_idname = "SMCAD_PT_bodies"
    bl_label = "ボディの親子（リギング準備）"
    bl_parent_id = "SMCAD_PT_main"
    bl_order = 1

    @classmethod
    def poll(cls, context):
        return context.active_object is not None and batch.blueprint_root(context.active_object) is not None

    def draw(self, context):
        from . import bodies
        layout = self.layout
        obj = context.active_object
        root = batch.blueprint_root(obj)

        if bodies.is_body(obj):
            col = layout.column(align=True)
            joint = obj.get("sm_joint")
            if obj.get("sm_root"):
                col.label(text="土台（ジョイントの根元）", icon="PINNED")
            elif joint is not None:
                kind = {"bearing": "ベアリング", "piston": "ピストン", "suspension": "サスペンション"}.get(
                    joint.get("type", ""), joint.get("type", "ジョイント"))
                pb = joint.get("parent_body", -1)
                partner = next((o for o in batch.blueprint_objects(root) if o.get("sm_body") == pb), None)
                col.label(text="%s: %s" % (kind, partner.name if partner else "-"), icon="LINKED")
            col.label(text="親: %s" % bodies.body_label(obj.parent), icon="TRIA_UP")
            kids = [c for c in obj.children if not bodies.is_detached(c)]
            # one child per line: blueprint names are long, a joined list gets cut off in the narrow sidebar
            shown = kids if len(kids) <= 3 else kids[:2]
            for i, c in enumerate(shown):
                col.label(text=("子: " if i == 0 else "　　") + c.name, icon="TRIA_DOWN" if i == 0 else "BLANK1")
            if len(kids) > len(shown):
                col.label(text="　　ほか %d 個" % (len(kids) - len(shown)), icon="BLANK1")
            if not kids:
                col.label(text="子: なし", icon="TRIA_DOWN")

            selected = bodies.selected_bodies(context)
            col = layout.column(align=True)
            if len(selected) > 1:
                col.label(text="選んだ %d 個のボディに:" % len(selected))
            row = col.row(align=True)
            row.operator("smcad.detach_body", text="外す", icon="UNLINKED")
            row.operator("smcad.delete_body", text="削除", icon="TRASH")
            col.label(text="子は親につなぎ直されます", icon="INFO")
            col = layout.column(align=True)
            col.operator("smcad.mirror_parts", text="左右反転コピー（先の部品も）", icon="MOD_MIRROR")
        else:
            layout.label(text="ボディを選ぶと親子を変えられます", icon="INFO")

        detached = bodies.detached_in(root)
        if not detached:
            return
        box = layout.box()
        box.label(text="外したボディ: %d 個（書き出しません）" % len(detached), icon="HIDE_ON")
        for body in detached:
            row = box.row(align=True)
            rec = body.get(bodies.RECORD)
            parent = rec.get("parent") if rec else None
            row.label(text="%s → %s" % (body.name, parent.name if parent else "-"))
            row.operator("smcad.restore_body", text="戻す", icon="LOOP_BACK").body = body.name
        if len(detached) > 1:
            box.operator("smcad.restore_body", text="すべて戻す", icon="LOOP_BACK").body = ""


class SMCAD_PT_override(_Panel, bpy.types.Panel):
    bl_idname = "SMCAD_PT_override"
    bl_label = "色の差し替え"
    bl_parent_id = "SMCAD_PT_main"
    bl_options = {"DEFAULT_CLOSED"}
    bl_order = 2

    @classmethod
    def poll(cls, context):
        obj = context.active_object
        return obj is not None and obj.type == "MESH" and len(obj.material_slots) > 0

    def draw(self, context):
        layout = self.layout
        obj = context.active_object
        layout.template_list("MATERIAL_UL_matslots", "", obj, "material_slots", obj, "active_material_index", rows=3)

        mat = obj.active_material
        if mat is None or "sm_shader" not in mat:
            layout.label(text="SMマテリアルではありません", icon="INFO")
            return

        ov = mat.smcad_override
        layout.prop(ov, "enabled")
        col = layout.column()
        col.enabled = ov.enabled
        col.row().prop(ov, "target", expand=True)
        col.row().prop(ov, "source", expand=True)

        if ov.source == "COLOR":
            col.prop(ov, "color")
        else:
            col.template_ID(ov, "image", new="image.new", open="image.open")
            row = col.row()
            row.prop(ov, "projection", expand=True)
            col.prop(ov, "scale")
        col.prop(ov, "strength")

        box = col.box()
        box.prop(ov, "match_enabled")
        sub = box.column()
        sub.enabled = ov.match_enabled
        row = sub.row(align=True)
        row.prop(ov, "match_color", text="")
        row.operator("smcad.pick_paint_color", text=("#" + ov.match_hex) if ov.match_hex else "色を選ぶ…",
                     icon="EYEDROPPER")
        row.operator("smcad.pick_paint_color", text="", icon="WORLD").from_scope = True
        sub.prop(ov, "match_tolerance")

        row = layout.row(align=True)
        op = row.operator("smcad.copy_override", text="選択中にコピー", icon="COPYDOWN")
        op.scope = "SELECTED"
        op = row.operator("smcad.clear_override", text="選択中は解除", icon="X")
        op.scope = "SELECTED"


class SMCAD_PT_override_batch(_Panel, bpy.types.Panel):
    bl_idname = "SMCAD_PT_override_batch"
    bl_label = "色の差し替え：一括操作"
    bl_parent_id = "SMCAD_PT_main"
    bl_options = {"DEFAULT_CLOSED"}
    bl_order = 3

    def draw(self, context):
        layout = self.layout
        scope = context.scene.smcad_batch_scope
        layout.row().prop(context.scene, "smcad_batch_scope", expand=True)

        objects = batch.scope_objects(context, scope)
        mats = batch.scope_materials(objects)
        overridden = sum(1 for m in mats if m.smcad_override.enabled)
        col = layout.column(align=True)
        col.label(text="ボディ %d ／ マテリアル %d" % (len(objects), len(mats)), icon="INFO")
        col.label(text="差し替え中 %d" % overridden, icon="BLANK1")

        # ---- apply
        box = layout.box()
        box.label(text="適用", icon="BRUSHES_ALL")
        obj = context.active_object
        mat = obj.active_material if obj is not None else None
        if batch.is_sm_material(mat):
            ov = mat.smcad_override
            col = box.column(align=True)
            col.label(text="元: %s" % mat.name, icon="MATERIAL")
            if not ov.enabled:
                col.label(text="差し替えオフ（適用＝解除）", icon="ERROR")
            elif ov.match_enabled:
                col.label(text="%s の部分だけ" % (("#" + ov.match_hex) if ov.match_hex else "指定色"), icon="FILTER")
            op = box.operator("smcad.copy_override", text="この差し替えを範囲に適用", icon="COPYDOWN")
            op.scope = scope
        else:
            box.label(text="元にするSMマテリアルを選んでください")
        op = box.operator("smcad.clear_override", text="範囲の差し替えを解除", icon="X")
        op.scope = scope

        # ---- select
        box = layout.box()
        box.label(text="ボディをまとめて選択", icon="RESTRICT_SELECT_OFF")
        grid = box.grid_flow(columns=2, even_columns=True, align=True)
        op = grid.operator("smcad.select_bodies", text="同じマテリアル", icon="MATERIAL")
        op.mode, op.scope = "MATERIAL", scope
        op = grid.operator("smcad.select_by_paint", text="塗装色で…", icon="COLOR")
        op.scope = scope
        op = grid.operator("smcad.select_bodies", text="差し替え済み", icon="BRUSHES_ALL")
        op.mode, op.scope = "OVERRIDDEN", scope
        op = grid.operator("smcad.select_bodies", text="すべて", icon="SELECT_SET")
        op.mode, op.scope = "ALL", scope


class SMCAD_PT_export(_Panel, bpy.types.Panel):
    bl_idname = "SMCAD_PT_export"
    bl_label = "書き出し・やり直し"
    bl_parent_id = "SMCAD_PT_main"
    bl_order = 4

    def draw(self, context):
        layout = self.layout
        layout.operator("smcad.export_unity_fbx", text="Unity 向けに書き出し（FBX）…", icon="EXPORT")
        layout.separator()
        scope = getattr(context.scene, "smcad_batch_scope", "BLUEPRINT")
        col = layout.column(align=True)
        op = col.operator("smcad.reset_to_import", text="取り込み直後に戻す（%s）" % SCOPE_LABELS.get(scope, scope),
                          icon="LOOP_BACK")
        op.scope = scope
        col.operator(SMCAD_OT_rebuild_materials.bl_idname, text="マテリアルを作り直す", icon="MATERIAL")


classes = (SMCAD_PT_main, SMCAD_PT_bodies, SMCAD_PT_override, SMCAD_PT_override_batch, SMCAD_PT_export)
