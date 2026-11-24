# SPDX-License-Identifier: GPL-3.0-or-later
import json

import bpy
from bpy.props import BoolProperty, EnumProperty, StringProperty

from . import core_link, flavors, parts, previews, surfaces

_surface_items_ref = []

TARGET_ITEMS = [
    ("MATERIAL", "このマテリアル", "アクティブなマテリアル（それを使う全パーツ）"),
    ("SCOPE", "範囲内すべて", "SMC-AD の一括操作の範囲（選択中／機体全体／シーン）にあるすべてのSMマテリアル"),
    ("FACES", "選択した面", "編集モードで選んだ面だけ（マテリアルの複製に移します）"),
    ("PARTS", "選んだパーツ", "クリックで選んだパーツだけ（マテリアルの複製に移します）"),
]


def _surface_items(self, context):
    global _surface_items_ref
    _surface_items_ref = [(sid, surfaces.label_of(sid), "") for sid in surfaces.surface_ids()]
    return _surface_items_ref


def active_sm_material(context):
    obj = context.active_object
    mat = obj.active_material if obj is not None else None
    return mat if mat is not None and "sm_shader" in mat else None


def _active_group(context):
    return surfaces.group_of(active_sm_material(context))


def _scope_materials(context, scope=None):
    batch = core_link.batch()
    if batch is None:
        return []
    scope = scope or context.scene.smcad_batch_scope
    return batch.scope_materials(batch.scope_objects(context, scope))


def _redraw(context):
    for area in context.screen.areas if context.screen else []:
        area.tag_redraw()


def _show_picked_material(context):
    """After faces moved: show the material of the picked parts on the active object."""
    obj = context.active_object
    if obj is None or not parts.picked(obj):
        return
    mask = parts.poly_mask(obj)
    if mask.any():
        first = int(mask.nonzero()[0][0])
        obj.active_material_index = obj.data.polygons[first].material_index


# --------------------------------------------------------------------------------------------
# Assigning surface types
# --------------------------------------------------------------------------------------------

class SMCADP_OT_set_surface(bpy.types.Operator):
    """パーツに材質（金属・ゴムなど）を割り当てます。「標準」を選ぶと外れます"""

    bl_idname = "smcadp.set_surface"
    bl_label = "材質を割り当て"
    bl_property = "surface"
    bl_options = {"REGISTER", "UNDO"}

    surface: EnumProperty(name="材質", items=_surface_items)
    target: EnumProperty(name="対象", items=TARGET_ITEMS, default="MATERIAL")

    @classmethod
    def description(cls, context, properties):
        return {
            "MATERIAL": "アクティブなマテリアル（それを使う全パーツ）に材質を割り当てます",
            "SCOPE": "範囲（SMC-ADの一括操作の範囲）にあるすべてのマテリアルに材質を割り当てます",
            "FACES": "編集モードで選んだ面にだけ材質を割り当てます（マテリアルの複製に移します）",
            "PARTS": "クリックで選んだパーツにだけ材質を割り当てます（マテリアルの複製に移します）",
        }[properties.target]

    def execute(self, context):
        if self.target == "FACES":
            if context.mode != "EDIT_MESH":
                self.report({"WARNING"}, "編集モードで面を選んでください")
                return {"CANCELLED"}
            n = sum(surfaces.assign_faces(o, self.surface) for o in context.objects_in_mode if o.type == "MESH")
            self.report({"INFO"}, "%s: %d 面に割り当てました" % (surfaces.label_of(self.surface), n))
            return {"FINISHED"}

        if self.target == "PARTS":
            objs = parts.picked_objects()
            if not objs:
                self.report({"WARNING"}, "パーツが選ばれていません")
                return {"CANCELLED"}
            sid = self.surface

            def target_for(mat):
                return surfaces.variant_for(mat, sid) if surfaces.supports(mat) else None
            n = sum(surfaces.move_polys(o, parts.poly_mask(o), target_for) for o in objs)
            _show_picked_material(context)
            self.report({"INFO"}, "%s: %d 面に割り当てました" % (surfaces.label_of(sid), n))
            return {"FINISHED"}

        if self.target == "MATERIAL":
            mats = [active_sm_material(context)] if active_sm_material(context) else []
        else:
            mats = _scope_materials(context)
        done = sum(1 for m in mats if surfaces.set_surface(m, self.surface))
        skipped = len(mats) - done
        msg = "%s: %d 個のマテリアルに割り当てました" % (surfaces.label_of(self.surface), done)
        if skipped:
            msg += "（ガラス・ライトの %d 個は対象外）" % skipped
        self.report({"INFO"}, msg)
        return {"FINISHED"}

    def invoke(self, context, event):
        if self.properties.is_property_set("surface"):
            return self.execute(context)  # thumbnail buttons pass the surface directly
        context.window_manager.invoke_search_popup(self)
        return {"RUNNING_MODAL"}


