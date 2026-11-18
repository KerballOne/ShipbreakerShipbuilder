"""Cut Point Seat  (sidebar > ShipBreaker > Cut Point Seat)

Select two mesh parts that meet at a seam (the ACTIVE one is "part A"), put the 3D cursor near the spot on the seam
where the cut point should go, pick the options, Preview, then Apply.

What it builds
  * a flat attachment pad ("seat") on one or both parts: a flat top in the seat plane plus skirt walls back down to the
    part's own surface, so a box-shaped stock cut point has a clean face to sit on;
  * optionally a placeholder box of the cut point's size in the seat (collection "Cut Point Seats"), so the pose can be
    read off / exported for placing the stock part in Unity.

Seat types
  * CO-PLANAR  : one plane across both parts; the cut point lies flat on it, straddling the seam.
  * ORTHOGONAL : two planes at 90 degrees; the cut point's bottom sits on one part, one long side butts against the other.

Pads
  * BOTH parts : the seat plane(s) are lifted clear of the parts and a pad is built on each.
  * ONE part   : the other part is left untouched; its own flat face near the cursor is used as that part's seat plane
                 and only the chosen part gets a pad built up to meet the cut point.

No jointing or collision checks are made here: that is verified later in Unity / in game.
Geometry is read from the mesh data in world space (modifiers are not applied).
"""
import math

import bpy
import bmesh
import numpy as np
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

bl_info = {
    "name": "Cut Point Seat",
    "author": "KerballOne",
    "version": (1, 0),
    "blender": (4, 0, 0),
    "category": "Object",
    "description": "Build attachment pads (seats) for a stock cut point between two selected parts",
}

_LOG = r"C:\Users\user\source\repos\ShipbreakerShipbuilder\blender_debug.log"
_NS_DRAW = 'cpseat_debug_draw'
_NS_HANDLER = 'cpseat_debug_handler'
_NS_RESULT = 'cpseat_last_result'
_COLLECTION = "Cut Point Seats"


def _log(msg):
    try:
        with open(_LOG, 'a') as f:
            f.write("[cutpoint_seat] " + msg + "\n")
    except Exception:
        pass


# --------------------------------------------------------------------------------------------------------------------
# presets: (label, length, width, thickness) in metres.  length runs along the seam.
_PRESETS = {
    'GENERIC':  ("Generic 1xBxB  (1.0 x 0.5 x 0.5)", 1.0, 0.5, 0.5),
    'SHIPKIT':  ("ShipKit Ax1CxB  (1.75 x 0.5 x 0.25)", 1.75, 0.5, 0.25),
    'JAVELIN':  ("Javelin 2xAxC  (0.375 x 0.25 x 0.125)", 0.375, 0.25, 0.125),
    'BAYONET':  ("Bayonet Straight  (1.65 x 0.375 x 1.03)", 1.65, 0.375, 1.03),
    'BRACKET':  ("Ring Bracket AxAxB  (0.5 x 0.25 x 0.25)", 0.5, 0.25, 0.25),
    'CUSTOM':   ("Custom size", None, None, None),
}


def _preset_update(self, context):
    p = _PRESETS.get(self.preset)
    if p and p[1] is not None:
        self.length, self.width, self.thickness = p[1], p[2], p[3]


