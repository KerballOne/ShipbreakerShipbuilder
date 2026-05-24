import bpy
import bmesh
import math

# ── Parameters ───────────────────────────────────────────────────────────────

SCALE = 2.0   # uniform scale baked into geometry (Unity transform stays at 1,1,1)

# Bell shape  (wide shallow dish, like the reference)
THROAT_RADIUS  = 3.22  * SCALE
EXIT_RADIUS    = 5.20  * SCALE
NOZZLE_LENGTH  = 5.40  * SCALE
BELL_POWER     = 0.42            # < 0.5 = deep concave dish profile

# Collar (attaches to ship body)
COLLAR_RADIUS  = 3.95  * SCALE
COLLAR_LENGTH  = 0.08  * SCALE

# Raised circumferential bands on bell exterior
BAND_POSITIONS = [0.11, 0.22, 0.33, 0.55, 0.66, 0.88]
BAND_HEIGHT    = 0.10  * SCALE
BAND_PROTRUDE  = 0.05  * SCALE

# Bell wall thickness
WALL_THICKNESS = 0.34  * SCALE

# Exit rim (U-channel flange)
RIM_WIDTH      = 0.20  * SCALE
RIM_DEPTH      = 0.44  * SCALE

# Rim bracket panels — curved arc segments matching the rim cross-section
RIM_PANEL_COUNT    = 6
RIM_PANEL_ARC_FRAC = 0.45           # fraction of each 1/6 arc the panel spans
RIM_PANEL_OFFSET_R = -0.02  * SCALE  # radial gap between rim outer edge and panel inner edge
RIM_PANEL_OFFSET_Z = -0.60  * SCALE  # axial offset upward from rim face
# Strut connecting rim to panel
RIM_STRUT_WIDTH    = 0.40  * SCALE  # tangential width of strut
RIM_STRUT_DEPTH    = 0.03  * SCALE  # radial depth of strut

# Flare panels hanging from the inner nozzle collar bottom
FLARE_COUNT      = 24             # number of panels around the collar
FLARE_BASE_WIDTH = 0.14  * SCALE  # tangential half-width at base (collar end)
FLARE_TIP_WIDTH  = 0.07  * SCALE  # tangential half-width at tip
FLARE_HEIGHT     = 0.50  * SCALE  # axial+radial extent downward from collar
FLARE_THICKNESS  = 0.03  * SCALE  # panel thickness
FLARE_TILT       = -0.10           # inward tilt in radians (~17°) — tip angles toward axis

# Resolution
SEGMENTS       = 128
RINGS          = 72

# Inner turbine structure — hub
HUB_OUTER_R       = 0.58  * SCALE
HUB_INNER_R       = 0.52  * SCALE
HUB_LENGTH        = 6.10  * SCALE
HUB_RING_COUNT    = 10
HUB_RING_HEIGHT   = 0.050 * SCALE
HUB_RING_PROTRUDE = 0.042 * SCALE

# Inner turbine structure — vanes
VANE_COUNT   = 16
VANE_THICK   = 0.055 * SCALE
VANE_HEIGHT  = HUB_LENGTH * 0.80
VANE_OUTER_R = 2.25 * SCALE   # tip radius — set larger than COLLAR_INNER_R to stick out past collar

# Inner turbine structure — outer collar
COLLAR_INNER_R = 2.10 * SCALE
COLLAR_WALL    = 0.10 * SCALE
COLLAR_TAPER   = 0.82 * SCALE  # how much the collar narrows at the opening (exit) end

EXPORT_OUTER = "C:/Users/user/source/repos/ShipbreakerShipbuilder/Assets/_CustomShips/Rocinante/rocinante_engine_bell.fbx"
EXPORT_INNER = "C:/Users/user/source/repos/ShipbreakerShipbuilder/Assets/_CustomShips/Rocinante/rocinante_engine_nozzle.fbx"

# Set to a source image path to generate PBR textures; leave blank to skip
SOURCE_TEXTURE = "C:/Users/user/Pictures/Screenshots/An EpsteinDrive.png"

