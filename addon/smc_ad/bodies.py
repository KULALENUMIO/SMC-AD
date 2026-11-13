# SPDX-License-Identifier: GPL-3.0-or-later
"""Taking bodies out of the joint hierarchy (e.g. a block that only held parts in place in the game).

  A ── B ── C          detach B          A ── C        (C keeps its world position, its pivot and axis)
                       ────────>         (B parked in the "外したボディ" collection, can be restored)

Detach:  the children of B are re-parented to B's parent, keeping their world transform; their joint info
         (sm_joint.parent_body) now names A. B moves to an excluded collection and remembers everything
         needed to restore it: original parent, children, collections.
Delete:  the same, then B is removed for good.
Restore: B goes back to its collections and its former children are parented to it again (they keep the
         world transform they have now).
The Unity export leaves out bodies that are detached or deleted, with the same re-parenting.
"""

import bpy
from mathutils import Matrix

from . import batch

PARKED_COLLECTION = "SMC-AD 外したボディ"
RECORD = "smcad_detached"            # on the detached body
JOINT_PARENT_ORIGINAL = "parent_body_original"


def is_body(obj):
    return obj is not None and obj.type == "MESH" and obj.get("sm_body") is not None


def is_detached(obj):
    return obj is not None and obj.get(RECORD) is not None


def body_label(obj):
    if obj is None:
        return "-"
    return obj.name


def _reparent_keep(child, parent):
    world = child.matrix_world.copy()
    child.parent = parent
    child.matrix_parent_inverse = Matrix.Identity(4)
    child.matrix_world = world


def _set_joint_parent(child, parent):
    joint = child.get("sm_joint")
    if joint is None:
        return
    if JOINT_PARENT_ORIGINAL not in joint:
        joint[JOINT_PARENT_ORIGINAL] = joint.get("parent_body", -1)
    joint["parent_body"] = int(parent["sm_body"]) if is_body(parent) else -1


def _restore_joint_parent(child, parent):
    joint = child.get("sm_joint")
    if joint is None:
        return
    joint["parent_body"] = int(parent["sm_body"]) if is_body(parent) else joint.get(JOINT_PARENT_ORIGINAL, -1)
    if is_body(parent) and joint.get(JOINT_PARENT_ORIGINAL) == joint["parent_body"]:
        del joint[JOINT_PARENT_ORIGINAL]


def parked_collection(scene):
    coll = bpy.data.collections.get(PARKED_COLLECTION)
    if coll is None:
        coll = bpy.data.collections.new(PARKED_COLLECTION)
    if coll.name not in scene.collection.children:
        scene.collection.children.link(coll)
    _exclude(bpy.context.view_layer.layer_collection, coll)
    return coll


def _exclude(layer_collection, coll):
    for lc in layer_collection.children:
        if lc.collection == coll:
            lc.exclude = True
            return True
        if _exclude(lc, coll):
            return True
    return False


def detach(context, body):
    """Takes `body` out of the hierarchy (reversible). Returns the re-parented children."""
    if not is_body(body) or is_detached(body):
        return []
    parent = body.parent
    children = list(body.children)
    for child in children:
        _reparent_keep(child, parent)
        _set_joint_parent(child, parent)
    record = {"parent": parent, "children": {str(i): c for i, c in enumerate(children)},
              "collections": [c.name for c in body.users_collection]}
    parked = parked_collection(context.scene)
    for coll in list(body.users_collection):
        coll.objects.unlink(body)
    parked.objects.link(body)
    body[RECORD] = record
    body.select_set(False) if context.view_layer.objects.get(body.name) else None
    return children


def delete(context, body):
    """Takes `body` out of the hierarchy and deletes it. Returns the re-parented children."""
    if not is_body(body):
        return []
    children = list(body.children) if not is_detached(body) else []
    parent = body.parent
    for child in children:
        _reparent_keep(child, parent)
        _set_joint_parent(child, parent)
    mesh = body.data
    bpy.data.objects.remove(body)
    if mesh is not None and mesh.users == 0:
        bpy.data.meshes.remove(mesh)
    return children


