"""Part Splitting tools (ShipBreaker sidebar).

Every part of a custom ship collides as its CONVEX HULL, so a part that other parts pass through (a ring, a frame, a
collar, a tunnel) has to be cut into pieces that are each close to convex. These three tools cover that job:

  Cut Along Face     cut the other selected objects along the plane of a face of the active object (or through three
                     selected vertices), fill every cut in the same step, and keep both sides as separate objects.
                     Cutting a nested part along the faces of the part it nests with keeps the seams of both in line.
  Check Parts        per selected object: open edges, non-manifold edges, loose pieces, volume. A good part is closed
                     and one piece.
  Collider Check     per pair of selected objects: how much of one sits inside the convex hull of the other.

The functions cut_object / part_health / collider_report hold the work and can be called from scripts.
"""
import bpy
import bmesh
import math
from mathutils import Vector

bl_info = {"name": "Part Splitting", "blender": (4, 0, 0), "category": "Mesh"}

REPORT_TEXT = "SB_Part_Report"


# --------------------------------------------------------------------------------------------------------------------
# cutting
# --------------------------------------------------------------------------------------------------------------------
def _loops_of(edges):
    """Group edges into connected loops."""
    left = set(edges)
    out = []
    while left:
        e = left.pop()
        grp = [e]
        stack = [e]
        while stack:
            x = stack.pop()
            for v in x.verts:
                for e2 in v.link_edges:
                    if e2 in left:
                        left.discard(e2)
                        grp.append(e2)
                        stack.append(e2)
        out.append(grp)
    return out


def _fill_cut(bm, co, no):
    """Fill the open loops lying on the plane. A loop inside another one (hollow cross-section) is filled together
    with it as a ring, so a thick-walled tube gets a ring-shaped cap and not a lid. Returns (new faces, loops left open)."""
    nn = no.normalized()
    cut = [e for e in bm.edges if e.is_boundary and all(abs((v.co - co).dot(nn)) < 1e-5 for v in e.verts)]
    loops = _loops_of(cut)
    ux = nn.cross(Vector((0, 1, 0)) if abs(nn.y) < 0.9 else Vector((1, 0, 0))).normalized()
    uy = nn.cross(ux)

    def outline(grp):
        nxt = {}
        for e in grp:
            a, b = e.verts
            nxt.setdefault(a, []).append(b)
            nxt.setdefault(b, []).append(a)
        start = grp[0].verts[0]
        pts = [start]
        prev, cur = None, start
        for _ in range(len(grp) + 2):
            cand = [v for v in nxt[cur] if v is not prev]
            if not cand:
                break
            prev, cur = cur, cand[0]
            if cur is start:
                break
            pts.append(cur)
        return [((v.co - co).dot(ux), (v.co - co).dot(uy)) for v in pts]

    def inside(pt, poly):
        x, y = pt
        c = False
        for i in range(len(poly)):
            x1, y1 = poly[i]
            x2, y2 = poly[(i + 1) % len(poly)]
            if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1:
                c = not c
        return c

    polys = [outline(g) for g in loops]
    parent = [None] * len(loops)
    for i in range(len(loops)):
        for j in range(len(loops)):
            if i != j and polys[i] and len(polys[j]) >= 3 and inside(polys[i][0], polys[j]):
                parent[i] = j
    faces = []
    bad = 0
    for j in range(len(loops)):
        if parent[j] is not None:
            continue
        kids = [i for i in range(len(loops)) if parent[i] == j]
        if kids:
            res = bmesh.ops.triangle_fill(bm, use_beauty=True, use_dissolve=False,
                                          edges=loops[j] + [e for i in kids for e in loops[i]])
            new = [g for g in res["geom"] if isinstance(g, bmesh.types.BMFace)]
        else:
            new = bmesh.ops.holes_fill(bm, edges=loops[j], sides=0)["faces"]
        if not new:
            bad += 1
        faces += new
    return faces, bad


def _half(bm_src, co, no, fill):
    """Copy of the mesh holding only the side the normal points to, cut filled, cap faces turned outward."""
    bm = bm_src.copy()
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-6, plane_co=co, plane_no=no,
                           clear_inner=True)
    bad = 0
    if fill and bm.faces:
        faces, bad = _fill_cut(bm, co, no)
        for f in faces:
            f.normal_update()
            if f.normal.dot(no) > 0:          # the cap of the kept side faces away from that side
                f.normal_flip()
            f.smooth = False
    return bm, bad


