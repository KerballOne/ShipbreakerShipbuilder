import bpy
import bmesh
import gpu
import blf
from gpu_extras.batch import batch_for_shader
from mathutils import Vector, Matrix

bl_info = {
    "name": "Hollow Mesh",
    "author": "KerballOne",
    "version": (3, 0),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Interactively hollow a mesh: drag to set wall thickness, Q/E for overshoot, R to cycle axis",
}

COLOR_CUTTER = (1.0, 0.4, 0.1, 1.0)   # orange wireframe — inner cutter (removed)

_THIS_FILE = __file__


# ------------------------------------------------------------------
# Helpers shared with execute path

def _compute_cutter_matrix(obj_world_mx, dims, axis_idx, wall_thickness, axis_overshoot):
    """
    Return the world-space Matrix that transforms a unit-bbox copy of obj
    into the boolean cutter shape: shrunk on perpendicular axes, expanded on open axis.
    dims: [dx, dy, dz] in local space after rotation is baked.
    """
    scale = [1.0, 1.0, 1.0]
    for i in range(3):
        d = dims[i]
        if d <= 0:
            continue
        if i != axis_idx:
            scale[i] = max((d - 2 * wall_thickness) / d, 0.001)
        else:
            scale[i] = (d + 2 * axis_overshoot) / d
    return obj_world_mx @ Matrix.Diagonal(Vector(scale + [1.0]))


def _bbox_dims(obj):
    """Local bounding-box dimensions after accounting for rotation/scale via bound_box."""
    bb = obj.bound_box
    return [
        max(v[i] for v in bb) - min(v[i] for v in bb)
        for i in range(3)
    ]


def _build_mesh_batch(obj, override_mx=None):
    """Filled TRIS batch from obj's faces in world space."""
    mx = override_mx if override_mx is not None else obj.matrix_world
    verts = []
    for poly in obj.data.polygons:
        fv = [mx @ obj.data.vertices[vi].co for vi in poly.vertices]
        for i in range(1, len(fv) - 1):
            verts += [tuple(fv[0]), tuple(fv[i]), tuple(fv[i + 1])]
    shader = gpu.shader.from_builtin('UNIFORM_COLOR')
    return batch_for_shader(shader, 'TRIS', {"pos": verts})


def _build_wire_batch(obj, override_mx=None):
    """LINES batch drawing every mesh edge in world space."""
    mx = override_mx if override_mx is not None else obj.matrix_world
    verts = []
    for edge in obj.data.edges:
        for vi in edge.vertices:
            verts.append(tuple(mx @ obj.data.vertices[vi].co))
    shader = gpu.shader.from_builtin('UNIFORM_COLOR')
    return batch_for_shader(shader, 'LINES', {"pos": verts})


# ------------------------------------------------------------------

