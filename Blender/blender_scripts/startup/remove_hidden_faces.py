import math
import random

import bpy
import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree

bl_info = {
    "name": "Remove Hidden Faces",
    "author": "KerballOne",
    "version": (1, 0),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Select or delete faces of the active mesh that can never be seen (stacked internal layers)",
}

# How a face is judged
# --------------------
# From sample points on the face, rays go out over the hemisphere on the face's FRONT side (the game draws front
# sides only). A ray is 'open' when it leaves the part without meeting a surface that would be drawn in front of
# the face for a viewer looking back along that ray. A surface that such a viewer sees from behind is culled by
# the renderer, so it does not hide anything and the ray carries on through it.
# openness = share of open rays (cosine weighted). A face is kept when any of its sample points reaches the
# threshold. Sheets stacked a few millimetres apart only see out through the slit at their rim, which is a tiny
# share of the hemisphere, so they fall under the threshold and are reported as hidden.


def _hemisphere(n):
    """n cosine-weighted directions about +Z (Fibonacci spiral)."""
    out = []
    g = math.pi * (3.0 - math.sqrt(5.0))
    for i in range(n):
        u = (i + 0.5) / n
        r = math.sqrt(u)
        z = math.sqrt(max(0.0, 1.0 - u))
        a = i * g
        out.append((r * math.cos(a), r * math.sin(a), z))
    return out


def _frame(nrm):
    t = Vector((1.0, 0.0, 0.0)) if abs(nrm.x) < 0.9 else Vector((0.0, 1.0, 0.0))
    u = nrm.cross(t).normalized()
    v = nrm.cross(u)
    return u, v


def find_hidden_faces(tris, tri_face, n_faces, occluder_tris=(), threshold=0.04, rays=48, front_only=True,
                      cull_aware=True, sample_area=0.02, max_samples=12, progress=None):
    """tris: list of 3 world-space Vectors per triangle; tri_face: owning face index per triangle.
    Returns (hidden face indices, openness per face)."""
    verts = []
    polys = []
    for t in list(tris) + list(occluder_tris):
        k = len(verts)
        verts.extend(t)
        polys.append((k, k + 1, k + 2))
    bvh = BVHTree.FromPolygons(verts, polys, all_triangles=True)
    lo = Vector((min(v.x for v in verts), min(v.y for v in verts), min(v.z for v in verts)))
    hi = Vector((max(v.x for v in verts), max(v.y for v in verts), max(v.z for v in verts)))
    far = (hi - lo).length * 1.05 + 0.01
    eps = max(1e-5, far * 2e-5)
    dirs = _hemisphere(rays)
    need = threshold * rays
    rng = random.Random(7)
    best = [0.0] * n_faces
    cast = bvh.ray_cast

    def openness(p, nrm):
        u, v = _frame(nrm)
        o = p + nrm * eps
        got = 0
        for i, (dx, dy, dz) in enumerate(dirs):
            d = u * dx + v * dy + nrm * dz
            q = o
            left = far
            blocked = False
            for _ in range(24):
                loc, hn, idx, dist = cast(q, d, left)
                if loc is None:
                    break
                if not cull_aware or hn.dot(d) > 1e-6:
                    blocked = True
                    break
                left -= dist + eps
                if left <= 0:
                    break
                q = loc + d * eps
            if not blocked:
                got += 1
                if got >= need:
                    return got / rays, True
            elif got + (rays - i - 1) < need:
                return got / rays, False
        return got / rays, got >= need

    n = len(tris)
    for ti in range(n):
        f = tri_face[ti]
        if best[f] >= threshold:
            continue
        a, b, c = tris[ti]
        cr = (b - a).cross(c - a)
        area = cr.length / 2.0
        if area < 1e-12:
            continue
        nrm = cr / (2.0 * area)
        pts = [(a + b + c) / 3.0, a * 0.8 + (b + c) * 0.1, b * 0.8 + (a + c) * 0.1, c * 0.8 + (a + b) * 0.1]
        extra = min(max_samples, int(area / sample_area)) - len(pts)
        for _ in range(max(0, extra)):
            r1 = math.sqrt(rng.random())
            r2 = rng.random()
            pts.append(a * (1 - r1) + b * (r1 * (1 - r2)) + c * (r1 * r2))
        for p in pts:
            sides = (nrm,) if front_only else (nrm, -nrm)
            done = False
            for s in sides:
                val, ok = openness(p, s)
                if val > best[f]:
                    best[f] = val
                if ok:
                    best[f] = max(best[f], threshold)
                    done = True
                    break
            if done:
                break
        if progress and ti % 2000 == 0:
            progress(ti / n)
    return [i for i in range(n_faces) if best[i] < threshold], best


