"""Stylized, seamless environment materials (Source/HL2-inspired: soft painted surfaces, bevelled seams, gentle grime).
Each material is a 256x256 tile covering 1 m, built from a periodic height field and albedo, then lit once
(top-left light + seam occlusion) so it reads as relief without normal maps. Mostly light greys: the game tints
each surface with the colour of the SS14 tile/wall it replaces, so one material serves every colour variant.
Run: blender -b --factory-startup -P Tools/models3d/materials.py   → Resources/Models3D/Materials/<name>.png
"""
import os, sys
import numpy as np

N = 256
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "..", "Resources", "Models3D", "Materials")
RNG = np.random.default_rng(14)

yy, xx = np.mgrid[0:N, 0:N].astype(np.float32) / N  # 0..1 across the metre


def noise(scale, rough=1.0):
    """Periodic smooth noise in 0..1: white noise low-passed in frequency space (tiles seamlessly)."""
    f = np.fft.fftfreq(N)[:, None] ** 2 + np.fft.fftfreq(N)[None, :] ** 2
    spectrum = np.fft.fft2(RNG.standard_normal((N, N)))
    filt = np.exp(-f * (N / scale) ** 2 / rough)
    n = np.real(np.fft.ifft2(spectrum * filt))
    n -= n.min()
    return n / max(n.max(), 1e-6)


def fbm(scales=(4, 12, 40, 120), weights=(0.5, 0.3, 0.15, 0.05)):
    return sum(w * noise(s) for s, w in zip(scales, weights))


def periodic_dist(u, period):
    """Distance (0..0.5 of a cell) from u to the nearest cell edge, cells of size 1/period."""
    f = (u * period) % 1.0
    return np.minimum(f, 1 - f) / period


def plates(nx, ny, gap=0.006, bevel=0.03, stagger=False):
    """Height 1 on plate tops, 0 in the seams, with a soft bevel."""
    x = xx.copy()
    if stagger:
        row = np.floor(yy * ny)
        x = (x + 0.5 * (row % 2) / nx) % 1.0
    d = np.minimum(periodic_dist(x, nx), periodic_dist(yy, ny))
    return np.clip((d - gap) / bevel, 0, 1) ** 0.6


def cell_tone(nx, ny, amount=0.08, stagger=False):
    """A random brightness per plate/plank, so repeated cells don't look stamped."""
    x = xx.copy()
    if stagger:
        x = (x + 0.5 * (np.floor(yy * ny) % 2) / nx) % 1.0
    ids = (np.floor(x * nx) * 7919 + np.floor(yy * ny) * 104729).astype(np.int64)
    vals = np.random.default_rng(3).uniform(-amount, amount, 1 << 16)
    return 1 + vals[ids % (1 << 16)]


def rivets(nx, ny, r=0.008, inset=0.03):
    h = np.zeros((N, N), np.float32)
    for cx in [(i + 0.0) / nx for i in range(nx)]:
        for cy in [(j + 0.0) / ny for j in range(ny)]:
            for ox, oy in ((inset, inset), (-inset, inset), (inset, -inset), (-inset, -inset)):
                dx = (xx - (cx + ox)) % 1.0
                dx = np.minimum(dx, 1 - dx)
                dy = (yy - (cy + oy)) % 1.0
                dy = np.minimum(dy, 1 - dy)
                h = np.maximum(h, np.clip(1 - np.sqrt(dx * dx + dy * dy) / r, 0, 1) ** 0.5)
    return h


def shade(height, albedo, relief=1.0, ao=0.6):
    """Bake simple lighting: light from the top-left, occlusion where the surface is low."""
    gx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * N * 0.02 * relief
    gy = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) * N * 0.02 * relief
    nrm = np.stack([-gx, -gy, np.ones_like(gx)], -1)
    nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
    light = np.array([-0.45, -0.55, 0.7], np.float32)
    light /= np.linalg.norm(light)
    diffuse = np.clip((nrm * light).sum(-1), 0, 1)
    lit = 1.0 + 0.6 * (diffuse - light[2])  # flat surfaces keep their albedo; slopes facing the light brighten
    occl = 1 - ao * (1 - np.clip(height, 0, 1)) ** 2
    if albedo.ndim == 2:
        albedo = np.repeat(albedo[..., None], 3, -1)
    return np.clip(albedo * (lit * occl)[..., None], 0, 1)


