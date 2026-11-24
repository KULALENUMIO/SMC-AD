# SPDX-License-Identifier: MIT
"""Writes the HDRP Shader Graphs of the SMC-AD line: SM Lit / SM Glass (com.smcad.unity), Details Lit
(com.smcad.details) and the particle shader of the Tank Rigging plugin (sibling repo, if it is there).

The graphs are thin: blackboard properties (every material property, so they land in UnityPerMaterial for the
SRP Batcher), the geometry input nodes (UV0-3, UV5-7: HDRP keeps TEXCOORD4 for motion vectors), and Custom Function nodes calling Runtime/Shaders/SMGraph.hlsl, where the
SM maths live (pipeline-neutral SMSurface.hlsl / SMCommon.hlsl). Edit the property lists here, then run:
    python make_sm_graphs.py
Object ids are derived from names, so re-running gives the same files (clean diffs).
"""
import hashlib, json, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
SHADERS = os.path.join(HERE, '..', 'com.smcad.unity', 'Runtime', 'Shaders')
SMGRAPH_GUID = '5c3b7e1a9d2f4e60a8b1c7d3e9f20411'   # SMGraph.hlsl.meta
DETAILS_SHADERS = os.path.join(HERE, '..', 'com.smcad.details', 'Runtime', 'Shaders')
DETAILS_GUID = '7e5d9a3c1b4f4a82b6d0e2f4a8c61733'   # SMDetailsGraph.hlsl.meta (com.smcad.details)
GUI = 'SMCAD.Editor.SMLitGUI'
TANKRIG_SHADERS = os.path.join(HERE, '..', '..', '..', 'Unity Tank Rigging Addon', 'com.tankrig.unity', 'Runtime', 'Shaders')
PARTICLE_GUID = '8f6e0b4d2c5a4b93c7e1f3a5b9d72844'   # TankRigParticle.hlsl.meta (com.tankrig.unity)
RING_GUID = '3b8e5c1f7a2d4c69b0e4f6a8c2d1e957'   # TankRigRingInterior.hlsl.meta (com.tankrig.unity)
RING_PROPS = r'''
_InnerDiameter ("ターレットリングの内径（m）", Float) = 2.25
_RingWidth ("ターレットリングの幅（m）", Float) = 0.16
[HideInInspector] _Side ("面（0 = 車体側、1 = 砲塔側）", Float) = 0
_Brightness ("明るさ", Range(0, 4)) = 1
_PaintColor ("内装の塗装色", Color) = (0.74, 0.73, 0.68, 1)
_EquipColor ("機器の色", Color) = (0.36, 0.38, 0.29, 1)
_FloorColor ("床の色", Color) = (0.45, 0.46, 0.43, 1)
'''
PARTICLE_PROPS = r'''
_BaseColorMap ("Texture", 2D) = "white" {}
_BaseColor ("Tint", Color) = (1, 1, 1, 1)
'''