class SMCADP_OT_auto_assign(bpy.types.Operator):
    """SMConverter がゲームのデータ（物理マテリアル・金属のシェーダー）から判定した材質を割り当てます"""

    bl_idname = "smcadp.auto_assign"
    bl_label = "自動で割り当て"
    bl_options = {"REGISTER", "UNDO"}

    keep_existing: BoolProperty(name="手動で付けた材質は残す", description="すでに材質が付いているマテリアルは変えません", default=True)

    def execute(self, context):
        done = 0
        for mat in _scope_materials(context):
            if self.keep_existing and surfaces.surface_of(mat) != surfaces.NO_SURFACE:
                continue
            sid = surfaces.converter_surface(mat)
            if sid and surfaces.set_surface(mat, sid):
                done += 1
        self.report({"INFO"}, "%d 個のマテリアルに自動で割り当てました" % done)
        return {"FINISHED"}


class SMCADP_OT_clear_surface(bpy.types.Operator):
    """材質を外して、Scrap Mechanic そのままの見た目に戻します"""

    bl_idname = "smcadp.clear_surface"
    bl_label = "材質を外す"
    bl_options = {"REGISTER", "UNDO"}

    target: EnumProperty(name="対象", items=TARGET_ITEMS[:2], default="MATERIAL")

    def execute(self, context):
        if self.target == "MATERIAL":
            mats = [active_sm_material(context)] if active_sm_material(context) else []
        else:
            mats = _scope_materials(context)
        done = sum(1 for m in mats if surfaces.remove_surface(m))
        self.report({"INFO"}, "%d 個のマテリアルから材質を外しました" % done)
        return {"FINISHED"}


class SMCADP_OT_select_parts(bpy.types.Operator):
    """選んでいる面を、そのパーツ全体まで広げます"""

    bl_idname = "smcadp.select_parts"
    bl_label = "パーツ全体を選択"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return context.mode == "EDIT_MESH"

    def execute(self, context):
        n = sum(surfaces.select_parts(o) for o in context.objects_in_mode if o.type == "MESH")
        self.report({"INFO"}, "%d 面を選択しました" % n)
        return {"FINISHED"}


# --------------------------------------------------------------------------------------------
# Picking parts by clicking (S5)
# --------------------------------------------------------------------------------------------

def _inside(region, event):
    return region.x <= event.mouse_x < region.x + region.width and region.y <= event.mouse_y < region.y + region.height


class SMCADP_OT_pick_parts(bpy.types.Operator):
    """ビューポートでパーツをクリックして選びます（Shift で追加・解除）。Esc か右クリックで終わります"""

    bl_idname = "smcadp.pick_parts"
    bl_label = "パーツをクリックで選ぶ"
    bl_options = {"REGISTER", "UNDO"}

    HELP = "パーツ選択: クリック＝選ぶ ／ Shift+クリック＝追加・解除 ／ 何もない所＝解除 ／ Esc・右クリック＝終了"

    @classmethod
    def poll(cls, context):
        return context.area is not None and context.area.type == "VIEW_3D"

    def _finish(self, context):
        context.window_manager.smcadp_picking = False
        if context.area is not None:
            context.area.header_text_set(None)
        context.workspace.status_text_set(None)
        _redraw(context)

    def modal(self, context, event):
        wm = context.window_manager
        if context.area is None:
            wm.smcadp_picking = False
            return {"CANCELLED"}
        if not wm.smcadp_picking:  # stopped from the panel button
            self._finish(context)
            return {"FINISHED"}
        if event.type in {"ESC", "RIGHTMOUSE"} and event.value == "PRESS":
            self._finish(context)
            return {"FINISHED"}
        if event.type == "LEFTMOUSE" and event.value == "PRESS":
            if any(r.type != "WINDOW" and _inside(r, event) for r in context.area.regions):
                return {"PASS_THROUGH"}  # sidebar, header, toolbar keep working
            region = next((r for r in context.area.regions if r.type == "WINDOW" and _inside(r, event)), None)
            if region is None:
                return {"PASS_THROUGH"}
            coord = (event.mouse_x - region.x, event.mouse_y - region.y)
            hit = parts.view_pick(context, region, region.data, coord)
            if hit is None:
                if not event.shift:
                    parts.clear_all()
            else:
                parts.apply_pick(context, hit[0], hit[1], extend=event.shift)
            _redraw(context)
            return {"RUNNING_MODAL"}
        return {"PASS_THROUGH"}

    def invoke(self, context, event):
        wm = context.window_manager
        if wm.smcadp_picking:
            wm.smcadp_picking = False  # pressing the button again stops picking
            return {"FINISHED"}
        if context.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
        wm.smcadp_picking = True
        context.area.header_text_set(self.HELP)
        context.workspace.status_text_set(self.HELP)
        wm.modal_handler_add(self)
        return {"RUNNING_MODAL"}


