# SPDX-License-Identifier: GPL-3.0-or-later
"""Flavours (味付け): named value sets inside one surface type, e.g. "新品の塗装" / "使い込んだ塗装" (S8),
and the user's own saved flavours, shared between .blend files (S11).

A flavour only lists the values it changes; the rest come from the surface type preset.
User flavours live in a JSON file in the user folder (same format as the "flavors" table of
surface_presets.json), so they can also be copied next to SMConverter / the Unity package.
"""

import json
import os
import re

import bpy

from . import surfaces

USER_FILE_NAME = "user_flavors.json"
TOLERANCE = 1e-4


def user_dir():
    override = os.environ.get("SMCADP_USER_DIR")
    if override:
        os.makedirs(override, exist_ok=True)
        return override
    try:
        path = bpy.utils.extension_path_user(__package__, create=True)
    except (ValueError, AttributeError, TypeError):
        path = os.path.join(bpy.utils.user_resource("CONFIG"), "smc_ad_surfaces")
        os.makedirs(path, exist_ok=True)
    _take_over_old_flavors(path)
    return path


OLD_ID = "smc_ad_plus_surfaces"   # the id before 0.4.0 (then named "SMC-AD+ Surfaces")


def _take_over_old_flavors(path):
    """0.4.0 renamed the extension (smc_ad_plus_surfaces -> smc_ad_surfaces), which moves its user folder.
    Copy the user's own flavours from the old folder once, so nothing is lost."""
    new_file = os.path.join(path, USER_FILE_NAME)
    if os.path.isfile(new_file):
        return
    for old_dir in (os.path.join(os.path.dirname(path), OLD_ID),
                    os.path.join(bpy.utils.user_resource("CONFIG"), OLD_ID)):
        old_file = os.path.join(old_dir, USER_FILE_NAME)
        if os.path.isfile(old_file):
            try:
                with open(old_file, encoding="utf-8") as src, open(new_file, "w", encoding="utf-8") as dst:
                    dst.write(src.read())
            except OSError:
                pass
            return


def user_file():
    return os.path.join(user_dir(), USER_FILE_NAME)


def _builtin():
    with open(surfaces.PRESET_FILE, encoding="utf-8") as f:
        return json.load(f).get("flavors", {})


def _load_user():
    path = user_file()
    if not os.path.isfile(path):
        return {}
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f).get("flavors", {})
    except (OSError, ValueError):
        return {}


def _save_user(table):
    with open(user_file(), "w", encoding="utf-8") as f:
        json.dump({"version": 1, "flavors": table}, f, ensure_ascii=False, indent=1)


def flavors_for(sid):
    """[{id, label, user, ...values}] for a surface type: built-in first, then the user's."""
    result = [dict(f, user=False) for f in _builtin().get(sid, [])]
    result += [dict(f, user=True) for f in _load_user().get(sid, [])]
    return result


def find(sid, fid):
    return next((f for f in flavors_for(sid) if f["id"] == fid), None)


def values_for(sid, flavor=None):
    """Full value set: the surface preset, overridden by the flavour."""
    values = dict(surfaces.default_presets().get(surfaces.NO_SURFACE, {}))
    values.update(surfaces.default_presets().get(sid, {}))
    if flavor:
        values.update({k: v for k, v in flavor.items() if k not in ("id", "label", "user")})
    return values


def apply(group, flavor):
    """Writes a flavour into a surface group (shared or a material's own)."""
    sid = group.get("smcadp_surface", surfaces.NO_SURFACE)
    values = values_for(sid, flavor)
    for key, _sock, _label, kind, _lo, _hi in surfaces.PARAMS:
        node = group.nodes.get("p_" + key)
        if node is not None:
            surfaces.set_param_node(node, kind, values.get(key))
    if flavor:
        group["smcadp_flavor"] = flavor["id"]
    elif "smcadp_flavor" in group:
        del group["smcadp_flavor"]


def current(group):
    """The flavour last applied to the group, or None."""
    fid = group.get("smcadp_flavor")
    return find(group.get("smcadp_surface", ""), fid) if fid else None


def is_modified(group):
    """True when the values differ from the flavour / preset they started from (●)."""
    sid = group.get("smcadp_surface", surfaces.NO_SURFACE)
    ref = values_for(sid, current(group))
    now = surfaces.group_values(group)
    for key, _sock, _label, kind, _lo, _hi in surfaces.PARAMS:
        a, b = now.get(key), ref.get(key)
        if a is None or b is None:
            continue
        if kind == "color":
            if any(abs(float(x) - float(y)) > TOLERANCE for x, y in zip(a, b)):
                return True
        elif abs(float(a) - float(b)) > TOLERANCE:
            return True
    return False


def save_user(sid, label, group):
    """Saves the group's current values as a named user flavour. Returns its id."""
    table = _load_user()
    entries = table.setdefault(sid, [])
    base = "user_" + (re.sub(r"[^0-9A-Za-z_]+", "_", label).strip("_").lower() or "flavor")
    fid, n = base, 2
    existing = {f["id"] for f in entries} | {f["id"] for f in _builtin().get(sid, [])}
    while fid in existing:
        fid, n = "%s_%d" % (base, n), n + 1
    entry = {"id": fid, "label": label}
    entry.update({k: v for k, v in surfaces.group_values(group).items() if k != "label"})
    entries.append(entry)
    _save_user(table)
    group["smcadp_flavor"] = fid
    return fid


def delete_user(sid, fid):
    table = _load_user()
    before = len(table.get(sid, []))
    table[sid] = [f for f in table.get(sid, []) if f["id"] != fid]
    if not table[sid]:
        del table[sid]
    _save_user(table)
    return before != len(table.get(sid, []))