def _islands(bm):
    seen = set()
    out = []
    for v in bm.verts:
        if v.index in seen:
            continue
        grp = [v.index]
        seen.add(v.index)
        stack = [v]
        while stack:
            w = stack.pop()
            for e in w.link_edges:
                u = e.other_vert(w)
                if u.index not in seen:
                    seen.add(u.index)
                    grp.append(u.index)
                    stack.append(u)
        out.append(grp)
    return sorted(out, key=len, reverse=True)


def _next_name(name):
    """'<base>_2', '<base>_3' ...: the first number not in use (a trailing _<number> of `name` is dropped first)."""
    import re
    base = re.sub(r"_\d+$", "", name)
    n = 2
    while "%s_%d" % (base, n) in bpy.data.objects:
        n += 1
    return "%s_%d" % (base, n)


def _new_like(obj, bm, name):
    me = obj.data.copy()
    me.name = name
    bm.to_mesh(me)
    me.update()
    o = obj.copy()
    o.data = me
    o.name = name
    for c in obj.users_collection:
        c.objects.link(o)
    return o


def _write(obj, bm, name, loose):
    """Put bm into obj (or into new objects named name / name_2 ... when it falls into loose pieces)."""
    bm.verts.index_update()
    parts = _islands(bm) if loose else [None]
    out = []
    for i, idx in enumerate(parts):
        b = bm
        if len(parts) > 1:
            b = bm.copy()
            b.verts.ensure_lookup_table()
            keep = set(idx)
            bmesh.ops.delete(b, geom=[v for v in b.verts if v.index not in keep], context='VERTS')
        nm = name if i == 0 else _next_name(name)
        if i == 0 and obj.name == name:
            b.to_mesh(obj.data)
            obj.data.update()
            out.append(obj)
        else:
            out.append(_new_like(obj, b, nm))
        if b is not bm:
            b.free()
    return out


def cut_object(obj, co, no, keep='BOTH', fill=True, loose=True):
    """Cut one mesh object along the world-space plane (co, no).

    keep  'BOTH'  the object keeps the side BEHIND the plane, a new object '<name>_2' (next free number) gets the side in front
          'FRONT' / 'BACK'  only that side is kept, in the object itself
    Returns (objects, loops that could not be filled). An object the plane does not cross is returned untouched."""
    mw = obj.matrix_world
    co_l = mw.inverted() @ co
    no_l = (mw.to_3x3().transposed() @ no).normalized()
    side = [(v.co - co_l).dot(no_l) for v in obj.data.vertices]
    if not side or min(side) > -1e-5 or max(side) < 1e-5:
        return [obj], 0
    src = bmesh.new()
    src.from_mesh(obj.data)
    out = []
    bad = 0
    name = obj.name
    if keep in ('BOTH', 'FRONT'):
        front, b = _half(src, co_l, no_l, fill)
        bad += b
    if keep in ('BOTH', 'BACK'):
        back, b = _half(src, co_l, -no_l, fill)
        bad += b
    src.free()
    if keep == 'FRONT':
        out += _write(obj, front, name, loose)
        front.free()
    else:
        out += _write(obj, back, name, loose)
        back.free()
        if keep == 'BOTH':
            dummy = _new_like(obj, front, _next_name(name))
            more = _write(dummy, front, dummy.name, loose)
            out += more
            front.free()
    return out, bad


