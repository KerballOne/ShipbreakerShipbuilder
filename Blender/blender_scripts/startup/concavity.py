import bpy
import bmesh
import blf
import gpu
import math
import re
from gpu_extras.batch import batch_for_shader
from mathutils import Vector
from bpy_extras import view3d_utils

bl_info = {
    "name": "Concavity",
    "author": "KerballOne",
    "version": (1, 3),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Detect large faces meeting at concave angles and split between them",
}

_THIS_FILE = __file__



# ------------------------------------------------------------------
# One-time mesh analysis (called at invoke only)

def _analyse_mesh(obj, axis_idx, coplanar_deg=2.0):
    """
    Build a bmesh in world space, group nearly-coplanar faces, then find
    adjacent group pairs and record their geometry.

    Faces are seeded largest-first so that large flat panels claim their
    coplanar neighbours before small transition/corner faces can seed their
    own tiny groups.  This prevents small bevels from blocking detection of
    the seam between two large adjacent panels.

    Each candidate:
      area_a, area_b   : float  — group areas
      normal_a, normal_b: Vector — area-weighted group normals
      dot              : float  — na.dot(nb)
      plane_co         : Vector — centroid of the shared boundary verts
      plane_no         : Vector — bisector of the two group normals
      seam_lines       : list of (Vector, Vector) — world-space edge segments
    """
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    bm.edges.ensure_lookup_table()

    mx = obj.matrix_world
    for v in bm.verts:
        v.co = mx @ v.co
    bm.normal_update()

    face_areas   = [f.calc_area() for f in bm.faces]
    face_normals = [f.normal.copy() for f in bm.faces]

    # --- coplanar flood-fill, seeded largest-face-first ---
    coplanar_thresh = math.cos(math.radians(coplanar_deg))
    # Sort face indices by area descending so large panels seed first
    seed_order = sorted(range(len(bm.faces)), key=lambda i: face_areas[i], reverse=True)

    visited = set()
    groups = []
    face_to_group = {}

    for seed_fi in seed_order:
        if seed_fi in visited:
            continue
        group = set()
        stack = [bm.faces[seed_fi]]
        seed_normal = face_normals[seed_fi]
        while stack:
            f = stack.pop()
            if f.index in visited:
                continue
            visited.add(f.index)
            group.add(f.index)
            for edge in f.edges:
                for linked in edge.link_faces:
                    if linked.index not in visited:
                        # Compare against the seed normal, not the current face normal,
                        # so the group stays coherent even as we expand across the panel
                        if seed_normal.dot(face_normals[linked.index]) >= coplanar_thresh:
                            stack.append(linked)
        g_idx = len(groups)
        groups.append(group)
        for fi in group:
            face_to_group[fi] = g_idx

    # --- per-group area, normal, and centroid ---
    def group_area(g):
        return sum(face_areas[fi] for fi in g)

    def group_normal(g):
        n = Vector((0, 0, 0))
        for fi in g:
            n += face_normals[fi] * face_areas[fi]
        return n.normalized() if n.length > 1e-6 else Vector((0, 0, 1))

    def group_centroid(g):
        c = Vector((0, 0, 0))
        total = 0.0
        for fi in g:
            c += bm.faces[fi].calc_center_median() * face_areas[fi]
            total += face_areas[fi]
        return c / total if total > 1e-8 else Vector((0, 0, 0))

    g_areas     = [group_area(g)     for g in groups]
    g_normals   = [group_normal(g)   for g in groups]
    g_centroids = [group_centroid(g) for g in groups]

    # --- find all adjacent group pairs ---
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
            'area_a':    g_areas[ga],
            'area_b':    g_areas[gb],
            'normal_a':  na,
            'normal_b':  nb,
            'dot':       dot,
            'plane_co':  plane_co,
            'plane_no':  bisector,
            'seam_lines': segs,
            'group_a':   ga,
            'group_b':   gb,
        })

    # --- per-group boundary edges ---
    group_boundary_edges = [[] for _ in groups]
    for edge in bm.edges:
        face_groups = [face_to_group.get(f.index, -1) for f in edge.link_faces]
        for g_idx in set(face_groups):
            if g_idx < 0:
                continue
            if face_groups.count(g_idx) < len(edge.link_faces):
                seg = (edge.verts[0].co.copy(), edge.verts[1].co.copy())
                group_boundary_edges[g_idx].append(seg)

    bm.free()
    return candidates, g_areas, g_centroids, group_boundary_edges