# ShaderLab property syntax: [attrs] _Name ("display", Type) = default
LIT_PROPS = r'''
_DifMap ("Dif（RGB＝色、A＝塗装マスク）", 2D) = "white" {}
_AsgMap ("Asg（R＝切り抜き、G＝反射、B＝発光、A＝つや）", 2D) = "gray" {}
[Normal] _NorMap ("Nor（ノーマルマップ）", 2D) = "bump" {}
[Toggle] _HasDif ("Dif あり", Float) = 0
[Toggle] _HasAsg ("Asg あり", Float) = 0
[Toggle] _HasNor ("Nor あり", Float) = 0
[Enum] _AlphaMode ("透明の扱い", Float) = 0
_Cutoff ("切り抜きのしきい値", Range(0, 1)) = 0.5
_SpecularMul ("反射の強さ", Range(0, 4)) = 1
_GlowMul ("発光の強さ", Range(0, 10)) = 1
[Toggle] _OvEnabled ("色を差し替える", Float) = 0
[Toggle] _OvWhole ("表面全体（オフ＝塗装だけ）", Float) = 0
_OvColor ("差し替える色", Color) = (0.8, 0.1, 0.1, 1)
[Toggle] _OvUseTex ("画像を使う", Float) = 0
_OvTex ("差し替える画像", 2D) = "white" {}
[Enum] _OvProjection ("貼り方", Float) = 0
_OvScale ("画像の大きさ（1メートルあたりの繰り返し）", Float) = 1
_OvStrength ("強さ", Range(0, 1)) = 1
[Toggle] _OvUseMatch ("1つの塗装色だけ", Float) = 0
_OvMatch ("差し替える塗装色", Color) = (0.5, 0.5, 0.5, 1)
_OvTolerance ("許容差", Range(0, 1)) = 0.02
[Toggle] _SfEnabled ("材質を使う", Float) = 0
_SfTexMetallic ("金属っぽさ（地）", Range(0, 1)) = 0
_SfPaintMetallic ("金属っぽさ（塗装）", Range(0, 1)) = 0
_SfRoughScale ("ざらつきの倍率", Range(0, 4)) = 1
_SfRoughOffset ("ざらつき", Range(-1, 1)) = 0
_SfSpecMul ("材質の反射の強さ", Range(0, 4)) = 1
_SfCoat ("塗装の厚み（クリアコート）", Range(0, 1)) = 0
_SfCoatRough ("塗装面のざらつき", Range(0, 1)) = 0.1
_SfNormalStrength ("凹凸の強さ", Range(0, 5)) = 1
_SfTint ("色味（リニアRGB）", Vector) = (1, 1, 1, 1)
_SfTintStrength ("色味の強さ", Range(0, 1)) = 0
_SfNoise ("ざらつきのムラ", Range(0, 1)) = 0
_SfNoiseScale ("ムラの細かさ", Float) = 10
_LBaseOp ("色：演算", Vector) = (0, -1, 0, 0)
_LBaseA ("色：A", Vector) = (0, 0, 0, 1)
_LBaseB ("色：B", Vector) = (0, 0, 0, 1)
_LBaseF ("色：係数", Float) = 0
_LMetalOp ("金属っぽさ：演算", Vector) = (0, -1, 0, 0)
_LMetalA ("金属っぽさ：A", Vector) = (0, 0, 0, 0)
_LMetalB ("金属っぽさ：B", Vector) = (0, 0, 0, 0)
_LMetalF ("金属っぽさ：係数", Float) = 0
_LRoughOp ("ざらつき：演算", Vector) = (0, -1, 0, 0)
_LRoughA ("ざらつき：A", Vector) = (0, 0, 0, 0)
_LRoughB ("ざらつき：B", Vector) = (0, 0, 0, 0)
_LRoughF ("ざらつき：係数", Float) = 0
_LSpecOp ("反射：演算", Vector) = (0, -1, 0, 0)
_LSpecA ("反射：A", Vector) = (0, 0, 0, 0)
_LSpecB ("反射：B", Vector) = (0, 0, 0, 0)
_LSpecF ("反射：係数", Float) = 0
_LEmisOp ("発光：演算", Vector) = (0, -1, 0, 0)
_LEmisA ("発光：A", Vector) = (0, 0, 0, 0)
_LEmisB ("発光：B", Vector) = (0, 0, 0, 0)
_LEmisF ("発光：係数", Float) = 0
[Toggle] _SmdEnabled ("汚しを使う", Float) = 0
[NoScaleOffset] _SmdGeo ("形の情報（3D）", 3D) = "" {}
[NoScaleOffset] _SmdNoise ("模様（3D）", 3D) = "" {}
[NoScaleOffset] _SmdPaint0 ("手描き：塗装ハゲ・傷・錆・くすみ（3D）", 3D) = "" {}
[NoScaleOffset] _SmdPaint1 ("手描き：泥・土・砂・濡れ（3D）", 3D) = "" {}
_SmdGridMin ("格子の始点", Vector) = (0, 0, 0, 0)
_SmdGridInv ("格子の大きさの逆数", Vector) = (1, 1, 1, 0)
_SmdAmount0 ("量：塗装ハゲ・傷・錆・くすみ", Vector) = (0.35, 0.2, 0.15, 0.4)
_SmdAmount1 ("量：泥・土・砂・濡れ", Vector) = (0.4, 0.3, 0.2, 0)
_SmdScale0 ("模様の細かさ：塗装ハゲ・傷・錆・くすみ", Vector) = (6, 3, 4, 3)
_SmdScale1 ("模様の細かさ：泥・土・砂", Vector) = (5, 2, 6, 1)
_SmdHeights ("地面からの高さ：泥・土・砂、地面", Vector) = (0.6, 1.2, 0.3, 0)
_SmdGen0 ("角の強さ、くぼみの強さ、近い/遠い調べ位置(m)", Vector) = (4, 4, 0.03, 0.12)
_SmdGen1 ("細かい模様の倍率、境目のぼかし、手描きあり、乱数", Vector) = (1, 0.06, 0, 0)
_SmdView ("表示（0 = 仕上がり）", Float) = 0
_SmdEdge ("角の幅(m)、へこんだ角の幅(m)、角のデータあり", Vector) = (0.03, 0.04, 0, 0)
_SmdKick ("履帯の巻き上げ：範囲(m)、量", Vector) = (0.6, 0.8, 0, 0)
_SmdBurn ("焦げ・すすの量", Vector) = (1, 0, 0, 0)
_SmdBurnColor ("焦げ・すす（リニアRGB）", Vector) = (0.015, 0.013, 0.012, 1)
[HideInInspector] _SmdTrackMin0 ("履帯の範囲0 最小", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdTrackMax0 ("履帯の範囲0 最大", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdTrackMin1 ("履帯の範囲1 最小", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdTrackMax1 ("履帯の範囲1 最大", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdSrcP0 ("すすの元0 位置", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdSrcP1 ("すすの元1 位置", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdSrcP2 ("すすの元2 位置", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdSrcP3 ("すすの元3 位置", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdSrcD0 ("すすの元0 向き", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdSrcD1 ("すすの元1 向き", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdSrcD2 ("すすの元2 向き", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdSrcD3 ("すすの元3 向き", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdLinkSeed ("履帯の乱数", Float) = 0
[HideInInspector] _SmdBase0 ("基本の汚し0", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdBase1 ("基本の汚し1", Vector) = (0, 0, 0, 0)
[HideInInspector] _SmdBase2 ("基本の汚し2", Vector) = (1.5, 12, 0.5, 3)
_SmdStrength0 ("強さ：塗装ハゲ・傷・錆・くすみ", Vector) = (1, 1, 1, 1)
_SmdStrength1 ("強さ：泥・土・砂・濡れ", Vector) = (1, 1, 1, 1)
_SmdPrimer ("下地（プライマー、リニアRGB）", Vector) = (0.32, 0.12, 0.07, 1)
_SmdMetal ("地金（リニアRGB）", Vector) = (0.45, 0.45, 0.46, 1)
_SmdRustA ("錆・明るい（リニアRGB）", Vector) = (0.38, 0.13, 0.04, 1)
_SmdRustB ("錆・濃い（リニアRGB）", Vector) = (0.12, 0.05, 0.025, 1)
_SmdGrime ("くすみ・すす（リニアRGB）", Vector) = (0.18, 0.15, 0.12, 1)
_SmdMud ("泥（リニアRGB）", Vector) = (0.09, 0.065, 0.045, 1)
_SmdSoil ("土（リニアRGB）", Vector) = (0.25, 0.18, 0.12, 1)
_SmdSand ("砂（リニアRGB）", Vector) = (0.55, 0.45, 0.32, 1)
_SmdParams0 ("ざらつき：地金・錆・泥、塗装していない所の塗装ハゲ", Vector) = (0.35, 0.9, 0.7, 0.3)
_SmdParams1 ("濡れた所の明るさ、凹凸の強さ、凹凸の深さ(m)", Vector) = (0.55, 0.6, 0.004, 0)
[HideInInspector] _Transparency ("透明度", Float) = 0
[HideInInspector][NoScaleOffset] _SMParams ("アトラスの設定表", 2D) = "black" {}
'''

# Details Lit (com.smcad.details): HDRP/Lit property names, so copies of HDRP/Lit materials keep their values
DETAILS_BASE_PROPS = r'''
_BaseColorMap ("基本の色（画像）", 2D) = "white" {}
_BaseColor ("基本の色", Color) = (1, 1, 1, 1)
_Metallic ("金属っぽさ", Range(0, 1)) = 0
_Smoothness ("なめらかさ", Range(0, 1)) = 0.5
[NoScaleOffset] _MaskMap ("マスク（R 金属、G 遮蔽、A なめらかさ）", 2D) = "white" {}
[Toggle] _HasMask ("マスクあり", Float) = 0
_MetallicRemapMin ("金属っぽさ（マスク）最小", Range(0, 1)) = 0
_MetallicRemapMax ("金属っぽさ（マスク）最大", Range(0, 1)) = 1
_SmoothnessRemapMin ("なめらかさ（マスク）最小", Range(0, 1)) = 0
_SmoothnessRemapMax ("なめらかさ（マスク）最大", Range(0, 1)) = 1
_AORemapMin ("遮蔽（マスク）最小", Range(0, 1)) = 0
_AORemapMax ("遮蔽（マスク）最大", Range(0, 1)) = 1
[Normal][NoScaleOffset] _NormalMap ("ノーマルマップ", 2D) = "bump" {}
_NormalScale ("ノーマルの強さ", Float) = 1
[Toggle] _SmdBelt ("履帯（土汚れだけ、部品の中の座標）", Float) = 0
'''

