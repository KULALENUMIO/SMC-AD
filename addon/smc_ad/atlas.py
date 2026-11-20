# SPDX-License-Identifier: GPL-3.0-or-later
"""Texture atlas for the Unity FBX export (「量産向け」: fewer materials and draw calls).

Most SM parts use a small piece of a big shared texture, and almost none repeat a texture across a face.
For every material that can go in (opaque, no image override, no hand-made layer nodes, no face that spans
more than one texture tile), this cuts the used regions out of its Dif / Asg / Nor textures (with a margin of
the original pixels around them), packs them into one atlas per export and rewrites the UVs, so the whole
blueprint draws with one material (+ the materials that could not go in).

Nothing in the scene changes: the meshes are swapped for edited copies during the export and swapped back.
Per-material settings (colour override, surface type) go into a small table in Unity; each face finds its
row through the "SM_Part" UV (v = row).
"""

import math
import os

import bpy
import numpy as np

from . import export

ATLAS_MATERIAL = "SMCAD_Atlas"
GLASS = {2, 6, 7, 8, 9, 10}
CUTOUT = {3, 4, 5, 11}
CELL = 16        # px of the region grid (in the texture the region is measured in)
MARGIN = 8       # px of original pixels kept around each region (mipmaps)
EPS = 1e-4
ROLES = (("dif", "SMC.Tex.Dif"), ("asg", "SMC.Tex.Asg"), ("nor", "SMC.Tex.Nor"))
KNOWN_SMCAD = {"override", "surface"}


def _srgb(c):
    return 1.055 * c ** (1 / 2.4) - 0.055 if c > 0.0031308 else 12.92 * c


# what the SM Lit shader uses when a texture is missing (Dif is stored as sRGB)
FILL = {"dif": (_srgb(0.8), _srgb(0.8), _srgb(0.8), 1.0), "asg": (0.5, 0.5, 0.0, 0.5), "nor": (0.5, 0.5, 1.0, 1.0)}


def _images(material):
    nodes = material.node_tree.nodes if material.node_tree is not None else {}
    out = {}
    for role, name in ROLES:
        node = nodes.get(name)
        if node is not None and getattr(node, "image", None) is not None and node.image.size[0] > 0:
            out[role] = node.image
    return out


def _solid(material):
    """All textures are a few pixels (one colour): repeating does not matter, every face can use one spot."""
    imgs = _images(material)
    return bool(imgs) and all(max(im.size) <= 4 for im in imgs.values())


def reason_to_skip(material, smcad):
    """None if the material can go into the atlas, else why not (shown in the export report)."""
    if material is None or "sm_shader" not in material:
        return "SM のマテリアルではない"
    shader = int(material["sm_shader"])
    if shader in GLASS:
        return "ガラス"
    if shader in CUTOUT:
        return "切り抜き"
    if not _images(material):
        return "テクスチャがない"
    extra = set(smcad) - KNOWN_SMCAD
    if "layers" in extra:
        return "手作業のノードがある"
    if extra:
        return "拡張機能の設定（%s）がある" % "、".join(sorted(extra))
    ov = smcad.get("override")
    if ov and ov.get("source") == "IMAGE":
        return "画像で色を差し替えている"
    return None


# --------------------------------------------------------------------------------------------
# Mesh data as arrays
# --------------------------------------------------------------------------------------------

