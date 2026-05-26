import bpy
import bmesh
import math
import os

# ── Parameters ───────────────────────────────────────────────────────────────

SCALE = 2.0   # uniform scale baked into geometry (Unity transform stays at 1,1,1)

# Bell shape  (wide shallow dish, like the reference)
THROAT_RADIUS  = 2.22  * SCALE
EXIT_RADIUS    = 5.20  * SCALE
NOZZLE_LENGTH  = 5.40  * SCALE
BELL_POWER     = 0.42            # < 0.5 = deep concave dish profile

# Collar (attaches to ship body)
COLLAR_RADIUS  = 2.22  * SCALE
COLLAR_LENGTH  = 0.45  * SCALE

# Raised circumferential bands on bell exterior
BAND_POSITIONS = [0.11, 0.22, 0.33, 0.55, 0.66, 0.88]
BAND_HEIGHT    = 0.10  * SCALE
BAND_PROTRUDE  = 0.05  * SCALE

# Bell wall thickness
WALL_THICKNESS = 0.60  * SCALE

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
FLARE_TIP_WIDTH  = 0.04  * SCALE  # tangential half-width at tip
FLARE_HEIGHT     = 0.50  * SCALE  # axial+radial extent downward from collar
FLARE_THICKNESS  = 0.03  * SCALE  # panel thickness
FLARE_TILT       = -0.10           # inward tilt in radians (~17°) — tip angles toward axis

# Resolution
SEGMENTS       = 32
RINGS          = 24

# Inner turbine structure — hub
HUB_OUTER_R       = 0.58  * SCALE
HUB_INNER_R       = 0.42  * SCALE
HUB_LENGTH        = 5.10  * SCALE
HUB_RING_COUNT    = 10
HUB_RING_HEIGHT   = 0.050 * SCALE
HUB_RING_PROTRUDE = 0.042 * SCALE

# Inner turbine structure — vanes
VANE_COUNT   = 16
VANE_THICK   = 0.055 * SCALE
VANE_HEIGHT  = HUB_LENGTH * 0.80
VANE_OUTER_R = 1.95 * SCALE   # tip radius — set larger than COLLAR_INNER_R to stick out past collar
VANE_SPIRAL  = 0.38           # angular offset in radians between vane top and bottom (positive = swept forward)

# Inner turbine structure — outer collar
COLLAR_INNER_R = 1.70 * SCALE
COLLAR_WALL    = 0.10 * SCALE
COLLAR_TAPER   = 0.65 * SCALE  # how much the collar narrows at the opening (exit) end

EXPORT_OUTER     = "C:/Users/user/source/repos/ShipbreakerShipbuilder/Assets/_CustomShips/Rocinante/Models/rocinante_engine_bell.fbx"
EXPORT_OUTER_COL = "C:/Users/user/source/repos/ShipbreakerShipbuilder/Assets/_CustomShips/Rocinante/Models/rocinante_engine_bell_col.fbx"
EXPORT_INNER     = "C:/Users/user/source/repos/ShipbreakerShipbuilder/Assets/_CustomShips/Rocinante/Models/rocinante_engine_nozzle.fbx"

# Set to a source image path to generate PBR textures; leave blank to skip
SOURCE_TEXTURE   = "C:/Users/user/source/repos/ShipbreakerShipbuilder/Assets/_CustomShips/Rocinante/Textures/EpsDrive.png"

# Texture generation parameters
TEX_NORMAL_STRENGTH    = 6.0   # Sobel normal map intensity
TEX_HEIGHT_BLUR        = 4.0   # Gaussian sigma for macro height extraction
TEX_HEIGHT_MICRO       = 3.0   # micro-detail amplification in height map
TEX_METALLIC_BASE      = 0.85  # metallic for fully desaturated (grey) pixels
TEX_METALLIC_SAT_SCALE = 0.60  # how much colour saturation pulls metallic down
TEX_SMOOTH_BASE        = 0.90  # smoothness for flat (low-variance) areas
TEX_SMOOTH_SCALE       = 0.80  # how much local roughness reduces smoothness
TEX_AO_RADIUS          = 16    # pixel radius for AO horizon sampling
TEX_AO_SAMPLES         = 12    # number of AO ray directions

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
    os.makedirs(os.path.dirname(filepath), exist_ok=True)
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