# Capping open rims
# -----------------
# A part cut out of a layered model shows the edges of all its layers on the cut: many open edges side by side in
# one flat plane. find_rim_caps() takes the open edges, groups them by the plane they lie in, and in each plane
# fills every slit between edges that is narrower than `gap` (a morphological closing on a fine grid, traced back
# to a polygon and triangulated). Openings wider than `gap` stay open. A lone sheet edge gets no cap. The cap sits
# one grid cell inside the outermost layer edge so nothing sticks out past the outside surface.


def _open_edges(tris, weld):
    import numpy as np
    key = {}
    pos = []
    def vid(v):
        k = (round(v.x / weld), round(v.y / weld), round(v.z / weld))
        i = key.get(k)
        if i is None:
            i = key[k] = len(pos)
            pos.append((v.x, v.y, v.z))
        return i
    bal = {}
    for t in tris:
        ids = [vid(v) for v in t]
        for e in range(3):
            a, b = ids[e], ids[(e + 1) % 3]
            if a == b:
                continue
            if a < b:
                bal[(a, b)] = bal.get((a, b), 0) + 1
            else:
                bal[(b, a)] = bal.get((b, a), 0) - 1
    P = np.array(pos, dtype=float)
    E = np.array([k for k, v in bal.items() if v != 0], dtype=int).reshape(-1, 2)
    return P, E


def _rim_planes(P, E, tol, min_len, max_planes=16, iters=600):
    import numpy as np
    rng = np.random.default_rng(11)
    A, B = P[E[:, 0]], P[E[:, 1]]
    L = np.linalg.norm(B - A, axis=1)
    left = np.ones(len(E), bool)
    out = []
    for _ in range(max_planes):
        idx = np.nonzero(left)[0]
        if len(idx) < 3 or L[idx].sum() < min_len:
            break
        w = L[idx] / L[idx].sum()
        best = (0.0, None)
        for _i in range(iters):
            i, j = rng.choice(idx, 2, p=w)
            d1 = B[i] - A[i]
            n = np.cross(d1, B[j] - A[j])
            if np.linalg.norm(n) < 0.2 * np.linalg.norm(d1) * L[j]:
                n = np.cross(d1, (A[j] + B[j]) / 2 - A[i])
            ln = np.linalg.norm(n)
            if ln < 1e-9:
                continue
            n = n / ln
            o = n @ A[i]
            ok = (np.abs(A[idx] @ n - o) < tol) & (np.abs(B[idx] @ n - o) < tol)
            s = L[idx][ok].sum()
            if s > best[0]:
                best = (s, idx[ok])
        if best[1] is None or best[0] < min_len:
            break
        ins = best[1]
        pts = np.vstack([A[ins], B[ins]])
        c = pts.mean(0)
        n = np.linalg.svd(pts - c)[2][2]
        ok = (np.abs((A[idx] - c) @ n) < tol) & (np.abs((B[idx] - c) @ n) < tol)
        ins = idx[ok]
        out.append((c, n, ins))
        left[ins] = False
    return out