class _MeshInfo:
    def __init__(self, mesh, slot_materials):
        self.mesh = mesh
        n_poly = len(mesh.polygons)
        self.loop_start = np.empty(n_poly, np.int64)
        self.loop_total = np.empty(n_poly, np.int64)
        self.mat_index = np.empty(n_poly, np.int64)
        mesh.polygons.foreach_get("loop_start", self.loop_start)
        mesh.polygons.foreach_get("loop_total", self.loop_total)
        mesh.polygons.foreach_get("material_index", self.mat_index)
        uv_layer = mesh.uv_layers[0] if len(mesh.uv_layers) else None
        self.uv = np.zeros((len(mesh.loops), 2), np.float64)
        if uv_layer is not None:
            flat = np.empty(len(mesh.loops) * 2, np.float32)
            uv_layer.data.foreach_get("uv", flat)
            self.uv = flat.reshape(-1, 2).astype(np.float64)
        self.slot_materials = slot_materials
        self.loop_poly = np.repeat(np.arange(n_poly), self.loop_total)
        if n_poly:
            order = self.loop_start
            self.pmin = np.minimum.reduceat(self.uv, order, axis=0) if len(self.uv) else np.zeros((n_poly, 2))
            self.pmax = np.maximum.reduceat(self.uv, order, axis=0) if len(self.uv) else np.zeros((n_poly, 2))
        else:
            self.pmin = self.pmax = np.zeros((0, 2))
        self.offset = np.floor(self.pmin + EPS)
        self.span = (self.pmax - self.offset).max(axis=1) if n_poly else np.zeros(0)


# --------------------------------------------------------------------------------------------
# Regions
# --------------------------------------------------------------------------------------------

def _components(mask):
    """4-connected components of a bool grid -> (labels, [(y0, x0, y1, x1)])."""
    labels = np.full(mask.shape, -1, np.int64)
    boxes = []
    h, w = mask.shape
    for sy, sx in zip(*np.nonzero(mask)):
        if labels[sy, sx] >= 0:
            continue
        k = len(boxes)
        labels[sy, sx] = k
        stack = [(sy, sx)]
        y0 = y1 = sy
        x0 = x1 = sx
        while stack:
            y, x = stack.pop()
            y0, y1, x0, x1 = min(y0, y), max(y1, y), min(x0, x), max(x1, x)
            for ny, nx in ((y + 1, x), (y - 1, x), (y, x + 1), (y, x - 1)):
                if 0 <= ny < h and 0 <= nx < w and mask[ny, nx] and labels[ny, nx] < 0:
                    labels[ny, nx] = k
                    stack.append((ny, nx))
        boxes.append((y0, x0, y1, x1))
    return labels, boxes


def _pack(sizes, width):
    """Shelf packing, tallest first. sizes: [(w, h)] -> (positions [(x, y)], used height) or None."""
    order = sorted(range(len(sizes)), key=lambda i: (-sizes[i][1], -sizes[i][0]))
    pos = [None] * len(sizes)
    x = y = shelf = 0
    for i in order:
        w, h = sizes[i]
        if w > width:
            return None
        if x + w > width:
            y += shelf
            x = shelf = 0
        pos[i] = (x, y)
        x += w
        shelf = max(shelf, h)
    return pos, y + shelf


def _pow2(n):
    return 1 << max(0, int(math.ceil(math.log2(max(n, 1)))))


# --------------------------------------------------------------------------------------------
# Pixels
# --------------------------------------------------------------------------------------------

def _pixels(image, cache):
    arr = cache.get(image)
    if arr is None:
        w, h = image.size
        flat = np.empty(w * h * 4, np.float32)
        image.pixels.foreach_get(flat)
        arr = flat.reshape(h, w, 4)
        cache[image] = arr
    return arr


def _sample(src, u, v):
    """Bilinear sample with repeat wrapping. src (h, w, 4); u, v arrays in texture uv (0..1 = one tile)."""
    h, w = src.shape[:2]
    x = u * w - 0.5
    y = v * h - 0.5
    x0 = np.floor(x)
    y0 = np.floor(y)
    fx = (x - x0)[..., None]
    fy = (y - y0)[..., None]
    x0 = x0.astype(np.int64) % w
    y0 = y0.astype(np.int64) % h
    x1 = (x0 + 1) % w
    y1 = (y0 + 1) % h
    top = src[y0, x0] * (1 - fx) + src[y0, x1] * fx
    bottom = src[y1, x0] * (1 - fx) + src[y1, x1] * fx
    return top * (1 - fy) + bottom * fy


