"""Animals. Facing -Y, feet on the floor. Tints come from the sprite palette: p0 coat, p1 belly/markings.
Bones for walk cycles: quadruped head, tail, legFL/legFR/legBL/legBR; bird head, legL/legR, wingL/wingR;
fish tail, finL/finR. Sizes are for a ~1 m animal; content scales per species."""
from m3d import box, cyl, sphere, paint, fixed

MODELS = {}


def model(fn):
    MODELS[fn.__name__] = fn
    return fn


def _eyes(y, z, spread, r, bone, piv):
    eye = fixed("eye", (0.03, 0.03, 0.03), grime=0, wear=0)
    for s in (-1, 1):
        sphere(f"eye{s}", r, (spread * s, y, z), eye, "none", bone, piv, segs=8)


@model
def quadruped():
    """Generic four-legged mammal (dog, cat, fox, pig, goat, cow...): body, head with snout and ears, tail."""
    fur = paint(0.9, grime=0.3, wear=0.0, noise_scale=80)
    belly = paint(0.92, grime=0.2, wear=0.0, noise_scale=80)
    nose = fixed("nose", (0.05, 0.04, 0.04), grime=0, wear=0)
    sphere("body", 0.22, (0, 0.02, 0.5), fur, "p0", scale=(0.95, 1.65, 0.9), segs=20)
    sphere("belly", 0.18, (0, 0.02, 0.43), belly, "p1", scale=(0.9, 1.5, 0.7), segs=16)
    hp = (0, -0.3, 0.6)
    sphere("neck", 0.12, (0, -0.3, 0.62), fur, "p0", "head", hp, scale=(1, 1.2, 1.1), segs=12)
    sphere("head", 0.15, (0, -0.42, 0.74), fur, "p0", "head", hp, scale=(0.95, 1.05, 0.9), segs=18)
    sphere("snout", 0.08, (0, -0.56, 0.7), belly, "p1", "head", hp, scale=(0.9, 1.3, 0.8), segs=12)
    sphere("nose", 0.03, (0, -0.665, 0.72), nose, "none", "head", hp, segs=8)
    for s in (-1, 1):
        cyl(f"ear{s}", 0.055, 0.12, (0.09 * s, -0.4, 0.88), fur, "p0", "head", hp, verts=8, radius2=0.01)
    _eyes(-0.52, 0.79, 0.065, 0.022, "head", hp)
    tp = (0, 0.36, 0.56)
    cyl("tail", 0.04, 0.32, (0, 0.5, 0.62), fur, "p0", "tail", tp, verts=8, radius2=0.015)
    for name, x, y in (("legFL", -1, -1), ("legFR", 1, -1), ("legBL", -1, 1), ("legBR", 1, 1)):
        piv = (0.11 * x, 0.22 * y, 0.45)
        cyl(name, 0.055, 0.42, (0.11 * x, 0.22 * y, 0.24), fur, "p0", name, piv, verts=10, radius2=0.042)
        sphere(name + "paw", 0.05, (0.11 * x, 0.22 * y - 0.02, 0.03), belly, "p1", name, piv, scale=(1, 1.3, 0.6), segs=8)
    return 512


@model
def bird():
    """Chicken/duck: round body, head with beak, wings, tail feathers, thin legs."""
    feather = paint(0.92, grime=0.25, wear=0.0, noise_scale=90)
    beak = fixed("beak", (0.9, 0.55, 0.1), grime=0.1, wear=0)
    leg = fixed("birdleg", (0.85, 0.55, 0.15), grime=0.1, wear=0)
    sphere("body", 0.2, (0, 0.03, 0.42), feather, "p0", scale=(0.9, 1.2, 0.95), segs=18)
    box("tailfeathers", (0.16, 0.12, 0.2), (0, 0.26, 0.55), feather, "p0", rot=(0.5, 0, 0), bevel=0.05)
    hp = (0, -0.16, 0.55)
    sphere("head", 0.1, (0, -0.2, 0.7), feather, "p0", "head", hp, segs=14)
    cyl("beak", 0.035, 0.08, (0, -0.31, 0.69), beak, "none", "head", hp, axis="X", verts=8, radius2=0.005)
    _eyes(-0.27, 0.73, 0.055, 0.016, "head", hp)
    for s, bone in ((-1, "wingL"), (1, "wingR")):
        sphere(f"wing{s}", 0.13, (0.17 * s, 0.05, 0.45), feather, "p1", bone, (0.15 * s, 0.0, 0.5), scale=(0.3, 1.1, 0.7), segs=12)
    for s, bone in ((-1, "legL"), (1, "legR")):
        piv = (0.07 * s, 0.02, 0.26)
        cyl(f"leg{s}", 0.015, 0.26, (0.07 * s, 0.02, 0.13), leg, "none", bone, piv, verts=6)
        box(f"foot{s}", (0.07, 0.1, 0.015), (0.07 * s, -0.02, 0.008), leg, "none", bone, piv, bevel=0.005)
    return 512


@model
def fish():
    """Space carp: a fat fish body with teeth, fins and a tail, floating at mid height."""
    scales = paint(0.9, grime=0.35, wear=0.1, noise_scale=70)
    fin = paint(0.85, grime=0.2, wear=0.0, noise_scale=50)
    teeth = fixed("teeth", (0.95, 0.93, 0.85), grime=0.1, wear=0)
    sphere("body", 0.25, (0, 0, 0.8), scales, "p0", scale=(0.75, 1.6, 0.95), segs=20)
    sphere("belly", 0.2, (0, -0.05, 0.72), fin, "p1", scale=(0.65, 1.4, 0.7), segs=16)
    box("mouth", (0.22, 0.04, 0.06), (0, -0.4, 0.74), teeth, "none", bevel=0.015)
    _eyes(-0.3, 0.88, 0.12, 0.035, None, None)
    tp = (0, 0.36, 0.8)
    box("tail", (0.04, 0.3, 0.4), (0, 0.5, 0.8), fin, "p1", "tail", tp, bevel=0.03)
    box("dorsal", (0.03, 0.3, 0.2), (0, 0.05, 1.05), fin, "p1", bevel=0.03)
    for s, bone in ((-1, "finL"), (1, "finR")):
        box(f"fin{s}", (0.2, 0.14, 0.03), (0.22 * s, -0.05, 0.72), fin, "p1", bone, (0.15 * s, -0.05, 0.75),
            rot=(0, 0.4 * s, 0), bevel=0.012)
    return 512
