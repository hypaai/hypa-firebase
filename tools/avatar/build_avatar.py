"""Builds the Hypa Stories profile character from the MakeHuman CC0 system assets with MPFB (Blender).

Output (Assets/HypaStories/Resources/HypaAvatar):
  hypa_avatar.glb      one skeleton (MPFB's mixamo_unity rig, prefix stripped) and one skinned mesh per part: body, eyes,
                       eyelashes, brow_<id>, hair_<id>, outfit_<id>, shoes_<id>. Every part carries the slider blend shapes
                       (<slider>_<low|high>) and "blink". The body's vertex colour holds one bit per outfit/shoe: the skin
                       that garment covers (Unity hides it); bits follow the catalogue order, outfits then shoes.
  Textures/*.png|jpg   skin_0..2 (light, medium, deep; mean colour normalised so a tint sets the tone), eyes_<id>,
                       eyelashes, brow_<id> and hair_<id> (greyscale, tinted at runtime), outfit_<id> (+ _grey), shoes_<id>.
  Thumbs/<id>.png      tile pictures for the studio.

Option ids are read from Assets/HypaStories/Runtime/Profile/ProfileCatalog.cs, so the two never drift.

Setup (once):
  blender --command extension install-file -r user_default -e add-on-mpfb-v2.0.18.zip
  unzip makehuman_system_assets_cc0.zip into the MPFB user data folder:
  ~/Library/Application Support/Blender/<version>/extensions/.user/user_default/mpfb/data
Run (from the repository root):
  blender -b --python tools/avatar/build_avatar.py -- [--no-thumbs] [--preview out.png]
"""
import argparse, bpy, importlib, json, math, os, re, sys
import numpy as np
from mathutils import Matrix, Vector

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(REPO, "Assets", "HypaStories", "Resources", "HypaAvatar")
CATALOG = os.path.join(REPO, "Assets", "HypaStories", "Runtime", "Profile", "ProfileCatalog.cs")

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
ap = argparse.ArgumentParser()
ap.add_argument("--no-thumbs", action="store_true")
ap.add_argument("--preview", help="also render a full-body preview to this png")
ap.add_argument("--only-preview", action="store_true", help="skip export (iterate on the look)")
args = ap.parse_args(argv)

# ------------------------------------------------------------------ MPFB

pkg = next((n for n in list(sys.modules) if n.startswith("bl_ext.") and n.endswith(".mpfb")), None)
if pkg is None:
    import addon_utils
    for mod in addon_utils.modules():
        if mod.__name__.endswith(".mpfb"):
            addon_utils.enable(mod.__name__, default_set=True)
            pkg = mod.__name__
            break
if pkg is None:
    sys.exit("MPFB is not installed as a Blender extension (see the docstring).")


def svc(module, name):
    return getattr(importlib.import_module(pkg + ".services." + module), name)


HumanService = svc("humanservice", "HumanService")
TargetService = svc("targetservice", "TargetService")
ClothesService = svc("clothesservice", "ClothesService")
AssetService = svc("assetservice", "AssetService")
HumanObjectProperties = importlib.import_module(pkg + ".entities.objectproperties").HumanObjectProperties

# ------------------------------------------------------------------ catalog (from the C#)


def catalog(array):
    src = open(CATALOG, encoding="utf-8").read()
    block = re.search(r"ProfileOption\[\]\s+" + array + r"\s*=\s*\{(.*?)\};", src, re.S).group(1)
    return [m.group(1) for m in re.finditer(r'new ProfileOption\("[^"]*",\s*"([^"]+)"', block)]


def catalog_lines(array):
    """Each option of an array, as source text (to see e.g. whether outfit colours other than the original exist)."""
    src = open(CATALOG, encoding="utf-8").read()
    block = re.search(r"ProfileOption\[\]\s+" + array + r"\s*=\s*\{(.*?)\};", src, re.S).group(1)
    return re.findall(r"new ProfileOption\([^)]*\)", block)


HAIR, BROWS, OUTFITS, SHOES, EYES = (catalog(a) for a in ("HairStyles", "Brows", "Outfits", "Shoes", "EyeColors"))
SKINS = ["young_caucasian_female", "young_asian_female", "young_african_female"]  # bands 0, 1, 2