class CutPointSeatSettings(bpy.types.PropertyGroup):
    seat_type: bpy.props.EnumProperty(
        name="Seat type",
        items=[('COPLANAR', "Co-planar", "One plane across both parts; the cut point lies flat on it, straddling the seam"),
               ('ORTHOGONAL', "Orthogonal", "Two planes at 90 degrees: the cut point's bottom sits on one part and one long side butts against the other")],
        default='COPLANAR')
    pads: bpy.props.EnumProperty(
        name="Pads on",
        items=[('BOTH', "Both parts", "Build a pad on each part; the seat is lifted clear of both"),
               ('ACTIVE', "Active part only", "Only the active part (A) gets a pad; the other part is left untouched and its own flat face is used"),
               ('OTHER', "Other part only", "Only the other part (B) gets a pad; the active part is left untouched and its own flat face is used")],
        default='BOTH')
    bottom_part: bpy.props.EnumProperty(
        name="Bottom rests on",
        items=[('ACTIVE', "Active part", "Orthogonal seat: the cut point's bottom sits on the active part, its side butts against the other"),
               ('OTHER', "Other part", "Orthogonal seat: the cut point's bottom sits on the other part, its side butts against the active part")],
        default='ACTIVE')
    preset: bpy.props.EnumProperty(name="Cut point", items=[(k, v[0], "") for k, v in _PRESETS.items()], default='GENERIC', update=_preset_update)
    length: bpy.props.FloatProperty(name="Length (along seam)", default=1.0, min=0.02, soft_max=4.0, unit='LENGTH', precision=3)
    width: bpy.props.FloatProperty(name="Width", default=0.5, min=0.02, soft_max=2.0, unit='LENGTH', precision=3)
    thickness: bpy.props.FloatProperty(name="Thickness (off the seat)", default=0.5, min=0.02, soft_max=2.0, unit='LENGTH', precision=3)

    location: bpy.props.EnumProperty(
        name="Where",
        items=[('CURSOR', "Nearest the 3D cursor", "Seat at the point of the seam closest to the 3D cursor"),
               ('MIDDLE', "Middle of the seam", "Seat at the middle of the contact between the two parts")],
        default='CURSOR')
    search_range: bpy.props.FloatProperty(name="Search along seam", default=1.0, min=0.0, soft_max=10.0, unit='LENGTH', precision=2,
                                          description="Also try spots up to this far either way along the seam and keep the lowest seat (0 = exactly at the chosen spot)")
    slide_along: bpy.props.FloatProperty(name="Slide along seam", default=0.0, soft_min=-3.0, soft_max=3.0, unit='LENGTH', precision=3)
    slide_across: bpy.props.FloatProperty(name="Slide across seam", default=0.0, soft_min=-1.0, soft_max=1.0, unit='LENGTH', precision=3,
                                          description="Co-planar seat: shift the cut point sideways off the seam line")
    side: bpy.props.EnumProperty(
        name="Side",
        items=[('VIEW', "Side I am looking at", "Build the seat on the side of the parts that faces the 3D viewport's point of view"),
               ('ANY', "Whichever is lowest", "Either side; pick the one needing the lowest pads")],
        default='VIEW')
    flip_side: bpy.props.BoolProperty(name="Flip to the far side", default=False, description="Build on the side facing AWAY from the viewport's point of view")
    auto_angle: bpy.props.BoolProperty(name="Find the lowest seat angle", default=True,
                                       description="Try every rotation of the seat about the seam and keep the one needing the lowest pads (pads on both parts only)")
    angle: bpy.props.FloatProperty(name="Seat angle", default=0.0, min=-math.pi, max=math.pi, subtype='ANGLE',
                                   description="Rotation of the seat about the seam when not searching automatically")
    angle_step: bpy.props.FloatProperty(name="Search step", default=math.radians(5.0), min=math.radians(0.5), max=math.radians(45.0), subtype='ANGLE')
    clearance: bpy.props.EnumProperty(
        name="Clear of",
        items=[('WHOLE', "Whole part", "Lift the seat above every vertex of the part (keeps the cut point outside the part's convex collider)"),
               ('LOCAL', "Nearby surface only", "Lift the seat above the surface near the seat only (lower pads; the cut point may end up inside a concave part's convex collider)")],
        default='WHOLE')
    lift: bpy.props.FloatProperty(name="Minimum pad height", default=0.02, min=0.0, soft_max=0.2, unit='LENGTH', precision=3)
    gap: bpy.props.FloatProperty(name="Gap under cut point", default=0.001, min=0.0, soft_max=0.02, unit='LENGTH', precision=4,
                                 description="Space between the pad face and the cut point (negative overlap is not offered; 0 = touching)")
    margin: bpy.props.FloatProperty(name="Pad margin", default=0.03, min=0.0, soft_max=0.5, unit='LENGTH', precision=3,
                                    description="How far the pad extends beyond the cut point's footprint")
    pad_length_scale: bpy.props.FloatProperty(name="Pad length", default=1.0, min=0.1, max=3.0, subtype='FACTOR',
                                              description="Pad length as a fraction of the cut point's length (1 = full length + margin)")
    side_cover: bpy.props.FloatProperty(name="Side pad height", default=1.0, min=0.1, max=1.5, subtype='FACTOR',
                                        description="Orthogonal seat: how much of the cut point's side the side pad backs (1 = all of it)")
    max_height: bpy.props.FloatProperty(name="Maximum pad height", default=0.6, min=0.01, soft_max=3.0, unit='LENGTH', precision=3,
                                        description="Reject seats whose pad would be taller than this")
    detail: bpy.props.FloatProperty(name="Skirt detail", default=0.125, min=0.02, max=1.0, unit='LENGTH', precision=3,
                                    description="Spacing of the points where the pad's walls follow the part's surface")
    seam_tolerance: bpy.props.FloatProperty(name="Seam tolerance", default=0.02, min=0.001, soft_max=0.5, unit='LENGTH', precision=3,
                                            description="Parts closer than this are treated as touching")
    pad_faces_first: bpy.props.BoolProperty(name="Pad faces first in the mesh", default=True,
                                            description="Put the new pad faces at the start of the face list (the game's joint search meets them first)")
    add_box: bpy.props.BoolProperty(name="Add cut point placeholder box", default=True,
                                    description="Add a box of the cut point's size in the seat, in the '" + _COLLECTION + "' collection")
    show_contacts: bpy.props.BoolProperty(name="Show contact points", default=False)


