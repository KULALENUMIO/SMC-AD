# SPDX-License-Identifier: GPL-3.0-or-later
import os

import bpy
from bpy.props import BoolProperty, FloatProperty, StringProperty
from bpy_extras.io_utils import ImportHelper

from . import api, materials, overrides
from .glb_reader import GlbFile


def _tag_root(objects, filepath):
    """The glTF root node carries sm_shapes / sm_joints; remember the source file on it."""
    for obj in objects:
        if obj.get("sm_shapes") is not None or obj.get("sm_blueprint") is not None:
            obj["smcad_source"] = filepath
            return obj
    return None


def blueprint_display_name(root, filepath):
    """The blueprint's own name (description.json, written by SMConverter), else the file name."""
    name = root.get("sm_blueprint_name") or root.get("sm_blueprint")
    return str(name) if name else os.path.splitext(os.path.basename(filepath))[0]


def name_objects(root, objects, filepath):
    """Root = blueprint name ("SEV 01"); bodies = "SEV 01_01", "_02", ... in joint-hierarchy order
    (depth first, children by body index), so a re-import gives the same numbers.
    Every body also gets the blueprint id, so it stays associated after being re-parented (e.g. to bones)."""
    import uuid
    name = blueprint_display_name(root, filepath)
    root.name = name
    if root.type == "EMPTY":
        root.empty_display_size = 0.5
    bp_id = uuid.uuid4().hex
    root["smcad_blueprint_id"] = bp_id

    order = []

    def walk(obj):
        if obj.type == "MESH" and obj.get("sm_body") is not None:
            order.append(obj)
        for child in sorted(obj.children, key=lambda c: (c.get("sm_body", 1 << 30), c.name)):
            walk(child)

    walk(root)
    width = max(2, len(str(len(order))))
    root["smcad_blueprint_name"] = name
    for i, obj in enumerate(order, 1):
        obj["smcad_blueprint_id"] = bp_id
        obj["smcad_blueprint_name"] = name
        obj.name = "%s_%0*d" % (name, width, i)
        if obj.data is not None and obj.data.users == 1:
            obj.data.name = obj.name
    for obj in objects:
        if obj not in order and obj != root:
            obj["smcad_blueprint_id"] = bp_id
    return name


def rebuild_materials(material_list, filepath, specular=1.0, glow=1.0):
    glb = GlbFile(filepath)
    prefix = os.path.splitext(os.path.basename(filepath))[0] + "_"
    images = materials.ImageCache(glb, prefix)
    built = 0
    try:
        for mat in material_list:
            if materials.build_material(mat, images, specular, glow):
                built += 1
    finally:
        images.close()
    return built


class SMCAD_OT_import_glb(bpy.types.Operator, ImportHelper):
    """SMConverter（-gltf）で書き出した Scrap Mechanic のブループリントを取り込み、SMのマテリアルを作ります"""

    bl_idname = "smcad.import_glb"
    bl_label = "SMブループリントを取り込む (.glb)"
    bl_options = {"REGISTER", "UNDO", "PRESET"}

    filename_ext = ".glb"
    filter_glob: StringProperty(default="*.glb", options={"HIDDEN"})

    build_materials: BoolProperty(
        name="SMのマテリアルを作る",
        description="glTF のマテリアルを、Scrap Mechanic の見た目（塗装色・ASG・発光）のマテリアルに置き換えます",
        default=True,
    )
    specular: FloatProperty(name="反射の強さ", default=1.0, min=0.0, soft_max=4.0)
    glow: FloatProperty(name="発光の強さ", default=1.0, min=0.0, soft_max=10.0)

    def execute(self, context):
        filepath = self.filepath
        try:
            glb = GlbFile(filepath)
        except (OSError, ValueError) as ex:
            self.report({"ERROR"}, str(ex))
            return {"CANCELLED"}

        if not glb.is_smconverter:
            self.report({"WARNING"}, "この .glb は SMConverter で書き出したものではないため、SMの情報なしで取り込みます")

        objects_before = set(bpy.data.objects)
        materials_before = set(bpy.data.materials)
        images_before = set(bpy.data.images)

        result = bpy.ops.import_scene.gltf(filepath=filepath)
        if "FINISHED" not in result:
            return {"CANCELLED"}

        new_objects = [o for o in bpy.data.objects if o not in objects_before]
        new_materials = [m for m in bpy.data.materials if m not in materials_before]
        root = _tag_root(new_objects, filepath)
        if root is not None:
            name_objects(root, new_objects, filepath)

        built = 0
        if self.build_materials:
            built = rebuild_materials(new_materials, filepath, self.specular, self.glow)

            # Images the glTF importer created but our materials no longer use
            for image in [i for i in bpy.data.images if i not in images_before]:
                if image.users == 0:
                    bpy.data.images.remove(image)

        if self.build_materials:
            from . import export, reset
            reset.record_import(new_objects, root, self.specular, self.glow)
            export.record_gltf_materials([m for m in new_materials if m.get("smcad_built")], glb.json)
            api.call(api.imported, context, new_objects, [m for m in new_materials if m.get("smcad_built")], filepath)

        bodies = sum(1 for o in new_objects if o.type == "MESH")
        joints = sum(1 for o in new_objects if o.get("sm_joint") is not None)
        self.report({"INFO"}, "SMC-AD: ボディ %d、ジョイント %d、SMマテリアル %d を取り込みました" % (bodies, joints, built))
        if root is not None:
            for obj in context.selected_objects:
                obj.select_set(False)
            root.select_set(True)
            context.view_layer.objects.active = root
        return {"FINISHED"}

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "build_materials")
        col = layout.column()
        col.enabled = self.build_materials
        col.prop(self, "specular")
        col.prop(self, "glow")
        for draw in api.import_options:
            draw(col, context)


class SMCAD_OT_rebuild_materials(bpy.types.Operator):
    """選択中のオブジェクトのSMマテリアルを、元の .glb から作り直します（自分で足したノードは残ります）"""

    bl_idname = "smcad.rebuild_materials"
    bl_label = "SMマテリアルを作り直す"
    bl_options = {"REGISTER", "UNDO"}

    specular: FloatProperty(name="反射の強さ", default=1.0, min=0.0, soft_max=4.0)
    glow: FloatProperty(name="発光の強さ", default=1.0, min=0.0, soft_max=10.0)

    @staticmethod
    def _source(obj):
        from . import batch
        return batch.source_of(obj)[0] or None

    def execute(self, context):
        by_source = {}
        for obj in context.selected_objects:
            src = self._source(obj)
            if not src:
                continue
            for slot in obj.material_slots:
                if slot.material is not None and "sm_shader" in slot.material:
                    by_source.setdefault(src, set()).add(slot.material)

        if not by_source:
            self.report({"WARNING"}, "SMC-AD で取り込んだオブジェクトが選ばれていません")
            return {"CANCELLED"}

        built = 0
        for src, mats in by_source.items():
            if not os.path.isfile(src):
                self.report({"WARNING"}, "元のファイルが見つかりません: " + src)
                continue
            built += rebuild_materials(mats, src, self.specular, self.glow)

        self.report({"INFO"}, "%d 個のマテリアルを作り直しました" % built)
        return {"FINISHED"}


def menu_import(self, context):
    self.layout.operator(SMCAD_OT_import_glb.bl_idname, text="Scrap Mechanic ブループリント（SMC-AD .glb）")
