import bpy

_LOG = r"C:\Users\user\AppData\Local\Temp\blender_reload.log"

def _log(msg):
    import datetime
    with open(_LOG, "a", encoding="utf-8") as f:
        f.write(f"{datetime.datetime.now()}: {msg}\n")

_log("reload_scripts.py loading")

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
                if fname == "reload_scripts.py":
                    continue
                fpath = os.path.join(d, fname)
                try:
                    with open(fpath, "r", encoding="utf-8") as f:
                        src = f.read()
                    # Unregister via old module before re-executing
                    old_ns = {"__file__": fpath, "__name__": "__main__", "__unregister_only__": True}
                    try:
                        exec(compile(src, fpath, "exec"), old_ns)
                        flag_seen = old_ns.get("__unregister_only__", "NOT FOUND")
                        _log(f"{fname}: __unregister_only__={flag_seen}, has unregister={'unregister' in old_ns}")
                        if "unregister" in old_ns:
                            old_ns["unregister"]()
                            _log(f"{fname}: unregister() called")
                    except Exception as e:
                        _log(f"{fname}: unregister phase error: {e}")
                    # Now re-execute fresh and explicitly call register()
                    new_ns = {"__file__": fpath, "__name__": "__main__"}
                    exec(compile(src, fpath, "exec"), new_ns)
                    if "register" in new_ns:
                        new_ns["register"]()
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


# Do NOT call register() here — Blender's startup loader calls it automatically.
# The reload script calls it explicitly after exec().
