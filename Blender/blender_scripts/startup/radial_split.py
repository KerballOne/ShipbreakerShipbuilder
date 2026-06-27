import bpy
import bmesh
import blf
import gpu
import math
import re
from gpu_extras.batch import batch_for_shader
from mathutils import Vector, Matrix

_LOG = r"C:\Users\user\AppData\Local\Temp\blender_reload.log"

def _log(msg):
    import datetime
    with open(_LOG, "a", encoding="utf-8") as f:
        f.write(f"{datetime.datetime.now()}: [radial_split] {msg}\n")


bl_info = {
    "name": "Radial Split",
    "author": "KerballOne",
    "version": (1, 2),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Split selected object into radial segments — fixed angle or seam-detect mode",
}

_THIS_FILE = __file__

COLOR_SEAM = (1.0, 0.85, 0.2, 0.28)


# ------------------------------------------------------------------
# Seam detection (used by Seam Detect mode)

def _analyse_mesh(obj, coplanar_deg=2.0):
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    bm.edges.ensure_lookup_table()

    mx = obj.matrix_world
    for v in bm.verts:
        v.co = mx @ v.co
    bm.normal_update()

    face_normals = [f.normal.copy() for f in bm.faces]

    coplanar_thresh = math.cos(math.radians(coplanar_deg))
    visited = set()
    groups = []
    face_to_group = {}

    for start in bm.faces:
        if start.index in visited:
            continue
        group = set()
        stack = [start]
        while stack:
            f = stack.pop()
            if f.index in visited:
                continue
            visited.add(f.index)
            group.add(f.index)
            for edge in f.edges:
                for linked in edge.link_faces:
                    if linked.index not in visited:
                        if face_normals[f.index].dot(face_normals[linked.index]) >= coplanar_thresh:
                            stack.append(linked)
        g_idx = len(groups)
        groups.append(group)
        for fi in group:
            face_to_group[fi] = g_idx

    def group_area(g):
        return sum(bm.faces[fi].calc_area() for fi in g)

    def group_normal(g):
        n = Vector((0, 0, 0))
        for fi in g:
            n += face_normals[fi] * bm.faces[fi].calc_area()
        return n.normalized() if n.length > 1e-6 else Vector((0, 0, 1))

    g_areas   = [group_area(g)   for g in groups]
    g_normals = [group_normal(g) for g in groups]

    pair_edges = {}
    for edge in bm.edges:
        if len(edge.link_faces) != 2:
            continue
        fa, fb = edge.link_faces[0], edge.link_faces[1]
        ga = face_to_group.get(fa.index, -1)
        gb = face_to_group.get(fb.index, -1)
        if ga < 0 or gb < 0 or ga == gb:
            continue
        key = (min(ga, gb), max(ga, gb))
        seg = (edge.verts[0].co.copy(), edge.verts[1].co.copy())
        pair_edges.setdefault(key, []).append(seg)

    candidates = []
    for (ga, gb), segs in pair_edges.items():
        na = g_normals[ga]
        nb = g_normals[gb]
        dot = max(-1.0, min(1.0, na.dot(nb)))
        bisector = na + nb
        if bisector.length < 1e-6:
            continue
        bisector.normalize()
        all_verts = [v for seg in segs for v in seg]
        plane_co = sum(all_verts, Vector((0, 0, 0))) / len(all_verts)
        candidates.append({
            'area_a':   g_areas[ga],
            'area_b':   g_areas[gb],
            'dot':      dot,
            'plane_co': plane_co,
            'plane_no': bisector,
        })

    bm.free()
    return candidates


# ------------------------------------------------------------------
# Radial Split (Seam Detect) — interactive modal

