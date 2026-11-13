# SPDX-License-Identifier: GPL-3.0-or-later
import bpy
import numpy as np
from bpy.props import BoolProperty, EnumProperty

from . import batch, overrides

# EnumProperty item strings must stay referenced while the menu is open
_paint_items = []
_select_items = []


def _active_sm_material(context):
    obj = context.active_object
    if obj is None or obj.active_material is None:
        return None
    mat = obj.active_material
    return mat if batch.is_sm_material(mat) else None


def _color_items(colors, limit=48):
    items = [(hx, "#" + hx, "#%s で塗られた頂点: %d" % (hx, n)) for hx, n in colors[:limit]]
    return items or [("NONE", "塗装色がありません", "")]


def _hex_to_linear_array(hexes):
    return np.array([overrides.srgb_hex_to_linear(hx) for hx in hexes]).reshape(-1, 3)


def _uses_match_color(ov, colors):
    """True when one of the paint colours (hex -> count) would be selected by the override's match colour."""
    if not colors:
        return False
    lin = _hex_to_linear_array(list(colors))
    dist = np.linalg.norm(lin - np.array(ov.match_color), axis=1)
    return bool((dist < ov.match_tolerance).any())


# --------------------------------------------------------------------------------------------
# Paint colour to replace
# --------------------------------------------------------------------------------------------

def _paint_color_items(self, context):
    global _paint_items
    if self.from_scope:
        colors = batch.paint_totals(batch.scope_objects(context, context.scene.smcad_batch_scope))
    else:
        mat = _active_sm_material(context)
        colors = overrides.paint_colors(mat) if mat is not None else []
    _paint_items = _color_items(colors)
    return _paint_items


class SMCAD_OT_pick_paint_color(bpy.types.Operator):
    """このマテリアルで使われている塗装色から、差し替える色を選びます"""

    bl_idname = "smcad.pick_paint_color"
    bl_label = "塗装色"
    bl_property = "color"
    bl_options = {"REGISTER", "UNDO"}

    color: EnumProperty(name="塗装色", items=_paint_color_items)
    from_scope: BoolProperty(
        name="一括操作の範囲から選ぶ",
        description="このマテリアルだけでなく、一括操作の範囲（機体全体など）で使われている塗装色から選びます",
        default=False, options={"SKIP_SAVE"})

    @classmethod
    def description(cls, context, properties):
        if properties.from_scope:
            return "一括操作の範囲（機体全体など）で使われている塗装色から、差し替える色を選びます"
        return cls.__doc__

    @classmethod
    def poll(cls, context):
        return _active_sm_material(context) is not None

    def execute(self, context):
        if self.color == "NONE":
            return {"CANCELLED"}
        ov = _active_sm_material(context).smcad_override
        with overrides.suspend_updates():
            ov.match_hex = self.color
            ov.match_color = overrides.srgb_hex_to_linear(self.color)
            ov.match_enabled = True
        overrides.apply_override(ov.id_data)
        return {"FINISHED"}

    def invoke(self, context, event):
        context.window_manager.invoke_search_popup(self)
        return {"RUNNING_MODAL"}


# --------------------------------------------------------------------------------------------
# Apply / clear over a scope
# --------------------------------------------------------------------------------------------

class SMCAD_OT_copy_override(bpy.types.Operator):
    """アクティブなマテリアルの色の差し替えを、範囲内のすべてのSMマテリアルにコピーします。
マテリアルはボディ同士で共有されているため、範囲外でも同じマテリアルを使うボディは変わります"""

    bl_idname = "smcad.copy_override"
    bl_label = "範囲に適用"
    bl_options = {"REGISTER", "UNDO"}

    scope: batch.scope_property("SELECTED")
    only_matching_paint: BoolProperty(
        name="その塗装色を使うマテリアルだけ",
        description="「1つの塗装色だけ」がオンのとき、その色が一度も塗られていないマテリアルは飛ばします",
        default=True)

    @classmethod
    def poll(cls, context):
        return _active_sm_material(context) is not None

    def execute(self, context):
        src = _active_sm_material(context)
        ov = src.smcad_override
        objects = batch.scope_objects(context, self.scope)
        targets = batch.scope_materials(objects)

        filter_paint = self.only_matching_paint and ov.enabled and ov.match_enabled
        usage = batch.paint_by_material(objects) if filter_paint else {}

        applied = skipped = failed = 0
        for mat in targets:
            if mat == src:
                continue
            if filter_paint and not _uses_match_color(ov, usage.get(mat)):
                skipped += 1
                continue
            with overrides.suspend_updates():
                overrides.copy_settings(ov, mat.smcad_override)
            if overrides.apply_override(mat):
                applied += 1
            else:
                failed += 1

        msg = "%d 個のマテリアルに適用しました" % applied
        if skipped:
            msg += "（%d 個はその塗装色を使っていないので飛ばしました）" % skipped
        if failed:
            msg += "（%d 個はSMのノードがありませんでした）" % failed
        self.report({"INFO"}, msg)
        return {"FINISHED"}


class SMCAD_OT_clear_override(bpy.types.Operator):
    """範囲内のすべてのSMマテリアルから、色の差し替えを解除します"""

    bl_idname = "smcad.clear_override"
    bl_label = "範囲で解除"
    bl_options = {"REGISTER", "UNDO"}

    scope: batch.scope_property("SELECTED")

    def execute(self, context):
        cleared = 0
        for mat in batch.scope_materials(batch.scope_objects(context, self.scope)):
            if not mat.smcad_override.enabled:
                continue
            with overrides.suspend_updates():
                mat.smcad_override.enabled = False
            overrides.apply_override(mat)
            cleared += 1
        self.report({"INFO"}, "%d 個のマテリアルで差し替えを解除しました" % cleared)
        return {"FINISHED"}


