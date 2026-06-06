using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using BBI.Unity.Game;

/// <summary>
/// Right-click a GameObject in the hierarchy → "Register Components in Parent ACL"
/// Finds the nearest ancestor AddressableComponentLoader, then appends one entry per
/// StructurePart and EntityBlueprintComponent found in the selected GO's descendants,
/// reusing addresses already present in that ACL for the same component types.
/// </summary>
public static class AclRegisterContextMenu
{
    const string MenuPath = "GameObject/Register Components in Parent ACL";

    [MenuItem(MenuPath, true)]
    static bool Validate()
    {
        var go = Selection.activeGameObject;
        if (go == null) return false;
        return FindAncestorAcl(go.transform) != null && CollectTargetComponents(go.transform).Count > 0;
    }

    [MenuItem(MenuPath, false)]
    static void Execute()
    {
        var go = Selection.activeGameObject;
        if (go == null) return;
        var t = go.transform;

        var acl = FindAncestorAcl(t);
        if (acl == null)
        {
            EditorUtility.DisplayDialog("Register in ACL", "No AddressableComponentLoader found in any ancestor.", "OK");
            return;
        }

        var targets = CollectTargetComponents(t);
        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("Register in ACL", "No StructurePart or EntityBlueprintComponent found under the selected object.", "OK");
            return;
        }

        // Build address lookup from existing ACL entries: component type → address
        var addressByType = new Dictionary<System.Type, string>();
        foreach (var cv in acl.componentValues)
        {
            if (cv.component == null) continue;
            var type = cv.component.GetType();
            if (!addressByType.ContainsKey(type) && !string.IsNullOrEmpty(cv.address))
                addressByType[type] = cv.address;
        }

        // Validate we have addresses for all target types
        var missing = new List<string>();
        foreach (var (comp, field) in targets)
        {
            if (!addressByType.ContainsKey(comp.GetType()))
                missing.Add(comp.GetType().Name);
        }
        if (missing.Count > 0)
        {
            EditorUtility.DisplayDialog("Register in ACL",
                $"Cannot infer address for: {string.Join(", ", missing)}\n\nThe parent ACL has no existing entries of that type to copy the address from.",
                "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog("Register in ACL",
                $"Add {targets.Count} ACL entry(s) to '{acl.gameObject.name}' for components under '{t.name}'?",
                "Add", "Cancel"))
            return;

        Undo.RecordObject(acl, "Register Components in Parent ACL");

        foreach (var (comp, field) in targets)
        {
            acl.componentValues.Add(new AddressableComponentValue
            {
                component = comp,
                field     = field,
                address   = addressByType[comp.GetType()],
            });
        }

        EditorUtility.SetDirty(acl);
        Debug.Log($"[AclRegister] Added {targets.Count} entries to ACL on '{acl.gameObject.name}' for '{t.name}'.");
    }

    static AddressableComponentLoader FindAncestorAcl(Transform t)
    {
        var p = t.parent;
        while (p != null)
        {
            var acl = p.GetComponent<AddressableComponentLoader>();
            if (acl != null) return acl;
            p = p.parent;
        }
        return null;
    }

    // Returns (component, fieldName) pairs for StructurePart and EntityBlueprintComponent
    static List<(Component comp, string field)> CollectTargetComponents(Transform t)
    {
        var result = new List<(Component, string)>();
        foreach (var sp in t.GetComponentsInChildren<StructurePart>(true))
            result.Add((sp, "m_StructurePartAsset"));
        foreach (var ebc in t.GetComponentsInChildren<EntityBlueprintComponent>(true))
            result.Add((ebc, "m_BlueprintAsset"));
        return result;
    }
}