def _closing_loops(seg2d, gap, cell):
    """seg2d: (n, 2, 2) rim edges in plane coordinates -> list of closed outlines (each an (m, 2) array)."""
    import numpy as np
    r = max(1, int(round(gap / 2 / cell)))
    lo = seg2d.reshape(-1, 2).min(0) - (r + 3) * cell
    hi = seg2d.reshape(-1, 2).max(0) + (r + 3) * cell
    W, H = (np.ceil((hi - lo) / cell).astype(int) + 1)
    if W * H > 60_000_000:
        return []
    M = np.zeros((H, W), bool)
    for a, b in seg2d:
        n = max(2, int(np.linalg.norm(b - a) / (cell * 0.5)) + 1)
        q = a + (b - a) * np.linspace(0, 1, n)[:, None]
        ij = np.floor((q - lo) / cell).astype(int)
        M[ij[:, 1], ij[:, 0]] = True

    def slide(X, k, func):
        Y = X.copy()
        for s in range(1, k + 1):
            Y[:, s:] = func(Y[:, s:], X[:, :-s]); Y[:, :-s] = func(Y[:, :-s], X[:, s:])
        Z = Y.copy()
        for s in range(1, k + 1):
            Z[s:, :] = func(Z[s:, :], Y[:-s, :]); Z[:-s, :] = func(Z[:-s, :], Y[s:, :])
        return Z
    D = slide(M, r, np.logical_or)
    C = slide(D, r + 2, np.logical_and)          # two cells more than the dilation: the cap stays inside the outer edge
    if not C.any():
        return []
    Cp = np.pad(C, 1)
    nxt = {}
    def add(mask, a_off, b_off):
        ys, xs = np.nonzero(mask)
        for y, x in zip(ys.tolist(), xs.tolist()):
            nxt.setdefault((x + a_off[0], y + a_off[1]), []).append((x + b_off[0], y + b_off[1]))
    core = Cp[1:-1, 1:-1]
    add(core & ~Cp[:-2, 1:-1], (0, 0), (1, 0))       # empty below: bottom side, left -> right
    add(core & ~Cp[1:-1, 2:], (1, 0), (1, 1))        # empty right: right side, up
    add(core & ~Cp[2:, 1:-1], (1, 1), (0, 1))        # empty above: top side, right -> left
    add(core & ~Cp[1:-1, :-2], (0, 1), (0, 0))       # empty left: left side, down
    loops = []
    while nxt:
        start = next(iter(nxt))
        cur = start
        loop = [cur]
        while True:
            lst = nxt.get(cur)
            if not lst:
                break
            b = lst.pop()
            if not lst:
                del nxt[cur]
            cur = b
            if cur == start:
                break
            loop.append(cur)
        if len(loop) >= 4 and cur == start:
            loops.append(np.array(loop, float) * cell + lo)
    return loops


def _simplify(loop, tol):
    import numpy as np
    def dp(pts):
        if len(pts) < 3:
            return pts
        a, b = pts[0], pts[-1]
        d = b - a
        ld = np.linalg.norm(d)
        if ld < 1e-12:
            dist = np.linalg.norm(pts - a, axis=1)
        else:
            dist = np.abs(d[0] * (pts[:, 1] - a[1]) - d[1] * (pts[:, 0] - a[0])) / ld
        i = int(dist.argmax())
        if dist[i] <= tol:
            return np.array([a, b])
        return np.vstack([dp(pts[:i + 1])[:-1], dp(pts[i:])])
    n = len(loop)
    k = int(np.linalg.norm(loop - loop[0], axis=1).argmax())
    a = dp(np.vstack([loop[:k + 1]]))
    b = dp(np.vstack([loop[k:], loop[:1]]))
    out = np.vstack([a[:-1], b[:-1]])
    return out if len(out) >= 3 else None


