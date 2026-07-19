using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Invoked by CustomMeshPostprocessor.OnPostprocessAllAssets whenever an FBX under /_CustomShips/
// reimports. Finds every GameObject (open scenes + prefab assets) whose MeshFilter references one
// of that FBX's sub-meshes and re-runs RecenterMeshOriginContextMenu.Recenter() on it, so parts
// stay centered without a manual step after each Blender re-export.
public static class AutoRecenterOnReimport
{
    public static void Run(string fbxPath)
    {
        var subMeshes = new HashSet<Mesh>(
            AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Mesh>());
        if (subMeshes.Count == 0)
        {
            Debug.Log($"[AutoRecenterOnReimport] '{fbxPath}': no sub-meshes found, nothing to do.");
            return;
        }

        int scenesChecked = 0, scenesFixed = 0, prefabsChecked = 0, prefabsFixed = 0, recentered = 0;

        // Open scenes
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            scenesChecked++;

            bool sceneChanged = false;
            foreach (var root in scene.GetRootGameObjects())
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || !subMeshes.Contains(mf.sharedMesh))
                    continue;

                var result = RecenterMeshOriginContextMenu.Recenter(mf.gameObject);
                if (result == RecenterMeshOriginContextMenu.Result.Recentered)
                {
                    recentered++;
                    sceneChanged = true;
                    Debug.Log($"[AutoRecenterOnReimport] Recentered '{mf.gameObject.name}' in scene '{scene.name}'.");
                }
            }

            if (sceneChanged)
            {
                scenesFixed++;
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        // Prefab assets — narrow the search to prefabs that actually reference this FBX's GUID,
        // since scanning every prefab under _CustomShips would be needlessly slow.
        var fbxGuid = AssetDatabase.AssetPathToGUID(fbxPath);
        var candidatePrefabPaths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_CustomShips" })
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => AssetDatabase.GetDependencies(p, false).Any(d => AssetDatabase.AssetPathToGUID(d) == fbxGuid)
                        || System.IO.File.ReadAllText(p).Contains(fbxGuid))
            .Distinct();

        foreach (var prefabPath in candidatePrefabPaths)
        {
            prefabsChecked++;
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            bool prefabChanged = false;
            try
            {
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf.sharedMesh == null || !subMeshes.Contains(mf.sharedMesh))
                        continue;

                    var result = RecenterMeshOriginContextMenu.Recenter(mf.gameObject);
                    if (result == RecenterMeshOriginContextMenu.Result.Recentered)
                    {
                        recentered++;
                        prefabChanged = true;
                        Debug.Log($"[AutoRecenterOnReimport] Recentered '{mf.gameObject.name}' in prefab '{prefabPath}'.");
                    }
                }

                if (prefabChanged)
                {
                    prefabsFixed++;
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        AssetDatabase.SaveAssets();

        Debug.Log($"[AutoRecenterOnReimport] '{fbxPath}': checked {scenesChecked} open scene(s) " +
            $"({scenesFixed} changed), {prefabsChecked} candidate prefab(s) ({prefabsFixed} changed), " +
            $"{recentered} GameObject(s) recentered total.");
    }
}