def restore(context, body):
    """Puts a detached body back. Returns the children parented to it again."""
    record = body.get(RECORD)
    if record is None:
        return []
    for coll in list(body.users_collection):
        coll.objects.unlink(body)
    targets = []
    for name in record.get("collections", []):
        coll = context.scene.collection if name == context.scene.collection.name else bpy.data.collections.get(name)
        if coll is not None and coll not in targets:
            targets.append(coll)
    for coll in targets or [context.scene.collection]:
        coll.objects.link(body)
    context.view_layer.update()
    parent = record.get("parent")
    if parent is not None and body.parent != parent:
        _reparent_keep(body, parent)
    children = [c for c in record.get("children", {}).values() if c is not None and c.name in bpy.data.objects]
    context.view_layer.update()
    for child in children:
        _reparent_keep(child, body)
        _restore_joint_parent(child, body)
    del body[RECORD]
    return children


def detached_in(root):
    """Detached bodies of one blueprint."""
    return [o for o in batch.blueprint_objects(root) if is_detached(o)] if root is not None else \
        [o for o in bpy.data.objects if is_detached(o)]


def selected_bodies(context):
    bodies = [o for o in context.selected_objects if is_body(o)]
    if not bodies and is_body(context.active_object):
        bodies = [context.active_object]
    return bodies


def removed_body_ids(root):
    """sm_body ids of the blueprint that are detached or deleted in Blender (for the Unity export)."""
    present = set()
    detached = set()
    for obj in batch.blueprint_objects(root):
        if is_body(obj):
            (detached if is_detached(obj) else present).add(int(obj["sm_body"]))
    return detached, present


# --------------------------------------------------------------------------------------------
# Operators
# --------------------------------------------------------------------------------------------

class SMCAD_OT_detach_body(bpy.types.Operator):
    """選んだボディを、ジョイントの親子からいったん外します（あとで戻せます）。
外したボディの子は、外したボディの親につなぎ直します（位置・回転の中心・軸はそのまま）"""

    bl_idname = "smcad.detach_body"
    bl_label = "ボディを外す（子は親につなぐ）"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return bool(selected_bodies(context))

    def execute(self, context):
        done, moved = 0, 0
        for body in selected_bodies(context):
            moved += len(detach(context, body))
            done += 1
            context.view_layer.update()
        self.report({"INFO"}, "%d 個のボディを外し、%d 個の子を親につなぎ直しました（「外したボディ」から戻せます）" % (done, moved))
        return {"FINISHED"}


class SMCAD_OT_delete_body(bpy.types.Operator):
    """選んだボディを削除します。子は削除したボディの親につなぎ直します（元には戻せません。Ctrl+Z は使えます）"""

    bl_idname = "smcad.delete_body"
    bl_label = "ボディを削除（子は親につなぐ）"
    bl_options = {"REGISTER", "UNDO"}

    @classmethod
    def poll(cls, context):
        return bool(selected_bodies(context))

    def execute(self, context):
        done, moved = 0, 0
        for body in selected_bodies(context):
            moved += len(delete(context, body))
            done += 1
            context.view_layer.update()
        self.report({"INFO"}, "%d 個のボディを削除し、%d 個の子を親につなぎ直しました" % (done, moved))
        return {"FINISHED"}

    def invoke(self, context, event):
        return context.window_manager.invoke_confirm(self, event)


class SMCAD_OT_restore_body(bpy.types.Operator):
    """外したボディを元の親子に戻します（子も、外したボディの下につなぎ直します）"""

    bl_idname = "smcad.restore_body"
    bl_label = "外したボディを戻す"
    bl_options = {"REGISTER", "UNDO"}

    body: bpy.props.StringProperty(name="ボディ", description="空のときは、この機体の外したボディをすべて戻します")

    def execute(self, context):
        if self.body:
            targets = [bpy.data.objects.get(self.body)]
        else:
            root = batch.blueprint_root(context.active_object) if context.active_object else None
            targets = detached_in(root)
        targets = [t for t in targets if t is not None and is_detached(t)]
        # innermost first does not matter: each restore keeps world transforms
        for body in targets:
            restore(context, body)
            context.view_layer.update()
        self.report({"INFO"}, "%d 個のボディを戻しました" % len(targets))
        return {"FINISHED"}


classes = (SMCAD_OT_detach_body, SMCAD_OT_delete_body, SMCAD_OT_restore_body)