# Texture generation parameters
TEX_NORMAL_STRENGTH = 8.0   # surface bump intensity in normal map
TEX_METALLIC_BASE   = 0.90  # base metallic value (0–1)
TEX_SMOOTH_BASE     = 0.95  # base smoothness (0=rough, 1=mirror)
TEX_SMOOTH_SCALE    = 0.10  # how much luminance contributes to smoothness

# ── Derived ───────────────────────────────────────────────────────────────────

COLLAR_OUTER_R = COLLAR_INNER_R + COLLAR_WALL
VANE_BOT_Z     = (HUB_LENGTH - VANE_HEIGHT) * 0.5
VANE_TOP_Z     = VANE_BOT_Z + VANE_HEIGHT

# ── Helpers ──────────────────────────────────────────────────────────────────

def r_bell(t):
    return THROAT_RADIUS + (EXIT_RADIUS - THROAT_RADIUS) * (t ** BELL_POWER)

def bell_at(t):
    return NOZZLE_LENGTH * (1.0 - t), r_bell(t)

def add_ring(bm, z, r, segs=None):
    if segs is None: segs = SEGMENTS
    v = []
    for i in range(segs):
        a = 2 * math.pi * i / segs
        v.append(bm.verts.new((r * math.cos(a), r * math.sin(a), z)))
    return v

def bridge(bm, ra, rb):
    n = len(ra)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new([ra[i], ra[j], rb[j], rb[i]])

def cap_fan(bm, rv, z, flip=False):
    c = bm.verts.new((0, 0, z))
    n = len(rv)
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new([rv[j], rv[i], c] if flip else [rv[i], rv[j], c])

def annular_cap(bm, outer_ring, inner_ring, flip=False):
    # flip=False → +Z normal (top); flip=True → -Z normal (bottom)
    n = len(outer_ring)
    for i in range(n):
        j = (i + 1) % n
        if flip:
            bm.faces.new([outer_ring[i], inner_ring[i], inner_ring[j], outer_ring[j]])
        else:
            bm.faces.new([outer_ring[i], outer_ring[j], inner_ring[j], inner_ring[i]])

def export_fbx(filepath):
    bpy.ops.export_scene.fbx(
        filepath            = filepath,
        use_selection       = True,
        axis_forward        = '-Z',
        axis_up             = 'Y',
        apply_unit_scale    = True,
        apply_scale_options = 'FBX_SCALE_ALL',
        mesh_smooth_type    = 'FACE',
        use_mesh_modifiers  = True,
    )

# ── Clean up previous ────────────────────────────────────────────────────────

for o in list(bpy.data.objects):
    if o.name.startswith("EngineBell") or o.name.startswith("EngineNozzle"):
        bpy.data.objects.remove(o, do_unlink=True)
for m in list(bpy.data.meshes):
    if m.name.startswith("EngineBellMesh") or m.name.startswith("EngineNozzleMesh"):
        bpy.data.meshes.remove(m)
for mat in list(bpy.data.materials):
    if mat.name.startswith("M_EngineBell") or mat.name.startswith("M_EngineNozzle"):
        bpy.data.materials.remove(mat)
for img in list(bpy.data.images):
    if img.name.startswith("T_Engine"):
        bpy.data.images.remove(img)

# ════════════════════════════════════════════════════════════════════════════
# PART 1 — OUTER BELL
# ════════════════════════════════════════════════════════════════════════════

bm = bmesh.new()

# ── Bell profile with bands ───────────────────────────────────────────────────

profile = []
profile.append((NOZZLE_LENGTH + COLLAR_LENGTH, COLLAR_RADIUS))
profile.append((NOZZLE_LENGTH,                 COLLAR_RADIUS))
profile.append((NOZZLE_LENGTH,                 THROAT_RADIUS))

inserted = set()
prev_t = 0.0

for i in range(1, RINGS + 1):
    t = i / RINGS
    for bp in BAND_POSITIONS:
        if prev_t < bp < t and bp not in inserted:
            bz, br = bell_at(bp)
            h = BAND_HEIGHT / 2
            profile.append((bz + h, br))
            profile.append((bz + h, br + BAND_PROTRUDE))
            profile.append((bz - h, br + BAND_PROTRUDE))
            profile.append((bz - h, br))
            inserted.add(bp)
    profile.append(bell_at(t))
    prev_t = t

