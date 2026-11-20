// SPDX-License-Identifier: MIT
// SMC-DETAILS for Unity: wear and dirt on an SMC-AD blueprint, computed live in SM Lit (keyword _SMCAD_DETAILS).
// This component sits on the blueprint root. It owns the weathered copies of the SM materials and pushes the
// settings into them; the shape grid, vertex streams and paint grids are made by the editor tools.
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SMCAD.Details
{
    public enum SMDChannel { Chip, Scratch, Rust, Grime, Mud, Soil, Sand, Wet }
    public enum SMDBeltDirt { Mud, Sand }

    [Serializable]
    public class SMDetailsSettings
    {
        public static readonly string[] Labels = { "塗装ハゲ", "傷", "錆", "くすみ・すす", "泥汚れ", "土汚れ", "砂汚れ", "濡れ" };

        [Tooltip("自動で付く量（0 = なし）")]
        public float[] amount = { 0.35f, 0.2f, 0.15f, 0.4f, 0.4f, 0.3f, 0.2f, 0f };
        [Tooltip("1メートルあたりの模様の数")]
        public float[] scale = { 6f, 3f, 4f, 3f, 5f, 2f, 6f, 1f };
        [Tooltip("泥・土・砂: 地面からこの高さ (m) まで")]
        public float[] height = { 0f, 0f, 0f, 0f, 0.6f, 1.2f, 0.3f, 0f };
        [Tooltip("見た目の強さ")]
        public float[] strength = { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };

        public Color primer = Lin(0.32f, 0.12f, 0.07f);
        public Color metal = Lin(0.45f, 0.45f, 0.46f);
        public Color rustLight = Lin(0.38f, 0.13f, 0.04f);
        public Color rustDark = Lin(0.12f, 0.05f, 0.025f);
        public Color grime = Lin(0.18f, 0.15f, 0.12f);
        public Color mud = Lin(0.09f, 0.065f, 0.045f);
        public Color soil = Lin(0.25f, 0.18f, 0.12f);
        public Color sand = Lin(0.55f, 0.45f, 0.32f);

        [Range(0, 1)] public float metalRoughness = 0.35f;
        [Range(0, 1)] public float rustRoughness = 0.9f;
        [Range(0, 1)] public float mudRoughness = 0.7f;
        [Range(0, 1)] public float chipOnTexture = 0.3f;
        [Range(0, 1)] public float wetDarken = 0.55f;
        [Range(0, 1)] public float bumpStrength = 0.6f;
        [Range(0, 0.05f)] public float bumpDistance = 0.006f;

        [Tooltip("角の検出の強さ")] public float edgeGain = 3f;
        [Tooltip("くぼみの検出の強さ")] public float cavityGain = 2.5f;
        [Tooltip("細かい模様の倍率")] public float fineScale = 1f;
        [Tooltip("境目のぼかし")] [Range(0.005f, 0.3f)] public float softness = 0.06f;
        public int seed;
        [Tooltip("地面の高さの調整 (m)")] public float groundOffset;
        [Tooltip("履帯（動くリンク）に付く汚れの量")] [Range(0, 1)] public float beltSoil = 0.6f;
        [Tooltip("履帯に付く汚れの種類")] public SMDBeltDirt beltDirt = SMDBeltDirt.Mud;

        [Tooltip("錆・塗装ハゲが付く角の幅 (m)")] [Range(0.002f, 0.15f)] public float edgeWidth = 0.03f;
        [Tooltip("へこんだ角（すみ）に汚れが溜まる幅 (m)")] [Range(0.002f, 0.2f)] public float cornerWidth = 0.04f;
        [Tooltip("履帯が巻き上げた泥・土が届く範囲 (m)")] [Range(0.05f, 3f)] public float kickRange = 0.6f;
        [Tooltip("履帯が巻き上げる泥・土の量")] [Range(0, 1.5f)] public float kickAmount = 0.8f;
        [Tooltip("砲口・排気の焦げ・すすの量")] [Range(0, 1.5f)] public float burnAmount = 1f;
        [Tooltip("焦げ・すすの広がり（倍率）")] [Range(0.2f, 4f)] public float burnSize = 1f;
        public Color burnColor = Lin(0.015f, 0.013f, 0.012f);

        // base weathering of the paint (model-kit techniques), under all the wear and dirt
        [Tooltip("色の変調: 上や高い所を明るく、下や下面を暗く")] [Range(0, 1)] public float modulation = 0.5f;
        [Tooltip("退色・日焼け: 上面が白っぽく色あせる")] [Range(0, 1)] public float fading = 0.3f;
        [Tooltip("装甲ごとの色ムラ: 部品ごとに少しずつ明るさが違う")] [Range(0, 1)] public float partTone = 0.4f;
        [Tooltip("フィルター: 暖色・寒色のまだらな色味のゆらぎ（ドットフィルター）")] [Range(0, 1)] public float filter = 0.4f;
        [Tooltip("フィルターのまだらの細かさ（1メートルあたり）")] [Range(0.2f, 10f)] public float filterScale = 1.5f;
        [Tooltip("ポストシェーディング: 板の縁に向かって暗く、中央は明るく")] [Range(0, 1)] public float postShade = 0.3f;
        [Tooltip("スミ入れ: へこんだ角・ボルトのまわりの暗い線")] [Range(0, 1)] public float pinWash = 0.5f;
        [Tooltip("エッジハイライト: 角の縁を明るく拾う（ドライブラシ）")] [Range(0, 1)] public float edgeHighlight = 0.4f;
        [Tooltip("雨だれ・ストリーキング: 側面を流れ落ちる暗い筋と明るい筋")] [Range(0, 1)] public float streaks = 0.4f;
        [Tooltip("雨だれの筋の多さ（1メートルあたり）")] [Range(2f, 40f)] public float streakDensity = 12f;
        [Tooltip("雨だれの筋の長さ")] [Range(0.1f, 2f)] public float streakLength = 0.5f;

        static Color Lin(float r, float g, float b) => new Color(r, g, b).gamma;   // stored as sRGB for the colour pickers

        public SMDetailsSettings Clone()
        {
            var c = (SMDetailsSettings)MemberwiseClone();
            c.amount = (float[])amount.Clone(); c.scale = (float[])scale.Clone();
            c.height = (float[])height.Clone(); c.strength = (float[])strength.Clone();
            return c;
        }
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("SMC-AD/SMC-DETAILS 汚し")]
    public class SMDetails : MonoBehaviour
    {
        public SMDetailsSettings settings = new SMDetailsSettings();
        [Tooltip("0 = 仕上がり、1〜8 = その汚れだけを白黒で表示")] public int view;

        [Header("解析の結果（自動）")]
        public Texture3D shape;
        public Texture3D noise;
        public Vector3 gridMin;
        public Vector3 gridSize = Vector3.one;
        public float voxel = 0.03f;
        public float groundY;
        public Texture3D paint0;
        public Texture3D paint1;
        [Tooltip("生成したファイルの置き場（機体ごと）")] public string assetFolder;
        public bool hasEdgeData;
        public Vector4[] trackMin = new Vector4[2], trackMax = new Vector4[2];   // w = used
        [Serializable]
        public class BurnSource
        {
            public string name;
            public Vector3 restPosition;
            public Vector3 restDirection;
            public float radius;
            public int type;   // 0 muzzle (soot along the barrel behind it), 1 exhaust (plume)
        }
        public List<BurnSource> burnSources = new List<BurnSource>();

        [Serializable]
        public class Slot
        {
            public Renderer renderer;
            public Material[] original;
            public Mesh stream;          // the weathered mesh copy (one vertex per triangle corner + SMC-DETAILS data)
            public Mesh originalMesh;
            public Matrix4x4 restMatrix; // mesh -> rest frame at analysis time (hand paint is only valid for this pose)
        }
        public List<Slot> slots = new List<Slot>();
        public List<Material> materials = new List<Material>();

        // track links (e.g. Tank Rigging plugin): the component's "linkPrefab" is swapped for a weathered copy
        [Serializable]
        public class LinkSwap
        {
            public Component track;
            public GameObject original;
            public GameObject weathered;
        }
        public List<LinkSwap> linkSwaps = new List<LinkSwap>();
        public List<Material> beltMaterials = new List<Material>();
        public List<SMDetailsTrackDirt> trackDirt = new List<SMDetailsTrackDirt>();

        // live paint targets while painting in the editor (not saved)
        [NonSerialized] public Texture livePaint0, livePaint1;

        public bool IsReady => shape != null && materials.Count > 0;

        void OnEnable() { Apply(); }
        void OnValidate() { Apply(); }

        /// <summary>Pushes the settings into the weathered materials.</summary>
        public void Apply()
        {
            if (settings == null) return;
            var s = settings;
            Texture p0 = livePaint0 != null ? livePaint0 : paint0;
            Texture p1 = livePaint1 != null ? livePaint1 : paint1;
            Vector4 inv = new Vector4(1f / Mathf.Max(gridSize.x, 1e-4f), 1f / Mathf.Max(gridSize.y, 1e-4f), 1f / Mathf.Max(gridSize.z, 1e-4f), voxel);
            foreach (var m in materials.Concat(beltMaterials))
            {
                if (m == null) continue;
                bool belt = beltMaterials.Contains(m);
                m.SetFloat("_SmdEnabled", 1f);
                m.EnableKeyword("_SMCAD_DETAILS");
                m.SetTexture("_SmdGeo", shape);
                m.SetTexture("_SmdNoise", noise);
                m.SetTexture("_SmdPaint0", p0);
                m.SetTexture("_SmdPaint1", p1);
                m.SetVector("_SmdGridMin", gridMin);
                m.SetVector("_SmdGridInv", inv);
                m.SetVector("_SmdAmount0", belt ? Vector4.zero : V(s.amount, 0));
                m.SetVector("_SmdAmount1", belt ? new Vector4(0f, s.beltSoil, 0f, 0f) : V(s.amount, 4));
                m.SetFloat("_SmdBelt", belt ? 1f : 0f);
                if (belt)
                {
                    m.EnableKeyword("_SMD_BELT");
                    m.SetVector("_SmdAmount1", s.beltDirt == SMDBeltDirt.Mud ? new Vector4(s.beltSoil, 0f, 0f, 0f) : new Vector4(0f, 0f, s.beltSoil, 0f));
                }
                m.SetVector("_SmdEdge", new Vector4(s.edgeWidth, s.cornerWidth, hasEdgeData ? 1f : 0f, 0f));
                m.SetVector("_SmdKick", new Vector4(s.kickRange, s.kickAmount, 0f, 0f));
                m.SetVector("_SmdBurn", new Vector4(s.burnAmount, 0f, 0f, 0f));
                m.SetVector("_SmdBase0", new Vector4(s.modulation, s.fading, s.partTone, s.filter));
                m.SetVector("_SmdBase1", new Vector4(s.postShade, s.pinWash, s.edgeHighlight, s.streaks));
                m.SetVector("_SmdBase2", new Vector4(s.filterScale, s.streakDensity, s.streakLength, Mathf.Max(0.3f, gridSize.y - (groundY - gridMin.y))));
                m.SetVector("_SmdBurnColor", L(s.burnColor));
                for (int i = 0; i < 2; i++)
                {
                    m.SetVector("_SmdTrackMin" + i, trackMin != null && i < trackMin.Length ? trackMin[i] : Vector4.zero);
                    m.SetVector("_SmdTrackMax" + i, trackMax != null && i < trackMax.Length ? trackMax[i] : Vector4.zero);
                }
                for (int i = 0; i < 4; i++)
                {
                    var b = i < burnSources.Count ? burnSources[i] : null;
                    m.SetVector("_SmdSrcP" + i, b == null ? Vector4.zero : new Vector4(b.restPosition.x, b.restPosition.y, b.restPosition.z, b.radius * s.burnSize));
                    m.SetVector("_SmdSrcD" + i, b == null ? Vector4.zero : new Vector4(b.restDirection.x, b.restDirection.y, b.restDirection.z, b.type));
                }
                m.SetVector("_SmdScale0", V(s.scale, 0));
                m.SetVector("_SmdScale1", V(s.scale, 4));
                m.SetVector("_SmdHeights", new Vector4(s.height[4], s.height[5], s.height[6], groundY + s.groundOffset));
                m.SetVector("_SmdGen0", new Vector4(s.edgeGain, s.cavityGain, voxel * 1.6f, voxel * 5f));   // near sample clear of the shell behind the surface
                m.SetVector("_SmdGen1", new Vector4(s.fineScale, s.softness, p0 != null && p1 != null ? 1f : 0f, s.seed));
                m.SetFloat("_SmdView", view);
                m.SetVector("_SmdStrength0", V(s.strength, 0));
                m.SetVector("_SmdStrength1", V(s.strength, 4));
                m.SetVector("_SmdPrimer", L(s.primer));
                m.SetVector("_SmdMetal", L(s.metal));
                m.SetVector("_SmdRustA", L(s.rustLight));
                m.SetVector("_SmdRustB", L(s.rustDark));
                m.SetVector("_SmdGrime", L(s.grime));
                m.SetVector("_SmdMud", L(s.mud));
                m.SetVector("_SmdSoil", L(s.soil));
                m.SetVector("_SmdSand", L(s.sand));
                m.SetVector("_SmdParams0", new Vector4(s.metalRoughness, s.rustRoughness, s.mudRoughness, s.chipOnTexture));
                m.SetVector("_SmdParams1", new Vector4(s.wetDarken, s.bumpStrength, s.bumpDistance, 0f));
            }
            foreach (var td in trackDirt) if (td != null) td.Refresh();
        }

        static Vector4 V(float[] a, int i) => new Vector4(a[i], a[i + 1], a[i + 2], a[i + 3]);
        static Vector4 L(Color c) { var l = c.linear; return new Vector4(l.r, l.g, l.b, 1f); }
    }
}
