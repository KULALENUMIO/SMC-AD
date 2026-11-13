# SPDX-License-Identifier: GPL-3.0-or-later
"""Slider properties for the surface values (S7).

The values live in the Value / RGB nodes "p_<key>" inside each surface group, so hand-shaders see them
in the node editor. These properties read and write those nodes directly (no copy), and add the
ranges that make them proper sliders in the panel: ShaderNodeTree.smcadp_values.<key>.
"""

import bpy
from bpy.props import FloatProperty, FloatVectorProperty

from . import surfaces

# key -> (plain label, technical name, min, max, soft max)
UI = {
    "metallic": ("金属っぽさ（地）", "Metallic", 0.0, 1.0, 1.0),
    "paint_metallic": ("金属っぽさ（塗装）", "Paint Metallic", 0.0, 1.0, 1.0),
    "roughness_offset": ("ざらつき", "Roughness +", -1.0, 1.0, 1.0),
    "coat": ("塗装の厚み", "Coat", 0.0, 1.0, 1.0),
    "tint": ("色味", "Tint", 0.0, 1.0, 1.0),
    "tint_strength": ("色味の強さ", "Tint Strength", 0.0, 1.0, 1.0),
    "roughness_scale": ("ざらつきの倍率", "Roughness ×", 0.0, 4.0, 2.0),
    "specular": ("反射の強さ", "Specular ×", 0.0, 4.0, 2.0),
    "coat_roughness": ("塗装面のざらつき", "Coat Roughness", 0.0, 1.0, 1.0),
    "normal_strength": ("凹凸の強さ", "Normal", 0.0, 5.0, 3.0),
    "noise": ("ムラ", "Noise", 0.0, 1.0, 1.0),
    "noise_scale": ("ムラの細かさ", "Noise Scale", 0.1, 200.0, 50.0),
}
MAIN = ["metallic", "paint_metallic", "roughness_offset", "coat", "tint", "tint_strength"]
MORE = ["roughness_scale", "specular", "coat_roughness", "normal_strength", "noise", "noise_scale"]


def _node(self, key):
    return self.id_data.nodes.get("p_" + key) if self.id_data is not None else None


def _float_getset(key):
    def get(self):
        node = _node(self, key)
        return float(node.outputs[0].default_value) if node is not None else 0.0

    def set(self, value):
        node = _node(self, key)
        if node is not None:
            node.outputs[0].default_value = value
    return get, set


def _color_getset(key):
    def get(self):
        node = _node(self, key)
        return tuple(node.outputs[0].default_value)[:3] if node is not None else (1.0, 1.0, 1.0)

    def set(self, value):
        node = _node(self, key)
        if node is not None:
            node.outputs[0].default_value = (value[0], value[1], value[2], 1.0)
    return get, set


def _make_class():
    annotations = {}
    for key, _sock, _label, kind, _lo, _hi in surfaces.PARAMS:
        label, tech, lo, hi, soft = UI[key]
        if kind == "color":
            get, set_ = _color_getset(key)
            annotations[key] = FloatVectorProperty(name=label, description="ノードでの名前: " + tech, subtype="COLOR", size=3,
                                                   min=0.0, max=1.0, get=get, set=set_)
        else:
            get, set_ = _float_getset(key)
            annotations[key] = FloatProperty(name=label, description="ノードでの名前: " + tech, min=lo, max=hi, soft_min=lo,
                                             soft_max=soft, get=get, set=set_,
                                             subtype="FACTOR" if (lo, hi) == (0.0, 1.0) else "NONE")
    return type("SMCADP_SurfaceValues", (bpy.types.PropertyGroup,), {"__annotations__": annotations})


SMCADP_SurfaceValues = _make_class()


def register():
    bpy.utils.register_class(SMCADP_SurfaceValues)
    bpy.types.ShaderNodeTree.smcadp_values = bpy.props.PointerProperty(type=SMCADP_SurfaceValues, name="SMC-AD+ 材質の値")


def unregister():
    del bpy.types.ShaderNodeTree.smcadp_values
    bpy.utils.unregister_class(SMCADP_SurfaceValues)