class MESH_OT_radial_split_seam(bpy.types.Operator):
    bl_idname  = "mesh.radial_split_seam"
    bl_label   = "Radial Split (Seam Detect)"
    bl_description = (
        "Auto-detect concave seams and preview split planes interactively. "
        "Drag=face size  Q/E=angle  R=axis  F=fill  Enter=confirm  RMB/Esc=cancel"
    )
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return context.active_object is not None and context.active_object.type == 'MESH'

    def invoke(self, context, event):
        obj = context.active_object
        if obj is None or obj.type != 'MESH':
            self.report({'ERROR'}, "Select a mesh object first")
            return {'CANCELLED'}
        if not obj.data.vertices:
            self.report({'ERROR'}, f"'{obj.name}' has no vertices")
            return {'CANCELLED'}

        self._obj      = obj
        self._obj_name = obj.name

        self._min_face_area = 10.0
        self._angle_deg     = 15.0
        self._fill_cut      = True
        self._axis_idx      = 2

        self._candidates = _analyse_mesh(obj)
        self._splits     = []
        self._line_batch = None
        self._shader     = gpu.shader.from_builtin('UNIFORM_COLOR')

        self._dragging        = False
        self._drag_start_y    = event.mouse_region_y
        self._drag_start_area = self._min_face_area

        self._rebuild_batches()

        self._handle = bpy.types.SpaceView3D.draw_handler_add(
            self._draw_callback, (context,), 'WINDOW', 'POST_VIEW'
        )
        self._hud_handle = bpy.types.SpaceView3D.draw_handler_add(
            self._draw_hud, (context,), 'WINDOW', 'POST_PIXEL'
        )
        context.window_manager.modal_handler_add(self)
        context.area.tag_redraw()
        return {'RUNNING_MODAL'}

    def modal(self, context, event):
        if event.type in {'RIGHTMOUSE', 'ESC'}:
            return self._finish(context, cancelled=True)

        if event.type in {'NUMPAD_ENTER', 'RET'} and event.value == 'PRESS':
            return self._finish(context, cancelled=False)

        if event.type == 'R' and event.value == 'PRESS':
            self._axis_idx = (self._axis_idx + 1) % 3
            self._rebuild_batches()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'F' and event.value == 'PRESS':
            self._fill_cut = not self._fill_cut
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'Q' and event.value == 'PRESS':
            self._angle_deg = max(1.0, self._angle_deg - 1.0)
            self._rebuild_batches()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'E' and event.value == 'PRESS':
            self._angle_deg = min(179.0, self._angle_deg + 1.0)
            self._rebuild_batches()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'LEFTMOUSE' and event.value == 'PRESS':
            self._dragging        = True
            self._drag_start_y    = event.mouse_region_y
            self._drag_start_area = self._min_face_area
            return {'RUNNING_MODAL'}

        if event.type == 'LEFTMOUSE' and event.value == 'RELEASE':
            self._dragging = False
            return {'RUNNING_MODAL'}

        if event.type == 'MOUSEMOVE' and self._dragging:
            region = context.region
            dy = event.mouse_region_y - self._drag_start_y
            travel = (dy / region.height) * 50.0
            new_a = max(0.0, self._drag_start_area + travel)
            if abs(new_a - self._min_face_area) > 1e-4:
                self._min_face_area = new_a
                self._rebuild_batches()
                context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'WHEELUPMOUSE':
            self._min_face_area = max(0.0, self._min_face_area + 0.5)
            self._rebuild_batches()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'WHEELDOWNMOUSE':
            self._min_face_area = max(0.0, self._min_face_area - 0.5)
            self._rebuild_batches()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        return {'PASS_THROUGH'}

    def _rebuild_batches(self):
        obj = self._obj
        coords = [obj.matrix_world @ Vector(v.co) for v in obj.data.vertices]
        ax_min = min(c[self._axis_idx] for c in coords)
        ax_max = max(c[self._axis_idx] for c in coords)
        bbox_center = Vector([
            (min(c[i] for c in coords) + max(c[i] for c in coords)) / 2.0
            for i in range(3)
        ])
        radius = max(
            math.sqrt(sum((c[i] - bbox_center[i])**2 for i in range(3) if i != self._axis_idx))
            for c in coords
        )

        angle_thresh_cos = math.cos(math.radians(self._angle_deg))

        raw_splits = [
            c for c in self._candidates
            if c['area_a'] >= self._min_face_area
            and c['area_b'] >= self._min_face_area
            and c['dot'] < angle_thresh_cos
            and max(c['area_a'], c['area_b']) / min(c['area_a'], c['area_b']) < 2.0
        ]

        ax = self._axis_idx
        other = [i for i in range(3) if i != ax]
        a0, a1 = other
        bc = bbox_center

        def radial_angle(c):
            p = c['plane_co']
            return math.atan2(p[a1] - bc[a1], p[a0] - bc[a0])

        dedup_rad = math.radians(20.0)
        sorted_splits = sorted(raw_splits, key=radial_angle)
        kept_angles = []
        self._splits = []
        for c in sorted_splits:
            ang = radial_angle(c)
            duplicate = any(
                (abs(ang - ka) if abs(ang - ka) <= math.pi else 2 * math.pi - abs(ang - ka)) < dedup_rad
                for ka in kept_angles
            )
            if not duplicate:
                kept_angles.append(ang)
                self._splits.append(c)

        tri_verts = []
        for s in self._splits:
            plane_no = s['plane_co'].copy()
            plane_no[self._axis_idx] = 0.0
            plane_no -= Vector([bbox_center[i] if i != self._axis_idx else 0.0 for i in range(3)])
            if plane_no.length < 1e-6:
                continue
            plane_no.normalize()

            lo_pt = bbox_center.copy(); lo_pt[self._axis_idx] = ax_min
            hi_pt = bbox_center.copy(); hi_pt[self._axis_idx] = ax_max

            p0 = tuple(lo_pt - plane_no * radius)
            p1 = tuple(lo_pt + plane_no * radius)
            p2 = tuple(hi_pt + plane_no * radius)
            p3 = tuple(hi_pt - plane_no * radius)

            tri_verts += [p0, p1, p2, p0, p2, p3]
            tri_verts += [p0, p2, p1, p0, p3, p2]

        self._line_batch = batch_for_shader(
            self._shader, 'TRIS', {"pos": tri_verts}
        ) if tri_verts else None

    def _draw_callback(self, context):
        if not self._line_batch:
            return
        shader = self._shader
        gpu.state.blend_set('ALPHA')
        gpu.state.depth_test_set('ALWAYS')
        gpu.state.face_culling_set('NONE')
        shader.bind()
        shader.uniform_float("color", COLOR_SEAM)
        self._line_batch.draw(shader)
        gpu.state.face_culling_set('BACK')
        gpu.state.blend_set('NONE')
        gpu.state.depth_test_set('LESS_EQUAL')

    def _draw_hud(self, context):
        axis_name = ('X', 'Y', 'Z')[self._axis_idx]
        fill_str  = "ON" if self._fill_cut else "OFF"
        line1 = (f"Radial Split (Seam Detect)  |  Axis: {axis_name}  |  "
                 f"Min Face: {self._min_face_area:.2f} m²  |  "
                 f"Angle: {self._angle_deg:.1f}°  |  "
                 f"Splits: {len(self._splits)}  |  Fill: {fill_str}")
        line2 = "Drag=face size  Scroll=fine  Q/E=angle  R=axis  F=fill  Enter=confirm  RMB/Esc=cancel"
        font_id = 0
        blf.size(font_id, 14)
        blf.color(font_id, 1.0, 1.0, 1.0, 1.0)
        blf.position(font_id, 20, 50, 0)
        blf.draw(font_id, line1)
        blf.size(font_id, 11)
        blf.color(font_id, 0.7, 0.7, 0.7, 1.0)
        blf.position(font_id, 20, 32, 0)
        blf.draw(font_id, line2)

    def _finish(self, context, cancelled):
        bpy.types.SpaceView3D.draw_handler_remove(self._handle, 'WINDOW')
        bpy.types.SpaceView3D.draw_handler_remove(self._hud_handle, 'WINDOW')
        context.area.tag_redraw()
        if cancelled:
            return {'CANCELLED'}
        if not self._splits:
            self.report({'WARNING'}, "No splits detected — adjust face size or angle threshold")
            return {'CANCELLED'}

        obj = self._obj
        obj_coords = [obj.matrix_world @ Vector(v.co) for v in obj.data.vertices]
        bbox_center = Vector([
            (min(c[i] for c in obj_coords) + max(c[i] for c in obj_coords)) / 2.0
            for i in range(3)
        ])

        ax = self._axis_idx
        other = [i for i in range(3) if i != ax]
        a0, a1 = other

        cut_planes = []
        for s in self._splits:
            pn = s['plane_co'].copy()
            pn[ax] = 0.0
            pn -= Vector([bbox_center[i] if i != ax else 0.0 for i in range(3)])
            if pn.length < 1e-6:
                continue
            pn.normalize()
            angle = math.atan2(pn[a1], pn[a0])
            cut_planes.append((angle, pn))

        if not cut_planes:
            self.report({'WARNING'}, "No valid cut planes")
            return {'CANCELLED'}

        cut_planes.sort(key=lambda x: x[0])
        n = len(cut_planes)

        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        context.view_layer.objects.active = obj
        bpy.ops.object.duplicate(linked=False)
        dup = context.active_object
        local_co = dup.matrix_world.inverted() @ bbox_center

        bpy.ops.object.mode_set(mode='EDIT')
        for _, pn in cut_planes:
            bpy.ops.mesh.select_all(action='SELECT')
            bpy.ops.mesh.bisect(
                plane_co=local_co,
                plane_no=pn,
                use_fill=False,
                clear_inner=False,
                clear_outer=False,
                threshold=0.0001,
            )
        bpy.ops.object.mode_set(mode='OBJECT')

        mesh = dup.data
        bm = bmesh.new()
        bm.from_mesh(mesh)
        sl = bm.faces.layers.int.new("_sector")
        cut_angles = [a for a, _ in cut_planes]
        mx = dup.matrix_world

        for face in bm.faces:
            cw = mx @ face.calc_center_median()
            ang = math.atan2(cw[a1] - bbox_center[a1], cw[a0] - bbox_center[a0])
            sector = n - 1
            for i in range(n):
                a_lo = cut_angles[i]
                a_hi = cut_angles[(i + 1) % n]
                if a_hi <= a_lo:
                    a_hi += 2 * math.pi
                a_test = ang
                if a_test < a_lo:
                    a_test += 2 * math.pi
                if a_lo <= a_test < a_hi:
                    sector = i
                    break
            face[sl] = sector

        bm.to_mesh(mesh)
        bm.free()
        mesh.update()

        base_name = re.sub(r'_\d+$', '', self._obj_name).split(".")[0]
        col_name = base_name
        seg_col = bpy.data.collections.get(col_name)
        if seg_col is None:
            seg_col = bpy.data.collections.new(col_name)
        parent_col = next(
            (c for c in bpy.data.collections if obj.name in c.objects),
            context.scene.collection,
        )
        if col_name not in [c.name for c in parent_col.children]:
            parent_col.children.link(seg_col)

        results = []
        current = dup

        for seg_idx in range(n - 1):
            bpy.ops.object.select_all(action='DESELECT')
            current.select_set(True)
            context.view_layer.objects.active = current

            bpy.ops.object.mode_set(mode='EDIT')
            bpy.ops.mesh.select_all(action='DESELECT')
            bm2 = bmesh.from_edit_mesh(current.data)
            sl2 = bm2.faces.layers.int.get("_sector")
            if sl2:
                for face in bm2.faces:
                    face.select = (face[sl2] == seg_idx)
            bmesh.update_edit_mesh(current.data)
            bpy.ops.mesh.separate(type='SELECTED')
            bpy.ops.object.mode_set(mode='OBJECT')

            pieces = [o for o in context.selected_objects if o is not current and o.type == 'MESH']
            for piece in pieces:
                piece.name = f"{base_name}_seg{seg_idx+1:02d}"
                if piece.data:
                    piece.data.name = piece.name
                for c in list(piece.users_collection):
                    c.objects.unlink(piece)
                seg_col.objects.link(piece)
                results.append(piece)

        current.name = f"{base_name}_seg{n:02d}"
        if current.data:
            current.data.name = current.name
        for c in list(current.users_collection):
            c.objects.unlink(current)
        seg_col.objects.link(current)
        results.append(current)

        if self._fill_cut:
            for seg in results:
                bm3 = bmesh.new()
                bm3.from_mesh(seg.data)
                bm3.edges.ensure_lookup_table()
                boundary = [e for e in bm3.edges if e.is_boundary]
                if boundary:
                    bmesh.ops.holes_fill(bm3, edges=boundary, sides=0)
                bmesh.ops.recalc_face_normals(bm3, faces=bm3.faces)
                bm3.to_mesh(seg.data)
                bm3.free()
                seg.data.update()
                seg.update_tag()
            context.view_layer.update()

        obj.hide_set(True)
        bpy.ops.object.select_all(action='DESELECT')
        for seg in results:
            seg.select_set(True)
        if results:
            context.view_layer.objects.active = results[0]

        self.report({'INFO'}, f"Split into {len(results)} segment(s) → collection '{col_name}'")
        return {'FINISHED'}


