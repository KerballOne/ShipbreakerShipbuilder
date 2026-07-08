import bpy
import bmesh
from mathutils import Vector

bl_info = {
    "name": "Miter Seam Fix",
    "author": "KerballOne",
    "version": (1, 0),
    "blender": (4, 0, 0),
    "category": "Object",
    "description": "Detect and fix overlapping/non-coplanar seams between separately-solidified panels via miter bisect",
}

_LOG = r"C:\Users\user\source\repos\ShipbreakerShipbuilder\blender_debug.log"


def _log(msg):
    with open(_LOG, 'a') as f:
        f.write(msg + "\n")


# --- Debug draw state --- stored in driver_namespace to survive reloads
def _draw_callback():
    import gpu
    from gpu_extras.batch import batch_for_shader
    data = bpy.app.driver_namespace.get('miter_debug_draw')
    if not data:
        return

    def draw_lines(lines, color):
        if not lines:
            return
        coords = []
        for a, b in lines:
            coords.append(a)
            coords.append(b)
        shader = gpu.shader.from_builtin('UNIFORM_COLOR')
        batch = batch_for_shader(shader, 'LINES', {"pos": coords})
        gpu.state.line_width_set(4.0)
        shader.bind()
        shader.uniform_float("color", color)
        batch.draw(shader)
        gpu.state.line_width_set(1.0)

    def draw_points(points, color, size=12.0):
        if not points:
            return
        shader = gpu.shader.from_builtin('UNIFORM_COLOR')
        batch = batch_for_shader(shader, 'POINTS', {"pos": points})
        gpu.state.point_size_set(size)
        shader.bind()
        shader.uniform_float("color", color)
        batch.draw(shader)

    draw_lines(data.get('plane_lines', []), (0.1, 0.6, 1.0, 1.0))       # blue: miter plane outline
    draw_lines(data.get('keep_a_lines', []), (0.1, 1.0, 0.2, 1.0))      # green: A keep direction
    draw_lines(data.get('keep_b_lines', []), (1.0, 0.6, 0.0, 1.0))      # orange: B keep direction
    draw_points(data.get('overlap_points', []), (1.0, 0.0, 0.0, 1.0))  # red: overlap centroid
    draw_points(data.get('centroid_a_points', []), (0.1, 1.0, 0.2, 1.0))
    draw_points(data.get('centroid_b_points', []), (1.0, 0.6, 0.0, 1.0))


def _install_draw_handler():
    if not bpy.app.driver_namespace.get('miter_debug_handler'):
        handler = bpy.types.SpaceView3D.draw_handler_add(
            _draw_callback, (), 'WINDOW', 'POST_VIEW')
        bpy.app.driver_namespace['miter_debug_handler'] = handler


def _clear_debug_draw(context=None):
    bpy.app.driver_namespace['miter_debug_draw'] = {}
    if context and context.screen:
        for area in context.screen.areas:
            if area.type == 'VIEW_3D':
                area.tag_redraw()


def _bbox_world(obj):
    corners = [obj.matrix_world @ Vector(c) for c in obj.bound_box]
    xs = [c.x for c in corners]
    ys = [c.y for c in corners]
    zs = [c.z for c in corners]
    return (min(xs), max(xs)), (min(ys), max(ys)), (min(zs), max(zs))


def _bbox_overlaps(a, b, margin):
    for (amin, amax), (bmin, bmax) in zip(a, b):
        if amax + margin < bmin or bmax + margin < amin:
            return False
    return True


