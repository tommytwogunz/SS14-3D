"""Furniture and fixtures. Each function builds one model's parts and returns its texture size (or None)."""
from m3d import box, cyl, sphere, slats, paint, fixed, decal
import math

MODELS = {}


def model(fn):
    MODELS[fn.__name__] = fn
    return fn


def _dark_metal():
    return fixed("darkmetal", (0.09, 0.09, 0.1), rough=0.4, wear=1.0, grime=0.3)


@model
def locker():
    """Tall storage locker: body in the sprite's main colour, door in its second colour, vents, handle, lock light."""
    w, d, h = 0.84, 0.6, 1.95
    body = paint(0.92)
    trim = paint(0.8)
    dark = _dark_metal()
    box("body", (w, d, h - 0.06), (0, 0, 0.06 + (h - 0.06) / 2), body, "p0", bevel=0.02)
    box("plinth", (w - 0.06, d - 0.06, 0.07), (0, 0, 0.035), dark, "none")
    # Door: a slightly proud panel on the front, hinged on the left edge.
    hinge = (-w / 2 + 0.03, -d / 2 - 0.02, 0)
    dz0, dz1 = 0.12, h - 0.06
    box("door", (w - 0.08, 0.035, dz1 - dz0), (0, -d / 2 - 0.012, (dz0 + dz1) / 2), trim, "p1", "door", hinge, bevel=0.01)
    slats("vent", 5, (w - 0.3, 0.02, 0.018), (0, -d / 2 - 0.034, dz1 - 0.12), (0, 0, -0.04), dark, "none", "door", hinge)
    slats("ventlow", 3, (w - 0.3, 0.02, 0.018), (0, -d / 2 - 0.034, dz0 + 0.18), (0, 0, -0.04), dark, "none", "door", hinge)
    box("handle", (0.035, 0.05, 0.28), (w / 2 - 0.12, -d / 2 - 0.05, 1.0), fixed("chrome", (0.7, 0.72, 0.75), rough=0.2, wear=0.2, grime=0.2), "none", "door", hinge, bevel=0.01)
    box("lock", (0.09, 0.03, 0.13), (w / 2 - 0.12, -d / 2 - 0.04, 1.25), dark, "none", "door", hinge, bevel=0.006)
    box("light", (0.05, 0.02, 0.03), (w / 2 - 0.12, -d / 2 - 0.058, 1.27), fixed("lamp", (0.95, 0.95, 0.95), grime=0, wear=0), "c0", "door", hinge, bevel=0.003)
    return 512


def _chrome():
    return fixed("chrome", (0.7, 0.72, 0.75), rough=0.2, wear=0.2, grime=0.2)


@model
def vending():
    """Vending machine: a deep cabinet whose front shows the machine's own sprite (so every vendor stays recognisable)."""
    w, d, h = 0.92, 0.8, 1.85
    body = paint(0.9)
    dark = _dark_metal()
    box("cabinet", (w, d, h - 0.08), (0, 0.02, 0.08 + (h - 0.08) / 2), body, "p0", bevel=0.03)
    box("plinth", (w - 0.04, d - 0.04, 0.09), (0, 0.02, 0.045), dark, "none")
    box("hood", (w + 0.03, d + 0.04, 0.08), (0, 0.02, h - 0.02), body, "p1", bevel=0.02)
    decal("front", w - 0.04, h - 0.12, (0, -d / 2 + 0.02 - 0.006, 0.08 + (h - 0.12) / 2 + 0.01))
    return 512


@model
def computer():
    """Console: desk cabinet, sloped keyboard deck and a monitor whose screen shows the console's screen layer."""
    body = paint(0.9)
    dark = _dark_metal()
    box("desk", (0.9, 0.62, 0.78), (0, 0.04, 0.39), body, "p0", bevel=0.025)
    box("kick", (0.86, 0.06, 0.08), (0, -0.25, 0.04), dark, "none")
    box("deck", (0.9, 0.36, 0.06), (0, -0.12, 0.84), body, "p0", rot=(math.radians(-12), 0, 0), bevel=0.015)
    box("keys", (0.6, 0.2, 0.02), (0, -0.14, 0.875), dark, "none", rot=(math.radians(-12), 0, 0), bevel=0.004)
    box("monitor", (0.82, 0.3, 0.62), (0, 0.18, 1.2), body, "p1", rot=(math.radians(10), 0, 0), bevel=0.03)
    box("bezel", (0.74, 0.02, 0.5), (0, 0.025, 1.21), dark, "none", rot=(math.radians(10), 0, 0), bevel=0.006)
    decal("screen", 0.68, 0.44, (0, 0.009, 1.21), "s1", tilt=math.radians(10))
    return 512


