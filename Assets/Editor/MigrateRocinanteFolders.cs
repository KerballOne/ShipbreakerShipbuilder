#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// One-shot migration: consolidates Javelin/Radial_Chassis_Kit parts and shared meshes into Rocinante.
// Delete this file after running.
public static class MigrateRocinanteFolders
{
    static void Run()
    {
        var moves = new[]
        {
            // (src, dst) — src contents are merged into dst
            ("Assets/_CustomShips/Prefabs/Javelin",                                        "Assets/_CustomShips/Rocinante/Prefabs/Javelin"),
            ("Assets/_CustomShips/Prefabs/Radial_Chassis_Kit",                             "Assets/_CustomShips/Rocinante/Prefabs/Radial_Chassis_Kit"),
            ("Assets/_CustomShips/Meshes/CurvedMeshes",                                    "Assets/_CustomShips/Rocinante/Meshes/CurvedMeshes"),
            ("Assets/_CustomShips/Meshes/BakedScale",                                      "Assets/_CustomShips/Rocinante/Meshes/LockedScale"),
            // Rename BakedScale → LockedScale everywhere under Rocinante
            ("Assets/_CustomShips/Rocinante/Meshes/BakedScale",                            "Assets/_CustomShips/Rocinante/Meshes/LockedScale"),
            ("Assets/_CustomShips/Rocinante/Prefabs/Javelin/Meshes/BakedScale",            "Assets/_CustomShips/Rocinante/Prefabs/Javelin/Meshes/LockedScale"),
            ("Assets/_CustomShips/Rocinante/Prefabs/Radial_Chassis_Kit/Meshes/BakedScale", "Assets/_CustomShips/Rocinante/Prefabs/Radial_Chassis_Kit/Meshes/LockedScale"),
        };

        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var (src, dst) in moves)
                MergeFolder(src, dst);
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        Debug.Log("[MigrateRocinanteFolders] Done. Delete Assets/Editor/MigrateRocinanteFolders.cs.");
    }

    static void MergeFolder(string src, string dst)
    {
        if (!AssetDatabase.IsValidFolder(src))
        {
            Debug.LogWarning($"[Migrate] Source not found, skipping: {src}");
            return;
        }

        EnsureFolder(dst);

        // Move child folders first (recurse), then files
        foreach (var subGuid in AssetDatabase.FindAssets("", new[] { src })
                     .Select(AssetDatabase.GUIDToAssetPath)
                     .Where(p => p.StartsWith(src + "/"))
                     // Only direct children to avoid double-processing nested items
                     .Where(p => !p.Substring(src.Length + 1).Contains("/"))
                     .OrderBy(p => p))
        {
            var name   = Path.GetFileName(subGuid);
            var target = dst + "/" + name;

            if (AssetDatabase.IsValidFolder(subGuid))
            {
                MergeFolder(subGuid, target);
            }
            else
            {
                if (AssetDatabase.LoadAssetAtPath<Object>(target) != null)
                {
                    var srcGuid = AssetDatabase.AssetPathToGUID(subGuid);
                    var dstGuid = AssetDatabase.AssetPathToGUID(target);
                    bool srcReferenced = IsGuidReferencedAnywhere(srcGuid);
                    bool dstReferenced = IsGuidReferencedAnywhere(dstGuid);
                    if (dstReferenced)
                    {
                        // Destination is live — source is the orphan, delete it
                        Debug.Log($"[Migrate] Dst is referenced, deleting orphan src: {subGuid}");
                        AssetDatabase.DeleteAsset(subGuid);
                    }
                    else if (srcReferenced)
                    {
                        // Source is live, destination is orphaned — replace dst with src
                        Debug.Log($"[Migrate] Src is referenced, replacing orphan dst: {target}");
                        AssetDatabase.DeleteAsset(target);
                        var err = AssetDatabase.MoveAsset(subGuid, target);
                        if (!string.IsNullOrEmpty(err))
                            Debug.LogError($"[Migrate] MoveAsset failed: {subGuid} → {target}: {err}");
                    }
                    else
                    {
                        // Neither is referenced — delete source, keep destination
                        Debug.Log($"[Migrate] Both unreferenced, deleting src: {subGuid}");
                        AssetDatabase.DeleteAsset(subGuid);
                    }
                    continue;
                }
                var moveErr = AssetDatabase.MoveAsset(subGuid, target);
                if (!string.IsNullOrEmpty(moveErr))
                    Debug.LogError($"[Migrate] MoveAsset failed: {subGuid} → {target}: {moveErr}");
            }
        }

        // Delete src if now empty
        if (!AssetDatabase.FindAssets("", new[] { src }).Any())
            AssetDatabase.DeleteAsset(src);
    }

    static bool IsGuidReferencedAnywhere(string guid)
    {
        foreach (var assetPath in AssetDatabase.FindAssets("t:Prefab t:SceneAsset", new[] { "Assets" })
                     .Select(AssetDatabase.GUIDToAssetPath))
        {
            var full = Path.GetFullPath(assetPath);
            if (!File.Exists(full)) continue;
            if (File.ReadAllText(full).Contains(guid)) return true;
        }
        return false;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        var parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
#endif
