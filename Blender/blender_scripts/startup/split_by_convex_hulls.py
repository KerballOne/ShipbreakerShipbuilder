import bpy
import bmesh

bl_info = {
    "name": "Split By Convex Hulls",
    "author": "KerballOne",
    "version": (1, 0),
    "blender": (4, 0, 0),
    "category": "Object",
    "description": "Cut the active textured mesh into pieces using selected convex hulls as boolean cutters",
}

_LOG = r"C:\Users\user\source\repos\ShipbreakerShipbuilder\blender_debug.log"


def _log(msg):
    with open(_LOG, 'a') as f:
        f.write(msg + "\n")


def _get_hulls_and_source(context):
    """Convention: select all UCX_* hull objects, active object is the source mesh to cut."""
    source = context.active_object
    hulls = [o for o in context.selected_objects
             if o.type == 'MESH' and o != source]
    return source, hulls


def _remove_boolean_cap_faces(piece, hull, min_uv_area=1e-8):
    """Delete faces of `piece` that are synthetic boolean caps introduced to
    close the solid where the hull cutter's boundary didn't align with the
    source mesh's real surface (rather than original textured geometry).

    Real, textured faces carry a meaningful UV island from the source mesh.
    Faces newly created by the boolean solver to close the cut have no
    corresponding source geometry, so their UVs collapse to zero area
    (a single point or degenerate sliver). A face is treated as a cap only
    if its UV island area is at/near zero.
    """
    bm = bmesh.new()
    bm.from_mesh(piece.data)
    bm.faces.ensure_lookup_table()
    uv_layer = bm.loops.layers.uv.active

    if uv_layer is None:
        _log(f"{piece.name} vs {hull.name}: no UV layer, skipping cap-face cleanup")
        bm.free()
        return 0

    cap_faces = []
    for face in bm.faces:
        uvs = [loop[uv_layer].uv for loop in face.loops]
        area = 0.0
        n = len(uvs)
        for i in range(n):
            x1, y1 = uvs[i]
            x2, y2 = uvs[(i + 1) % n]
            area += x1 * y2 - x2 * y1
        area = abs(area) * 0.5
        if area < min_uv_area:
            cap_faces.append(face)

    total_faces = len(bm.faces)
    fraction = (len(cap_faces) / total_faces) if total_faces else 0
    _log(
        f"{piece.name} vs {hull.name}: total_faces={total_faces} "
        f"zero_uv_faces={len(cap_faces)} fraction={fraction:.2f}"
    )

    removed = len(cap_faces)
    if cap_faces and len(cap_faces) < total_faces:
        bmesh.ops.delete(bm, geom=cap_faces, context='FACES')
        bm.to_mesh(piece.data)
    else:
        removed = 0
    bm.free()
    piece.data.update()
    return removed


class OBJECT_OT_split_by_convex_hulls(bpy.types.Operator):
    bl_idname  = "object.split_by_convex_hulls"
    bl_label   = "Split By Convex Hulls"
    bl_options = {'REGISTER', 'UNDO'}

    remove_cap_faces: bpy.props.BoolProperty(
        name="Remove Boolean Cap Faces",
        description=(
            "Delete faces created by the boolean solver exactly on a hull's "
            "cutting boundary (untextured seams/caps), rather than keeping "
            "all resulting geometry"
        ),
        default=True,
    )

    @classmethod
    def poll(cls, context):
        source, hulls = _get_hulls_and_source(context)
        return (context.mode == 'OBJECT'
                and source is not None and source.type == 'MESH'
                and len(hulls) > 0)

    def execute(self, context):
        source, hulls = _get_hulls_and_source(context)
        if source is None or source.type != 'MESH':
            self.report({'ERROR'}, "Active object must be a mesh (the source to cut)")
            return {'CANCELLED'}
        if not hulls:
            self.report({'ERROR'}, "Select one or more convex hull mesh objects along with the source")
            return {'CANCELLED'}

        collection_name = f"{source.name}_split"
        result_col = bpy.data.collections.get(collection_name)
        if result_col is None:
            result_col = bpy.data.collections.new(collection_name)
            context.scene.collection.children.link(result_col)

        created = []
        for hull in hulls:
            piece = source.copy()
            piece.data = source.data.copy()
            piece.name = f"{source.name}_{hull.name}"
            for coll in list(piece.users_collection):
                coll.objects.unlink(piece)
            result_col.objects.link(piece)

            mod = piece.modifiers.new(name="HullIntersect", type='BOOLEAN')
            mod.operation = 'INTERSECT'
            mod.object = hull
            mod.solver = 'EXACT'

            context.view_layer.objects.active = piece
            bpy.ops.object.modifier_apply(modifier=mod.name)

            if self.remove_cap_faces:
                _remove_boolean_cap_faces(piece, hull)

            created.append(piece)

        context.view_layer.objects.active = created[0] if created else source
        bpy.ops.object.select_all(action='DESELECT')
        for piece in created:
            piece.select_set(True)

        self.report({'INFO'}, f"Created {len(created)} piece(s) in collection '{result_col.name}'")
        return {'FINISHED'}


class VIEW3D_PT_split_by_convex_hulls(bpy.types.Panel):
    bl_space_type  = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category    = 'ShipBreaker'
    bl_label       = 'Split By Convex Hulls'
    bl_options     = {'DEFAULT_CLOSED'}

    def draw(self, context):
        layout = self.layout
        source, hulls = _get_hulls_and_source(context)

        col = layout.column(align=True)
        col.label(text=f"Active: {source.name if source else '<none>'}")
        col.label(text=f"Hulls selected: {len(hulls)}")

        layout.prop(context.scene, "sbch_remove_cap_faces")
        op = layout.operator(OBJECT_OT_split_by_convex_hulls.bl_idname,
                              text="Split By Convex Hulls", icon='MOD_BOOLEAN')
        op.remove_cap_faces = context.scene.sbch_remove_cap_faces


def register():
    for cls in (OBJECT_OT_split_by_convex_hulls, VIEW3D_PT_split_by_convex_hulls):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
        bpy.utils.register_class(cls)
    bpy.types.Scene.sbch_remove_cap_faces = bpy.props.BoolProperty(
        name="Remove Boolean Cap Faces",
        description=(
            "Delete faces created by the boolean solver exactly on a hull's "
            "cutting boundary (untextured seams/caps), rather than keeping "
            "all resulting geometry"
        ),
        default=True,
    )


def unregister():
    for cls in (OBJECT_OT_split_by_convex_hulls, VIEW3D_PT_split_by_convex_hulls):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
    del bpy.types.Scene.sbch_remove_cap_faces