# ── Collar as a solid tube (outer + inner surface + top/bottom annular caps)
# Built before snapshotting so these faces are excluded from reverse_faces.
collar_top_outer = rings[0]   # top of collar at NOZZLE_LENGTH + COLLAR_LENGTH
collar_bot_outer = rings[1]   # bottom of collar at NOZZLE_LENGTH
collar_top_inner = add_ring(bm, NOZZLE_LENGTH + COLLAR_LENGTH, COLLAR_RADIUS - WALL_THICKNESS)
collar_bot_inner = add_ring(bm, NOZZLE_LENGTH,                 COLLAR_RADIUS - WALL_THICKNESS)

bridge(bm, collar_bot_outer, collar_top_outer)   # outer wall
bridge(bm, collar_top_inner, collar_bot_inner)   # inner wall
annular_cap(bm, collar_top_outer, collar_top_inner, flip=False)  # top annular cap
annular_cap(bm, collar_bot_outer, collar_bot_inner, flip=True)   # bottom annular cap

bridge(bm, rings[1], rings[2])  # collar-to-throat step (single surface, excluded from reverse)

_after_collar = set(bm.faces)
for i in range(2, len(rings) - 1):
    bridge(bm, rings[i], rings[i + 1])

bell_exit_ring = rings[bell_exit_profile_idx]

outer_bell_faces = [f for f in bm.faces if f not in _after_collar]

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

# Cylindrical UV — U = angle / 2π, V = normalized Z
uv_layer = bm.loops.layers.uv.new("UVMap")
_z_min = -RIM_DEPTH
_z_rng = NOZZLE_LENGTH + COLLAR_LENGTH - _z_min
for face in bm.faces:
    us = [(math.atan2(lp.vert.co.y, lp.vert.co.x) / (2 * math.pi)) % 1.0
          for lp in face.loops]
    if max(us) - min(us) > 0.5:  # face straddles the seam — shift low side up
        us = [u + 1.0 if u < 0.5 else u for u in us]
    for lp, u in zip(face.loops, us):
        lp[uv_layer].uv = (u, (lp.vert.co.z - _z_min) / _z_rng)

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
# PART 1b — COLLISION WEDGE MESH
# A single 45° arc wedge of the bell wall (outer + inner surface + end caps).
# Eight copies rotated 0–315° in Unity approximate the full hollow bell.
# Each wedge is individually convex, so the hull hugs the wall tightly and
# leaves the throat open.
# ════════════════════════════════════════════════════════════════════════════

COL_RINGS = 16   # axial resolution of wedge bell curve
COL_SEGS  = 6    # angular steps within the 45° arc
WEDGE_ARC = math.pi / 4   # 45°

# Match the main bell profile exactly: collar, throat step, bell curve with
# bands, and exit rim — so the wedge collision shape tracks all parameters.
col_pts = []
col_pts.append((NOZZLE_LENGTH + COLLAR_LENGTH, COLLAR_RADIUS))
col_pts.append((NOZZLE_LENGTH,                 COLLAR_RADIUS))
col_pts.append((NOZZLE_LENGTH,                 THROAT_RADIUS))

_col_inserted = set()
_col_prev_t   = 0.0
for i in range(1, COL_RINGS + 1):
    t = i / COL_RINGS
    for bp in BAND_POSITIONS:
        if _col_prev_t < bp < t and bp not in _col_inserted:
            bz, br = bell_at(bp)
            h = BAND_HEIGHT / 2
            col_pts.append((bz + h, br))
            col_pts.append((bz + h, br + BAND_PROTRUDE))
            col_pts.append((bz - h, br + BAND_PROTRUDE))
            col_pts.append((bz - h, br))
            _col_inserted.add(bp)
    col_pts.append(bell_at(t))
    _col_prev_t = t