def _bridge_free_edges(P, E, idx, gap, step=0.02):
    """Open edges (rows idx of E) that run alongside another open edge closer than `gap` get a strip between them.
    Strips are returned double sided; the hidden-face pass removes the side that cannot be seen."""
    import numpy as np
    from mathutils import kdtree
    if len(idx) < 2:
        return []
    A, B = P[E[idx, 0]], P[E[idx, 1]]
    L = np.linalg.norm(B - A, axis=1)
    ok = L > 1e-6
    idx, A, B, L = idx[ok], A[ok], B[ok], L[ok]
    Dn = (B - A) / L[:, None]
    pts = []; own = []; par = []
    for i in range(len(idx)):
        n = max(2, int(L[i] / step) + 1)
        for t in np.linspace(0.0, 1.0, n):
            pts.append(A[i] + (B[i] - A[i]) * t); own.append(i); par.append(t)
    pts = np.array(pts); own = np.array(own); par = np.array(par)
    kd = kdtree.KDTree(len(pts))
    for k, q in enumerate(pts):
        kd.insert(q, k)
    kd.balance()
    vset = [set((int(E[idx[i], 0]), int(E[idx[i], 1]))) for i in range(len(idx))]
    match = -np.ones(len(pts), int)
    for k, q in enumerate(pts):
        i = own[k]; best = None
        for co, k2, dist in kd.find_range(q, gap):
            j = own[k2]
            if j == i or dist < 1e-3 or (best is not None and dist >= best[0]):
                continue
            if abs(Dn[i] @ Dn[j]) < 0.9 or (vset[i] & vset[j]):
                continue
            if abs((pts[k2] - q) @ Dn[i]) > 0.5 * dist:
                continue
            best = (dist, j)
        if best:
            match[k] = best[1]
    out = []; done = set()
    for i in range(len(idx)):
        ks = np.nonzero(own == i)[0]
        js = match[ks]
        start = 0
        while start < len(ks):
            j = js[start]; end = start
            while end + 1 < len(ks) and js[end + 1] == j:
                end += 1
            if j >= 0 and end > start and (min(i, j), max(i, j)) not in done:
                done.add((min(i, j), max(i, j)))
                p0, p1 = pts[ks[start]], pts[ks[end]]
                def proj(q):
                    t = np.clip((q - A[j]) @ Dn[j], 0.0, L[j]); return A[j] + Dn[j] * t
                q0, q1 = proj(p0), proj(p1)
                if np.linalg.norm(q1 - q0) > 1e-4:
                    for a_, b_, c_ in ((p0, p1, q1), (p0, q1, q0), (p0, q1, p1), (p0, q0, q1)):
                        out.append([Vector(a_), Vector(b_), Vector(c_)])
            start = end + 1
    return out


