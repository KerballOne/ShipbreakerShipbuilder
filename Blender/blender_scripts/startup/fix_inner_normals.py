import bpy
import bmesh
import os
from mathutils import Vector

_LOG = r"C:\Users\user\source\repos\ShipbreakerShipbuilder\blender_debug.log"

def _log(msg):
    with open(_LOG, 'a') as f:
        f.write(msg + "\n")

bl_info = {
    "name": "Fix Inner Normals",
    "author": "KerballOne",
    "version": (1, 7),
    "blender": (4, 0, 0),
    "category": "Mesh",
    "description": "Fix inner shell normals using raycasts from the ring centre",
}

# --- Debug draw state --- stored in driver_namespace to survive reloads
def _draw_callback():
    import gpu
    from gpu_extras.batch import batch_for_shader
    lines = bpy.app.driver_namespace.get('fix_normals_lines', [])
    if not lines:
        return
    coords = []
    for a, b in lines:
        coords.append(a)
        coords.append(b)
    shader = gpu.shader.from_builtin('UNIFORM_COLOR')
    batch = batch_for_shader(shader, 'LINES', {"pos": coords})
    gpu.state.line_width_set(4.0)
    shader.bind()
    shader.uniform_float("color", (1.0, 1.0, 0.0, 1.0))
    batch.draw(shader)
    gpu.state.line_width_set(1.0)

def _install_draw_handler():
    # Only register once — the callback reads from driver_namespace so it
    # works across reloads without needing to re-register.
    if not bpy.app.driver_namespace.get('fix_normals_handler'):
        handler = bpy.types.SpaceView3D.draw_handler_add(
            _draw_callback, (), 'WINDOW', 'POST_VIEW')
        bpy.app.driver_namespace['fix_normals_handler'] = handler

def _remove_draw_handler():
    bpy.app.driver_namespace['fix_normals_lines'] = []


class MESH_OT_fix_inner_normals(bpy.types.Operator):
    bl_idname  = "mesh.fix_inner_normals"
    bl_label   = "Fix Inner Normals"
    bl_options = {'UNDO'}

    axis: bpy.props.EnumProperty(
        name="Ring Axis",
        items=[('Z', 'Z', ''), ('Y', 'Y', ''), ('X', 'X', '')],
        default='Z',
    )

    debug_rays: bpy.props.BoolProperty(
        name="Show Debug Rays",
        description="Draw 3 yellow ray lines in viewport instead of flipping",
        default=False,
    )

    @classmethod
    def poll(cls, context):
        return (context.mode == 'OBJECT' and
                any(o.type == 'MESH' for o in context.selected_objects))

    def execute(self, context):
        global _draw_lines
        meshes = [o for o in context.selected_objects if o.type == 'MESH']
        if not meshes:
            self.report({'ERROR'}, "No mesh objects selected")
            return {'CANCELLED'}

        axis_idx = 'XYZ'.index(self.axis)
        centre = meshes[0].location.copy()
        _log(f"ring centre={centre}")

        # Build combined world-space BVH
        from mathutils.bvhtree import BVHTree
        bm_combined = bmesh.new()
        for obj in meshes:
            temp_mesh = bpy.data.meshes.new("_tmp")
            bm_tmp = bmesh.new()
            bm_tmp.from_mesh(obj.data)
            for v in bm_tmp.verts:
                v.co = obj.matrix_world @ v.co
            bm_tmp.to_mesh(temp_mesh)
            bm_tmp.free()
            bm_combined.from_mesh(temp_mesh)
            bpy.data.meshes.remove(temp_mesh)
        combined_tree = BVHTree.FromBMesh(bm_combined)
        bm_combined.free()

        if self.debug_rays:
            # Draw 3 rays from first object's first 3 non-cap faces
            bpy.app.driver_namespace['fix_normals_lines'] = []
            obj = meshes[0]
            bm = bmesh.new()
            bm.from_mesh(obj.data)
            bm.faces.ensure_lookup_table()
            count = 0
            for face in bm.faces:
                # Only faces that are mostly radial — XY magnitude must dominate Z
                radial = Vector(face.normal)
                radial[axis_idx] = 0.0
                if radial.length < 0.7:  # normal is less than 70% radial — skip
                    continue
                if count >= 3:
                    break
                world_face_centre = obj.matrix_world @ face.calc_center_median()
                target = centre.copy()
                target[axis_idx] = world_face_centre[axis_idx]
                ray_dir = (target - world_face_centre)
                dist_to_centre = ray_dir.length
                if dist_to_centre < 1e-6:
                    continue
                ray_dir = ray_dir.normalized()
                ray_origin = world_face_centre + ray_dir * 1e-4
                hit_loc, hit_norm, hit_idx, hit_dist = combined_tree.ray_cast(ray_origin, ray_dir, dist_to_centre)
                hit_str = f"face{hit_idx} at {hit_dist:.2f}" if hit_idx is not None else "NONE"
                _log(f"  ray{count}: from={world_face_centre.x:.2f},{world_face_centre.y:.2f} → {hit_str}")
                bpy.app.driver_namespace.setdefault('fix_normals_lines', []).append((world_face_centre.copy(), target.copy()))
                count += 1
            bm.free()
            _install_draw_handler()
            for area in context.screen.areas:
                if area.type == 'VIEW_3D':
                    area.tag_redraw()
            self.report({'INFO'}, f"Drew {count} debug rays — check viewport")
            return {'FINISHED'}

        # Clear custom split normals
        prev_active = context.view_layer.objects.active
        for obj in meshes:
            if obj.data.has_custom_normals:
                context.view_layer.objects.active = obj
                bpy.ops.object.mode_set(mode='EDIT')
                bpy.ops.mesh.customdata_custom_splitnormals_clear()
                bpy.ops.object.mode_set(mode='OBJECT')
        context.view_layer.objects.active = prev_active

        total_flipped = 0
        for obj in meshes:
            bm = bmesh.new()
            bm.from_mesh(obj.data)
            bm.faces.ensure_lookup_table()

            to_flip = set()
            for face in bm.faces:
                radial = Vector(face.normal)
                radial[axis_idx] = 0.0
                if radial.length < 0.7:
                    continue
                world_face_centre = obj.matrix_world @ face.calc_center_median()
                target = centre.copy()
                target[axis_idx] = world_face_centre[axis_idx]
                ray_dir = (target - world_face_centre)
                dist_to_centre = ray_dir.length
                if dist_to_centre < 1e-6:
                    continue
                ray_dir = ray_dir.normalized()
                ray_origin = world_face_centre + ray_dir * 1e-4
                hit_loc, hit_norm, hit_idx, hit_dist = combined_tree.ray_cast(ray_origin, ray_dir, dist_to_centre)
                if hit_idx is None:
                    to_flip.add(face.index)

            _log(f"  {obj.name}: {len(to_flip)} inner faces to flip")
            for idx in to_flip:
                bm.faces[idx].normal_flip()
            bm.to_mesh(obj.data)
            bm.free()
            obj.data.update()
            total_flipped += len(to_flip)

        self.report({'INFO'}, f"Flipped {total_flipped} face(s) across {len(meshes)} object(s)")
        return {'FINISHED'}

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def draw(self, context):
        self.layout.prop(self, "axis", expand=True)
        self.layout.prop(self, "debug_rays")