@model
def crate():
    """Cargo crate: ribbed box, lid hinged at the back (bone "lid"), latch."""
    w, d, h = 0.92, 0.7, 0.66
    body = paint(0.9)
    rib = paint(0.75)
    dark = _dark_metal()
    box("body", (w, d, h - 0.08), (0, 0, (h - 0.08) / 2), body, "p0", bevel=0.02)
    for x in (-w / 2 + 0.06, w / 2 - 0.06):
        box(f"rib{x}", (0.07, d + 0.02, h - 0.1), (x, 0, (h - 0.1) / 2), rib, "p1", bevel=0.012)
    hinge = (0, d / 2, h - 0.08)
    box("lid", (w + 0.02, d + 0.02, 0.08), (0, 0, h - 0.04), body, "p0", "lid", hinge, bevel=0.02)
    box("latch", (0.12, 0.03, 0.1), (0, -d / 2 - 0.02, h - 0.1), dark, "none", "lid", hinge, bevel=0.006)
    return 512


@model
def chair():
    """Metal chair facing -Y: seat, backrest, four legs."""
    seat = paint(0.9)
    frame = paint(0.6, rough=0.3, wear=0.8)
    box("seat", (0.46, 0.44, 0.06), (0, 0, 0.46), seat, "p0", bevel=0.02)
    box("back", (0.46, 0.05, 0.42), (0, 0.2, 0.76), seat, "p0", rot=(math.radians(-6), 0, 0), bevel=0.02)
    for x in (-0.2, 0.2):
        for y in (-0.18, 0.18):
            cyl(f"leg{x}{y}", 0.018, 0.44, (x, y, 0.22), frame, "p1", verts=8)
        cyl(f"post{x}", 0.016, 0.36, (x, 0.2, 0.66), frame, "p1", verts=8)
    return 256


@model
def armchair():
    """Upholstered comfy chair facing -Y."""
    cloth = paint(0.9, grime=0.3, noise_scale=70)
    wood = fixed("wood", (0.25, 0.14, 0.07), wear=0.6)
    box("base", (0.8, 0.72, 0.36), (0, 0.02, 0.22), cloth, "p0", bevel=0.06)
    box("cushion", (0.56, 0.58, 0.12), (0, -0.02, 0.44), cloth, "p0", bevel=0.05)
    box("back", (0.8, 0.2, 0.62), (0, 0.28, 0.68), cloth, "p0", bevel=0.07)
    for x in (-0.34, 0.34):
        box(f"arm{x}", (0.13, 0.7, 0.26), (x, 0.0, 0.5), cloth, "p0", bevel=0.05)
    for x in (-0.33, 0.33):
        for y in (-0.28, 0.32):
            cyl(f"foot{x}{y}", 0.03, 0.06, (x, y, 0.03), wood, "none", verts=8)
    return 512


@model
def officechair():
    """Swivel office chair facing -Y: cushioned seat and back on a gas lift and five-star base."""
    cloth = paint(0.9, grime=0.3, noise_scale=70)
    dark = _dark_metal()
    box("seat", (0.5, 0.48, 0.09), (0, 0, 0.5), cloth, "p0", bevel=0.035)
    box("back", (0.46, 0.08, 0.5), (0, 0.24, 0.86), cloth, "p0", rot=(math.radians(-8), 0, 0), bevel=0.035)
    box("spine", (0.06, 0.04, 0.3), (0, 0.24, 0.6), dark, "none", bevel=0.01)
    cyl("lift", 0.03, 0.34, (0, 0, 0.27), _chrome(), "none", verts=10)
    for i in range(5):
        a = i * 2 * math.pi / 5
        box(f"spoke{i}", (0.05, 0.3, 0.04), (math.sin(a) * 0.15, -math.cos(a) * 0.15, 0.07), dark, "none",
            rot=(0, 0, a), bevel=0.01)
        sphere(f"wheel{i}", 0.03, (math.sin(a) * 0.29, -math.cos(a) * 0.29, 0.03), dark, "none", segs=8)
    return 256