def _intersect_solids(obj_a, obj_b):
    """Boolean-intersect copies of obj_a/obj_b (world space) and return
    (volume, centroid_world, verts_world). volume > 0 means the two solids
    actually interpenetrate rather than just sharing a coincident boundary
    face. verts_world are the overlap wedge's vertices, used to derive the
    seam line/plane via PCA."""
    bm_a = bmesh.new()
    bm_a.from_mesh(obj_a.data)
    bm_a.transform(obj_a.matrix_world)
    mesh_a = bpy.data.meshes.new("_moa_tmp")
    bm_a.to_mesh(mesh_a)
    bm_a.free()

    bm_b = bmesh.new()
    bm_b.from_mesh(obj_b.data)
    bm_b.transform(obj_b.matrix_world)
    mesh_b = bpy.data.meshes.new("_mob_tmp")
    bm_b.to_mesh(mesh_b)
    bm_b.free()

    tmp_a = bpy.data.objects.new("_moa_tmp", mesh_a)
    tmp_b = bpy.data.objects.new("_mob_tmp", mesh_b)
    bpy.context.collection.objects.link(tmp_a)
    bpy.context.collection.objects.link(tmp_b)

    mod = tmp_a.modifiers.new(name="Overlap", type='BOOLEAN')
    mod.operation = 'INTERSECT'
    mod.object = tmp_b
    mod.solver = 'EXACT'

    depsgraph = bpy.context.evaluated_depsgraph_get()
    eval_obj = tmp_a.evaluated_get(depsgraph)
    eval_mesh = eval_obj.to_mesh()

    bm_result = bmesh.new()
    bm_result.from_mesh(eval_mesh)
    volume = abs(bm_result.calc_volume()) if bm_result.faces else 0.0
    verts_world = [v.co.copy() for v in bm_result.verts]
    centroid = Vector((0.0, 0.0, 0.0))
    if verts_world:
        for co in verts_world:
            centroid += co
        centroid /= len(verts_world)
    bm_result.free()
    eval_obj.to_mesh_clear()

    bpy.data.objects.remove(tmp_a, do_unlink=True)
    bpy.data.objects.remove(tmp_b, do_unlink=True)
    bpy.data.meshes.remove(mesh_a)
    bpy.data.meshes.remove(mesh_b)

    return volume, centroid, verts_world


def _overlap_seam_axis(verts_world, centroid):
    """PCA of the overlap wedge's vertices: returns the seam LINE direction
    (the wedge's longest axis — it runs along the shared edge where the two
    panels meet) and the plane NORMAL to bisect the wedge across its short
    axis (second-longest axis, the direction the wedge is thin along)."""
    import numpy as np
    pts = np.array([[v.x, v.y, v.z] for v in verts_world])
    if len(pts) < 3:
        return Vector((0, 0, 1)), Vector((1, 0, 0))
    c = np.array([centroid.x, centroid.y, centroid.z])
    centered = pts - c
    cov = centered.T @ centered
    eigvals, eigvecs = np.linalg.eigh(cov)
    # eigh returns ascending eigenvalues: [smallest, mid, largest]
    seam_dir = Vector(eigvecs[:, 2])       # largest variance = along the seam line
    plane_no = Vector(eigvecs[:, 0])       # smallest variance = across the thin wedge
    return seam_dir.normalized(), plane_no.normalized()


def _world_centroid(obj):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.transform(obj.matrix_world)
    centroid = Vector((0.0, 0.0, 0.0))
    if bm.verts:
        for v in bm.verts:
            centroid += v.co
        centroid /= len(bm.verts)
    bm.free()
    return centroid


def _miter_plane(obj_a, obj_b, overlap_centroid, overlap_verts):
    """Miter plane derived from the overlap wedge itself via PCA: the plane
    passes through the overlap centroid (the seam) and its normal points
    across the wedge's short axis, i.e. it cuts the wedge in half along its
    length. Also returns each object's own world-space centroid, used to
    decide which side of the plane belongs to which panel."""
    seam_dir, plane_no = _overlap_seam_axis(overlap_verts, overlap_centroid)
    centroid_a = _world_centroid(obj_a)
    centroid_b = _world_centroid(obj_b)
    return overlap_centroid, plane_no, centroid_a, centroid_b