# --------------------------------------------------------------------------------------------------------------------
# geometry helpers (all in world space)
class _Part:
    def __init__(self, obj):
        self.obj = obj
        me = obj.data
        mw = obj.matrix_world
        self.V = np.array([tuple(mw @ v.co) for v in me.vertices], dtype=float).reshape(-1, 3)
        me.calc_loop_triangles()
        self.T = np.array([tuple(t.vertices) for t in me.loop_triangles], dtype=int).reshape(-1, 3)
        self.bvh = BVHTree.FromPolygons([Vector(v) for v in self.V], [tuple(int(i) for i in t) for t in self.T], all_triangles=True)
        a, b, c = self.V[self.T[:, 0]], self.V[self.T[:, 1]], self.V[self.T[:, 2]]
        n = np.cross(b - a, c - a)
        ar = np.linalg.norm(n, axis=1) / 2
        ok = ar > 1e-9
        self.tn = np.zeros_like(n)
        self.tn[ok] = n[ok] / (2 * ar[ok, None])
        self.ta = ar
        self.tc = (a + b + c) / 3

    def support(self, n, p0=None, radius=None):
        V = self.V
        if p0 is not None and radius is not None:
            m = np.linalg.norm(V - p0, axis=1) < radius
            if m.sum() >= 3:
                V = V[m]
        return float((V @ n).max())

    def face_planes(self, p0, radius, limit=6):
        """flat faces of this part near p0: [(normal, offset, area)], largest first"""
        idx = sorted({h[2] for h in self.bvh.find_nearest_range(Vector(p0), radius) if h[2] is not None})
        m = np.zeros(len(self.T), bool)
        m[idx] = True
        m &= self.ta > 1e-6
        if not m.any():
            return []
        acc = {}
        for n, c, a in zip(self.tn[m], self.tc[m], self.ta[m]):
            k = (tuple(np.round(n, 2)), round(float(np.dot(n, c)) / 0.005))
            e = acc.setdefault(k, [np.zeros(3), 0.0, 0.0])
            e[0] += n * a
            e[1] += float(np.dot(n, c)) * a
            e[2] += a
        out = []
        for e in acc.values():
            n = e[0] / max(np.linalg.norm(e[0]), 1e-12)
            out.append((n, e[1] / e[2], e[2]))
        out.sort(key=lambda r: -r[2])
        return out[:limit]


def _contact_points(A, B, tol):
    pts = []
    for X, Y in ((A, B), (B, A)):
        lo, hi = Y.V.min(0) - tol, Y.V.max(0) + tol
        cand = [X.V[np.all((X.V >= lo) & (X.V <= hi), axis=1)]]
        # also sample along the edges, so long straight edges still give points
        e = np.vstack([X.T[:, [0, 1]], X.T[:, [1, 2]], X.T[:, [2, 0]]])
        p, q = X.V[e[:, 0]], X.V[e[:, 1]]
        ln = np.linalg.norm(q - p, axis=1)
        m = (ln > 0.15) & np.all((np.minimum(p, q) <= hi) & (np.maximum(p, q) >= lo), axis=1)
        for a, b, l in zip(p[m][:4000], q[m][:4000], ln[m][:4000]):
            k = int(min(40, max(2, round(l / 0.1))))
            cand.append(a + (b - a) * np.linspace(0, 1, k + 1)[1:-1, None])
        for v in np.vstack(cand):
            if not np.all((v >= lo) & (v <= hi)):
                continue
            if Y.bvh.find_nearest(Vector(v), tol)[0] is not None:
                pts.append(v)
    if not pts:
        return np.zeros((0, 3))
    return np.unique(np.round(np.asarray(pts), 4), axis=0)


