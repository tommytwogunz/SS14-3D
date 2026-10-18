"""Tiny modelling + baking toolkit for the SS14 3D models (run inside Blender: blender -b -P build.py -- names...).

A model is a set of parts. Each part is a Blender object with:
  tint  - how the game colours it: "p0".."p2" = the entity's own sprite palette (most common colour first),
          "c0".."c7" = colours content passes in (skin, hair, clothing...), "none" = the baked texture as is.
  bone  - optional name; content can rotate the part about its pivot (doors, limbs).
Tinted parts bake to light grey detail (the tint supplies the colour); fixed parts bake in their own colour.
Units are metres, Z up, the model's front faces -Y (an SS14 entity at rotation 0 faces south).
Output: <out>/<name>.json (triangles per part) and <out>/<name>.png (baked albedo x ambient occlusion).
"""
import bpy, bmesh, json, math, os
import numpy as np
from mathutils import Vector

PARTS = []  # (object, tint, bone, pivot, group)
GROUP = None  # visibility group for parts created now (see group())
V_FLIP = 0  # 1 = store v top-down


def reset():
    global GROUP
    GROUP = None
    PARTS.clear()
    bpy.ops.wm.read_factory_settings(use_empty=True)


def group(name):
    """Parts created after this belong to visibility group `name` (None = always drawn); the game can hide a group."""
    global GROUP
    GROUP = name


def _finish(obj, tint, bone, pivot, mat):
    obj.data.materials.append(mat)
    PARTS.append((obj, tint, bone, tuple(pivot) if pivot else None, GROUP))
    return obj