def _plane_quad_lines(plane_co, plane_no, half_size=0.3):
    """Line segments outlining a square patch of the plane, for debug draw."""
    up = Vector((0.0, 0.0, 1.0))
    if abs(plane_no.dot(up)) > 0.95:
        up = Vector((1.0, 0.0, 0.0))
    tangent_u = plane_no.cross(up).normalized()
    tangent_v = plane_no.cross(tangent_u).normalized()
    corners = [
        plane_co + tangent_u * half_size + tangent_v * half_size,
        plane_co - tangent_u * half_size + tangent_v * half_size,
        plane_co - tangent_u * half_size - tangent_v * half_size,
        plane_co + tangent_u * half_size - tangent_v * half_size,
    ]
    lines = [(corners[i], corners[(i + 1) % 4]) for i in range(4)]
    # Cross through the middle so the plane's facing is visually obvious
    lines.append((plane_co - tangent_u * half_size, plane_co + tangent_u * half_size))
    lines.append((plane_co - tangent_v * half_size, plane_co + tangent_v * half_size))
    # Normal stub so orientation (which way "outer" is) is visible
    lines.append((plane_co, plane_co + plane_no * (half_size * 0.5)))
    return lines


def _bisect_clear_side(obj, plane_co_world, plane_no_world, keep_towards_world):
    """Cut obj's mesh with the world-space plane and delete geometry on the
    side that does NOT contain keep_towards_world (a point known to lie in
    this object's own bulk, e.g. its world-space centroid) — i.e. clear the
    half that encroaches on the neighbouring panel."""
    mat = obj.matrix_world
    mat_inv = mat.inverted()
    rot_inv = mat.to_3x3().inverted()

    plane_co_local = mat_inv @ plane_co_world
    plane_no_local = (rot_inv @ plane_no_world).normalized()
    keep_point_local = mat_inv @ keep_towards_world

    # bisect_plane's clear_inner removes verts on the -normal side (the side
    # plane_no points away from). We want to KEEP the side containing
    # keep_point_local, so if that point is on the -normal side, flip the
    # plane normal so clear_inner removes the other (unwanted) side instead.
    to_point = keep_point_local - plane_co_local
    if plane_no_local.dot(to_point) < 0:
        plane_no_local = -plane_no_local

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()

    geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
    bmesh.ops.bisect_plane(
        bm,
        geom=geom,
        plane_co=plane_co_local,
        plane_no=plane_no_local,
        clear_outer=False,
        clear_inner=True,
    )
    bmesh.ops.holes_fill(bm, edges=[e for e in bm.edges if e.is_boundary])

    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()


def _find_overlapping_pairs(meshes, bbox_margin, min_overlap_volume):
    bboxes = {obj.name: _bbox_world(obj) for obj in meshes}
    pairs = []
    for i in range(len(meshes)):
        for j in range(i + 1, len(meshes)):
            a, b = meshes[i], meshes[j]
            if not _bbox_overlaps(bboxes[a.name], bboxes[b.name], bbox_margin):
                continue
            vol, centroid, verts = _intersect_solids(a, b)
            _log(f"pair {a.name} / {b.name}: overlap_volume={vol:.8f}")
            if vol > min_overlap_volume:
                pairs.append((a, b, vol, centroid, verts))
    return pairs


