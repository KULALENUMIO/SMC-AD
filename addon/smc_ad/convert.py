# SPDX-License-Identifier: GPL-3.0-or-later
"""Convert a Scrap Mechanic blueprint from inside Blender: pick it by name, run the SMC-AD build of
SMConverter (SMConverterCLI.exe) in the background, then import the .glb.

Settings (add-on preferences): where SMConverterCLI.exe is, the blueprint folder(s), conversion options.
Both paths are found automatically when left empty:
  converter   SMCAD_CONVERTER environment variable, or SMConverter/Build/SMConverterCLI-Release next to the
              add-on source (repository layout)
  blueprints  %APPDATA%/Axolot Games/Scrap Mechanic/User/User_*/Blueprints
"""

import glob
import json
import os
import re
import subprocess
import time

import bpy
from bpy.props import BoolProperty, EnumProperty, StringProperty

CONVERTER_EXE = "SMConverterCLI.exe"


# --------------------------------------------------------------------------------------------
# Preferences
# --------------------------------------------------------------------------------------------

def prefs():
    addon = bpy.context.preferences.addons.get(__package__)
    return addon.preferences if addon is not None else None


class SMCAD_Preferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    converter_path: StringProperty(
        name="変換ツール（SMConverterCLI.exe）", subtype="FILE_PATH",
        description="SMC-AD 版の SMConverterCLI.exe。空のときは自動で探します")
    blueprint_folder: StringProperty(
        name="ブループリントのフォルダ", subtype="DIR_PATH",
        description="Scrap Mechanic のブループリントのフォルダ。空のときは自動で探します（User_*/Blueprints）")
    cull_hidden: BoolProperty(name="見えない面を削除", default=True,
                              description="外から見えない内側の面を消して軽くします（-cullhidden）")
    optimize_blocks: BoolProperty(name="ブロックをまとめる", default=True,
                                  description="同じ種類のブロックの面をまとめて軽くします（-optimizeblocks）")
    fix_coplanar: BoolProperty(name="重なった面のちらつきを直す", default=True,
                               description="同じ位置に重なった面を少しずらします（-fixcoplanar）")
    max_texture_compression: BoolProperty(name="テクスチャを最大圧縮", default=False,
                                          description="ファイルは少し小さくなりますが、変換に数倍かかります（-maxtexcompression）")

    def draw(self, context):
        layout = self.layout
        col = layout.column()
        col.prop(self, "converter_path")
        exe = find_converter()
        col.label(text=("見つかった: " + exe) if exe else "変換ツールが見つかりません", icon="CHECKMARK" if exe else "ERROR")
        col.prop(self, "blueprint_folder")
        folders = blueprint_folders()
        col.label(text=("ブループリント: %d 個（%s）" % (len(list_blueprints()), "、".join(folders))) if folders
                  else "ブループリントのフォルダが見つかりません", icon="CHECKMARK" if folders else "ERROR")
        box = layout.box()
        box.label(text="変換の設定")
        draw_options(box, self)


def draw_options(layout, p):
    col = layout.column(align=True)
    col.prop(p, "cull_hidden")
    col.prop(p, "optimize_blocks")
    col.prop(p, "fix_coplanar")
    col.prop(p, "max_texture_compression")


# --------------------------------------------------------------------------------------------
# Finding the converter and the blueprints
# --------------------------------------------------------------------------------------------

def find_converter():
    p = prefs()
    candidates = []
    if p is not None and p.converter_path:
        candidates.append(bpy.path.abspath(p.converter_path))
    if os.environ.get("SMCAD_CONVERTER"):
        candidates.append(os.environ["SMCAD_CONVERTER"])
    here = os.path.dirname(os.path.abspath(__file__))
    for up in (2, 3):
        base = here
        for _ in range(up):
            base = os.path.dirname(base)
        candidates.append(os.path.join(base, "SMConverter", "Build", "SMConverterCLI-Release", CONVERTER_EXE))
    for c in candidates:
        if c and os.path.isdir(c):
            c = os.path.join(c, CONVERTER_EXE)
        if c and os.path.isfile(c):
            return os.path.normpath(c)
    return None


def blueprint_folders():
    p = prefs()
    if p is not None and p.blueprint_folder:
        path = bpy.path.abspath(p.blueprint_folder)
        return [path] if os.path.isdir(path) else []
    appdata = os.environ.get("APPDATA", "")
    return sorted(glob.glob(os.path.join(appdata, "Axolot Games", "Scrap Mechanic", "User", "User_*", "Blueprints")))


_cache = {"time": 0.0, "items": []}


def list_blueprints(max_age=10.0):
    """[(blueprint.json path, name, uuid, modified time)], newest first. Cached for a few seconds."""
    if time.time() - _cache["time"] < max_age:
        return _cache["items"]
    items = []
    for folder in blueprint_folders():
        for d in os.listdir(folder):
            bp = os.path.join(folder, d, "blueprint.json")
            desc = os.path.join(folder, d, "description.json")
            if not os.path.isfile(bp):
                continue
            name = d
            try:
                with open(desc, encoding="utf-8-sig") as f:
                    name = json.load(f).get("name") or d
            except (OSError, ValueError):
                pass
            items.append((bp, str(name), d, os.path.getmtime(bp)))
    items.sort(key=lambda i: -i[3])
    _cache.update(time=time.time(), items=items)
    return items


def output_name(name, uuid):
    """Folder / file name for the converter (-name): readable and unique per blueprint."""
    safe = re.sub(r"[^0-9A-Za-z_-]+", "_", name).strip("_") or "blueprint"
    return "%s_%s" % (safe[:40], uuid[:8])


