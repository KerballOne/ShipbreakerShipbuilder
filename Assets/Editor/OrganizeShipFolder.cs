#if UNITY_EDITOR

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Moves loose files in a ship folder into type-named subfolders
/// (Materials, Textures, Meshes, Data) without touching existing subfolders.
/// </summary>
public static class OrganizeShipFolder
{
    static readonly Dictionary<string, string> ExtToFolder = new Dictionary<string, string>(
        System.StringComparer.OrdinalIgnoreCase)
    {
        { ".mat",  "Models"    },
        { ".png",  "Textures"  },
        { ".jpg",  "Textures"  },
        { ".jpeg", "Textures"  },
        { ".tga",  "Textures"  },
        { ".tif",  "Textures"  },
        { ".tiff", "Textures"  },
        { ".exr",  "Textures"  },
        { ".hdr",  "Textures"  },
        { ".psd",  "Textures"  },
        { ".fbx",  "Models"    },
        { ".obj",  "Models"    },
        { ".blend","Models"    },
        { ".3ds",  "Models"    },
        { ".dae",  "Models"    },
        { ".asset","Data"      },
    };

    [MenuItem("Shipbreaker/Shipbuilder Tools/Organize Ship Folder...", priority = 10)]
    static void Run()
    {
        // Ask user to pick a folder
        var picked = EditorUtility.OpenFolderPanel(
            "Select ship folder to organize", Application.dataPath, "");

        if (string.IsNullOrEmpty(picked))
            return;

        if (!picked.StartsWith(Application.dataPath))
        {
            EditorUtility.DisplayDialog("Invalid folder",
                "Please pick a folder inside the Assets directory.", "OK");
            return;
        }

        var folderAssetPath = "Assets" + picked.Substring(Application.dataPath.Length).Replace('\\', '/');

        if (!AssetDatabase.IsValidFolder(folderAssetPath))
        {
            EditorUtility.DisplayDialog("Invalid folder",
                $"{folderAssetPath} is not a recognized Unity asset folder.", "OK");
            return;
        }

        // Build move plan: only immediate children files (not recursing into subfolders)
        var plan = BuildPlan(folderAssetPath);

        if (plan.Count == 0)
        {
            EditorUtility.DisplayDialog("Nothing to organize",
                "No loose files matched the known types (Materials, Textures, Meshes, Data).\n\n" +
                "Subfolders and unrecognized file types are left untouched.", "OK");
            return;
        }

        // Show preview dialog
        var preview = BuildPreviewText(plan, folderAssetPath);
        bool confirmed = EditorUtility.DisplayDialog(
            "Organize Ship Folder — Preview",
            preview,
            "Organize", "Cancel");

        if (!confirmed)
            return;

        ApplyPlan(plan, folderAssetPath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("Done",
            $"Moved {plan.Count} file{(plan.Count == 1 ? "" : "s")} into subfolders in\n{folderAssetPath}",
            "OK");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Returns list of (sourceAssetPath, destinationSubfolder) for immediate file children.</summary>
    static List<(string src, string destSubfolder)> BuildPlan(string folderAssetPath)
    {
        var plan = new List<(string, string)>();

        // Find all assets whose path is directly inside this folder (no extra slash = not in a subfolder)
        var guids = AssetDatabase.FindAssets("", new[] { folderAssetPath });

        foreach (var guid in guids)
        {
            var assetPath = AssetDatabase.GUIDToAssetPath(guid);

            // Skip if it's the folder itself or inside a deeper subfolder
            var relative = assetPath.Substring(folderAssetPath.Length).TrimStart('/');
            if (relative.Contains("/"))
                continue; // already in a subfolder

            // Must be a file (folders come back too)
            if (AssetDatabase.IsValidFolder(assetPath))
                continue;

            var ext = Path.GetExtension(assetPath);
            if (!ExtToFolder.TryGetValue(ext, out var destSubfolder))
                continue; // unrecognized type — skip

            plan.Add((assetPath, destSubfolder));
        }

        return plan;
    }

    static string BuildPreviewText(List<(string src, string destSubfolder)> plan, string folderAssetPath)
    {
        // Group by destination subfolder for readability
        var byDest = new SortedDictionary<string, List<string>>();
        foreach (var (src, dest) in plan)
        {
            if (!byDest.ContainsKey(dest))
                byDest[dest] = new List<string>();
            byDest[dest].Add(Path.GetFileName(src));
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Will move {plan.Count} file{(plan.Count == 1 ? "" : "s")} in:\n{folderAssetPath}\n");

        foreach (var kv in byDest)
        {
            sb.AppendLine($"→ {kv.Key}/  ({kv.Value.Count} file{(kv.Value.Count == 1 ? "" : "s")})");
            // Show up to 6 filenames to keep the dialog from overflowing
            int shown = System.Math.Min(kv.Value.Count, 6);
            for (int i = 0; i < shown; i++)
                sb.AppendLine($"    {kv.Value[i]}");
            if (kv.Value.Count > shown)
                sb.AppendLine($"    … and {kv.Value.Count - shown} more");
        }

        sb.AppendLine("\nExisting subfolders and unrecognized types are untouched.");
        return sb.ToString();
    }

    static void ApplyPlan(List<(string src, string destSubfolder)> plan, string folderAssetPath)
    {
        // Ensure all needed subfolders exist first
        var needed = new HashSet<string>();
        foreach (var (_, dest) in plan)
            needed.Add(dest);

        foreach (var sub in needed)
        {
            var subPath = $"{folderAssetPath}/{sub}";
            if (!AssetDatabase.IsValidFolder(subPath))
                AssetDatabase.CreateFolder(folderAssetPath, sub);
        }

        // Move each file — AssetDatabase.MoveAsset handles .meta automatically
        foreach (var (src, destSubfolder) in plan)
        {
            var fileName = Path.GetFileName(src);
            var dest = $"{folderAssetPath}/{destSubfolder}/{fileName}";

            var error = AssetDatabase.MoveAsset(src, dest);
            if (!string.IsNullOrEmpty(error))
                Debug.LogWarning($"[OrganizeShipFolder] Could not move {src}: {error}");
        }
    }
}

#endif