class OBJECT_OT_preview_miter_plane(bpy.types.Operator):
    bl_idname = "object.preview_miter_plane"
    bl_label = "Preview Miter Plane"
    bl_options = {'UNDO'}

    bbox_margin: bpy.props.FloatProperty(
        name="Bbox Margin",
        default=0.01,
        min=0.0,
        max=1.0,
        precision=4,
    )

    min_overlap_volume: bpy.props.FloatProperty(
        name="Min Overlap Volume",
        default=1e-9,
        min=0.0,
        max=1.0,
        precision=10,
    )

    @classmethod
    def poll(cls, context):
        return (context.mode == 'OBJECT' and
                len([o for o in context.selected_objects if o.type == 'MESH']) >= 2)

    def execute(self, context):
        meshes = [o for o in context.selected_objects if o.type == 'MESH']
        pairs = _find_overlapping_pairs(meshes, self.bbox_margin, self.min_overlap_volume)

        plane_lines = []
        keep_a_lines = []
        keep_b_lines = []
        overlap_points = []
        centroid_a_points = []
        centroid_b_points = []
        lines_log = []

        for a, b, vol, centroid, verts in pairs:
            plane_co, plane_no, centroid_a, centroid_b = _miter_plane(a, b, centroid, verts)
            plane_lines.extend(_plane_quad_lines(plane_co, plane_no))
            overlap_points.append(centroid)
            centroid_a_points.append(centroid_a)
            centroid_b_points.append(centroid_b)
            # Stub line from plane centre toward each object's own centroid —
            # this is the side _bisect_clear_side will KEEP for that object.
            keep_a_lines.append((plane_co, centroid_a))
            keep_b_lines.append((plane_co, centroid_b))
            lines_log.append(
                f"{a.name} <-> {b.name}: overlap_vol={vol:.6f} "
                f"plane_co={tuple(round(c,4) for c in plane_co)} "
                f"plane_no={tuple(round(c,4) for c in plane_no)} "
                f"centroid_a={tuple(round(c,4) for c in centroid_a)} "
                f"centroid_b={tuple(round(c,4) for c in centroid_b)}"
            )

        bpy.app.driver_namespace['miter_debug_draw'] = {
            'plane_lines': plane_lines,
            'keep_a_lines': keep_a_lines,
            'keep_b_lines': keep_b_lines,
            'overlap_points': overlap_points,
            'centroid_a_points': centroid_a_points,
            'centroid_b_points': centroid_b_points,
        }
        _install_draw_handler()
        for area in context.screen.areas:
            if area.type == 'VIEW_3D':
                area.tag_redraw()

        _log("=== Preview Miter Plane ===\n" + "\n".join(lines_log))
        self.report({'INFO'}, f"Previewing {len(pairs)} pair(s) — blue=plane, red=overlap centroid, green line=A's keep side, orange line=B's keep side")
        return {'FINISHED'}


class OBJECT_OT_clear_miter_debug(bpy.types.Operator):
    bl_idname = "object.clear_miter_debug"
    bl_label = "Clear Miter Debug"

    def execute(self, context):
        _clear_debug_draw(context)
        return {'FINISHED'}


class OBJECT_OT_detect_miter_seams(bpy.types.Operator):
    bl_idname = "object.detect_miter_seams"
    bl_label = "Detect Overlapping Seams"
    bl_options = {'UNDO'}

    bbox_margin: bpy.props.FloatProperty(
        name="Bbox Margin",
        description="Broad-phase bounding box padding, metres",
        default=0.01,
        min=0.0,
        max=1.0,
        precision=4,
    )

    min_overlap_volume: bpy.props.FloatProperty(
        name="Min Overlap Volume",
        description="Ignore pairs whose intersection volume is below this (coincident faces with no real interpenetration)",
        default=1e-9,
        min=0.0,
        max=1.0,
        precision=10,
    )

    @classmethod
    def poll(cls, context):
        return (context.mode == 'OBJECT' and
                len([o for o in context.selected_objects if o.type == 'MESH']) >= 2)

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def draw(self, context):
        self.layout.prop(self, "bbox_margin")
        self.layout.prop(self, "min_overlap_volume")

    def execute(self, context):
        meshes = [o for o in context.selected_objects if o.type == 'MESH']
        pairs = _find_overlapping_pairs(meshes, self.bbox_margin, self.min_overlap_volume)

        bpy.ops.object.select_all(action='DESELECT')
        seen = set()
        for a, b, vol, centroid, verts in pairs:
            a.select_set(True)
            b.select_set(True)
            seen.add(a.name)
            seen.add(b.name)
        if pairs:
            context.view_layer.objects.active = bpy.data.objects[next(iter(seen))]

        lines = [f"{a.name}  <->  {b.name}   overlap_vol={vol:.6f}" for a, b, vol, centroid, verts in pairs]
        report = "\n".join(lines) if lines else "No overlapping seams found"
        _log("=== Detect Miter Seams ===\n" + report)

        context.scene["miter_seam_pairs"] = [(a.name, b.name) for a, b, _, _, _ in pairs]

        self.report({'INFO'}, f"Found {len(pairs)} overlapping pair(s) — see sidebar / log")

        def draw_popup(self2, ctx2):
            for line in lines[:30]:
                self2.layout.label(text=line)
            if len(lines) > 30:
                self2.layout.label(text=f"... and {len(lines) - 30} more")
        context.window_manager.popup_menu(draw_popup, title="Miter Seam Detection", icon='INFO')
        return {'FINISHED'}


