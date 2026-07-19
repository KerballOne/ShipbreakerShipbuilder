using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Right-click a leaf GameObject (mesh + collider + StructurePart/EBC all on the same node,
/// no children — e.g. Piping2) → "Recenter Mesh to Origin".
/// Shifts the mesh asset's own vertices in place so the bounding-box center lands at mesh-local
/// origin — i.e. moves the MESH to the origin. Does NOT compensate the GameObject's
/// transform.position — see below. (Opposite direction from RecenterPivotContextMenu.cs, which
/// moves a parent's ORIGIN to the mesh by shifting the parent's children.)
///
/// Use this when a part's Blender-authored object origin sits far from its own geometry —
/// confirmed root cause of cut/explosion FX spawning away from the visible mesh for parts
/// assigned via ComponentCopyWindow (not baked through ImportGamePartWizard, which recenters
/// automatically via RecenterChildren). Unlike RecenterPivotContextMenu.cs (which recenters a
/// PARENT by shifting its children), this operates on the mesh's own vertex data since leaf
/// parts have no children to shift.
///
/// Mutates the FBX sub-mesh asset directly (no clone) — a reimport of the source FBX will wipe
/// this out, since Unity regenerates FBX sub-meshes from scratch on every reimport. That's
/// expected: re-run this tool (or CustomPartWizard's auto-recenter pass) after every Blender
/// re-export instead of trying to make the fix durable across reimport.
///
/// By default does NOT compensate go.transform.position for the vertex shift — used by the manual
/// menu command (re-recentering after a Blender re-export on GameObjects that are already
/// correctly placed in the scene: only the mesh needs to move back to the origin, not the
/// GameObject). CustomPartWizard's first-time bake passes compensatePosition: true instead, since
/// each freshly-baked mesh is unshared and needs its GameObject positioned to match the original
/// Blender placement. Do not flip the default — with multiple GameObjects sharing one mesh asset
/// (e.g. repeated tower/leg instances on Rocinante), only the first Recenter() call on a shared
/// mesh actually shifts vertices; later calls on sibling GOs see it already centered and skip,
/// leaving their position compensation stale/mismatched, which visibly drifted objects apart.
/// </summary>
public static class RecenterMeshOriginContextMenu
{
    const string GOMenuPath   = "GameObject/Shipbuilder/Recenter Mesh to Origin";
    const string ShipMenuPath = "Shipbuilder/Recenter Mesh to Origin";

    static bool Validate() =>
        Selection.gameObjects.Length > 0 &&
        Selection.gameObjects.All(go =>
        {
            var mf = go.GetComponent<MeshFilter>();
            return mf != null && mf.sharedMesh != null;
        });

    [MenuItem(GOMenuPath, true)]
    static bool ValidateGO() => Validate();

    // Unity invokes a GameObject/ menu item once PER SELECTED OBJECT, not once for the whole
    // selection — calling Execute() directly here would pop one dialog per object. Defer to a
    // single delayed call instead, so N invocations this frame collapse into one batch + one dialog.
    [MenuItem(GOMenuPath, false)]
    static void ExecuteGO() => ScheduleExecute();

    [MenuItem(ShipMenuPath, true, priority = 131)]
    static bool ValidateShip() => Validate();

    [MenuItem(ShipMenuPath, false, priority = 131)]
    static void ExecuteShip() => Execute();

    static bool s_ExecuteScheduled;

    static void ScheduleExecute()
    {
        if (s_ExecuteScheduled) return;
        s_ExecuteScheduled = true;
        EditorApplication.delayCall += () =>
        {
            s_ExecuteScheduled = false;
            Execute();
        };
    }

    static void Execute()
    {
        int recentered = 0, skippedNoMesh = 0, skippedAlreadyCentered = 0;

        foreach (var go in Selection.gameObjects)
        {
            var result = Recenter(go);
            switch (result)
            {
                case Result.Recentered:      recentered++; break;
                case Result.NoMesh:           skippedNoMesh++; break;
                case Result.AlreadyCentered:  skippedAlreadyCentered++; break;
            }
        }

        string msg = $"Recentered {recentered} object(s).";
        if (skippedAlreadyCentered > 0) msg += $" {skippedAlreadyCentered} already centered.";
        if (skippedNoMesh > 0)          msg += $" {skippedNoMesh} had no mesh.";
        Debug.Log($"[RecenterMeshOrigin] {msg}");
        EditorUtility.DisplayDialog("Recenter Mesh to Origin", msg, "OK");
    }

    public enum Result { Recentered, NoMesh, AlreadyCentered }

    // compensatePosition: true is for CustomPartWizard's first-time bake (unshared mesh, needs
    // the GameObject positioned to preserve the original Blender placement). false (the default,
    // used by the manual menu command) leaves transform.position untouched — see class doc comment.
    public static Result Recenter(GameObject go, bool compensatePosition = false)
    {
        var mf = go.GetComponent<MeshFilter>();
        var mesh = mf != null ? mf.sharedMesh : null;
        if (mesh == null) return Result.NoMesh;

        Vector3 offset = mesh.bounds.center;
        if (offset.sqrMagnitude < 1e-8f) return Result.AlreadyCentered;

        // FBX-imported meshes are non-readable by default — vertices can't be read/written
        // until Read/Write is enabled on the model importer.
        var meshPath = AssetDatabase.GetAssetPath(mesh);
        if (!string.IsNullOrEmpty(meshPath))
        {
            var importer = AssetImporter.GetAtPath(meshPath) as ModelImporter;
            if (importer != null && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }

        // Mutate the mesh asset's own vertices in place. This gets wiped by Unity on the next
        // FBX reimport (sub-meshes are regenerated from scratch) — that's expected; re-run this
        // tool (or CustomPartWizard's auto-recenter) after every Blender re-export.
        var verts = mesh.vertices;
        for (int i = 0; i < verts.Length; i++)
            verts[i] -= offset;
        mesh.vertices = verts;
        mesh.RecalculateBounds();
        EditorUtility.SetDirty(mesh);

        if (compensatePosition)
        {
            Undo.RecordObject(go.transform, "Recenter Mesh Origin");
            go.transform.position += go.transform.TransformVector(offset);
        }

        EditorUtility.SetDirty(go);
        Debug.Log($"[RecenterMeshOrigin] '{go.name}': shifted mesh '{mesh.name}' vertices by local " +
            $"offset {offset}" + (compensatePosition ? " (world position preserved)." : " (position not compensated)."));
        return Result.Recentered;
    }
}