# Slider extremes. Macros are MakeHuman's own 0..1 values (0.5 is the default); targets are (file, weight) pairs.
ALL_MACROS = {
    "frame_feminine": ("gender", 0.0), "frame_masculine": ("gender", 1.0),
    "weight_low": ("weight", 0.15), "weight_high": ("weight", 0.88),
    "muscle_low": ("muscle", 0.15), "muscle_high": ("muscle", 0.92),
}
ALL_TARGETS = {
    "head_round": [("head/head-round", 0.7)], "head_square": [("head/head-square", 0.7)],
    "jaw_narrow": [("chin/chin-width-decr", 1.0), ("head/head-fat-decr", 0.3)],
    "jaw_wide": [("chin/chin-width-incr", 1.0), ("chin/chin-bones-incr", 0.5)],
    "chin_soft": [("chin/chin-prominent-decr", 1.0)], "chin_strong": [("chin/chin-prominent-incr", 0.8), ("chin/chin-height-incr", 0.4)],
    "cheeks_soft": [("cheek/l-cheek-bones-decr", 1.0), ("cheek/r-cheek-bones-decr", 1.0)],
    "cheeks_high": [("cheek/l-cheek-bones-incr", 1.0), ("cheek/r-cheek-bones-incr", 1.0)],
    "nose_narrow": [("nose/nose-scale-horiz-decr", 0.9), ("nose/nose-nostrils-width-decr", 0.6)],
    "nose_wide": [("nose/nose-scale-horiz-incr", 0.9), ("nose/nose-nostrils-width-incr", 0.6), ("nose/nose-flaring-incr", 0.4)],
    "nose_short": [("nose/nose-scale-vert-decr", 0.8)], "nose_long": [("nose/nose-scale-vert-incr", 0.8)],
    "lips_thin": [("mouth/mouth-lowerlip-volume-decr", 0.9), ("mouth/mouth-upperlip-volume-decr", 0.9)],
    "lips_full": [("mouth/mouth-lowerlip-volume-incr", 1.0), ("mouth/mouth-upperlip-volume-incr", 1.0)],
    "eyes_small": [("eyes/l-eye-scale-decr", 0.8), ("eyes/r-eye-scale-decr", 0.8)],
    "eyes_large": [("eyes/l-eye-scale-incr", 0.8), ("eyes/r-eye-scale-incr", 0.8)],
}
# What the profile card uses: the body choice (frame). The other sliders' shapes are left out of the model to save memory; list them in
# SHAPES (and expose them in the UI) to bring them back.
SHAPES = ["frame_feminine", "frame_masculine"]
MACROS = {k: v for k, v in ALL_MACROS.items() if k in SHAPES}
TARGETS = {k: v for k, v in ALL_TARGETS.items() if k in SHAPES}

# Texture sizes (pixels). The portrait is 512 px across, so the skin needs no more than 1024.
SIZES = {"skin": 1024, "hair": 512, "outfit": 512, "small": 256}

TARGET_DIR = os.path.join(os.path.dirname(importlib.import_module(pkg).__file__), "data", "targets")

# ------------------------------------------------------------------ helpers


def coords(obj, mix=False):
    if mix:
        key = obj.shape_key_add(name="tmp_mix", from_mix=True)
        out = np.empty(len(key.data) * 3, np.float32)
        key.data.foreach_get("co", out)
        obj.shape_key_remove(key)
    else:
        out = np.empty(len(obj.data.vertices) * 3, np.float32)
        obj.data.vertices.foreach_get("co", out)
    return out.reshape(-1, 3)


def evaluated(obj):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(dg)
    mesh = ev.to_mesh()
    out = np.empty(len(mesh.vertices) * 3, np.float32)
    mesh.vertices.foreach_get("co", out)
    ev.to_mesh_clear()
    return out.reshape(-1, 3)


def find(name, kind):
    path = AssetService.find_asset_absolute_path(name + ".mhclo", kind)
    if not path:
        raise IOError("MakeHuman asset not found: %s/%s (is the CC0 system asset pack installed?)" % (kind, name))
    return path


def mhmat_texture(mhclo_path, key="diffuseTexture"):
    """The texture a .mhclo's default .mhmat names (absolute path), or None."""
    folder = os.path.dirname(mhclo_path)
    mat = None
    for line in open(mhclo_path, encoding="utf-8", errors="ignore"):
        if line.startswith("material "):
            mat = os.path.join(folder, line.split(None, 1)[1].strip())
    if mat is None or not os.path.exists(mat):
        return None
    return material_texture(mat, key)


