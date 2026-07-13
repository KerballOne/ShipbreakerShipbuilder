import bpy

bl_info = {
    "name": "Normalize UV Maps For Join",
    "author": "KerballOne",
    "version": (1, 0),
    "blender": (4, 0, 0),
    "category": "Object",
    "description": "Add empty UV map slots so all selected objects share the same primary UV map names in the same list order before joining",
}


class OBJECT_OT_normalize_uv_maps_for_join(bpy.types.Operator):
    bl_idname  = "object.normalize_uv_maps_for_join"
    bl_label   = "Normalize UV Maps For Join"
    bl_options = {'REGISTER', 'UNDO'}

    @classmethod
    def poll(cls, context):
        return len([o for o in context.selected_objects if o.type == 'MESH']) >= 2

    def execute(self, context):
        objs = [o for o in context.selected_objects if o.type == 'MESH']

        # Blender's Join merges UV map slots by list position, not by name.
        # Collect each selected object's primary (first) UV map name, deduped,
        # in the order objects were selected, then make sure every object has
        # an empty placeholder slot for each name it's missing so slot
        # positions line up by name across all objects before joining.
        primary_names = []
        for o in objs:
            uv_layers = o.data.uv_layers
            if len(uv_layers) == 0:
                continue
            name = uv_layers[0].name
            if name not in primary_names:
                primary_names.append(name)

        if len(primary_names) < 2:
            self.report({'INFO'}, "Selected objects already share a single primary UV map name; nothing to normalize")
            return {'FINISHED'}

        added = 0
        for o in objs:
            existing_names = {uv.name for uv in o.data.uv_layers}
            for name in primary_names:
                if name not in existing_names:
                    o.data.uv_layers.new(name=name)
                    added += 1

        self.report({'INFO'}, f"Added {added} placeholder UV map slot(s) across {len(objs)} object(s) for names: {', '.join(primary_names)}")
        return {'FINISHED'}


class VIEW3D_PT_normalize_uv_maps_for_join(bpy.types.Panel):
    bl_space_type  = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category    = 'ShipBreaker'
    bl_label       = 'Normalize UV Maps For Join'
    bl_options     = {'DEFAULT_CLOSED'}

    def draw(self, context):
        layout = self.layout
        objs = [o for o in context.selected_objects if o.type == 'MESH']

        col = layout.column(align=True)
        col.label(text=f"Selected mesh objects: {len(objs)}")
        for o in objs:
            names = [uv.name for uv in o.data.uv_layers]
            col.label(text=f"  {o.name}: {names if names else '<none>'}")

        layout.operator(OBJECT_OT_normalize_uv_maps_for_join.bl_idname,
                         text="Normalize UV Maps", icon='UV')
        layout.label(text="Run before Ctrl+J to prevent UV data loss.")


def register():
    for cls in (OBJECT_OT_normalize_uv_maps_for_join, VIEW3D_PT_normalize_uv_maps_for_join):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)


def unregister():
    for cls in (OBJECT_OT_normalize_uv_maps_for_join, VIEW3D_PT_normalize_uv_maps_for_join):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