class MESH_OT_clear_debug_rays(bpy.types.Operator):
    bl_idname = "mesh.clear_debug_rays"
    bl_label  = "Clear Debug Rays"
    def execute(self, context):
        # Just clear the lines list — the callback checks this and draws nothing
        bpy.app.driver_namespace['fix_normals_lines'] = []
        for area in context.screen.areas:
            if area.type == 'VIEW_3D':
                area.tag_redraw()
        return {'FINISHED'}


class MESH_OT_check_mesh(bpy.types.Operator):
    bl_idname = "mesh.check_mesh_normals"
    bl_label  = "Check Mesh"
    bl_options = {'UNDO'}

    @classmethod
    def poll(cls, context):
        return (context.mode == 'OBJECT' and
                any(o.type == 'MESH' for o in context.selected_objects))

    def execute(self, context):
        meshes = [o for o in context.selected_objects if o.type == 'MESH']
        lines = []
        total_thin = 0
        for obj in meshes:
            bm = bmesh.new()
            bm.from_mesh(obj.data)
            bm.edges.ensure_lookup_table()
            bm.faces.ensure_lookup_table()

            non_manifold = [e for e in bm.edges if not e.is_manifold]
            thin_faces = set()
            for e in non_manifold:
                for f in e.link_faces:
                    thin_faces.add(f.index)

            # Also detect closed single-thickness shells via volume/area ratio
            volume = abs(bm.calc_volume())
            area = sum(f.calc_area() for f in bm.faces)
            # A flat shell has near-zero volume even if manifold
            is_flat_shell = area > 0 and (volume / area) < 0.01
            bm.free()

            total_thin += len(thin_faces)
            needs_solidify = len(non_manifold) > 0 or is_flat_shell
            reason = []
            if len(non_manifold) > 0:
                reason.append(f"{len(non_manifold)} open edges")
            if is_flat_shell:
                reason.append(f"flat shell (vol/area={volume/area:.4f})")
            lines.append(
                f"{obj.name}: {len(obj.data.polygons)} faces, "
                f"{len(thin_faces)} thin faces — "
                f"{'NEEDS SOLIDIFY (' + ', '.join(reason) + ')' if needs_solidify else 'OK'}"
            )
        report = "\n".join(lines)
        _log("=== Check Mesh ===\n" + report)

        # Mark thin faces selected on each mesh in Object Mode, then enter Edit Mode once
        if total_thin > 0:
            for obj in meshes:
                bm = bmesh.new()
                bm.from_mesh(obj.data)
                bm.edges.ensure_lookup_table()
                bm.faces.ensure_lookup_table()

                volume = abs(bm.calc_volume())
                area = sum(f.calc_area() for f in bm.faces)
                is_flat_shell = area > 0 and (volume / area) < 0.01

                thin_faces = set()
                for e in bm.edges:
                    if not e.is_manifold:
                        for f in e.link_faces:
                            thin_faces.add(f.index)

                for f in bm.faces:
                    # Select if open-edge thin face, or if whole object is a flat shell
                    f.select = f.index in thin_faces or is_flat_shell

                bm.to_mesh(obj.data)
                bm.free()
                obj.data.update()

            # Enter Edit Mode once with all selected objects active
            context.view_layer.objects.active = meshes[0]
            bpy.ops.object.mode_set(mode='EDIT')
            bpy.ops.mesh.select_mode(type='FACE')

        self.report({'INFO'}, f"Checked {len(meshes)} object(s) — {total_thin} thin faces selected")
        def draw_popup(self2, context):
            for line in lines:
                self2.layout.label(text=line)
        context.window_manager.popup_menu(draw_popup, title="Mesh Check Results", icon='INFO')
        return {'FINISHED'}