# SM Glass reads the same SMCommon constants: the ones it does not show stay hidden (defaults are fine)
GLASS_SHOWN = ['_DifMap', '_AsgMap', '_NorMap', '_HasDif', '_HasAsg', '_HasNor', '_Transparency', '_OvEnabled', '_OvWhole',
               '_OvColor', '_OvUseTex', '_OvTex', '_OvProjection', '_OvScale', '_OvStrength', '_OvUseMatch', '_OvMatch', '_OvTolerance']
GLASS_OVERRIDES = {'_Transparency': '_Transparency ("透明度", Range(0, 1)) = 0.7',
                   '_AsgMap': '_AsgMap ("Asg（G＝反射、A＝つや）", 2D) = "gray" {}'}

PROP_RE = re.compile(r'^((?:\[[^\]]*\])*)\s*(\w+)\s*\("([^"]*)",\s*([^)]+\)?)\)\s*=\s*(.+)$')


def parse(text):
    out = []
    for line in text.strip().splitlines():
        m = PROP_RE.match(line.strip())
        if not m:
            raise ValueError('cannot parse: ' + line)
        attrs, name, disp, typ, default = m.groups()
        out.append(dict(attrs=re.findall(r'\[([^\]]*)\]', attrs), name=name, disp=disp, type=typ.strip(), default=default.strip()))
    return out


class Graph:
    def __init__(self, key):
        self.key = key
        self.objs = []

    def oid(self, *parts):
        return hashlib.md5(('|'.join((self.key,) + parts)).encode('utf-8')).hexdigest()

    def guid(self, *parts):
        h = self.oid('guid', *parts)
        return f'{h[:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:]}'

    def add(self, o):
        self.objs.append(o)
        return o['m_ObjectId']

    # ------------------------------------------------------------ properties
    def prop(self, p, hidden=False):
        base = {
            'm_Guid': {'m_GuidSerialized': self.guid('prop', p['name'])},
            'm_Name': p['disp'], 'm_DefaultRefNameVersion': 1, 'm_RefNameGeneratedByDisplayName': p['disp'],
            'm_DefaultReferenceName': p['name'], 'm_OverrideReferenceName': p['name'], 'm_GeneratePropertyBlock': True,
            'm_UseCustomSlotLabel': False, 'm_CustomSlotLabel': '', 'm_DismissedVersion': 0, 'm_Precision': 0,
            'overrideHLSLDeclaration': False, 'hlslDeclarationOverride': 0,
            'm_Hidden': hidden or 'HideInInspector' in p['attrs'],
        }
        t, d, oid = p['type'], p['default'], self.oid('prop', p['name'])
        nums = [float(x) for x in re.findall(r'-?[\d.]+', d)]
        if t == '2D':
            kind = d.split('"')[1]
            o = {'m_SGVersion': 0, 'm_Type': 'UnityEditor.ShaderGraph.Internal.Texture2DShaderProperty', 'm_ObjectId': oid, **base,
                 'm_Value': {'m_SerializedTexture': '{"texture":{"instanceID":0}}', 'm_Guid': ''},
                 'isMainTexture': p['name'] in ('_DifMap', '_BaseColorMap'), 'useTilingAndOffset': p['name'] in ('_DifMap', '_BaseColorMap'), 'm_Modifiable': True,
                 'm_DefaultType': {'white': 0, 'black': 1, 'gray': 2, 'grey': 2, 'bump': 3}[kind]}
        elif t == '3D':
            o = {'m_SGVersion': 0, 'm_Type': 'UnityEditor.ShaderGraph.Internal.Texture3DShaderProperty', 'm_ObjectId': oid, **base,
                 'm_PerRendererData': False, 'm_customAttributes': [], 'm_Value': {'m_SerializedTexture': '', 'm_Guid': ''}, 'm_Modifiable': True}
        elif t == 'Float' and 'Toggle' in p['attrs']:
            o = {'m_SGVersion': 0, 'm_Type': 'UnityEditor.ShaderGraph.Internal.BooleanShaderProperty', 'm_ObjectId': oid, **base,
                 'm_Value': nums[0] > 0.5}
        elif t == 'Float' or t.startswith('Range'):
            rng = [float(x) for x in re.findall(r'-?[\d.]+', t)] if t.startswith('Range') else [0.0, 1.0]
            o = {'m_SGVersion': 1, 'm_Type': 'UnityEditor.ShaderGraph.Internal.Vector1ShaderProperty', 'm_ObjectId': oid, **base,
                 'm_Value': nums[0], 'm_FloatType': 1 if t.startswith('Range') else 0, 'm_RangeValues': {'x': rng[0], 'y': rng[1]}}
        elif t == 'Vector':
            o = {'m_SGVersion': 1, 'm_Type': 'UnityEditor.ShaderGraph.Internal.Vector4ShaderProperty', 'm_ObjectId': oid, **base,
                 'm_PerRendererData': False, 'm_customAttributes': [], 'm_Value': dict(zip('xyzw', nums))}
        elif t == 'Color':
            o = {'m_SGVersion': 3, 'm_Type': 'UnityEditor.ShaderGraph.Internal.ColorShaderProperty', 'm_ObjectId': oid, **base,
                 'm_Value': dict(zip('rgba', nums)), 'isMainColor': False, 'm_ColorMode': 0}
        else:
            raise ValueError(t)
        return self.add(o)

    def keyword(self, ref, disp):
        return self.add({
            'm_SGVersion': 1, 'm_Type': 'UnityEditor.ShaderGraph.ShaderKeyword', 'm_ObjectId': self.oid('kw', ref),
            'm_Guid': {'m_GuidSerialized': self.guid('kw', ref)}, 'm_Name': disp, 'm_DefaultRefNameVersion': 1,
            'm_RefNameGeneratedByDisplayName': disp, 'm_DefaultReferenceName': ref, 'm_OverrideReferenceName': ref,
            'm_GeneratePropertyBlock': False, 'm_UseCustomSlotLabel': False, 'm_CustomSlotLabel': '', 'm_DismissedVersion': 0,
            'm_KeywordType': 0, 'm_KeywordDefinition': 0, 'm_KeywordScope': 0, 'm_KeywordStages': 63,
            'm_Entries': [], 'm_Value': 0, 'm_IsEditable': True})

    # ------------------------------------------------------------ nodes and slots
    def slot(self, owner, sid, kind, name, out, value=None, space=None, disp=None, cap=3):
        o = {'m_SGVersion': 0, 'm_Type': 'UnityEditor.ShaderGraph.' + kind, 'm_ObjectId': self.oid('slot', owner, str(sid)),
             'm_Id': sid, 'm_DisplayName': disp or name, 'm_SlotType': 1 if out else 0, 'm_Hidden': False,
             'm_ShaderOutputName': name, 'm_StageCapability': cap}
        if kind == 'BooleanMaterialSlot':
            o.update({'m_Value': False, 'm_DefaultValue': False})
        elif kind == 'Vector1MaterialSlot':
            v = 0.0 if value is None else value
            o.update({'m_Value': v, 'm_DefaultValue': v, 'm_Labels': []})
        else:
            n = {'Vector2MaterialSlot': 2, 'Vector4MaterialSlot': 4}.get(kind, 3)
            v = dict(zip('xyzw', (value or [0.0] * n)))
            o.update({'m_Value': v, 'm_DefaultValue': dict(zip('xyzw', [0.0] * n)), 'm_Labels': []})
            if kind == 'ColorRGBMaterialSlot':
                hdr = name == 'Emission'
                o.update({'m_ColorMode': 1 if hdr else 0,
                          'm_DefaultColor': {'r': v['x'], 'g': v['y'], 'b': v['z'], 'a': 1.0}})
            if space is not None:
                o['m_Space'] = space
        return self.add(o)

    def node(self, key, kind, name, slots, pos, sgver=0, **extra):
        o = {'m_SGVersion': sgver, 'm_Type': 'UnityEditor.ShaderGraph.' + kind, 'm_ObjectId': self.oid('node', key),
             'm_Group': {'m_Id': ''}, 'm_Name': name,
             'm_DrawState': {'m_Expanded': True, 'm_Position': {'serializedVersion': '2', 'x': pos[0], 'y': pos[1], 'width': 0.0, 'height': 0.0}},
             'm_Slots': [{'m_Id': s} for s in slots], 'synonyms': [], 'm_Precision': 0, 'm_PreviewExpanded': False,
             'm_DismissedVersion': 0, 'm_PreviewMode': 0, 'm_CustomColors': {'m_SerializableColors': []}}
        o.update(extra)
        return self.add(o)

    def input_node(self, key, kind, name, out_kind, pos, sgver=0, **extra):
        s = self.slot('in_' + key, 0, out_kind, 'Out', True)
        return self.node('in_' + key, kind, name, [s], pos, sgver, **extra)

    def custom_function(self, key, fn, inputs, outputs, pos, source):
        slots = []
        for i, (n, k) in enumerate(inputs):
            slots.append(self.slot('cf_' + key, i, k, n, False))
        for j, (n, k) in enumerate(outputs):
            slots.append(self.slot('cf_' + key, len(inputs) + j, k, n, True))
        return self.node('cf_' + key, 'CustomFunctionNode', fn + ' (Custom Function)', slots, pos, sgver=1,
                         m_SourceType=0, m_FunctionName=fn, m_FunctionSource=source, m_FunctionBody='')

    def block(self, desc, kind, name, disp, value=None, space=None, stage=2):
        s = self.slot('blk_' + desc, 0, kind, name, False, value=value, space=space, disp=disp, cap=stage)
        return self.node('blk_' + desc, 'BlockNode', desc, [s], (0.0, 0.0), m_SerializedDescriptor=desc)