class MESH_OT_hollow_mesh(bpy.types.Operator):
    bl_idname  = "mesh.hollow_mesh"
    bl_label   = "Hollow Mesh"
    bl_description = (
        "Interactively hollow the mesh. "
        "Drag=wall thickness | Scroll=fine | R=cycle axis | Q/E=overshoot | Enter=confirm | RMB/Esc=cancel"
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

        self._obj = obj

        # Load persisted settings
        s = context.scene
        self._axis_idx       = {'X': 0, 'Y': 1, 'Z': 2}.get(s.hollow_mesh_axis, 2)
        self._wall_thickness = s.hollow_mesh_wall_thickness
        self._axis_overshoot = s.hollow_mesh_axis_overshoot

        # Bake object dims (rotation-aware via bound_box)
        self._dims = _bbox_dims(obj)

        self._shader       = gpu.shader.from_builtin('UNIFORM_COLOR')
        self._batch_cutter = None
        self._rebuild_cutter_batch()

        self._fill_cut        = False
        self._dragging        = False
        self._drag_start_y    = event.mouse_region_y
        self._drag_start_wall = self._wall_thickness

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
            self._rebuild_cutter_batch()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'Q' and event.value == 'PRESS':
            self._axis_overshoot = max(0.001, self._axis_overshoot - 0.01)
            self._rebuild_cutter_batch()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'E' and event.value == 'PRESS':
            self._axis_overshoot += 0.01
            self._rebuild_cutter_batch()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'F' and event.value == 'PRESS':
            self._fill_cut = not self._fill_cut
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'LEFTMOUSE' and event.value == 'PRESS':
            self._dragging        = True
            self._drag_start_y    = event.mouse_region_y
            self._drag_start_wall = self._wall_thickness
            return {'RUNNING_MODAL'}

        if event.type == 'LEFTMOUSE' and event.value == 'RELEASE':
            self._dragging = False
            return {'RUNNING_MODAL'}

        if event.type == 'MOUSEMOVE' and self._dragging:
            region = context.region
            # Drag up = thicker walls; drag down = thinner
            dy     = event.mouse_region_y - self._drag_start_y
            # Scale travel: full region height = half the mesh's perpendicular span
            perp_dims = [self._dims[i] for i in range(3) if i != self._axis_idx]
            span   = min(perp_dims) * 0.5 if perp_dims else 1.0
            travel = (dy / region.height) * span
            new_t  = max(0.001, self._drag_start_wall + travel)
            if new_t != self._wall_thickness:
                self._wall_thickness = new_t
                self._rebuild_cutter_batch()
                context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'WHEELUPMOUSE':
            self._wall_thickness = max(0.001, self._wall_thickness - 0.005)
            self._rebuild_cutter_batch()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        if event.type == 'WHEELDOWNMOUSE':
            self._wall_thickness += 0.005
            self._rebuild_cutter_batch()
            context.area.tag_redraw()
            return {'RUNNING_MODAL'}

        return {'PASS_THROUGH'}

    def _rebuild_cutter_batch(self):
        obj      = self._obj
        axis_idx = self._axis_idx
        dims     = self._dims

        # Compute per-axis scale factors for the cutter
        cutter_scale = [1.0, 1.0, 1.0]
        for i in range(3):
            d = dims[i]
            if d <= 0:
                continue
            if i != axis_idx:
                cutter_scale[i] = max((d - 2 * self._wall_thickness) / d, 0.001)
            else:
                cutter_scale[i] = (d + 2 * self._axis_overshoot) / d

        # Build a world matrix that applies the cutter scale centered on the bbox center,
        # not the object origin, so overshoot expands symmetrically on both sides.
        loc, rot, orig_scale = obj.matrix_world.decompose()
        bb = obj.bound_box  # 8 corners in local space
        local_center = Vector(sum((Vector(v) for v in bb), Vector()) / 8)
        world_center = obj.matrix_world @ local_center
        cutter_mx = (
            Matrix.Translation(world_center)
            @ rot.to_matrix().to_4x4()
            @ Matrix.Diagonal(Vector([orig_scale[i] * cutter_scale[i] for i in range(3)] + [1.0]))
            @ Matrix.Translation(-local_center)
        )

        self._batch_cutter = _build_wire_batch(obj, override_mx=cutter_mx)

    def _draw_callback(self, context):
        if not self._batch_cutter:
            return
        shader = self._shader
        gpu.state.depth_test_set('ALWAYS')
        gpu.state.blend_set('NONE')
        shader.bind()
        shader.uniform_float("color", COLOR_CUTTER)
        self._batch_cutter.draw(shader)
        gpu.state.depth_test_set('LESS_EQUAL')

    def _draw_hud(self, context):
        axis_name = ('X', 'Y', 'Z')[self._axis_idx]
        fill_str  = "ON" if self._fill_cut else "OFF"
        line1 = f"Hollow Mesh  |  Open axis: {axis_name}  |  Thickness: {self._wall_thickness:.4f} m  |  Overshoot: {self._axis_overshoot:.4f} m  |  Fill boundary: {fill_str}"
        line2 = "Drag=thickness  Scroll=fine  R=cycle axis  Q/E=overshoot  F=toggle fill  Enter=confirm  RMB/Esc=cancel"
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
        return self._apply_hollow(context)

    def _apply_hollow(self, context):
        obj          = self._obj
        axis_idx     = self._axis_idx
        axis_name    = ('X', 'Y', 'Z')[axis_idx]
        t            = self._wall_thickness
        overshoot    = self._axis_overshoot
        dims         = self._dims

        # Persist settings
        s = context.scene
        s.hollow_mesh_axis           = axis_name
        s.hollow_mesh_wall_thickness = t
        s.hollow_mesh_axis_overshoot = overshoot

        # Duplicate original as the boolean cutter
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        context.view_layer.objects.active = obj
        bpy.ops.object.duplicate(linked=False)
        dup = context.active_object
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

        scale = [1.0, 1.0, 1.0]
        for i in range(3):
            d = dims[i]
            if d <= 0:
                continue
            if i != axis_idx:
                scale[i] = max((d - 2 * t) / d, 0.001)
            else:
                scale[i] = (d + 2 * overshoot) / d

        # Scale around the bbox center so overshoot expands symmetrically on both sides.
        # After transform_apply(rotation=True, scale=True), dup has identity rot/scale,
        # so bound_box corners are in local space == world space offset by location.
        bb = dup.bound_box
        local_center = sum((Vector(v) for v in bb), Vector()) / 8
        # Temporarily move origin to bbox center, scale, move back.
        dup.location += local_center
        for v in dup.data.vertices:
            v.co -= local_center
        dup.scale = tuple(scale)
        bpy.ops.object.select_all(action='DESELECT')
        dup.select_set(True)
        context.view_layer.objects.active = dup
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)

        # Remove faces whose normal aligns with the open axis so the cutter is an
        # open shell — prevents the boolean from capping the holes it cuts.
        bpy.ops.object.select_all(action='DESELECT')
        dup.select_set(True)
        context.view_layer.objects.active = dup
        bpy.ops.object.mode_set(mode='EDIT')
        bm_dup = bmesh.from_edit_mesh(dup.data)
        axis_vec = Vector([1.0 if i == axis_idx else 0.0 for i in range(3)])
        cap_faces = [f for f in bm_dup.faces if abs(f.normal.dot(axis_vec)) > 0.99]
        if cap_faces:
            bmesh.ops.delete(bm_dup, geom=cap_faces, context='FACES')
        bmesh.update_edit_mesh(dup.data)
        bpy.ops.object.mode_set(mode='OBJECT')

        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        context.view_layer.objects.active = obj

        mod = obj.modifiers.new("_hol_inner", 'BOOLEAN')
        mod.operation = 'DIFFERENCE'
        mod.object    = dup
        mod.solver    = 'EXACT'

        try:
            bpy.ops.object.modifier_apply(modifier=mod.name)
        except Exception as e:
            obj.modifiers.remove(mod)
            self.report({'ERROR'}, f"Boolean failed: {e}")
            bpy.data.objects.remove(dup, do_unlink=True)
            return {'CANCELLED'}

        bpy.data.objects.remove(dup, do_unlink=True)

        # Optionally fill open boundary loops left by the boolean
        if self._fill_cut:
            bpy.ops.object.select_all(action='DESELECT')
            obj.select_set(True)
            context.view_layer.objects.active = obj
            bpy.ops.object.mode_set(mode='EDIT')
            bm = bmesh.from_edit_mesh(obj.data)
            bm.edges.ensure_lookup_table()
            boundary_edges = [e for e in bm.edges if e.is_boundary]
            if boundary_edges:
                bmesh.ops.contextual_create(bm, geom=boundary_edges)
                bmesh.update_edit_mesh(obj.data)
            bpy.ops.object.mode_set(mode='OBJECT')

        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        context.view_layer.objects.active = obj

        self.report({'INFO'}, f"Hollowed along {axis_name} | thickness={t:.4f}m | overshoot={overshoot:.4f}m")
        return {'FINISHED'}