def find_rim_caps(tris, gap=0.08, cell=0.004, weld=0.001, min_len=0.5, plane_tol=0.002, min_area=0.004, bridge=True, extrude=True):
    """tris: list of 3 world-space Vectors per triangle. Returns (cap triangles as lists of 3 Vectors, info list)."""
    import numpy as np
    P, E = _open_edges(tris, weld)
    info = []
    caps = []
    if len(E) < 3:
        return caps, info
    allv = np.array([(v.x, v.y, v.z) for t in tris for v in t])
    used = np.zeros(len(E), bool)
    plane_data = []
    for c, n, ins in _rim_planes(P, E, plane_tol, min_len):
        u = np.cross(n, [1.0, 0, 0] if abs(n[0]) < 0.9 else [0, 1.0, 0])
        u /= np.linalg.norm(u)
        v = np.cross(n, u)
        A, B = P[E[ins, 0]], P[E[ins, 1]]
        seg = np.stack([np.stack([(A - c) @ u, (A - c) @ v], 1), np.stack([(B - c) @ u, (B - c) @ v], 1)], 1)
        lo3 = np.minimum(A, B).min(0) - 0.3
        hi3 = np.maximum(A, B).max(0) + 0.3
        near = allv[np.all((allv >= lo3) & (allv <= hi3), axis=1)]
        sd = (near - c) @ n
        pos, neg = int((sd > 0.005).sum()), int((sd < -0.005).sum())
        rec = {"centre": c.round(3).tolist(), "normal": n.round(3).tolist(), "edges": int(len(ins)),
               "length": float(np.linalg.norm(B - A, axis=1).sum()), "tris": 0, "area": 0.0, "skipped": ""}
        info.append(rec)
        if min(pos, neg) > 0.25 * max(pos, neg, 1):
            rec["skipped"] = "geometry on both sides of the plane (not a cut rim)"
            continue
        out_n = -n if pos > neg else n
        loops = [x for x in (_simplify(l, cell * 1.5) for l in _closing_loops(seg, gap, cell)) if x is not None]
        def _area(q):
            return abs(float((q[:, 0] * np.roll(q[:, 1], -1) - np.roll(q[:, 0], -1) * q[:, 1]).sum()) / 2)
        rec["loops"] = len(loops)
        loops = [q for q in loops if _area(q) >= min_area]          # specks go, pinholes get filled
        rec["loops_kept"] = len(loops)
        rec["outline_pts"] = int(sum(len(q) for q in loops))
        if not loops:
            rec["skipped"] = "no slits narrower than the gap"
            continue
        plane_data.append((c, n, u, v, loops, rec))
        bm = bmesh.new()
        for lp in loops:
            vs = [bm.verts.new(Vector(c + u * p[0] + v * p[1])) for p in lp]
            for i in range(len(vs)):
                bm.edges.new((vs[i], vs[(i + 1) % len(vs)]))
        res = bmesh.ops.triangle_fill(bm, use_beauty=True, use_dissolve=False, edges=bm.edges[:])
        faces = [f for f in res["geom"] if isinstance(f, bmesh.types.BMFace)]
        on = Vector(out_n)
        for f in faces:
            pts = [Vector(l.vert.co) for l in f.loops]
            if len(pts) != 3:
                continue
            if (pts[1] - pts[0]).cross(pts[2] - pts[0]).dot(on) < 0:
                pts = [pts[0], pts[2], pts[1]]
            caps.append(pts)
            rec["tris"] += 1
            rec["area"] += (pts[1] - pts[0]).cross(pts[2] - pts[0]).length / 2
        bm.free()
        used[ins] = True
    if extrude:
        def _ar(q):
            return abs(float((q[:, 0] * np.roll(q[:, 1], -1) - np.roll(q[:, 0], -1) * q[:, 1]).sum()) / 2)
        done = set()
        for i in range(len(plane_data)):
            ci, ni, ui, vi, li, reci = plane_data[i]
            for k in range(i + 1, len(plane_data)):
                ck, nk, uk, vk, lk, reck = plane_data[k]
                if i in done or k in done or abs(ni @ nk) < 0.999:
                    continue
                h = float((ck - ci) @ ni)
                if abs(h) < 0.05:
                    continue
                pk = np.vstack([np.stack([((ck + uk * q[0] + vk * q[1]) - ci) @ ui, ((ck + uk * q[0] + vk * q[1]) - ci) @ vi]) for l in lk for q in l])
                pi = np.vstack(li)
                ai, ak = sum(_ar(q) for q in li), sum(_ar(q) for q in lk)
                same = abs(ai - ak) < 0.15 * max(ai, ak) and np.abs(pi.min(0) - pk.min(0)).max() < 0.05 and np.abs(pi.max(0) - pk.max(0)).max() < 0.05
                if not same:
                    continue
                done.add(i); done.add(k)
                walls = []
                for lp in li:
                    m = len(lp)
                    for q in range(m):
                        a0 = ci + ui * lp[q][0] + vi * lp[q][1]
                        a1 = ci + ui * lp[(q + 1) % m][0] + vi * lp[(q + 1) % m][1]
                        b0, b1 = a0 + ni * h, a1 + ni * h
                        for x, y, z in ((a0, a1, b1), (a0, b1, b0), (a0, b1, a1), (a0, b0, b1)):
                            walls.append([Vector(x), Vector(y), Vector(z)])
                info.append({"centre": ci.round(3).tolist(), "normal": ni.round(3).tolist(), "edges": 0, "length": abs(h), "tris": len(walls),
                             "area": float(sum((t[1] - t[0]).cross(t[2] - t[0]).length / 2 for t in walls)), "skipped": "", "bridge": True, "extrude": True})
                caps += walls
    if bridge:
        br = _bridge_free_edges(P, E, np.nonzero(~used)[0], gap)
        info.append({"centre": [0, 0, 0], "normal": [0, 0, 0], "edges": int((~used).sum()), "length": float(np.linalg.norm(P[E[~used, 1]] - P[E[~used, 0]], axis=1).sum()),
                     "tris": len(br), "area": float(sum((t[1] - t[0]).cross(t[2] - t[0]).length / 2 for t in br)), "skipped": "", "bridge": True})
        caps += br
    return caps, info


