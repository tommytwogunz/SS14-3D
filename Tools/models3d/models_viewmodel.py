"""First-person view model: your arms, in VIEW space (camera at the origin, +X right, +Y forward, +Z up).
The right hand grips at (0.2, 0.5, -0.2), where the game puts the held item. Colour slots: c1 sleeves (jumpsuit/coat),
c6 hands (gloves or skin). Bones armR/armL pivot at the (off-screen) shoulders, for swings, punches and recoil."""
from m3d import limb, sphere, box, paint, fixed

MODELS = {}


def model(fn):
    MODELS[fn.__name__] = fn
    return fn


@model
def viewarms():
    cloth = paint(0.9, grime=0.35, wear=0.25, noise_scale=60)
    skin = paint(0.92, grime=0.1, wear=0.0, noise_scale=40)
    cuff = paint(0.8, grime=0.3, wear=0.4, noise_scale=60)
    for s, bone in ((1, "armR"), (-1, "armL")):
        shoulder = (0.3 * s, -0.12, -0.42)
        elbow = (0.27 * s, 0.18, -0.36)
        wrist = (0.21 * s, 0.43, -0.22) if s > 0 else (-0.22, 0.42, -0.26)
        grip = (0.2, 0.5, -0.2) if s > 0 else (-0.2, 0.48, -0.24)
        limb(f"upper{s}", shoulder, elbow, 0.062, 0.056, cloth, "c1", bone, shoulder)
        limb(f"fore{s}", elbow, wrist, 0.055, 0.045, cloth, "c1", bone, shoulder)
        limb(f"cuff{s}", wrist, tuple(w + (g - w) * 0.25 for w, g in zip(wrist, grip)), 0.048, 0.047, cuff, "c1", bone, shoulder)
        # Fist: palm block, knuckle row, thumb.
        sphere(f"palm{s}", 0.048, grip, skin, "c6", bone, shoulder, scale=(0.9, 1.15, 0.8), segs=14)
        for i in range(4):
            sphere(f"knuckle{s}{i}", 0.017, (grip[0] - 0.024 * s + 0.016 * i * s, grip[1] + 0.045, grip[2] + 0.012),
                   skin, "c6", bone, shoulder, scale=(1, 1.2, 1), segs=8)
        limb(f"thumb{s}", (grip[0] - 0.035 * s, grip[1] - 0.01, grip[2] - 0.01),
             (grip[0] - 0.03 * s, grip[1] + 0.035, grip[2] + 0.02), 0.014, 0.012, skin, "c6", bone, shoulder, verts=8)
    return 512