def material(name, color=(0.8, 0.8, 0.8), rough=0.6, wear=0.5, grime=0.5, noise_scale=18.0):
    """Albedo-only material for baking, layered like painted station hardware:
    base colour x large-scale fade x fine grime x darker concave creases, then lighter scuffed edges and scratches."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    for n in list(nt.nodes):
        nt.nodes.remove(n)
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Roughness"].default_value = rough
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])

    def ramp_of(src, p0, c0, p1, c1):
        r = nt.nodes.new("ShaderNodeValToRGB")
        r.color_ramp.elements[0].position = p0
        r.color_ramp.elements[0].color = (c0, c0, c0, 1)
        r.color_ramp.elements[1].position = p1
        r.color_ramp.elements[1].color = (c1, c1, c1, 1)
        nt.links.new(src, r.inputs["Fac"])
        return r.outputs["Color"]

    def noise(scale, detail=8.0, stretch=None):
        n = nt.nodes.new("ShaderNodeTexNoise")
        n.inputs["Scale"].default_value = scale
        n.inputs["Detail"].default_value = detail
        if stretch:
            coord = nt.nodes.new("ShaderNodeTexCoord")
            mapping = nt.nodes.new("ShaderNodeMapping")
            mapping.inputs["Scale"].default_value = stretch
            nt.links.new(coord.outputs["Object"], mapping.inputs["Vector"])
            nt.links.new(mapping.outputs["Vector"], n.inputs["Vector"])
        return n.outputs["Fac"]

    def mix(kind, a, b, factor=1.0):
        mx = nt.nodes.new("ShaderNodeMix")
        mx.data_type = "RGBA"
        mx.blend_type = kind
        mx.inputs["Factor"].default_value = factor
        nt.links.new(a, mx.inputs[6])
        if isinstance(b, tuple):
            mx.inputs[7].default_value = b
        else:
            nt.links.new(b, mx.inputs[7])
        return mx.outputs[2]

    base = nt.nodes.new("ShaderNodeRGB")
    base.outputs[0].default_value = (*color, 1)
    col = base.outputs[0]
    geo = nt.nodes.new("ShaderNodeNewGeometry")

    col = mix("MULTIPLY", col, ramp_of(noise(2.5, 3.0), 0.3, 1 - 0.12 * grime, 0.7, 1.0))       # faded paint
    col = mix("MULTIPLY", col, ramp_of(noise(noise_scale), 0.4, 1 - 0.38 * grime, 0.72, 1.0))  # grime
    col = mix("MULTIPLY", col, ramp_of(geo.outputs["Pointiness"], 0.44, 1 - 0.5 * grime, 0.5, 1.0))  # creases
    edge = ramp_of(geo.outputs["Pointiness"], 0.5, 0.0, 0.535, 1.0)                              # scuffed edges
    col = mix("SCREEN", col, (1, 1, 1, 1), 0.0)
    worn = col.node
    amt = nt.nodes.new("ShaderNodeMath")
    amt.operation = "MULTIPLY"
    amt.inputs[1].default_value = 0.55 * wear
    nt.links.new(edge, amt.inputs[0])
    nt.links.new(amt.outputs[0], worn.inputs["Factor"])
    scratch = ramp_of(noise(60.0, 2.0, stretch=(1.0, 1.0, 14.0)), 0.66, 0.0, 0.7, 1.0)          # fine vertical scratches
    amt2 = nt.nodes.new("ShaderNodeMath")
    amt2.operation = "MULTIPLY"
    amt2.inputs[1].default_value = 0.25 * wear
    nt.links.new(scratch, amt2.inputs[0])
    col = mix("SCREEN", col, (1, 1, 1, 1), 0.0)
    nt.links.new(amt2.outputs[0], col.node.inputs["Factor"])
    nt.links.new(col, bsdf.inputs["Base Color"])
    return m


# Shared materials. Tinted parts use light greys so the game's tint carries the colour.
def paint(shade=0.9, **kw):
    return material(f"paint{shade}", (shade,) * 3, **kw)


def fixed(name, rgb, **kw):
    return material(name, rgb, **kw)


def _obj_from_bmesh(name, bm):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    obj = bpy.data.objects.new(name, me)
    bpy.context.collection.objects.link(obj)
    return obj


def _bevel(obj, width, segments=2):
    if width <= 0:
        return
    mod = obj.modifiers.new("bevel", "BEVEL")
    mod.width = width
    mod.segments = segments
    mod.limit_method = "ANGLE"
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.modifier_apply(modifier=mod.name)
    obj.select_set(False)


def box(name, size, center, mat, tint="p0", bone=None, pivot=None, bevel=0.012, rot=(0, 0, 0)):
    """Axis-aligned box: size (x, y, z), centre (x, y, z)."""
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
    obj = _obj_from_bmesh(name, bm)
    obj.rotation_euler = rot
    obj.location = center
    _bevel(obj, min(bevel, min(size) * 0.45))
    _apply_transform(obj)
    return _finish(obj, tint, bone, pivot, mat)


def cyl(name, radius, depth, center, mat, tint="p0", bone=None, pivot=None, verts=16, axis="Z", bevel=0.006, radius2=None):
    """Cylinder (or cone/frustum with radius2) along an axis."""
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=verts, radius1=radius,
                          radius2=radius if radius2 is None else radius2, depth=depth)
    obj = _obj_from_bmesh(name, bm)
    obj.rotation_euler = {"Z": (0, 0, 0), "X": (math.pi / 2, 0, 0), "Y": (0, math.pi / 2, 0)}[axis]
    obj.location = center
    _bevel(obj, bevel)
    _apply_transform(obj)
    for p in obj.data.polygons:
        p.use_smooth = len(p.vertices) == 4
    return _finish(obj, tint, bone, pivot, mat)


def sphere(name, radius, center, mat, tint="p0", bone=None, pivot=None, scale=(1, 1, 1), segs=16):
    bm = bmesh.new()
    bmesh.ops.create_uvsphere(bm, u_segments=segs, v_segments=segs // 2, radius=radius)
    for v in bm.verts:
        v.co = Vector((v.co.x * scale[0], v.co.y * scale[1], v.co.z * scale[2]))
    obj = _obj_from_bmesh(name, bm)
    obj.location = center
    _apply_transform(obj)
    for p in obj.data.polygons:
        p.use_smooth = True
    return _finish(obj, tint, bone, pivot, mat)


def slats(name, count, size, start, step, mat, tint="none", bone=None, pivot=None):
    """A row of thin boxes (vents, grilles): `count` boxes of `size`, first centred at `start`, offset by `step`."""
    objs = []
    for i in range(count):
        c = tuple(start[k] + step[k] * i for k in range(3))
        objs.append(box(f"{name}{i}", size, c, mat, tint, bone, pivot, bevel=0.003))
    return objs


def _apply_transform(obj):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    obj.select_set(False)


def build(name, out_dir, tex_size=512, ao_distance=0.25, samples=48):
    """Join the parts, unwrap, bake albedo x AO, and write <name>.json + <name>.png."""
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = samples
    scene.render.bake.margin = 6
    scene.world = scene.world or bpy.data.worlds.new("w")

    # One material slot per part (a per-part copy, so each keeps its own baked image node).
    meta = []
    objs = []
    for i, (obj, tint, bone, pivot, grp) in enumerate(PARTS):
        mat = obj.data.materials[0].copy()
        mat.name = f"part{i}"
        obj.data.materials.clear()
        obj.data.materials.append(mat)
        cos = [v.co for v in obj.data.vertices]
        bbox = ([min(c[k] for c in cos) for k in range(3)], [max(c[k] for c in cos) for k in range(3)])
        meta.append({"name": obj.name, "tint": tint, "bone": bone, "pivot": pivot, "group": grp, "bbox": bbox})
        objs.append(obj)

    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    joined = bpy.context.view_layer.objects.active
    joined.name = name

    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.01, area_weight=0.0, scale_to_bounds=True)
    bpy.ops.object.mode_set(mode="OBJECT")

    # Decal parts ("s0".."s7") show the entity's sprite: planar UVs over the part's front (X -> u, Z -> v).
    me = joined.data
    uvl = me.uv_layers.active.data
    for poly in me.polygons:
        m = meta[poly.material_index]
        if not m["tint"].startswith("s"):
            continue
        lo, hi = m["bbox"]
        for li in poly.loop_indices:
            co = me.vertices[me.loops[li].vertex_index].co
            uvl[li].uv = ((co.x - lo[0]) / max(hi[0] - lo[0], 1e-5), (co.z - lo[2]) / max(hi[2] - lo[2], 1e-5))

    def bake(kind, **kw):
        img = bpy.data.images.new(f"{name}_{kind}", tex_size, tex_size, alpha=False, float_buffer=True)
        for mat in joined.data.materials:
            nt = mat.node_tree
            node = nt.nodes.new("ShaderNodeTexImage")
            node.image = img
            nt.nodes.active = node
        bpy.ops.object.select_all(action="DESELECT")
        joined.select_set(True)
        bpy.context.view_layer.objects.active = joined
        bpy.ops.object.bake(type=kind, **kw)
        px = np.array(img.pixels[:], dtype=np.float32).reshape(tex_size, tex_size, 4)
        for mat in joined.data.materials:
            mat.node_tree.nodes.remove(mat.node_tree.nodes.active)
        return px

    scene.world.light_settings.distance = ao_distance
    albedo = bake("DIFFUSE", pass_filter={"COLOR"})
    ao = bake("AO")
    final = albedo.copy()
    final[..., :3] = albedo[..., :3] * (0.45 + 0.55 * ao[..., :1])
    final[..., 3] = 1.0
    # Linear -> sRGB for the PNG.
    rgb = np.clip(final[..., :3], 0, 1)
    final[..., :3] = np.where(rgb <= 0.0031308, rgb * 12.92, 1.055 * np.power(rgb, 1 / 2.4) - 0.055)

    out_img = bpy.data.images.new(f"{name}_out", tex_size, tex_size, alpha=True)
    out_img.pixels[:] = final.ravel()
    out_img.filepath_raw = os.path.join(out_dir, f"{name}.png")
    out_img.file_format = "PNG"
    out_img.save()

    # Triangles grouped by part (material index).
    me = joined.data
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data
    normals = me.corner_normals
    parts = [[] for _ in meta]
    for tri in me.loop_triangles:
        buf = parts[tri.material_index]
        for li in tri.loops:
            v = me.vertices[me.loops[li].vertex_index].co
            n = normals[li].vector
            u = uv[li].uv
            buf.extend((round(v.x, 4), round(v.y, 4), round(v.z, 4), round(n.x, 3), round(n.y, 3), round(n.z, 3),
                        round(u.x, 4), round(V_FLIP - u.y if V_FLIP else u.y, 4)))

    lo = [min(v.co[k] for v in me.vertices) for k in range(3)]
    hi = [max(v.co[k] for v in me.vertices) for k in range(3)]
    data = {"texture": f"{name}.png", "bounds": [round(x, 3) for x in lo + hi], "parts": []}
    # Parts the game draws identically (same tint and bone) become one draw.
    merged = {}
    for m, tris in zip(meta, parts):
        if not tris:
            continue
        key = (m["tint"], m["bone"], m["pivot"], m["group"])
        if key not in merged:
            merged[key] = {**{k: v for k, v in m.items() if k != "bbox"}, "tris": []}
            data["parts"].append(merged[key])
        merged[key]["tris"].extend(tris)
    with open(os.path.join(out_dir, f"{name}.json"), "w") as f:
        json.dump(data, f, separators=(",", ":"))
    print(f"MODEL {name}: {sum(len(p) for p in parts) // 24} tris, {len(data['parts'])} parts")


def decal(name, width, height, center, tint="s0", bone=None, pivot=None, tilt=0.0):
    """A quad facing -Y (optionally tilted back by `tilt` radians about X) that shows the entity's sprite in game."""
    import mathutils
    bm = bmesh.new()
    w, h = width / 2, height / 2
    vs = [bm.verts.new((x, 0, z)) for x, z in ((-w, -h), (w, -h), (w, h), (-w, h))]
    bm.faces.new(vs)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    if bm.faces[0].normal.y > 0:
        bmesh.ops.reverse_faces(bm, faces=bm.faces)
    obj = _obj_from_bmesh(name, bm)
    obj.rotation_euler = (-tilt, 0, 0)
    obj.location = center
    _apply_transform(obj)
    return _finish(obj, tint, bone, pivot, material(f"decal{name}", (1, 1, 1), grime=0, wear=0))
