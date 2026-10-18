"""Characters. Colour slots content fills in: c0 skin, c1 jumpsuit (torso, sleeves), c2 legs (jumpsuit or pants),
c3 shoes, c4 hair, c5 hat, c6 hands (gloves or skin), c7 backpack. Bones: head, armL, armR,
legL, legR (pivots at neck, shoulders, hips) so content can animate walking and looking.
Visibility groups content can hide: chest, head, hair, hat, arms, pack."""
import math
from m3d import box, cyl, sphere, paint, fixed, group

MODELS = {}


def model(fn):
    MODELS[fn.__name__] = fn
    return fn


@model
def human():
    skin = paint(0.92, grime=0.1, wear=0.0, noise_scale=40)
    cloth = paint(0.9, grime=0.35, wear=0.2, noise_scale=60)
    leather = paint(0.85, rough=0.4, grime=0.2, wear=0.6)
    hair = paint(0.9, grime=0.5, wear=0.0, noise_scale=90)
    eye = fixed("eye", (0.04, 0.04, 0.05), grime=0, wear=0)
    belt = fixed("belt", (0.12, 0.11, 0.1), grime=0.2, wear=0.5)

    hip_z, shoulder_z, neck_z = 0.93, 1.42, 1.5
    # Torso: shirt (jumpsuit colour) tapering to the waist, belt, collar. The chest is its own group so your own
    # first-person body can drop it (it would fill the view when you look down at your legs).
    group("chest")
    box("chest", (0.42, 0.24, 0.32), (0, 0, 1.28), cloth, "c1", bevel=0.07)
    group(None)
    box("waist", (0.36, 0.21, 0.26), (0, 0, 1.03), cloth, "c1", bevel=0.06)
    box("belt", (0.37, 0.22, 0.05), (0, 0, 0.92), belt, "none", bevel=0.02)
    box("hips", (0.36, 0.21, 0.12), (0, 0, 0.86), cloth, "c2", bevel=0.05)
    group("chest")
    cyl("neck", 0.055, 0.1, (0, 0, neck_z), skin, "c0", verts=12)
    group(None)

    # Head (bone "head", pivot at the neck): face, eyes, nose, ears, hair cap.
    hp = (0, 0, neck_z)
    group("head")  # hidden on your own first-person body
    sphere("head", 0.12, (0, 0, 1.63), skin, "c0", "head", hp, scale=(0.95, 1.0, 1.12), segs=20)
    box("jaw", (0.17, 0.14, 0.09), (0, -0.02, 1.53), skin, "c0", "head", hp, bevel=0.04)
    sphere("nose", 0.022, (0, -0.125, 1.61), skin, "c0", "head", hp, scale=(0.8, 1, 1.2), segs=8)
    for s in (-1, 1):
        sphere(f"eye{s}", 0.017, (0.045 * s, -0.108, 1.655), eye, "none", "head", hp, segs=8)
        sphere(f"ear{s}", 0.03, (0.118 * s, 0, 1.62), skin, "c0", "head", hp, scale=(0.4, 0.8, 1.2), segs=8)
    group("hair")
    sphere("hair", 0.128, (0, 0.012, 1.665), hair, "c4", "head", hp, scale=(0.98, 1.02, 0.95), segs=20)
    box("fringe", (0.2, 0.05, 0.05), (0, -0.095, 1.73), hair, "c4", "head", hp, bevel=0.02)
    # Hat: a generic cap + brim in the worn hat's colour.
    group("hat")
    hat = paint(0.9, grime=0.4, wear=0.3, noise_scale=50)
    sphere("hatcrown", 0.138, (0, 0.01, 1.7), hat, "c5", "head", hp, scale=(1.0, 1.04, 0.8), segs=20)
    cyl("hatband", 0.14, 0.05, (0, 0.01, 1.69), hat, "c5", "head", hp, verts=20, bevel=0.01)
    box("hatbrim", (0.24, 0.12, 0.018), (0, -0.14, 1.675), hat, "c5", "head", hp, rot=(0.12, 0, 0), bevel=0.008)
    group(None)

    # Arms (bones armL/armR at the shoulders): sleeve, forearm skin, hand. Group "arms": your own body hides them
    # (the first-person view model draws its own arms).
    group("arms")
    for s, bone in ((-1, "armL"), (1, "armR")):
        sx = 0.25 * s
        piv = (sx, 0, shoulder_z)
        sphere(f"shoulder{s}", 0.075, (sx, 0, shoulder_z - 0.02), cloth, "c1", bone, piv, segs=12)
        cyl(f"upper{s}", 0.058, 0.3, (sx + 0.01 * s, 0, shoulder_z - 0.18), cloth, "c1", bone, piv, verts=12, radius2=0.052)
        cyl(f"fore{s}", 0.048, 0.26, (sx + 0.015 * s, 0, shoulder_z - 0.45), skin, "c0", bone, piv, verts=12, radius2=0.04)
        sphere(f"hand{s}", 0.05, (sx + 0.015 * s, -0.005, shoulder_z - 0.62), skin, "c6", bone, piv, scale=(0.7, 0.9, 1.25), segs=10)

    group(None)
    # Legs (bones legL/legR at the hips): thigh, shin, shoe.
    for s, bone in ((-1, "legL"), (1, "legR")):
        lx = 0.1 * s
        piv = (lx, 0, hip_z)
        cyl(f"thigh{s}", 0.085, 0.45, (lx, 0, hip_z - 0.24), cloth, "c2", bone, piv, verts=12, radius2=0.068)
        cyl(f"shin{s}", 0.066, 0.4, (lx, 0, hip_z - 0.64), cloth, "c2", bone, piv, verts=12, radius2=0.056)
        box(f"shoe{s}", (0.11, 0.26, 0.09), (lx, -0.04, 0.045), leather, "c3", bone, piv, bevel=0.035)

    # Backpack on the back (+Y), shown when something is in the back slot.
    group("pack")
    pack = paint(0.88, grime=0.45, wear=0.4, noise_scale=55)
    box("pack", (0.34, 0.16, 0.42), (0, 0.2, 1.2), pack, "c7", bevel=0.05)
    box("packpocket", (0.26, 0.06, 0.16), (0, 0.29, 1.08), pack, "c7", bevel=0.025)
    for s in (-1, 1):
        box(f"strap{s}", (0.04, 0.27, 0.03), (0.12 * s, 0.0, 1.43), belt, "none", bevel=0.01)
    group(None)
    return 1024
