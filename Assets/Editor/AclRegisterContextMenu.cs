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
/// All confirmations and address selections are shown in a single window.
/// </summary>
public static class AclRegisterContextMenu
{
    const string GOMenuPath   = "GameObject/Shipbuilder/Register Components in Parent ACL";
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

        // Tier 2: name-matched entries from other ACLs in the scene
        var sceneLookup = BuildSceneAclLookup(acl);

        // Tier 3 (lazy): project prefab assets
        Dictionary<(System.Type, string, string), List<(string address, string label)>> prefabLookup = null;

        // Resolve candidates per component
        var candidatesByComp = new Dictionary<Component, List<(string address, string label)>>();
        var unresolved = new List<Component>();

        foreach (var (comp, field) in toAdd)
        {
            string goName = comp.gameObject.name;
            string strippedName = StripCopySuffix(goName);
            var type = comp.GetType();
            var key = (type, field, strippedName);

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
                if (prefabLookup == null)
                    prefabLookup = BuildPrefabAddressLookup(neededKeys);
                prefabLookup.TryGetValue(key, out var tier3);
                if (tier3 != null) AddUnique(matches, tier3);
            }

            if (matches.Count == 0)
                unresolved.Add(comp);
            else
                candidatesByComp[comp] = matches;
        }

        // Build row list: resolved entries get their best-match pre-selected; unresolved flagged
        var rows = new List<AclRegisterRow>();
        foreach (var (comp, field) in toAdd)
        {
            if (candidatesByComp.TryGetValue(comp, out var candidates))
                rows.Add(new AclRegisterRow(comp, field, candidates));
            else
                rows.Add(new AclRegisterRow(comp, field, null)); // unresolved
        }

        int alreadyRegistered = targets.Count - toAdd.Count;
        AclRegisterWindow.Show(acl, rows, alreadyRegistered);
    }

    static void AddUnique(List<(string address, string label)> list, List<(string address, string label)> items)
    {
        foreach (var item in items)
            if (!list.Exists(e => e.address == item.address))
                list.Add(item);
    }

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

    static string StripCopySuffix(string name)
    {
        name = name.Trim();
        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)\s+-\s+\d+$");
        if (m.Success) return m.Groups[1].Value.Trim();
        m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*?)\s+\(\d+\)$");
        if (m.Success) return m.Groups[1].Value.Trim();
        return name;
    }

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

    // Called by BuildValidationWindow to resolve address candidates for a single component.
    public static List<(string address, string label)> ResolveAddressCandidates(
        Component comp, string field, AddressableComponentLoader acl)
    {
        string strippedName = StripCopySuffix(comp.gameObject.name);
        var key = (comp.GetType(), field, strippedName);
        var needed = new HashSet<(System.Type, string, string)> { key };

        var matches = new List<(string address, string label)>();

        var tier1 = BuildAclNameLookup(acl);
        if (tier1.TryGetValue(key, out var t1)) AddUnique(matches, t1);

        if (matches.Count == 0)
        {
            var tier2 = BuildSceneAclLookup(acl);
            if (tier2.TryGetValue(key, out var t2)) AddUnique(matches, t2);
        }

        if (matches.Count == 0)
        {
            var tier3 = BuildPrefabAddressLookup(needed);
            if (tier3.TryGetValue(key, out var t3)) AddUnique(matches, t3);
        }

        return matches;
    }

    // Called by AclRegisterWindow once the user clicks Register
    public static void CommitRows(AddressableComponentLoader acl, List<AclRegisterRow> rows)
    {
        var toCommit = rows.Where(r => r.Include && r.SelectedAddress != null).ToList();
        if (toCommit.Count == 0) return;

        Undo.RecordObject(acl, "Register Components in Parent ACL");
        foreach (var row in toCommit)
        {
            acl.componentValues.Add(new AddressableComponentValue
            {
                component = row.Comp,
                field     = row.Field,
                address   = row.SelectedAddress,
            });
        }
        EditorUtility.SetDirty(acl);
        Debug.Log($"[AclRegister] Added {toCommit.Count} entries to ACL on '{acl.gameObject.name}'.");
    }
}

// Data for a single row in the confirmation window
public class AclRegisterRow
{
    public Component Comp;
    public string Field;
    public List<(string address, string label)> Candidates; // null = unresolved
    public int SelectedIndex;
    public bool Include = true;

    public string SelectedAddress =>
        Candidates != null && SelectedIndex < Candidates.Count ? Candidates[SelectedIndex].address : null;

    public AclRegisterRow(Component comp, string field, List<(string address, string label)> candidates)
    {
        Comp = comp;
        Field = field;
        Candidates = candidates;
        SelectedIndex = 0;
    }
}

public class AclRegisterWindow : EditorWindow
{
    AddressableComponentLoader _acl;
    List<AclRegisterRow> _rows;
    int _alreadyRegistered;
    Vector2 _scroll;

    static readonly Color UnresolvedColor  = new Color(1f, 0.55f, 0.3f);
    static readonly Color MultiMatchColor  = new Color(1f, 0.95f, 0.5f);
    static readonly Color ResolvedColor    = new Color(0.7f, 1f, 0.7f);
    static readonly Color SkippedColor     = new Color(0.55f, 0.55f, 0.55f);