# --------------------------------------------------------------------------------------------------------------------
# checks
# --------------------------------------------------------------------------------------------------------------------
def part_health(obj, weld=0.0002):
    """Open edges, non-manifold edges, loose pieces, volume and triangle count of a mesh object. Vertices closer than
    `weld` count as one, so split normals or uv seams do not show up as open edges."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=weld)
    opened = sum(1 for e in bm.edges if e.is_boundary)
    nonman = sum(1 for e in bm.edges if not e.is_manifold and not e.is_boundary)
    bm.verts.index_update()
    pieces = len(_islands(bm)) if bm.verts else 0
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    sc = obj.matrix_world.to_scale()
    vol = abs(bm.calc_volume()) * abs(sc.x * sc.y * sc.z) if opened == 0 and bm.faces else 0.0
    tris = len(bm.faces)
    bm.free()
    return {"name": obj.name, "open": opened, "nonmanifold": nonman, "pieces": pieces, "volume": vol, "tris": tris,
            "ok": opened == 0 and nonman == 0 and pieces == 1}


def _hull_planes(points):
    """Outward planes (normals F x 3, offsets F) of the convex hull of world-space points, as numpy arrays."""
    import numpy as np
    bm = bmesh.new()
    for p in points:
        bm.verts.new(p)
    res = bmesh.ops.convex_hull(bm, input=bm.verts[:])
    faces = [g for g in res["geom"] if isinstance(g, bmesh.types.BMFace)]
    if not faces:
        bm.free()
        return None, None
    cen = sum((v.co for v in bm.verts), Vector()) / len(bm.verts)
    ns = []
    ds = []
    for f in faces:
        f.normal_update()
        n = f.normal.copy()
        p = f.verts[0].co
        if n.length < 1e-9:
            continue
        if n.dot(cen - p) > 0:
            n = -n
        ns.append(n[:])
        ds.append(n.dot(p))
    bm.free()
    return np.array(ns), np.array(ds)


def _sample_points(obj):
    """World-space sample points of an object: vertices, edge middles and face centres."""
    import numpy as np
    mw = obj.matrix_world
    me = obj.data
    vs = [mw @ v.co for v in me.vertices]
    pts = [v[:] for v in vs]
    pts += [((vs[e.vertices[0]] + vs[e.vertices[1]]) / 2)[:] for e in me.edges]
    pts += [(mw @ p.center)[:] for p in me.polygons]
    return np.array(pts) if pts else np.zeros((0, 3))


def collider_report(objs, tolerance=0.005):
    """For every ordered pair (A, B): the share of B's sample points lying more than `tolerance` inside the convex hull
    of A, and the deepest one. Only pairs with something inside are returned, deepest first."""
    import numpy as np
    data = []
    for o in objs:
        pts = _sample_points(o)
        n, d = _hull_planes([o.matrix_world @ v.co for v in o.data.vertices]) if len(o.data.vertices) >= 4 else (None, None)
        data.append((o, pts, n, d))
    rows = []
    for a, _, n, d in data:
        if n is None:
            continue
        for b, pts, _, _ in data:
            if b is a or not len(pts):
                continue
            depth = -(pts @ n.T - d).max(axis=1)          # > 0: that far inside every hull face
            ins = depth > tolerance
            if ins.any():
                rows.append({"collider": a.name, "part": b.name, "share": float(ins.mean()), "depth": float(depth.max()),
                             "points": int(ins.sum())})
    return sorted(rows, key=lambda r: -r["depth"])


# --------------------------------------------------------------------------------------------------------------------
# operators
# --------------------------------------------------------------------------------------------------------------------
def _show(context, title, lines):
    txt = bpy.data.texts.get(REPORT_TEXT) or bpy.data.texts.new(REPORT_TEXT)
    txt.clear()
    txt.write(title + "\n" + "\n".join(lines) + "\n")
    context.scene.sb_part_report = title + "\n" + "\n".join(lines)


def _health_line(h):
    if h["ok"]:
        return "OK   %s: closed, one piece, %.3f m3, %d tris" % (h["name"], h["volume"], h["tris"])
    why = []
    if h["open"]:
        why.append("%d open edges" % h["open"])
    if h["nonmanifold"]:
        why.append("%d non-manifold edges" % h["nonmanifold"])
    if h["pieces"] != 1:
        why.append("%d loose pieces" % h["pieces"])
    return "FIX  %s: %s" % (h["name"], ", ".join(why))


def _plane_from_edit(context):
    """World-space plane from the edit-mode selection of the active object: its active / first selected face, or
    three selected vertices. Returns (co, no) or a message."""
    obj = context.edit_object or context.active_object
    if obj is None or obj.type != 'MESH' or obj.mode != 'EDIT':
        return "Enter Edit Mode and select a face (or three vertices) on the reference object"
    bm = bmesh.from_edit_mesh(obj.data)
    mw = obj.matrix_world
    nm = mw.to_3x3().inverted().transposed()
    faces = [f for f in bm.faces if f.select]
    if faces:
        f = bm.faces.active if bm.faces.active in faces else faces[0]
        return mw @ f.calc_center_median(), (nm @ f.normal).normalized()
    vs = [v for v in bm.verts if v.select]
    if len(vs) == 3:
        a, b, c = (mw @ v.co for v in vs)
        n = (b - a).cross(c - a)
        if n.length < 1e-9:
            return "The three vertices are in one line"
        return a, n.normalized()
    return "Select one face, or exactly three vertices, on the reference object"


class MESH_OT_sb_pick_cut_plane(bpy.types.Operator):
    bl_idname = "mesh.sb_pick_cut_plane"
    bl_label = "Pick Cut Plane"
    bl_description = ("Remember the plane of the selected face (or through three selected vertices) so it can be used "
                      "later with Cut Selected Along Plane")
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        r = _plane_from_edit(context)
        if isinstance(r, str):
            self.report({'ERROR'}, r)
            return {'CANCELLED'}
        context.scene.sb_cut_plane_co = r[0]
        context.scene.sb_cut_plane_no = r[1]
        context.scene.sb_cut_plane_set = True
        self.report({'INFO'}, "Cut plane stored")
        return {'FINISHED'}


class MESH_OT_sb_cut_along_face(bpy.types.Operator):
    bl_idname = "mesh.sb_cut_along_face"
    bl_label = "Cut Along Face"
    bl_description = ("Edit Mode: cut the OTHER selected objects along the plane of the selected face of the active "
                      "object (or through three selected vertices). Object Mode: cut the selected objects along the "
                      "stored plane. Every cut is filled and both sides are kept as separate objects")
    bl_options = {'REGISTER', 'UNDO'}

    keep: bpy.props.EnumProperty(name="Keep", default='BOTH', items=[
        ('BOTH', "Both sides", "The object keeps the side behind the face; a new object '<name>_2' (next free number) gets the side in front"),
        ('BACK', "Behind the face", "Keep only the side behind the face"),
        ('FRONT', "In front of the face", "Keep only the side the face normal points to")])
    offset: bpy.props.FloatProperty(name="Offset", default=0.0, unit='LENGTH',
                                    description="Move the cut plane this far along the face normal")
    fill: bpy.props.BoolProperty(name="Fill the cut", default=True,
                                 description="Close every cut loop in the same step (nested loops are filled as a ring)")
    loose: bpy.props.BoolProperty(name="Separate loose pieces", default=True,
                                  description="If a side falls into several pieces, make each piece its own object")

    @classmethod
    def poll(cls, context):
        return context.active_object is not None and context.active_object.type == 'MESH'

    def execute(self, context):
        sc = context.scene
        ref = None
        if context.mode == 'EDIT_MESH':
            r = _plane_from_edit(context)
            if isinstance(r, str):
                self.report({'ERROR'}, r)
                return {'CANCELLED'}
            co, no = r
            ref = context.edit_object or context.active_object
            targets = [o for o in context.selected_objects if o.type == 'MESH' and o is not ref]
            bpy.ops.object.mode_set(mode='OBJECT')
        else:
            if not sc.sb_cut_plane_set:
                self.report({'ERROR'}, "No plane: in Edit Mode select a face on the reference object, or use Pick Cut Plane first")
                return {'CANCELLED'}
            co, no = Vector(sc.sb_cut_plane_co), Vector(sc.sb_cut_plane_no)
            targets = [o for o in context.selected_objects if o.type == 'MESH']
        if not targets:
            self.report({'ERROR'}, "Select the objects to cut as well (the reference object itself is not cut)")
            return {'CANCELLED'}
        co = co + no * self.offset
        results = []
        lines = []
        untouched = 0
        unfilled = 0
        for o in targets:
            if o.data.users > 1:
                o.data = o.data.copy()
            out, bad = cut_object(o, co, no, keep=self.keep, fill=self.fill, loose=self.loose)
            unfilled += bad
            if len(out) == 1 and out[0] is o and bad == 0 and self.keep == 'BOTH':
                untouched += 1
                lines.append("--   %s: the plane does not cross it" % o.name)
                continue
            results += out
            lines += [_health_line(part_health(x)) for x in out]
        for o in context.selected_objects:
            o.select_set(False)
        for o in results:
            o.select_set(True)
        if results:
            context.view_layer.objects.active = results[0]
        probs = sum(1 for ln in lines if ln.startswith("FIX"))
        title = "Cut Along Face: %d object(s) cut into %d piece(s); %d need attention; %d not crossed" % (
            len(targets) - untouched, len(results), probs, untouched)
        if unfilled:
            title += "; %d cut loop(s) could not be filled" % unfilled
        _show(context, title, lines)
        self.report({'WARNING'} if probs or unfilled else {'INFO'}, title)
        return {'FINISHED'}


class OBJECT_OT_sb_check_parts(bpy.types.Operator):
    bl_idname = "object.sb_check_parts"
    bl_label = "Check Parts"
    bl_description = ("Per selected object: open edges, non-manifold edges, loose pieces and volume. A part is good "
                      "when it is closed and one piece. Afterwards only the objects that need attention stay selected")
    bl_options = {'REGISTER', 'UNDO'}

    select_problems: bpy.props.BoolProperty(name="Select the ones to fix", default=True)

    @classmethod
    def poll(cls, context):
        return any(o.type == 'MESH' for o in context.selected_objects)

    def execute(self, context):
        if context.mode != 'OBJECT':
            bpy.ops.object.mode_set(mode='OBJECT')
        objs = [o for o in context.selected_objects if o.type == 'MESH']
        hs = [part_health(o) for o in objs]
        bad = [h for h in hs if not h["ok"]]
        lines = [_health_line(h) for h in sorted(hs, key=lambda h: (h["ok"], h["name"]))]
        title = "Check Parts: %d of %d closed and one piece" % (len(hs) - len(bad), len(hs))
        _show(context, title, lines)
        if self.select_problems and bad:
            names = {h["name"] for h in bad}
            for o in objs:
                o.select_set(o.name in names)
        self.report({'WARNING'} if bad else {'INFO'}, title)
        return {'FINISHED'}


class OBJECT_OT_sb_collider_check(bpy.types.Operator):
    bl_idname = "object.sb_collider_check"
    bl_label = "Collider Check"
    bl_description = ("Every part collides as its convex hull. For each pair of selected objects: how much of one lies "
                      "inside the convex hull of the other. Parts that only touch are not reported")
    bl_options = {'REGISTER', 'UNDO'}

    tolerance: bpy.props.FloatProperty(name="Ignore below", default=0.005, min=0.0, unit='LENGTH',
                                       description="Points less than this far inside a hull count as touching")

    @classmethod
    def poll(cls, context):
        return sum(1 for o in context.selected_objects if o.type == 'MESH') >= 2

    def execute(self, context):
        if context.mode != 'OBJECT':
            bpy.ops.object.mode_set(mode='OBJECT')
        objs = [o for o in context.selected_objects if o.type == 'MESH']
        rows = collider_report(objs, self.tolerance)
        lines = ["%s is inside the collider of %s: %.0f %% of it, up to %.2f m deep" % (
            r["part"], r["collider"], 100 * r["share"], r["depth"]) for r in rows]
        title = "Collider Check: %d object(s), %d overlap(s)" % (len(objs), len(rows))
        if not rows:
            lines = ["No part sits inside another part's convex hull."]
        _show(context, title, lines)
        self.report({'WARNING'} if rows else {'INFO'}, title)
        return {'FINISHED'}


class VIEW3D_PT_sb_part_splitting(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'ShipBreaker'
    bl_label = 'Part Splitting'
    bl_options = {'DEFAULT_CLOSED'}

    def draw(self, context):
        layout = self.layout
        sc = context.scene
        col = layout.column(align=True)
        if context.mode == 'EDIT_MESH':
            col.label(text="Select a face on the reference part,")
            col.label(text="with the parts to cut also selected.")
            col.operator(MESH_OT_sb_cut_along_face.bl_idname, text="Cut Others Along This Face", icon='MOD_BOOLEAN')
            col.operator(MESH_OT_sb_pick_cut_plane.bl_idname, text="Pick Cut Plane (use later)", icon='EYEDROPPER')
        else:
            row = col.row(align=True)
            row.enabled = sc.sb_cut_plane_set
            row.operator(MESH_OT_sb_cut_along_face.bl_idname, text="Cut Selected Along Stored Plane", icon='MOD_BOOLEAN')
            col.label(text="Plane stored" if sc.sb_cut_plane_set else "Edit Mode: select a face to cut along",
                      icon='CHECKMARK' if sc.sb_cut_plane_set else 'INFO')
        layout.separator()
        col = layout.column(align=True)
        col.operator(OBJECT_OT_sb_check_parts.bl_idname, icon='CHECKMARK')
        col.operator(OBJECT_OT_sb_collider_check.bl_idname, icon='MESH_ICOSPHERE')
        if sc.sb_part_report:
            box = layout.box()
            lines = sc.sb_part_report.split("\n")
            for ln in lines[:16]:
                box.label(text=ln[:110])
            if len(lines) > 16:
                box.label(text="... %d more lines" % (len(lines) - 16))
            box.label(text="Full text: Text Editor > %s" % REPORT_TEXT, icon='TEXT')


_CLASSES = (MESH_OT_sb_pick_cut_plane, MESH_OT_sb_cut_along_face, OBJECT_OT_sb_check_parts, OBJECT_OT_sb_collider_check,
            VIEW3D_PT_sb_part_splitting)
_PROPS = {
    "sb_cut_plane_co": bpy.props.FloatVectorProperty(size=3),
    "sb_cut_plane_no": bpy.props.FloatVectorProperty(size=3, default=(0.0, 0.0, 1.0)),
    "sb_cut_plane_set": bpy.props.BoolProperty(default=False),
    "sb_part_report": bpy.props.StringProperty(default=""),
}


def register():
    for cls in _CLASSES:
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
    for cls in reversed(_CLASSES):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