# ------------------------------------------------------------------

def _menu_func(self, context):
    self.layout.operator(MESH_OT_hollow_mesh.bl_idname, icon='MOD_SOLIDIFY')


def _register_scene_props():
    for stale in ("hollow_mesh_top_radius", "hollow_mesh_bottom_radius"):
        try:
            delattr(bpy.types.Scene, stale)
        except Exception:
            pass

    bpy.types.Scene.hollow_mesh_axis = bpy.props.EnumProperty(
        name="Hollow Axis",
        items=[('X', "X", ""), ('Y', "Y", ""), ('Z', "Z", "")],
        default='Z',
    )
    bpy.types.Scene.hollow_mesh_wall_thickness = bpy.props.FloatProperty(
        name="Hollow Wall Thickness", default=0.05, min=0.001, unit='LENGTH',
    )
    bpy.types.Scene.hollow_mesh_axis_overshoot = bpy.props.FloatProperty(
        name="Hollow Axis Overshoot", default=0.05, min=0.001, unit='LENGTH',
    )


def _unregister_scene_props():
    for attr in ("hollow_mesh_axis", "hollow_mesh_wall_thickness", "hollow_mesh_axis_overshoot"):
        try:
            delattr(bpy.types.Scene, attr)
        except Exception:
            pass


def register():
    try:
        bpy.utils.unregister_class(MESH_OT_hollow_mesh)
    except Exception:
        pass
    bpy.utils.register_class(MESH_OT_hollow_mesh)
    _register_scene_props()

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
        bpy.utils.unregister_class(MESH_OT_hollow_mesh)
    except Exception:
        pass
    _unregister_scene_props()
    if hasattr(bpy.types.VIEW3D_MT_object, '_dyn_ui_initialize'):
        for fn in list(bpy.types.VIEW3D_MT_object._dyn_ui_initialize()):
            code = getattr(getattr(fn, '__func__', fn), '__code__', None)
            if code and code.co_filename == _THIS_FILE:
                try:
                    bpy.types.VIEW3D_MT_object.remove(fn)
                except Exception:
                    pass
