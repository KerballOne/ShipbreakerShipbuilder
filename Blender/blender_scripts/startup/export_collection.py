import bpy
import os

bl_info = {
    "name": "Export Collection to Unity FBX",
    "author": "KerballOne",
    "version": (1, 0),
    "blender": (4, 0, 0),
    "category": "Import-Export",
    "description": "Export the active collection to FBX with Unity-correct settings",
}


class EXPORT_OT_collection_unity_fbx(bpy.types.Operator):
    bl_idname  = "export.collection_unity_fbx"
    bl_label   = "Export Collection to Unity FBX"
    bl_description = "Export all meshes in the active collection to FBX with Unity axis and scale settings"

    filepath: bpy.props.StringProperty(subtype="FILE_PATH")
    scale:    bpy.props.FloatProperty(name="Scale", default=5.0, min=0.001)

    def invoke(self, context, event):
        col = context.collection
        if col is None:
            self.report({'ERROR'}, "No active collection")
            return {'CANCELLED'}
        # Default filename = collection name, in same folder as current blend file
        blend_dir = os.path.dirname(bpy.data.filepath) if bpy.data.filepath else os.path.expanduser("~")
        self.filepath = os.path.join(blend_dir, col.name + ".fbx")
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        col = context.collection

        # Deselect all, then select every mesh object in the collection (recursive)
        bpy.ops.object.select_all(action='DESELECT')
        def select_meshes(collection):
            for obj in collection.objects:
                if obj.type == 'MESH':
                    obj.select_set(True)
            for child_col in collection.children:
                select_meshes(child_col)
        select_meshes(col)

        selected = [o for o in context.selected_objects]
        if not selected:
            self.report({'ERROR'}, f"No mesh objects found in collection '{col.name}'")
            return {'CANCELLED'}

        bpy.ops.export_scene.fbx(
            filepath            = self.filepath,
            use_selection       = True,
            object_types        = {'MESH'},
            scale               = self.scale,
            apply_scale_options = 'FBX_SCALE_ALL',
            axis_forward        = 'Y',
            axis_up             = 'Z',
            apply_unit_scale    = True,
            use_space_transform = True,
            bake_space_transform= True,
            mesh_smooth_type    = 'FACE',
            use_mesh_modifiers  = True,
        )

        self.report({'INFO'}, f"Exported {len(selected)} mesh(es) to {self.filepath}")
        return {'FINISHED'}


def menu_func(self, context):
    self.layout.operator(
        EXPORT_OT_collection_unity_fbx.bl_idname,
        text="Export Collection to Unity FBX",
        icon='EXPORT',
    )


def register():
    try:
        bpy.utils.unregister_class(EXPORT_OT_collection_unity_fbx)
    except Exception:
        pass
    bpy.utils.register_class(EXPORT_OT_collection_unity_fbx)

    to_remove = [
        fn for fn in bpy.types.VIEW3D_MT_object._dyn_ui_initialize()
        if getattr(fn, '__func__', fn).__code__.co_filename.endswith('export_collection.py')
    ] if hasattr(bpy.types.VIEW3D_MT_object, '_dyn_ui_initialize') else []
    for fn in to_remove:
        try:
            bpy.types.VIEW3D_MT_object.remove(fn)
        except Exception:
            pass
    bpy.types.VIEW3D_MT_object.append(menu_func)


def unregister():
    to_remove = [
        fn for fn in bpy.types.VIEW3D_MT_object._dyn_ui_initialize()
        if getattr(fn, '__func__', fn).__code__.co_filename.endswith('export_collection.py')
    ] if hasattr(bpy.types.VIEW3D_MT_object, '_dyn_ui_initialize') else []
    for fn in to_remove:
        try:
            bpy.types.VIEW3D_MT_object.remove(fn)
        except Exception:
            pass
    try:
        bpy.utils.unregister_class(EXPORT_OT_collection_unity_fbx)
    except Exception:
        pass
