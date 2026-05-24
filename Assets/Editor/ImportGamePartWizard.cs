#if UNITY_EDITOR

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using BBI.Unity.Game;

public class ImportGamePartWizard : EditorWindow
{
    const string PrefOutputFolder = "ImportGamePartWizard.OutputFolder";
    const int MaxResults = 100;

    string m_Search = "";
    string m_LastSearch = null;
    bool m_PrefabsOnly = true;
    Vector2 m_Scroll;
    string m_OutputFolder = "Assets/_CustomShips/";

    // Multi-select: guid → (path, displayName)
    readonly Dictionary<string, (string path, string displayName)> m_Selection =
        new Dictionary<string, (string, string)>();

    readonly List<(string guid, string path, string displayName)> m_Results =
        new List<(string, string, string)>();

    static readonly GUIStyle s_PathStyle = new GUIStyle(EditorStyles.miniLabel)
    {
        wordWrap = true,
        richText = false,
    };

    [MenuItem("Shipbuilder/Import Game Part Wizard")]
    static void Open()
    {
        var w = GetWindow<ImportGamePartWizard>("Import Game Part");
        w.m_OutputFolder = EditorPrefs.GetString(PrefOutputFolder, "Assets/_CustomShips/");
    }

    void OnGUI()
    {
        if (LoadGameAssets.knownAssetMap == null || LoadGameAssets.knownAssetMap.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "No game assets loaded. Run  Shipbreaker → Reload Assets  first.",
                MessageType.Warning);
            return;
        }

        // ── Search ───────────────────────────────────────────────────────────
        GUILayout.Label("Search Game Library", EditorStyles.boldLabel);

        EditorGUILayout.BeginHorizontal();
        var newSearch = EditorGUILayout.TextField("Name", m_Search);
        var newPrefabsOnly = EditorGUILayout.ToggleLeft("Prefabs only", m_PrefabsOnly, GUILayout.Width(100));
        EditorGUILayout.EndHorizontal();

        if (newSearch != m_Search || newPrefabsOnly != m_PrefabsOnly || m_LastSearch == null)
        {
            m_Search = newSearch;
            m_PrefabsOnly = newPrefabsOnly;
            m_LastSearch = m_Search;
            RebuildResults();
        }

        EditorGUILayout.LabelField(
            $"{m_Results.Count} result{(m_Results.Count == 1 ? "" : "s")}" +
            (m_Results.Count >= MaxResults ? $" (capped at {MaxResults})" : "") +
            (m_Selection.Count > 0 ? $"  —  {m_Selection.Count} selected" : ""),
            EditorStyles.miniLabel);

