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
    "description": "Export meshes from active collection or selected objects to Unity FBX",
}


def _get_export_source(context):
    """
    Returns (label, meshes) where label is what will be exported and meshes is the list.
    Priority:
      1. Selected objects in the viewport (including mesh children of selected parents)
      2. Active layer collection (if it's not the root Scene Collection and nothing is selected)
    """
    scene = context.scene

    # Priority: selected objects + their mesh children
    def mesh_children(obj):
        result = []
        if obj.type == 'MESH':
            result.append(obj)
        for child in obj.children:
            result.extend(mesh_children(child))
        return result

    if context.selected_objects:
        meshes = []
        seen = set()
        for obj in context.selected_objects:
            for m in mesh_children(obj):
                if m.name not in seen:
                    meshes.append(m)
                    seen.add(m.name)
        if meshes:
            base = context.active_object.name if context.active_object else meshes[0].name
            return base, meshes

    # Fall back to active layer collection when nothing is selected
    alc = context.view_layer.active_layer_collection
    if alc and alc.collection != scene.collection:
        col = alc.collection
        meshes = []
        def collect_meshes(c):
            for obj in c.objects:
                if obj.type == 'MESH':
                    meshes.append(obj)
            for child in c.children:
                collect_meshes(child)
        collect_meshes(col)
        if meshes:
            return col.name, meshes

    return None, []