# ------------------------------------------------------------------
# Radial Split (Fixed Angle) — dialog operator

class MESH_OT_radial_split(bpy.types.Operator):
    bl_idname = "mesh.radial_split"
    bl_label = "Radial Split (Fixed Angle)"
    bl_description = "Split the selected mesh into N equal radial pie-slice segments around a chosen axis."
    bl_options = {'REGISTER', 'UNDO'}

    segments: bpy.props.IntProperty(
        name="Segments",
        default=8, min=2, max=64,
    )
    axis: bpy.props.EnumProperty(
        name="Axis",
        items=[
            ('X', "X", "Split around local X axis"),
            ('Y', "Y", "Split around local Y axis"),
            ('Z', "Z", "Split around local Z axis"),
        ],
        default='Y',
    )
    offset_deg: bpy.props.FloatProperty(
        name="Angle Offset",
        default=0.0, min=-180.0, max=180.0,
    )

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "segments")
        layout.prop(self, "axis")
        layout.prop(self, "offset_deg")

    def execute(self, context):
        original = context.active_object
        if original is None or original.type != 'MESH':
            self.report({'ERROR'}, "Select a mesh object first")
            return {'CANCELLED'}

        n = self.segments
        angle_step = (2 * math.pi) / n
        offset_rad = math.radians(self.offset_deg)

        axis_map = {'X': Vector((1,0,0)), 'Y': Vector((0,1,0)), 'Z': Vector((0,0,1))}
        ref_map  = {'X': Vector((0,1,0)), 'Y': Vector((1,0,0)), 'Z': Vector((1,0,0))}
        up  = axis_map[self.axis]
        ref = ref_map[self.axis]

        boundary_normals = []
        for i in range(n):
            angle = offset_rad + i * angle_step
            rot = Matrix.Rotation(angle, 4, up)
            boundary_normals.append(rot @ ref)

        base_name = original.name.split(".")[0]
        col_name = base_name
        seg_collection = bpy.data.collections.get(col_name)
        if seg_collection is None:
            seg_collection = bpy.data.collections.new(col_name)
        parent_col = next(
            (c for c in bpy.data.collections if original.name in c.objects),
            context.scene.collection
        )
        if col_name not in parent_col.children:
            parent_col.children.link(seg_collection)

        results = []
        for seg in range(n):
            bpy.ops.object.select_all(action='DESELECT')
            original.select_set(True)
            context.view_layer.objects.active = original
            bpy.ops.object.duplicate(linked=False)
            dup = context.active_object

            n1 = boundary_normals[seg]
            n2 = boundary_normals[(seg + 1) % n]
            self._bisect(dup, n1, clear_inner=True,  clear_outer=False)
            self._bisect(dup, n2, clear_inner=False, clear_outer=True)

            if self._has_geometry(dup):
                dup.name = f"{base_name}_seg{seg+1:02d}"
                if dup.data:
                    dup.data.name = dup.name
                for c in list(dup.users_collection):
                    c.objects.unlink(dup)
                seg_collection.objects.link(dup)
                results.append(dup)
            else:
                bpy.data.objects.remove(dup, do_unlink=True)

        for seg in results:
            bm = bmesh.new()
            bm.from_mesh(seg.data)
            bm.edges.ensure_lookup_table()
            boundary = [e for e in bm.edges if e.is_boundary]
            if boundary:
                bmesh.ops.holes_fill(bm, edges=boundary, sides=0)
            bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
            bm.to_mesh(seg.data)
            bm.free()
            seg.data.update()

        original.hide_set(True)
        bpy.ops.object.select_all(action='DESELECT')
        for o in results:
            o.select_set(True)
        if results:
            context.view_layer.objects.active = results[0]

        self.report({'INFO'}, f"Split into {len(results)} segments")
        return {'FINISHED'}

    def _bisect(self, obj, normal, clear_inner, clear_outer):
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.mesh.select_all(action='SELECT')
        bpy.ops.mesh.bisect(
            plane_co=(0.0, 0.0, 0.0),
            plane_no=normal,
            use_fill=False,
            clear_inner=clear_inner,
            clear_outer=clear_outer,
            threshold=0.0001,
        )
        bpy.ops.object.mode_set(mode='OBJECT')

    def _has_geometry(self, obj):
        return obj and obj.data and len(obj.data.vertices) > 0


