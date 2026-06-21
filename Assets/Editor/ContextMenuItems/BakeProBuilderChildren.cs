#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BakeProBuilderChildren
{
    const string GOMenuPath   = "GameObject/Shipbuilder/Bake ProBuilder Children";
    const string ShipMenuPath = "Shipbuilder/Bake ProBuilder Children";

    [MenuItem(GOMenuPath, true)]
    [MenuItem(ShipMenuPath, true, priority = 142)]
    static bool Validate() => Selection.gameObjects.Length > 0;

    [MenuItem(GOMenuPath, false, 49)]
    [MenuItem(ShipMenuPath, false, priority = 142)]
    static void Execute()
    {
        var selected = Selection.gameObjects;
        if (selected.Length == 0) return;

        Undo.SetCurrentGroupName("Bake ProBuilder Children");
        int group    = Undo.GetCurrentGroup();
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

            if (baked > 0)
                Debug.Log($"[BakeProBuilderChildren] Baked {baked} mesh(es) to '{outputFolder}' on '{root.name}'.");

            totalBaked += baked;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Undo.CollapseUndoOperations(group);

        if (selected.Length > 1)
            Debug.Log($"[BakeProBuilderChildren] Total: {totalBaked} mesh(es) baked across {selected.Length} objects.");
    }
}
#endif
