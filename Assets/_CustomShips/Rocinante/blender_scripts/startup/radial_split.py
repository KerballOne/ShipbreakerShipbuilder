import bpy
import bmesh
import math
from mathutils import Vector, Matrix

bl_info = {
    "name": "Radial Split",
    "author": "KerballOne",
    "version": (1, 1),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Split selected object into N radial pie-slice segments",
}


class MESH_OT_radial_split(bpy.types.Operator):
    bl_idname = "mesh.radial_split"
    bl_label = "Radial Split"
    bl_description = "Split the selected mesh into N equal radial pie-slice segments around a chosen axis. Original is hidden (not deleted) so you can undo."
    bl_options = {'REGISTER', 'UNDO'}

    segments: bpy.props.IntProperty(
        name="Segments",
        description="Number of radial segments to split into",
        default=8,
        min=2,
        max=64,
    )

    axis: bpy.props.EnumProperty(
        name="Axis",
        description="Local axis to split around",
        items=[
            ('X', "X", "Split around local X axis"),
            ('Y', "Y", "Split around local Y axis"),
            ('Z', "Z", "Split around local Z axis"),
        ],
        default='Y',
    )

    offset_deg: bpy.props.FloatProperty(
        name="Angle Offset",
        description="Rotate all cuts by this many degrees",
        default=0.0,
        min=-180.0,
        max=180.0,
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

        axis_map = {
            'X': Vector((1, 0, 0)),
            'Y': Vector((0, 1, 0)),
            'Z': Vector((0, 0, 1)),
        }
        ref_map = {
            'X': Vector((0, 1, 0)),
            'Y': Vector((1, 0, 0)),
            'Z': Vector((1, 0, 0)),
        }
        up  = axis_map[self.axis]
        ref = ref_map[self.axis]

        # Pre-compute the N boundary plane normals
        # Boundary i sits between segment i and i+1
        # at angle: offset + i * angle_step + angle_step/2 ...
        # actually boundary normals are at: offset + i * angle_step
        boundary_normals = []
        for i in range(n):
            angle = offset_rad + i * angle_step
            rot = Matrix.Rotation(angle, 4, up)
            boundary_normals.append(rot @ ref)

        base_name = original.name.split(".")[0]

        # Create a collection to hold the segments, nested under the original's collection
        col_name = base_name
        seg_collection = bpy.data.collections.get(col_name)
        if seg_collection is None:
            seg_collection = bpy.data.collections.new(col_name)
        # Link into the same parent collection as the original
        parent_col = next(
            (c for c in bpy.data.collections if original.name in c.objects),
            context.scene.collection
        )
        if col_name not in parent_col.children:
            parent_col.children.link(seg_collection)

        results = []

        for seg in range(n):
            # Duplicate original for this segment
            bpy.ops.object.select_all(action='DESELECT')
            original.select_set(True)
            context.view_layer.objects.active = original
            bpy.ops.object.duplicate(linked=False)
            dup = context.active_object

            # Each segment lives between boundary[seg] and boundary[(seg+1) % n]
            # boundary[seg]       -> clear_outer=True  (remove the outer side)
            # boundary[(seg+1)%n] -> clear_inner=True  (remove the inner side)
            n1 = boundary_normals[seg]
            n2 = boundary_normals[(seg + 1) % n]

            self._bisect(dup, n1, clear_inner=True,  clear_outer=False)
            self._bisect(dup, n2, clear_inner=False, clear_outer=True)

            if self._has_geometry(dup):
                dup.name = f"{base_name}_seg{seg+1:02d}"
                if dup.data:
                    dup.data.name = dup.name
                # Move into segments collection
                for c in list(dup.users_collection):
                    c.objects.unlink(dup)
                seg_collection.objects.link(dup)
                results.append(dup)
            else:
                bpy.data.objects.remove(dup, do_unlink=True)

        # Hide original, select results
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
            use_fill=True,
            clear_inner=clear_inner,
            clear_outer=clear_outer,
            threshold=0.0001,
        )
        bpy.ops.object.mode_set(mode='OBJECT')

    def _has_geometry(self, obj):
        return obj and obj.data and len(obj.data.vertices) > 0


def menu_func(self, context):
    self.layout.operator(MESH_OT_radial_split.bl_idname, icon='MOD_BOOLEAN')


_NS_KEY = "radial_split_menu_func"


def register():
    try:
        bpy.utils.unregister_class(MESH_OT_radial_split)
    except Exception:
        pass
    bpy.utils.register_class(MESH_OT_radial_split)

    # Remove all previously registered menu funcs for this operator (cleanup duplicates)
    for fn in list(bpy.types.VIEW3D_MT_object._dyn_ui_initialize()):
        if getattr(fn, '__name__', '') == 'menu_func':
            co = getattr(fn, '__code__', None)
            if co and 'radial_split' in getattr(co, 'co_filename', ''):
                try:
                    bpy.types.VIEW3D_MT_object.remove(fn)
                except Exception:
                    pass

    bpy.types.VIEW3D_MT_object.append(menu_func)
    bpy.app.driver_namespace[_NS_KEY] = menu_func


def unregister():
    try:
        bpy.utils.unregister_class(MESH_OT_radial_split)
    except Exception:
        pass
    old = bpy.app.driver_namespace.pop(_NS_KEY, None)
    if old is not None:
        try:
            bpy.types.VIEW3D_MT_object.remove(old)
        except Exception:
            pass


register()