def material_texture(mhmat_path, key="diffuseTexture"):
    for line in open(mhmat_path, encoding="utf-8", errors="ignore"):
        parts = line.strip().split(None, 1)
        if len(parts) == 2 and parts[0] == key:
            tex = os.path.join(os.path.dirname(mhmat_path), parts[1].strip())
            return tex if os.path.exists(tex) else None
    return None


def asset_file(kind, filename):
    for root in AssetService.get_asset_roots(kind):
        for dirpath, _, files in os.walk(root):
            if filename in files:
                return os.path.join(dirpath, filename)
    return None


# ------------------------------------------------------------------ build


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def build():
    clear_scene()
    basemesh = HumanService.create_human(scale=0.1)
    rig = HumanService.add_builtin_rig(basemesh, "mixamo_unity")

    parts = {}  # export name -> (object, mhclo path)

    def add(kind, asset, name):
        path = find(asset, kind.lower())
        obj = HumanService.add_mhclo_asset(path, basemesh, asset_type=kind, subdiv_levels=0, material_type="MAKESKIN",
                                           set_up_rigging=True, interpolate_weights=True, import_subrig=False, import_weights=True)
        for m in list(obj.modifiers):
            if m.type == "SUBSURF":
                obj.modifiers.remove(m)
        if obj.data.shape_keys:
            obj.shape_key_clear()
        parts[name] = (obj, path)
        return obj

    add("Eyes", "high-poly", "eyes")
    add("Eyelashes", "eyelashes01", "eyelashes")
    for b in BROWS:
        add("Eyebrows", b, "brow_" + b)
    for h in HAIR:
        add("Hair", h, "hair_" + h)
    for o in OUTFITS:
        add("Clothes", o, "outfit_" + o)
    for s in SHOES:
        add("Clothes", s, "shoes_" + s)

    # -------- capture every slider extreme on the body and on every part (refitted, so clothes follow)
    captured = {}

    def capture(key):
        captured[key] = {"body": coords(basemesh, mix=True)}
        for name, (obj, path) in parts.items():
            ClothesService.fit_clothes_to_human(obj, basemesh, set_parent=False)
            captured[key][name] = coords(obj)

    def macro(name, value):
        HumanObjectProperties.set_value(name, value, entity_reference=basemesh)
        TargetService.reapply_macro_details(basemesh)

    for key, (name, value) in MACROS.items():
        macro(name, value)
        capture(key)
        macro(name, 0.5)
        print("captured", key)
    for key, targets in TARGETS.items():
        loaded = []
        for i, (target, weight) in enumerate(targets):
            loaded.append(TargetService.load_target(basemesh, os.path.join(TARGET_DIR, target + ".target.gz"), weight=weight, name="tmp_%s_%d" % (key, i)))
        capture(key)
        for k in loaded:
            basemesh.shape_key_remove(k)
        print("captured", key)
    capture("neutral")

    # -------- bake the neutral body and add the captured extremes as shape keys
    for m in list(basemesh.modifiers):
        if m.type == "MASK":
            basemesh.modifiers.remove(m)
    TargetService.bake_targets(basemesh)
    neutral = captured["neutral"]

    def add_keys(obj, name):
        base = neutral[name]
        obj.data.vertices.foreach_set("co", base.ravel())
        obj.shape_key_add(name="Basis", from_mix=False)
        for key in list(MACROS) + list(TARGETS):
            co = captured[key][name]
            if np.abs(co - base).max() < 2e-4:
                continue
            k = obj.shape_key_add(name=key, from_mix=False)
            k.data.foreach_set("co", co.ravel())
            k.value = 0.0
        obj.data.update()

    add_keys(basemesh, "body")
    for name, (obj, _) in parts.items():
        add_keys(obj, name)

    # -------- blink: close the lids with the eyelid bones, keep the difference as a shape key on body and lashes
    blink(rig, [basemesh, parts["eyelashes"][0]])

    # -------- outfit masks in the body's vertex colour (skin under a garment is hidden at runtime)
    masks = outfit_masks(basemesh)

    # -------- remove helper geometry and the delete groups' modifiers
    bpy.context.view_layer.objects.active = basemesh
    for o in bpy.context.selected_objects:
        o.select_set(False)
    basemesh.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    basemesh.vertex_groups.active_index = basemesh.vertex_groups["body"].index
    bpy.ops.object.vertex_group_deselect()
    bpy.ops.mesh.delete(type="VERT")
    bpy.ops.object.mode_set(mode="OBJECT")
    for obj in [basemesh] + [p[0] for p in parts.values()]:
        for m in list(obj.modifiers):
            if m.type != "ARMATURE":
                obj.modifiers.remove(m)

    # -------- names: Unity looks parts and bones up by these
    for b in rig.data.bones:
        b.name = b.name.replace("mixamorig:", "")
    basemesh.name = basemesh.data.name = "body"
    for name, (obj, _) in parts.items():
        obj.name = obj.data.name = name
    rig.name = rig.data.name = "hypa_avatar_rig"
    return basemesh, rig, parts, masks


