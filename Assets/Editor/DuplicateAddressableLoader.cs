using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEditor;

public static class DuplicateAddressableLoader
{
    const string KnownAssetsPath = "known_assets_enriched.json";

    [MenuItem("GameObject/Duplicate AddressableLoader", false, 47)]
    static void Execute(MenuCommand cmd)
    {
        var go = cmd.context as GameObject ?? Selection.activeGameObject;
        if (go == null) return;

        var loader = FindNearestLoader(go);

        // Derive the candidate part name from the selected node's name, stripping Unity's " (N)" suffix.
        string rawName = go.GetComponent<SelectAddressableParent>() != null ? go.name : null;
        string candidateName = rawName != null ? Regex.Replace(rawName, @" \(\d+\)$", "") : null;

        // If the candidate name is a path (contains '/'), take just the filename without extension.
        if (candidateName != null && candidateName.Contains("/"))
            candidateName = Path.GetFileNameWithoutExtension(candidateName);

        Debug.Log($"[DuplicateAddressableLoader] Selected='{go.name}' candidateName='{candidateName}' loader='{(loader != null ? loader.name : "none")}'");

        // Try to resolve GUID by part name first (most specific), fall back to loader's GUID.
        string guid = null;
        string partName = null;

        if (candidateName != null)
        {
            guid = LookupGUIDByPartName(candidateName);
            if (guid != null) partName = candidateName;
        }

        if (guid == null)
        {
            if (loader == null)
            {
                Debug.LogWarning("[DuplicateAddressableLoader] No AddressableLoader found and part name not in known_assets. Walk up to a parent loader and try again.");
                return;
            }
            guid = loader.assetGUID;
            partName = LookupPartName(guid);
            if (partName == null)
            {
                Debug.LogWarning($"[DuplicateAddressableLoader] GUID '{guid}' not found in {KnownAssetsPath}. Walk up to a parent loader and try again.");
                return;
            }
        }

        Debug.Log($"[DuplicateAddressableLoader] Resolved partName='{partName}' guid='{guid}'");

        // Find or create a prefab asset on disk, then instantiate it into the scene.
        string prefabPath = FindExistingPrefab(partName);
        var loaderForCopy = (loader != null && loader.assetGUID == guid) ? loader : null;
        if (prefabPath == null)
            prefabPath = SaveNewPrefab(partName, guid, loaderForCopy);

        if (prefabPath == null)
        {
            Debug.LogWarning($"[DuplicateAddressableLoader] Failed to save prefab for '{partName}'.");
            return;
        }

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null)
        {
            Debug.LogWarning($"[DuplicateAddressableLoader] Could not load prefab at '{prefabPath}'.");
            return;
        }

        Transform placementParent = loader != null ? loader.transform.parent : null;
        Vector3 placementPos = loader != null ? loader.transform.position : go.transform.position;
        Quaternion placementRot = loader != null ? loader.transform.rotation : go.transform.rotation;

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(inst, "Duplicate AddressableLoader");
        inst.transform.SetParent(placementParent, false);
        inst.transform.SetPositionAndRotation(placementPos, placementRot);
        inst.transform.localScale = Vector3.one;

        Selection.activeGameObject = inst;
        Debug.Log($"[DuplicateAddressableLoader] Placed '{partName}' (guid={guid}) under '{(placementParent != null ? placementParent.name : "scene root")}')");
    }

    [MenuItem("GameObject/Duplicate AddressableLoader", true)]
    static bool Validate()
    {
        var go = Selection.activeGameObject;
        if (go == null) return false;
        if (go.GetComponent<SelectAddressableParent>() != null) return true;
        return FindNearestLoader(go) != null;
    }

    static BBI.Unity.Game.AddressableLoader FindNearestLoader(GameObject go)
    {
        for (var t = go.transform; t != null; t = t.parent)
        {
            var l = t.GetComponent<BBI.Unity.Game.AddressableLoader>();
            if (l != null) return l;
        }
        return null;
    }

    static string FindExistingPrefab(string partName)
    {
        var guids = AssetDatabase.FindAssets($"t:Prefab {partName}", new[] { "Assets/_CustomShips" });
        foreach (var g in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(g);
            if (Path.GetFileNameWithoutExtension(path) == partName)
                return path;
        }
        return null;
    }

    static string SaveNewPrefab(string partName, string guid, BBI.Unity.Game.AddressableLoader srcLoader)
    {
        string srcPrefabPath = srcLoader != null
            ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(srcLoader.gameObject)
            : null;
        string folder = !string.IsNullOrEmpty(srcPrefabPath)
            ? Path.GetDirectoryName(srcPrefabPath).Replace('\\', '/')
            : "Assets/_CustomShips/Prefabs";

        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(Path.GetDirectoryName(folder), Path.GetFileName(folder));

        string prefabPath = $"{folder}/{partName}.prefab";

        var tempGo = new GameObject(partName);
        var newLoader = tempGo.AddComponent<BBI.Unity.Game.AddressableLoader>();
        newLoader.assetGUID = guid;
        if (srcLoader != null)
        {
            newLoader.childPath        = srcLoader.childPath;
            newLoader.disabledChildren = srcLoader.disabledChildren != null
                ? new List<string>(srcLoader.disabledChildren)
                : null;
            if (srcLoader.refs != null && srcLoader.refs.Count > 0)
                newLoader.refs = new List<string>(srcLoader.refs);
        }

        PrefabUtility.SaveAsPrefabAsset(tempGo, prefabPath);
        Object.DestroyImmediate(tempGo);
        AssetDatabase.Refresh();

        return prefabPath;
    }

    static string LookupGUIDByPartName(string partName)
    {
        if (!File.Exists(KnownAssetsPath)) return null;
        string json = File.ReadAllText(KnownAssetsPath);

        // JSON structure: "GUID": {"partName":"NAME",...}
        // Search for the partName value and extract the key before it.
        string needle = $"\"partName\":\"{partName}\"";
        int idx = json.IndexOf(needle);
        if (idx < 0) return null;

        // Walk backward to find the opening quote of the GUID key.
        int closeQuote = json.LastIndexOf('"', idx - 3);
        if (closeQuote < 0) return null;
        int openQuote = json.LastIndexOf('"', closeQuote - 1);
        if (openQuote < 0) return null;

        return json.Substring(openQuote + 1, closeQuote - openQuote - 1);
    }

    static string LookupPartName(string guid)
    {
        if (!File.Exists(KnownAssetsPath)) return null;
        string json = File.ReadAllText(KnownAssetsPath);
        int idx = json.IndexOf($"\"{guid}\"");
        if (idx < 0) return null;
        int nameIdx = json.IndexOf("\"partName\":\"", idx);
        if (nameIdx < 0) return null;
        nameIdx += "\"partName\":\"".Length;
        int nameEnd = json.IndexOf('"', nameIdx);
        if (nameEnd < 0) return null;
        return json.Substring(nameIdx, nameEnd - nameIdx);
    }
}
