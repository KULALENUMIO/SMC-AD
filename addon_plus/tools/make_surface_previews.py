# SPDX-License-Identifier: GPL-3.0-or-later
"""Makes the small tileable textures used only by the surface-type thumbnails of SMC-AD Surfaces.

    python make_surface_previews.py            (needs numpy and Pillow; not needed by the add-on itself)

Writes smc_ad_surfaces/preview_textures/<id>_color.png and <id>_height.png (+ <id>_mask.png = paint
for metal_painted). They only have to read at a glance in a 64 px icon ("that's wood"), not be realistic.
Every pattern is periodic, so the textures tile.
"""

import os

import numpy as np
from PIL import Image

N = 256
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "smc_ad_surfaces", "preview_textures")
rng = np.random.default_rng(7)

y, x = np.mgrid[0:N, 0:N] / N  # 0..1, periodic over the tile


def noise(beta=2.0, lo=1.0, hi=None, aspect=(1.0, 1.0)):
    """Periodic fractal noise (filtered white noise), 0..1. aspect (x, y): a small value makes the features thin across that axis
    (aspect=(1, 0.03) gives long streaks along X)."""
    f = np.fft.fftfreq(N) * N
    fy, fx = np.meshgrid(f, f, indexing="ij")
    r = np.sqrt((fx * aspect[0]) ** 2 + (fy * aspect[1]) ** 2)
    amp = np.where(r < lo, 0.0, 1.0 / np.maximum(r, 1e-6) ** (beta / 2))
    if hi is not None:
        amp = amp * np.exp(-(r / hi) ** 2)
    spec = np.fft.fft2(rng.standard_normal((N, N))) * amp
    n = np.real(np.fft.ifft2(spec))
    return (n - n.min()) / (n.max() - n.min())


def mix(a, b, t):
    t = np.clip(t, 0, 1)[..., None]
    return np.asarray(a) * (1 - t) + np.asarray(b) * t


def smooth(e0, e1, v):
    t = np.clip((v - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def save(sid, color, height, mask=None):
    os.makedirs(OUT, exist_ok=True)
    Image.fromarray((np.clip(color, 0, 1) * 255 + 0.5).astype(np.uint8), "RGB").save(os.path.join(OUT, sid + "_color.png"))
    h = (height - height.min()) / max(height.max() - height.min(), 1e-6)
    Image.fromarray((h * 255 + 0.5).astype(np.uint8), "L").save(os.path.join(OUT, sid + "_height.png"))
    if mask is not None:
        Image.fromarray((np.clip(mask, 0, 1) * 255 + 0.5).astype(np.uint8), "L").save(os.path.join(OUT, sid + "_mask.png"))


# ---- 標準（SM）: plain grey panels, like untextured blocks
def standard():
    gx, gy = (x * 2) % 1, (y * 2) % 1
    edge = np.minimum(np.minimum(gx, 1 - gx), np.minimum(gy, 1 - gy))
    seam = 1 - smooth(0.0, 0.03, edge)
    n = noise(2.5, hi=40)
    color = mix((0.62, 0.63, 0.65), (0.30, 0.31, 0.33), seam * 0.9)
    color = color * (0.94 + 0.12 * n[..., None])
    save("standard", color, 1 - seam + 0.05 * n)


# ---- 塗装金属: orange paint, chipped at random to bare steel, a few scratches
def metal_painted():
    chips = noise(2.2, lo=2, hi=70)
    paint = smooth(0.33, 0.37, chips)                     # 0 = chipped
    scratch = noise(2.0, lo=4, aspect=(0.06, 1.0))
    paint = paint * (1 - smooth(0.88, 0.92, scratch))
    steel = mix((0.50, 0.51, 0.53), (0.70, 0.71, 0.73), noise(2.0, aspect=(1.0, 0.05)))
    orange = np.array((0.90, 0.38, 0.08)) * (0.92 + 0.1 * noise(3.0, hi=30))[..., None]
    color = mix(steel, orange, paint)
    save("metal_painted", color, paint * 0.8 + 0.2 * chips, mask=paint)


# ---- 地金: brushed steel (long streaks along X) with a couple of scratches
def metal_bare():
    streak = noise(1.6, lo=1, aspect=(1.0, 0.03))
    fine = noise(1.2, lo=8, aspect=(1.0, 0.02))
    scratch = smooth(0.9, 0.94, noise(2.0, lo=3, aspect=(0.05, 1.0)))
    v = 0.55 + 0.25 * streak + 0.12 * fine + 0.12 * scratch
    color = np.stack([v * 0.97, v * 0.99, v * 1.02], -1)
    save("metal_bare", color, 0.6 * streak + 0.4 * fine)


# ---- ゴム: near-black tyre tread (chevrons)
def rubber():
    vx = np.abs((x * 2) % 1 - 0.5)                       # V shape across each half
    chevron = (y * 4 + vx * 1.6) % 1
    groove = smooth(0.0, 0.08, chevron) * (1 - smooth(0.55, 0.63, chevron))   # 1 = raised block
    n = noise(2.0, lo=6)
    v = 0.05 + 0.07 * groove + 0.03 * n
    color = np.stack([v, v, v * 1.05], -1)
    save("rubber", color, groove + 0.1 * n)


# ---- プラスチック: glossy saturated blue, faint moulded speckle
def plastic():
    n = noise(1.0, lo=20)
    color = np.array((0.12, 0.42, 0.88)) * (0.97 + 0.06 * n)[..., None]
    save("plastic", color, 0.15 * n)


# ---- 木: four planks, grain along Y, darker seams
def wood():
    plank = np.floor(x * 4)
    warp = noise(3.0, lo=1, hi=10)
    offset = (plank * 0.37) % 1
    rings = (np.sin(2 * np.pi * ((x * 4 % 1) * 7 + warp * 1.2 + offset * 5)) * 0.5 + 0.5) ** 1.5
    fibre = noise(1.5, lo=10, aspect=(0.04, 1.0))
    tone = 0.6 * rings + 0.25 * fibre + 0.15 * ((plank * 0.53) % 1)
    seam = 1 - smooth(0.0, 0.02, np.minimum(x * 4 % 1, 1 - x * 4 % 1))
    color = mix((0.70, 0.47, 0.25), (0.40, 0.23, 0.10), tone)
    color = mix(color, (0.18, 0.10, 0.05), seam)
    save("wood", color, 1 - seam * 0.8 - 0.2 * rings)


# ---- 石・コンクリート: grey, mottled, pores and pebbles
def stone():
    mottle = noise(2.2, lo=1, hi=60)
    grain = noise(0.6, lo=30)
    pores = smooth(0.80, 0.86, noise(2.0, lo=18, hi=110))
    pebbles = smooth(0.70, 0.76, noise(2.6, lo=6, hi=60))
    v = 0.48 + 0.18 * mottle + 0.10 * grain - 0.25 * pores + 0.08 * pebbles
    color = np.stack([v * 1.02, v, v * 0.96], -1)
    save("stone", color, 0.4 * mottle + 0.3 * grain - 0.6 * pores + 0.3 * pebbles)


if __name__ == "__main__":
    for make in (standard, metal_painted, metal_bare, rubber, plastic, wood, stone):
        make()
    print("written to", os.path.normpath(OUT))