def blink(rig, meshes):
    """Rotates the upper lids down and the lower lids up a little; the difference from rest becomes "blink"."""
    rest = {m.name: evaluated(m) for m in meshes}
    bones = {"LeftOrbicularisTop": 1.0, "RightOrbicularisTop": 1.0, "LeftOrbicularisBottom": -0.25, "RightOrbicularisBottom": -0.25}
    pose = rig.pose.bones
    angle = math.radians(float(os.environ.get("HYPA_BLINK_DEGREES", "34")))
    for bone, amount in bones.items():
        pb = pose.get("mixamorig:" + bone)
        if pb is None:
            print("no eyelid bone", bone)
            continue
        pb.rotation_mode = "XYZ"
        pb.rotation_euler = (angle * amount, 0, 0)
    bpy.context.view_layer.update()
    for m in meshes:
        delta = evaluated(m) - rest[m.name]
        basis = coords(m)
        k = m.shape_key_add(name="blink", from_mix=False)
        k.data.foreach_set("co", (basis + delta).ravel())
        k.value = 0.0
    for bone in bones:
        pb = pose.get("mixamorig:" + bone)
        if pb is not None:
            pb.rotation_euler = (0, 0, 0)
    bpy.context.view_layer.update()


def outfit_masks(basemesh):
    """One bit per outfit and shoe in a float colour attribute: R bits 0-7, G 8-15, B 16-23 (k/255)."""
    names = ["outfit_" + o for o in OUTFITS] + ["shoes_" + s for s in SHOES]
    bits = np.zeros(len(basemesh.data.vertices), np.int64)
    table = {}
    for i, name in enumerate(names):
        asset = name.split("_", 1)[1]
        group = basemesh.vertex_groups.get("Delete." + asset)
        table[name] = i
        if group is None:
            continue
        gi = group.index
        for v in basemesh.data.vertices:
            for g in v.groups:
                if g.group == gi and g.weight > 0.5:
                    bits[v.index] |= 1 << i
    attr = basemesh.data.color_attributes.new("outfit_mask", "FLOAT_COLOR", "POINT")
    col = np.zeros((len(bits), 4), np.float32)
    col[:, 0] = (bits & 255) / 255.0
    col[:, 1] = ((bits >> 8) & 255) / 255.0
    col[:, 2] = ((bits >> 16) & 255) / 255.0
    col[:, 3] = 1.0
    attr.data.foreach_set("color", col.ravel())
    basemesh.data.color_attributes.active_color = attr
    print("outfit masks:", {n: int(((bits >> i) & 1).sum()) for n, i in table.items()})
    return table


# ------------------------------------------------------------------ textures