class MESH_OT_solidify_meshes(bpy.types.Operator):
    bl_idname  = "mesh.solidify_meshes"
    bl_label   = "Solidify Meshes"
    bl_options = {'UNDO'}

    thickness: bpy.props.FloatProperty(
        name="Thickness",
        description="Shell thickness in metres",
        default=0.02,
        min=0.0001,
        max=10.0,
        step=1,
        precision=4,
    )

    @classmethod
    def poll(cls, context):
        return (context.mode == 'OBJECT' and
                any(o.type == 'MESH' for o in context.selected_objects))

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self)

    def draw(self, context):
        self.layout.prop(self, "thickness")

    def execute(self, context):
        meshes = [o for o in context.selected_objects if o.type == 'MESH']
        prev_active = context.view_layer.objects.active
        for obj in meshes:
            mod = obj.modifiers.new(name="Solidify", type='SOLIDIFY')
            mod.thickness = self.thickness
            mod.offset = -1.0  # grow inward so outer surface stays in place
            mod.use_even_offset = True
            mod.use_quality_normals = True
            context.view_layer.objects.active = obj
            bpy.ops.object.modifier_apply(modifier=mod.name)
        context.view_layer.objects.active = prev_active or meshes[0]
        self.report({'INFO'}, f"Solidified {len(meshes)} object(s) at {self.thickness:.4f}m thickness")
        return {'FINISHED'}


class VIEW3D_PT_check_mesh(bpy.types.Panel):
    bl_space_type  = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category    = 'ShipBreaker'
    bl_label       = 'Check Mesh'
    bl_options     = {'DEFAULT_CLOSED'}

    def draw(self, context):
        self.layout.operator(MESH_OT_check_mesh.bl_idname,
                             text="Check Mesh", icon='VIEWZOOM')
        self.layout.separator()
        self.layout.operator("mesh.convex_hull_preview", text="Preview Convex Hull(s)", icon='MOD_MESHDEFORM')
        self.layout.operator("mesh.convex_hull_preview_clear", text="Clear Hull Preview", icon='X')


class VIEW3D_PT_fix_inner_normals(bpy.types.Panel):
    bl_space_type  = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category    = 'ShipBreaker'
    bl_label       = 'Fix Normals'
    bl_options     = {'DEFAULT_CLOSED'}

    def draw(self, context):
        self.layout.operator(MESH_OT_solidify_meshes.bl_idname,
                             text="Solidify", icon='MOD_SOLIDIFY')
        self.layout.operator(MESH_OT_fix_inner_normals.bl_idname,
                             text="Fix Inner Normals", icon='NORMALS_FACE')
        self.layout.operator(MESH_OT_clear_debug_rays.bl_idname,
                             text="Clear Debug Rays")


def register():
    _install_draw_handler()
    for cls in (MESH_OT_fix_inner_normals, MESH_OT_clear_debug_rays, MESH_OT_check_mesh, MESH_OT_solidify_meshes, VIEW3D_PT_check_mesh, VIEW3D_PT_fix_inner_normals):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)


def unregister():
    _remove_draw_handler()
    for cls in (MESH_OT_fix_inner_normals, MESH_OT_clear_debug_rays, MESH_OT_check_mesh, MESH_OT_solidify_meshes, VIEW3D_PT_check_mesh, VIEW3D_PT_fix_inner_normals):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
