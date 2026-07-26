"""
Replicates the exact game algorithm from RigidbodyUtils.CalculateMassFromVolume:
  SignedVolumeOfTriangle = (1/6) * scalar triple product
  Volume = abs(sum over all triangles)
  Mass = Volume * density
"""
import math

# ── Parameters (must match generate_nozzle.py exactly) ───────────────────────
SCALE          = 2.0
THROAT_RADIUS  = 3.22  * SCALE
EXIT_RADIUS    = 5.20  * SCALE
NOZZLE_LENGTH  = 5.40  * SCALE
BELL_POWER     = 0.42
COLLAR_RADIUS  = 3.95  * SCALE
COLLAR_LENGTH  = 0.08  * SCALE
BAND_POSITIONS = [0.11, 0.22, 0.33, 0.55, 0.66, 0.88]
BAND_HEIGHT    = 0.10  * SCALE
BAND_PROTRUDE  = 0.05  * SCALE
WALL_THICKNESS = 0.38  * SCALE
RIM_WIDTH      = 0.10  * SCALE
RIM_DEPTH      = 0.34  * SCALE
INNER_TOP_R    = 1.70  * SCALE
INNER_BOT_R    = 1.08  * SCALE
INNER_TOP_Z    = NOZZLE_LENGTH * 0.90
INNER_BOT_Z    = NOZZLE_LENGTH * 0.08
INNER_SEGS     = 32
FIN_COUNT      = 8
FIN_REACH      = 0.17  * SCALE
FIN_THICK      = 0.212 * SCALE
STRUT_COUNT    = 3
STRUT_R        = 0.028 * SCALE
STRUT_SEGS     = 8
SEGMENTS       = 128
RINGS          = 72

# ── Vertex / face tracking ────────────────────────────────────────────────────
verts = []
tris  = []

def add_v(x, y, z):
    verts.append((x, y, z))
    return len(verts) - 1

def add_ring(z, r, segs=None):
    if segs is None: segs = SEGMENTS
    return [
        add_v(r * math.cos(2*math.pi*i/segs),
              r * math.sin(2*math.pi*i/segs), z)
        for i in range(segs)
    ]

def bridge(ra, rb):
    n = len(ra)
    for i in range(n):
        j = (i + 1) % n
        tris.append((ra[i], ra[j], rb[j]))
        tris.append((ra[i], rb[j], rb[i]))

def cap_fan(rv, z, flip=False):
    c = add_v(0, 0, z)
    n = len(rv)
    for i in range(n):
        j = (i + 1) % n
        if flip:
            tris.append((rv[j], rv[i], c))
        else:
            tris.append((rv[i], rv[j], c))

def snap():
    return len(tris)

# ── 1. Outer bell + collar + rim ─────────────────────────────────────────────
def r_bell(t):
    return THROAT_RADIUS + (EXIT_RADIUS - THROAT_RADIUS) * (t ** BELL_POWER)

def bell_at(t):
    return NOZZLE_LENGTH * (1.0 - t), r_bell(t)

profile = [
    (NOZZLE_LENGTH + COLLAR_LENGTH, COLLAR_RADIUS),
    (NOZZLE_LENGTH,                 COLLAR_RADIUS),
    (NOZZLE_LENGTH,                 THROAT_RADIUS),
]
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

bell_exit_idx = len(profile) - 1
profile += [
    (0.0,        EXIT_RADIUS + RIM_WIDTH * 0.35),
    (0.0,        EXIT_RADIUS + RIM_WIDTH),
    (-RIM_DEPTH, EXIT_RADIUS + RIM_WIDTH),
    (-RIM_DEPTH, EXIT_RADIUS),
]

rings_outer = [add_ring(z, r) for z, r in profile]
outer_start = snap()
for i in range(len(rings_outer) - 1):
    bridge(rings_outer[i], rings_outer[i + 1])