def _pad(part, nrm, off, u_ax, u0, u1, d, l0, l1, step, hmax):
    """flat top in plane (nrm . p = off) over [l0,l1] along d x [u0,u1] along u_ax, skirt back to the part.
    returns (tris, hmin, hmax, centre) or (None, reason)"""
    ls = np.linspace(l0, l1, max(2, int(round((l1 - l0) / step)) + 1))
    us = np.linspace(u0, u1, max(2, int(round((u1 - u0) / step)) + 1))
    dep = {}
    for i, l in enumerate(ls):
        for k, u in enumerate(us):
            p = d * l + u_ax * u + nrm * off
            h = part.bvh.ray_cast(Vector(p), Vector(-nrm), hmax + 0.02)
            if h[0] is None:
                return None, "the pad's footprint runs off " + part.obj.name
            if h[3] > hmax:
                return None, "pad on %s would be %.2f m tall" % (part.obj.name, h[3])
            dep[(i, k)] = h[3]
    P = lambda i, k, h=0.0: d * ls[i] + u_ax * us[k] + nrm * (off - h)
    I, K = len(ls) - 1, len(us) - 1
    per = [(i, 0) for i in range(I)] + [(I, k) for k in range(K)] + [(i, K) for i in range(I, 0, -1)] + [(0, k) for k in range(K, 0, -1)]
    tris = [(P(0, 0), P(I, 0), P(I, K)), (P(0, 0), P(I, K), P(0, K))]
    for q in range(len(per)):
        a_, b_ = per[q], per[(q + 1) % len(per)]
        tris += [(P(*a_), P(*a_, dep[a_]), P(*b_, dep[b_])), (P(*a_), P(*b_, dep[b_]), P(*b_))]
    cen = sum((np.asarray(t).mean(0) for t in tris), np.zeros(3)) / len(tris)
    out = []
    for t in tris:
        t = [np.asarray(x, float) for x in t]
        nn = np.cross(t[1] - t[0], t[2] - t[0])
        if np.linalg.norm(nn) < 1e-10:
            continue                                        # zero-height wall
        if np.dot(nn, np.mean(t, 0) - cen) < 0:
            t = [t[0], t[2], t[1]]
        out.append(t)
    hs = list(dep.values())
    return (out, min(hs), max(hs), d * (l0 + l1) / 2 + u_ax * (u0 + u1) / 2 + nrm * off), "ok"


def _bare_ok(part, nrm, off, u_ax, u0, u1, d, l0, l1):
    """does the untouched part really have its own surface in this plane under the cut point?"""
    hit = 0
    tot = 0
    for l in np.linspace(l0, l1, 5):
        for u in np.linspace(u0, u1, 3):
            tot += 1
            h = part.bvh.ray_cast(Vector(d * l + u_ax * u + nrm * (off + 0.05)), Vector(-nrm), 0.2)
            if h[0] is not None and abs(h[3] - 0.05) < 0.006:
                hit += 1
    return hit, tot


def _seam_dir(C, p0, L):
    near = C[np.linalg.norm(C - p0, axis=1) < max(1.5 * L, 1.0)]
    if len(near) < 2:
        near = C
    w, v = np.linalg.eigh(np.cov((near - near.mean(0)).T))
    if w[-1] < 1e-8:
        return None
    return v[:, -1] / np.linalg.norm(v[:, -1])


def _plan(A, B, s, view_dir, cursor):
    """returns (result dict | None, message). Tries the asked-for spot first, then other spots along the seam
    within the search range, and keeps the lowest seat (nearer spots win ties)."""
    C = _contact_points(A, B, s.seam_tolerance)
    if len(C) < 2:
        return None, "The two parts do not come within %.3f m of each other (raise Seam tolerance?)" % s.seam_tolerance
    target = cursor if s.location == 'CURSOR' else C.mean(0)
    base = C[np.argmin(np.linalg.norm(C - target, axis=1))]
    d_base = _seam_dir(C, base, s.length)
    if d_base is None:
        return None, "Could not find a seam direction at that spot (the parts touch at a single point)"
    step = max(s.length / 2, 0.1)
    offs = [0.0]
    for k in range(1, int(s.search_range / step) + 1):
        offs += [k * step, -k * step]
    best = None
    msg0 = None
    seen = set()
    for off in offs:
        q = base + d_base * off
        i = int(np.argmin(np.linalg.norm(C - q, axis=1)))
        if i in seen or np.linalg.norm(C[i] - q) > step:
            continue
        seen.add(i)
        d0 = _seam_dir(C, C[i], s.length)
        if d0 is None:
            continue
        res, msg = _plan_at(A, B, s, view_dir, C[i], d0)
        if res is None:
            msg0 = msg0 or msg
            continue
        score = res['cost'] + 0.02 * abs(off)
        if best is None or score < best[0]:
            best = (score, res, off)
        if off == 0.0 and res['cost'] <= s.lift + 0.05:
            break                                            # a low seat right where it was asked for: take it
    if best is None:
        return None, (msg0 or "No seat found") + ("" if s.search_range > 0 else " (try a Search range along the seam)")
    res = best[1]
    res['contacts'] = C
    res['moved'] = best[2]
    return res, "ok"


