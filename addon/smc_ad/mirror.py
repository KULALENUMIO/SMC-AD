# SPDX-License-Identifier: GPL-3.0-or-later
"""左右反転コピー: a mirrored copy of bodies (an arm, a leg) on the other side of the blueprint.

    torso ── arm_L ── hand_L          select arm_L          torso ─┬─ arm_L ── hand_L
                                     ────────────>                └─ arm_R ── hand_R   (mirror image)

The copy is real geometry, not a negative scale: every object gets  M' = R · M · F  where R is the reflection
across the mirror plane (world) and F flips the object's local X. The mesh is flipped by F as well (vertices,
custom normals, face winding), so the object matrix stays a proper rotation (FBX / Unity friendly) and the
local Z axis - the joint axis - becomes the mirror image of the original axis.

The mirror plane: an axis of the blueprint (the root object's X / Y / Z) through the centre of the part's
parent body (default), of the whole blueprint, the blueprint origin or the 3D cursor.

SM data on the copies: new body numbers (sm_body) and joint numbers, joint parent / pivot / axis mirrored,
paint colours, part ids and materials as they are. Textures are mirrored with the geometry (text on a part
reads backwards). Bones are not mirrored: copies of bone-parented objects hang on the armature object.
"""

import re

import bpy
import numpy as np
from bpy.props import BoolProperty, EnumProperty
from mathutils import Matrix, Vector

from . import batch, bodies

MIRROR_OF = "smcad_mirror_of"
FLIP = Matrix.Scale(-1.0, 4, (1.0, 0.0, 0.0))

AXIS_ITEMS = [
    ("X", "X", "機体（ルート）の X 方向に反転"),
    ("Y", "Y", "機体（ルート）の Y 方向に反転"),
    ("Z", "Z", "機体（ルート）の Z 方向に反転"),
]
CENTER_ITEMS = [
    ("PARENT", "親ボディの中心", "反転する部品がつながっているボディ（胴など）の中心で反転"),
    ("BLUEPRINT", "機体の中心", "機体全体の中心で反転"),
    ("ROOT", "機体の原点", "機体（ルート）の原点で反転"),
    ("CURSOR", "3D カーソル", "3D カーソルの位置で反転"),
]


# --------------------------------------------------------------------------------------------
# Names
# --------------------------------------------------------------------------------------------

# a side token: "L" / "R" standing alone (arm_L, Leg.R, L_hand), or a word Left / Right (LeftArm, arm_right)
_TOKEN = re.compile(r"(?<![A-Za-z0-9])(L|R)(?![A-Za-z0-9])|(?<![A-Za-z])(Left|Right|left|right|LEFT|RIGHT)(?![a-z])")
_OPPOSITE = {"L": "R", "R": "L", "Left": "Right", "Right": "Left", "left": "right", "right": "left",
             "LEFT": "RIGHT", "RIGHT": "LEFT"}


def mirror_name(name):
    """左 <-> 右, L <-> R, Left <-> Right; names without a side get "_反転"."""
    base = re.sub(r"\.\d{3}$", "", name)
    swapped = _TOKEN.sub(lambda m: _OPPOSITE[m.group(1) or m.group(2)], base)
    if "左" in base or "右" in base:
        swapped = "".join({"左": "右", "右": "左"}.get(ch, ch) for ch in swapped)
    return swapped if swapped != base else base + "_反転"


# --------------------------------------------------------------------------------------------
# Geometry
# --------------------------------------------------------------------------------------------

def reflection(point, normal):
    """4x4 reflection across the plane through `point` with `normal` (world)."""
    n = Vector(normal).normalized()
    m = Matrix.Identity(4)
    for i in range(3):
        for j in range(3):
            m[i][j] -= 2.0 * n[i] * n[j]
    d = 2.0 * Vector(point).dot(n)
    for i in range(3):
        m[i][3] = d * n[i]
    return m


def _corner_normals(mesh):
    out = np.empty(len(mesh.loops) * 3, np.float32)
    mesh.corner_normals.foreach_get("vector", out)
    return out.reshape(-1, 3)


def flip_mesh_x(mesh):
    """Mirror mesh data along its local X: vertices (and shape keys), face winding, custom normals."""
    custom = mesh.has_custom_normals
    n_loops = len(mesh.loops)
    normals = _corner_normals(mesh) if custom and n_loops else None
    old_vert = np.empty(n_loops, np.int64)
    mesh.loops.foreach_get("vertex_index", old_vert)

    mesh.transform(FLIP, shape_keys=True)
    mesh.flip_normals()

    if normals is not None:
        # flip_normals reverses the corners of each face: find, for every new corner, the old corner of the
        # same face and vertex, and give it that normal, mirrored
        new_vert = np.empty(n_loops, np.int64)
        mesh.loops.foreach_get("vertex_index", new_vert)
        loop_total = np.empty(len(mesh.polygons), np.int64)
        mesh.polygons.foreach_get("loop_total", loop_total)
        face = np.repeat(np.arange(len(mesh.polygons)), loop_total)
        nv = max(int(old_vert.max(initial=0)), int(new_vert.max(initial=0))) + 1
        old_key = face * nv + old_vert
        new_key = face * nv + new_vert
        order = np.argsort(old_key, kind="stable")
        pos = np.searchsorted(old_key[order], new_key)
        src = order[np.clip(pos, 0, n_loops - 1)]
        mirrored = normals[src] * np.array([-1.0, 1.0, 1.0], np.float32)
        mesh.normals_split_custom_set([tuple(v) for v in mirrored])
    mesh.update()