cap_fan(rings_outer[0], NOZZLE_LENGTH + COLLAR_LENGTH, flip=True)
outer_end = snap()
outer_bell_tris = set(range(outer_start, outer_end))
bell_exit_ring = rings_outer[bell_exit_idx]

# ── 2. Inner bell surface ─────────────────────────────────────────────────────
inner_bell_profile = [(NOZZLE_LENGTH, THROAT_RADIUS - WALL_THICKNESS)]
for i in range(1, RINGS + 1):
    t = i / RINGS
    bz, br = bell_at(t)
    inner_bell_profile.append((bz, br - WALL_THICKNESS))
inner_bell_profile.append((0.0, EXIT_RADIUS - WALL_THICKNESS))

inner_bell_rings = [add_ring(z, r) for z, r in inner_bell_profile]
for i in range(len(inner_bell_rings) - 1):
    bridge(inner_bell_rings[i], inner_bell_rings[i + 1])

for i in range(SEGMENTS):
    j = (i + 1) % SEGMENTS
    # throat cap
    tris.append((inner_bell_rings[0][i], inner_bell_rings[0][j],
                 rings_outer[2][j]))
    tris.append((inner_bell_rings[0][i], rings_outer[2][j],
                 rings_outer[2][i]))
    # exit cap
    tris.append((inner_bell_rings[-1][i], inner_bell_rings[-1][j],
                 bell_exit_ring[j]))
    tris.append((inner_bell_rings[-1][i], bell_exit_ring[j],
                 bell_exit_ring[i]))
    # rim inner face
    tris.append((rings_outer[-1][j], rings_outer[-1][i],
                 bell_exit_ring[i]))
    tris.append((rings_outer[-1][j], bell_exit_ring[i],
                 bell_exit_ring[j]))

inner_bell_end = snap()

# ── 3. Inner cone ─────────────────────────────────────────────────────────────
cone_start = snap()
top_ring = add_ring(INNER_TOP_Z, INNER_TOP_R, INNER_SEGS)
bot_ring = add_ring(INNER_BOT_Z, INNER_BOT_R, INNER_SEGS)
bridge(top_ring, bot_ring)
cap_fan(top_ring, INNER_TOP_Z, flip=True)
cap_fan(bot_ring, INNER_BOT_Z)
cone_end = snap()
cone_tris = set(range(cone_start, cone_end))

# ── 4. Fins ───────────────────────────────────────────────────────────────────
fin_start = snap()
for k in range(FIN_COUNT):
    a = 2 * math.pi * k / FIN_COUNT
    ca, sa = math.cos(a), math.sin(a)
    px, py = -sa, ca

    def fv(r, z, _ca=ca, _sa=sa, _px=px, _py=py):
        return add_v(
            r*_ca + _px*FIN_THICK*0.5,
            r*_sa + _py*FIN_THICK*0.5,
            z)

    def fv2(r, z, _ca=ca, _sa=sa, _px=px, _py=py):
        return add_v(
            r*_ca - _px*FIN_THICK*0.5,
            r*_sa - _py*FIN_THICK*0.5,
            z)

    it = INNER_TOP_R;  ib = INNER_BOT_R
    ot = INNER_TOP_R + FIN_REACH;  ob = INNER_BOT_R + FIN_REACH

    v = [
        fv(it, INNER_TOP_Z), fv2(it, INNER_TOP_Z),
        fv2(ib, INNER_BOT_Z), fv(ib, INNER_BOT_Z),
        fv(ot, INNER_TOP_Z), fv2(ot, INNER_TOP_Z),
        fv2(ob, INNER_BOT_Z), fv(ob, INNER_BOT_Z),
    ]

    def quad(a, b, c, d):
        tris.append((a, b, c))
        tris.append((a, c, d))

    quad(v[0],v[1],v[2],v[3])  # inner face
    quad(v[5],v[4],v[7],v[6])  # outer face
    quad(v[4],v[0],v[3],v[7])  # side A
    quad(v[1],v[5],v[6],v[2])  # side B
    quad(v[4],v[5],v[1],v[0])  # top cap
    quad(v[3],v[2],v[6],v[7])  # bot cap

