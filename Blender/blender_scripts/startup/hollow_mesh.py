import bpy
import bmesh
from mathutils import Vector

bl_info = {
    "name": "Hollow Mesh",
    "author": "KerballOne",
    "version": (2, 0),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Hollow out a mesh by subtracting a copy scaled inward on the two non-axis dimensions",
}


class MESH_OT_hollow_mesh(bpy.types.Operator):
    bl_idname = "mesh.hollow_mesh"
    bl_label = "Hollow Mesh"
    bl_description = (
        "Hollow the selected mesh: subtract a copy scaled inward on the two axes "
        "perpendicular to the chosen open axis, leaving a shell of the specified wall thickness."
    )
    bl_options = {'REGISTER', 'UNDO'}

    axis: bpy.props.EnumProperty(
        name="Open Axis",
        description="Axis the hollow runs along (top/bottom stay open along this axis)",
        items=[
            ('X', "X", ""),
            ('Y', "Y", ""),
            ('Z', "Z", ""),
        ],
        default='Z',
    )

    wall_thickness: bpy.props.FloatProperty(
        name="Wall Thickness",
        description="Thickness of the remaining shell on the two perpendicular axes",
        default=0.05,
        min=0.001,
        unit='LENGTH',
    )

    axis_overshoot: bpy.props.FloatProperty(
        name="Axis Overshoot",
        description="How far the cutter extends past the top and bottom surfaces along the open axis (prevents coplanar boolean artifacts)",
        default=0.05,
        min=0.001,
        unit='LENGTH',
    )

    def invoke(self, context, event):
        s = context.scene
        self.axis           = s.hollow_mesh_axis
        self.wall_thickness = s.hollow_mesh_wall_thickness
        self.axis_overshoot = s.hollow_mesh_axis_overshoot
        return context.window_manager.invoke_props_dialog(self)

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "axis")
        layout.prop(self, "wall_thickness")
        layout.prop(self, "axis_overshoot")

    def execute(self, context):
        obj = context.active_object
        if obj is None or obj.type != 'MESH':
            self.report({'ERROR'}, "Select a mesh object first")
            return {'CANCELLED'}

        # Persist settings
        s = context.scene
        s.hollow_mesh_axis            = self.axis
        s.hollow_mesh_wall_thickness  = self.wall_thickness
        s.hollow_mesh_axis_overshoot  = self.axis_overshoot

        # Duplicate the object to use as the boolean cutter
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        context.view_layer.objects.active = obj
        bpy.ops.object.duplicate(linked=False)
        dup = context.active_object

        # Apply all transforms so the bounding box is reliable in local space
        bpy.ops.object.mode_set(mode='OBJECT')
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

        # Compute scale factors for the two perpendicular axes only
        bbox = dup.bound_box  # 8 corners in local space
        dims = [
            max(v[i] for v in bbox) - min(v[i] for v in bbox)
            for i in range(3)
        ]

        t = self.wall_thickness
        overshoot = self.axis_overshoot
        scale = [1.0, 1.0, 1.0]
        for i, axis_name in enumerate(('X', 'Y', 'Z')):
            d = dims[i]
            if d <= 0:
                continue
            if axis_name != self.axis:
                # Shrink inward by wall_thickness on each side
                scale[i] = max((d - 2 * t) / d, 0.001)
            else:
                # Expand outward so cutter protrudes past top and bottom surfaces
                scale[i] = (d + 2 * overshoot) / d

        dup.scale = tuple(scale)
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)

        # Boolean-subtract the shrunken cutter from the original
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        context.view_layer.objects.active = obj

        mod = obj.modifiers.new("_hol_inner", 'BOOLEAN')
        mod.operation = 'DIFFERENCE'
        mod.object = dup
        mod.solver = 'EXACT'

        try:
            bpy.ops.object.modifier_apply(modifier=mod.name)
        except Exception as e:
            obj.modifiers.remove(mod)
            self.report({'ERROR'}, f"Boolean failed: {e}")
            bpy.data.objects.remove(dup, do_unlink=True)
            return {'CANCELLED'}

        bpy.data.objects.remove(dup, do_unlink=True)

        # Fill any open boundary loops left by the boolean
        self._fill_boundary_loops(context, obj)

        # Restore selection
        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        context.view_layer.objects.active = obj

        self.report({'INFO'}, "Mesh hollowed successfully")
        return {'FINISHED'}

    def _fill_boundary_loops(self, context, obj):
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


# ------------------------------------------------------------------

def _menu_func(self, context):
    self.layout.operator(MESH_OT_hollow_mesh.bl_idname, icon='MOD_SOLIDIFY')


def _register_scene_props():
    # Clean up any stale props from previous versions first
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
    for attr in (
        "hollow_mesh_axis",
        "hollow_mesh_wall_thickness",
        "hollow_mesh_axis_overshoot",
    ):
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

    to_remove = [
        fn for fn in bpy.types.VIEW3D_MT_object._dyn_ui_initialize()
        if getattr(fn, '__func__', fn).__code__.co_filename.endswith('hollow_mesh.py')
    ] if hasattr(bpy.types.VIEW3D_MT_object, '_dyn_ui_initialize') else []
    for fn in to_remove:
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
    to_remove = [
        fn for fn in bpy.types.VIEW3D_MT_object._dyn_ui_initialize()
        if getattr(fn, '__func__', fn).__code__.co_filename.endswith('hollow_mesh.py')
    ] if hasattr(bpy.types.VIEW3D_MT_object, '_dyn_ui_initialize') else []
    for fn in to_remove:
        try:
            bpy.types.VIEW3D_MT_object.remove(fn)
        except Exception:
            pass
