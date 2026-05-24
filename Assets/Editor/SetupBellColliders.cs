using UnityEditor;
using UnityEngine;

public static class SetupBellColliders
{
    const string PREFAB_PATH = "Assets/_CustomShips/Rocinante/Engine Bell.prefab";
    const string COL_MESH_PATH = "Assets/_CustomShips/Rocinante/rocinante_engine_bell_col.fbx";
    const int WEDGE_COUNT = 8;

    [MenuItem("Shipbuilder/Setup Engine Bell Colliders")]
    static void Run()
    {
        var importer = AssetImporter.GetAtPath(COL_MESH_PATH) as ModelImporter;
        if (importer == null)
        {
            Debug.LogError($"Collision mesh not found at {COL_MESH_PATH} — run the Blender script first.");
            return;
        }
        if (!importer.isReadable)
        {
            importer.isReadable = true;
            importer.SaveAndReimport();
        }

        var colMesh = AssetDatabase.LoadAssetAtPath<Mesh>(COL_MESH_PATH);
        if (colMesh == null)
        {
            Debug.LogError($"Could not load mesh from {COL_MESH_PATH}");
            return;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PREFAB_PATH);
        if (prefab == null)
        {
            Debug.LogError($"Prefab not found at {PREFAB_PATH}");
            return;
        }

        using var scope = new PrefabUtility.EditPrefabContentsScope(PREFAB_PATH);
        var root = scope.prefabContentsRoot;

        // Remove any previously generated wedge collider children
        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            var child = root.transform.GetChild(i);
            if (child.name.StartsWith("BellCollider_"))
                Object.DestroyImmediate(child.gameObject);
        }

        // Disable the existing convex MeshCollider on the root — it covers the
        // full bell and blocks entry. The wedge children replace it for physics.
        var rootCol = root.GetComponent<MeshCollider>();
        if (rootCol != null)
            rootCol.enabled = false;

        for (int i = 0; i < WEDGE_COUNT; i++)
        {
            float angleDeg = i * (360f / WEDGE_COUNT);

            var child = new GameObject($"BellCollider_{i:D2}");
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = Vector3.zero;
            child.transform.localScale    = Vector3.one;

            // Bell is exported with Z-up from Blender → FBX importer rotates to Y-up.
            // Wedges need to spin around the bell's axis which is local Y in Unity.
            child.transform.localRotation = Quaternion.Euler(0f, 0f, angleDeg);

            var mc = child.AddComponent<MeshCollider>();
            mc.sharedMesh = colMesh;
            mc.convex     = true;
        }

        Debug.Log($"Engine Bell: added {WEDGE_COUNT} wedge colliders, disabled root MeshCollider.");
    }
}
