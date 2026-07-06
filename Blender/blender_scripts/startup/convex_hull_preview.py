import bpy
import bmesh
import gpu
from gpu_extras.batch import batch_for_shader
from mathutils import Vector

_LOG = r"C:\Users\user\source\repos\ShipbreakerShipbuilder\blender_debug.log"


def _log(msg):
    with open(_LOG, 'a') as f:
        f.write(msg + "\n")

bl_info = {
    "name": "Convex Hull Preview",
    "author": "KerballOne",
    "version": (1, 0),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Preview the convex hull Unity would generate for selected mesh(es), and highlight the exact overlap volume between hulls",
}

# Preview state lives in bpy.app.driver_namespace rather than module globals.
# Startup scripts get re-exec'd into a fresh module namespace on "Reload
# Scripts", which would otherwise orphan any draw handler already registered
# with SpaceView3D by a previous module instance (driver_namespace survives
# reload since it's process-global, not tied to the module object).
_NS_KEY = 'convex_hull_preview_state'


def _state():
    ns = bpy.app.driver_namespace
    if _NS_KEY not in ns:
        ns[_NS_KEY] = {
            'handle': None,
            'hud_handle': None,
            'batches': [],
            'overlap_batch': None,
            'obj_count': 0,
            'overlap_count': 0,
        }
    return ns[_NS_KEY]


_HULL_COLORS = [
    (0.2, 0.6, 1.0, 1.0),   # blue
    (0.2, 1.0, 0.4, 1.0),   # green
    (1.0, 0.4, 0.2, 1.0),   # orange
    (0.8, 0.2, 1.0, 1.0),   # purple
    (0.2, 1.0, 1.0, 1.0),   # cyan
    (1.0, 1.0, 0.2, 1.0),   # yellow-bright
    (1.0, 0.2, 0.6, 1.0),   # pink
    (0.4, 1.0, 0.2, 1.0),   # lime
]
_OVERLAP_COLOR = (1.0, 0.05, 0.05, 1.0)


def _compute_hull_bmesh_world(obj):
    """Run Convex Hull on a copy of the object's mesh data in an isolated
    bmesh (no edit-mode / undo needed), with verts baked to world space.
    Returns the hull bmesh (faces intact, for boolean intersection) plus
    world-space line segments (for the wireframe overlay) and the hull's
    world-space bbox min/max (cheap pre-filter before an exact boolean)."""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()

    mx = obj.matrix_world
    for v in bm.verts:
        v.co = mx @ v.co

    ch = bmesh.ops.convex_hull(bm, input=bm.verts, use_existing_faces=False)
    hull_geom = ch["geom"]
    hull_edges = [g for g in hull_geom if isinstance(g, bmesh.types.BMEdge)]
    segs = [(e.verts[0].co.copy(), e.verts[1].co.copy()) for e in hull_edges]

    # Discard verts left behind inside the hull (source mesh geometry the
    # hull operator determined isn't part of the hull surface) so the
    # bmesh only contains the hull solid for the boolean step.
    interior_verts = [g for g in ch.get("geom_interior", []) if isinstance(g, bmesh.types.BMVert)]
    if interior_verts:
        bmesh.ops.delete(bm, geom=interior_verts, context='VERTS')
    bm.verts.ensure_lookup_table()

    bmin = Vector((min(v.co.x for v in bm.verts), min(v.co.y for v in bm.verts), min(v.co.z for v in bm.verts)))
    bmax = Vector((max(v.co.x for v in bm.verts), max(v.co.y for v in bm.verts), max(v.co.z for v in bm.verts)))

    return bm, segs, bmin, bmax


def _boxes_overlap(amin, amax, bmin, bmax):
    return (amin.x <= bmax.x and amax.x >= bmin.x and
            amin.y <= bmax.y and amax.y >= bmin.y and
            amin.z <= bmax.z and amax.z >= bmin.z)


_MIN_OVERLAP_VOLUME = 1e-6  # m^3 — below this, treat two touching hulls (shared face/edge, no real interpenetration) as non-overlapping


def _bm_volume(bm):
    """Same divergence-theorem volume calc as _mesh_volume, but operating
    directly on a bmesh's (already-triangulated-by-convex_hull) faces."""
    vol = 0.0
    for f in bm.faces:
        verts = f.verts
        v0 = verts[0].co
        for i in range(1, len(verts) - 1):
            v1 = verts[i].co
            v2 = verts[i + 1].co
            vol += v0.dot(v1.cross(v2)) / 6.0
    return abs(vol)


def _mesh_volume(mesh):
    """Signed volume via the divergence theorem, summed over triangles from
    the origin — degenerate slivers left by touching (non-overlapping) hulls
    have near-zero volume even though the boolean solver returns a non-empty,
    numerically noisy mesh for them."""
    mesh.calc_loop_triangles()
    vol = 0.0
    for tri in mesh.loop_triangles:
        v0 = mesh.vertices[tri.vertices[0]].co
        v1 = mesh.vertices[tri.vertices[1]].co
        v2 = mesh.vertices[tri.vertices[2]].co
        vol += v0.dot(v1.cross(v2)) / 6.0
    return abs(vol)