def _world_bounds_center(objs):
    pts = []
    for o in objs:
        if o.type == "MESH":
            mw = o.matrix_world
            pts += [mw @ Vector(c) for c in o.bound_box]
    if not pts:
        return None
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return (lo + hi) * 0.5


# --------------------------------------------------------------------------------------------
# What to mirror
# --------------------------------------------------------------------------------------------

def _usable(o):
    return o is not None and o.type in {"MESH", "EMPTY"} and not bodies.is_detached(o)


def collect(selected, include_children):
    """Selected objects (+ everything under them), parents before children; and the top ones."""
    chosen = []
    seen = set()
    for o in selected:
        group = [o] + (list(o.children_recursive) if include_children else [])
        for x in group:
            if _usable(x) and x not in seen:
                seen.add(x)
                chosen.append(x)

    def depth(o):
        d = 0
        while o.parent is not None:
            d += 1
            o = o.parent
        return d

    chosen.sort(key=depth)
    tops = [o for o in chosen if o.parent not in seen]
    return chosen, tops


def plane_for(context, top, root, axis, center):
    """(point, normal) of the mirror plane for one selected part."""
    basis = root.matrix_world.to_3x3().normalized() if root is not None else Matrix.Identity(3)
    normal = basis.col["XYZ".index(axis)].normalized()
    point = None
    if center == "PARENT":
        parent = top.parent
        while parent is not None and parent.type != "MESH":
            parent = parent.parent
        point = _world_bounds_center([parent]) if parent is not None else None
    if point is None and center in {"PARENT", "BLUEPRINT"}:
        members = [o for o in batch.blueprint_objects(root) if bodies.is_body(o) and not bodies.is_detached(o)] \
            if root is not None else []
        point = _world_bounds_center(members)
    if point is None and center == "CURSOR":
        point = context.scene.cursor.location.copy()
    if point is None:
        point = root.matrix_world.translation.copy() if root is not None else Vector()
    return point, normal


# --------------------------------------------------------------------------------------------
# Mirror
# --------------------------------------------------------------------------------------------

def _joint_dict(joint):
    try:
        return joint.to_dict()
    except AttributeError:
        return dict(joint)


def _mirror_joint(joint, R, parent_body, new_id):
    data = _joint_dict(joint)
    if "pivot" in data and len(data["pivot"]) == 3:
        data["pivot"] = list(R @ Vector(data["pivot"]))
    if "axis_world" in data and len(data["axis_world"]) == 3:
        data["axis_world"] = list((R.to_3x3() @ Vector(data["axis_world"])).normalized())
    data["parent_body"] = parent_body
    if new_id is not None:
        data["mirror_of_id"] = data.get("id", -1)
        data["id"] = new_id
    data.pop(bodies.JOINT_PARENT_ORIGINAL, None)
    return data