def converter_command(exe, bp_path, out_name, p):
    cmd = [exe, "-type", "Blueprint", "-path", bp_path, "-name", out_name, "-gltf", "-septype", "joints",
           "-exportmaterials", "-exportuvs", "-exportnormals"]
    if p is None or p.cull_hidden:
        cmd.append("-cullhidden")
    if p is None or p.optimize_blocks:
        cmd.append("-optimizeblocks")
    if p is None or p.fix_coplanar:
        cmd.append("-fixcoplanar")
    if p is not None and p.max_texture_compression:
        cmd.append("-maxtexcompression")
    return cmd


def output_path(exe, out_name):
    return os.path.join(os.path.dirname(exe), "ConvertedBlueprints", out_name, out_name + ".glb")


# --------------------------------------------------------------------------------------------
# Operator
# --------------------------------------------------------------------------------------------

_items_ref = []


def _blueprint_items(self, context):
    global _items_ref
    _items_ref = [(uuid, name, "%s（%s）" % (name, uuid)) for _bp, name, uuid, _t in list_blueprints()]
    return _items_ref or [("NONE", "ブループリントが見つかりません", "")]


class SMCAD_OT_convert_blueprint(bpy.types.Operator):
    """Scrap Mechanic のブループリントを名前で選び、変換して取り込みます（SMC-AD 版の変換ツールを使います）"""

    bl_idname = "smcad.convert_blueprint"
    bl_label = "ブループリントから取り込む"
    bl_property = "blueprint"
    bl_options = {"REGISTER", "UNDO"}

    blueprint: EnumProperty(name="ブループリント", items=_blueprint_items)
    sync: BoolProperty(name="終わるまで待つ", default=False, options={"HIDDEN", "SKIP_SAVE"})

    _proc = None
    _timer = None
    _started = 0.0

    @classmethod
    def poll(cls, context):
        return not context.window_manager.smcad_converting

    def _prepare(self):
        exe = find_converter()
        if exe is None:
            self.report({"ERROR"}, "変換ツール（SMConverterCLI.exe）が見つかりません。アドオンの設定で場所を指定してください")
            return None
        entry = (next((i for i in list_blueprints() if i[2] == self.blueprint), None)
                 or next((i for i in list_blueprints(0) if i[2] == self.blueprint), None))
        if entry is None:
            self.report({"ERROR"}, "ブループリントを選んでください")
            return None
        bp_path, name, uuid, _t = entry
        out_name = output_name(name, uuid)
        out = output_path(exe, out_name)
        if os.path.isfile(out):
            os.remove(out)  # so a failed conversion is noticed instead of importing an old file
        return exe, converter_command(exe, bp_path, out_name, prefs()), out, name

    def _finish(self, context, out, name, code):
        wm = context.window_manager
        wm.smcad_converting = ""
        if not os.path.isfile(out):
            self.report({"ERROR"}, "「%s」の変換に失敗しました（終了コード %s）" % (name, code))
            return {"CANCELLED"}
        result = bpy.ops.smcad.import_glb(filepath=out)
        if "FINISHED" not in result:
            return {"CANCELLED"}
        self.report({"INFO"}, "「%s」を変換して取り込みました（%.0f 秒）" % (name, time.time() - self._started))
        return {"FINISHED"}

    def execute(self, context):
        # Picked in the search popup: start again as a background conversion (the UI stays usable)
        if not bpy.app.background and context.window is not None and not self.sync:
            bpy.ops.smcad.convert_blueprint("INVOKE_DEFAULT", blueprint=self.blueprint)
            return {"FINISHED"}
        # Background / scripted use: convert synchronously
        prepared = self._prepare()
        if prepared is None:
            return {"CANCELLED"}
        exe, cmd, out, name = prepared
        self._started = time.time()
        code = subprocess.run(cmd, cwd=os.path.dirname(exe), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)).returncode
        return self._finish(context, out, name, code)

    def invoke(self, context, event):
        if not self.properties.is_property_set("blueprint"):
            list_blueprints(0)
            context.window_manager.invoke_search_popup(self)
            return {"RUNNING_MODAL"}
        prepared = self._prepare()
        if prepared is None:
            return {"CANCELLED"}
        self._exe, cmd, self._out, self._name = prepared
        self._started = time.time()
        self._proc = subprocess.Popen(cmd, cwd=os.path.dirname(self._exe), stdout=subprocess.DEVNULL,
                                      stderr=subprocess.DEVNULL, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        wm = context.window_manager
        wm.smcad_converting = self._name
        self._timer = wm.event_timer_add(0.5, window=context.window)
        wm.modal_handler_add(self)
        self._status(context)
        return {"RUNNING_MODAL"}

    def _status(self, context, text=None):
        msg = text or "SMC-AD: 「%s」を変換中… %d 秒（Esc で中止）" % (self._name, time.time() - self._started)
        context.workspace.status_text_set(msg)
        for area in context.screen.areas:
            area.tag_redraw()

    def _cleanup(self, context):
        if self._timer is not None:
            context.window_manager.event_timer_remove(self._timer)
            self._timer = None
        context.workspace.status_text_set(None)

    def modal(self, context, event):
        if event.type == "ESC" and event.value == "PRESS":
            self._proc.terminate()
            self._cleanup(context)
            context.window_manager.smcad_converting = ""
            self.report({"WARNING"}, "変換を中止しました")
            return {"CANCELLED"}
        if event.type != "TIMER":
            return {"PASS_THROUGH"}
        code = self._proc.poll()
        if code is None:
            self._status(context)
            return {"RUNNING_MODAL"}
        self._cleanup(context)
        return self._finish(context, self._out, self._name, code)


def menu_import(self, context):
    self.layout.operator(SMCAD_OT_convert_blueprint.bl_idname, text="Scrap Mechanic ブループリント（変換して取り込む）")


classes = (SMCAD_Preferences, SMCAD_OT_convert_blueprint)