def _hull_intersection_tris(bm_a, bm_b, vol_a, vol_b):
    """Exact boolean INTERSECT of two hull bmeshes (already in world space,
    faces already welled up by convex_hull). Uses temporary mesh objects
    with a Boolean modifier — the same approach as Split By Convex Hulls —
    since bmesh.ops.intersect operates on a single self-intersecting mesh,
    not two separate solids. Returns a flat list of triangle verts for the
    overlap solid, or an empty list if the hulls don't actually interpenetrate.

    Two safety checks on the raw boolean result:
    - near-zero volume: hulls that merely touch along a shared face/edge can
      still produce a thin, numerically-noisy non-empty result.
    - volume exceeding min(vol_a, vol_b): a true intersection can never be
      larger than its smallest operand. The EXACT solver has been observed
      to emit a corrupted/mis-wound result mesh for certain thin, coplanar-
      touching hull pairs, whose "volume" computes to many times larger than
      either input — that's a solver artifact, not a real overlap, so it's
      discarded rather than trusted.
    """
    mesh_a = bpy.data.meshes.new("_hull_overlap_a_tmp")
    bm_a.to_mesh(mesh_a)
    obj_a = bpy.data.objects.new("_hull_overlap_a_tmp", mesh_a)
    bpy.context.scene.collection.objects.link(obj_a)

    mesh_b = bpy.data.meshes.new("_hull_overlap_b_tmp")
    bm_b.to_mesh(mesh_b)
    obj_b = bpy.data.objects.new("_hull_overlap_b_tmp", mesh_b)
    bpy.context.scene.collection.objects.link(obj_b)

    tris = []
    try:
        mod = obj_a.modifiers.new(name="_hull_overlap", type='BOOLEAN')
        mod.operation = 'INTERSECT'
        mod.object = obj_b
        mod.solver = 'EXACT'

        depsgraph = bpy.context.evaluated_depsgraph_get()
        obj_a_eval = obj_a.evaluated_get(depsgraph)
        eval_mesh = obj_a_eval.to_mesh()
        vol = _mesh_volume(eval_mesh)
        max_plausible = min(vol_a, vol_b) * (1.0 + 1e-4)
        if vol < _MIN_OVERLAP_VOLUME:
            pass  # touching, not overlapping
        elif vol > max_plausible:
            _log(f"hull overlap DISCARDED (implausible): result_vol={vol:.6f} "
                 f"exceeds min(hull_a={vol_a:.6f}, hull_b={vol_b:.6f}) — solver artifact")
        else:
            eval_mesh.calc_loop_triangles()
            for tri in eval_mesh.loop_triangles:
                for vi in tri.vertices:
                    tris.append(tuple(eval_mesh.vertices[vi].co))
        obj_a_eval.to_mesh_clear()
    finally:
        bpy.data.objects.remove(obj_a, do_unlink=True)
        bpy.data.objects.remove(obj_b, do_unlink=True)
        bpy.data.meshes.remove(mesh_a)
        bpy.data.meshes.remove(mesh_b)

    return tris


def _draw_callback():
    st = _state()
    shader = gpu.shader.from_builtin('UNIFORM_COLOR')
    gpu.state.blend_set('ALPHA')
    gpu.state.depth_test_set('LESS_EQUAL')
    gpu.state.face_culling_set('NONE')
    shader.bind()

    if st['overlap_batch']:
        shader.uniform_float("color", (_OVERLAP_COLOR[0], _OVERLAP_COLOR[1], _OVERLAP_COLOR[2], 0.35))
        st['overlap_batch'].draw(shader)

    gpu.state.line_width_set(2.0)
    for color, batch in st['batches']:
        shader.uniform_float("color", color)
        batch.draw(shader)
    gpu.state.line_width_set(1.0)

    gpu.state.face_culling_set('BACK')
    gpu.state.blend_set('NONE')


def _draw_hud():
    import blf
    st = _state()
    font_id = 0
    blf.size(font_id, 14)
    blf.color(font_id, 1.0, 1.0, 1.0, 1.0)
    blf.position(font_id, 20, 50, 0)
    blf.draw(font_id, f"Convex Hull Preview  |  Hulls: {st['obj_count']}  |  Overlaps: {st['overlap_count']}")


