#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

public static class BatchRenameObjects
{
    const string GOMenuPath   = "GameObject/Shipbuilder/Batch Rename…";
    const string ShipMenuPath = "Shipbuilder/Batch Rename…";

    [MenuItem(GOMenuPath, true)]
    [MenuItem(ShipMenuPath, true, priority = 145)]
    static bool Validate() => Selection.gameObjects.Length > 0;

    [MenuItem(GOMenuPath, false, 49)]
    [MenuItem(ShipMenuPath, false, priority = 145)]
    static void Execute() => BatchRenameWizard.Open(Selection.gameObjects);
}

public class BatchRenameWizard : ScriptableWizard
{
    public string find    = "";
    public string replace = "";

    [System.NonSerialized] GameObject[] m_Targets;

    // Cached preview so we don't recompute every repaint
    [System.NonSerialized] List<(string oldName, string newName, bool collision)> m_Preview;
    [System.NonSerialized] string m_LastFind;
    [System.NonSerialized] string m_LastReplace;
    [System.NonSerialized] Vector2 m_Scroll;

    public static void Open(GameObject[] targets)
    {
        var wiz = DisplayWizard<BatchRenameWizard>("Batch Rename", "Rename");
        wiz.m_Targets = targets;
    }

    protected override bool DrawWizardGUI()
    {
        EditorGUILayout.HelpBox(
            $"{m_Targets?.Length ?? 0} object(s) selected",
            MessageType.None);

        EditorGUILayout.Space(4);
        find    = EditorGUILayout.TextField("Find (regex)", find);
        replace = EditorGUILayout.TextField("Replace with", replace);

        EditorGUILayout.Space(6);

        // Rebuild preview only when inputs change
        if (m_Preview == null || find != m_LastFind || replace != m_LastReplace)
        {
            BuildPreview();
            m_LastFind    = find;
            m_LastReplace = replace;
        }

        if (m_Preview != null && m_Preview.Count > 0)
        {
            // Column header
            var headerRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float colW = headerRect.width / 2f - 2f;
            var leftHeader  = new Rect(headerRect.x, headerRect.y, colW, headerRect.height);
            var rightHeader = new Rect(headerRect.x + colW + 4f, headerRect.y, colW, headerRect.height);
            EditorGUI.LabelField(leftHeader,  "Before", EditorStyles.boldLabel);
            EditorGUI.LabelField(rightHeader, "After",  EditorStyles.boldLabel);

            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.MaxHeight(240));
            foreach (var (oldName, newName, collision) in m_Preview)
            {
                bool changed = oldName != newName;
                var color    = collision ? Color.yellow : (changed ? GUI.color : new Color(0.6f, 0.6f, 0.6f));

                var rowRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                float cw    = rowRect.width / 2f - 2f;
                var leftRect  = new Rect(rowRect.x, rowRect.y, cw, rowRect.height);
                var rightRect = new Rect(rowRect.x + cw + 4f, rowRect.y, cw, rowRect.height);

                var prev  = GUI.color;
                GUI.color = color;
                EditorGUI.SelectableLabel(leftRect,  oldName, EditorStyles.label);
                EditorGUI.SelectableLabel(rightRect, newName + (collision ? "  ⚠" : ""), EditorStyles.label);
                GUI.color = prev;
            }
            EditorGUILayout.EndScrollView();

            int changed2 = 0;
            foreach (var (o, n, _) in m_Preview) if (o != n) changed2++;
            EditorGUILayout.HelpBox($"{changed2} of {m_Preview.Count} object(s) will be renamed.", MessageType.Info);
        }

        return false;
    }

    void BuildPreview()
    {
        m_Preview = new List<(string, string, bool)>();
        if (m_Targets == null) return;

        Regex rx = TryCompile(find);

        // Track what names each parent has after renames, to detect collisions
        // Key: parent instance ID (or -1 for scene root), Value: set of final names
        var usedNames = new Dictionary<int, HashSet<string>>();

        // Seed with existing siblings that are NOT in the rename set
        var renameSet = new HashSet<int>();
        foreach (var go in m_Targets)
            if (go != null) renameSet.Add(go.GetInstanceID());

        foreach (var go in m_Targets)
        {
            if (go == null) continue;
            int parentKey = go.transform.parent != null ? go.transform.parent.gameObject.GetInstanceID() : -1;
            if (!usedNames.TryGetValue(parentKey, out var siblingNames))
            {
                siblingNames = new HashSet<string>();
                usedNames[parentKey] = siblingNames;
                // Add all existing siblings not in the rename batch
                if (go.transform.parent != null)
                {
                    foreach (Transform sib in go.transform.parent)
                    {
                        if (sib == go.transform) continue;
                        if (!renameSet.Contains(sib.gameObject.GetInstanceID()))
                            siblingNames.Add(sib.name);
                    }
                }
                else
                {
                    // Scene root — iterate root GameObjects in the same scene
                    var scene = go.scene;
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        if (root == go) continue;
                        if (!renameSet.Contains(root.GetInstanceID()))
                            siblingNames.Add(root.name);
                    }
                }
            }
        }

        foreach (var go in m_Targets)
        {
            if (go == null) continue;
            int parentKey  = go.transform.parent != null ? go.transform.parent.gameObject.GetInstanceID() : -1;
            var sibNames   = usedNames[parentKey];

            string proposed = ApplyRename(go.name, rx, replace);
            bool collision  = false;

            if (proposed != go.name && sibNames.Contains(proposed))
            {
                // Auto-suffix until unique, matching Unity's own (N) pattern
                int idx = 1;
                string candidate;
                do { candidate = $"{proposed} ({idx})"; idx++; }
                while (sibNames.Contains(candidate));
                proposed  = candidate;
                collision = true;
            }

            sibNames.Add(proposed);
            m_Preview.Add((go.name, proposed, collision));
        }
    }

    void OnWizardCreate()
    {
        if (m_Targets == null || m_Targets.Length == 0) return;

        BuildPreview();

        Undo.SetCurrentGroupName("Batch Rename");
        int group = Undo.GetCurrentGroup();

        int count = 0;
        for (int i = 0; i < m_Targets.Length; i++)
        {
            var go = m_Targets[i];
            if (go == null) continue;
            var (oldName, newName, _) = m_Preview[i];
            if (oldName == newName) continue;

            Undo.RecordObject(go, "Batch Rename");
            go.name = newName;
            EditorUtility.SetDirty(go);
            count++;
        }

        Undo.CollapseUndoOperations(group);
        Debug.Log($"[BatchRename] Renamed {count} of {m_Targets.Length} object(s).");
    }

    void OnWizardOtherButton() { }

    static string ApplyRename(string name, Regex rx, string replacement)
    {
        if (rx == null) return name;
        try { return rx.Replace(name, replacement ?? ""); }
        catch { return name; }
    }

    static Regex TryCompile(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return null;
        try { return new Regex(pattern); }
        catch { return null; }
    }
}
#endif