def grime(amount=0.12):
    return 1 - amount * fbm()


def save(name, rgb):
    import bpy
    os.makedirs(OUT, exist_ok=True)
    img = bpy.data.images.new(name, N, N, alpha=rgb.shape[-1] == 4)
    px = np.ones((N, N, 4), np.float32)
    px[..., :rgb.shape[-1]] = np.flipud(rgb)  # Blender pixel rows are bottom-up; RGBA keeps its alpha
    img.pixels[:] = px.ravel()
    img.filepath_raw = os.path.join(OUT, f"{name}.png")
    img.file_format = "PNG"
    img.save()
    print(f"MATERIAL {name}")


def g(v):
    return np.full((N, N), v, np.float32)


MATERIALS = {}


def material(fn):
    MATERIALS[fn.__name__] = fn
    return fn


@material
def floor_plates():
    """Station steel: four 50 cm plates with bevelled seams and corner rivets."""
    h = plates(2, 2) * 0.9 + rivets(2, 2) * 0.1
    return shade(h, g(0.86) * cell_tone(2, 2, 0.05) * grime(0.14))


@material
def floor_tiles():
    """Small ceramic tiles (white/kitchen/medical floors): 4x4 per metre with grout."""
    h = plates(4, 4, gap=0.004, bevel=0.012)
    return shade(h, g(0.92) * cell_tone(4, 4, 0.04) * grime(0.08), relief=0.7)


@material
def floor_checker():
    """Checkerboard tiles (bar, freezer, kitchen variants): alternating light and dark squares."""
    h = plates(4, 4, gap=0.004, bevel=0.012)
    check = ((np.floor(xx * 4) + np.floor(yy * 4)) % 2).astype(np.float32)
    return shade(h, (0.45 + 0.5 * check) * grime(0.08), relief=0.7)


@material
def floor_wood():
    """Wooden planks: six staggered planks per metre with soft grain."""
    h = plates(2, 6, gap=0.004, bevel=0.01, stagger=True)
    # Grain: noise stretched along the planks (x): every 8th column, repeated sideways.
    fine = noise(160)
    grain = 0.6 * fine[:, ::8].repeat(8, 1) + 0.4 * noise(10)
    return shade(h, (0.68 + 0.3 * grain) * cell_tone(2, 6, 0.1, stagger=True) * grime(0.08), relief=0.6, ao=0.5)


@material
def floor_carpet():
    """Carpet: soft woven noise and a faint border weave, no hard seams."""
    weave = 0.5 + 0.5 * np.sin(xx * 2 * np.pi * 64) * np.sin(yy * 2 * np.pi * 64)
    alb = 0.78 + 0.08 * weave + 0.14 * noise(40)
    return shade(g(1.0), alb * grime(0.06), relief=0.0, ao=0.0)


@material
def floor_grating():
    """Maintenance grating: a grid of bars over a dark void."""
    bars = np.maximum(np.clip(1 - periodic_dist(xx, 8) / 0.012, 0, 1), np.clip(1 - periodic_dist(yy, 8) / 0.012, 0, 1))
    h = np.clip(bars * 1.5, 0, 1)
    alb = 0.12 + 0.68 * h
    return shade(h, alb * grime(0.1), relief=1.2, ao=0.3)


@material
def floor_concrete():
    """Concrete: mottled with fine speckles and faint cracks."""
    speck = (noise(160) > 0.72).astype(np.float32) * 0.08
    return shade(g(1.0), (0.8 + 0.15 * fbm() - speck), relief=0.0, ao=0.0)