fin_end = snap()
fin_tris = set(range(fin_start, fin_end))

# ── 5. Struts (open tubes, no end caps) ──────────────────────────────────────
bell_z_mid, bell_r_mid = bell_at(0.55)
inner_r_mid = (INNER_TOP_R + INNER_BOT_R) * 0.5
inner_z_mid = (INNER_TOP_Z + INNER_BOT_Z) * 0.5

for k in range(STRUT_COUNT):
    a = 2 * math.pi * k / STRUT_COUNT
    ca, sa = math.cos(a), math.sin(a)
    p1 = (inner_r_mid*ca, inner_r_mid*sa, inner_z_mid)
    p2 = (bell_r_mid*0.82*ca, bell_r_mid*0.82*sa, bell_z_mid)
    dx,dy,dz = p2[0]-p1[0], p2[1]-p1[1], p2[2]-p1[2]
    ln = math.sqrt(dx*dx + dy*dy + dz*dz)
    nx,ny,nz = dx/ln, dy/ln, dz/ln
    ax,ay,az = (0,0,1) if abs(nx) < 0.9 else (0,1,0)
    bx = ny*az - nz*ay
    by = nz*ax - nx*az
    bz_ = nx*ay - ny*ax
    bl = math.sqrt(bx*bx + by*by + bz_*bz_)
    bx,by,bz_ = bx/bl, by/bl, bz_/bl
    cx_ = ny*bz_ - nz*by
    cy_ = nz*bx  - nx*bz_
    cz_ = nx*by  - ny*bx
    r1, r2 = [], []
    for s in range(STRUT_SEGS):
        sa2 = 2 * math.pi * s / STRUT_SEGS
        ox = STRUT_R*(math.cos(sa2)*bx + math.sin(sa2)*cx_)
        oy = STRUT_R*(math.cos(sa2)*by + math.sin(sa2)*cy_)
        oz = STRUT_R*(math.cos(sa2)*bz_ + math.sin(sa2)*cz_)
        r1.append(add_v(p1[0]+ox, p1[1]+oy, p1[2]+oz))
        r2.append(add_v(p2[0]+ox, p2[1]+oy, p2[2]+oz))
    bridge(r1, r2)

# ── Apply face reversals matching bmesh.ops.reverse_faces ────────────────────
reversed_tris = outer_bell_tris | cone_tris | fin_tris
final_tris = []
for idx, (a, b, c) in enumerate(tris):
    if idx in reversed_tris:
        final_tris.append((a, c, b))
    else:
        final_tris.append((a, b, c))

# ── Signed volume (exact C# formula from RigidbodyUtils) ─────────────────────
def signed_vol(p1, p2, p3):
    return (1.0 / 6.0) * (
        - p3[0]*p2[1]*p1[2]
        + p2[0]*p3[1]*p1[2]
        + p3[0]*p1[1]*p2[2]
        - p1[0]*p3[1]*p2[2]
        - p2[0]*p1[1]*p3[2]
        + p1[0]*p2[1]*p3[2]
    )

total   = sum(signed_vol(verts[a], verts[b], verts[c])
              for a, b, c in final_tris)
volume  = abs(total)

# ── Report ────────────────────────────────────────────────────────────────────
print(f"Vertices : {len(verts):,}")
print(f"Triangles: {len(final_tris):,}")
print(f"")
print(f"Signed sum : {total:+.4f} m³")
print(f"Volume     : {volume:.4f} m³")
print(f"")
# IRigidbodyAsset C# default: m_DensityOrMass = 10f, m_UseAsMass = false
# SP_Mat_Panel_Ext sets its own value (asset binary not extracted yet).
print(f"{'Density':>12}  {'Mass':>12}")
print(f"{'-'*12}  {'-'*12}")
for density in [5.0, 10.0, 15.0, 25.0, 50.0]:
    mass = volume * density
    print(f"{density:>10.1f}  {mass:>12.1f} kg")
