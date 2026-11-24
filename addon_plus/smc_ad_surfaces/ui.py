# SPDX-License-Identifier: GPL-3.0-or-later
"""Panels for people who never open the shader editor (S5-S11).

  SMC-AD Surfaces（材質）   pick parts by clicking, choose a surface type from thumbnails
    ├ 味付けと調整           flavours, everyday-word sliders, who the sliders affect, reset
    └ 一括・設定             batch over the core scope, import option, preset files
"""

import bpy

from . import core_link, flavors, parts, previews, surfaces, values
from .ops import active_sm_material

def _read_version():
    import os
    try:
        import tomllib
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "blender_manifest.toml"), "rb") as f:
            return str(tomllib.load(f).get("version", ""))
    except Exception:  # noqa: BLE001
        return ""


VERSION = _read_version()

THUMB_SCALE = 3.2
WIDE = 360  # sidebar width (px at UI scale 1) from which technical names and 2-column flavours fit


def _wide(context):
    return context.region is not None and context.region.width >= WIDE * context.preferences.view.ui_scale


def _picked_summary():
    objs = parts.picked_objects()
    n = sum(len(parts.picked(o)) for o in objs)
    if not objs:
        return None
    where = objs[0].name if len(objs) == 1 else "%s ほか%d" % (objs[0].name, len(objs) - 1)
    return "選んだパーツ: %d 個（%s）" % (n, where)


def _bodies_using(material):
    return sum(1 for o in bpy.data.objects if o.type == "MESH" and any(s.material == material for s in o.material_slots))


class SMCADP_PT_surfaces(bpy.types.Panel):
    bl_idname = "SMCADP_PT_surfaces"
    bl_label = "材質（SMC-AD Surfaces）" + ("  v" + VERSION if VERSION else "")
    bl_category = "SMC-AD"   # the same sidebar tab as SMC-AD, right under its panel
    bl_order = 10
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"

    def draw(self, context):
        layout = self.layout
        wm = context.window_manager

        # ---- S5: pick parts by clicking
        picking = wm.smcadp_picking
        row = layout.row()
        row.scale_y = 1.4
        row.operator("smcadp.pick_parts", text="クリックを終える" if picking else "パーツをクリックで選ぶ",
                     icon="RESTRICT_SELECT_OFF", depress=picking)
        if picking:
            layout.label(text="Shift+クリックで追加・解除、Escで終了", icon="INFO")
        summary = _picked_summary()
        if summary:
            box = layout.box()
            row = box.row(align=True)
            row.label(text=summary, icon="RESTRICT_SELECT_OFF")
            row.operator("smcadp.clear_picks", text="", icon="X")
            box.row().prop(wm, "smcadp_assign_to", expand=True)

        mat = active_sm_material(context)
        if mat is None:
            layout.label(text="ボディ（パーツ）を選ぶと、ここに材質が出ます", icon="INFO")
            return

        # ---- the material being edited
        sid = surfaces.surface_of(mat)
        col = layout.column(align=True)
        name = mat.name + ("（パーツ用）" if mat.get("smcadp_base") else "")
        col.label(text=name, icon="MATERIAL")
        if not surfaces.supports(mat):
            col.label(text="ガラス・ライトは材質の対象外です", icon="INFO")
            return
        hint = surfaces.converter_surface(mat)
        if hint and hint != sid:
            col.label(text="おすすめ（変換時の判定）: %s" % surfaces.label_of(hint), icon="LIGHT")

        # ---- S6: thumbnails
        to_parts = summary is not None and wm.smcadp_assign_to == "PARTS"
        grid = layout.grid_flow(row_major=True, columns=3, even_columns=True, even_rows=True, align=False)
        for s in surfaces.surface_ids():
            cell = grid.column(align=True)
            icon = previews.icon(s)
            if icon:
                cell.template_icon(icon_value=icon, scale=THUMB_SCALE)
            group = bpy.data.node_groups.get(surfaces.GROUP_PREFIX + s)
            changed = group is not None and flavors.is_modified(group)
            text = surfaces.label_of(s).split("（")[0] + (" ●" if changed else "")
            op = cell.operator("smcadp.set_surface", text=text, depress=(s == sid and not to_parts))
            op.surface = s
            op.target = "PARTS" if to_parts else "MATERIAL"


