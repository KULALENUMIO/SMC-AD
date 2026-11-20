// SPDX-License-Identifier: MIT
// Wear and dirt from SMC-DETAILS for Unity (package com.smcad.details), keyword _SMCAD_DETAILS.
// Everything is computed live (no UV, no baked masks). Inputs per vertex (written by the set-up into a copy of
// each mesh, one vertex per triangle corner):
//   UV5 rest position / UV6 rest normal  (frame = world axes at the blueprint root at analysis time, y = up)
//   UV4.xyz distance to the triangle's convex edges, UV7.xyz to its concave edges (metres; 10 = not an edge).
//     Each component is linear over the triangle, so the shader gets the exact distance to real part edges.
// Shape grid _SmdGeo (3D): fraction of directions from which a point is visible (cavities, thin parts).
// The including shader declares the _Smd* constants in its UnityPerMaterial buffer (see SMCommon.hlsl).
#ifndef SMCAD_DETAILS_INCLUDED
#define SMCAD_DETAILS_INCLUDED

TEXTURE3D(_SmdGeo);    SAMPLER(sampler_SmdGeo);
TEXTURE3D(_SmdNoise);  SAMPLER(sampler_SmdNoise);
TEXTURE3D(_SmdPaint0); SAMPLER(sampler_SmdPaint0);
TEXTURE3D(_SmdPaint1);

#define SMD_NOISE_CELLS 12.0
#define SMD_NO_EDGE 9.0

float4 SMDNoise(float3 p, float scale, float3 seed)
{
    return SAMPLE_TEXTURE3D(_SmdNoise, sampler_SmdNoise, p * (scale / SMD_NOISE_CELLS) + seed);
}

// coverage of a 0..1 value for an amount: nothing at amount 0 (v is clamped: corners / the ground can push it over 1)
float SMDCover(float v, float amount, float soft) { return saturate((saturate(v) - (1.0 - amount)) / soft); }

// fine detail decides the shape of the border: crisp, high-frequency edges from a soft mask
float SMDCrisp(float m, float d, float w)
{
    w = min(w, 0.49);                // wider would put the lower edge below 0 and cover m = 0 too
    float t = lerp(w, 1.0 - w, d);
    return m > 0.0 ? smoothstep(t - w, t + w, m) : 0.0;
}

// Blender's Bump node (screen-space derivatives of the height, as in EEVEE)
float3 SMDBump(float3 N, float3 positionWS, float height, float strength, float dist)
{
    float3 dPdx = ddx(positionWS);
    float3 dPdy = ddy(positionWS);
    float3 Rx = cross(dPdy, N);
    float3 Ry = cross(N, dPdx);
    float det = dot(dPdx, Rx);
    float3 surfgrad = ddx(height) * Rx + ddy(height) * Ry;
    float3 r = abs(det) * N - dist * sign(det) * surfgrad;
    r = dot(r, r) > 1e-20 ? normalize(r) : N;
    return normalize(lerp(N, r, saturate(strength)));
}

struct SMDMasks
{
    float chip, scratch, rust, grime, mud, soil, sand, wet;
    float burn, dust;             // soot (muzzle / exhaust), - (unused)
    float relief;                 // extra height for the bump (mud / soil / sand / rust bodies)
    float edge, cavity;           // shape info (debug views)
    float grain, clump, thick;    // look variation
    // base weathering (model-kit techniques, under all the dirt)
    float hilite, panel, wash;    // edge highlight (drybrush), distance into a panel (post-shading), pin wash
    float streakDark, streakLight;
    float3 filter;                // dot-filter mottling, -0.5..0.5 per channel
};

float SMDMin3(float3 v) { return min(v.x, min(v.y, v.z)); }

float SMDBoxDist(float3 p, float4 mn, float4 mx)
{
    if (mx.w < 0.5) return 1e4;
    float3 d = max(max(mn.xyz - p, p - mx.xyz), 0.0);
    return length(d);
}

// soot source: P = position + radius, D = direction + type (0 muzzle: along the barrel behind it, 1 exhaust: plume)
float SMDBurnSource(float3 p, float4 P, float4 D, float grow)
{
    if (P.w <= 0.0) return 0.0;
    P.w *= grow;
    float3 rel = p - P.xyz;
    float along = dot(rel, D.xyz);
    float perp = length(rel - D.xyz * along);
    float stretch = along > 0.0 ? (D.w > 0.5 ? 3.0 : 0.35) : (D.w > 0.5 ? 0.7 : 4.0);
    float d = length(float2(perp, along / stretch)) / P.w;
    return 1.0 - smoothstep(0.15, 1.0, d);
}