bell_exit_profile_idx = len(profile) - 1

profile.append((0.0,        EXIT_RADIUS + RIM_WIDTH * 0.35))
profile.append((0.0,        EXIT_RADIUS + RIM_WIDTH))
profile.append((-RIM_DEPTH, EXIT_RADIUS + RIM_WIDTH))
profile.append((-RIM_DEPTH, EXIT_RADIUS))

rings = [add_ring(bm, z, r) for z, r in profile]
for i in range(len(rings) - 1):
    bridge(bm, rings[i], rings[i + 1])

bell_exit_ring = rings[bell_exit_profile_idx]
cap_fan(bm, rings[0], NOZZLE_LENGTH + COLLAR_LENGTH, flip=True)

outer_bell_faces = list(bm.faces)

# ── Inner bell wall ───────────────────────────────────────────────────────────

inner_bell_profile = [(NOZZLE_LENGTH, THROAT_RADIUS - WALL_THICKNESS)]
inserted_inner = set()
prev_t_inner   = 0.0

for i in range(1, RINGS + 1):
    t = i / RINGS
    bz, br = bell_at(t)

    for bp in BAND_POSITIONS:
        if prev_t_inner < bp < t and bp not in inserted_inner:
            ibz, ibr = bell_at(bp)
            h = BAND_HEIGHT / 2
            inner_bell_profile.append((ibz + h, ibr - WALL_THICKNESS))
            inner_bell_profile.append((ibz + h, ibr - WALL_THICKNESS - BAND_PROTRUDE))
            inner_bell_profile.append((ibz - h, ibr - WALL_THICKNESS - BAND_PROTRUDE))
            inner_bell_profile.append((ibz - h, ibr - WALL_THICKNESS))
            inserted_inner.add(bp)

    inner_bell_profile.append((bz, br - WALL_THICKNESS))
    prev_t_inner = t

inner_bell_profile.append((0.0, EXIT_RADIUS - WALL_THICKNESS))

inner_bell_rings = [add_ring(bm, z, r) for z, r in inner_bell_profile]
for i in range(len(inner_bell_rings) - 1):
    bridge(bm, inner_bell_rings[i], inner_bell_rings[i + 1])

for i in range(SEGMENTS):
    j = (i + 1) % SEGMENTS
    bm.faces.new([inner_bell_rings[0][i], inner_bell_rings[0][j], rings[2][j], rings[2][i]])

for i in range(SEGMENTS):
    j = (i + 1) % SEGMENTS
    bm.faces.new([inner_bell_rings[-1][i], inner_bell_rings[-1][j], bell_exit_ring[j], bell_exit_ring[i]])

for i in range(SEGMENTS):
    j = (i + 1) % SEGMENTS
    bm.faces.new([rings[-1][j], rings[-1][i], bell_exit_ring[i], bell_exit_ring[j]])

# ── Rim bracket panels ────────────────────────────────────────────────────────
# Each panel is built by spinning a closed U-channel profile face around Z,
# so Blender computes outward normals automatically — no manual winding needed.
# The strut is a bmesh cube scaled/translated into position.