def _collect(bm, mw):
    bm.faces.ensure_lookup_table()
    tris = []
    tri_face = []
    for lt in bm.calc_loop_triangles():
        tris.append([mw @ l.vert.co for l in lt])
        tri_face.append(lt[0].face.index)
    return tris, tri_face


def _occluders(context, obj, use):
    out = []
    if not use:
        return out
    dg = context.evaluated_depsgraph_get()
    for o in context.selected_objects:
        if o == obj or o.type != 'MESH':
            continue
        me = o.evaluated_get(dg).to_mesh()
        me.calc_loop_triangles()
        mw = o.matrix_world
        for lt in me.loop_triangles:
            out.append([mw @ me.vertices[i].co for i in lt.vertices])
        o.evaluated_get(dg).to_mesh_clear()
    return out


class MESH_OT_remove_hidden_faces(bpy.types.Operator):
    bl_idname = "mesh.remove_hidden_faces"
    bl_label = "Remove Hidden Faces"
    bl_description = ("Find faces of the active mesh that can never be seen (stacked internal layers) "
                      "and select or delete them")
    bl_options = {'REGISTER', 'UNDO'}

    action: bpy.props.EnumProperty(
        name="Action",
        items=[('SELECT', "Select", "Select the hidden faces in Edit Mode so they can be inspected first"),
               ('DELETE', "Delete", "Delete the hidden faces")],
        default='SELECT',
    )
    threshold: bpy.props.FloatProperty(
        name="Openness Threshold", subtype='PERCENTAGE', min=0.1, max=50.0, default=4.0,
        description="A face is hidden when less than this share of the view directions in front of it are open. "
                    "Raise it to remove more (deep crevices), lower it to keep more",
    )
    rays: bpy.props.IntProperty(
        name="Rays", min=16, max=512, default=48,
        description="View directions tested per sample point. More is slower and more exact",
    )
    front_only: bpy.props.BoolProperty(
        name="Front Sides Only", default=True,
        description="Judge each face by its front (normal) side only, the way the game draws it. "
                    "Turn off if the mesh is drawn double sided",
    )
    cull_aware: bpy.props.BoolProperty(
        name="Back Faces Do Not Hide", default=True,
        description="A surface seen from behind is not drawn, so it does not hide what is behind it. "
                    "Turn off to treat every surface as solid from both sides (removes more)",
    )
    use_selected: bpy.props.BoolProperty(
        name="Other Selected Objects Hide Too", default=False,
        description="Faces covered by the other selected mesh objects also count as hidden. Leave off for parts "
                    "that come apart in game: a face against a neighbour shows once the neighbour is removed",
    )

    @classmethod
    def poll(cls, context):
        o = context.active_object
        return o is not None and o.type == 'MESH' and context.mode in {'OBJECT', 'EDIT_MESH'}

    def execute(self, context):
        obj = context.active_object
        was_edit = context.mode == 'EDIT_MESH'
        if was_edit:
            bpy.ops.object.mode_set(mode='OBJECT')
        me = obj.data
        bm = bmesh.new()
        bm.from_mesh(me)
        tris, tri_face = _collect(bm, obj.matrix_world)
        n_faces = len(bm.faces)
        wm = context.window_manager
        wm.progress_begin(0, 100)
        hidden, _ = find_hidden_faces(tris, tri_face, n_faces, _occluders(context, obj, self.use_selected),
                                      self.threshold / 100.0, self.rays, self.front_only, self.cull_aware,
                                      progress=lambda x: wm.progress_update(int(x * 100)))
        wm.progress_end()
        hs = set(hidden)
        area_all = sum(f.calc_area() for f in bm.faces)
        area_hid = sum(f.calc_area() for f in bm.faces if f.index in hs)
        if self.action == 'DELETE':
            bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.index in hs], context='FACES')
            bm.to_mesh(me)
            me.update()
        else:
            for f in bm.faces:
                f.select = f.index in hs
            bm.select_flush_mode()
            bm.to_mesh(me)
        bm.free()
        if self.action == 'SELECT' or was_edit:
            bpy.ops.object.mode_set(mode='EDIT')
            if self.action == 'SELECT':
                context.tool_settings.mesh_select_mode = (False, False, True)
        self.report({'INFO'}, "%s %d of %d faces hidden (%.0f%% of the surface area)" % (
            "Deleted:" if self.action == 'DELETE' else "Selected:", len(hidden), n_faces,
            100.0 * area_hid / area_all if area_all else 0.0))
        return {'FINISHED'}