def _plan_at(A, B, s, view_dir, p0, d0):
    L, W, T = s.length, s.width, s.thickness
    PL = L * s.pad_length_scale
    best = None
    why = {}
    radius_local = max(L, W, T) + 0.3

    def sup(part, n):
        return part.support(n, p0, radius_local) if s.clearance == 'LOCAL' else part.support(n)

    def side_ok(n):
        if s.side == 'ANY' or view_dir is None:
            return True
        return float(np.dot(n, view_dir)) * (-1.0 if s.flip_side else 1.0) > 0.05

    def note(msg):
        why[msg] = why.get(msg, 0) + 1

    def consider(cost, res):
        nonlocal best
        if best is None or cost < best[0]:
            best = (cost, res)

    ref = np.cross(d0, [0.0, 0.0, 1.0]) if abs(d0[2]) < 0.9 else np.cross(d0, [1.0, 0.0, 0.0])
    ref /= np.linalg.norm(ref)
    e = np.cross(d0, ref)
    if s.pads == 'BOTH' and not s.auto_angle:
        angles = [s.angle]
    else:
        angles = list(np.arange(0.0, 2 * math.pi, s.angle_step))
    padA = s.pads in ('BOTH', 'ACTIVE')
    padB = s.pads in ('BOTH', 'OTHER')

    def frame(d):
        al = float(np.dot(p0, d)) + s.slide_along
        return al - PL / 2 - s.margin, al + PL / 2 + s.margin, al

    if s.seat_type == 'COPLANAR':
        if s.pads == 'BOTH':
            cands = [(math.cos(a) * ref + math.sin(a) * e, None, d0) for a in angles]
        else:
            bare = B if padA else A
            cands = []
            for n, o, ar in bare.face_planes(p0, radius_local):
                d = d0 - n * np.dot(d0, n)
                if np.linalg.norm(d) > 0.3:
                    cands.append((n, o, d / np.linalg.norm(d)))
            if not cands:
                return None, "%s has no flat face near that spot to use as the seat" % bare.obj.name
        for n, o_face, d in cands:
            if not side_ok(n):
                note("seat would be on the far side (try Flip, or Side: whichever is lowest)")
                continue
            l0, l1, al = frame(d)
            for sg in (1.0, -1.0):
                lat = sg * np.cross(d, n)                               # part A lies on the +lat side
                cl = float(np.dot(p0, lat)) + s.slide_across
                o = (max(sup(A, n), sup(B, n)) + s.lift) if o_face is None else o_face
                specs = ((A, padA, cl + s.gap, cl + W / 2 + s.margin), (B, padB, cl - W / 2 - s.margin, cl - s.gap))
                pads = []
                cost = 0.0
                bad = None
                for part, want, u0, u1 in specs:
                    if want:
                        r, msg = _pad(part, n, o, lat, u0, u1, d, l0, l1, s.detail, s.max_height)
                        if r is None:
                            bad = msg
                            break
                        pads.append((part, r))
                        cost = max(cost, r[2])
                    else:
                        hit, tot = _bare_ok(part, n, o, lat, u0, u1, d, l0, l1)
                        if hit < 0.6 * tot:
                            bad = "%s has no flat face under its half of the cut point" % part.obj.name
                            break
                if bad:
                    note(bad)
                    continue
                centre = d * al + lat * cl + n * (o + s.gap + T / 2)
                consider(cost, dict(kind='COPLANAR', centre=centre, axes=(d, lat, n), half=(L / 2, W / 2, T / 2), pads=pads, cost=cost))
    else:
        bottom, sidep = (A, B) if s.bottom_part == 'ACTIVE' else (B, A)
        pad_bottom = (s.pads == 'BOTH') or (s.pads == 'ACTIVE') == (bottom is A)
        pad_side = (s.pads == 'BOTH') or (s.pads == 'ACTIVE') == (sidep is A)
        cands = []
        if s.pads == 'BOTH':
            for a in angles:
                n1 = math.cos(a) * ref + math.sin(a) * e
                for sg in (1.0, -1.0):
                    cands.append((n1, None, sg * np.cross(d0, n1), None, d0))
        elif not pad_bottom:                                            # bottom part untouched: its face is the seat
            for n1, o1, ar in bottom.face_planes(p0, radius_local):
                d = d0 - n1 * np.dot(d0, n1)
                if np.linalg.norm(d) < 0.3:
                    continue
                d /= np.linalg.norm(d)
                for sg in (1.0, -1.0):
                    cands.append((n1, o1, sg * np.cross(d, n1), None, d))
        else:                                                           # side part untouched: its face is the stop
            for n2, o2, ar in sidep.face_planes(p0, radius_local):
                d = d0 - n2 * np.dot(d0, n2)
                if np.linalg.norm(d) < 0.3:
                    continue
                d /= np.linalg.norm(d)
                for sg in (1.0, -1.0):
                    cands.append((sg * np.cross(d, n2), None, n2, o2, d))
        if not cands:
            return None, "No flat face near that spot to use as the seat on the untouched part"
        SH = T * s.side_cover
        for n1, o1, n2, o2, d in cands:
            if not side_ok(n1 + n2):
                note("seat would be on the far side (try Flip, or Side: whichever is lowest)")
                continue
            l0, l1, al = frame(d)
            if o1 is None:
                o1 = sup(bottom, n1) + s.lift
            if o2 is None:
                o2 = sup(sidep, n2) + s.lift
            pads = []
            cost = 0.0
            bad = None
            for part, want, nrm, off, u_ax, u0, u1 in ((bottom, pad_bottom, n1, o1, n2, o2 + s.gap, o2 + s.gap + W + s.margin),
                                                       (sidep, pad_side, n2, o2, n1, o1 + s.gap, o1 + s.gap + SH + s.margin)):
                if want:
                    r, msg = _pad(part, nrm, off, u_ax, u0, u1, d, l0, l1, s.detail, s.max_height)
                    if r is None:
                        bad = msg
                        break
                    pads.append((part, r))
                    cost = max(cost, r[2])
                else:
                    hit, tot = _bare_ok(part, nrm, off, u_ax, u0, u1, d, l0, l1)
                    if hit < 0.5 * tot:
                        bad = "%s has no flat face where the cut point would touch it" % part.obj.name
                        break
            if bad:
                note(bad)
                continue
            centre = d * al + n1 * (o1 + s.gap + T / 2) + n2 * (o2 + s.gap + W / 2)
            consider(cost, dict(kind='ORTHOGONAL', centre=centre, axes=(d, n2, n1), half=(L / 2, W / 2, T / 2), pads=pads, cost=cost))
    if best is None:
        top = sorted(why.items(), key=lambda kv: -kv[1])[:2]
        return None, "No seat found here: " + "; ".join(k for k, _ in top) if top else "No seat found here"
    res = best[1]
    res['p0'] = p0
    return res, "ok"


