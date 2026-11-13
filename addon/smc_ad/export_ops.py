# SPDX-License-Identifier: GPL-3.0-or-later
import os

import bpy
from bpy.props import BoolProperty, StringProperty
from bpy_extras.io_utils import ExportHelper

from . import batch, export, export_fbx

REPORT_TEXT = "SMC-AD Unity 書き出し結果"


def _blueprint_root(context):
    obj = context.active_object
    root = batch.blueprint_root(obj) if obj is not None else None
    if root is None:
        for o in context.selected_objects:
            root = batch.blueprint_root(o)
            if root is not None:
                break
    return root


class SMCAD_OT_export_unity(bpy.types.Operator, ExportHelper):
    """Unity 向けに書き出します：SMConverter の .glb に、Blender で変えたもの（色の差し替え、材質タイプ、
パーツ用のマテリアル、簡単な手作業のノード）を足します。持っていけないものは一覧にします"""

    bl_idname = "smcad.export_unity"
    bl_label = "Unity 向けに書き出し (.glb)"
    bl_options = {"REGISTER"}

    filename_ext = ".glb"
    filter_glob: StringProperty(default="*.glb", options={"HIDDEN"})
    open_report: BoolProperty(name="書き出し結果を表示", description="持っていけないものの一覧を、書き出した後に表示します", default=True)

    @classmethod
    def poll(cls, context):
        return _blueprint_root(context) is not None

    def invoke(self, context, event):
        root = _blueprint_root(context)
        src = batch.source_of(root)[0] if root else ""
        folder = os.path.dirname(src) if src else ""
        name = os.path.splitext(os.path.basename(src))[0] if src else (root.name if root else "blueprint")
        self.filepath = os.path.join(folder, name + "_smcad.glb")
        return super().invoke(context, event)

    def execute(self, context):
        root = _blueprint_root(context)
        if root is None:
            self.report({"ERROR"}, "SMC-AD で取り込んだ機体を選んでください")
            return {"CANCELLED"}
        if os.path.abspath(self.filepath) == os.path.abspath(batch.source_of(root)[0] or "?"):
            self.report({"ERROR"}, "元の .glb は上書きできません。別の名前にしてください")
            return {"CANCELLED"}
        try:
            report = export.export_blueprint(context, root, self.filepath)
        except (OSError, ValueError, KeyError) as ex:
            self.report({"ERROR"}, str(ex))
            return {"CANCELLED"}

        text = bpy.data.texts.get(REPORT_TEXT) or bpy.data.texts.new(REPORT_TEXT)
        text.clear()
        text.write(export.report_text(report, self.filepath))
        warnings = report.warnings()
        if warnings:
            self.report({"WARNING"}, "書き出しました。Unity に反映されないものが %d 件あります（テキスト「%s」）"
                        % (len(warnings), REPORT_TEXT))
        else:
            self.report({"INFO"}, "書き出しました: %s" % self.filepath)
        if self.open_report and report.items and context.window is not None and not bpy.app.background:
            items = report.items

            def draw(menu, _context):
                layout = menu.layout
                for item in items[:14]:
                    icon = "ERROR" if item["level"] == "warning" else "INFO"
                    layout.label(text="[%s] %s" % (item["material"], item["message"]), icon=icon)
                if len(items) > 14:
                    layout.label(text="ほか %d 件（テキスト「%s」）" % (len(items) - 14, REPORT_TEXT))
            context.window_manager.popup_menu(draw, title="Unity に反映されないもの・近似になるもの", icon="EXPORT")
        return {"FINISHED"}


class SMCAD_OT_export_unity_fbx(bpy.types.Operator, ExportHelper):
    """Unity 向けに FBX で書き出します。Blender での位置・回転・拡大、原点の移動、親子の変更、アーマチュア（骨・ウェイト）を
そのまま書き出し、SM の見た目（マテリアル・テクスチャ・塗装色）とジョイントの情報も一緒に入れます。アニメーションは含めません"""

    bl_idname = "smcad.export_unity_fbx"
    bl_label = "Unity 向けに書き出し (FBX)"
    bl_options = {"REGISTER"}

    filename_ext = ".fbx"
    filter_glob: StringProperty(default="*.fbx", options={"HIDDEN"})
    open_report: BoolProperty(name="書き出し結果を表示", description="持っていけないものの一覧を、書き出した後に表示します", default=True)

    @classmethod
    def poll(cls, context):
        return _blueprint_root(context) is not None

    def invoke(self, context, event):
        root = _blueprint_root(context)
        src = batch.source_of(root)[0] if root else ""
        folder = os.path.dirname(src) if src else ""
        self.filepath = os.path.join(folder, bpy.path.clean_name(batch.blueprint_name(root) or "blueprint") + ".fbx")
        return super().invoke(context, event)

    def execute(self, context):
        root = _blueprint_root(context)
        if root is None:
            self.report({"ERROR"}, "SMC-AD で取り込んだ機体を選んでください")
            return {"CANCELLED"}
        if context.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
        try:
            report, n_objects, n_materials, n_textures = export_fbx.export_fbx(context, root, self.filepath)
        except (OSError, RuntimeError, ValueError) as ex:
            self.report({"ERROR"}, str(ex))
            return {"CANCELLED"}
        text = bpy.data.texts.get(REPORT_TEXT) or bpy.data.texts.new(REPORT_TEXT)
        text.clear()
        text.write(export.report_text(report, self.filepath))
        warnings = report.warnings()
        msg = "書き出しました（オブジェクト %d、マテリアル %d、テクスチャ %d）" % (n_objects, n_materials, n_textures)
        if warnings:
            self.report({"WARNING"}, msg + "。Unity に反映されないものが %d 件あります（テキスト「%s」）" % (len(warnings), REPORT_TEXT))
        else:
            self.report({"INFO"}, msg + ": " + self.filepath)
        if self.open_report and report.items and context.window is not None and not bpy.app.background:
            items = report.items

            def draw(menu, _context):
                for item in items[:14]:
                    menu.layout.label(text="[%s] %s" % (item["material"], item["message"]),
                                      icon="ERROR" if item["level"] == "warning" else "INFO")
            context.window_manager.popup_menu(draw, title="書き出し結果", icon="EXPORT")
        return {"FINISHED"}


def menu_export(self, context):
    self.layout.operator(SMCAD_OT_export_unity_fbx.bl_idname, text="Scrap Mechanic ブループリント・Unity 向け（SMC-AD FBX）")
    self.layout.operator(SMCAD_OT_export_unity.bl_idname, text="Scrap Mechanic ブループリント・見た目だけ（SMC-AD .glb）")