def _clear_preview():
    st = _state()
    if st['handle'] is not None:
        try:
            bpy.types.SpaceView3D.draw_handler_remove(st['handle'], 'WINDOW')
        except Exception:
            pass
        st['handle'] = None
    if st['hud_handle'] is not None:
        try:
            bpy.types.SpaceView3D.draw_handler_remove(st['hud_handle'], 'WINDOW')
        except Exception:
            pass
        st['hud_handle'] = None
    st['batches'] = []
    st['overlap_batch'] = None
    st['obj_count'] = 0
    st['overlap_count'] = 0
    for area in bpy.context.screen.areas:
        if area.type == 'VIEW_3D':
            area.tag_redraw()


class MESH_OT_convex_hull_preview(bpy.types.Operator):
    bl_idname  = "mesh.convex_hull_preview"
    bl_label   = "Preview Convex Hull(s)"
    bl_description = "Show the convex hull mesh Unity would generate for each selected object, as a wireframe overlay; highlights the exact overlap volume between hulls in red"
    bl_options = {'REGISTER'}

    @classmethod
    def poll(cls, context):
        return any(o.type == 'MESH' for o in context.selected_objects)

    def execute(self, context):
        st = _state()

        objs = [o for o in context.selected_objects if o.type == 'MESH' and o.data.vertices]
        if not objs:
            self.report({'ERROR'}, "Select one or more mesh objects with geometry")
            return {'CANCELLED'}

        _clear_preview()

        shader = gpu.shader.from_builtin('UNIFORM_COLOR')
        hulls = []  # (obj, bm, segs, bmin, bmax)
        for obj in objs:
            bm, segs, bmin, bmax = _compute_hull_bmesh_world(obj)
            hulls.append((obj, bm, segs, bmin, bmax))

        batches = []
        for i, (obj, bm, segs, bmin, bmax) in enumerate(hulls):
            color = _HULL_COLORS[i % len(_HULL_COLORS)]
            verts = []
            for va, vb in segs:
                verts += [tuple(va), tuple(vb)]
            if verts:
                batches.append((color, batch_for_shader(shader, 'LINES', {"pos": verts})))

        overlap_tris = []
        overlap_count = 0
        try:
            for i in range(len(hulls)):
                for j in range(i + 1, len(hulls)):
                    obj_a, bm_a, _, amin, amax = hulls[i]
                    obj_b, bm_b, _, bmin, bmax = hulls[j]
                    if not _boxes_overlap(amin, amax, bmin, bmax):
                        continue
                    vol_a = _bm_volume(bm_a)
                    vol_b = _bm_volume(bm_b)
                    tris = _hull_intersection_tris(bm_a, bm_b, vol_a, vol_b)
                    if tris:
                        overlap_count += 1
                        overlap_tris += tris
        finally:
            for _, bm, _, _, _ in hulls:
                bm.free()

        st['batches'] = batches
        st['overlap_batch'] = (
            batch_for_shader(shader, 'TRIS', {"pos": overlap_tris}) if overlap_tris else None
        )
        st['obj_count'] = len(hulls)
        st['overlap_count'] = overlap_count

        st['handle'] = bpy.types.SpaceView3D.draw_handler_add(_draw_callback, (), 'WINDOW', 'POST_VIEW')
        st['hud_handle'] = bpy.types.SpaceView3D.draw_handler_add(_draw_hud, (), 'WINDOW', 'POST_PIXEL')
        for area in context.screen.areas:
            if area.type == 'VIEW_3D':
                area.tag_redraw()

        self.report({'INFO'}, f"Previewing {len(hulls)} hull(s), {overlap_count} overlapping pair(s)")
        return {'FINISHED'}


class MESH_OT_convex_hull_preview_clear(bpy.types.Operator):
    bl_idname  = "mesh.convex_hull_preview_clear"
    bl_label   = "Clear Convex Hull Preview"
    bl_description = "Remove the convex hull preview overlay"
    bl_options = {'REGISTER'}

    def execute(self, context):
        _clear_preview()
        self.report({'INFO'}, "Convex hull preview cleared")
        return {'FINISHED'}


class VIEW3D_PT_convex_hull_preview(bpy.types.Panel):
    bl_space_type  = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category    = 'ShipBreaker'
    bl_label       = 'Convex Hull Preview'
    bl_options     = {'DEFAULT_CLOSED'}

    def draw(self, context):
        layout = self.layout
        sel = [o for o in context.selected_objects if o.type == 'MESH']
        col = layout.column(align=True)
        col.label(text=f"Selected meshes: {len(sel)}")
        layout.operator(MESH_OT_convex_hull_preview.bl_idname, text="Preview Convex Hull(s)", icon='MESH_ICOSPHERE')
        layout.operator(MESH_OT_convex_hull_preview_clear.bl_idname, text="Clear Preview", icon='X')


def register():
    for cls in (MESH_OT_convex_hull_preview, MESH_OT_convex_hull_preview_clear, VIEW3D_PT_convex_hull_preview):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)


def unregister():
    _clear_preview()
    for cls in (MESH_OT_convex_hull_preview, MESH_OT_convex_hull_preview_clear, VIEW3D_PT_convex_hull_preview):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
