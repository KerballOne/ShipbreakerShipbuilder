using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Right-click a leaf GameObject (mesh + collider + StructurePart/EBC all on the same node,
/// no children — e.g. Piping2) → "Recenter Mesh Origin to Geometry (Keep World Position)".
/// Clones the mesh asset, shifts its vertices so the bounding-box center lands at mesh-local
/// origin, reassigns MeshFilter/MeshCollider to the clone, and compensates the GameObject's
/// world position so nothing moves in the editor or in-game.
///
/// Use this when a part's Blender-authored object origin sits far from its own geometry —
/// confirmed root cause of cut/explosion FX spawning away from the visible mesh for parts
/// assigned via ComponentCopyWindow (not baked through ImportGamePartWizard, which recenters
/// automatically via RecenterChildren). Unlike RecenterPivotContextMenu.cs (which recenters a
/// PARENT by shifting its children), this operates on the mesh's own vertex data since leaf
/// parts have no children to shift.
///
/// Not durable against a future reimport of the source FBX — the FBX sub-mesh is left
/// untouched; only a new standalone clone is fixed. Re-run this after any re-export that
/// resets MeshFilter/MeshCollider back to the FBX sub-asset.
/// </summary>
public static class RecenterMeshOriginContextMenu
{
    const string GOMenuPath   = "GameObject/Shipbuilder/Recenter Mesh Origin to Geometry (Keep World Position)";
    const string ShipMenuPath = "Shipbuilder/Recenter Mesh Origin to Geometry (Keep World Position)";

    static bool Validate() =>
        Selection.gameObjects.Length > 0 &&
        Selection.gameObjects.All(go =>
        {
            var mf = go.GetComponent<MeshFilter>();
            return mf != null && mf.sharedMesh != null;
        });

    [MenuItem(GOMenuPath, true)]
    static bool ValidateGO() => Validate();

    [MenuItem(GOMenuPath, false)]
    static void ExecuteGO() => Execute();

    [MenuItem(ShipMenuPath, true, priority = 131)]
    static bool ValidateShip() => Validate();

    [MenuItem(ShipMenuPath, false, priority = 131)]
    static void ExecuteShip() => Execute();

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
        EditorUtility.DisplayDialog("Recenter Mesh Origin", msg, "OK");
    }

    public enum Result { Recentered, NoMesh, AlreadyCentered }

    // Exposed for CustomPartWizard.cs to auto-recenter each mesh it creates. Same behavior as
    // the context menu action — clones the mesh, shifts vertices, compensates the transform.
    public static Result Recenter(GameObject go)
    {
        var mf = go.GetComponent<MeshFilter>();
        var sourceMesh = mf != null ? mf.sharedMesh : null;
        if (sourceMesh == null) return Result.NoMesh;

        Vector3 offset = sourceMesh.bounds.center;
        if (offset.sqrMagnitude < 1e-8f) return Result.AlreadyCentered;

        // FBX-imported meshes are non-readable by default — vertices can't be read/written
        // until Read/Write is enabled on the model importer.
        var meshPath = AssetDatabase.GetAssetPath(sourceMesh);
        if (!string.IsNullOrEmpty(meshPath))
        {
            var importer = AssetImporter.GetAtPath(meshPath) as ModelImporter;
            if (importer != null && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }

        // Never mutate the FBX sub-asset in place — it gets silently regenerated on the next
        // reimport. Clone to a new standalone asset instead (same idiom as Clone_Asset.cs).
        var newMesh = Object.Instantiate(sourceMesh);
        newMesh.name = sourceMesh.name + "_Recentered";
        var verts = newMesh.vertices;
        for (int i = 0; i < verts.Length; i++)
            verts[i] -= offset;
        newMesh.vertices = verts;
        newMesh.RecalculateBounds();

        string newAssetPath = ResolveNewAssetPath(meshPath, newMesh.name);
        AssetDatabase.CreateAsset(newMesh, newAssetPath);
        AssetDatabase.SaveAssets();

        Undo.RecordObject(mf, "Recenter Mesh Origin");
        mf.sharedMesh = newMesh;

        var mc = go.GetComponent<MeshCollider>();
        if (mc != null)
        {
            Undo.RecordObject(mc, "Recenter Mesh Origin");
            mc.sharedMesh = newMesh;
        }

        Undo.RecordObject(go.transform, "Recenter Mesh Origin");
        go.transform.position += go.transform.TransformVector(offset);

        EditorUtility.SetDirty(go);
        Debug.Log($"[RecenterMeshOrigin] '{go.name}': shifted mesh vertices by local offset {offset} " +
            $"(world position preserved), saved clone to '{newAssetPath}'.");
        return Result.Recentered;
    }

    // Places the recentered clone in a sibling "_Recentered_Meshes" folder next to the
    // source mesh's asset (e.g. the FBX), matching ImportGamePartWizard's folder-per-purpose
    // convention. Falls back to Assets/_Recentered_Meshes if the source has no asset path.
    static string ResolveNewAssetPath(string sourceMeshPath, string meshName)
    {
        string folder = !string.IsNullOrEmpty(sourceMeshPath)
            ? $"{Path.GetDirectoryName(sourceMeshPath).Replace('\\', '/')}/_Recentered_Meshes"
            : "Assets/_Recentered_Meshes";

        EnsureFolder(folder);

        string safeName = meshName;
        foreach (char c in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(c, '_');

        string path = $"{folder}/{safeName}.asset";
        return AssetDatabase.GenerateUniqueAssetPath(path);
    }

    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        string leaf = Path.GetFileName(folder);
        if (!AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