def mirror(context, selected, axis="X", center="PARENT", include_children=True, keep_original=True):
    """Mirrors the selected parts. Returns (new or changed objects, notes)."""
    chosen, tops = collect(selected, include_children)
    if not chosen:
        return [], []
    root = batch.blueprint_root(chosen[0])
    notes = []

    # one reflection per selected part (each may hang on a different parent)
    top_of = {}
    for o in chosen:
        t = o
        while t not in tops:
            t = t.parent
        top_of[o] = t
    planes = {t: reflection(*plane_for(context, t, root, axis, center)) for t in tops}

    members = batch.blueprint_objects(root) if root is not None else []
    next_body = max([int(o["sm_body"]) for o in members if o.get("sm_body") is not None] + [-1]) + 1
    next_joint = max([int(o["sm_joint"].get("id", -1)) for o in members if o.get("sm_joint") is not None] + [0]) + 1

    old_world = {o: o.matrix_world.copy() for o in chosen}
    # in place: children that are not mirrored stay where they are
    staying = [(c, c.matrix_world.copy()) for o in chosen for c in o.children if c not in chosen]         if not keep_original else []
    new_world = {o: planes[top_of[o]] @ old_world[o] @ FLIP for o in chosen}

    # ---- the objects to change: copies, or the originals themselves
    target = {}
    meshes = {}
    for o in chosen:
        if keep_original:
            t = o.copy()
            t.name = mirror_name(o.name)
            for coll in o.users_collection:
                coll.objects.link(t)
        else:
            t = o
        if o.type == "MESH" and o.data is not None:
            data = o.data
            if data not in meshes:
                shared_outside = any(u not in chosen for u in bpy.data.objects if u.data == data)
                meshes[data] = data.copy() if (keep_original or shared_outside) else data
                if meshes[data] is not data:
                    meshes[data].name = mirror_name(data.name)
                flip_mesh_x(meshes[data])
            t.data = meshes[data]
        target[o] = t

    # ---- hierarchy and transforms (parents first)
    bone_parented = 0
    for o in chosen:
        t = target[o]
        parent = o.parent
        new_parent = target.get(parent, parent)
        if o.parent_type != "OBJECT" and parent not in target:
            bone_parented += 1
        if keep_original or new_parent is not o.parent or o.parent_type != "OBJECT":
            t.parent = new_parent
            t.parent_type = "OBJECT"
        t.matrix_parent_inverse = Matrix.Identity(4)
        pw = new_world[parent] if parent in new_world else (new_parent.matrix_world if new_parent else Matrix.Identity(4))
        t.matrix_basis = pw.inverted_safe() @ new_world[o]
        if keep_original:
            for mod in [m for m in t.modifiers if m.type == "ARMATURE"]:
                t.modifiers.remove(mod)

    for c, world in staying:
        c.matrix_parent_inverse = Matrix.Identity(4)
        c.matrix_basis = new_world[c.parent].inverted_safe() @ world

    # ---- SM data
    new_ids = {}
    if keep_original:
        for o in chosen:
            if bodies.is_body(o):
                new_ids[o] = next_body
                next_body += 1
    for o in chosen:
        t = target[o]
        if keep_original:
            t[MIRROR_OF] = o.name
            if o in new_ids:
                t["sm_body"] = new_ids[o]
            if "sm_root" in t:
                del t["sm_root"]
            for key in (bodies.RECORD,):
                if key in t:
                    del t[key]
        joint = o.get("sm_joint")
        if joint is not None and bodies.is_body(o):
            parent_body_obj = o.parent
            while parent_body_obj is not None and not bodies.is_body(parent_body_obj):
                parent_body_obj = parent_body_obj.parent
            if parent_body_obj in target and keep_original:
                pb = int(target[parent_body_obj]["sm_body"])
            elif parent_body_obj is not None:
                pb = int(parent_body_obj["sm_body"])
            else:
                pb = int(joint.get("parent_body", -1))
            jid = None
            if keep_original:
                jid = next_joint
                next_joint += 1
            t["sm_joint"] = _mirror_joint(joint, planes[top_of[o]], pb, jid)

    if bone_parented:
        notes.append("骨につないでいた %d 個は、アーマチュアにつなぎました（骨は反転していません）" % bone_parented)
    return [target[o] for o in chosen], notes


# --------------------------------------------------------------------------------------------
# Operator
# --------------------------------------------------------------------------------------------

class SMCAD_OT_mirror_parts(bpy.types.Operator):
    """選んだ部品（腕・脚など、ジョイントの先も含めて）を、機体の反対側に左右反転してコピーします。
形は本当に反転します（拡大縮小のマイナスは使いません）。ジョイントの軸・つながりも反転したコピーに付け直します"""

    bl_idname = "smcad.mirror_parts"
    bl_label = "左右反転コピー"
    bl_options = {"REGISTER", "UNDO"}

    axis: EnumProperty(name="反転する向き", items=AXIS_ITEMS, default="X")
    center: EnumProperty(name="反転の中心", items=CENTER_ITEMS, default="PARENT")
    include_children: BoolProperty(name="先につながる部品も", description="選んだボディの子（ジョイントの先）も一緒に反転します",
                                   default=True)
    keep_original: BoolProperty(name="コピーを作る", description="オフにすると、コピーを作らずに選んだ部品そのものを反転します",
                                default=True)

    @classmethod
    def poll(cls, context):
        return context.mode == "OBJECT" and bool(bodies.selected_bodies(context))

    def draw(self, context):
        layout = self.layout
        layout.use_property_split = True
        layout.prop(self, "axis", expand=True)
        layout.prop(self, "center")
        layout.prop(self, "include_children")
        layout.prop(self, "keep_original")

    def execute(self, context):
        selected = bodies.selected_bodies(context)
        done, notes = mirror(context, selected, self.axis, self.center, self.include_children, self.keep_original)
        if not done:
            self.report({"WARNING"}, "反転できる部品がありません")
            return {"CANCELLED"}
        if self.keep_original:
            for o in context.selected_objects:
                o.select_set(False)
            for o in done:
                if context.view_layer.objects.get(o.name) is not None:
                    o.select_set(True)
            context.view_layer.objects.active = done[0]
        n_bodies = sum(1 for o in done if bodies.is_body(o))
        msg = ("%d 個のボディを反転してコピーしました" if self.keep_original else "%d 個のボディを反転しました") % n_bodies
        if notes:
            msg += "。" + "。".join(notes)
        self.report({"INFO"}, msg)
        return {"FINISHED"}


classes = (SMCAD_OT_mirror_parts,)