@model
def stool():
    """Round stool: cushioned seat on three legs with a foot ring."""
    seat = paint(0.9, grime=0.3)
    frame = paint(0.6, rough=0.3, wear=0.8)
    cyl("seat", 0.2, 0.07, (0, 0, 0.6), seat, "p0", verts=20, bevel=0.025)
    for i in range(3):
        a = i * 2 * math.pi / 3
        box(f"leg{i}", (0.03, 0.03, 0.6), (math.sin(a) * 0.13, math.cos(a) * 0.13, 0.3), frame, "p1",
            rot=(math.cos(a) * 0.12, -math.sin(a) * 0.12, 0), bevel=0.008)
    cyl("ring", 0.15, 0.025, (0, 0, 0.25), frame, "p1", verts=16, bevel=0.005)
    return 256


@model
def bed():
    """Bed: metal frame, mattress, pillow at +Y."""
    frame = paint(0.6, rough=0.3, wear=0.8)
    linen = paint(0.95, grime=0.2, noise_scale=60)
    box("frame", (0.96, 0.98, 0.2), (0, 0, 0.22), frame, "p1", bevel=0.02)
    for x in (-0.44, 0.44):
        for y in (-0.44, 0.44):
            box(f"leg{x}{y}", (0.06, 0.06, 0.14), (x, y, 0.07), frame, "p1", bevel=0.01)
    box("headboard", (0.96, 0.05, 0.5), (0, 0.475, 0.45), frame, "p1", bevel=0.02)
    box("mattress", (0.9, 0.92, 0.16), (0, -0.01, 0.39), linen, "p0", bevel=0.05)
    box("pillow", (0.56, 0.24, 0.09), (0, 0.32, 0.5), linen, "none", bevel=0.04)
    return 512


@model
def canister():
    """Gas canister: painted cylinder with a dark collar, valve and wheel base."""
    paint_ = paint(0.9, wear=0.8)
    dark = _dark_metal()
    cyl("body", 0.3, 1.1, (0, 0, 0.66), paint_, "p0", verts=24, bevel=0.04)
    cyl("band", 0.305, 0.12, (0, 0, 0.9), paint(0.8), "p1", verts=24, bevel=0.01)
    cyl("base", 0.32, 0.1, (0, 0, 0.06), dark, "none", verts=24, bevel=0.015)
    cyl("collar", 0.14, 0.12, (0, 0, 1.26), dark, "none", verts=16)
    cyl("valve", 0.04, 0.12, (0, 0, 1.36), _chrome(), "none", verts=10)
    cyl("wheel", 0.08, 0.02, (0, 0, 1.43), dark, "none", verts=16)
    box("gauge", (0.1, 0.03, 0.1), (0, -0.3, 1.05), _chrome(), "none", bevel=0.01)
    return 512


@model
def plant():
    """Potted plant: pot (sprite's second colour) and a leafy bush (main colour)."""
    pot = paint(0.85, wear=0.4)
    leaf = paint(0.9, grime=0.6, noise_scale=40)
    soil = fixed("soil", (0.12, 0.08, 0.05))
    cyl("pot", 0.2, 0.36, (0, 0, 0.18), pot, "p1", verts=16, radius2=0.25, bevel=0.02)
    cyl("soil", 0.23, 0.02, (0, 0, 0.35), soil, "none", verts=16)
    import random
    rnd = random.Random(7)
    for i in range(14):
        a = rnd.uniform(0, 2 * math.pi)
        r = rnd.uniform(0.0, 0.17)
        z = rnd.uniform(0.5, 1.05)
        s = rnd.uniform(0.12, 0.2)
        sphere(f"leaf{i}", s, (math.cos(a) * r, math.sin(a) * r, z), leaf, "p0", scale=(1, 1, 1.3), segs=10)
    cyl("stem", 0.025, 0.5, (0, 0, 0.55), fixed("stem", (0.2, 0.3, 0.1)), "none", verts=8)
    return 512
