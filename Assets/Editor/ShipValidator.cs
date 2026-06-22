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
    public enum ErrorKind { NullAclEntry, UnregisteredSP, UnregisteredEBC, NonUnitScale }

    public class ValidationIssue
    {
        public bool IsError;        // false = warning
        public ErrorKind Kind;
        public string PrefabPath;  // asset path
        public GameObject Prefab;  // prefab asset root
        public Component Component; // null for NullAclEntry / NonUnitScale
        public Transform Transform; // the Transform the issue is on
        public string Message;
    }

    public struct ValidationResult
    {
        public List<ValidationIssue> Issues;
        public List<string> Errors   => Issues.FindAll(i => i.IsError).ConvertAll(i => i.Message);
        public List<string> Warnings => Issues.FindAll(i => !i.IsError).ConvertAll(i => i.Message);
        public bool HasErrors => Issues.Exists(i => i.IsError);
    }

    public static ValidationResult Validate()
    {
        var result = new ValidationResult { Issues = new List<ValidationIssue>() };

        var settings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
        if (settings == null)
        {
            Debug.LogError("[ShipValidator] Addressable settings not found.");
            return result;
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

                ValidatePrefab(prefab, path, result);
            }
        }

        if (result.Issues.Count > 0)
        {
            var sb = new StringBuilder("[ShipValidator] Validation results:\n");
            foreach (var issue in result.Issues)
                sb.AppendLine($"  {(issue.IsError ? "ERROR" : "WARNING")}: {issue.Message}");

            if (result.HasErrors) Debug.LogError(sb.ToString());
            else                  Debug.LogWarning(sb.ToString());
        }
        else
        {
            Debug.Log("[ShipValidator] Passed.");
        }

        return result;
    }

    public static bool ValidateAll() => !Validate().HasErrors;

    static void ValidatePrefab(GameObject prefab, string path, ValidationResult result)
    {
        // Build set of all components registered across every ACL in this prefab.
        var registeredComponents = new HashSet<Component>();
        foreach (var acl in prefab.GetComponentsInChildren<AddressableComponentLoader>(true))
            foreach (var cv in acl.componentValues)
                if (cv.component != null) registeredComponents.Add(cv.component);

        // Deduplicate unregistered errors by component instance — one error per component.
        var reportedComponents = new HashSet<Component>();

        foreach (var t in prefab.GetComponentsInChildren<Transform>(true))
        {
            var go = t.gameObject;

            // ── ACL with null/missing component entries ───────────────────
            var acl = go.GetComponent<AddressableComponentLoader>();
            if (acl != null)
            {
                int nullCount = acl.componentValues.FindAll(cv => cv.component == null).Count;
                if (nullCount > 0)
                    result.Issues.Add(new ValidationIssue
                    {
                        IsError = true, Kind = ErrorKind.NullAclEntry,
                        PrefabPath = path, Prefab = prefab, Transform = t,
                        Message = $"{path} → '{GetPath(t)}': ACL has {nullCount} null/missing entry(s) — clean before building."
                    });
            }

            // ── StructurePart / EBC not registered in any ACL ────────────
            // Skip GOs that use AddressableSOLoader — it stores asset refs inline as GUIDs,
            // so those components never need an ACL entry.
            bool hasSoLoader = go.GetComponent<AddressableSOLoader>() != null;
            if (!hasSoLoader)
            {
                var sp = go.GetComponent<StructurePart>();
                if (sp != null && !registeredComponents.Contains(sp) && HasAncestorAcl(t) && reportedComponents.Add(sp))
                    result.Issues.Add(new ValidationIssue
                    {
                        IsError = true, Kind = ErrorKind.UnregisteredSP,
                        PrefabPath = path, Prefab = prefab, Component = sp, Transform = t,
                        Message = $"{path} → '{GetPath(t)}': StructurePart not registered in any ACL."
                    });

                var ebc = go.GetComponent<EntityBlueprintComponent>();
                if (ebc != null && !registeredComponents.Contains(ebc) && HasAncestorAcl(t) && reportedComponents.Add(ebc))
                    result.Issues.Add(new ValidationIssue
                    {
                        IsError = true, Kind = ErrorKind.UnregisteredEBC,
                        PrefabPath = path, Prefab = prefab, Component = ebc, Transform = t,
                        Message = $"{path} → '{GetPath(t)}': EntityBlueprintComponent not registered in any ACL."
                    });
            }

            // ── Non-unit localScale without LockInRescale ────────────────
            var s = t.localScale;
            bool nonUnit = !Mathf.Approximately(s.x, 1f)
                        || !Mathf.Approximately(s.y, 1f)
                        || !Mathf.Approximately(s.z, 1f);
            if (nonUnit)
            {
                bool hasSP = go.GetComponent<StructurePart>() != null
                          || go.GetComponentInChildren<StructurePart>(true) != null;
                if (hasSP)
                    result.Issues.Add(new ValidationIssue
                    {
                        IsError = false, Kind = ErrorKind.NonUnitScale,
                        PrefabPath = path, Prefab = prefab, Transform = t,
                        Message = $"{path} → '{GetPath(t)}': non-unit scale ({s.x:F3},{s.y:F3},{s.z:F3}) — run Lock In Rescale before building."
                    });
            }
        }
    }

    static bool HasAncestorAcl(Transform t)
    {
        var p = t.parent;
        while (p != null)
        {
            if (p.GetComponent<AddressableComponentLoader>() != null) return true;
            p = p.parent;
        }
        return false;
    }

    /// <summary>
    /// Returns all scene Transforms with non-unit scale that have a StructurePart somewhere
    /// in their subtree. Operates on the live scene, not addressable prefab assets.
    /// </summary>
    public static List<Transform> FindScaleViolations()
    {
        var found = new List<Transform>();
        var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                var s = t.localScale;
                bool nonUnit = !Mathf.Approximately(s.x, 1f)
                            || !Mathf.Approximately(s.y, 1f)
                            || !Mathf.Approximately(s.z, 1f);
                if (!nonUnit) continue;
                bool hasSP = t.GetComponent<StructurePart>() != null
                          || t.GetComponentInChildren<StructurePart>(true) != null;
                if (hasSP) found.Add(t);
            }
        }
        return found;
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
