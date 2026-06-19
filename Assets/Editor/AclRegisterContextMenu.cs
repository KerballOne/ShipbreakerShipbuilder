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
    const string GOMenuPath   = "GameObject/Shipbreaker/Register Components in Parent ACL";
    const string ShipMenuPath = "Shipbuilder/Register Components in Parent ACL";

    static bool ValidateSelection()
    {
        if (Selection.gameObjects.Length == 0) return false;
        AddressableComponentLoader acl = null;
        foreach (var go in Selection.gameObjects)
        {
            var found = FindAncestorAcl(go.transform);
            if (found == null) return false;
            if (acl == null) acl = found;
            else if (acl != found) return false; // different ACLs
        }
        return acl != null;
    }

    [MenuItem(GOMenuPath, true)]
    static bool ValidateGO() => ValidateSelection();

    [MenuItem(GOMenuPath, false)]
    static void ExecuteGO() => Execute();

    [MenuItem(ShipMenuPath, true, priority = 120)]
    static bool ValidateShip() => ValidateSelection();

    [MenuItem(ShipMenuPath, false, priority = 120)]
    static void ExecuteShip() => Execute();

    static void Execute()
    {
        var selected = Selection.gameObjects;
        if (selected.Length == 0) return;

        // Resolve ACL — validate all selections share the same one
        AddressableComponentLoader acl = null;
        foreach (var go in selected)
        {
            var found = FindAncestorAcl(go.transform);
            if (found == null)
            {
                EditorUtility.DisplayDialog("Register in ACL", $"'{go.name}' has no AddressableComponentLoader ancestor.", "OK");
                return;
            }
            if (acl == null) acl = found;
            else if (acl != found)
            {
                EditorUtility.DisplayDialog("Register in ACL", "Selected objects belong to different ACLs. Select objects under the same ACL.", "OK");
                return;
            }
        }

        // Collect targets across all selected objects
        var targets = new List<(Component comp, string field)>();
        foreach (var go in selected)
            targets.AddRange(CollectTargetComponents(go.transform));

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("Register in ACL", "No StructurePart or EntityBlueprintComponent found under the selected objects.", "OK");
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
        var missingTypes = new HashSet<string>();
        foreach (var (comp, _) in targets)
            if (!addressByType.ContainsKey(comp.GetType()))
                missingTypes.Add(comp.GetType().Name);
        if (missingTypes.Count > 0)
        {
            EditorUtility.DisplayDialog("Register in ACL",
                $"Cannot infer address for: {string.Join(", ", missingTypes)}\n\nThe parent ACL has no existing entries of that type to copy the address from.",
                "OK");
            return;
        }

        var existingComponents = new HashSet<Component>();
        foreach (var cv in acl.componentValues)
            if (cv.component != null) existingComponents.Add(cv.component);

        var toAdd = new List<(Component comp, string field)>();
        foreach (var (comp, field) in targets)
            if (!existingComponents.Contains(comp))
                toAdd.Add((comp, field));

        if (toAdd.Count == 0)
        {
            EditorUtility.DisplayDialog("Register in ACL", "All components are already registered in the ACL.", "OK");
            return;
        }

        string selectionLabel = selected.Length == 1 ? $"'{selected[0].name}'" : $"{selected.Length} objects";
        if (!EditorUtility.DisplayDialog("Register in ACL",
                $"Add {toAdd.Count} ACL entry(s) to '{acl.gameObject.name}' for components under {selectionLabel}?" +
                (toAdd.Count < targets.Count ? $"\n({targets.Count - toAdd.Count} already registered, skipped)" : ""),
                "Add", "Cancel"))
            return;

        Undo.RecordObject(acl, "Register Components in Parent ACL");

        foreach (var (comp, field) in toAdd)
        {
            acl.componentValues.Add(new AddressableComponentValue
            {
                component = comp,
                field     = field,
                address   = addressByType[comp.GetType()],
            });
        }

        EditorUtility.SetDirty(acl);
        Debug.Log($"[AclRegister] Added {toAdd.Count} entries to ACL on '{acl.gameObject.name}' for {selectionLabel}.");
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

public static class AclCleanContextMenu
{
    const string GOMenuPath   = "GameObject/Shipbreaker/Clean ACL (Remove Missing + Deduplicate)";
    const string ShipMenuPath = "Shipbuilder/Clean ACL (Remove Missing + Deduplicate)";

    static AddressableComponentLoader SelectedAcl()
    {
        var go = Selection.activeGameObject;
        return go != null ? go.GetComponent<AddressableComponentLoader>() : null;
    }

    [MenuItem(GOMenuPath, true)]
    static bool ValidateGO() => SelectedAcl() != null;

    [MenuItem(GOMenuPath, false)]
    static void ExecuteGO() => Run(SelectedAcl());

    [MenuItem(ShipMenuPath, true, priority = 121)]
    static bool ValidateShip() => SelectedAcl() != null;

    [MenuItem(ShipMenuPath, false, priority = 121)]
    static void ExecuteShip() => Run(SelectedAcl());

    static void Run(AddressableComponentLoader acl)
    {
        if (acl == null) return;

        var original = acl.componentValues;
        int removedMissing = 0;
        int removedDupes   = 0;

        var seen    = new HashSet<Component>();
        var cleaned = new List<AddressableComponentValue>();

        foreach (var cv in original)
        {
            if (cv.component == null) { removedMissing++; continue; }
            if (!seen.Add(cv.component)) { removedDupes++; continue; }
            cleaned.Add(cv);
        }

        if (removedMissing == 0 && removedDupes == 0)
        {
            EditorUtility.DisplayDialog("Clean ACL", "Nothing to clean — no missing or duplicate entries.", "OK");
            return;
        }

        var parts = new List<string>();
        if (removedMissing > 0) parts.Add($"{removedMissing} missing");
        if (removedDupes   > 0) parts.Add($"{removedDupes} duplicate");

        if (!EditorUtility.DisplayDialog("Clean ACL",
                $"Remove {string.Join(" and ", parts)} entr{(removedMissing + removedDupes == 1 ? "y" : "ies")} from '{acl.gameObject.name}'?",
                "Clean", "Cancel"))
            return;

        Undo.RecordObject(acl, "Clean ACL");
        acl.componentValues.Clear();
        acl.componentValues.AddRange(cleaned);
        EditorUtility.SetDirty(acl);

        Debug.Log($"[AclClean] '{acl.gameObject.name}': removed {removedMissing} missing, {removedDupes} duplicate. {cleaned.Count} entries remain.");
    }
}
