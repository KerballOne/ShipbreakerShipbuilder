#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BakeProBuilderChildren
{
    [MenuItem("GameObject/Shipbreaker/Bake ProBuilder Children", true)]
    static bool Validate() => Selection.activeGameObject != null;

    [MenuItem("GameObject/Shipbreaker/Bake ProBuilder Children", false, 49)]
    static void Execute()
    {
        var root = Selection.activeGameObject;
        var filters = root.GetComponentsInChildren<MeshFilter>(true);

        // Determine output folder next to the prefab asset, or next to the scene if not a prefab
        string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
        string outputFolder;
        if (!string.IsNullOrEmpty(prefabPath))
        {
            var dir = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/') ?? "Assets";
            var name = Path.GetFileNameWithoutExtension(prefabPath);
            outputFolder = $"{dir}/{name}_Meshes";
        }
        else
        {
            outputFolder = "Assets/_CustomShips/Meshes";
        }

        if (!AssetDatabase.IsValidFolder(outputFolder))
        {
            var parent = Path.GetDirectoryName(outputFolder)?.Replace('\\', '/') ?? "Assets";
            var leaf = Path.GetFileName(outputFolder);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        Undo.SetCurrentGroupName("Bake ProBuilder Children");
        int group = Undo.GetCurrentGroup();
        int baked = 0;

        foreach (var mf in filters)
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;

            // Skip meshes already saved to disk
            if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh))) continue;

            var safeName = string.Join("_", mf.gameObject.name.Split(Path.GetInvalidFileNameChars()));
            var assetPath = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{safeName}.asset");

            // Clone so we own the asset (the pb_Mesh is owned by PB)
            var saved = Object.Instantiate(mesh);
            saved.name = mesh.name;
            AssetDatabase.CreateAsset(saved, assetPath);

            Undo.RecordObject(mf, "Bake ProBuilder Children");
            mf.sharedMesh = saved;
            EditorUtility.SetDirty(mf);

            var mc = mf.GetComponent<MeshCollider>();
            if (mc != null)
            {
                Undo.RecordObject(mc, "Bake ProBuilder Children");
                mc.sharedMesh = saved;
                EditorUtility.SetDirty(mc);
            }

            baked++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Undo.CollapseUndoOperations(group);

        Debug.Log($"[BakeProBuilderChildren] Baked {baked} mesh(es) to '{outputFolder}' on '{root.name}'.");
    }
}
#endif