# --------------------------------------------------------------------------------------------------------------------
# viewport preview
def _draw_callback():
    import gpu
    from gpu_extras.batch import batch_for_shader
    data = bpy.app.driver_namespace.get(_NS_DRAW)
    if not data:
        return
    shader = gpu.shader.from_builtin('UNIFORM_COLOR')

    def lines(key, color, width):
        pts = data.get(key)
        if not pts:
            return
        batch = batch_for_shader(shader, 'LINES', {"pos": pts})
        gpu.state.line_width_set(width)
        shader.bind()
        shader.uniform_float("color", color)
        batch.draw(shader)
        gpu.state.line_width_set(1.0)

    gpu.state.depth_test_set('NONE')
    lines('pad_a', (0.2, 0.8, 1.0, 1.0), 2.0)
    lines('pad_b', (1.0, 0.45, 0.45, 1.0), 2.0)
    lines('box', (1.0, 0.7, 0.1, 1.0), 3.0)
    pts = data.get('contacts')
    if pts:
        batch = batch_for_shader(shader, 'POINTS', {"pos": pts})
        gpu.state.point_size_set(5.0)
        shader.bind()
        shader.uniform_float("color", (1.0, 1.0, 0.2, 1.0))
        batch.draw(shader)


def _install_draw_handler():
    ns = bpy.app.driver_namespace
    if not ns.get(_NS_HANDLER):
        ns[_NS_HANDLER] = bpy.types.SpaceView3D.draw_handler_add(_draw_callback, (), 'WINDOW', 'POST_VIEW')


def _clear_preview():
    bpy.app.driver_namespace[_NS_DRAW] = {}
    bpy.app.driver_namespace[_NS_RESULT] = None
    for w in bpy.context.window_manager.windows if bpy.context.window_manager else []:
        for a in w.screen.areas:
            if a.type == 'VIEW_3D':
                a.tag_redraw()


def _box_corners(res):
    c = res['centre']
    ax = res['axes']
    h = res['half']
    return [c + sx * h[0] * ax[0] + sy * h[1] * ax[1] + sz * h[2] * ax[2] for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]


def _set_preview(res, A, show_contacts):
    cs = _box_corners(res)
    edges = [(0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7)]
    data = {'box': [tuple(cs[i]) for e in edges for i in e], 'pad_a': [], 'pad_b': []}
    for part, r in res['pads']:
        key = 'pad_a' if part is A else 'pad_b'
        for t in r[0]:
            for i in range(3):
                data[key] += [tuple(t[i]), tuple(t[(i + 1) % 3])]
    if show_contacts:
        data['contacts'] = [tuple(p) for p in res['contacts'][:4000]]
    bpy.app.driver_namespace[_NS_DRAW] = data


