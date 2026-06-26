import bpy
import bmesh
import blf
import gpu
import math
import re
from gpu_extras.batch import batch_for_shader
from mathutils import Vector

bl_info = {
    "name": "Concavity Split",
    "author": "KerballOne",
    "version": (1, 3),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Detect large faces meeting at concave angles and split between them",
}

_THIS_FILE = __file__

COLOR_SEAM = (1.0, 0.85, 0.2, 0.28)   # yellow cut face preview


# ------------------------------------------------------------------
# One-time mesh analysis (called at invoke only)

def _analyse_mesh(obj, coplanar_deg=2.0):
    """
    Build a bmesh in world space, group nearly-coplanar faces, then find ALL
    adjacent group pairs and record their geometry.  Returns a list of candidate
    dicts — cheap to filter by area/angle threshold later.

    Each candidate:
      area_a, area_b   : float  — group areas
      normal_a, normal_b: Vector — area-weighted group normals
      dot              : float  — na.dot(nb)
      plane_co         : Vector — centroid of the shared boundary verts
      plane_no         : Vector — bisector of the two group normals
      seam_lines       : list of (Vector, Vector) — world-space line segments
                         along the shared boundary for display
    """
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    bm.edges.ensure_lookup_table()

    mx = obj.matrix_world
    for v in bm.verts:
        v.co = mx @ v.co
    bm.normal_update()

    face_normals = [f.normal.copy() for f in bm.faces]

    # --- coplanar flood-fill grouping ---
    coplanar_thresh = math.cos(math.radians(coplanar_deg))
    visited = set()
    groups = []          # list of sets of face indices
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

    # --- per-group area and normal ---
    def group_area(g):
        return sum(bm.faces[fi].calc_area() for fi in g)

    def group_normal(g):
        n = Vector((0, 0, 0))
        for fi in g:
            n += face_normals[fi] * bm.faces[fi].calc_area()
        return n.normalized() if n.length > 1e-6 else Vector((0, 0, 1))

    g_areas   = [group_area(g)   for g in groups]
    g_normals = [group_normal(g) for g in groups]

    # --- find all adjacent group pairs ---
    # For each pair collect every shared boundary edge as a line segment
    pair_edges = {}   # (ga, gb) -> list of (co_a, co_b)

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

        # plane_co = centroid of all boundary verts
        all_verts = [v for seg in segs for v in seg]
        plane_co = sum(all_verts, Vector((0, 0, 0))) / len(all_verts)

        candidates.append({
            'area_a':    g_areas[ga],
            'area_b':    g_areas[gb],
            'normal_a':  na,
            'normal_b':  nb,
            'dot':       dot,
            'plane_co':  plane_co,
            'plane_no':  bisector,
            'seam_lines': segs,
        })

    bm.free()
    return candidates


# ------------------------------------------------------------------