class EXPORT_OT_collection_unity_fbx(bpy.types.Operator):
    bl_idname  = "export.collection_unity_fbx"
    bl_label   = "Export to Unity FBX"
    bl_description = "Export active collection or selected objects to FBX with Unity-correct settings"
    bl_options = {'REGISTER'}

    filepath:    bpy.props.StringProperty(subtype="FILE_PATH")
    export_name: bpy.props.StringProperty()  # locked in at invoke time

    def invoke(self, context, event):
        name, meshes = _get_export_source(context)
        if not meshes:
            self.report({'ERROR'}, "No meshes found — activate a collection or select objects")
            return {'CANCELLED'}

        self.export_name = name
        directory = _get_last_dir() or (os.path.dirname(bpy.data.filepath) if bpy.data.filepath else os.path.expanduser("~"))
        self.filepath = os.path.join(directory, name + ".fbx")
        context.window_manager.fileselect_add(self)
        return {'RUNNING_MODAL'}

    def execute(self, context):
        # If filepath wasn't set (e.g. called directly from Outliner menu), open the file dialog.
        if not self.filepath:
            return self.invoke(context, None)

        _, meshes = _get_export_source(context)
        if not meshes:
            self.report({'ERROR'}, "No meshes found")
            return {'CANCELLED'}

        # Select only the meshes we want to export
        bpy.ops.object.select_all(action='DESELECT')
        for obj in meshes:
            obj.select_set(True)

        bpy.ops.export_scene.fbx(
            filepath            = self.filepath,
            use_selection       = True,
            object_types        = {'MESH'},
            global_scale        = 1.0,
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

        copied = self._copy_textures(meshes, self.filepath)
        msg = f"Exported {len(meshes)} mesh(es) to {self.filepath}"
        if copied:
            msg += f"; copied {len(copied)} texture(s)"
        self.report({'INFO'}, msg)
        return {'FINISHED'}

    def _copy_textures(self, objects, fbx_path):
        fbx_dir  = os.path.dirname(fbx_path)
        fbx_name = os.path.splitext(os.path.basename(fbx_path))[0]
        tex_dir  = os.path.join(os.path.dirname(fbx_dir), "Textures")

        mat_tex_map = {}
        seen_srcs   = {}

        for obj in objects:
            for slot in obj.material_slots:
                mat = slot.material
                if mat is None or not mat.use_nodes:
                    continue
                mat_images = {}
                for node in mat.node_tree.nodes:
                    if node.type != 'TEX_IMAGE' or node.image is None:
                        continue
                    src = bpy.path.abspath(node.image.filepath)
                    if not src:
                        continue
                    fname  = os.path.basename(src)
                    stem   = os.path.splitext(fname)[0]
                    suffix = stem.rsplit('_', 1)[-1] if '_' in stem else stem
                    if suffix not in mat_images:
                        mat_images[suffix] = (fname, src)
                    if fname not in seen_srcs:
                        seen_srcs[fname] = src

                if 'BaseColor' in mat_images:
                    bc_stem = os.path.splitext(mat_images['BaseColor'][0])[0]
                    tex_set = bc_stem[:-len('_BaseColor')]
                else:
                    tex_set = mat.name

                if tex_set not in mat_tex_map:
                    mat_tex_map[tex_set] = {}
                for suffix, (fname, _) in mat_images.items():
                    mat_tex_map[tex_set][suffix] = fname

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

        sidecar = os.path.join(fbx_dir, fbx_name + ".textures.json")
        with open(sidecar, 'w') as f:
            json.dump(mat_tex_map, f, indent=2)

        return copied


class VIEW3D_PT_export_unity(bpy.types.Panel):
    bl_space_type  = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category    = 'ShipBreaker'
    bl_label       = 'ShipBreaker'

    def draw(self, context):
        layout = self.layout
        obj = context.active_object
        sel = context.selected_objects
        is_mesh = obj and obj.type == 'MESH'
        name, meshes = _get_export_source(context)

        # --- Selection info ---
        box = layout.box()
        if obj:
            if len(sel) > 1:
                box.label(text=f"{obj.name} (+{len(sel)-1} more)", icon='OBJECT_DATA')
            else:
                box.label(text=obj.name, icon='OBJECT_DATA')
        else:
            box.label(text="Nothing selected", icon='ERROR')

        layout.separator()

        # --- Tools ---
        layout.operator(EXPORT_OT_collection_unity_fbx.bl_idname, text="Export to Unity FBX", icon='EXPORT')
        layout.operator("object.group_selected", text="Group to Collection", icon='OUTLINER_COLLECTION')
        layout.separator()
        layout.operator("mesh.bisect_interactive", text="Interactive Bisect", icon='MOD_BOOLEAN')
        layout.operator("mesh.concavity_split", text="Concavity Split", icon='MOD_MESHDEFORM')
        layout.operator("mesh.radial_split", text="Radial Split", icon='MOD_ARRAY')
        layout.operator("mesh.hollow_mesh", text="Hollow Mesh", icon='MOD_SOLIDIFY')
        layout.separator()
        layout.operator("dev.reload_startup_scripts", text="Reload Scripts", icon='FILE_REFRESH')


class OBJECT_OT_group_selected(bpy.types.Operator):
    bl_idname  = "object.group_selected"
    bl_label   = "Group Selected"
    bl_description = "Create a new collection containing the selected objects, nested under their current collection"
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return len(context.selected_objects) > 0

    def execute(self, context):
        selected = list(context.selected_objects)
        if not selected:
            self.report({'ERROR'}, "Nothing selected")
            return {'CANCELLED'}

        # Find the parent collection — use the active layer collection, or the
        # first collection the selected objects share
        alc = context.view_layer.active_layer_collection
        parent_col = alc.collection if alc else context.scene.collection

        # Create the new collection
        new_col = bpy.data.collections.new("Group")
        parent_col.children.link(new_col)

        # Move each selected object into the new collection,
        # removing it from whichever collections it was in under parent_col
        for obj in selected:
            for col in list(obj.users_collection):
                col.objects.unlink(obj)
            new_col.objects.link(obj)

        # Make the new collection active in the outliner
        layer_col = self._find_layer_collection(context.view_layer.layer_collection, new_col.name)
        if layer_col:
            context.view_layer.active_layer_collection = layer_col

        self.report({'INFO'}, f"Grouped {len(selected)} object(s) into collection '{new_col.name}'")
        return {'FINISHED'}

    def _find_layer_collection(self, layer_col, name):
        if layer_col.collection.name == name:
            return layer_col
        for child in layer_col.children:
            result = self._find_layer_collection(child, name)
            if result:
                return result
        return None


def _menu_func(self, context):
    self.layout.operator(
        EXPORT_OT_collection_unity_fbx.bl_idname,
        text="Export to Unity FBX",
        icon='EXPORT',
    )

_MENU_TYPES = [
    "OUTLINER_MT_object",
    "OUTLINER_MT_collection",
    "OUTLINER_MT_collection_new",
]

def _remove_menu_entries(menu_type, filename):
    if hasattr(menu_type, '_dyn_ui_initialize'):
        for fn in list(menu_type._dyn_ui_initialize()):
            if getattr(fn, '__func__', fn).__code__.co_filename.endswith(filename):
                try:
                    menu_type.remove(fn)
                except Exception:
                    pass


def register():
    for cls in (EXPORT_OT_collection_unity_fbx, VIEW3D_PT_export_unity, OBJECT_OT_group_selected):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)

    # Remove leftover entries from all menus (including old Object menu)
    for name in _MENU_TYPES + ["VIEW3D_MT_object"]:
        mt = getattr(bpy.types, name, None)
        if mt:
            _remove_menu_entries(mt, 'export_collection.py')

    for name in _MENU_TYPES:
        mt = getattr(bpy.types, name, None)
        if mt:
            mt.append(_menu_func)


def unregister():
    for name in _MENU_TYPES + ["VIEW3D_MT_object"]:
        mt = getattr(bpy.types, name, None)
        if mt:
            _remove_menu_entries(mt, 'export_collection.py')
    for cls in (EXPORT_OT_collection_unity_fbx, VIEW3D_PT_export_unity, OBJECT_OT_group_selected):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