class SMCADP_OT_clear_picks(bpy.types.Operator):
    """選んだパーツの選択を解除します"""

    bl_idname = "smcadp.clear_picks"
    bl_label = "選択を解除"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        parts.clear_all()
        _redraw(context)
        return {"FINISHED"}


class SMCADP_OT_split_parts(bpy.types.Operator):
    """選んだパーツに専用のマテリアルを作ります。調整しても他のパーツは変わりません"""

    bl_idname = "smcadp.split_parts"
    bl_label = "選んだパーツ用に複製"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return bool(parts.picked_objects())

    def execute(self, context):
        target_for, made = surfaces.part_copy_factory()
        n = sum(surfaces.move_polys(o, parts.poly_mask(o), target_for) for o in parts.picked_objects())
        _show_picked_material(context)
        self.report({"INFO"}, "%d 面を、新しいマテリアル %d 個に移しました" % (n, len(made)))
        return {"FINISHED"}


# --------------------------------------------------------------------------------------------
# Who the sliders affect (S10)
# --------------------------------------------------------------------------------------------

class SMCADP_OT_make_local(bpy.types.Operator):
    """このマテリアルだけを調整できるように、材質の値を専用にします"""

    bl_idname = "smcadp.make_local"
    bl_label = "このマテリアルだけ調整する"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        mat = active_sm_material(context)
        if mat is None or surfaces.make_local(mat) is None:
            return {"CANCELLED"}
        return {"FINISHED"}


class SMCADP_OT_make_shared(bpy.types.Operator):
    """材質タイプの共有の値に戻します（このマテリアル専用の調整は捨てます）"""

    bl_idname = "smcadp.make_shared"
    bl_label = "共有の設定に戻す"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        mat = active_sm_material(context)
        if mat is None or surfaces.make_shared(mat) is None:
            return {"CANCELLED"}
        return {"FINISHED"}


# --------------------------------------------------------------------------------------------
# Flavours (S8, S11)
# --------------------------------------------------------------------------------------------

class SMCADP_OT_apply_flavor(bpy.types.Operator):
    """この味付けに切り替えます"""

    bl_idname = "smcadp.apply_flavor"
    bl_label = "味付けを適用"
    bl_options = {"REGISTER", "UNDO"}

    flavor: StringProperty()

    def execute(self, context):
        group = _active_group(context)
        if group is None:
            return {"CANCELLED"}
        sid = group.get("smcadp_surface", "")
        flavor = flavors.find(sid, self.flavor) if self.flavor else None
        if self.flavor and flavor is None:
            return {"CANCELLED"}
        flavors.apply(group, flavor)
        previews.refresh(sid)
        return {"FINISHED"}


class SMCADP_OT_save_flavor(bpy.types.Operator):
    """今の値を、自分の味付けとして名前を付けて保存します（どの .blend でも使えます）"""

    bl_idname = "smcadp.save_flavor"
    bl_label = "今の調整を名前を付けて保存"
    bl_options = {"REGISTER", "UNDO"}

    label: StringProperty(name="名前", default="わたしの味付け")

    def execute(self, context):
        group = _active_group(context)
        if group is None or not self.label.strip():
            return {"CANCELLED"}
        flavors.save_user(group.get("smcadp_surface", ""), self.label.strip(), group)
        self.report({"INFO"}, "保存しました: %s" % self.label)
        return {"FINISHED"}

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)


class SMCADP_OT_delete_flavor(bpy.types.Operator):
    """自分で保存した味付けを削除します"""

    bl_idname = "smcadp.delete_flavor"
    bl_label = "自分の味付けを削除"

    surface: StringProperty()
    flavor: StringProperty()

    def execute(self, context):
        flavors.delete_user(self.surface, self.flavor)
        return {"FINISHED"}

    def invoke(self, context, event):
        return context.window_manager.invoke_confirm(self, event)