# ------------------------------------------------------------------

def menu_func(self, context):
    self.layout.operator(MESH_OT_radial_split_seam.bl_idname, icon='VIEWZOOM')
    self.layout.operator(MESH_OT_radial_split.bl_idname, icon='MOD_ARRAY')


def register():
    for cls in (MESH_OT_radial_split_seam, MESH_OT_radial_split):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)

    if hasattr(bpy.types.VIEW3D_MT_object, '_dyn_ui_initialize'):
        for fn in list(bpy.types.VIEW3D_MT_object._dyn_ui_initialize()):
            code = getattr(getattr(fn, '__func__', fn), '__code__', None)
            if code and code.co_filename == _THIS_FILE:
                try:
                    bpy.types.VIEW3D_MT_object.remove(fn)
                except Exception:
                    pass

    bpy.types.VIEW3D_MT_object.append(menu_func)
    _log("register: done")


def unregister():
    for cls in (MESH_OT_radial_split_seam, MESH_OT_radial_split):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass

    if hasattr(bpy.types.VIEW3D_MT_object, '_dyn_ui_initialize'):
        for fn in list(bpy.types.VIEW3D_MT_object._dyn_ui_initialize()):
            code = getattr(getattr(fn, '__func__', fn), '__code__', None)
            if code and code.co_filename == _THIS_FILE:
                try:
                    bpy.types.VIEW3D_MT_object.remove(fn)
                except Exception:
                    pass
