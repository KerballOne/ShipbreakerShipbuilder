import bpy
import bmesh
import gpu
import blf
import re
from gpu_extras.batch import batch_for_shader
from mathutils import Vector

bl_info = {
    "name": "Interactive Bisect",
    "author": "KerballOne",
    "version": (1, 2),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Right-click a mesh to drag a cutting plane along an axis; confirm to split into two objects in the same parent/collection",
}

# Colors for the two halves (RGBA)
COLOR_TOP    = (0.2, 0.6, 1.0, 0.45)
COLOR_BOTTOM = (1.0, 0.45, 0.1, 0.45)
COLOR_PLANE  = (1.0, 1.0, 0.2, 0.25)


# ------------------------------------------------------------------
# Helpers

def _strip_numeric_suffix(name):
    return re.sub(r'_\d+$', '', name)


def _next_pair_names(base):
    existing = set(bpy.data.objects.keys())
    n = 1
    while True:
        a = f"{base}_{n}"
        b = f"{base}_{n + 1}"
        if a not in existing and b not in existing:
            return a, b
        n += 1


def _world_axis_bounds(obj, axis_idx):
    coords = [obj.matrix_world @ Vector(v.co) for v in obj.data.vertices]
    if not coords:
        return 0.0, 1.0
    vals = [c[axis_idx] for c in coords]
    return min(vals), max(vals)



def _build_plane_batch(obj, axis_idx, cut_val):
    mx = obj.matrix_world
    bbox = [mx @ Vector(c) for c in obj.bound_box]
    other = [a for a in range(3) if a != axis_idx]
    a, b = other
    pad = 0.5
    lo_a = min(c[a] for c in bbox) - pad
    hi_a = max(c[a] for c in bbox) + pad
    lo_b = min(c[b] for c in bbox) - pad
    hi_b = max(c[b] for c in bbox) + pad

    def corner(va, vb):
        v = [0.0, 0.0, 0.0]
        v[axis_idx] = cut_val
        v[a] = va
        v[b] = vb
        return tuple(v)

    verts = [corner(lo_a, lo_b), corner(hi_a, lo_b), corner(hi_a, hi_b),
             corner(lo_a, lo_b), corner(hi_a, hi_b), corner(lo_a, hi_b)]
    shader = gpu.shader.from_builtin('UNIFORM_COLOR')
    return batch_for_shader(shader, 'TRIS', {"pos": verts})


# ------------------------------------------------------------------