def edge(out_node, out_slot, in_node, in_slot):
    return {'m_OutputSlot': {'m_Node': {'m_Id': out_node}, 'm_SlotId': out_slot},
            'm_InputSlot': {'m_Node': {'m_Id': in_node}, 'm_SlotId': in_slot}}


V1, V3, V4, B = 'Vector1MaterialSlot', 'Vector3MaterialSlot', 'Vector4MaterialSlot', 'BooleanMaterialSlot'


def build(kind):
    """kind: 'lit' (SM Lit), 'glass' (SM Glass) or 'details' (Details Lit of com.smcad.details)."""
    glass, details = kind == 'glass', kind == 'details'
    g = Graph({'lit': 'SM Lit', 'glass': 'SM Glass', 'details': 'Details Lit'}[kind])
    props = parse(LIT_PROPS)
    if glass:
        props = [parse(GLASS_OVERRIDES[p['name']])[0] if p['name'] in GLASS_OVERRIDES else p for p in props]
        props.sort(key=lambda p: GLASS_SHOWN.index(p['name']) if p['name'] in GLASS_SHOWN else 999)
        prop_ids = [g.prop(p, hidden=p['name'] not in GLASS_SHOWN) for p in props]
        kw_ids = []
    elif details:
        props = parse(DETAILS_BASE_PROPS) + [p for p in props if p['name'].startswith('_Smd')]
        prop_ids = [g.prop(p) for p in props]
        kw_ids = [g.keyword('_SMCAD_DETAILS', '汚し（SMC-DETAILS）'), g.keyword('_SMD_BELT', '履帯')]
    else:
        prop_ids = [g.prop(p) for p in props]
        kw_ids = [g.keyword('_SMCAD_ATLAS', 'アトラス'), g.keyword('_SMCAD_DETAILS', '汚し（SMC-DETAILS）')]

    # geometry inputs (HDRP: no UV4, the motion vector pass owns TEXCOORD4)
    x = -1400.0
    channels = {'lit': [0, 1, 2, 3, 5, 6, 7], 'glass': [0, 3], 'details': [0, 1, 5, 6, 7]}[kind]
    def uv(ch, y): return g.input_node(f'uv{ch}', 'UVNode', 'UV', V4, (x, y), m_OutputChannel=ch)
    n_uv = {ch: uv(ch, 40.0 * i) for i, ch in enumerate(channels)}
    y0 = 400.0
    n_col = None if details else g.input_node('color', 'VertexColorNode', 'Vertex Color', V4, (x, y0))
    n_pos_os = g.input_node('pos_os', 'PositionNode', 'Position', V3, (x, y0 + 40), sgver=1, m_Space=0, m_PositionSource=0)
    n_nrm_os = g.input_node('nrm_os', 'NormalVectorNode', 'Normal Vector', V3, (x, y0 + 80), m_Space=0)
    n_pos_ws = g.input_node('pos_ws', 'PositionNode', 'Position', V3, (x, y0 + 120), sgver=1, m_Space=2, m_PositionSource=0)
    n_nrm_ws = g.input_node('nrm_ws', 'NormalVectorNode', 'Normal Vector', V3, (x, y0 + 160), m_Space=2)
    n_tan_ws = g.input_node('tan_ws', 'TangentVectorNode', 'Tangent Vector', V3, (x, y0 + 200), m_Space=2)
    n_bit_ws = g.input_node('bit_ws', 'BitangentVectorNode', 'Bitangent Vector', V3, (x, y0 + 240), m_Space=2)
    n_front = g.input_node('front', 'IsFrontFaceNode', 'Is Front Face', B, (x, y0 + 280))

    edges = []
    geo = [('PositionOS', V3, n_pos_os), ('NormalOS', V3, n_nrm_os), ('PositionWS', V3, n_pos_ws),
           ('NormalWS', V3, n_nrm_ws), ('TangentWS', V3, n_tan_ws), ('BitangentWS', V3, n_bit_ws), ('IsFront', B, n_front)]
    if not details:
        geo = [('VertexColor', V4, n_col)] + geo
    fa = None
    if glass:
        ins = [('UV0', V4, n_uv[0]), ('UV3', V4, n_uv[3])] + geo
        outs = [('BaseColor', V3), ('Normal', V3), ('Specular', V3), ('Smoothness', V1), ('Alpha', V1)]
        fn = g.custom_function('glass', 'SMGlass', [(a, b) for a, b, _ in ins], outs, (-700.0, 200.0), SMGRAPH_GUID)
    elif details:
        ins = [(f'UV{c}', V4, n_uv[c]) for c in channels] + geo
        outs = [('BaseColor', V3), ('Normal', V3), ('Metallic', V1), ('Smoothness', V1), ('Occlusion', V1)]
        fn = g.custom_function('details', 'DetailsLit', [(a, b) for a, b, _ in ins], outs, (-700.0, 200.0), DETAILS_GUID)
    else:
        ins = [(f'UV{c}', V4, n_uv[c]) for c in channels] + geo
        outs = [('BaseColor', V3), ('Normal', V3), ('Specular', V3), ('Smoothness', V1), ('Emission', V3), ('CoatMask', V1)]
        fn = g.custom_function('lit', 'SMLit', [(a, b) for a, b, _ in ins], outs, (-700.0, 200.0), SMGRAPH_GUID)
        fa = g.custom_function('alpha', 'SMAlpha', [('UV0', V4)], [('Alpha', V1), ('ClipThreshold', V1)], (-700.0, 700.0), SMGRAPH_GUID)
        edges.append(edge(n_uv[0], 0, fa, 0))
    for i, (_, _, src) in enumerate(ins):
        edges.append(edge(src, 0, fn, i))
    out_slot = {n: len(ins) + j for j, (n, _) in enumerate(outs)}

    # master stack
    vb = [g.block('VertexDescription.Position', 'PositionMaterialSlot', 'Position', 'Position', space=0, stage=1),
          g.block('VertexDescription.Normal', 'NormalMaterialSlot', 'Normal', 'Normal', space=0, stage=1),
          g.block('VertexDescription.Tangent', 'TangentMaterialSlot', 'Tangent', 'Tangent', space=0, stage=1)]
    fb = {}
    fb['BaseColor'] = g.block('SurfaceDescription.BaseColor', 'ColorRGBMaterialSlot', 'BaseColor', 'Base Color', value=[0.5, 0.5, 0.5])
    fb['Normal'] = g.block('SurfaceDescription.NormalWS', 'NormalMaterialSlot', 'NormalWS', 'Normal (World Space)', space=2)
    if details:
        fb['Metallic'] = g.block('SurfaceDescription.Metallic', V1, 'Metallic', 'Metallic', value=0.0)
    else:
        fb['Specular'] = g.block('SurfaceDescription.Specular', 'ColorRGBMaterialSlot', 'Specular', 'Specular Color', value=[0.04, 0.04, 0.04])
    if kind == 'lit':
        fb['CoatMask'] = g.block('SurfaceDescription.CoatMask', V1, 'CoatMask', 'Coat Mask', value=0.0)
    fb['Smoothness'] = g.block('SurfaceDescription.Smoothness', V1, 'Smoothness', 'Smoothness', value=0.5)
    if kind == 'lit':
        fb['Emission'] = g.block('SurfaceDescription.Emission', 'ColorRGBMaterialSlot', 'Emission', 'Emission')
    fb['Occlusion'] = g.block('SurfaceDescription.Occlusion', V1, 'Occlusion', 'Ambient Occlusion', value=1.0)
    fb['Alpha'] = g.block('SurfaceDescription.Alpha', V1, 'Alpha', 'Alpha', value=1.0)
    if kind == 'lit':
        fb['AlphaClipThreshold'] = g.block('SurfaceDescription.AlphaClipThreshold', V1, 'AlphaClipThreshold', 'Alpha Clip Threshold', value=0.5)
    for n in outs:
        edges.append(edge(fn, out_slot[n[0]], fb[n[0]], 0))
    if fa:
        edges.append(edge(fa, 1, fb['Alpha'], 0))
        edges.append(edge(fa, 2, fb['AlphaClipThreshold'], 0))

    # HDRP target
    lit = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.HDLitData', 'm_ObjectId': g.oid('litdata'),
                 'm_RayTracing': False, 'm_MaterialType': 0 if details else 4, 'm_MaterialTypeMask': 2 if details else 16,
                 'm_RefractionModel': 0, 'm_SSSTransmission': True, 'm_EnergyConservingSpecular': True, 'm_ClearCoat': kind == 'lit'})
    system = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.SystemData', 'm_ObjectId': g.oid('system'),
                    'm_MaterialNeedsUpdateHash': 0, 'm_SurfaceType': 1 if glass else 0, 'm_RenderingPass': 4 if glass else 1,
                    'm_BlendMode': 0, 'm_ZTest': 4, 'm_ZWrite': False, 'm_TransparentCullMode': 2, 'm_OpaqueCullMode': 2,
                    'm_SortPriority': 0, 'm_AlphaTest': kind == 'lit', 'm_ExcludeFromTUAndAA': False,
                    'm_TransparentDepthPrepass': False, 'm_TransparentDepthPostpass': False, 'm_SupportLodCrossFade': False,
                    'm_DoubleSidedMode': 0 if details else 2, 'm_DOTSInstancing': False, 'm_CustomVelocity': False, 'm_Tessellation': False,
                    'm_TessellationMode': 0, 'm_TessellationFactorMinDistance': 20.0, 'm_TessellationFactorMaxDistance': 50.0,
                    'm_TessellationFactorTriangleSize': 100.0, 'm_TessellationShapeFactor': 0.75,
                    'm_TessellationBackFaceCullEpsilon': -0.25, 'm_TessellationMaxDisplacement': 0.01,
                    'm_DebugSymbols': False, 'm_Version': 2, 'inspectorFoldoutMask': 0})
    builtin = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.BuiltinData', 'm_ObjectId': g.oid('builtin'),
                     'm_Distortion': False, 'm_DistortionMode': 0, 'm_DistortionDepthTest': True, 'm_AddPrecomputedVelocity': False,
                     'm_TransparentWritesMotionVec': False, 'm_DepthOffset': False, 'm_ConservativeDepthOffset': False,
                     'm_TransparencyFog': True, 'm_AlphaTestShadow': False, 'm_BackThenFrontRendering': False,
                     'm_TransparentDepthPrepass': False, 'm_TransparentDepthPostpass': False,
                     'm_TransparentPerPixelSorting': False, 'm_SupportLodCrossFade': False})
    lighting = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.LightingData', 'm_ObjectId': g.oid('lighting'),
                      'm_NormalDropOffSpace': 2, 'm_BlendPreserveSpecular': True, 'm_ReceiveDecals': True, 'm_ReceiveSSR': True,
                      'm_ReceiveSSRTransparent': False, 'm_SpecularAA': False, 'm_SpecularOcclusionMode': 1, 'm_OverrideBakedGI': False})
    sub = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.HDLitSubTarget', 'm_ObjectId': g.oid('sub')})
    target = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.HDTarget', 'm_ObjectId': g.oid('target'),
                    'm_ActiveSubTarget': {'m_Id': sub}, 'm_Datas': [{'m_Id': i} for i in (lit, system, builtin, lighting)],
                    'm_CustomEditorGUI': '' if details else GUI, 'm_SupportVFX': False, 'm_SupportLineRendering': False})
    cat = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.ShaderGraph.CategoryData', 'm_ObjectId': g.oid('category'),
                 'm_Name': '', 'm_ChildObjectList': [{'m_Id': i} for i in prop_ids + kw_ids]})

    node_ids = [o['m_ObjectId'] for o in g.objs if o['m_Type'].endswith('Node')]
    root = {'m_SGVersion': 3, 'm_Type': 'UnityEditor.ShaderGraph.GraphData', 'm_ObjectId': g.oid('graph'),
            'm_Properties': [{'m_Id': i} for i in prop_ids], 'm_Keywords': [{'m_Id': i} for i in kw_ids], 'm_Dropdowns': [],
            'm_CategoryData': [{'m_Id': cat}], 'm_Nodes': [{'m_Id': i} for i in node_ids], 'm_GroupDatas': [], 'm_StickyNoteDatas': [],
            'm_Edges': edges,
            'm_VertexContext': {'m_Position': {'x': 0.0, 'y': 0.0}, 'm_Blocks': [{'m_Id': i} for i in vb]},
            'm_FragmentContext': {'m_Position': {'x': 0.0, 'y': 200.0}, 'm_Blocks': [{'m_Id': i} for i in fb.values()]},
            'm_PreviewData': {'serializedMesh': {'m_SerializedMesh': '{"mesh":{"instanceID":0}}', 'm_Guid': ''}, 'preventRotation': False},
            'm_Path': 'SMC-AD', 'm_GraphPrecision': 1, 'm_PreviewMode': 2, 'm_OutputNode': {'m_Id': ''},
            'm_SubDatas': [], 'm_ActiveTargets': [{'m_Id': target}]}
    docs = [root] + sorted(g.objs, key=lambda o: o['m_ObjectId'])
    return '\n\n'.join(json.dumps(o, indent=4, ensure_ascii=False) for o in docs) + '\n'