col_pts.append((0.0,        EXIT_RADIUS + RIM_WIDTH * 0.35))
col_pts.append((0.0,        EXIT_RADIUS + RIM_WIDTH))
col_pts.append((-RIM_DEPTH, EXIT_RADIUS + RIM_WIDTH))
col_pts.append((-RIM_DEPTH, EXIT_RADIUS))

bm_col = bmesh.new()

# Build outer and inner rings for each profile point across the 45° arc
def wedge_ring(bm, z, r_outer, r_inner, arc_start, arc_end, steps):
    outer, inner = [], []
    for s in range(steps + 1):
        a = arc_start + (arc_end - arc_start) * s / steps
        ca, sa = math.cos(a), math.sin(a)
        outer.append(bm.verts.new((r_outer * ca, r_outer * sa, z)))
        inner.append(bm.verts.new((r_inner * ca, r_inner * sa, z)))
    return outer, inner

arc_start = -WEDGE_ARC / 2
arc_end   =  WEDGE_ARC / 2

outer_rings, inner_rings = [], []
for (z, r) in col_pts:
    o, i = wedge_ring(bm_col, z, r, r - WALL_THICKNESS, arc_start, arc_end, COL_SEGS)
    outer_rings.append(o)
    inner_rings.append(i)

# Outer surface
for i in range(len(outer_rings) - 1):
    ra, rb = outer_rings[i], outer_rings[i + 1]
    for s in range(COL_SEGS):
        bm_col.faces.new([ra[s], ra[s+1], rb[s+1], rb[s]])

# Inner surface
for i in range(len(inner_rings) - 1):
    ra, rb = inner_rings[i], inner_rings[i + 1]
    for s in range(COL_SEGS):
        bm_col.faces.new([ra[s], rb[s], rb[s+1], ra[s+1]])

# Side caps (the two flat 45° cut faces)
for i in range(len(outer_rings) - 1):
    o, io = outer_rings[i], inner_rings[i]
    on, ion = outer_rings[i+1], inner_rings[i+1]
    bm_col.faces.new([o[0],  io[0],  ion[0], on[0]])   # start edge
    bm_col.faces.new([o[-1], on[-1], ion[-1], io[-1]])  # end edge


bmesh.ops.recalc_face_normals(bm_col, faces=bm_col.faces[:])

mesh_col = bpy.data.meshes.new("EngineBellMesh_Col")
obj_col  = bpy.data.objects.new("EngineBell_Col", mesh_col)
bpy.context.collection.objects.link(obj_col)
bpy.context.view_layer.objects.active = obj_col
obj_col.select_set(True)
bm_col.to_mesh(mesh_col)
bm_col.free()

bpy.ops.object.select_all(action='DESELECT')
obj_col.select_set(True)
export_fbx(EXPORT_OUTER_COL)
print(f"EngineBell_Col: {len(mesh_col.vertices)} verts / {len(mesh_col.polygons)} faces")
print(f"Exported → {EXPORT_OUTER_COL}")

