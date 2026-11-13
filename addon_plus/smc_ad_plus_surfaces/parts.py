# SPDX-License-Identifier: GPL-3.0-or-later
"""Parts (one SM part / block = one _SM_SHAPE value) picked by clicking in object mode.

The picked parts are stored on the object (custom property "smcadp_parts", a list of part ids), so
picking is undoable and survives saving. They are shown with a translucent overlay; no edit mode
and no face selection is involved (requirement S5).
"""

import bpy
import gpu
import numpy as np
from bpy_extras import view3d_utils
from gpu_extras.batch import batch_for_shader

SHAPE_ATTRIBUTE = "_SM_SHAPE"
PROP = "smcadp_parts"
HIGHLIGHT = (1.0, 0.55, 0.05, 0.38)

_poly_parts_cache = {}


def poly_parts(mesh):
    """Part id per polygon (numpy int64), or None when the mesh has no SM part ids."""
    key = (mesh.as_pointer(), len(mesh.polygons), len(mesh.loops))
    cached = _poly_parts_cache.get(mesh.name_full)
    if cached is not None and cached[0] == key:
        return cached[1]
    attr = mesh.attributes.get(SHAPE_ATTRIBUTE)
    if attr is None or len(mesh.polygons) == 0:
        return None
    values = np.empty(len(attr.data), dtype=np.float32)
    attr.data.foreach_get("value", values)
    loop_start = np.empty(len(mesh.polygons), dtype=np.int64)
    mesh.polygons.foreach_get("loop_start", loop_start)
    if attr.domain == "POINT":
        loop_vert = np.empty(len(mesh.loops), dtype=np.int64)
        mesh.loops.foreach_get("vertex_index", loop_vert)
        parts = values[loop_vert[loop_start]]
    elif attr.domain == "CORNER":
        parts = values[loop_start]
    elif attr.domain == "FACE":
        parts = values
    else:
        return None
    parts = np.rint(parts).astype(np.int64)
    _poly_parts_cache[mesh.name_full] = (key, parts)
    return parts


def picked(obj):
    return set(int(v) for v in obj.get(PROP, []))


def set_picked(obj, parts):
    if parts:
        obj[PROP] = sorted(int(p) for p in parts)
    elif PROP in obj:
        del obj[PROP]


def picked_objects():
    return [o for o in bpy.data.objects if o.type == "MESH" and o.get(PROP)]


def clear_all():
    for obj in picked_objects():
        set_picked(obj, None)


def count():
    return sum(len(picked(o)) for o in picked_objects())


def poly_mask(obj, parts=None):
    """Bool per polygon: True on the picked (or given) parts."""
    pp = poly_parts(obj.data)
    parts = picked(obj) if parts is None else parts
    if pp is None or not parts:
        return np.zeros(len(obj.data.polygons), dtype=bool)
    return np.isin(pp, np.array(sorted(parts), dtype=np.int64))


def part_of_poly(obj, poly_index):
    pp = poly_parts(obj.data)
    if pp is None or poly_index < 0 or poly_index >= len(pp):
        return None
    return int(pp[poly_index])


def ray_pick(context, origin, direction):
    """(object, polygon index) hit by the ray, or None."""
    depsgraph = context.evaluated_depsgraph_get()
    hit, _loc, _normal, index, obj, _matrix = context.scene.ray_cast(depsgraph, origin, direction)
    if not hit or obj is None:
        return None
    obj = obj.original
    if obj.type != "MESH":
        return None
    return obj, index


def view_pick(context, region, rv3d, coord):
    origin = view3d_utils.region_2d_to_origin_3d(region, rv3d, coord)
    direction = view3d_utils.region_2d_to_vector_3d(region, rv3d, coord)
    return ray_pick(context, origin, direction)