def build_particle():
    """TankRig/Particle Unlit (file "Particle Unlit.shadergraph"; the shader name is m_Path + the file name): HDRP Unlit, transparent; texture x vertex colour x tint. Alpha blend by default, the
    effects factory switches the additive material to HDRP's Additive blend mode."""
    g = Graph('TankRig Particle')
    prop_ids = [g.prop(p) for p in parse(PARTICLE_PROPS)]
    n_uv = g.input_node('uv0', 'UVNode', 'UV', V4, (-900.0, 0.0), m_OutputChannel=0)
    n_col = g.input_node('color', 'VertexColorNode', 'Vertex Color', V4, (-900.0, 60.0))
    fn = g.custom_function('particle', 'TankRigParticle', [('UV0', V4), ('VertexColor', V4)], [('Color', V3), ('Alpha', V1)],
                           (-500.0, 0.0), PARTICLE_GUID)
    vb = [g.block('VertexDescription.Position', 'PositionMaterialSlot', 'Position', 'Position', space=0, stage=1),
          g.block('VertexDescription.Normal', 'NormalMaterialSlot', 'Normal', 'Normal', space=0, stage=1),
          g.block('VertexDescription.Tangent', 'TangentMaterialSlot', 'Tangent', 'Tangent', space=0, stage=1)]
    fb = [g.block('SurfaceDescription.BaseColor', 'ColorRGBMaterialSlot', 'BaseColor', 'Base Color', value=[1.0, 1.0, 1.0]),
          g.block('SurfaceDescription.Alpha', V1, 'Alpha', 'Alpha', value=1.0)]
    edges = [edge(n_uv, 0, fn, 0), edge(n_col, 0, fn, 1), edge(fn, 2, fb[0], 0), edge(fn, 3, fb[1], 0)]
    unlit = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.HDUnlitData', 'm_ObjectId': g.oid('unlitdata'),
                   'm_EnableShadowMatte': False, 'm_DistortionOnly': False})
    system = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.SystemData', 'm_ObjectId': g.oid('system'),
                    'm_MaterialNeedsUpdateHash': 0, 'm_SurfaceType': 1, 'm_RenderingPass': 4, 'm_BlendMode': 0, 'm_ZTest': 4,
                    'm_ZWrite': False, 'm_TransparentCullMode': 2, 'm_OpaqueCullMode': 2, 'm_SortPriority': 0, 'm_AlphaTest': False,
                    'm_ExcludeFromTUAndAA': False, 'm_TransparentDepthPrepass': False, 'm_TransparentDepthPostpass': False,
                    'm_SupportLodCrossFade': False, 'm_DoubleSidedMode': 1, 'm_DOTSInstancing': False, 'm_CustomVelocity': False,
                    'm_Tessellation': False, 'm_TessellationMode': 0, 'm_TessellationFactorMinDistance': 20.0,
                    'm_TessellationFactorMaxDistance': 50.0, 'm_TessellationFactorTriangleSize': 100.0, 'm_TessellationShapeFactor': 0.75,
                    'm_TessellationBackFaceCullEpsilon': -0.25, 'm_TessellationMaxDisplacement': 0.01,
                    'm_DebugSymbols': False, 'm_Version': 2, 'inspectorFoldoutMask': 0})
    builtin = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.BuiltinData', 'm_ObjectId': g.oid('builtin'),
                     'm_Distortion': False, 'm_DistortionMode': 0, 'm_DistortionDepthTest': True, 'm_AddPrecomputedVelocity': False,
                     'm_TransparentWritesMotionVec': False, 'm_DepthOffset': False, 'm_ConservativeDepthOffset': False,
                     'm_TransparencyFog': True, 'm_AlphaTestShadow': False, 'm_BackThenFrontRendering': False,
                     'm_TransparentDepthPrepass': False, 'm_TransparentDepthPostpass': False,
                     'm_TransparentPerPixelSorting': False, 'm_SupportLodCrossFade': False})
    sub = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.HDUnlitSubTarget', 'm_ObjectId': g.oid('sub')})
    target = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.HDTarget', 'm_ObjectId': g.oid('target'),
                    'm_ActiveSubTarget': {'m_Id': sub}, 'm_Datas': [{'m_Id': i} for i in (unlit, system, builtin)],
                    'm_CustomEditorGUI': '', 'm_SupportVFX': False, 'm_SupportLineRendering': False})
    cat = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.ShaderGraph.CategoryData', 'm_ObjectId': g.oid('category'),
                 'm_Name': '', 'm_ChildObjectList': [{'m_Id': i} for i in prop_ids]})
    node_ids = [o['m_ObjectId'] for o in g.objs if o['m_Type'].endswith('Node')]
    root = {'m_SGVersion': 3, 'm_Type': 'UnityEditor.ShaderGraph.GraphData', 'm_ObjectId': g.oid('graph'),
            'm_Properties': [{'m_Id': i} for i in prop_ids], 'm_Keywords': [], 'm_Dropdowns': [],
            'm_CategoryData': [{'m_Id': cat}], 'm_Nodes': [{'m_Id': i} for i in node_ids], 'm_GroupDatas': [], 'm_StickyNoteDatas': [],
            'm_Edges': edges,
            'm_VertexContext': {'m_Position': {'x': 0.0, 'y': 0.0}, 'm_Blocks': [{'m_Id': i} for i in vb]},
            'm_FragmentContext': {'m_Position': {'x': 0.0, 'y': 200.0}, 'm_Blocks': [{'m_Id': i} for i in fb]},
            'm_PreviewData': {'serializedMesh': {'m_SerializedMesh': '{"mesh":{"instanceID":0}}', 'm_Guid': ''}, 'preventRotation': False},
            'm_Path': 'TankRig', 'm_GraphPrecision': 1, 'm_PreviewMode': 2, 'm_OutputNode': {'m_Id': ''},
            'm_SubDatas': [], 'm_ActiveTargets': [{'m_Id': target}]}
    docs = [root] + sorted(g.objs, key=lambda o: o['m_ObjectId'])
    return '\n\n'.join(json.dumps(o, indent=4, ensure_ascii=False) for o in docs) + '\n'