# --------------------------------------------------------------------------------------------
# Batch selection
# --------------------------------------------------------------------------------------------

def _select(context, objects, extend):
    """Selects the objects (only those that can be selected). Returns how many were selected."""
    pickable = [o for o in objects if o.visible_get() and not o.hide_select]
    if not extend:
        for obj in context.selected_objects:
            obj.select_set(False)
    for obj in pickable:
        obj.select_set(True)
    active = context.view_layer.objects.active
    if pickable and (active is None or not active.select_get()):
        context.view_layer.objects.active = pickable[0]
    return len(pickable)


class SMCAD_OT_select_bodies(bpy.types.Operator):
    """範囲内のボディをまとめて選択します"""

    bl_idname = "smcad.select_bodies"
    bl_label = "ボディをまとめて選択"
    bl_options = {"REGISTER", "UNDO"}

    mode: EnumProperty(
        name="選ぶもの",
        items=[
            ("MATERIAL", "同じマテリアル", "アクティブなマテリアルを使っているボディ"),
            ("OVERRIDDEN", "差し替え済み", "色の差し替えをしたマテリアルを持つボディ"),
            ("ALL", "すべてのボディ", "範囲内のすべてのボディ"),
        ],
        default="MATERIAL")
    scope: batch.scope_property()
    extend: BoolProperty(name="選択に追加", description="今の選択を残したまま追加します", default=False)

    @classmethod
    def description(cls, context, properties):
        return {
            "MATERIAL": "範囲内で、アクティブなマテリアルを使っているボディをすべて選択します",
            "OVERRIDDEN": "範囲内で、色の差し替えをしたボディをすべて選択します",
            "ALL": "範囲内のボディをすべて選択します",
        }[properties.mode]

    def execute(self, context):
        objects = batch.scope_objects(context, self.scope)
        if self.mode == "MATERIAL":
            mat = _active_sm_material(context)
            if mat is None:
                self.report({"WARNING"}, "アクティブなオブジェクトにSMマテリアルがありません")
                return {"CANCELLED"}
            picked = [o for o in objects if any(s.material == mat for s in o.material_slots)]
        elif self.mode == "OVERRIDDEN":
            picked = [o for o in objects if any(batch.is_sm_material(s.material) and s.material.smcad_override.enabled
                                                for s in o.material_slots)]
        else:
            picked = objects

        n = _select(context, picked, self.extend)
        self.report({"INFO"}, "%d 個のボディを選択しました" % n)
        return {"FINISHED"}


def _select_color_items(self, context):
    global _select_items
    _select_items = _color_items(batch.paint_totals(batch.scope_objects(context, self.scope)))
    return _select_items


class SMCAD_OT_select_by_paint(bpy.types.Operator):
    """範囲内で、選んだ塗装色で塗られているボディをすべて選択します"""

    bl_idname = "smcad.select_by_paint"
    bl_label = "塗装色で選択"
    bl_property = "color"
    bl_options = {"REGISTER", "UNDO"}

    color: EnumProperty(name="塗装色", items=_select_color_items)
    scope: batch.scope_property()
    extend: BoolProperty(name="選択に追加", description="今の選択を残したまま追加します", default=False)

    def execute(self, context):
        if self.color == "NONE":
            return {"CANCELLED"}
        objects = batch.scope_objects(context, self.scope)
        picked = [o for o in objects if batch.object_has_paint(o, self.color)]
        n = _select(context, picked, self.extend)
        self.report({"INFO"}, "#%s で塗られたボディを %d 個選択しました" % (self.color, n))
        return {"FINISHED"}

    def invoke(self, context, event):
        context.window_manager.invoke_search_popup(self)
        return {"RUNNING_MODAL"}


# --------------------------------------------------------------------------------------------
# Back to the import state
# --------------------------------------------------------------------------------------------

class SMCAD_OT_reset_to_import(bpy.types.Operator):
    """インポートした直後の状態に戻します：マテリアルを元の .glb から作り直し、テクスチャとスロットを元に戻し、
色の差し替えを解除し、パーツ用の複製を削除します。拡張機能の設定もインポート時に戻ります。ジョイントの角度はそのままです"""

    bl_idname = "smcad.reset_to_import"
    bl_label = "インポート直後に戻す"
    bl_options = {"REGISTER", "UNDO"}

    scope: EnumProperty(name="範囲", items=batch.SCOPE_ITEMS + [
        ("MATERIAL", "マテリアル", "アクティブなマテリアルと、そこから作った複製")], default="BLUEPRINT")
    keep_user_nodes: BoolProperty(name="自分で足したノードは残す", default=False,
                                  description="シェーダーエディタで自分で足したノードを消さずに残します")

    def execute(self, context):
        from . import reset
        if self.scope == "MATERIAL":
            mat = _active_sm_material(context)
            if mat is None:
                self.report({"WARNING"}, "アクティブなオブジェクトにSMマテリアルがありません")
                return {"CANCELLED"}
            summary = reset.reset_material(context, mat, self.keep_user_nodes)
        else:
            summary = reset.reset_to_import(context, batch.scope_objects(context, self.scope), self.keep_user_nodes,
                                            self.scope)
        if summary is None:
            return {"CANCELLED"}
        if summary["missing_sources"]:
            self.report({"WARNING"}, "元の .glb が見つかりません: %s" % ", ".join(summary["missing_sources"]))
        self.report({"INFO"}, "インポート直後に戻しました（マテリアル %d、複製の削除 %d）"
                    % (summary["materials"], summary["copies_removed"]))
        return {"FINISHED"}

    def invoke(self, context, event):
        return context.window_manager.invoke_confirm(self, event)