SMDMasks SMDGenerate(float3 p, float3 n, float3 edgeConvex, float3 edgeConcave)
{
    SMDMasks m = (SMDMasks)0;
    float3 seed = _SmdGen1.www * float3(0.137, 0.271, 0.419);
    float4 sc0 = _SmdScale0, sc1 = _SmdScale1;   // chip scratch rust grime | mud soil sand -
    float4 a0 = _SmdAmount0, a1 = _SmdAmount1;

    // ---- shape: visibility grid (cavities, thin parts) + exact part edges from the vertex data
    float3 uvwNear = (p + n * _SmdGen0.z - _SmdGridMin.xyz) * _SmdGridInv.xyz;
    float3 uvwFar = (p + n * _SmdGen0.w - _SmdGridMin.xyz) * _SmdGridInv.xyz;
    float vNear = SAMPLE_TEXTURE3D_LOD(_SmdGeo, sampler_SmdGeo, uvwNear, 0).r;
    float vFar = SAMPLE_TEXTURE3D_LOD(_SmdGeo, sampler_SmdGeo, uvwFar, 0).r;
    float gridEdge = saturate((vNear - 0.5) * _SmdGen0.x);
    float cavity = max(saturate((0.5 - vNear) * _SmdGen0.y), saturate((0.5 - vFar) * _SmdGen0.y) * 0.8);
    float bandNoise = SMDNoise(p, sc0.x * 2.0, seed + 0.91).r;
    float edge, corner = 0.0, edgeDust;
    if (_SmdEdge.z > 0.5)
    {
        float dEdge = SMDMin3(edgeConvex);
        edgeDust = dEdge < SMD_NO_EDGE ? 1.0 - smoothstep(0.0, _SmdEdge.x * 1.8, dEdge) : 0.0;
        float dCorner = SMDMin3(edgeConcave);
        float wE = _SmdEdge.x * (0.35 + 1.3 * bandNoise);              // the band wanders along the edge
        edge = dEdge < SMD_NO_EDGE ? 1.0 - smoothstep(0.0, wE, dEdge) : 0.0;
        corner = dCorner < SMD_NO_EDGE ? 1.0 - smoothstep(0.0, _SmdEdge.y * (0.5 + bandNoise), dCorner) : 0.0;
        cavity = max(cavity, corner * 0.9);
    }
    else
    {
        edge = gridEdge * saturate(1.6 - vFar * 1.2);
        edgeDust = edge;
    }
    float up = saturate(n.y), down = saturate(-n.y), side = 1.0 - abs(n.y);
    float h = p.y - _SmdHeights.w;
    m.edge = edge;
    m.cavity = cavity;

    // ---- base weathering masks (no randomness of their own: they follow the shape)
    {
        float dE = _SmdEdge.z > 0.5 ? SMDMin3(edgeConvex) : SMD_NO_EDGE;
        float dC = _SmdEdge.z > 0.5 ? SMDMin3(edgeConcave) : SMD_NO_EDGE;
        m.hilite = dE < SMD_NO_EDGE ? 1.0 - smoothstep(0.0, _SmdEdge.x * 0.45, dE) : gridEdge * 0.5;
        m.panel = dE < SMD_NO_EDGE ? smoothstep(0.0, 0.22, dE) : 1.0;
        m.wash = _SmdEdge.z > 0.5 ? (dC < SMD_NO_EDGE ? 1.0 - smoothstep(0.0, _SmdEdge.y * 0.5, dC) : 0.0)
                                  : saturate((cavity - 0.4) * 2.0) * 0.6;
        m.filter = SMDNoise(p, _SmdBase2.x, seed + 0.05).rgb - 0.5;
        // streaks (rain marks): thin vertical strands on vertical faces. Each strand starts at its own height and
        // fades out downwards over the streak length, like grime washed down from a ledge or a bolt
        float sideK = saturate((side - 0.35) / 0.4);
        float3 col = float3(p.x, 0.0, p.z);
        float dens = _SmdBase2.y;
        float s1 = SMDNoise(col, dens * 2.5, seed + 0.33).r;
        float s2 = SMDNoise(col, dens * 3.2, seed + 0.57).g;
        float start1 = _SmdHeights.w + SMDNoise(col, dens * 0.35, seed + 0.13).b * _SmdBase2.w * 1.1;
        float start2 = _SmdHeights.w + SMDNoise(col, dens * 0.3, seed + 0.79).a * _SmdBase2.w * 1.1;
        float len = max(_SmdBase2.z, 0.05);
        float t1 = (start1 - p.y) / len, t2 = (start2 - p.y) / (len * 0.7);
        float fall1 = smoothstep(0.0, 0.06, t1) * (1.0 - smoothstep(0.2, 1.0, t1));
        float fall2 = smoothstep(0.0, 0.06, t2) * (1.0 - smoothstep(0.1, 1.0, t2));
        float wobble = SMDNoise(p * float3(1.0, 0.25, 1.0), dens, seed + 0.41).r;   // strands thin out here and there
        m.streakDark = smoothstep(0.7, 0.8, s1) * fall1 * smoothstep(0.3, 0.55, wobble) * sideK;
        m.streakLight = smoothstep(0.74, 0.84, s2) * fall2 * sideK;
    }

    // ---- wear
    float blob = SMDNoise(p, sc0.x, seed).r;
    float spots = saturate((SMDNoise(p, sc0.x * 3.0, seed).g - 0.94) * 16.0) * 0.6;   // rare small chips on flat areas
    m.chip = SMDCover(edge * 1.3 * (blob * 1.1 + 0.2) + spots, a0.x, 0.25);
    float lx = 1.0 - saturate(abs(SMDNoise(p * float3(0.05, 1.0, 1.0), sc0.y * 2.0, seed).b - 0.5) * 12.0);
    float lz = 1.0 - saturate(abs(SMDNoise(p * float3(1.0, 0.3, 0.05), sc0.y * 2.0, seed + 0.31).b - 0.5) * 12.0);
    float patch = SMDCover(edge * 0.4 + SMDNoise(p, sc0.y * 0.5, seed + 0.17).r * 0.8, a0.y, 0.3);
    m.scratch = max(lx, lz) * patch;
    float streak = saturate((SMDNoise(p * float3(1.0, 0.08, 1.0), sc0.z, seed + 0.53).r - 0.6) * 3.0) * side;
    float v = cavity * 0.6 + corner * 0.4 + m.chip * 0.6 + streak * 0.4 + edge * 0.35;
    m.rust = SMDCover(v * (SMDNoise(p, sc0.z, seed + 0.71).g * 0.8 + 0.6), a0.z, 0.3);
    // grime / soot haze: only around the muzzles and exhausts (a wider area than the soot itself),
    // a little darker in cavities and underneath there
    float nearSrc = max(max(SMDBurnSource(p, _SmdSrcP0, _SmdSrcD0, 2.5), SMDBurnSource(p, _SmdSrcP1, _SmdSrcD1, 2.5)),
                        max(SMDBurnSource(p, _SmdSrcP2, _SmdSrcD2, 2.5), SMDBurnSource(p, _SmdSrcP3, _SmdSrcD3, 2.5)));
    float gv = nearSrc * (0.75 + cavity * 0.35 + down * 0.15) * (SMDNoise(p, sc0.w * 0.5, seed + 0.23).r * 0.3 + 0.85);
    m.grime = saturate(gv * a0.w * 1.8);

    // ---- dirt from the ground up (a height gradient, noise only shapes the border) + thrown up by the tracks
    float kick = exp(-min(SMDBoxDist(p, _SmdTrackMin0, _SmdTrackMax0), SMDBoxDist(p, _SmdTrackMin1, _SmdTrackMax1))
                     / max(_SmdKick.x, 1e-3)) * _SmdKick.y;
    float hm = 1.0 - smoothstep(0.0, max(_SmdHeights.x, 1e-3), h);
    float hs = 1.0 - smoothstep(0.0, max(_SmdHeights.y, 1e-3), h);
    float nb1 = SMDNoise(p, sc1.x, seed + 0.41).r - 0.5;
    float nb2 = SMDNoise(p, sc1.y, seed + 0.61).g - 0.5;
    float mudV = max(hm, kick * 0.85) + down * hm * 0.2;
    m.mud = SMDCover(mudV + nb1 * 0.45, a1.x, 0.15);
    float soilV = max(hs, kick) + cavity * 0.15;
    m.soil = SMDCover(soilV + nb2 * 0.55, a1.y, 0.2);
    // sand: a fine dust film over the whole vehicle (long drives in the desert) — no spots: thicker on top surfaces,
    // on edges and in corners, thinner underneath; only a broad, gentle variation
    float gentle = 0.82 + 0.36 * SMDNoise(p, max(sc1.z * 0.15, 0.1), seed + 0.83).r;
    float film = 0.13 + up * up * 0.62 + edgeDust * 0.5 + corner * 0.25 + cavity * 0.1 - down * 0.1;
    m.sand = saturate(film * a1.z * gentle);
    m.wet = saturate(m.mud * a1.w * 1.5);

    // ---- soot around the muzzle / exhausts
    float b = max(max(SMDBurnSource(p, _SmdSrcP0, _SmdSrcD0, 1.0), SMDBurnSource(p, _SmdSrcP1, _SmdSrcD1, 1.0)),
                  max(SMDBurnSource(p, _SmdSrcP2, _SmdSrcD2, 1.0), SMDBurnSource(p, _SmdSrcP3, _SmdSrcD3, 1.0)));
    m.burn = saturate(b * (SMDNoise(p, 9.0, seed + 0.37).r * 0.6 + 0.7) * _SmdBurn.x * 1.4);

    // ---- hand painting
    if (_SmdGen1.z > 0.5)
    {
        float3 uvwP = (p - _SmdGridMin.xyz) * _SmdGridInv.xyz;
        float4 p0 = SAMPLE_TEXTURE3D_LOD(_SmdPaint0, sampler_SmdPaint0, uvwP, 0) * 2.0 - 1.0;
        float4 p1 = SAMPLE_TEXTURE3D_LOD(_SmdPaint1, sampler_SmdPaint0, uvwP, 0) * 2.0 - 1.0;
        m.chip = saturate(m.chip + p0.x); m.scratch = saturate(m.scratch + p0.y);
        m.rust = saturate(m.rust + p0.z); m.grime = saturate(m.grime + p0.w);
        m.mud = saturate(m.mud + p1.x); m.soil = saturate(m.soil + p1.y);
        m.sand = saturate(m.sand + p1.z); m.wet = saturate(m.wet + p1.w);
    }

    // ---- crisp borders, grain and bodies
    float fine = _SmdGen1.x;
    float4 d0 = SMDNoise(p, 35.0 * fine, seed + 0.11);
    float4 d1 = SMDNoise(p, 55.0 * fine, seed + 0.29);
    float4 d2 = SMDNoise(p, 22.0 * fine, seed + 0.47);
    float4 d3 = SMDNoise(p, 110.0 * fine, seed + 0.67);
    float w = _SmdGen1.y;
    m.chip = SMDCrisp(m.chip, d0.g, w);
    m.rust = SMDCrisp(m.rust, d1.r, w * 1.6);
    // grime stays a soft haze (no crisp border)
    float mudBody = m.mud;
    m.mud = SMDCrisp(m.mud, d2.r, w);
    m.soil = SMDCrisp(m.soil, d0.a, w * 2.0);
    m.burn = SMDCrisp(m.burn, d2.b, w * 5.0);
    m.grain = d3.g;
    m.clump = d0.b;
    m.thick = saturate(mudBody * 1.5 - 0.3);
    m.relief = m.mud * (0.25 + 0.75 * d2.g) * (0.6 + m.thick) + m.soil * (0.2 + 0.8 * d0.b) * 0.6
             + m.sand * d3.g * 0.05 + m.rust * d1.b * 0.4;
    return m;
}

