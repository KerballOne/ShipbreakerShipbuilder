import bpy

bl_info = {
    "name": "Reload Startup Scripts",
    "author": "KerballOne",
    "version": (1, 0),
    "blender": (4, 0, 0),
    "category": "Development",
    "description": "Reload all startup scripts without restarting Blender",
}


class DEV_OT_reload_startup_scripts(bpy.types.Operator):
    bl_idname = "dev.reload_startup_scripts"
    bl_label = "Reload Startup Scripts"
    bl_description = "Re-execute all startup scripts from registered script directories (Ctrl+Shift+R)"

    def execute(self, context):
        import os

        count = 0
        errors = []
        for d in bpy.utils.script_paths(subdir="startup"):
            if not os.path.isdir(d):
                continue
            for fname in sorted(os.listdir(d)):
                if not fname.endswith(".py") or fname.startswith("_"):
                    continue
                fpath = os.path.join(d, fname)
                try:
                    with open(fpath, "r", encoding="utf-8") as f:
                        src = f.read()
                    exec(compile(src, fpath, "exec"), {"__file__": fpath, "__name__": "__main__"})
                    count += 1
                except Exception as e:
                    errors.append(f"{fname}: {e}")

        for e in errors:
            self.report({'WARNING'}, e)
        self.report({'INFO'}, f"Reloaded {count} startup script(s)")
        return {'FINISHED'}


def _clean_keymaps():
    wm = bpy.context.window_manager
    kc = wm.keyconfigs.addon
    if not kc:
        return
    for km in kc.keymaps:
        for kmi in list(km.keymap_items):
            if kmi.idname == DEV_OT_reload_startup_scripts.bl_idname:
                km.keymap_items.remove(kmi)


def register():
    try:
        bpy.utils.unregister_class(DEV_OT_reload_startup_scripts)
    except Exception:
        pass
    bpy.utils.register_class(DEV_OT_reload_startup_scripts)

    _clean_keymaps()
    wm = bpy.context.window_manager
    kc = wm.keyconfigs.addon
    if kc:
        km = kc.keymaps.new(name='3D View', space_type='VIEW_3D')
        km.keymap_items.new(
            DEV_OT_reload_startup_scripts.bl_idname,
            type='R', value='PRESS',
            shift=True, ctrl=True,
        )


def unregister():
    _clean_keymaps()
    try:
        bpy.utils.unregister_class(DEV_OT_reload_startup_scripts)
    except Exception:
        pass


register()
