import bpy
import os
import shutil
import json

_PREFS_KEY = "export_collection_last_dir"

def _get_last_dir():
    return bpy.app.driver_namespace.get(_PREFS_KEY)

def _set_last_dir(path):
    bpy.app.driver_namespace[_PREFS_KEY] = path

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
    scale:    bpy.props.FloatProperty(name="Scale", default=1.0, min=0.001)

    def invoke(self, context, event):
        col = context.collection
        if col is None:
            self.report({'ERROR'}, "No active collection")
            return {'CANCELLED'}
        # Use last exported directory, or default to blend file folder
        directory = _get_last_dir() or (os.path.dirname(bpy.data.filepath) if bpy.data.filepath else os.path.expanduser("~"))
        self.filepath = os.path.join(directory, col.name + ".fbx")
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
            global_scale        = self.scale,
            apply_scale_options = 'FBX_SCALE_ALL',
            axis_forward        = 'Y',
            axis_up             = 'Z',
            apply_unit_scale    = True,
            use_space_transform = True,
            bake_space_transform= True,
            mesh_smooth_type    = 'FACE',
            use_mesh_modifiers  = True,
        )

        _set_last_dir(os.path.dirname(self.filepath))

        copied = self._copy_textures(selected, self.filepath)
        msg = f"Exported {len(selected)} mesh(es) to {self.filepath}"
        if copied:
            msg += f"; copied {len(copied)} texture(s)"
        self.report({'INFO'}, msg)
        return {'FINISHED'}

    def _copy_textures(self, objects, fbx_path):
        fbx_dir  = os.path.dirname(fbx_path)
        fbx_name = os.path.splitext(os.path.basename(fbx_path))[0]
        tex_dir  = os.path.join(os.path.dirname(fbx_dir), "Textures")

        # Collect mat_name → {suffix → filename} and all unique source paths
        # suffix: BaseColor / Normal / Metallic / Roughness / AO etc (stem after last _)
        mat_tex_map = {}  # mat_name → {suffix → dest_filename}
        seen_srcs   = {}  # dest filename → src absolute path

        for obj in objects:
            if obj.type != 'MESH':
                continue
            for slot in obj.material_slots:
                mat = slot.material
                if mat is None or not mat.use_nodes:
                    continue
                if mat.name not in mat_tex_map:
                    mat_tex_map[mat.name] = {}
                for node in mat.node_tree.nodes:
                    if node.type != 'TEX_IMAGE' or node.image is None:
                        continue
                    src = bpy.path.abspath(node.image.filepath)
                    if not src:
                        continue
                    fname = os.path.basename(src)
                    stem  = os.path.splitext(fname)[0]
                    # Suffix is everything after the last underscore e.g. BaseColor, Normal
                    suffix = stem.rsplit('_', 1)[-1] if '_' in stem else stem
                    mat_tex_map[mat.name][suffix] = fname
                    if fname not in seen_srcs:
                        seen_srcs[fname] = src

        if not seen_srcs:
            return []

        os.makedirs(tex_dir, exist_ok=True)
        copied = []
        for fname, src in seen_srcs.items():
            if not os.path.isfile(src):
                self.report({'WARNING'}, f"Texture not on disk, skipping: {src}")
                continue
            dest = os.path.join(tex_dir, fname)
            if not os.path.exists(dest):
                shutil.copy2(src, dest)
                copied.append(fname)

        # Write sidecar JSON next to the FBX so Unity knows the mat→texture mapping
        sidecar = os.path.join(fbx_dir, fbx_name + ".textures.json")
        with open(sidecar, 'w') as f:
            json.dump(mat_tex_map, f, indent=2)

        return copied


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