class MESH_OT_bisect_interactive(bpy.types.Operator):
    bl_idname = "mesh.bisect_interactive"
    bl_label = "Interactive Bisect"
    bl_description = (
        "Drag to position a cutting plane. "
        "Enter=confirm, RMB/Esc=cancel, R=cycle axis, Scroll=fine adjust"
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
            self.report({'ERROR'}, f"'{obj.name}' has no vertices — select a mesh with geometry")
            return {'CANCELLED'}

        self._obj      = obj
        self._obj_name = obj.name
        self._axis_idx = 2  # Z

        amin, amax = _world_axis_bounds(obj, self._axis_idx)
        self._axis_min = amin
        self._axis_max = amax
        self._cut_val  = (amin + amax) / 2.0

        self._fill_cut   = False  # Q toggles whether the cut face is filled

        self._dragging       = False
        self._drag_start_x   = event.mouse_region_x
        self._drag_start_y   = event.mouse_region_y
        self._drag_start_cut = self._cut_val

        self._shader = gpu.shader.from_builtin('UNIFORM_COLOR')
        self._batch_top    = None
        self._batch_bottom = None
        self._batch_plane  = None

        # Precompute world-space triangle lists once — reused every _rebuild_batches call
        self._tri_centers  = []  # world-space face center per tri
        self._tri_verts    = []  # flat list of 3 Vector per tri
        mx = obj.matrix_world
        for poly in obj.data.polygons:
            center_world = mx @ poly.center
            face_verts = [mx @ obj.data.vertices[vi].co for vi in poly.vertices]
            for i in range(1, len(face_verts) - 1):
                self._tri_centers.append(center_world)
                self._tri_verts.append((face_verts[0], face_verts[i], face_verts[i + 1]))

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

        if event.type == 'LEFTMOUSE' and event.value == 'PRESS':
            self._dragging        = True
            self._drag_start_x    = event.mouse_region_x
            self._drag_start_y    = event.mouse_region_y
            self._drag_start_cut  = self._cut_val
            return {'RUNNING_MODAL'}

        if event.type == 'LEFTMOUSE' and event.value == 'RELEASE':
            self._dragging = False
            return {'RUNNING_MODAL'}

        if event.type == 'MOUSEMOVE' and self._dragging:
            region = context.region
            span   = self._axis_max - self._axis_min
            # X axis: drag horizontally (right = positive X)
            # Y axis: drag horizontally (right = positive Y)
            # Z axis: drag vertically   (up    = positive Z)
            if self._axis_idx == 2:
                delta  = event.mouse_region_y - self._drag_start_y
                travel = (delta / region.height) * span
            else:
                delta  = event.mouse_region_x - self._drag_start_x
                travel = (delta / region.width) * span
            new_val = max(self._axis_min, min(self._axis_max,
                          self._drag_start_cut + travel))
            if new_val != self._cut_val:
                self._cut_val = new_val
                self._rebuild_batches()
                context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'WHEELUPMOUSE':
            span = self._axis_max - self._axis_min
            self._cut_val = min(self._axis_max, self._cut_val + span * 0.001)
            self._rebuild_batches()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'WHEELDOWNMOUSE':
            span = self._axis_max - self._axis_min
            self._cut_val = max(self._axis_min, self._cut_val - span * 0.001)
            self._rebuild_batches()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'R' and event.value == 'PRESS':
            self._axis_idx = (self._axis_idx + 1) % 3
            amin, amax = _world_axis_bounds(self._obj, self._axis_idx)
            self._axis_min = amin
            self._axis_max = amax
            self._cut_val  = (amin + amax) / 2.0
            self._rebuild_batches()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'F' and event.value == 'PRESS':
            self._fill_cut = not self._fill_cut
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        return {'PASS_THROUGH'}

    def _rebuild_batches(self):
        shader = self._shader
        axis_idx = self._axis_idx
        cut_val  = self._cut_val

        verts_top = []
        verts_bot = []
        for center, tri in zip(self._tri_centers, self._tri_verts):
            if center[axis_idx] >= cut_val:
                verts_top.extend(tri)
            else:
                verts_bot.extend(tri)

        def make(verts):
            return batch_for_shader(shader, 'TRIS', {"pos": verts}) if verts else None

        self._batch_top    = make(verts_top)
        self._batch_bottom = make(verts_bot)
        self._batch_plane  = _build_plane_batch(self._obj, axis_idx, cut_val)

    def _draw_callback(self, context):
        shader = self._shader
        gpu.state.blend_set('ALPHA')
        gpu.state.depth_test_set('LESS_EQUAL')
        gpu.state.face_culling_set('NONE')

        if self._batch_top:
            shader.bind()
            shader.uniform_float("color", COLOR_TOP)
            self._batch_top.draw(shader)

        if self._batch_bottom:
            shader.bind()
            shader.uniform_float("color", COLOR_BOTTOM)
            self._batch_bottom.draw(shader)

        gpu.state.depth_test_set('ALWAYS')
        if self._batch_plane:
            shader.bind()
            shader.uniform_float("color", COLOR_PLANE)
            self._batch_plane.draw(shader)

        gpu.state.blend_set('NONE')
        gpu.state.depth_test_set('LESS_EQUAL')
        gpu.state.face_culling_set('BACK')

    def _draw_hud(self, context):
        axis_name = ('X', 'Y', 'Z')[self._axis_idx]
        fill_str  = "ON" if self._fill_cut else "OFF"
        line1 = f"Interactive Bisect  |  Axis: {axis_name}  |  Cut: {self._cut_val:.4f} m  |  Fill cut face: {fill_str}"
        line2 = "Drag=move plane  Scroll=fine  R=cycle axis  F=toggle fill  Enter=confirm  RMB/Esc=cancel"
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
        return {'CANCELLED'} if cancelled else self._apply_bisect(context)

    def _apply_bisect(self, context):
        obj      = self._obj
        axis_idx = self._axis_idx
        cut_val  = self._cut_val

        original_parent    = obj.parent
        original_parent_mx = obj.matrix_parent_inverse.copy()
        original_world_mx  = obj.matrix_world.copy()
        original_cols      = list(obj.users_collection)

        plane_co_world = original_world_mx.translation.copy()
        plane_co_world[axis_idx] = cut_val
        plane_no_world = Vector([1.0 if i == axis_idx else 0.0 for i in range(3)])

        def _make_dup():
            """Create a duplicate object with rotation+scale baked, return (new_obj, plane_co_local)."""
            new_mesh = obj.data.copy()
            new_obj  = bpy.data.objects.new(obj.name, new_mesh)
            for col in original_cols:
                col.objects.link(new_obj)
            new_obj.matrix_world = original_world_mx
            bpy.ops.object.select_all(action='DESELECT')
            new_obj.select_set(True)
            context.view_layer.objects.active = new_obj
            bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
            context.view_layer.update()
            loc = new_obj.matrix_world.translation.copy()
            plane_co_local = plane_co_world - loc
            return new_obj, plane_co_local

        def _cut_half(dup, plane_co_local, keep_above):
            mesh = dup.data
            bm   = bmesh.new()
            bm.from_mesh(mesh)

            geom_all = bm.verts[:] + bm.edges[:] + bm.faces[:]
            bmesh.ops.bisect_plane(
                bm,
                geom=geom_all,
                plane_co=plane_co_local,
                plane_no=plane_no_world,
                use_snap_center=False,
                dist=0.0001,
            )

            del_verts = []
            for v in bm.verts:
                d = (v.co - plane_co_local).dot(plane_no_world)
                if keep_above and d < -0.0001:
                    del_verts.append(v)
                elif not keep_above and d > 0.0001:
                    del_verts.append(v)
            bmesh.ops.delete(bm, geom=del_verts, context='VERTS')

            if self._fill_cut:
                boundary_edges = [e for e in bm.edges if e.is_boundary]
                if boundary_edges:
                    bmesh.ops.contextual_create(bm, geom=boundary_edges)

            bm.to_mesh(mesh)
            bm.free()
            mesh.update()

            if original_parent is not None:
                dup.parent = original_parent
                dup.matrix_parent_inverse = original_parent_mx

        # Step 1: create both duplicates with transforms baked BEFORE cutting either
        dup_bottom, plane_co_local_b = _make_dup()
        dup_top,    plane_co_local_t = _make_dup()

        # Step 2: cut each independently
        _cut_half(dup_bottom, plane_co_local_b, keep_above=False)
        _cut_half(dup_top,    plane_co_local_t, keep_above=True)

        obj_bottom = dup_bottom
        obj_top    = dup_top

        # Remove the original
        bpy.data.objects.remove(obj, do_unlink=True)

        # Name both halves with incrementing suffix
        base_name = _strip_numeric_suffix(self._obj_name)
        name_a, name_b = _next_pair_names(base_name)
        obj_top.name = name_a
        if obj_top.data:
            obj_top.data.name = name_a
        obj_bottom.name = name_b
        if obj_bottom.data:
            obj_bottom.data.name = name_b

        axis_name = ('X', 'Y', 'Z')[axis_idx]
        self.report({'INFO'}, f"Bisected along {axis_name} at {cut_val:.4f}m → {name_a}, {name_b}")
        return {'FINISHED'}


# ------------------------------------------------------------------
# Menu registration

_THIS_FILE = __file__  # captured at module scope, stable across reloads

def _purge_menus():
    """Remove any stale IB entries from all menus (cleanup from previous versions)."""
    for mt in (bpy.types.VIEW3D_MT_object_context_menu,
               bpy.types.OUTLINER_MT_context_menu,
               bpy.types.OUTLINER_MT_object):
        if not hasattr(mt, '_dyn_ui_initialize'):
            continue
        for fn in list(mt._dyn_ui_initialize()):
            inner = getattr(fn, '__func__', fn)
            fname = getattr(getattr(inner, '__code__', None), 'co_filename', '')
            if fname == _THIS_FILE:
                try:
                    mt.remove(fn)
                except Exception:
                    pass


def register():
    try:
        bpy.utils.unregister_class(MESH_OT_bisect_interactive)
    except Exception:
        pass
    bpy.utils.register_class(MESH_OT_bisect_interactive)
    _purge_menus()


def unregister():
    try:
        bpy.utils.unregister_class(MESH_OT_bisect_interactive)
    except Exception:
        pass
    _purge_menus()