class OBJECT_OT_fix_miter_seams(bpy.types.Operator):
    bl_idname = "object.fix_miter_seams"
    bl_label = "Fix Overlapping Seams (Miter Bisect)"
    bl_options = {'REGISTER', 'UNDO'}

    bbox_margin: bpy.props.FloatProperty(
        name="Bbox Margin",
        default=0.01,
        min=0.0,
        max=1.0,
        precision=4,
    )

    min_overlap_volume: bpy.props.FloatProperty(
        name="Min Overlap Volume",
        default=1e-9,
        min=0.0,
        max=1.0,
        precision=10,
    )

    @classmethod
    def poll(cls, context):
        return (context.mode == 'OBJECT' and
                len([o for o in context.selected_objects if o.type == 'MESH']) >= 2)

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def draw(self, context):
        self.layout.prop(self, "bbox_margin")
        self.layout.prop(self, "min_overlap_volume")

    def execute(self, context):
        meshes = [o for o in context.selected_objects if o.type == 'MESH']
        pairs = _find_overlapping_pairs(meshes, self.bbox_margin, self.min_overlap_volume)

        if not pairs:
            self.report({'INFO'}, "No overlapping seams found — nothing to fix")
            return {'FINISHED'}

        fixed = 0
        skipped = []
        for a, b, vol, centroid, verts in pairs:
            # Compute the miter plane from the overlap wedge's own geometry
            # (PCA: normal = wedge's short axis), positioned at the overlap
            # centroid (the seam). Each object keeps its own identity — we
            # bisect each one independently with the same plane, clearing
            # the half that encroaches on the other panel. No boolean
            # solver, no join.
            try:
                plane_co, plane_no, centroid_a, centroid_b = _miter_plane(a, b, centroid, verts)
                _bisect_clear_side(a, plane_co, plane_no, centroid_a)
                _bisect_clear_side(b, plane_co, plane_no, centroid_b)
            except Exception as e:
                skipped.append(f"{a.name} / {b.name}: {e}")
                continue
            fixed += 1
            _log(f"Fixed seam {a.name} <-> {b.name} (overlap_vol={vol:.8f}, plane_co={plane_co}, plane_no={plane_no})")

        msg = f"Fixed {fixed} seam(s)"
        if skipped:
            msg += f", {len(skipped)} failure(s) — see log"
            _log("=== Fix Miter Seams skipped ===\n" + "\n".join(skipped))
        self.report({'INFO'} if not skipped else {'WARNING'}, msg)
        return {'FINISHED'}


class VIEW3D_PT_miter_seam_fix(bpy.types.Panel):
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = 'ShipBreaker'
    bl_label = 'Miter Seam Fix'
    bl_options = {'DEFAULT_CLOSED'}

    def draw(self, context):
        layout = self.layout
        selected = [o for o in context.selected_objects if o.type == 'MESH']
        layout.label(text=f"Selected meshes: {len(selected)}")
        layout.operator(OBJECT_OT_detect_miter_seams.bl_idname,
                         text="Detect Overlapping Seams", icon='VIEWZOOM')
        layout.operator(OBJECT_OT_preview_miter_plane.bl_idname,
                         text="Preview Miter Plane", icon='EMPTY_AXIS')
        layout.operator(OBJECT_OT_clear_miter_debug.bl_idname,
                         text="Clear Preview", icon='X')
        layout.separator()
        layout.operator(OBJECT_OT_fix_miter_seams.bl_idname,
                         text="Fix Overlapping Seams", icon='MOD_BOOLEAN')


_CLASSES = (
    OBJECT_OT_detect_miter_seams,
    OBJECT_OT_preview_miter_plane,
    OBJECT_OT_clear_miter_debug,
    OBJECT_OT_fix_miter_seams,
    VIEW3D_PT_miter_seam_fix,
)


def register():
    _install_draw_handler()
    for cls in _CLASSES:
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)


def unregister():
    _clear_debug_draw()
    for cls in _CLASSES:
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
