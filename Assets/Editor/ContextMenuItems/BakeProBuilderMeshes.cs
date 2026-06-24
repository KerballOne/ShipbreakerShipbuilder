#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BakeProBuilderMeshes
{
    const string GOMenuPath   = "GameObject/Shipbuilder/Bake PB Meshes";
    const string ShipMenuPath = "Shipbuilder/Bake PB Meshes";

    [MenuItem(GOMenuPath, true)]
    [MenuItem(ShipMenuPath, true, priority = 202)]
    static bool Validate() => Selection.gameObjects.Length > 0;

    [MenuItem(GOMenuPath, false, 49)]
    [MenuItem(ShipMenuPath, false, priority = 202)]
    static void Execute()
    {
        var selected = Selection.gameObjects;
        if (selected.Length == 0) return;

        // Use ProBuilder's own undoable strip action
        EditorApplication.ExecuteMenuItem("Tools/ProBuilder/Actions/Strip ProBuilder Scripts in Selection");

        // Refresh so the unsaved pb_Mesh instances are visible to AssetDatabase
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        int totalBaked = 0;

        foreach (var root in selected)
        {
            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            string outputFolder;
            if (!string.IsNullOrEmpty(prefabPath))
            {
                var dir  = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/') ?? "Assets";
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
                var leaf   = Path.GetFileName(outputFolder);
                AssetDatabase.CreateFolder(parent, leaf);
            }

            int baked = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh))) continue;

                var safeName  = string.Join("_", mf.gameObject.name.Split(Path.GetInvalidFileNameChars()));
                var assetPath = AssetDatabase.GenerateUniqueAssetPath($"{outputFolder}/{safeName}.asset");

                var saved = Object.Instantiate(mesh);
                saved.name = mesh.name;
                AssetDatabase.CreateAsset(saved, assetPath);

                mf.sharedMesh = saved;
                EditorUtility.SetDirty(mf);

                if (mf.TryGetComponent(out MeshCollider mc))
                {
                    mc.sharedMesh = saved;
                    EditorUtility.SetDirty(mc);
                }

                baked++;
            }

            if (baked > 0)
                Debug.Log($"[BakeProBuilderMeshes] Baked {baked} mesh(es) to '{outputFolder}' on '{root.name}'.");

            totalBaked += baked;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[BakeProBuilderMeshes] Baked {totalBaked} mesh(es).");
    }
}
#endif