# --------------------------------------------------------------------------------------------------------------------
def _view_dir(context):
    """unit vector pointing from the scene toward the viewer, or None when there is no 3D viewport (background mode)"""
    rd = getattr(context, 'region_data', None)
    if rd is None:
        for a in (context.screen.areas if context.screen else []):
            if a.type == 'VIEW_3D':
                rd = a.spaces.active.region_3d
                break
    if rd is None:
        return None
    v = rd.view_rotation @ Vector((0.0, 0.0, 1.0))
    return np.array(v.normalized())


def _two_parts(context):
    sel = [o for o in context.selected_objects if o.type == 'MESH']
    act = context.active_object
    if len(sel) != 2 or act not in sel:
        return None, None
    return act, [o for o in sel if o is not act][0]


def _run(context):
    a, b = _two_parts(context)
    if a is None:
        return None, None, None, "Select exactly two mesh objects (the active one is part A)"
    if context.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    s = context.scene.cutpoint_seat
    A, B = _Part(a), _Part(b)
    res, msg = _plan(A, B, s, _view_dir(context), np.array(context.scene.cursor.location))
    return res, A, B, msg


def _summary(res):
    bits = ["%s seat" % ("co-planar" if res['kind'] == 'COPLANAR' else "orthogonal")]
    for part, r in res['pads']:
        bits.append("pad on %s %.0f-%.0f mm tall" % (part.obj.name, 1000 * r[1], 1000 * r[2]))
    if not res['pads']:
        bits.append("no pads needed")
    if abs(res.get('moved', 0.0)) > 1e-6:
        bits.append("moved %.2f m along the seam to a lower spot" % res['moved'])
    return ", ".join(bits)


class OBJECT_OT_cutpoint_seat_preview(bpy.types.Operator):
    bl_idname = "object.cutpoint_seat_preview"
    bl_label = "Preview Cut Point Seat"
    bl_description = "Show where the cut point and its pad(s) would go, without changing anything"
    bl_options = {'REGISTER'}

    def execute(self, context):
        res, A, B, msg = _run(context)
        if res is None:
            _clear_preview()
            self.report({'WARNING'}, msg)
            _log("preview: " + msg)
            return {'CANCELLED'}
        _install_draw_handler()
        _set_preview(res, A, context.scene.cutpoint_seat.show_contacts)
        for a in (context.screen.areas if context.screen else []):
            if a.type == 'VIEW_3D':
                a.tag_redraw()
        self.report({'INFO'}, _summary(res))
        _log("preview: " + _summary(res))
        return {'FINISHED'}


class OBJECT_OT_cutpoint_seat_clear(bpy.types.Operator):
    bl_idname = "object.cutpoint_seat_clear"
    bl_label = "Clear Cut Point Seat Preview"
    bl_options = {'REGISTER'}

    def execute(self, context):
        _clear_preview()
        return {'FINISHED'}


def _add_pad(part, tris, faces_first):
    obj = part.obj
    me = obj.data
    inv = obj.matrix_world.inverted()
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    cen = sum((np.mean(t, axis=0) for t in tris), np.zeros(3)) / len(tris)
    src = None
    if len(bm.faces):
        ok, loc, nrm, idx = obj.closest_point_on_mesh(inv @ Vector(cen))
        if ok and 0 <= idx < len(bm.faces):
            src = bm.faces[idx]
    uvs = [(lay, src.loops[0][lay].uv.copy()) for lay in bm.loops.layers.uv.values()] if src else []
    for f in bm.faces:
        f.tag = False
    new_verts = []
    for t in tris:
        vs = [bm.verts.new(inv @ Vector(p)) for p in t]
        new_verts += vs
        try:
            f = bm.faces.new(vs)
        except ValueError:
            continue
        f.tag = True
        f.smooth = False
        if src:
            f.material_index = src.material_index
            for lp in f.loops:
                for lay, uv in uvs:
                    lp[lay].uv = uv
    bmesh.ops.remove_doubles(bm, verts=[v for v in new_verts if v.is_valid], dist=1e-5)
    n_new = sum(1 for f in bm.faces if f.tag)
    if faces_first:
        bm.faces.sort(key=lambda f: 0 if f.tag else 1)
    bm.normal_update()
    bm.to_mesh(me)
    bm.free()
    me.update()
    return n_new


def _add_box(context, res, name):
    d, w, t = res['axes']
    h = res['half']
    if np.linalg.det(np.column_stack([d, w, t])) < 0:
        d = -d
    me = bpy.data.meshes.new(name)
    vs = [(sx * h[0], sy * h[1], sz * h[2]) for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)]
    fs = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
    me.from_pydata(vs, [], fs)
    me.update()
    ob = bpy.data.objects.new(name, me)
    m = Matrix.Identity(4)
    for r in range(3):
        m[r][0], m[r][1], m[r][2], m[r][3] = d[r], w[r], t[r], res['centre'][r]
    ob.matrix_world = m
    ob.display_type = 'WIRE'
    ob.show_in_front = True
    ob["cutpoint_size_LWT"] = [2 * h[0], 2 * h[1], 2 * h[2]]
    col = bpy.data.collections.get(_COLLECTION)
    if col is None:
        col = bpy.data.collections.new(_COLLECTION)
        context.scene.collection.children.link(col)
    col.objects.link(ob)
    return ob


