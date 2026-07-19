using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Shows build validation errors/warnings as a single-column expandable list.
/// Each row shows the object name; clicking it expands to show the issue type and full hierarchy path.
/// "Register All" collects every unregistered component and opens AclRegisterWindow in one pass.
/// </summary>
public class BuildValidationWindow : EditorWindow
{
    List<ShipValidator.ValidationIssue> _issues;
    HashSet<int> _expanded = new HashSet<int>();
    Vector2 _scroll;

    static readonly Color ErrorRowColor   = new Color(1f,  0.45f, 0.35f, 0.3f);
    static readonly Color WarningRowColor = new Color(1f,  0.88f, 0.35f, 0.3f);
    static readonly Color ExpandedBgColor = new Color(0f,  0f,    0f,    0.15f);

    public static void Show(List<ShipValidator.ValidationIssue> issues)
    {
        var win = GetWindow<BuildValidationWindow>(true, "Build Validation Issues", true);
        win._issues = issues;
        win._expanded = new HashSet<int>();
        win.minSize = new Vector2(520, 360);
        win.Show();
    }

    void OnGUI()
    {
        int errorCount   = _issues.Count(i => i.IsError);
        int warningCount = _issues.Count(i => !i.IsError);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField(
            $"Build aborted — {errorCount} error(s), {warningCount} warning(s). Fix errors before building.",
            EditorStyles.boldLabel);
        EditorGUILayout.Space(6);

        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        for (int i = 0; i < _issues.Count; i++)
        {
            var issue = _issues[i];
            bool expanded = _expanded.Contains(i);

            // Header row — clickable
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = issue.IsError ? ErrorRowColor : WarningRowColor;
            EditorGUILayout.BeginHorizontal(GUI.skin.box);
            GUI.backgroundColor = prevBg;

            string arrow = expanded ? "▼" : "▶";
            string objName = issue.Transform != null ? issue.Transform.gameObject.name : "—";
            if (GUILayout.Button($"{arrow}  {objName}", EditorStyles.label, GUILayout.ExpandWidth(true)))
            {
                if (expanded) _expanded.Remove(i);
                else          _expanded.Add(i);
            }

            EditorGUILayout.EndHorizontal();

            // Expanded detail
            if (expanded)
            {
                var prevBg2 = GUI.backgroundColor;
                GUI.backgroundColor = ExpandedBgColor;
                EditorGUILayout.BeginVertical(GUI.skin.box);
                GUI.backgroundColor = prevBg2;

                EditorGUILayout.LabelField(ShortDesc(issue), EditorStyles.wordWrappedLabel);
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Path:", EditorStyles.miniLabel);
                EditorGUILayout.LabelField(GetFullPath(issue.Transform), EditorStyles.wordWrappedMiniLabel);

                EditorGUILayout.EndVertical();
            }
        }

        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(6);

        bool hasRegisterable = _issues.Any(i =>
            i.Kind == ShipValidator.ErrorKind.UnregisteredSP ||
            i.Kind == ShipValidator.ErrorKind.UnregisteredEBC);

        bool hasCleanable = _issues.Any(i => i.Kind == ShipValidator.ErrorKind.NullAclEntry);

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        using (new EditorGUI.DisabledScope(!hasCleanable))
        {
            if (GUILayout.Button("Clean ACLs", GUILayout.Width(100)))
            {
                Close();
                CleanAll(_issues);
            }
        }

        using (new EditorGUI.DisabledScope(!hasRegisterable))
        {
            if (GUILayout.Button("Register All", GUILayout.Width(100)))
            {
                Close();
                RegisterAll(_issues);
            }
        }

        if (GUILayout.Button("Cancel", GUILayout.Width(80)))
            Close();

        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(4);
    }

    static string ShortDesc(ShipValidator.ValidationIssue issue) => issue.Kind switch
    {
        ShipValidator.ErrorKind.NullAclEntry    => "ACL has null/missing entries — use Clean ACL.",
        ShipValidator.ErrorKind.UnregisteredSP  => "StructurePart not registered in any ACL.",
        ShipValidator.ErrorKind.UnregisteredEBC => "EntityBlueprintComponent not registered in any ACL.",
        ShipValidator.ErrorKind.NonUnitScale     => "Non-unit scale — run Lock In Rescale.",
        _                                        => issue.Message,
    };

    static string GetFullPath(Transform t)
    {
        if (t == null) return "—";
        var parts = new List<string>();
        var cur = t;
        while (cur != null)
        {
            parts.Add(cur.gameObject.name);
            cur = cur.parent;
        }
        parts.Reverse();
        return string.Join(" /\n", parts);
    }

    static void RegisterAll(List<ShipValidator.ValidationIssue> issues)
    {
        var byAcl = new Dictionary<AddressableComponentLoader, List<AclRegisterRow>>();
        var alreadySeen = new HashSet<Component>();

        foreach (var issue in issues)
        {
            bool registerable = issue.Kind == ShipValidator.ErrorKind.UnregisteredSP
                             || issue.Kind == ShipValidator.ErrorKind.UnregisteredEBC;
            if (!registerable || issue.Component == null || issue.Transform == null) continue;
            if (!alreadySeen.Add(issue.Component)) continue;

            var acl = FindAncestorAcl(issue.Transform);
            if (acl == null) continue;

            bool alreadyRegistered = false;
            foreach (var cv in acl.componentValues)
                if (cv.component == issue.Component) { alreadyRegistered = true; break; }
            if (alreadyRegistered) continue;

            string field = issue.Kind == ShipValidator.ErrorKind.UnregisteredSP
                ? "m_StructurePartAsset" : "m_BlueprintAsset";

            var candidates = AclRegisterContextMenu.ResolveAddressCandidates(issue.Component, field, acl);
            var row = new AclRegisterRow(issue.Component, field, candidates.Count > 0 ? candidates : null);

            if (!byAcl.TryGetValue(acl, out var rows))
                byAcl[acl] = rows = new List<AclRegisterRow>();
            rows.Add(row);
        }

        if (byAcl.Count == 0) return;

        var aclList = byAcl.ToList();
        OpenNext(aclList, 0);
    }

    static void OpenNext(List<System.Collections.Generic.KeyValuePair<AddressableComponentLoader, List<AclRegisterRow>>> list, int index)
    {
        if (index >= list.Count) return;
        AclRegisterWindow.Show(list[index].Key, list[index].Value, 0);
        if (index + 1 < list.Count)
            EditorApplication.delayCall += () => OpenNext(list, index + 1);
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

    static void CleanAll(List<ShipValidator.ValidationIssue> issues)
    {
        var acls = new HashSet<AddressableComponentLoader>();
        foreach (var issue in issues)
        {
            if (issue.Kind != ShipValidator.ErrorKind.NullAclEntry || issue.Transform == null) continue;
            var acl = issue.Transform.GetComponent<AddressableComponentLoader>();
            if (acl != null) acls.Add(acl);
        }

        foreach (var acl in acls)
            AclCleanContextMenu.Run(acl);
    }
}