bpy.data.objects.remove(obj_col, do_unlink=True)
bpy.data.meshes.remove(mesh_col)

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
    a_top = 2 * math.pi * k / VANE_COUNT
    a_bot = a_top + VANE_SPIRAL

    ca_t, sa_t = math.cos(a_top), math.sin(a_top)
    ca_b, sa_b = math.cos(a_bot), math.sin(a_bot)
    px_t, py_t = -sa_t, ca_t  # tangent at top
    px_b, py_b = -sa_b, ca_b  # tangent at bottom

    def pt_top(r, side):
        sign = 1.0 if side == 'A' else -1.0
        return (r * ca_t + sign * px_t * VANE_THICK * 0.5,
                r * sa_t + sign * py_t * VANE_THICK * 0.5,
                VANE_TOP_Z)

    def pt_bot(r, side):
        sign = 1.0 if side == 'A' else -1.0
        return (r * ca_b + sign * px_b * VANE_THICK * 0.5,
                r * sa_b + sign * py_b * VANE_THICK * 0.5,
                VANE_BOT_Z)

    vane_tip_bot = VANE_OUTER_R - COLLAR_TAPER

    v = [
        bm.verts.new(pt_bot(HUB_OUTER_R,  'A')),  # 0 bot hub  A
        bm.verts.new(pt_bot(HUB_OUTER_R,  'B')),  # 1 bot hub  B
        bm.verts.new(pt_bot(vane_tip_bot,  'B')),  # 2 bot tip  B
        bm.verts.new(pt_bot(vane_tip_bot,  'A')),  # 3 bot tip  A
        bm.verts.new(pt_top(HUB_OUTER_R,  'A')),  # 4 top hub  A
        bm.verts.new(pt_top(HUB_OUTER_R,  'B')),  # 5 top hub  B
        bm.verts.new(pt_top(VANE_OUTER_R,  'B')),  # 6 top tip  B
        bm.verts.new(pt_top(VANE_OUTER_R,  'A')),  # 7 top tip  A
    ]

    bm.faces.new([v[4], v[7], v[3], v[0]])  # side A
    bm.faces.new([v[1], v[2], v[6], v[5]])  # side B
    bm.faces.new([v[5], v[6], v[7], v[4]])  # top cap
    bm.faces.new([v[0], v[3], v[2], v[1]])  # bot cap
    bm.faces.new([v[0], v[1], v[5], v[4]])  # hub end
    bm.faces.new([v[3], v[7], v[6], v[2]])  # col end

# ── Outer structural collar ───────────────────────────────────────────────────

collar_outer_bot = add_ring(bm, VANE_BOT_Z, COLLAR_OUTER_R - COLLAR_TAPER)
collar_outer_top = add_ring(bm, VANE_TOP_Z, COLLAR_OUTER_R)
collar_inner_bot = add_ring(bm, VANE_BOT_Z, COLLAR_INNER_R - COLLAR_TAPER)
collar_inner_top = add_ring(bm, VANE_TOP_Z, COLLAR_INNER_R)