class OBJECT_OT_cutpoint_seat_apply(bpy.types.Operator):
    bl_idname = "object.cutpoint_seat_apply"
    bl_label = "Build Cut Point Seat"
    bl_description = "Add the pad(s) to the selected parts' meshes (and the placeholder box, if enabled)"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        res, A, B, msg = _run(context)
        if res is None:
            self.report({'WARNING'}, msg)
            _log("apply: " + msg)
            return {'CANCELLED'}
        s = context.scene.cutpoint_seat
        made = []
        for part, r in res['pads']:
            n = _add_pad(part, r[0], s.pad_faces_first)
            made.append("%s +%d faces" % (part.obj.name, n))
        if s.add_box:
            ob = _add_box(context, res, "CutPoint_%s__%s" % (A.obj.name, B.obj.name))
            made.append("box '%s'" % ob.name)
        _clear_preview()
        text = _summary(res) + " | " + ", ".join(made)
        self.report({'INFO'}, text)
        _log("apply: " + text)
        return {'FINISHED'}


class VIEW3D_PT_cutpoint_seat(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'ShipBreaker'
    bl_label = 'Cut Point Seat'
    bl_options = {'DEFAULT_CLOSED'}

    def draw(self, context):
        layout = self.layout
        s = context.scene.cutpoint_seat
        a, b = _two_parts(context)
        box = layout.box()
        if a is None:
            box.label(text="Select two mesh parts", icon='INFO')
        else:
            box.label(text="A (active): " + a.name, icon='OBJECT_DATA')
            box.label(text="B (other):  " + b.name, icon='OBJECT_DATA')

        col = layout.column(align=True)
        col.label(text="Cut point")
        col.prop(s, "preset", text="")
        col.prop(s, "length")
        col.prop(s, "width")
        col.prop(s, "thickness")

        col = layout.column(align=True)
        col.label(text="Seat")
        col.prop(s, "seat_type", expand=True)
        col.prop(s, "pads", text="Pads on")
        if s.seat_type == 'ORTHOGONAL':
            col.prop(s, "bottom_part")
            col.prop(s, "side_cover", slider=True)

        col = layout.column(align=True)
        col.label(text="Position")
        col.prop(s, "location", text="")
        col.prop(s, "search_range")
        col.prop(s, "slide_along")
        if s.seat_type == 'COPLANAR':
            col.prop(s, "slide_across")
        col.prop(s, "side", text="")
        if s.side == 'VIEW':
            col.prop(s, "flip_side")
        if s.pads == 'BOTH':
            col.prop(s, "auto_angle")
            col.prop(s, "angle_step" if s.auto_angle else "angle")

        col = layout.column(align=True)
        col.label(text="Pad size")
        col.prop(s, "clearance", text="Clear of")
        col.prop(s, "lift")
        col.prop(s, "margin")
        col.prop(s, "pad_length_scale", slider=True)
        col.prop(s, "max_height")
        col.prop(s, "gap")

        col = layout.column(align=True)
        col.label(text="Advanced")
        col.prop(s, "detail")
        col.prop(s, "seam_tolerance")
        col.prop(s, "pad_faces_first")
        col.prop(s, "add_box")
        col.prop(s, "show_contacts")

        layout.separator()
        row = layout.row(align=True)
        row.operator(OBJECT_OT_cutpoint_seat_preview.bl_idname, text="Preview", icon='HIDE_OFF')
        row.operator(OBJECT_OT_cutpoint_seat_clear.bl_idname, text="", icon='X')
        layout.operator(OBJECT_OT_cutpoint_seat_apply.bl_idname, text="Build Seat", icon='MOD_BUILD')


_CLASSES = (
    CutPointSeatSettings,
    OBJECT_OT_cutpoint_seat_preview,
    OBJECT_OT_cutpoint_seat_clear,
    OBJECT_OT_cutpoint_seat_apply,
    VIEW3D_PT_cutpoint_seat,
)


def register():
    for cls in _CLASSES:
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)
    bpy.types.Scene.cutpoint_seat = bpy.props.PointerProperty(type=CutPointSeatSettings)
    if not bpy.app.background:
        _install_draw_handler()


def unregister():
    bpy.app.driver_namespace[_NS_DRAW] = {}
    for cls in _CLASSES:
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
    if hasattr(bpy.types.Scene, "cutpoint_seat"):
        del bpy.types.Scene.cutpoint_seat


if __name__ == "__main__":
    register()