def build_ring():
    """TankRig/Turret Ring Interior: HDRP Unlit, opaque with alpha clip. A fake interior (procedural, its own fixed
    lighting) ray marched in TankRigRingInterior.hlsl from the object-space surface point along the view ray."""
    g = Graph('TankRig Ring Interior')
    prop_ids = [g.prop(p) for p in parse(RING_PROPS)]
    n_pos_os = g.input_node('pos_os', 'PositionNode', 'Position', V3, (-900.0, 0.0), sgver=1, m_Space=0, m_PositionSource=0)
    n_pos_ws = g.input_node('pos_ws', 'PositionNode', 'Position', V3, (-900.0, 60.0), sgver=1, m_Space=2, m_PositionSource=0)
    fn = g.custom_function('ring', 'TankRigRingInterior', [('PositionOS', V3), ('PositionWS', V3)], [('Color', V3), ('Alpha', V1)],
                           (-500.0, 0.0), RING_GUID)
    vb = [g.block('VertexDescription.Position', 'PositionMaterialSlot', 'Position', 'Position', space=0, stage=1),
          g.block('VertexDescription.Normal', 'NormalMaterialSlot', 'Normal', 'Normal', space=0, stage=1),
          g.block('VertexDescription.Tangent', 'TangentMaterialSlot', 'Tangent', 'Tangent', space=0, stage=1)]
    fb = [g.block('SurfaceDescription.BaseColor', 'ColorRGBMaterialSlot', 'BaseColor', 'Base Color', value=[0.0, 0.0, 0.0]),
          g.block('SurfaceDescription.Alpha', V1, 'Alpha', 'Alpha', value=1.0),
          g.block('SurfaceDescription.AlphaClipThreshold', V1, 'AlphaClipThreshold', 'Alpha Clip Threshold', value=0.5)]
    edges = [edge(n_pos_os, 0, fn, 0), edge(n_pos_ws, 0, fn, 1), edge(fn, 2, fb[0], 0), edge(fn, 3, fb[1], 0)]
    unlit = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.HDUnlitData', 'm_ObjectId': g.oid('unlitdata'),
                   'm_EnableShadowMatte': False, 'm_DistortionOnly': False})
    system = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.SystemData', 'm_ObjectId': g.oid('system'),
                    'm_MaterialNeedsUpdateHash': 0, 'm_SurfaceType': 0, 'm_RenderingPass': 1, 'm_BlendMode': 0, 'm_ZTest': 4,
                    'm_ZWrite': True, 'm_TransparentCullMode': 2, 'm_OpaqueCullMode': 2, 'm_SortPriority': 0, 'm_AlphaTest': True,
                    'm_ExcludeFromTUAndAA': False, 'm_TransparentDepthPrepass': False, 'm_TransparentDepthPostpass': False,
                    'm_SupportLodCrossFade': False, 'm_DoubleSidedMode': 0, 'm_DOTSInstancing': False, 'm_CustomVelocity': False,
                    'm_Tessellation': False, 'm_TessellationMode': 0, 'm_TessellationFactorMinDistance': 20.0,
                    'm_TessellationFactorMaxDistance': 50.0, 'm_TessellationFactorTriangleSize': 100.0, 'm_TessellationShapeFactor': 0.75,
                    'm_TessellationBackFaceCullEpsilon': -0.25, 'm_TessellationMaxDisplacement': 0.01,
                    'm_DebugSymbols': False, 'm_Version': 2, 'inspectorFoldoutMask': 0})
    builtin = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.BuiltinData', 'm_ObjectId': g.oid('builtin'),
                     'm_Distortion': False, 'm_DistortionMode': 0, 'm_DistortionDepthTest': True, 'm_AddPrecomputedVelocity': False,
                     'm_TransparentWritesMotionVec': False, 'm_DepthOffset': False, 'm_ConservativeDepthOffset': False,
                     'm_TransparencyFog': True, 'm_AlphaTestShadow': False, 'm_BackThenFrontRendering': False,
                     'm_TransparentDepthPrepass': False, 'm_TransparentDepthPostpass': False,
                     'm_TransparentPerPixelSorting': False, 'm_SupportLodCrossFade': False})
    sub = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.HDUnlitSubTarget', 'm_ObjectId': g.oid('sub')})
    target = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.Rendering.HighDefinition.ShaderGraph.HDTarget', 'm_ObjectId': g.oid('target'),
                    'm_ActiveSubTarget': {'m_Id': sub}, 'm_Datas': [{'m_Id': i} for i in (unlit, system, builtin)],
                    'm_CustomEditorGUI': '', 'm_SupportVFX': False, 'm_SupportLineRendering': False})
    cat = g.add({'m_SGVersion': 0, 'm_Type': 'UnityEditor.ShaderGraph.CategoryData', 'm_ObjectId': g.oid('category'),
                 'm_Name': '', 'm_ChildObjectList': [{'m_Id': i} for i in prop_ids]})
    node_ids = [o['m_ObjectId'] for o in g.objs if o['m_Type'].endswith('Node')]
    root = {'m_SGVersion': 3, 'm_Type': 'UnityEditor.ShaderGraph.GraphData', 'm_ObjectId': g.oid('graph'),
            'm_Properties': [{'m_Id': i} for i in prop_ids], 'm_Keywords': [], 'm_Dropdowns': [],
            'm_CategoryData': [{'m_Id': cat}], 'm_Nodes': [{'m_Id': i} for i in node_ids], 'm_GroupDatas': [], 'm_StickyNoteDatas': [],
            'm_Edges': edges,
            'm_VertexContext': {'m_Position': {'x': 0.0, 'y': 0.0}, 'm_Blocks': [{'m_Id': i} for i in vb]},
            'm_FragmentContext': {'m_Position': {'x': 0.0, 'y': 200.0}, 'm_Blocks': [{'m_Id': i} for i in fb]},
            'm_PreviewData': {'serializedMesh': {'m_SerializedMesh': '{"mesh":{"instanceID":0}}', 'm_Guid': ''}, 'preventRotation': False},
            'm_Path': 'TankRig', 'm_GraphPrecision': 1, 'm_PreviewMode': 2, 'm_OutputNode': {'m_Id': ''},
            'm_SubDatas': [], 'm_ActiveTargets': [{'m_Id': target}]}
    docs = [root] + sorted(g.objs, key=lambda o: o['m_ObjectId'])
    return '\n\n'.join(json.dumps(o, indent=4, ensure_ascii=False) for o in docs) + '\n'


