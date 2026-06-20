using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using BBI.Unity.Game;

/// <summary>
/// Right-click a GameObject in the hierarchy → "Register Components in Parent ACL"
/// Finds the nearest ancestor AddressableComponentLoader, then appends one entry per
/// StructurePart and EntityBlueprintComponent found in the selected GO's descendants.
/// Resolves addresses by: (1) name-matching against all prefab ACLs on disc,
/// (2) existing entries in the parent ACL, (3) manual picker as fallback.
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
            else if (acl != found) return false;
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
                EditorUtility.DisplayDialog("Register in ACL", "Selected objects belong to different ACLs.", "OK");
                return;
            }
        }

        var targets = new List<(Component comp, string field)>();
        foreach (var go in selected)
            targets.AddRange(CollectTargetComponents(go.transform));

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("Register in ACL", "No StructurePart or EntityBlueprintComponent found under the selected objects.", "OK");
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

        // Build needed key set for early-exit prefab search
        var neededKeys = new HashSet<(System.Type, string, string)>();
        foreach (var (comp, field) in toAdd)
            neededKeys.Add((comp.GetType(), field, StripCopySuffix(comp.gameObject.name)));

        // Tier 1: name-matched entries in the parent ACL itself
        var aclNameLookup = BuildAclNameLookup(acl);

        // Tier 2: name-matched entries in other ACLs in the scene
        var sceneLookup = BuildSceneAclLookup(acl);

        // Tier 3 (lazy): project prefab assets — only search for keys still unresolved
        Dictionary<(System.Type, string, string), List<(string address, string label)>> prefabLookup = null;

        // Resolve address per component
        var addressByComp = new Dictionary<Component, string>();
        foreach (var (comp, field) in toAdd)
        {
            string goName = comp.gameObject.name;
            string strippedName = StripCopySuffix(goName);
            var type = comp.GetType();
            var key = (type, field, strippedName);

            // Collect matches in tier order, stopping as soon as we have an exact name hit
            var matches = new List<(string address, string label)>();

            aclNameLookup.TryGetValue(key, out var tier1);
            if (tier1 != null) AddUnique(matches, tier1);

            if (matches.Count == 0)
            {
                sceneLookup.TryGetValue(key, out var tier2);
                if (tier2 != null) AddUnique(matches, tier2);
            }

            if (matches.Count == 0)
            {
                // Lazy-load project prefab search for remaining keys
                if (prefabLookup == null)
                    prefabLookup = BuildPrefabAddressLookup(neededKeys);
                prefabLookup.TryGetValue(key, out var tier3);
                if (tier3 != null) AddUnique(matches, tier3);
            }

            if (matches.Count == 0)
            {
                EditorUtility.DisplayDialog("Register in ACL",
                    $"No address found for {type.Name} on '{goName}'.\n\nNo name match in ACL, scene, or project prefabs.",
                    "OK");
                return;
            }

            string picked;
            if (matches.Count == 1)
            {
                int choice = EditorUtility.DisplayDialogComplex("Register in ACL",
                    $"Use this address for {type.Name} on '{goName}'?\n\n{matches[0].label}",
                    "Use", "Cancel", "Show All");
                if (choice == 1) return;
                picked = choice == 0 ? matches[0].address : AddressPickerWindow.Show($"{type.Name} on '{goName}'", matches);
                if (picked == null) return;
            }
            else
            {
                picked = AddressPickerWindow.Show($"{type.Name} on '{goName}'", matches);
                if (picked == null) return;
            }

            addressByComp[comp] = picked;
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
                address   = addressByComp[comp],
            });
        }

        EditorUtility.SetDirty(acl);
        Debug.Log($"[AclRegister] Added {toAdd.Count} entries to ACL on '{acl.gameObject.name}' for {selectionLabel}.");
    }

    static void AddUnique(List<(string address, string label)> list, List<(string address, string label)> items)
    {
        foreach (var item in items)
            if (!list.Exists(e => e.address == item.address))
                list.Add(item);
    }

    // Tier 1: name-matched entries within the same ACL
    static Dictionary<(System.Type, string, string), List<(string address, string label)>> BuildAclNameLookup(
        AddressableComponentLoader acl)
    {
        var result = new Dictionary<(System.Type, string, string), List<(string address, string label)>>();
        foreach (var cv in acl.componentValues)
        {
            if (cv.component == null || string.IsNullOrEmpty(cv.address)) continue;
            AddToLookup(result, cv, "parent ACL");
        }
        return result;
    }

    // Tier 2: name-matched entries from all other ACLs in the scene
    static Dictionary<(System.Type, string, string), List<(string address, string label)>> BuildSceneAclLookup(
        AddressableComponentLoader exclude)
    {
        var result = new Dictionary<(System.Type, string, string), List<(string address, string label)>>();
        foreach (var loader in Object.FindObjectsOfType<AddressableComponentLoader>())
        {
            if (loader == exclude) continue;
            string sourceName = loader.gameObject.name;
            foreach (var cv in loader.componentValues)
            {
                if (cv.component == null || string.IsNullOrEmpty(cv.address)) continue;
                AddToLookup(result, cv, $"scene: {sourceName}");
            }
        }
        return result;
    }

    static void AddToLookup(
        Dictionary<(System.Type, string, string), List<(string address, string label)>> lookup,
        AddressableComponentValue cv, string source)
    {
        var type = cv.component.GetType();
        string goName = StripCopySuffix(cv.component.gameObject.name);
        var key = (type, cv.field, goName);
        string addrLabel = cv.address.Contains("/")
            ? System.IO.Path.GetFileNameWithoutExtension(cv.address)
            : cv.component.gameObject.name;
        string label = $"{addrLabel}  [{cv.component.gameObject.name} — {source}]";
        if (!lookup.ContainsKey(key))
            lookup[key] = new List<(string, string)>();
        if (!lookup[key].Exists(e => e.address == cv.address))
            lookup[key].Add((cv.address, label));
    }

    // Strips Unity copy suffixes: " - 1", " (1)", " 1" at end of name
    static string StripCopySuffix(string name)
    {
        name = name.Trim();
        // " - N"
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)\s+-\s+\d+$");
        if (m.Success) return m.Groups[1].Value.Trim();
        // " (N)"
        m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)\s+\(\d+\)$");
        if (m.Success) return m.Groups[1].Value.Trim();
        return name;
    }

    // Searches prefab assets for ACL entries matching the needed keys, stopping early once all are found.
    static Dictionary<(System.Type, string, string), List<(string address, string label)>> BuildPrefabAddressLookup(
        HashSet<(System.Type type, string field, string strippedName)> needed)
    {
        var result = new Dictionary<(System.Type, string, string), List<(string address, string label)>>();
        var remaining = new HashSet<(System.Type, string, string)>(needed);

        var guids = AssetDatabase.FindAssets("t:Prefab");
        foreach (var guid in guids)
        {
            if (remaining.Count == 0) break;

            var path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            foreach (var loader in prefab.GetComponentsInChildren<AddressableComponentLoader>(true))
            {
                foreach (var cv in loader.componentValues)
                {
                    if (cv.component == null || string.IsNullOrEmpty(cv.address)) continue;
                    var type = cv.component.GetType();
                    string goName = StripCopySuffix(cv.component.gameObject.name);
                    var key = (type, cv.field, goName);
                    if (!remaining.Contains(key)) continue;

                    string prefabName = System.IO.Path.GetFileNameWithoutExtension(path);
                    string addrLabel = cv.address.Contains("/")
                        ? System.IO.Path.GetFileNameWithoutExtension(cv.address)
                        : cv.component.gameObject.name;
                    string label = $"{addrLabel}  [{cv.component.gameObject.name} in {prefabName}]";

                    if (!result.ContainsKey(key))
                        result[key] = new List<(string, string)>();
                    if (!result[key].Exists(e => e.address == cv.address))
                        result[key].Add((cv.address, label));

                    remaining.Remove(key);
                }
            }
        }

        return result;
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

        int removedMissing = 0;
        int removedDupes   = 0;
        var seen    = new HashSet<Component>();
        var cleaned = new List<AddressableComponentValue>();

        foreach (var cv in acl.componentValues)
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

public class AddressPickerWindow : EditorWindow
{
    string _typeName;
    List<(string address, string label)> _options;
    string _picked;
    bool _done;
    int _selectedIndex;

    public static string Show(string typeName, List<(string address, string label)> options)
    {
        var win = CreateInstance<AddressPickerWindow>();
        win._typeName = typeName;
        win._options = options;
        win._selectedIndex = 0;
        win.titleContent = new GUIContent($"Pick address for {typeName}");
        win.minSize = new Vector2(620, 160 + options.Count * 22);
        win.maxSize = new Vector2(900, 160 + options.Count * 22);
        win.ShowModalUtility();
        return win._done ? win._picked : null;
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField($"Select address to use for {_typeName}:", EditorStyles.wordWrappedLabel);
        EditorGUILayout.Space(6);

        for (int i = 0; i < _options.Count; i++)
        {
            bool newSelected = EditorGUILayout.ToggleLeft(_options[i].label, _selectedIndex == i);
            if (newSelected) _selectedIndex = i;
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Use", GUILayout.Width(80)))
        {
            _picked = _options[_selectedIndex].address;
            _done = true;
            Close();
        }
        if (GUILayout.Button("Cancel", GUILayout.Width(80)))
        {
            _done = false;
            Close();
        }
        EditorGUILayout.EndHorizontal();
    }
}
