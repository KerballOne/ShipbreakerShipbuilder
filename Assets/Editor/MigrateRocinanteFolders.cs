#if UNITY_EDITOR
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// One-shot migration: moves Javelin parts and shared CurvedMeshes into Rocinante.
// Delete this file after running.
public static class MigrateRocinanteFolders
{
    [MenuItem("Shipbreaker/Dev/Migrate Rocinante Folders (run once then delete)")]
    static void Run()
    {
        var moves = new[]
        {
            // (src, dst) — src contents are merged into dst
            ("Assets/_CustomShips/Prefabs/Javelin",                              "Assets/_CustomShips/Rocinante/Prefabs/Javelin"),
            ("Assets/_CustomShips/Meshes/CurvedMeshes",                          "Assets/_CustomShips/Rocinante/Meshes/CurvedMeshes"),
            // Rename BakedScale → LockedScale to match current convention
            ("Assets/_CustomShips/Rocinante/Prefabs/Javelin/Meshes/BakedScale",  "Assets/_CustomShips/Rocinante/Prefabs/Javelin/Meshes/LockedScale"),
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
                    Debug.LogWarning($"[Migrate] Destination exists, skipping: {target}");
                    continue;
                }
                var err = AssetDatabase.MoveAsset(subGuid, target);
                if (!string.IsNullOrEmpty(err))
                    Debug.LogError($"[Migrate] MoveAsset failed: {subGuid} → {target}: {err}");
            }
        }

        // Delete src if now empty
        if (!AssetDatabase.FindAssets("", new[] { src }).Any())
            AssetDatabase.DeleteAsset(src);
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