def apply_pick(context, obj, poly_index, extend):
    """Picks (or with extend toggles) the part under a polygon and shows its material in the panel.
    Returns the part id or None."""
    part = part_of_poly(obj, poly_index)
    if part is None:
        return None
    if not extend:
        for other in picked_objects():
            if other != obj:
                set_picked(other, None)
        current = set()
    else:
        current = picked(obj)
    if extend and part in current:
        current.discard(part)
    else:
        current.add(part)
    set_picked(obj, current)

    # Make the body active and show the clicked face's material in the panel
    if context.view_layer.objects.get(obj.name) is not None:
        if not extend:
            for o in context.selected_objects:
                o.select_set(False)
        obj.select_set(True)
        context.view_layer.objects.active = obj
    if 0 <= poly_index < len(obj.data.polygons):
        mi = obj.data.polygons[poly_index].material_index
        if mi < len(obj.material_slots):
            obj.active_material_index = mi
    return part


def picked_materials():
    """Materials on the picked parts."""
    mats = []
    for obj in picked_objects():
        mask = poly_mask(obj)
        if not mask.any():
            continue
        idx = np.empty(len(obj.data.polygons), dtype=np.int32)
        obj.data.polygons.foreach_get("material_index", idx)
        for si in np.unique(idx[mask]).tolist():
            if si < len(obj.material_slots):
                m = obj.material_slots[si].material
                if m is not None and m not in mats:
                    mats.append(m)
    return mats


# --------------------------------------------------------------------------------------------
# Highlight overlay
# --------------------------------------------------------------------------------------------

_handle = None
_batches = {}
_shader = None


def _batch_for(obj, parts):
    mesh = obj.data
    mw = obj.matrix_world
    key = (tuple(sorted(parts)), len(mesh.polygons), len(mesh.vertices),
           tuple(round(v, 5) for row in mw for v in row))
    cached = _batches.get(obj.name_full)
    if cached is not None and cached[0] == key:
        return cached[1]

    mesh.calc_loop_triangles()
    n_tri = len(mesh.loop_triangles)
    pp = poly_parts(mesh)
    if pp is None or n_tri == 0:
        return None
    tri_verts = np.empty(n_tri * 3, dtype=np.int64)
    mesh.loop_triangles.foreach_get("vertices", tri_verts)
    tri_poly = np.empty(n_tri, dtype=np.int64)
    mesh.loop_triangles.foreach_get("polygon_index", tri_poly)
    keep = np.isin(pp[tri_poly], np.array(sorted(parts), dtype=np.int64))
    if not keep.any():
        return None
    co = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
    mesh.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    nrm = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
    mesh.vertex_normals.foreach_get("vector", nrm)
    co = co + nrm.reshape(-1, 3) * 0.002  # lift slightly so the overlay doesn't z-fight
    m = np.array(mw, dtype=np.float32)
    world = co @ m[:3, :3].T + m[:3, 3]
    pos = world[tri_verts.reshape(-1, 3)[keep].reshape(-1)]
    batch = batch_for_shader(_shader, "TRIS", {"pos": pos.tolist()})
    _batches[obj.name_full] = (key, batch)
    return batch


def _draw():
    global _shader
    objs = [o for o in picked_objects() if o.visible_get()]
    if not objs:
        return
    if _shader is None:
        _shader = gpu.shader.from_builtin("UNIFORM_COLOR")
    gpu.state.blend_set("ALPHA")
    gpu.state.depth_test_set("LESS_EQUAL")
    gpu.state.depth_mask_set(False)
    _shader.bind()
    _shader.uniform_float("color", HIGHLIGHT)
    for obj in objs:
        batch = _batch_for(obj, picked(obj))
        if batch is not None:
            batch.draw(_shader)
    gpu.state.depth_mask_set(True)
    gpu.state.depth_test_set("NONE")
    gpu.state.blend_set("NONE")


def register():
    global _handle
    if _handle is None and not bpy.app.background:
        _handle = bpy.types.SpaceView3D.draw_handler_add(_draw, (), "WINDOW", "POST_VIEW")


def unregister():
    global _handle
    if _handle is not None:
        bpy.types.SpaceView3D.draw_handler_remove(_handle, "WINDOW")
        _handle = None
    _batches.clear()
    _poly_parts_cache.clear()