// Track links (_SMD_BELT): they travel along the belt, so no shape grid and no rest pose: mud or sand only (amount in
// _SmdAmount1.x / .z), from the link's own space; _SmdLinkSeed (per link, set by SMDetailsLinkSeed) varies the pattern.
SMDMasks SMDGenerateBelt(float3 p, float3 n)
{
    SMDMasks m = (SMDMasks)0;
    p += _SmdLinkSeed * float3(1.71, 2.37, 3.13);
    float3 seed = _SmdGen1.www * float3(0.137, 0.271, 0.419);
    float scale = 14.0;
    float v = SMDNoise(p, scale, seed + 0.61).g * 0.6 + SMDNoise(p, scale * 0.35, seed + 0.13).r * 0.4 + 0.2;
    float4 d0 = SMDNoise(p, 45.0 * _SmdGen1.x, seed + 0.11);
    float4 d3 = SMDNoise(p, 110.0 * _SmdGen1.x, seed + 0.67);
    m.mud = SMDCrisp(SMDCover(v, _SmdAmount1.x, 0.35), d0.a, _SmdGen1.y * 2.0);
    m.sand = saturate(_SmdAmount1.z * (0.8 + 0.2 * v + saturate(n.y) * 0.3));
    m.wet = m.mud * 0.35;
    m.thick = saturate(m.mud * 1.3 - 0.3);
    m.grain = d3.g;
    m.clump = d0.b;
    m.relief = m.mud * (0.3 + 0.7 * d0.b) * 0.9 + m.sand * d3.g * 0.05;
    return m;
}