_rp_segs      = max(4, SEGMENTS // RIM_PANEL_COUNT)
_rp_half      = math.pi / RIM_PANEL_COUNT * RIM_PANEL_ARC_FRAC
_strut_params = []

# U-channel cross-section profile (closed loop, in the XZ plane at angle=0)
# r values are absolute; z values match the rim offsets.
_pp = [
    (EXIT_RADIUS + RIM_WIDTH * 0.35 + RIM_PANEL_OFFSET_R,  RIM_PANEL_OFFSET_Z),
    (EXIT_RADIUS + RIM_WIDTH        + RIM_PANEL_OFFSET_R,  RIM_PANEL_OFFSET_Z),
    (EXIT_RADIUS + RIM_WIDTH        + RIM_PANEL_OFFSET_R, -RIM_DEPTH + RIM_PANEL_OFFSET_Z),
    (EXIT_RADIUS + 0.0              + RIM_PANEL_OFFSET_R, -RIM_DEPTH + RIM_PANEL_OFFSET_Z),
]

for k in range(RIM_PANEL_COUNT):
    center_a = 2 * math.pi * k / RIM_PANEL_COUNT
    start_a  = center_a - _rp_half
    sweep    = 2 * _rp_half

    # Build closed profile face in a temp bmesh at start angle, then spin it
    bm_panel = bmesh.new()
    profile_verts = [
        bm_panel.verts.new((r * math.cos(start_a), r * math.sin(start_a), z))
        for (r, z) in _pp
    ]
    bm_panel.faces.new(profile_verts)
    bmesh.ops.recalc_face_normals(bm_panel, faces=bm_panel.faces[:])

    # Spin around Z axis to sweep the arc
    spin_result = bmesh.ops.spin(
        bm_panel,
        geom        = bm_panel.verts[:] + bm_panel.edges[:] + bm_panel.faces[:],
        axis        = (0, 0, 1),
        cent        = (0, 0, 0),
        angle       = sweep,
        steps       = _rp_segs,
        use_duplicate = False,
    )
    bmesh.ops.recalc_face_normals(bm_panel, faces=bm_panel.faces[:])

    # Merge into main bell bmesh
    src_mesh = bpy.data.meshes.new("_tmp_panel")
    bm_panel.to_mesh(src_mesh)
    bm_panel.free()
    bm.from_mesh(src_mesh)
    bpy.data.meshes.remove(src_mesh)

    # ── Strut (deferred — merged after bell is finalised) ─────────────────────
    _strut_params.append((center_a,))

# ── Finalise outer bell ───────────────────────────────────────────────────────

bmesh.ops.reverse_faces(bm, faces=outer_bell_faces)
bm.normal_update()

mesh_outer = bpy.data.meshes.new("EngineBellMesh")
obj_outer  = bpy.data.objects.new("EngineBell", mesh_outer)
bpy.context.collection.objects.link(obj_outer)
bpy.context.view_layer.objects.active = obj_outer
obj_outer.select_set(True)

bm.to_mesh(mesh_outer)
bm.free()

# ── Merge struts into finalised bell mesh ─────────────────────────────────────
import mathutils

bm_merge = bmesh.new()
bm_merge.from_mesh(mesh_outer)

for (center_a,) in _strut_params:
    ca, sa = math.cos(center_a), math.sin(center_a)
    ta, tb = -sa, ca

    r_mid  = EXIT_RADIUS + RIM_WIDTH + RIM_PANEL_OFFSET_R * 0.5
    z_mid  = (_pp[2][1]) * 0.5
    z_size = abs(_pp[2][1])

    bm_strut = bmesh.new()
    bmesh.ops.create_cube(bm_strut, size=1.0)

    scale = mathutils.Matrix.Diagonal((RIM_STRUT_WIDTH, RIM_STRUT_DEPTH, z_size, 1)).to_4x4()
    rot = mathutils.Matrix([
        [ta,  ca, 0, 0],
        [tb,  sa, 0, 0],
        [0,   0,  1, 0],
        [0,   0,  0, 1],
    ])
    trans = mathutils.Matrix.Translation((r_mid * ca, r_mid * sa, z_mid))
    bmesh.ops.transform(bm_strut, matrix=trans @ rot @ scale, verts=bm_strut.verts[:])
    bmesh.ops.recalc_face_normals(bm_strut, faces=bm_strut.faces[:])

    src_strut = bpy.data.meshes.new("_tmp_strut")
    bm_strut.to_mesh(src_strut)
    bm_strut.free()
    bm_merge.from_mesh(src_strut)
    bpy.data.meshes.remove(src_strut)

bm_merge.to_mesh(mesh_outer)
bm_merge.free()

for poly in mesh_outer.polygons:
    poly.use_smooth = True

mat = bpy.data.materials.new("M_EngineBell")
mat.use_nodes = True
bsdf = mat.node_tree.nodes["Principled BSDF"]
bsdf.inputs["Base Color"].default_value = (0.08, 0.08, 0.10, 1.0)
bsdf.inputs["Metallic"].default_value   = 0.95
bsdf.inputs["Roughness"].default_value  = 0.35
obj_outer.data.materials.append(mat)

bpy.ops.object.select_all(action='DESELECT')
obj_outer.select_set(True)
export_fbx(EXPORT_OUTER)
print(f"EngineBell: {len(mesh_outer.vertices)} verts / {len(mesh_outer.polygons)} faces")
print(f"Exported → {EXPORT_OUTER}")

# ════════════════════════════════════════════════════════════════════════════
# PART 2 — INNER TURBINE STRUCTURE
# ════════════════════════════════════════════════════════════════════════════

bm = bmesh.new()

# ── Hub outer surface with decorative rings ───────────────────────────────────

hub_profile = [(0.0, HUB_OUTER_R)]
for k in range(HUB_RING_COUNT):
    t  = (k + 0.5) / HUB_RING_COUNT
    rz = t * HUB_LENGTH
    h  = HUB_RING_HEIGHT * 0.5
    hub_profile.append((rz - h, HUB_OUTER_R))
    hub_profile.append((rz - h, HUB_OUTER_R + HUB_RING_PROTRUDE))
    hub_profile.append((rz + h, HUB_OUTER_R + HUB_RING_PROTRUDE))
    hub_profile.append((rz + h, HUB_OUTER_R))
hub_profile.append((HUB_LENGTH, HUB_OUTER_R))

hub_outer_rings = [add_ring(bm, z, r) for z, r in hub_profile]
for i in range(len(hub_outer_rings) - 1):
    bridge(bm, hub_outer_rings[i], hub_outer_rings[i + 1])

# ── Hub inner bore ────────────────────────────────────────────────────────────

hub_inner_bot = add_ring(bm, 0.0,        HUB_INNER_R)
hub_inner_top = add_ring(bm, HUB_LENGTH, HUB_INNER_R)
bridge(bm, hub_inner_top, hub_inner_bot)  # top→bot = inward normals

annular_cap(bm, hub_outer_rings[-1], hub_inner_top, flip=False)  # top (+Z)
annular_cap(bm, hub_outer_rings[0],  hub_inner_bot, flip=True)   # bottom (-Z)

# ── Radial vanes ──────────────────────────────────────────────────────────────

for k in range(VANE_COUNT):
    a      = 2 * math.pi * k / VANE_COUNT
    ca, sa = math.cos(a), math.sin(a)
    px, py = -sa, ca

    def pt(r, side, z):
        sign = 1.0 if side == 'A' else -1.0
        return (r * ca + sign * px * VANE_THICK * 0.5,
                r * sa + sign * py * VANE_THICK * 0.5,
                z)

    vane_tip_bot = VANE_OUTER_R - COLLAR_TAPER

    v = [
        bm.verts.new(pt(HUB_OUTER_R,   'A', VANE_BOT_Z)),  # 0 bot hub  A
        bm.verts.new(pt(HUB_OUTER_R,   'B', VANE_BOT_Z)),  # 1 bot hub  B
        bm.verts.new(pt(vane_tip_bot,   'B', VANE_BOT_Z)),  # 2 bot tip  B (tapered)
        bm.verts.new(pt(vane_tip_bot,   'A', VANE_BOT_Z)),  # 3 bot tip  A (tapered)
        bm.verts.new(pt(HUB_OUTER_R,   'A', VANE_TOP_Z)),  # 4 top hub  A
        bm.verts.new(pt(HUB_OUTER_R,   'B', VANE_TOP_Z)),  # 5 top hub  B
        bm.verts.new(pt(VANE_OUTER_R,   'B', VANE_TOP_Z)),  # 6 top tip  B
        bm.verts.new(pt(VANE_OUTER_R,   'A', VANE_TOP_Z)),  # 7 top tip  A
    ]

    bm.faces.new([v[4], v[7], v[3], v[0]])  # side A  (+perp) — winding fixed
    bm.faces.new([v[1], v[2], v[6], v[5]])  # side B  (-perp)
    bm.faces.new([v[5], v[6], v[7], v[4]])  # top cap (+Z)
    bm.faces.new([v[0], v[3], v[2], v[1]])  # bot cap (-Z)
    bm.faces.new([v[0], v[1], v[5], v[4]])  # hub end (-radial)
    bm.faces.new([v[3], v[7], v[6], v[2]])  # col end (+radial)

# ── Outer structural collar ───────────────────────────────────────────────────

collar_outer_bot = add_ring(bm, VANE_BOT_Z, COLLAR_OUTER_R - COLLAR_TAPER)
collar_outer_top = add_ring(bm, VANE_TOP_Z, COLLAR_OUTER_R)
collar_inner_bot = add_ring(bm, VANE_BOT_Z, COLLAR_INNER_R - COLLAR_TAPER)
collar_inner_top = add_ring(bm, VANE_TOP_Z, COLLAR_INNER_R)

bridge(bm, collar_outer_bot, collar_outer_top)
bridge(bm, collar_inner_top, collar_inner_bot)
annular_cap(bm, collar_outer_top, collar_inner_top, flip=False)
annular_cap(bm, collar_outer_bot, collar_inner_bot, flip=True)

# ── Flare panels hanging from the collar bottom ───────────────────────────────
# Each panel roots at COLLAR_INNER_R (inside face of collar), at z=VANE_BOT_Z,
# and its tip angles inward (smaller radius) and downward (lower z).

_flare_base_r = COLLAR_INNER_R - COLLAR_TAPER
_flare_base_z = VANE_BOT_Z
_flare_tip_r  = _flare_base_r - FLARE_HEIGHT * math.sin(FLARE_TILT)
_flare_tip_z  = _flare_base_z - FLARE_HEIGHT * math.cos(FLARE_TILT)

for k in range(FLARE_COUNT):
    angle = 2 * math.pi * k / FLARE_COUNT
    ca, sa = math.cos(angle), math.sin(angle)
    ta, tb = -sa, ca  # tangent direction for arc width

    def fp(r, half_w, z, sign):
        return (r * ca + sign * ta * half_w,
                r * sa + sign * tb * half_w,
                z)

    # Outer (radially outward) face of the panel — base and tip
    bo_a = bm.verts.new(fp(_flare_base_r,                    FLARE_BASE_WIDTH, _flare_base_z, +1))
    bo_b = bm.verts.new(fp(_flare_base_r,                    FLARE_BASE_WIDTH, _flare_base_z, -1))
    to_a = bm.verts.new(fp(_flare_tip_r,                     FLARE_TIP_WIDTH,  _flare_tip_z,  +1))
    to_b = bm.verts.new(fp(_flare_tip_r,                     FLARE_TIP_WIDTH,  _flare_tip_z,  -1))
    # Inner (radially inward) face — offset inward by FLARE_THICKNESS
    bi_a = bm.verts.new(fp(_flare_base_r - FLARE_THICKNESS,  FLARE_BASE_WIDTH, _flare_base_z, +1))
    bi_b = bm.verts.new(fp(_flare_base_r - FLARE_THICKNESS,  FLARE_BASE_WIDTH, _flare_base_z, -1))
    ti_a = bm.verts.new(fp(_flare_tip_r  - FLARE_THICKNESS,  FLARE_TIP_WIDTH,  _flare_tip_z,  +1))
    ti_b = bm.verts.new(fp(_flare_tip_r  - FLARE_THICKNESS,  FLARE_TIP_WIDTH,  _flare_tip_z,  -1))

    bm.faces.new([bo_a, bo_b, to_b, to_a])  # outer face
    bm.faces.new([bi_b, bi_a, ti_a, ti_b])  # inner face
    bm.faces.new([bo_a, to_a, ti_a, bi_a])  # side A
    bm.faces.new([bo_b, bi_b, ti_b, to_b])  # side B
    bm.faces.new([to_a, to_b, ti_b, ti_a])  # tip cap
    bm.faces.new([bo_b, bo_a, bi_a, bi_b])  # base cap (against collar)

# ── Finalise inner structure ──────────────────────────────────────────────────

bm.normal_update()

mesh_inner = bpy.data.meshes.new("EngineNozzleMesh")
obj_inner  = bpy.data.objects.new("EngineNozzle", mesh_inner)
bpy.context.collection.objects.link(obj_inner)
bpy.context.view_layer.objects.active = obj_inner
obj_inner.select_set(True)

bm.to_mesh(mesh_inner)
bm.free()

for poly in mesh_inner.polygons:
    poly.use_smooth = True

mat2 = bpy.data.materials.new("M_EngineNozzle")
mat2.use_nodes = True
bsdf2 = mat2.node_tree.nodes["Principled BSDF"]
bsdf2.inputs["Base Color"].default_value = (0.08, 0.08, 0.10, 1.0)
bsdf2.inputs["Metallic"].default_value   = 0.95
bsdf2.inputs["Roughness"].default_value  = 0.35
obj_inner.data.materials.append(mat2)

bpy.ops.object.select_all(action='DESELECT')
obj_inner.select_set(True)
export_fbx(EXPORT_INNER)
print(f"EngineNozzle: {len(mesh_inner.vertices)} verts / {len(mesh_inner.polygons)} faces")
print(f"Exported → {EXPORT_INNER}")

# ════════════════════════════════════════════════════════════════════════════
# PART 3 — PBR TEXTURE MAPS  (skipped if SOURCE_TEXTURE is empty)
# ════════════════════════════════════════════════════════════════════════════

if SOURCE_TEXTURE:
    import numpy as np
    import os

    src_name = os.path.basename(SOURCE_TEXTURE)
    for img in list(bpy.data.images):
        if img.name == src_name:
            bpy.data.images.remove(img)

    src  = bpy.data.images.load(SOURCE_TEXTURE)
    W, H = src.size
    raw  = np.empty(W * H * 4, dtype=np.float32)
    src.pixels.foreach_get(raw)
    px   = raw.reshape(H, W, 4)

    lum  = (0.2126 * px[...,0] + 0.7152 * px[...,1] + 0.0722 * px[...,2]).astype(np.float64)

    # Normal map via luminance gradient (Sobel-equivalent)
    dy, dx = np.gradient(lum)
    dx    *= TEX_NORMAL_STRENGTH
    dy    *= TEX_NORMAL_STRENGTH
    dz     = np.ones_like(dx)
    mag    = np.sqrt(dx**2 + dy**2 + dz**2)
    norm_px = np.stack([
        (dx / mag * 0.5 + 0.5).astype(np.float32),
        (dy / mag * 0.5 + 0.5).astype(np.float32),
        (dz / mag * 0.5 + 0.5).astype(np.float32),
        np.ones((H, W), dtype=np.float32),
    ], axis=-1)

    # MaskMap — HDRP convention: R=Metallic, G=AO, B=Detail (0.5), A=Smoothness
    metallic = np.clip(TEX_METALLIC_BASE + (lum - 0.5) * 0.3, 0.0, 1.0).astype(np.float32)
    smooth   = np.clip(TEX_SMOOTH_BASE   +  lum * TEX_SMOOTH_SCALE,     0.0, 1.0).astype(np.float32)
    ao       = np.clip(lum * 1.2, 0.0, 1.0).astype(np.float32)  # bright areas = no occlusion
    mask_px  = np.stack([
        metallic,
        ao,
        np.full((H, W), 0.5, dtype=np.float32),
        smooth,
    ], axis=-1)

    out_dir = os.path.dirname(EXPORT_OUTER)

    def save_tex(name, data, linear=False):
        img = bpy.data.images.new(name, width=W, height=H, alpha=True)
        img.pixels.foreach_set(data.ravel())
        if linear:
            img.colorspace_settings.name = 'Non-Color'
        path = os.path.join(out_dir, name + ".png")
        img.filepath_raw = path
        img.file_format  = 'PNG'
        img.save()
        bpy.data.images.remove(img)
        print(f"Saved → {path}")

    save_tex("T_Engine_BaseColor", px,       linear=False)
    save_tex("T_Engine_Normal",    norm_px,  linear=True)
    save_tex("T_Engine_MaskMap",   mask_px,  linear=True)
    print("Texture generation complete.")