def _resample(src, u0, v0, u1, v1, dw, dh):
    """Region (u0, v0)-(u1, v1) of src resampled to dw x dh, supersampled when shrinking."""
    h, w = src.shape[:2]
    k = int(min(4, max(1, math.ceil(max((u1 - u0) * w / dw, (v1 - v0) * h / dh)))))
    acc = np.zeros((dh, dw, 4), np.float32)
    xs = np.arange(dw, dtype=np.float64)
    ys = np.arange(dh, dtype=np.float64)
    for sy in range(k):
        for sx in range(k):
            u = u0 + (xs + (sx + 0.5) / k) / dw * (u1 - u0)
            v = v0 + (ys + (sy + 0.5) / k) / dh * (v1 - v0)
            uu, vv = np.meshgrid(u, v)
            acc += _sample(src, uu, vv)
    return acc / (k * k)


def _save_png(pixels, path, name, non_color):
    h, w = pixels.shape[:2]
    img = bpy.data.images.new(name, w, h, alpha=True)
    try:
        if non_color:
            img.colorspace_settings.name = "Non-Color"
        img.pixels.foreach_set(np.clip(pixels, 0.0, 1.0).astype(np.float32).ravel())
        img.filepath_raw = path
        img.file_format = "PNG"
        img.save()
    finally:
        bpy.data.images.remove(img)


# --------------------------------------------------------------------------------------------
# Build / restore
# --------------------------------------------------------------------------------------------

class Atlas:
    """Builds the atlas, swaps the meshes for edited copies; restore() puts everything back."""

    def __init__(self):
        self.swapped = []          # (object, original mesh)
        self.copies = []
        self.loop_rows = {}        # mesh copy -> atlas row per loop (goes into the "SM_Part" UV, v)
        self.material = None
        self.entry = None          # JSON for the atlas material
        self.included = []         # names of the materials that went in
        self.size = (0, 0)
        self.scale = 1.0

    def restore(self):
        for obj, original in self.swapped:
            obj.data = original
        for mesh in self.copies:
            if mesh.users == 0:
                bpy.data.meshes.remove(mesh)
        if self.material is not None and self.material.users == 0:
            bpy.data.materials.remove(self.material)
        self.swapped, self.copies, self.material, self.loop_rows = [], [], None, {}