@material
def floor_diamond():
    """Diamond tread plate (reinforced/engineering floors)."""
    u, v = xx * 10, yy * 10
    a = np.abs(((u + v) % 1.0) - 0.5) + np.abs(((u - v) % 1.0) - 0.5)
    bumps = np.clip(1 - a / 0.25, 0, 1) ** 0.5 * 0.4
    h = plates(1, 1, gap=0.004, bevel=0.02) * 0.6 + bumps
    return shade(h, g(0.8) * grime(0.16), relief=1.0)


@material
def floor_tech():
    """Tech/circuit floors: plates with recessed light channels."""
    h = plates(2, 2, gap=0.01, bevel=0.02)
    channel = (np.clip(1 - periodic_dist(xx + 0.25, 2) / 0.008, 0, 1)) * 0.5
    return shade(h - channel * 0.5, g(0.75) * grime(0.12) + channel * 0.25)


@material
def floor_rock():
    """Asteroid/cave rock: lumpy cells and grit."""
    h = fbm((3, 8, 24, 80), (0.45, 0.3, 0.17, 0.08))
    return shade(h, 0.6 + 0.4 * h, relief=2.0, ao=0.4)


@material
def floor_grass():
    """Grass/plant floors: blade-like streaks."""
    blades = noise(90, rough=0.5) * 0.6 + noise(12) * 0.4
    return shade(blades, 0.55 + 0.45 * blades, relief=0.8, ao=0.3)


@material
def floor_snow():
    """Snow/ice: soft drifts, very bright."""
    h = fbm((2, 6, 30, 100), (0.5, 0.3, 0.15, 0.05))
    return shade(h, g(0.97), relief=0.6, ao=0.2)


@material
def wall_panel():
    """Station wall: 1 m wide panels with a centre belt, bevelled seams and rivets (tiles every metre of height)."""
    seams = np.minimum(periodic_dist(xx, 1), np.clip(np.abs(yy - 0.5) - 0.0, 0, 1) + 1)
    h = np.clip((periodic_dist(xx, 1) - 0.006) / 0.03, 0, 1) ** 0.6
    belt = np.clip(1 - np.abs(yy - 0.5) / 0.05, 0, 1)
    h = h * (1 - 0.35 * belt) + rivets(1, 2, inset=0.05) * 0.15
    return shade(h, g(0.85) * grime(0.14) - belt[..., None].squeeze() * 0.05)


@material
def wall_reinforced():
    """Reinforced wall: heavier plates with cross bracing."""
    h = plates(2, 2, gap=0.01, bevel=0.04)
    brace = np.clip(1 - np.abs(((xx - yy) % 1.0) - 0.5) / 0.03, 0, 1) * 0.25
    return shade(np.clip(h + brace, 0, 1), g(0.7) * grime(0.18))


@material
def ceiling_panel():
    """Ceiling: square acoustic panels in a light frame."""
    h = plates(2, 2, gap=0.012, bevel=0.01)
    perf = (np.sin(xx * 2 * np.pi * 32) * np.sin(yy * 2 * np.pi * 32) > 0.85).astype(np.float32) * 0.06
    return shade(h, (g(0.82) - perf) * grime(0.06), relief=0.6)



@material
def glass_pane():
    """Window glass: a bevelled metal frame (opaque) around a pale, mostly see-through pane with a soft sheen."""
    border = np.minimum(np.minimum(xx, 1 - xx), np.minimum(yy, 1 - yy))
    frame = np.clip(1 - (border - 0.03) / 0.012, 0, 1)
    h = 1 - frame * 0.6
    sheen = 0.5 + 0.5 * np.clip(1 - np.abs((xx + yy) - 0.9) / 0.12, 0, 1)
    rgb = shade(h, g(0.85) * (1 - frame * 0.25), relief=1.0, ao=0.4)
    rgb = rgb * (1 - frame[..., None]) * np.array([0.55, 0.72, 0.85]) * (0.7 + 0.3 * sheen[..., None]) + rgb * frame[..., None]
    alpha = 0.1 + 0.12 * sheen * (1 - frame) + frame
    return np.concatenate([rgb, np.clip(alpha, 0, 1)[..., None]], -1)

if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    for name in args or MATERIALS:
        save(name, MATERIALS[name]())