class SMCADP_PT_adjust(bpy.types.Panel):
    bl_idname = "SMCADP_PT_adjust"
    bl_label = "味付けと調整"
    bl_category = "SMC-AD"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_parent_id = "SMCADP_PT_surfaces"

    @classmethod
    def poll(cls, context):
        return surfaces.group_of(active_sm_material(context)) is not None

    def draw(self, context):
        layout = self.layout
        wm = context.window_manager
        mat = active_sm_material(context)
        group = surfaces.group_of(mat)
        sid = group.get("smcadp_surface", "")
        label = surfaces.label_of(sid)

        # ---- S10: who these sliders affect
        box = layout.box()
        if surfaces.is_local(group):
            box.label(text="このマテリアル専用（%d ボディ）" % _bodies_using(mat), icon="PINNED")
            box.operator("smcadp.make_shared", icon="LINKED")
        else:
            users = len(surfaces.materials_using(group))
            box.label(text="効く範囲: 「%s」の %d マテリアル" % (label, users), icon="LINKED")
            row = box.row(align=True) if _wide(context) else box.column(align=True)
            row.operator("smcadp.make_local", text="このマテリアルだけ調整する", icon="UNLINKED")
            sub = row.row(align=True)
            sub.enabled = bool(parts.picked_objects())
            sub.operator("smcadp.split_parts", text="選んだパーツだけ調整する", icon="RESTRICT_SELECT_OFF")

        # ---- S8 / S11: flavours
        current = group.get("smcadp_flavor")
        col = layout.column(align=True)
        col.label(text="味付け", icon="SHADERFX")
        grid = col.grid_flow(columns=2 if _wide(context) else 1, even_columns=True, align=True)
        for f in flavors.flavors_for(sid):
            row = grid.row(align=True)
            op = row.operator("smcadp.apply_flavor", text=f["label"], depress=(f["id"] == current))
            op.flavor = f["id"]
            if f["user"]:
                d = row.operator("smcadp.delete_flavor", text="", icon="X")
                d.surface, d.flavor = sid, f["id"]
        col.operator("smcadp.save_flavor", icon="ADD")

        # ---- S7: sliders in everyday words
        vals = group.smcadp_values
        modified = flavors.is_modified(group)
        col = layout.column(align=True)
        col.label(text="調整" + ("（● 変更あり）" if modified else ""), icon="OPTIONS")
        wide = _wide(context)
        for key in values.MAIN:
            _slider(col, vals, key, wide)
        col.prop(wm, "smcadp_show_more", text="もっと細かく", icon="TRIA_DOWN" if wm.smcadp_show_more else "TRIA_RIGHT",
                 emboss=False)
        if wm.smcadp_show_more:
            for key in values.MORE:
                _slider(col, vals, key, wide)

        # ---- S9: reset
        col = layout.column(align=True)
        row = col.row(align=True)
        row.operator("smcadp.reset_surface", text="値だけ既定に戻す", icon="LOOP_BACK").surface = sid
        row.operator("smcadp.clear_surface", text="材質を外す", icon="X").target = "MATERIAL"
        col.operator("smcadp.reset_material", text="このマテリアルを取り込み直後に戻す", icon="FILE_REFRESH")


def _slider(layout, vals, key, wide):
    label, tech, *_rest = values.UI[key]
    if not wide:  # narrow sidebar: the technical name stays in the tooltip
        layout.prop(vals, key, text=label)
        return
    split = layout.split(factor=0.78, align=True)
    split.prop(vals, key, text=label)
    sub = split.row()
    sub.enabled = False
    sub.label(text=tech)


class SMCADP_PT_batch(bpy.types.Panel):
    bl_idname = "SMCADP_PT_batch"
    bl_label = "一括・設定"
    bl_category = "SMC-AD"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_parent_id = "SMCADP_PT_surfaces"
    bl_options = {"DEFAULT_CLOSED"}

    def draw(self, context):
        layout = self.layout
        if core_link.batch() is not None and hasattr(context.scene, "smcad_batch_scope"):
            layout.row().prop(context.scene, "smcad_batch_scope", expand=True)
        col = layout.column(align=True)
        col.operator("smcadp.auto_assign", icon="AUTO")
        row = col.row(align=True)
        row.operator("smcadp.set_surface", text="範囲に割り当て…", icon="BRUSH_DATA").target = "SCOPE"
        row.operator("smcadp.clear_surface", text="範囲から外す", icon="X").target = "SCOPE"
        col.operator("smcadp.reset_all", text="範囲を取り込み直後に戻す", icon="FILE_REFRESH")

        if context.mode == "EDIT_MESH":
            box = layout.box()
            box.label(text="編集モード（面・パーツ単位）", icon="FACESEL")
            row = box.row(align=True)
            row.operator("smcadp.select_parts", icon="SELECT_EXTEND")
            row.operator("smcadp.set_surface", text="選択した面に…", icon="BRUSH_DATA").target = "FACES"

        col = layout.column(align=True)
        col.prop(context.scene, "smcadp_auto_assign")
        row = col.row(align=True)
        row.operator("smcadp.save_presets", icon="EXPORT")
        row.operator("smcadp.load_presets", icon="IMPORT")
        layout.label(text="自分の味付けの保存先: %s" % flavors.USER_FILE_NAME, icon="FILE")


classes = (SMCADP_PT_surfaces, SMCADP_PT_adjust, SMCADP_PT_batch)