// The look: chip, scratch, rust, grime, soil, mud, sand dust film (also over the mud), soot, wet
void SMApplyDetails(float3 restPos, float3 restNormal, float3 edgeConvex, float3 edgeConcave, half paintMask,
                    float partRandom, float3 positionWS, half facing,
                    inout float3 baseColor, inout float metallic, inout float roughness, inout float3 normalWS)
{
    // Face normal in the rest frame, turned towards the side the camera sees. Vertex normals are not used:
    // some SM parts have them pointing inwards, which samples the hollow inside of the part.
    float3 ngRest = cross(ddx(restPos), ddy(restPos));
    float3 ngWS = cross(ddx(positionWS), ddy(positionWS));
    float3 n;
    if (dot(ngRest, ngRest) > 1e-16 && dot(ngWS, ngWS) > 1e-16)
        n = normalize(ngRest) * (dot(ngWS, GetCameraPositionWS() - positionWS) >= 0.0 ? 1.0 : -1.0);
    else
        n = normalize(restNormal) * facing;
#if defined(_SMD_BELT)
    SMDMasks g = SMDGenerateBelt(restPos, n);
#else
    SMDMasks g = SMDGenerate(restPos, n, edgeConvex, edgeConcave);
#endif
    float4 w = saturate(float4(g.chip, g.scratch, g.rust, g.grime) * _SmdStrength0);
    float4 d = saturate(float4(g.mud, g.soil, g.sand, g.wet) * _SmdStrength1);
    float burn = g.burn;

    // debug view: one mask in black and white
    int view = (int)(_SmdView + 0.5);
    if (view > 0)
    {
        float vals[14] = { 0, w.x, w.y, w.z, w.w, d.x, d.y, d.z, d.w, g.edge, g.cavity, burn,
                           max(g.streakDark, g.streakLight * 0.6), g.wash };
        baseColor = vals[min(view, 13)].xxx;
        metallic = 0.0;
        roughness = 1.0;
        return;
    }

#if !defined(_SMD_BELT)
    // ---- base weathering of the paint (model-kit techniques), under all the wear and dirt
    {
        float pk = lerp(0.6, 1.0, paintMask);   // mostly on painted surfaces
        float lum = dot(baseColor, float3(0.2126, 0.7152, 0.0722));
        // colour modulation: higher and upward-facing lighter, lower and undersides darker
        float hRel = saturate((restPos.y - _SmdHeights.w) / max(_SmdBase2.w, 0.3));
        baseColor *= 1.0 + ((hRel - 0.45) * 0.5 + n.y * 0.25) * _SmdBase0.x * 0.6 * pk;
        // fading / sun bleaching on top surfaces: lighter and greyer
        float fade = saturate(n.y) * saturate(n.y) * _SmdBase0.y * pk;
        baseColor = lerp(baseColor, lerp(baseColor, lum.xxx, 0.45) * 1.3 + 0.015, fade * 0.7);
        // a slightly different tone per part (armour plates)
        baseColor *= 1.0 + (partRandom - 0.5) * 0.25 * _SmdBase0.z * pk;
        // dot filter: blended warm / cool mottling
        float3 shift = float3(g.filter.x, g.filter.y * 0.6, -g.filter.x * 0.8) * 0.35 + g.filter.z * 0.25;
        baseColor *= 1.0 + shift * _SmdBase0.w * pk;
        // post-shading: darker towards the panel edges
        baseColor *= lerp(1.0 - 0.3 * _SmdBase1.x * pk, 1.0, g.panel);
        // streaking grime: dark and light strands running down vertical faces
        baseColor *= 1.0 - g.streakDark * 0.35 * _SmdBase1.w;
        baseColor = lerp(baseColor, baseColor * 1.35 + 0.02, g.streakLight * 0.6 * _SmdBase1.w);
        // drybrushed edges, then the pin wash in the corners
        baseColor = lerp(baseColor, baseColor * 1.6 + 0.03, g.hilite * 0.55 * _SmdBase1.z * pk);
        baseColor *= 1.0 - g.wash * 0.6 * _SmdBase1.y;
        roughness = lerp(roughness, saturate(roughness + 0.15), fade);
    }
#endif

    float metalRough = _SmdParams0.x, rustRough = _SmdParams0.y, mudRough = _SmdParams0.z, chipOnTex = _SmdParams0.w;
    float wetDarken = _SmdParams1.x, bumpStrength = _SmdParams1.y, bumpDistance = _SmdParams1.z;

    float chip = w.x * lerp(chipOnTex, 1.0, paintMask);
    float primer = saturate(chip * 2.0);
    float bare = saturate(chip * 2.0 - 1.0);
    baseColor = lerp(baseColor, _SmdPrimer.rgb, primer);
    roughness = lerp(roughness, 0.7, primer);
    baseColor = lerp(baseColor, _SmdMetal.rgb, bare);
    metallic = lerp(metallic, 1.0, bare);
    roughness = lerp(roughness, metalRough, bare);
    float scr = w.y;
    baseColor = lerp(baseColor, _SmdMetal.rgb, scr);
    metallic = lerp(metallic, 1.0, scr);
    roughness = lerp(roughness, metalRough * 0.7, scr);
    float cov = saturate(w.z * 1.5);
    float tone = saturate(w.z * 2.0 - 1.0);
    baseColor = lerp(baseColor, lerp(_SmdRustA.rgb, _SmdRustB.rgb, tone), cov);
    metallic = lerp(metallic, 0.0, cov);
    roughness = lerp(roughness, rustRough, cov);
    baseColor *= lerp(float3(1.0, 1.0, 1.0), _SmdGrime.rgb, w.w);
    roughness = lerp(roughness, 0.8, w.w * 0.5);
    // soil: clumps darker than the dry crust
    float3 soil = _SmdSoil.rgb * lerp(0.72, 1.15, g.clump);
    baseColor = lerp(baseColor, soil, d.y); metallic = lerp(metallic, 0.0, d.y); roughness = lerp(roughness, 0.95, d.y);
    // mud: thick parts darker and glossier (wet inside), thin parts drier and lighter
    float3 mud = _SmdMud.rgb * lerp(1.25, 0.7, g.thick);
    baseColor = lerp(baseColor, mud, d.x); metallic = lerp(metallic, 0.0, d.x);
    roughness = lerp(roughness, lerp(mudRough, mudRough * 0.55, g.thick), d.x);
    // sand dust: a film over everything, half as strong on the mud; never fully hides the colour underneath
    float film = saturate(d.z * 1.1) * lerp(1.0, 0.5, d.x) * 0.8;
    float3 sand = _SmdSand.rgb * lerp(0.93, 1.06, g.grain);
    baseColor = lerp(baseColor, sand, film);
    metallic = lerp(metallic, 0.0, saturate(film * 1.3));
    roughness = lerp(roughness, 0.96, saturate(film * 1.2));
    // soot last: it settles on top of the dirt (black, dry, slightly lighter where it is thin)
    baseColor = lerp(baseColor, _SmdBurnColor.rgb * lerp(1.6, 0.8, burn), burn);
    metallic = lerp(metallic, 0.0, burn);
    roughness = lerp(roughness, 0.85, burn);
    baseColor *= lerp(1.0, wetDarken, d.w * (1.0 - burn));
    roughness *= lerp(1.0, 0.3, d.w * (1.0 - burn));

    float h = d.x * 0.6 + d.y * 0.3 + cov * 0.25 - bare * 0.5 - scr * 0.3 + g.relief;
    normalWS = SMDBump(normalWS, positionWS, h, bumpStrength, bumpDistance * facing);
}

#endif