    public static void Show(AddressableComponentLoader acl, List<AclRegisterRow> rows, int alreadyRegistered)
    {
        var win = CreateInstance<AclRegisterWindow>();
        win._acl = acl;
        win._rows = rows;
        win._alreadyRegistered = alreadyRegistered;
        win.titleContent = new GUIContent($"Register in ACL — {acl.gameObject.name}");
        win.minSize = new Vector2(700, 360);
        win.ShowModalUtility();
    }

    void OnGUI()
    {
        DrawHeader();
        DrawLegend();
        EditorGUILayout.Space(4);
        DrawTable();
        EditorGUILayout.Space(6);
        DrawFooter();
    }

    void DrawHeader()
    {
        EditorGUILayout.Space(6);
        string subtitle = $"ACL: {_acl.gameObject.name}  |  {_rows.Count} new component(s)";
        if (_alreadyRegistered > 0)
            subtitle += $"  |  {_alreadyRegistered} already registered (skipped)";
        EditorGUILayout.LabelField(subtitle, EditorStyles.boldLabel);
        EditorGUILayout.Space(2);
    }

    void DrawLegend()
    {
        EditorGUILayout.BeginHorizontal();
        DrawColorSwatch(ResolvedColor);   EditorGUILayout.LabelField("1 match", GUILayout.Width(60));
        DrawColorSwatch(MultiMatchColor); EditorGUILayout.LabelField("multiple matches", GUILayout.Width(120));
        DrawColorSwatch(UnresolvedColor); EditorGUILayout.LabelField("unresolved", GUILayout.Width(80));
        DrawColorSwatch(SkippedColor);    EditorGUILayout.LabelField("skipped", GUILayout.Width(60));
        EditorGUILayout.EndHorizontal();
    }

    static void DrawColorSwatch(Color c)
    {
        var prev = GUI.color;
        GUI.color = c;
        GUILayout.Label(GUIContent.none, GUI.skin.box, GUILayout.Width(14), GUILayout.Height(14));
        GUI.color = prev;
    }

    void DrawTable()
    {
        // Column header
        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(22);                                          // checkbox width
        EditorGUILayout.LabelField("GameObject",  EditorStyles.miniLabel, GUILayout.Width(160));
        EditorGUILayout.LabelField("Type",        EditorStyles.miniLabel, GUILayout.Width(90));
        EditorGUILayout.LabelField("Address",     EditorStyles.miniLabel, GUILayout.MinWidth(200));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(2);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            bool unresolved = row.Candidates == null || row.Candidates.Count == 0;
            bool multi      = !unresolved && row.Candidates.Count > 1;

            Color rowColor = !row.Include ? SkippedColor
                           : unresolved   ? UnresolvedColor
                           : multi        ? MultiMatchColor
                           :                ResolvedColor;

            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = rowColor;
            EditorGUILayout.BeginHorizontal(GUI.skin.box);
            GUI.backgroundColor = prevBg;

            // Include checkbox
            bool newInclude = EditorGUILayout.Toggle(row.Include, GUILayout.Width(18));
            if (newInclude != row.Include) row.Include = newInclude;

            // GameObject name
            EditorGUILayout.LabelField(row.Comp.gameObject.name, GUILayout.Width(160));

            // Component type (abbreviated)
            string typeName = row.Comp.GetType().Name == "EntityBlueprintComponent" ? "EBC" : "SP";
            EditorGUILayout.LabelField(typeName, GUILayout.Width(90));

            // Address picker / status
            if (unresolved)
            {
                EditorGUILayout.LabelField("— no match found —", EditorStyles.miniLabel);
            }
            else if (!row.Include)
            {
                EditorGUILayout.LabelField(row.Candidates[row.SelectedIndex].label, EditorStyles.miniLabel);
            }
            else if (row.Candidates.Count == 1)
            {
                EditorGUILayout.LabelField(row.Candidates[0].label, EditorStyles.miniLabel);
            }
            else
            {
                // Dropdown for multiple candidates
                var labels = row.Candidates.Select(c => c.label).ToArray();
                row.SelectedIndex = EditorGUILayout.Popup(row.SelectedIndex, labels);
            }

            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.EndScrollView();
    }

    void DrawFooter()
    {
        int registerable = _rows.Count(r => r.Include && r.SelectedAddress != null);
        int skipped      = _rows.Count(r => !r.Include);
        int unresolved   = _rows.Count(r => r.Include && r.SelectedAddress == null);

        string summary = $"{registerable} will be registered";
        if (skipped > 0)    summary += $", {skipped} skipped";
        if (unresolved > 0) summary += $", {unresolved} unresolved (will not be added)";

        EditorGUILayout.LabelField(summary, EditorStyles.miniLabel);
        EditorGUILayout.Space(4);

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        using (new EditorGUI.DisabledScope(registerable == 0))
        {
            if (GUILayout.Button($"Register ({registerable})", GUILayout.Width(130)))
            {
                AclRegisterContextMenu.CommitRows(_acl, _rows);
                Close();
            }
        }

        if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            Close();

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(4);
    }
}

// ─────────────────────────────────────────────────────────────────────────────

public static class AclCleanContextMenu
{
    const string GOMenuPath   = "GameObject/Shipbuilder/Clean ACL (Remove Missing + Deduplicate)";
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
