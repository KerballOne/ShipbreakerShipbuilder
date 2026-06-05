using System.Collections.Generic;
using System.Text;
using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Pre-build validation: run from BuildContent.RunBuild() before the addressables build.
/// Reports errors that will cause runtime crashes or silent breakage.
/// </summary>
public static class ShipValidator
{
    public struct ValidationResult
    {
        public List<string> Errors;
        public List<string> Warnings;
        public bool HasErrors => Errors.Count > 0;
    }

    /// <summary>
    /// Validates all prefabs in the project that are marked addressable.
    /// Returns false and logs errors if any blocking issues are found.
    /// </summary>
    public static bool ValidateAll()
    {
        var result = new ValidationResult
        {
            Errors   = new List<string>(),
            Warnings = new List<string>(),
        };

        var settings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[ShipValidator] Addressable settings not found.");
            return false;
        }

        foreach (var group in settings.groups)
        {
            if (group == null) continue;
            foreach (var entry in group.entries)
            {
                var path = entry.AssetPath;
                if (!path.EndsWith(".prefab")) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                ValidatePrefab(prefab, path, ref result);
            }
        }

        if (result.HasErrors || result.Warnings.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ShipValidator] Validation results:");
            foreach (var e in result.Errors)   sb.AppendLine($"  ERROR:   {e}");
            foreach (var w in result.Warnings) sb.AppendLine($"  WARNING: {w}");

            if (result.HasErrors)
                Debug.LogError(sb.ToString());
            else
                Debug.LogWarning(sb.ToString());
        }

        if (!result.HasErrors)
            Debug.Log($"[ShipValidator] Passed ({result.Warnings.Count} warning(s)).");

        return !result.HasErrors;
    }

    static void ValidatePrefab(GameObject prefab, string path, ref ValidationResult result)
    {
        foreach (var go in prefab.GetComponentsInChildren<Transform>(true))
        {
            var gameObject = go.gameObject;

            // ── ACL with null/missing component entries ───────────────────
            var acl = gameObject.GetComponent<AddressableComponentLoader>();
            if (acl != null)
            {
                int nullCount = 0;
                foreach (var cv in acl.componentValues)
                {
                    if (cv.component == null)
                        nullCount++;
                }
                if (nullCount > 0)
                    result.Errors.Add(
                        $"{path} → '{GetPath(go)}': AddressableComponentLoader has {nullCount} null/missing component entry(s). " +
                        $"Remove the dead entries before building.");
            }

            // ── Non-unit localScale without LockInRescale ────────────────
            var scale = go.transform.localScale;
            bool nonUnit = !Mathf.Approximately(scale.x, 1f)
                        || !Mathf.Approximately(scale.y, 1f)
                        || !Mathf.Approximately(scale.z, 1f);
            if (nonUnit)
            {
                var hasSP = gameObject.GetComponent<StructurePart>() != null
                         || gameObject.GetComponentInChildren<StructurePart>(true) != null;
                if (hasSP)
                    result.Warnings.Add(
                        $"{path} → '{GetPath(go)}': non-unit scale ({scale.x:F3},{scale.y:F3},{scale.z:F3}) " +
                        $"with StructurePart — joints and mass will use unscaled geometry. " +
                        $"Run Lock In Rescale (LIR) before building.");
            }
        }
    }

    static string GetPath(Transform t)
    {
        var sb = new StringBuilder(t.name);
        var p = t.parent;
        int depth = 0;
        while (p != null && depth++ < 6)
        {
            sb.Insert(0, p.name + "/");
            p = p.parent;
        }
        return sb.ToString();
    }
}