bridge(bm, collar_outer_bot, collar_outer_top)
_before_inner_collar = set(bm.faces)
bridge(bm, collar_inner_bot, collar_inner_top)
_inner_collar_faces = [f for f in bm.faces if f not in _before_inner_collar]
bmesh.ops.reverse_faces(bm, faces=_inner_collar_faces)
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

    rgb  = px[..., :3].astype(np.float64)
    lum  = 0.2126 * rgb[...,0] + 0.7152 * rgb[...,1] + 0.0722 * rgb[...,2]

    # ── Helpers ───────────────────────────────────────────────────────────────

    def gauss_blur(arr, sigma):
        """Separable Gaussian blur — pure numpy, no scipy."""
        if sigma <= 0:
            return arr.copy()
        radius = max(1, int(3 * sigma + 0.5))
        x = np.arange(-radius, radius + 1, dtype=np.float64)
        k = np.exp(-0.5 * (x / sigma) ** 2);  k /= k.sum()
        out = np.apply_along_axis(lambda r: np.convolve(r, k, mode='same'), 1, arr)
        return  np.apply_along_axis(lambda c: np.convolve(c, k, mode='same'), 0, out)

    def conv3x3(arr, kernel):
        """3×3 convolution via unrolled array slicing — no scipy needed."""
        p = np.pad(arr, 1, mode='edge')
        return (kernel[0,0]*p[0:-2,0:-2] + kernel[0,1]*p[0:-2,1:-1] + kernel[0,2]*p[0:-2,2:] +
                kernel[1,0]*p[1:-1,0:-2] + kernel[1,1]*p[1:-1,1:-1] + kernel[1,2]*p[1:-1,2:] +
                kernel[2,0]*p[2:, 0:-2] + kernel[2,1]*p[2:, 1:-1] + kernel[2,2]*p[2:, 2:])

    # ── Height map ────────────────────────────────────────────────────────────
    # Macro shape from blurred lum blended with boosted micro-detail
    height = np.clip(
        gauss_blur(lum, TEX_HEIGHT_BLUR) * 0.7 +
        (0.5 + (lum - gauss_blur(lum, 2.0)) * TEX_HEIGHT_MICRO) * 0.3,
        0.0, 1.0)

    # ── Normal map via 3×3 Sobel ──────────────────────────────────────────────
    Kx = np.array([[-1,0,1],[-2,0,2],[-1,0,1]], dtype=np.float64)
    Ky = np.array([[-1,-2,-1],[0,0,0],[1,2,1]], dtype=np.float64)
    dx = conv3x3(height, Kx) * TEX_NORMAL_STRENGTH
    dy = conv3x3(height, Ky) * TEX_NORMAL_STRENGTH
    dz = np.ones_like(dx)
    mag = np.sqrt(dx**2 + dy**2 + dz**2)
    norm_px = np.stack([
        (dx / mag * 0.5 + 0.5).astype(np.float32),
        (dy / mag * 0.5 + 0.5).astype(np.float32),
        (dz / mag * 0.5 + 0.5).astype(np.float32),
        np.ones((H, W), dtype=np.float32),
    ], axis=-1)

    # ── AO via horizon sampling ───────────────────────────────────────────────
    # Cast rays in TEX_AO_SAMPLES directions; neighbours higher than centre occlude it.
    _ao_acc = np.zeros((H, W), dtype=np.float64)
    for _i in range(TEX_AO_SAMPLES):
        _a  = 2 * np.pi * _i / TEX_AO_SAMPLES
        _sx = int(round(TEX_AO_RADIUS * np.cos(_a)))
        _sy = int(round(TEX_AO_RADIUS * np.sin(_a)))
        _nb = np.roll(np.roll(height, -_sy, axis=0), -_sx, axis=1)
        _ao_acc += np.clip((_nb - height) * 4.0, 0.0, 1.0)
    ao = np.clip(1.0 - _ao_acc / TEX_AO_SAMPLES, 0.0, 1.0).astype(np.float32)

    # ── Smoothness from local variance (flat area = smooth, detailed = rough) ─
    _lm  = gauss_blur(lum, 2.0)
    _lm2 = gauss_blur(lum ** 2, 2.0)
    smooth = np.clip(
        TEX_SMOOTH_BASE - np.sqrt(np.clip(_lm2 - _lm**2, 0.0, None)) * 8.0 * TEX_SMOOTH_SCALE,
        0.0, 1.0).astype(np.float32)

    # ── Metallic from colour desaturation (grey/neutral = metal, saturated = non-metal)
    _maxc = np.max(rgb, axis=-1)
    _minc = np.min(rgb, axis=-1)
    _sat  = np.where(_maxc > 1e-6, (_maxc - _minc) / _maxc, 0.0)
    metallic = np.clip(TEX_METALLIC_BASE - _sat * TEX_METALLIC_SAT_SCALE, 0.0, 1.0).astype(np.float32)

    # ── MaskMap (HDRP: R=Metallic, G=AO, B=Detail 0.5, A=Smoothness) ─────────
    mask_px = np.stack([metallic, ao, np.full((H, W), 0.5, np.float32), smooth], axis=-1)

    out_dir = os.path.join(os.path.dirname(os.path.dirname(EXPORT_OUTER)), "Textures")
    os.makedirs(out_dir, exist_ok=True)

    def save_tex(name, data, linear=False):
        img = bpy.data.images.new(name, width=W, height=H, alpha=True)
        if linear:
            img.colorspace_settings.name = 'Non-Color'
        img.pixels.foreach_set(data.ravel().tolist())
        img.update()
        path = os.path.join(out_dir, name + ".png")
        img.filepath_raw = path
        img.file_format  = 'PNG'
        img.save()
        bpy.data.images.remove(img)
        print(f"Saved → {path}")

    save_tex("T_Engine_BaseColor", px.astype(np.float32), linear=False)
    save_tex("T_Engine_Normal",    norm_px,               linear=True)
    save_tex("T_Engine_MaskMap",   mask_px,               linear=True)
    print("Texture generation complete.")