def write_meta(path, guid, importer):
    if not os.path.exists(path + '.meta'):
        with open(path + '.meta', 'w', encoding='utf-8', newline='\n') as f:
            f.write(f'fileFormatVersion: 2\nguid: {guid}\n{importer}')


if __name__ == '__main__':
    graph_meta = ('ScriptedImporter:\n  internalIDToNameTable: []\n  externalObjects: {}\n  serializedVersion: 2\n'
                  '  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
                  '  script: {fileID: 11500000, guid: 625f186215c104763be7675aa2d941aa, type: 3}\n')
    for kind, folder, name, guid in [('lit', SHADERS, 'SM Lit', 'a7d1c4e2b8f94d3a9c6e1f0b2d4a6c81'),
                                     ('glass', SHADERS, 'SM Glass', 'b8e2d5f3c9a04e4b8d7f2a1c3e5b7d92'),
                                     ('details', DETAILS_SHADERS, 'Details Lit', 'c9f3e6a4d0b15f5c9e8a3b2d4f6c8ea3')]:
        path = os.path.join(folder, name + '.shadergraph')
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(build(kind))
        write_meta(path, guid, graph_meta)
        print('wrote', path)
    include_meta = 'ShaderIncludeImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    for folder, hl, guid in [(SHADERS, 'SMGraph.hlsl', SMGRAPH_GUID), (SHADERS, 'SMSurface.hlsl', '6d4c8f2b0e3a4f71b9c2d8e4f0a31522'),
                             (DETAILS_SHADERS, 'SMDetailsGraph.hlsl', DETAILS_GUID)]:
        write_meta(os.path.join(folder, hl), guid, include_meta)
    if os.path.isdir(os.path.dirname(TANKRIG_SHADERS)):
        os.makedirs(TANKRIG_SHADERS, exist_ok=True)
        path = os.path.join(TANKRIG_SHADERS, 'Particle Unlit.shadergraph')   # shader name = m_Path + file name
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(build_particle())
        write_meta(path, 'd0a4f7b5e1c26a6dafb9c4e3a5d7f9b4', graph_meta)
        write_meta(os.path.join(TANKRIG_SHADERS, 'TankRigParticle.hlsl'), PARTICLE_GUID, include_meta)
        print('wrote', path)
        path = os.path.join(TANKRIG_SHADERS, 'Turret Ring Interior.shadergraph')
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(build_ring())
        write_meta(path, 'e1b5a8c6f2d37b7ebfc0d5f4b6e8fac5', graph_meta)
        write_meta(os.path.join(TANKRIG_SHADERS, 'TankRigRingInterior.hlsl'), RING_GUID, include_meta)
        print('wrote', path)