class VIEW3D_PT_remove_hidden_faces(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'ShipBreaker'
    bl_label = 'Remove Hidden Faces'
    bl_options = {'DEFAULT_CLOSED'}
    bl_order = 6

    def draw(self, context):
        layout = self.layout
        s = context.scene
        o = context.active_object
        col = layout.column(align=True)
        col.label(text="Active: %s" % (o.name if o else "<none>"))
        if o and o.type == 'MESH':
            col.label(text="Faces: %d" % len(o.data.polygons))
        layout.prop(s, "rhf_threshold")
        layout.prop(s, "rhf_rays")
        layout.prop(s, "rhf_front_only")
        layout.prop(s, "rhf_cull_aware")
        layout.prop(s, "rhf_use_selected")
        row = layout.row(align=True)
        for action, text, icon in (('SELECT', "Select Hidden", 'RESTRICT_SELECT_OFF'), ('DELETE', "Delete Hidden", 'TRASH')):
            op = row.operator(MESH_OT_remove_hidden_faces.bl_idname, text=text, icon=icon)
            op.action = action
            op.threshold = s.rhf_threshold
            op.rays = s.rhf_rays
            op.front_only = s.rhf_front_only
            op.cull_aware = s.rhf_cull_aware
            op.use_selected = s.rhf_use_selected


_PROPS = {
    "rhf_threshold": bpy.props.FloatProperty(
        name="Openness Threshold", subtype='PERCENTAGE', min=0.1, max=50.0, default=4.0,
        description="A face is hidden when less than this share of the view directions in front of it are open"),
    "rhf_rays": bpy.props.IntProperty(name="Rays", min=16, max=512, default=48,
                                      description="View directions tested per sample point"),
    "rhf_front_only": bpy.props.BoolProperty(name="Front Sides Only", default=True,
                                             description="Judge each face by its front side only, as the game draws it"),
    "rhf_cull_aware": bpy.props.BoolProperty(name="Back Faces Do Not Hide", default=True,
                                             description="A surface seen from behind is not drawn and hides nothing"),
    "rhf_use_selected": bpy.props.BoolProperty(name="Other Selected Objects Hide Too", default=False,
                                               description="Faces covered by the other selected meshes count as hidden"),
}


def register():
    for cls in (MESH_OT_remove_hidden_faces, VIEW3D_PT_remove_hidden_faces):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)
    for k, v in _PROPS.items():
        setattr(bpy.types.Scene, k, v)


def unregister():
    for k in _PROPS:
        if hasattr(bpy.types.Scene, k):
            delattr(bpy.types.Scene, k)
    for cls in (VIEW3D_PT_remove_hidden_faces, MESH_OT_remove_hidden_faces):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