class MESH_OT_concavity_split(bpy.types.Operator):
    bl_idname  = "mesh.concavity_split"
    bl_label   = "Concavity Split"
    bl_description = (
        "Detect large faces meeting at concave angles and preview split seams. "
        "Drag=face size  Q/E=angle  Enter=confirm  RMB/Esc=cancel"
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

        self._min_face_area = 10.0   # m²
        self._angle_deg     = 15.0   # degrees
        self._fill_cut      = False
        self._axis_idx      = 2      # Z

        # Heavy work done once here
        self._candidates = _analyse_mesh(obj)

        self._splits     = []
        self._line_batch = None
        self._shader     = gpu.shader.from_builtin('UNIFORM_COLOR')

        # Precompute mesh triangle batch for semi-transparent overlay
        mx = obj.matrix_world
        mesh_tris = []
        for poly in obj.data.polygons:
            fv = [mx @ obj.data.vertices[vi].co for vi in poly.vertices]
            for i in range(1, len(fv) - 1):
                mesh_tris += [tuple(fv[0]), tuple(fv[i]), tuple(fv[i+1])]
        self._mesh_batch = batch_for_shader(
            gpu.shader.from_builtin('UNIFORM_COLOR'), 'TRIS', {"pos": mesh_tris}
        ) if mesh_tris else None

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
        """Filter pre-cached candidates by current thresholds — no bmesh work here."""
        # Compute bbox center first — needed for dedup and quad building
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
            # Use the seam position relative to bbox center, not the bisector normal direction
            p = c['plane_co']
            return math.atan2(p[a1] - bc[a1], p[a0] - bc[a0])

        # Sort by angle, then deduplicate: skip if within 20° of a kept entry
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

        axis_vec = Vector([1.0 if i == self._axis_idx else 0.0 for i in range(3)])

        tri_verts = []
        for s in self._splits:
            # Derive cut normal from seam position relative to bbox center (radial direction)
            plane_no = s['plane_co'].copy()
            plane_no[self._axis_idx] = 0.0
            plane_no -= Vector([bbox_center[i] if i != self._axis_idx else 0.0 for i in range(3)])
            if plane_no.length < 1e-6:
                continue
            plane_no.normalize()

            # Tangent in the cut plane = axis_vec × plane_no (lies in cut plane, along axis)
            t_along = axis_vec.cross(plane_no)
            # t_along should be ~zero length since axis_vec ⊥ plane_no — use axis directly
            # The cut plane quad: extends from ax_min to ax_max along axis,
            # and from 0 to radius along plane_no direction from origin
            lo_pt = bbox_center.copy(); lo_pt[self._axis_idx] = ax_min
            hi_pt = bbox_center.copy(); hi_pt[self._axis_idx] = ax_max

            p0 = tuple(lo_pt - plane_no * radius)
            p1 = tuple(lo_pt + plane_no * radius)
            p2 = tuple(hi_pt + plane_no * radius)
            p3 = tuple(hi_pt - plane_no * radius)

            # Draw both windings so quad is visible from both sides without doubling
            tri_verts += [p0, p1, p2, p0, p2, p3]  # front
            tri_verts += [p0, p2, p1, p0, p3, p2]  # back

        if tri_verts:
            self._line_batch = batch_for_shader(
                self._shader, 'TRIS', {"pos": tri_verts}
            )
        else:
            self._line_batch = None

    def _draw_callback(self, context):
        shader = self._shader
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
        line1 = (f"Concavity Split  |  Axis: {axis_name}  |  Min Face: {self._min_face_area:.2f} m²  |  "
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
        return self._apply_splits(context)

    def _apply_splits(self, context):
        obj = self._obj

        # Bbox center in world space — plane_co for all bisects
        obj_coords = [obj.matrix_world @ Vector(v.co) for v in obj.data.vertices]
        bbox_center = Vector([
            (min(c[i] for c in obj_coords) + max(c[i] for c in obj_coords)) / 2.0
            for i in range(3)
        ])

        ax = self._axis_idx
        other = [i for i in range(3) if i != ax]
        a0, a1 = other

        # Build cut plane normals (radially outward from bbox center to each seam)
        cut_planes = []  # list of (angle, plane_normal_world)
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

        # --- Step 1: duplicate once, insert all cuts (no clear) ---
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
                use_fill=self._fill_cut,
                clear_inner=False,
                clear_outer=False,
                threshold=0.0001,
            )
        bpy.ops.object.mode_set(mode='OBJECT')

        # --- Step 2: assign each face a sector index by centroid angle ---
        mesh = dup.data
        bm = bmesh.new()
        bm.from_mesh(mesh)
        sl = bm.faces.layers.int.new("_sector")

        cut_angles = [a for a, _ in cut_planes]
        mx = dup.matrix_world

        for face in bm.faces:
            cw = mx @ face.calc_center_median()
            ang = math.atan2(cw[a1] - bbox_center[a1], cw[a0] - bbox_center[a0])
            sector = n - 1  # default: last sector (wraps around)
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

        # --- Step 3: collection setup ---
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

        # --- Step 4: separate sector by sector ---
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

            # The newly separated piece is the non-active selected object
            pieces = [o for o in context.selected_objects if o is not current and o.type == 'MESH']
            for piece in pieces:
                piece.name = f"{base_name}_seg{seg_idx+1:02d}"
                if piece.data:
                    piece.data.name = piece.name
                for c in list(piece.users_collection):
                    c.objects.unlink(piece)
                seg_col.objects.link(piece)
                results.append(piece)

        # Last remaining piece = final sector
        current.name = f"{base_name}_seg{n:02d}"
        if current.data:
            current.data.name = current.name
        for c in list(current.users_collection):
            c.objects.unlink(current)
        seg_col.objects.link(current)
        results.append(current)

        obj.hide_set(True)
        bpy.ops.object.select_all(action='DESELECT')
        for seg in results:
            seg.select_set(True)
        if results:
            context.view_layer.objects.active = results[0]

        self.report({'INFO'}, f"Split into {len(results)} segment(s) → collection '{col_name}'")
        return {'FINISHED'}


# ------------------------------------------------------------------

def _menu_func(self, context):
    self.layout.operator(MESH_OT_concavity_split.bl_idname, icon='MOD_MESHDEFORM')


def register():
    try:
        bpy.utils.unregister_class(MESH_OT_concavity_split)
    except Exception:
        pass
    bpy.utils.register_class(MESH_OT_concavity_split)

    if hasattr(bpy.types.VIEW3D_MT_object, '_dyn_ui_initialize'):
        for fn in list(bpy.types.VIEW3D_MT_object._dyn_ui_initialize()):
            code = getattr(getattr(fn, '__func__', fn), '__code__', None)
            if code and code.co_filename == _THIS_FILE:
                try:
                    bpy.types.VIEW3D_MT_object.remove(fn)
                except Exception:
                    pass
    bpy.types.VIEW3D_MT_object.append(_menu_func)


def unregister():
    try:
        bpy.utils.unregister_class(MESH_OT_concavity_split)
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