def load_pixels(path, size):
    img = bpy.data.images.load(path, check_existing=False)
    w, h = img.size
    if max(w, h) > size:
        img.scale(size * w // max(w, h), size * h // max(w, h))
        w, h = img.size
    px = np.empty(w * h * 4, np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return px.reshape(h, w, 4)


def save_pixels(px, path, jpg=False):
    h, w, _ = px.shape
    img = bpy.data.images.new(os.path.basename(path), w, h, alpha=not jpg)
    img.pixels.foreach_set(np.clip(px, 0, 1).astype(np.float32).ravel())
    img.filepath_raw = path
    img.file_format = "JPEG" if jpg else "PNG"
    if jpg:
        bpy.context.scene.render.image_settings.quality = 88
    img.save()
    bpy.data.images.remove(img)


def lum(px):
    return px[..., 0] * 0.299 + px[..., 1] * 0.587 + px[..., 2] * 0.114


def normalised_colour(px, target=0.8):
    """Divide by the mean colour of the painted pixels: a uniform tint then sets the overall tone, details stay."""
    l = lum(px)
    # the flat colour around the UV islands (a corner pixel) isn't skin
    background = px[2, 2, :3]
    painted = np.abs(px[..., :3] - background).max(axis=-1) > 0.035
    mask = (px[..., 3] > 0.5) & (l > 0.08) & painted
    mean = px[..., :3][mask].mean(axis=0)
    print("skin mean", np.round(mean, 3), "from", int(mask.sum()), "pixels")
    out = px.copy()
    out[..., :3] = px[..., :3] / mean * target
    return out


def greyscale(px, target=0.75):
    l = lum(px)
    mask = px[..., 3] > 0.5
    m = l[mask].mean() if mask.any() else 0.5
    out = px.copy()
    g = np.clip(l / max(m, 1e-3) * target, 0, 1)
    out[..., 0] = out[..., 1] = out[..., 2] = g
    return out


def textures(parts):
    tex = os.path.join(OUT, "Textures")
    os.makedirs(tex, exist_ok=True)
    made = {}
    for band, skin in enumerate(SKINS):
        mat = asset_file("skins", skin + ".mhmat")
        src = material_texture(mat) if mat else None
        if src is None:
            raise IOError("skin texture missing: " + skin)
        px = normalised_colour(load_pixels(src, SIZES["skin"]))
        px[..., 3] = 1
        save_pixels(px, os.path.join(tex, "skin_%d.jpg" % band), jpg=True)
        made["skin_%d" % band] = src
    for eye in EYES:
        colour = eye.split("_", 1)[1]
        mat = asset_file("eyes", colour + ".mhmat")
        src = material_texture(mat) if mat else None
        if src is None:
            print("eye material missing:", colour)
            continue
        save_pixels(load_pixels(src, SIZES["small"]), os.path.join(tex, eye + ".png"))
        made[eye] = src
    for name, (obj, path) in parts.items():
        src = mhmat_texture(path)
        if src is None:
            print("no texture for", name)
            continue
        if name.startswith(("hair_", "brow_")) or name == "eyelashes":
            px = load_pixels(src, SIZES["hair"] if name.startswith("hair_") else SIZES["small"])
            save_pixels(greyscale(px) if name != "eyelashes" else px, os.path.join(tex, name + ".png"))
        elif name.startswith("outfit_"):
            px = load_pixels(src, SIZES["outfit"])
            px[..., 3] = 1
            save_pixels(px, os.path.join(tex, name + ".jpg"), jpg=True)
            if any('original: true' not in l for l in catalog_lines("OutfitColors")):
                save_pixels(greyscale(px, 0.72), os.path.join(tex, name + "_grey.jpg"), jpg=True)
        elif name.startswith("shoes_"):
            px = load_pixels(src, SIZES["small"])
            px[..., 3] = 1
            save_pixels(px, os.path.join(tex, name + ".jpg"), jpg=True)
        made[name] = src
    return made


# ------------------------------------------------------------------ preview materials, thumbnails


def preview_materials(basemesh, parts, skin_band=1, tone=(0.71, 0.5, 0.34), hair=(0.2, 0.13, 0.09)):
    tex = os.path.join(OUT, "Textures")

    def material(name, image, tint=(1, 1, 1), alpha=False, rough=0.6):
        m = bpy.data.materials.new(name)
        m.use_nodes = True
        nt = m.node_tree
        bsdf = nt.nodes["Principled BSDF"]
        bsdf.inputs["Roughness"].default_value = rough
        path = None
        for ext in (".png", ".jpg"):
            if os.path.exists(os.path.join(tex, image + ext)):
                path = os.path.join(tex, image + ext)
        if path:
            node = nt.nodes.new("ShaderNodeTexImage")
            node.image = bpy.data.images.load(path, check_existing=True)
            mul = nt.nodes.new("ShaderNodeVectorMath")
            mul.operation = "MULTIPLY"
            nt.links.new(node.outputs["Color"], mul.inputs[0])
            mul.inputs[1].default_value = tint
            nt.links.new(mul.outputs[0], bsdf.inputs["Base Color"])
            if alpha:
                nt.links.new(node.outputs["Alpha"], bsdf.inputs["Alpha"])
                if hasattr(m, "blend_method"):
                    m.blend_method = "CLIP"
                if hasattr(m, "surface_render_method"):
                    m.surface_render_method = "DITHERED"
        return m

    def assign(obj, m):
        obj.data.materials.clear()
        obj.data.materials.append(m)

    s = 1.25
    assign(basemesh, material("body", "skin_%d" % skin_band, tuple(min(1, c * s) for c in tone), rough=0.5))
    for name, (obj, _) in parts.items():
        if name == "eyes":
            assign(obj, material(name, EYES[0], alpha=True, rough=0.1))
        elif name == "eyelashes":
            assign(obj, material(name, "eyelashes", (0.1, 0.08, 0.07), alpha=True))
        elif name.startswith("brow_"):
            assign(obj, material(name, name, tuple(c * 0.8 for c in hair), alpha=True))
        elif name.startswith("hair_"):
            assign(obj, material(name, name, tuple(c * 1.35 for c in hair), alpha=True, rough=0.45))
        else:
            assign(obj, material(name, name, rough=0.8))


def show_only(parts, visible, body=None):
    for name, (obj, _) in parts.items():
        hide = name not in visible and name not in ("eyes", "eyelashes")
        obj.hide_render = hide
        obj.hide_viewport = hide
    if body is None:
        return
    # hide the skin under the visible garments, as Unity does with the vertex-colour bits
    group = body.vertex_groups.get("preview_hidden") or body.vertex_groups.new(name="preview_hidden")
    body.vertex_groups.active_index = group.index
    group.remove(range(len(body.data.vertices)))
    covered = set()
    for name in visible:
        if name.startswith(("outfit_", "shoes_")):
            g = body.vertex_groups.get("Delete." + name.split("_", 1)[1])
            if g is not None:
                covered.add(g.index)
    hidden = [v.index for v in body.data.vertices if any(x.group in covered and x.weight > 0.5 for x in v.groups)]
    if hidden:
        group.add(hidden, 1.0, "REPLACE")
    mask = body.modifiers.get("preview_mask") or body.modifiers.new("preview_mask", "MASK")
    mask.vertex_group = group.name
    mask.invert_vertex_group = True


def lower_arms(rig):
    """Arms down about 75 degrees from the bind pose, for renders only (Unity poses its own)."""
    for side, sign in (("Left", 1), ("Right", -1)):
        pb = rig.pose.bones.get(side + "Arm") or rig.pose.bones.get("mixamorig:" + side + "Arm")
        if pb is None:
            continue
        head = pb.head.copy()
        tail = pb.tail.copy()
        d = (tail - head).normalized()
        want = Vector((math.sin(math.radians(10)) * (1 if d.x > 0 else -1), 0, -math.cos(math.radians(10))))
        rot = d.rotation_difference(want).to_matrix().to_4x4()
        pb.matrix = Matrix.Translation(head) @ rot @ Matrix.Translation(-head) @ pb.matrix
        bpy.context.view_layer.update()


def studio(size):
    scene = bpy.context.scene
    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        try:
            scene.render.engine = "BLENDER_EEVEE"
        except TypeError:
            scene.render.engine = "CYCLES"
    if scene.render.engine == "CYCLES":
        scene.cycles.samples = 48
        scene.cycles.device = "CPU"
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"
    world = bpy.data.worlds.new("studio")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (0.12, 0.11, 0.1, 1)
    scene.world = world
    for name, loc, energy, colour in (("key", (-1.6, -2.2, 2.4), 70, (1, 0.92, 0.84)), ("fill", (2.0, -1.6, 1.4), 22, (0.8, 0.86, 1)),
                                      ("rim", (0.6, 2.2, 2.2), 80, (1, 0.75, 0.5))):
        light = bpy.data.lights.new(name, "AREA")
        light.energy = energy
        light.color = colour
        light.size = 1.5
        obj = bpy.data.objects.new(name, light)
        obj.location = loc
        obj.rotation_euler = (Vector((0, 0, 1.2)) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
        scene.collection.objects.link(obj)
    cam = bpy.data.objects.new("camera", bpy.data.cameras.new("camera"))
    scene.collection.objects.link(cam)
    scene.camera = cam
    return cam


def aim(cam, target, distance, lens=85, yaw=-20):
    cam.data.lens = lens
    a = math.radians(yaw)
    cam.location = Vector(target) + Vector((math.sin(a) * distance, -math.cos(a) * distance, 0.02))
    cam.rotation_euler = (Vector(target) - cam.location).to_track_quat("-Z", "Y").to_euler()


def render(path):
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def thumbnails(rig, body, parts, head_z):
    thumbs = os.path.join(OUT, "Thumbs")
    os.makedirs(thumbs, exist_ok=True)
    cam = studio(256)
    default_hair, default_brow, default_outfit, default_shoes = "hair_" + HAIR[0], "brow_" + BROWS[0], "outfit_" + OUTFITS[0], "shoes_" + SHOES[0]
    for h in HAIR:
        show_only(parts, {"hair_" + h, default_brow, default_outfit}, body)
        aim(cam, (0, 0, head_z + 0.02), 1.15, yaw=-28)
        render(os.path.join(thumbs, h + ".png"))
    for b in BROWS:
        show_only(parts, {"brow_" + b, default_outfit, default_hair}, body)
        aim(cam, (0, 0, head_z - 0.045), 0.36, yaw=-14)
        render(os.path.join(thumbs, b + ".png"))
    for o in OUTFITS:
        show_only(parts, {"outfit_" + o, default_shoes, default_hair, default_brow}, body)
        aim(cam, (0, 0, head_z * 0.56), 3.7, lens=85, yaw=-18)
        render(os.path.join(thumbs, o + ".png"))
    for s in SHOES:
        show_only(parts, {"shoes_" + s, default_outfit}, body)
        aim(cam, (0.13, -0.08, 0.045), 0.72, yaw=-35)
        render(os.path.join(thumbs, s + ".png"))


# ------------------------------------------------------------------ export


def export(basemesh, rig, parts):
    path = os.path.join(OUT, "hypa_avatar.glb")
    for o in bpy.context.scene.objects:
        o.select_set(o == rig or o == basemesh or o.name in parts)
    kwargs = dict(filepath=path, export_format="GLB", use_selection=True, export_skins=True, export_morph=True,
                  export_morph_normal=False, export_animations=False, export_apply=False, export_yup=True,
                  export_materials="PLACEHOLDER", export_texcoords=True, export_normals=True)
    props = bpy.ops.export_scene.gltf.get_rna_type().properties.keys()
    if "export_vertex_color" in props:
        kwargs["export_vertex_color"] = "ACTIVE"
    elif "export_colors" in props:
        kwargs["export_colors"] = True
    if "export_try_sparse_sk" in props:
        kwargs["export_try_sparse_sk"] = True
    if "export_def_bones" in props:
        kwargs["export_def_bones"] = False
    bpy.ops.export_scene.gltf(**kwargs)
    print("wrote", path, os.path.getsize(path) // 1024, "KB")


def main():
    os.makedirs(OUT, exist_ok=True)
    basemesh, rig, parts, masks = build()
    made = textures(parts)
    head = rig.pose.bones.get("Head")
    head_z = (rig.matrix_world @ head.head).z + 0.08 if head else 1.6
    if not args.only_preview:
        export(basemesh, rig, parts)
        for name, src in sorted(made.items()):
            print("source", name, "<-", src.split("/data/")[-1])
    preview_materials(basemesh, parts)
    lower_arms(rig)
    if not args.no_thumbs and not args.only_preview:
        thumbnails(rig, basemesh, parts, head_z)
    if args.preview:
        # HYPA_PREVIEW_KEYS="frame_feminine=1,blink=1" previews blend shapes on every part
        for pair in filter(None, os.environ.get("HYPA_PREVIEW_KEYS", "").split(",")):
            key, value = pair.split("=")
            for obj in [basemesh] + [p[0] for p in parts.values()]:
                if obj.data.shape_keys and key in obj.data.shape_keys.key_blocks:
                    obj.data.shape_keys.key_blocks[key].value = float(value)
        cam = studio(900)
        show_only(parts, {"hair_" + HAIR[0], "brow_" + BROWS[0], "outfit_" + OUTFITS[0], "shoes_" + SHOES[0]}, basemesh)
        aim(cam, (0, 0, head_z * 0.52), 6.0, lens=70, yaw=-20)
        render(args.preview)
        aim(cam, (0, 0, head_z), 0.9, lens=85, yaw=-15)
        render(args.preview.replace(".png", "_face.png"))


main()