def build(meshes, smcad_of, tex_dir, folder_name, stem, report, max_size=2048):
    """meshes: mesh objects to export. smcad_of(material) -> the material's "smcad" export data.
    Returns an Atlas (already swapped in) or None when nothing could go in."""
    report_skip = {}

    # ---- which materials could go in (per material), and the meshes that use them
    by_mesh = {}
    for obj in meshes:
        if any(slot.link == "OBJECT" for slot in obj.material_slots):
            report.add("info", obj.name, "マテリアルをオブジェクトに付けているため、アトラスに入れませんでした")
            continue
        if obj.data not in by_mesh:
            by_mesh[obj.data] = []
        by_mesh[obj.data].append(obj)

    smcad_cache = {}
    candidates = set()
    for mesh in by_mesh:
        for mat in mesh.materials:
            if mat is None or mat in smcad_cache:
                continue
            smcad_cache[mat] = smcad_of(mat)
            why = reason_to_skip(mat, smcad_cache[mat])
            if why is None:
                candidates.add(mat)
            else:
                report_skip[mat.name] = why

    infos = {mesh: _MeshInfo(mesh, list(mesh.materials)) for mesh in by_mesh}
    for mesh, info in infos.items():
        for i, mat in enumerate(info.slot_materials):
            if mat in candidates and (len(info.uv) == 0 and (info.mat_index == i).any()):
                candidates.discard(mat)
                report_skip[mat.name] = "UV がない"
            elif mat in candidates and not _solid(mat) and (info.span[info.mat_index == i] > 1.0 + EPS).any():
                candidates.discard(mat)
                report_skip[mat.name] = "テクスチャを繰り返して貼っている面がある"
    for name, why in sorted(report_skip.items()):
        report.add("info", name, "アトラスに入れませんでした（%s）" % why)
    if not candidates:
        return None

    # ---- texture sets (materials with the same textures share their regions)
    rows = sorted(candidates, key=lambda m: m.name)
    row_of = {m: i for i, m in enumerate(rows)}
    sets = {}
    for mat in rows:
        imgs = _images(mat)
        key = tuple(imgs[r].name if r in imgs else "" for r, _n in ROLES)
        sets.setdefault(key, {"images": imgs, "polys": [], "solid": _solid(mat)})
    for mesh, info in infos.items():
        for i, mat in enumerate(info.slot_materials):
            if mat in candidates:
                imgs = _images(mat)
                key = tuple(imgs[r].name if r in imgs else "" for r, _n in ROLES)
                sel = np.nonzero(info.mat_index == i)[0]
                if len(sel):
                    sets[key]["polys"].append((mesh, sel))

    regions = []                   # dicts: set, uv rect, ref size
    poly_region = {}               # (mesh, poly) -> region index
    for key, s in sets.items():
        imgs = s["images"]
        if s["solid"]:
            regions.append({"images": imgs, "uv": (0.0, 0.0, 1.0, 1.0), "px": (4, 4), "solid": True})
            for mesh, sel in s["polys"]:
                for p in sel:
                    poly_region[(mesh, int(p))] = len(regions) - 1
            continue
        ref = imgs.get("dif") or max(imgs.values(), key=lambda im: im.size[0] * im.size[1])
        w, h = ref.size
        gw, gh = max(1, math.ceil(w / CELL)), max(1, math.ceil(h / CELL))
        mask = np.zeros((gh, gw), bool)
        cells = []
        for mesh, sel in s["polys"]:
            info = infos[mesh]
            lo = info.pmin[sel] - info.offset[sel]
            hi = info.pmax[sel] - info.offset[sel]
            cx0 = np.clip(np.floor(lo[:, 0] * gw).astype(np.int64), 0, gw - 1)
            cy0 = np.clip(np.floor(lo[:, 1] * gh).astype(np.int64), 0, gh - 1)
            cx1 = np.clip(np.ceil(hi[:, 0] * gw).astype(np.int64) - 1, cx0, gw - 1)
            cy1 = np.clip(np.ceil(hi[:, 1] * gh).astype(np.int64) - 1, cy0, gh - 1)
            for a, b, c, d in zip(cx0, cy0, cx1, cy1):
                mask[b:d + 1, a:c + 1] = True
            cells.append((mesh, sel, cx0, cy0))
        labels, boxes = _components(mask)
        base = len(regions)
        for (y0, x0, y1, x1) in boxes:
            u0 = x0 / gw - MARGIN / w
            u1 = (x1 + 1) / gw + MARGIN / w
            v0 = y0 / gh - MARGIN / h
            v1 = (y1 + 1) / gh + MARGIN / h
            regions.append({"images": imgs, "uv": (u0, v0, u1, v1), "px": ((u1 - u0) * w, (v1 - v0) * h)})
        for mesh, sel, cx0, cy0 in cells:
            lab = labels[cy0, cx0] + base
            for p, r in zip(sel, lab):
                poly_region[(mesh, int(p))] = int(r)

    # ---- packing: the smallest square-ish atlas that holds everything at full size, else shrink
    scale = 1.0
    while True:
        sizes = [(max(2, int(math.ceil(r["px"][0] * scale))), max(2, int(math.ceil(r["px"][1] * scale)))) for r in regions]
        placed = None
        width = 256
        while width <= max_size:
            res = _pack(sizes, width)
            if res is not None and res[1] <= width:
                placed = (res[0], width, _pow2(res[1]))
                break
            width *= 2
        if placed is not None:
            break
        scale *= 0.9
    positions, aw, ah = placed
    if scale < 1.0:
        report.add("info", "", "アトラスに入りきらないため、テクスチャを %d%% に縮めました" % round(scale * 100))

    # ---- atlas pixels
    cache = {}
    atlas_px = {}
    for role, _n in ROLES:
        if not any(role in r["images"] for r in regions):
            continue
        out = np.empty((ah, aw, 4), np.float32)
        out[:] = FILL[role]
        for r, (x, y), (dw, dh) in zip(regions, positions, sizes):
            img = r["images"].get(role)
            if img is None:
                continue
            u0, v0, u1, v1 = r["uv"]
            out[y:y + dh, x:x + dw] = _resample(_pixels(img, cache), u0, v0, u1, v1, dw, dh)
        atlas_px[role] = out
    textures = {}
    for role, px in atlas_px.items():
        rel = "%s/%s/%s_atlas.png" % (folder_name, role, export_safe(stem))
        path = os.path.join(os.path.dirname(tex_dir), rel.replace("/", os.sep))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        _save_png(px, path, "SMCAD_Atlas_%s" % role, role != "dif")
        textures[role] = rel

    # ---- edited mesh copies
    atlas = Atlas()
    atlas.material = bpy.data.materials.new(ATLAS_MATERIAL)
    atlas.material["sm_shader"] = 1
    atlas.size = (aw, ah)
    atlas.scale = scale
    for mesh, objs in by_mesh.items():
        info = infos[mesh]
        in_atlas = np.array([m in candidates for m in info.slot_materials] or [False], bool)
        poly_in = in_atlas[np.clip(info.mat_index, 0, len(in_atlas) - 1)] if len(info.mat_index) else np.zeros(0, bool)
        if not poly_in.any():
            continue
        copy = mesh.copy()
        atlas.copies.append(copy)
        # materials: the atlas first, then the ones that stay
        keep = [m for m in info.slot_materials if m not in candidates]
        remap = np.array([0 if m in candidates else 1 + keep.index(m) for m in info.slot_materials] or [0], np.int64)
        copy.materials.clear()
        copy.materials.append(atlas.material)
        for m in keep:
            copy.materials.append(m)
        new_index = remap[np.clip(info.mat_index, 0, len(remap) - 1)]
        copy.polygons.foreach_set("material_index", new_index.astype(np.int32))

        # uvs: every face of the atlas goes into its region
        uv = info.uv.copy()
        row = np.zeros(len(info.mat_index), np.float32)
        for p in np.nonzero(poly_in)[0]:
            r = poly_region[(mesh, int(p))]
            reg = regions[r]
            x, y = positions[r]
            dw, dh = sizes[r]
            u0, v0, u1, v1 = reg["uv"]
            a, n = info.loop_start[p], info.loop_total[p]
            if reg.get("solid"):
                uv[a:a + n, 0] = (x + dw * 0.5) / aw
                uv[a:a + n, 1] = (y + dh * 0.5) / ah
                row[p] = row_of[info.slot_materials[info.mat_index[p]]]
                continue
            local = uv[a:a + n] - info.offset[p]
            uv[a:a + n, 0] = (x + (local[:, 0] - u0) / (u1 - u0) * dw) / aw
            uv[a:a + n, 1] = (y + (local[:, 1] - v0) / (v1 - v0) * dh) / ah
            row[p] = row_of[info.slot_materials[info.mat_index[p]]]
        copy.uv_layers[0].data.foreach_set("uv", uv.astype(np.float32).ravel())
        atlas.loop_rows[copy] = row[info.loop_poly]
        for obj in objs:
            atlas.swapped.append((obj, obj.data))
            obj.data = copy

    atlas.included = [m.name for m in rows]
    atlas.entry = {
        "sm_shader": 1, "textures": textures, "smcad": {},
        "atlas": {"size": [aw, ah], "scale": round(scale, 4),
                  "rows": [{"material": m.name, "smcad": smcad_cache[m]} for m in rows]},
    }
    report.add("info", "", "アトラス %d×%d: %d マテリアルを 1 つにまとめました（領域 %d）" % (aw, ah, len(rows), len(regions)))
    return atlas


def export_safe(name):
    import re
    return re.sub(r"[^0-9A-Za-z_.-]+", "_", name).strip("_") or "item"