# ------------------------------------------------------------------

class MESH_OT_concavity(bpy.types.Operator):
    bl_idname  = "mesh.concavity"
    bl_label   = "Concavity"
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
        self._seam_mode     = False  # C: seam-aligned planes vs radial-through-center

        # Heavy work — re-run when axis changes
        self._candidates, self._g_areas, self._g_centroids, self._group_boundary_edges = _analyse_mesh(obj, self._axis_idx)

        self._splits        = []
        self._line_batch    = None
        self._ghost_batch   = None
        self._wire_batches  = []
        self._group_labels  = []
        self._shader        = gpu.shader.from_builtin('UNIFORM_COLOR')

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
            self._candidates, self._g_areas, self._g_centroids, self._group_boundary_edges = _analyse_mesh(self._obj, self._axis_idx)
            self._rebuild_batches()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'F' and event.value == 'PRESS':
            self._fill_cut = not self._fill_cut
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'C' and event.value == 'PRESS':
            self._seam_mode = not self._seam_mode
            self._rebuild_batches()
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

        # Assign a color per unique group index that appears in detected splits
        SECTOR_COLORS = [
            (0.2, 0.6, 1.0, 1.0),   # blue
            (0.2, 1.0, 0.4, 1.0),   # green
            (1.0, 0.4, 0.2, 1.0),   # orange
            (0.8, 0.2, 1.0, 1.0),   # purple
            (0.2, 1.0, 1.0, 1.0),   # cyan
            (1.0, 1.0, 0.2, 1.0),   # yellow-bright
            (1.0, 0.2, 0.6, 1.0),   # pink
            (0.4, 1.0, 0.2, 1.0),   # lime
        ]
        # Collect all group indices from detected splits, assign color index in order seen
        group_color_idx = {}
        for s in self._splits:
            for key in ('group_a', 'group_b'):
                g = s[key]
                if g not in group_color_idx:
                    group_color_idx[g] = len(group_color_idx) % len(SECTOR_COLORS)

        # Build one wire batch per color
        color_wire_verts = {}
        for g_idx, ci in group_color_idx.items():
            verts = color_wire_verts.setdefault(ci, [])
            for (va, vb) in self._group_boundary_edges[g_idx]:
                verts += [tuple(va), tuple(vb)]

        self._wire_batches = [
            (SECTOR_COLORS[ci], batch_for_shader(self._shader, 'LINES', {"pos": verts}))
            for ci, verts in color_wire_verts.items() if verts
        ]

        # Store (world_pos, label_str, color) for each active group label
        self._group_labels = []
        for g_idx, ci in group_color_idx.items():
            co = self._g_centroids[g_idx]
            self._group_labels.append((co, str(g_idx), SECTOR_COLORS[ci]))

        # Collect mesh polygons in world space for intersection
        mx = obj.matrix_world
        mesh_polys = []
        for poly in obj.data.polygons:
            mesh_polys.append([mx @ obj.data.vertices[vi].co for vi in poly.vertices])

        ghost_verts = []   # nearly invisible full-extent plane
        cut_line_verts = [] # bright lines where plane intersects mesh faces

        for s in self._splits:
            if self._seam_mode:
                plane_co = s['plane_co'].copy()
                plane_no = s['plane_no'].copy()
                if plane_no.length < 1e-6:
                    continue
                plane_no.normalize()
            else:
                plane_no = s['plane_co'].copy()
                plane_no[self._axis_idx] = 0.0
                plane_no -= Vector([bbox_center[i] if i != self._axis_idx else 0.0 for i in range(3)])
                if plane_no.length < 1e-6:
                    continue
                plane_no.normalize()
                plane_co = bbox_center.copy()

            half_h = (ax_max - ax_min) * 0.5
            perp = plane_no.cross(axis_vec)
            if perp.length < 1e-6:
                perp = Vector([1.0 if i == a0 else 0.0 for i in range(3)])
            perp.normalize()

            # Ghost quad: full bbox-spanning plane, nearly invisible
            lo = plane_co.copy(); lo[self._axis_idx] = ax_min
            hi = plane_co.copy(); hi[self._axis_idx] = ax_max
            p0 = tuple(lo - perp * radius)
            p1 = tuple(lo + perp * radius)
            p2 = tuple(hi + perp * radius)
            p3 = tuple(hi - perp * radius)
            ghost_verts += [p0, p1, p2, p0, p2, p3, p0, p2, p1, p0, p3, p2]

            # Build two orthonormal axes within the cut plane
            u = axis_vec - axis_vec.dot(plane_no) * plane_no
            if u.length < 1e-6:
                u = perp.copy()
            u.normalize()
            v2 = plane_no.cross(u)
            v2.normalize()

            # Collect one crossing segment per polygon that the plane slices.
            # A convex polygon intersected by a plane yields exactly 0 or 2 crossing
            # points; for non-convex polys we keep all crossing points and pair them
            # sequentially.  Each pair (p0, p1) is a chord across one polygon face.
            cross_segments = []   # list of (Vector, Vector)
            for poly_verts in mesh_polys:
                nv = len(poly_verts)
                hits = []
                for i in range(nv):
                    va = poly_verts[i]
                    vb = poly_verts[(i + 1) % nv]
                    da = (va - plane_co).dot(plane_no)
                    db = (vb - plane_co).dot(plane_no)
                    if (da < 0) != (db < 0):
                        t = da / (da - db)
                        hits.append(va + t * (vb - va))
                for i in range(0, len(hits) - 1, 2):
                    cross_segments.append((hits[i], hits[i + 1]))

            if not cross_segments:
                continue

            # Chain segments into loops: each segment shares endpoints with
            # neighbouring segments (within snap_dist).  Walking chains gives us
            # the separate loops (outer wall loop, inner wall loop, etc.).
            snap_dist = 0.001
            snap_dist2 = snap_dist * snap_dist
            used = [False] * len(cross_segments)
            loops = []

            def dist2(a, b):
                d = a - b
                return d.dot(d)

            for start in range(len(cross_segments)):
                if used[start]:
                    continue
                used[start] = True
                loop = [cross_segments[start][0], cross_segments[start][1]]
                # grow forward
                changed = True
                while changed:
                    changed = False
                    tail = loop[-1]
                    for j, (pa, pb) in enumerate(cross_segments):
                        if used[j]:
                            continue
                        if dist2(tail, pa) < snap_dist2:
                            used[j] = True
                            loop.append(pb)
                            changed = True
                            break
                        if dist2(tail, pb) < snap_dist2:
                            used[j] = True
                            loop.append(pa)
                            changed = True
                            break
                loops.append(loop)

            # Fan-triangulate each loop independently — no bridging between loops
            for loop in loops:
                if len(loop) < 3:
                    continue
                c = sum(loop, Vector((0, 0, 0))) / len(loop)
                ct = tuple(c)
                for i in range(len(loop) - 1):
                    cut_line_verts += [ct, tuple(loop[i]), tuple(loop[i + 1])]
                # close the loop
                cut_line_verts += [ct, tuple(loop[-1]), tuple(loop[0])]

        self._ghost_batch = batch_for_shader(self._shader, 'TRIS', {"pos": ghost_verts})    if ghost_verts    else None
        self._line_batch  = batch_for_shader(self._shader, 'TRIS', {"pos": cut_line_verts}) if cut_line_verts else None


    def _draw_callback(self, context):
        shader = self._shader
        gpu.state.blend_set('ALPHA')
        gpu.state.depth_test_set('ALWAYS')
        gpu.state.face_culling_set('NONE')
        shader.bind()
        # ghost plane and intersection highlight hidden for now
        # if self._ghost_batch:
        #     shader.uniform_float("color", (1.0, 0.85, 0.2, 0.01))
        #     self._ghost_batch.draw(shader)
        # if self._line_batch:
        #     shader.uniform_float("color", (1.0, 0.9, 0.1, 0.8))
        #     self._line_batch.draw(shader)
        if self._wire_batches:
            gpu.state.line_width_set(2.0)
            for color, batch in self._wire_batches:
                shader.uniform_float("color", color)
                batch.draw(shader)
            gpu.state.line_width_set(1.0)
        gpu.state.face_culling_set('BACK')
        gpu.state.blend_set('NONE')
        gpu.state.depth_test_set('LESS_EQUAL')

    def _draw_hud(self, context):
        axis_name = ('X', 'Y', 'Z')[self._axis_idx]
        fill_str  = "ON" if self._fill_cut else "OFF"
        mode_str  = "Seam" if self._seam_mode else "Radial"
        line1 = (f"Concavity  |  Axis: {axis_name}  |  Min Face: {self._min_face_area:.2f} m²  |  "
                 f"Angle: {self._angle_deg:.1f}°  |  "
                 f"Splits: {len(self._splits)}  |  Fill: {fill_str}  |  Mode: {mode_str}")
        line2 = "Drag=face size  Scroll=fine  Q/E=angle  R=axis  F=fill  C=mode  Enter=confirm  RMB/Esc=cancel"
        font_id = 0
        blf.size(font_id, 14)
        blf.color(font_id, 1.0, 1.0, 1.0, 1.0)
        blf.position(font_id, 20, 50, 0)
        blf.draw(font_id, line1)
        blf.size(font_id, 11)
        blf.color(font_id, 0.7, 0.7, 0.7, 1.0)
        blf.position(font_id, 20, 32, 0)
        blf.draw(font_id, line2)

        # Draw group ID labels at each active group's centroid
        if self._group_labels:
            region = context.region
            rv3d = context.region_data
            blf.size(font_id, 12)
            for world_co, label, color in self._group_labels:
                screen = view3d_utils.location_3d_to_region_2d(region, rv3d, world_co)
                if screen is None:
                    continue
                blf.color(font_id, color[0], color[1], color[2], 1.0)
                blf.position(font_id, screen.x + 4, screen.y + 4, 0)
                blf.draw(font_id, label)

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

        # Build cut planes — (angle, normal_world, co_world)
        cut_planes = []
        for s in self._splits:
            if self._seam_mode:
                # Seam-aligned: plane sits at the seam centroid.
                # Normal = direction from bbox center to seam centroid (radial),
                # but plane_co = seam centroid instead of bbox center.
                # This places the cut exactly on the face boundary rather than
                # projecting it through the center of the mesh.
                pn = s['plane_co'].copy()
                pn[ax] = 0.0
                pn -= Vector([bbox_center[i] if i != ax else 0.0 for i in range(3)])
                if pn.length < 1e-6:
                    continue
                pn.normalize()
                co = s['plane_co'].copy()
            else:
                # Radial: normal points from bbox center toward seam
                pn = s['plane_co'].copy()
                pn[ax] = 0.0
                pn -= Vector([bbox_center[i] if i != ax else 0.0 for i in range(3)])
                if pn.length < 1e-6:
                    continue
                pn.normalize()
                co = bbox_center.copy()

            angle = math.atan2(pn[a1], pn[a0])
            cut_planes.append((angle, pn, co))

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
        inv_mx = dup.matrix_world.inverted()

        bpy.ops.object.mode_set(mode='EDIT')
        for _, pn, co_world in cut_planes:
            local_co = inv_mx @ co_world
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

        cut_angles = [a for a, _, _co in cut_planes]
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
    self.layout.operator(MESH_OT_concavity.bl_idname, icon='MOD_MESHDEFORM')


def register():
    try:
        bpy.utils.unregister_class(MESH_OT_concavity)
    except Exception:
        pass
    bpy.utils.register_class(MESH_OT_concavity)

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
        bpy.utils.unregister_class(MESH_OT_concavity)
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