        // ── Results list ─────────────────────────────────────────────────────
        m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.Height(260));
        foreach (var (guid, path, displayName) in m_Results)
        {
            bool selected = m_Selection.ContainsKey(guid);

            EditorGUILayout.BeginVertical(selected ? GUI.skin.box : GUIStyle.none);

            if (GUILayout.Button(selected ? $"✓  {displayName}" : displayName,
                    selected ? EditorStyles.boldLabel : EditorStyles.label))
            {
                if (selected)
                    m_Selection.Remove(guid);
                else
                    m_Selection[guid] = (path, displayName);
            }

            // Indented, small path
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(20f);
            GUILayout.Label(path, s_PathStyle);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space();

        // ── Selection summary ─────────────────────────────────────────────────
        GUILayout.Label("Selected", EditorStyles.boldLabel);
        if (m_Selection.Count == 0)
        {
            EditorGUILayout.HelpBox("Click results above to select (click again to deselect).", MessageType.None);
        }
        else
        {
            foreach (var kv in m_Selection)
                EditorGUILayout.LabelField(kv.Value.displayName, EditorStyles.miniLabel);
        }

        EditorGUILayout.Space();

        // ── Output folder ─────────────────────────────────────────────────────
        GUILayout.Label("Output", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Each part is imported into its own named subfolder:  Output/PartName/PartName.prefab",
            MessageType.None);

        EditorGUILayout.BeginHorizontal();
        m_OutputFolder = EditorGUILayout.TextField("Folder", m_OutputFolder);
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            var picked = EditorUtility.OpenFolderPanel("Select Output Folder", Application.dataPath, "");
            if (!string.IsNullOrEmpty(picked) && picked.StartsWith(Application.dataPath))
                m_OutputFolder = "Assets" + picked.Substring(Application.dataPath.Length).Replace('\\', '/');
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();

        // ── Import button ─────────────────────────────────────────────────────
        string error = Validate();
        GUI.enabled = error == null;
        var label = m_Selection.Count > 1
            ? $"Import {m_Selection.Count} Parts"
            : "Import Selected";
        if (GUILayout.Button(label, GUILayout.Height(32)))
            DoImport();
        GUI.enabled = true;

        if (error != null)
            EditorGUILayout.HelpBox(error, MessageType.Error);
    }

    void RebuildResults()
    {
        m_Results.Clear();
        var term = m_Search.Trim();

        foreach (var kv in LoadGameAssets.knownAssetMap)
        {
            var path = kv.Value;

            if (m_PrefabsOnly && !path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(term) &&
                path.IndexOf(term, System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var displayName = Path.GetFileNameWithoutExtension(path);
            m_Results.Add((kv.Key, path, displayName));

            if (m_Results.Count >= MaxResults)
                break;
        }

        m_Results.Sort((a, b) =>
            string.Compare(a.displayName, b.displayName, System.StringComparison.OrdinalIgnoreCase));
    }

    string Validate()
    {
        if (m_Selection.Count == 0)
            return "Select at least one game asset from the results list.";
        if (string.IsNullOrWhiteSpace(m_OutputFolder))
            return "Output folder is required.";
        if (!AssetDatabase.IsValidFolder(m_OutputFolder.TrimEnd('/')))
            return $"Output folder does not exist: {m_OutputFolder}";
        return null;
    }

    void DoImport()
    {
        EditorPrefs.SetString(PrefOutputFolder, m_OutputFolder);

        var outRoot = m_OutputFolder.TrimEnd('/');

        // Collect any name conflicts upfront
        var conflicts = new List<string>();
        foreach (var kv in m_Selection)
        {
            var name = kv.Value.displayName;
            var prefabPath = $"{outRoot}/{name}/{name}.prefab";
            if (File.Exists(Path.GetFullPath(prefabPath)))
                conflicts.Add(name);
        }

        if (conflicts.Count > 0)
        {
            var msg = conflicts.Count == 1
                ? $"{conflicts[0]} already exists. Overwrite?"
                : $"{conflicts.Count} parts already exist:\n{string.Join(", ", conflicts)}\n\nOverwrite all?";
            if (!EditorUtility.DisplayDialog("Overwrite?", msg, "Yes", "Cancel"))
                return;
        }

        var created = new List<GameObject>();

        foreach (var kv in m_Selection)
        {
            var guid = kv.Key;
            var (_, displayName) = kv.Value;

            var partFolder = $"{outRoot}/{displayName}";
            var prefabPath = $"{partFolder}/{displayName}.prefab";

            // Create subfolder
            if (!AssetDatabase.IsValidFolder(partFolder))
                AssetDatabase.CreateFolder(outRoot, displayName);

            if (File.Exists(Path.GetFullPath(prefabPath)))
                AssetDatabase.DeleteAsset(prefabPath);

            var go = new GameObject(displayName);
            var loader = go.AddComponent<AddressableLoader>();
            loader.assetGUID = guid;

            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            DestroyImmediate(go);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab != null)
                created.Add(prefab);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (created.Count > 0)
        {
            Selection.objects = created.ToArray();
            EditorGUIUtility.PingObject(created[created.Count - 1]);
        }

        var count = created.Count;
        EditorUtility.DisplayDialog("Imported",
            $"Created {count} prefab{(count == 1 ? "" : "s")} in  {outRoot}/\n\n" +
            "Next steps:\n" +
            "1. Drag a prefab into your ship hierarchy in the scene\n" +
            "2. Run  Shipbreaker → Force View Refresh  to see it render\n" +
            "3. Position and build as normal",
            "OK");

        m_Selection.Clear();
    }
}

#endif
