import bpy
import bmesh

bl_info = {
    "name": "Split By Large Faces",
    "author": "KerballOne",
    "version": (1, 0),
    "blender": (4, 0, 0),
    "category": "Object",
    "description": "Separate regions bounded by sharp edges that contain a face exceeding an area threshold into their own objects",
}


class OBJECT_OT_split_by_large_faces(bpy.types.Operator):
    bl_idname  = "object.split_by_large_faces"
    bl_label   = "Split By Large Faces"
    bl_options = {'REGISTER', 'UNDO'}

    min_area: bpy.props.FloatProperty(
        name="Minimum Area (m²)",
        description=(
            "Any sharp-edge-delimited region containing at least one face "
            "larger than this will be separated into its own object"
        ),
        default=10.0,
        min=0.0001,
        subtype='UNSIGNED',
    )

    @classmethod
    def poll(cls, context):
        obj = context.active_object
        return (context.mode == 'OBJECT'
                and obj is not None and obj.type == 'MESH')

    def execute(self, context):
        obj = context.active_object
        if obj is None or obj.type != 'MESH':
            self.report({'ERROR'}, "Active object must be a mesh")
            return {'CANCELLED'}

        bpy.ops.object.select_all(action='DESELECT')
        obj.select_set(True)
        context.view_layer.objects.active = obj

        bpy.ops.object.mode_set(mode='EDIT')
        bpy.ops.mesh.select_mode(type='FACE')

        created = []
        handled = set()

        while True:
            bm = bmesh.from_edit_mesh(obj.data)
            bm.faces.ensure_lookup_table()

            seed = next(
                (f for f in bm.faces
                 if f.index not in handled and f.calc_area() > self.min_area),
                None,
            )
            if seed is None:
                break

            bpy.ops.mesh.select_all(action='DESELECT')
            seed.select = True
            bm.faces.active = seed
            bmesh.update_edit_mesh(obj.data)

            bpy.ops.mesh.select_linked(delimit={'SHARP'})

            bm = bmesh.from_edit_mesh(obj.data)
            bm.faces.ensure_lookup_table()
            region = {f.index for f in bm.faces if f.select}
            handled |= region

            bpy.ops.mesh.separate(type='SELECTED')

            pieces = [o for o in context.selected_objects if o is not obj and o.type == 'MESH']
            created.extend(pieces)

        bpy.ops.object.mode_set(mode='OBJECT')

        bpy.ops.object.select_all(action='DESELECT')
        for piece in created:
            piece.select_set(True)
        if created:
            context.view_layer.objects.active = created[-1]

        self.report({'INFO'}, f"Separated {len(created)} region(s) with faces exceeding {self.min_area:.2f} m²")
        return {'FINISHED'}


class VIEW3D_PT_split_by_large_faces(bpy.types.Panel):
    bl_space_type  = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category    = 'ShipBreaker'
    bl_label       = 'Split By Large Faces'
    bl_options     = {'DEFAULT_CLOSED'}

    def draw(self, context):
        layout = self.layout
        obj = context.active_object

        col = layout.column(align=True)
        col.label(text=f"Active: {obj.name if obj else '<none>'}")

        layout.prop(context.scene, "sblf_min_area")
        op = layout.operator(OBJECT_OT_split_by_large_faces.bl_idname,
                              text="Split By Large Faces", icon='MOD_EDGESPLIT')
        op.min_area = context.scene.sblf_min_area


def register():
    for cls in (OBJECT_OT_split_by_large_faces, VIEW3D_PT_split_by_large_faces):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)
    bpy.types.Scene.sblf_min_area = bpy.props.FloatProperty(
        name="Minimum Area (m²)",
        description=(
            "Any sharp-edge-delimited region containing at least one face "
            "larger than this will be separated into its own object"
        ),
        default=10.0,
        min=0.0001,
        subtype='UNSIGNED',
    )


def unregister():
    for cls in (OBJECT_OT_split_by_large_faces, VIEW3D_PT_split_by_large_faces):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
    del bpy.types.Scene.sblf_min_area