# --------------------------------------------------------------------------------------------
# Resetting (S9)
# --------------------------------------------------------------------------------------------

class SMCADP_OT_reset_surface(bpy.types.Operator):
    """スライダーの値だけを、材質タイプの既定値に戻します（材質は付いたまま）"""

    bl_idname = "smcadp.reset_surface"
    bl_label = "値を既定に戻す"
    bl_options = {"REGISTER", "UNDO"}

    surface: StringProperty()

    def execute(self, context):
        if self.surface not in surfaces.default_presets():
            return {"CANCELLED"}
        group = _active_group(context)
        if group is None or group.get("smcadp_surface") != self.surface:
            group = surfaces.ensure_surface_group(self.surface)
        flavors.apply(group, None)
        previews.refresh(self.surface)
        return {"FINISHED"}


class SMCADP_OT_reset_material(bpy.types.Operator):
    """このマテリアルを、取り込んだ直後に戻します（テクスチャ・ノード・材質・色の差し替え。パーツ用の複製も元に戻します）"""

    bl_idname = "smcadp.reset_material"
    bl_label = "このマテリアルを取り込み直後に戻す"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        return bpy.ops.smcad.reset_to_import("EXEC_DEFAULT", scope="MATERIAL")

    def invoke(self, context, event):
        return context.window_manager.invoke_confirm(self, event)


class SMCADP_OT_reset_all(bpy.types.Operator):
    """範囲内のすべてを、取り込んだ直後に戻します（SMC-AD の機能を使います）"""

    bl_idname = "smcadp.reset_all"
    bl_label = "範囲を取り込み直後に戻す（材質も）"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        return bpy.ops.smcad.reset_to_import("EXEC_DEFAULT", scope=context.scene.smcad_batch_scope)

    def invoke(self, context, event):
        return context.window_manager.invoke_confirm(self, event)


# --------------------------------------------------------------------------------------------
# Preset files
# --------------------------------------------------------------------------------------------

class SMCADP_OT_save_presets(bpy.types.Operator):
    """このファイルの材質タイプを、プリセットの JSON に保存します（SMConverter・Unity と同じ形式）"""

    bl_idname = "smcadp.save_presets"
    bl_label = "プリセットを保存"
    filepath: StringProperty(subtype="FILE_PATH")
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    def execute(self, context):
        presets = {g["smcadp_surface"]: surfaces.group_values(g) for g in bpy.data.node_groups
                   if g.name.startswith(surfaces.GROUP_PREFIX) and g.get("smcadp_surface")
                   and not surfaces.is_local(g)}
        with open(self.filepath, "w", encoding="utf-8") as f:
            json.dump({"version": 1, "surfaces": presets}, f, ensure_ascii=False, indent=1)
        self.report({"INFO"}, "%d 種類の材質を保存しました" % len(presets))
        return {"FINISHED"}

    def invoke(self, context, event):
        if not self.filepath:
            self.filepath = "SurfacePresets.json"
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}


class SMCADP_OT_load_presets(bpy.types.Operator):
    """プリセットの JSON から材質タイプを読み込み、このファイルの材質タイプを更新します"""

    bl_idname = "smcadp.load_presets"
    bl_label = "プリセットを読み込み"
    bl_options = {"REGISTER", "UNDO"}
    filepath: StringProperty(subtype="FILE_PATH")
    filter_glob: StringProperty(default="*.json", options={"HIDDEN"})

    def execute(self, context):
        with open(self.filepath, encoding="utf-8") as f:
            data = json.load(f)
        surfaces.apply_presets(data.get("surfaces", data), overwrite=True)
        self.report({"INFO"}, "材質のプリセットを読み込みました")
        return {"FINISHED"}

    def invoke(self, context, event):
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}


classes = (
    SMCADP_OT_set_surface,
    SMCADP_OT_auto_assign,
    SMCADP_OT_clear_surface,
    SMCADP_OT_select_parts,
    SMCADP_OT_pick_parts,
    SMCADP_OT_clear_picks,
    SMCADP_OT_split_parts,
    SMCADP_OT_make_local,
    SMCADP_OT_make_shared,
    SMCADP_OT_apply_flavor,
    SMCADP_OT_save_flavor,
    SMCADP_OT_delete_flavor,
    SMCADP_OT_reset_surface,
    SMCADP_OT_reset_material,
    SMCADP_OT_reset_all,
    SMCADP_OT_save_presets,
    SMCADP_OT_load_presets,
)
