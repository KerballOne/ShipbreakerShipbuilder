#if UNITY_EDITOR

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using BBI.Unity.Game;

public class ImportGamePartWizard : EditorWindow
{
    const string PrefOutputFolder  = "ImportGamePartWizard.OutputFolder";
    const string PrefImportedOnce  = "ImportGamePartWizard.ImportedOnce";
    const int    MaxResults       = 100;

    enum SearchMode { DisplayName, PartName, Path }
    enum SortColumn { DisplayName, PartName, DimX, DimY, DimZ, Volume, Mass }

    // Fixed column widths; Display Name and Part Name share remaining flexible space equally
    const float W_EXP        = 28f;   // header column width
    const float W_SEL        = 26f;   // header column width
    const float W_EXP_BTN    = 24f;   // content expand button (smaller; pad to W_EXP)
    const float W_SEL_BTN    = 22f;   // content select button (smaller; pad to W_SEL)
    const float W_CHILD_INDENT = 12f;
    const float W_DIM  = 54f;
    const float W_VOL  = 60f;
    const float W_MASS = 60f;

    string     m_Search      = "";
    string     m_LastSearch  = null;
    bool       m_PrefabsOnly = true;
    SearchMode m_SearchMode  = SearchMode.DisplayName;
    bool       m_UseRegex    = false;
    string     m_RegexError  = null;
    Vector2    m_Scroll;
    string     m_OutputFolder = "Assets/_CustomShips/";

    SortColumn m_SortCol    = SortColumn.DisplayName;
    bool       m_SortAsc    = true;
    int        m_ChildDepth = 1;

    // Key: guid for root items, "guid|childPath" for child items
    // Value: (assetPath, partName, childPath)  — childPath is "" for root
    readonly Dictionary<string, (string assetPath, string partName, string childPath)> m_Selection =
        new Dictionary<string, (string, string, string)>();

    struct ResultRow
    {
        public string guid, path, partName, displayName;
        public float  dimX, dimY, dimZ, volume, mass;
    }
    readonly List<ResultRow> m_Results = new List<ResultRow>();

    struct ChildRow
    {
        public string childPath, displayName;
        public float  dimX, dimY, dimZ, volume, mass;
        public bool   isPrefab;
    }

    readonly HashSet<string>                   m_Expanded   = new HashSet<string>();
    readonly HashSet<string>                   m_Loading    = new HashSet<string>();
    readonly Dictionary<string, List<ChildRow>> m_ChildCache = new Dictionary<string, List<ChildRow>>();

    Dictionary<string, EnrichedPart> m_Enriched;
    Dictionary<string, EnrichedPart> m_EnrichedByName;

    static GUIStyle s_PathStyle;
    static GUIStyle s_ChildBgStyle;

    static string LoadingLabel()
    {
        int frame = (int)(EditorApplication.timeSinceStartup * 3.0) % 3;
        return frame == 0 ? "·" : frame == 1 ? "··" : "···";
    }

    void EnsureStyles()
    {
        if (s_PathStyle != null) return;
        s_PathStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false };

        var tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, new Color(0.18f, 0.22f, 0.28f, 1f));
        tex.Apply();
        s_ChildBgStyle = new GUIStyle(GUIStyle.none);
        s_ChildBgStyle.normal.background = tex;
    }

    [MenuItem("Shipbuilder/Import Game Part Wizard")]
    static void Open()
    {
        var w = GetWindow<ImportGamePartWizard>("Import Game Part");
        w.m_OutputFolder = EditorPrefs.GetString(PrefOutputFolder, "Assets/_CustomShips/");
        w.LoadEnrichedData();
    }

    void LoadEnrichedData()
    {
        var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "known_assets_enriched.json"));
        if (!File.Exists(path)) { m_Enriched = new Dictionary<string, EnrichedPart>(); return; }
        try
        {
            m_Enriched = JsonConvert.DeserializeObject<Dictionary<string, EnrichedPart>>(
                File.ReadAllText(path)) ?? new Dictionary<string, EnrichedPart>();
        }
        catch { m_Enriched = new Dictionary<string, EnrichedPart>(); }

        m_EnrichedByName = new Dictionary<string, EnrichedPart>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var ep in m_Enriched.Values)
            if (!string.IsNullOrEmpty(ep.PartName))
                m_EnrichedByName[ep.PartName] = ep;

        m_LastSearch = null;
    }

    EnrichedPart FindEnrichedByName(string name)
    {
        if (m_EnrichedByName == null) return null;
        m_EnrichedByName.TryGetValue(name, out var ep);
        return ep;
    }

    void BeginExpandLoad(string guid)
    {
        if (m_ChildCache.ContainsKey(guid)) { m_Expanded.Add(guid); return; }
        m_Loading.Add(guid);
        int capturedDepth = m_ChildDepth;

        var locOp = Addressables.LoadResourceLocationsAsync(guid, typeof(GameObject));
        locOp.Completed += locRes =>
        {
            if (locRes.Status != AsyncOperationStatus.Succeeded || locRes.Result?.Count == 0)
            {
                m_Loading.Remove(guid);
                Repaint();
                return;
            }

            var loadOp = Addressables.LoadAssetAsync<GameObject>(locRes.Result[0]);
            loadOp.Completed += res =>
            {
                m_Loading.Remove(guid);
                if (res.Status == AsyncOperationStatus.Succeeded && res.Result != null)
                {
                    var children = new List<ChildRow>();
                    CollectChildren(res.Result.transform, "", 1, capturedDepth, children);
                    m_ChildCache[guid] = children;
                    m_Expanded.Add(guid);
                }
                Repaint();
            };
        };
    }

    void CollectChildren(Transform t, string parentPath, int depth, int maxDepth, List<ChildRow> rows)
    {
        foreach (Transform child in t)
        {
            var path = parentPath == "" ? child.name : parentPath + "/" + child.name;
            var ep   = FindEnrichedByName(child.name);
            float[] d = ep?.Dims;
            rows.Add(new ChildRow
            {
                childPath   = path,
                displayName = ep?.DisplayName ?? child.name,
                dimX        = d != null && d.Length > 0 ? d[0] : 0f,
                dimY        = d != null && d.Length > 1 ? d[1] : 0f,
                dimZ        = d != null && d.Length > 2 ? d[2] : 0f,
                volume      = ep?.Volume ?? 0f,
                mass        = ep?.Mass   ?? 0f,
                isPrefab    = child.name.StartsWith("PRF_", System.StringComparison.OrdinalIgnoreCase),
            });
            if (depth < maxDepth)
                CollectChildren(child, path, depth + 1, maxDepth, rows);
        }
    }

    void OnGUI()
    {
        EnsureStyles();

        if (m_Enriched == null) LoadEnrichedData();

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
        var newSearch  = EditorGUILayout.TextField("Name", m_Search);
        var newPrefabs = EditorGUILayout.ToggleLeft("Prefabs only", m_PrefabsOnly, GUILayout.Width(100));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Search by", GUILayout.Width(60));
        var newMode  = (SearchMode)EditorGUILayout.EnumPopup(m_SearchMode, GUILayout.Width(120));
        var newRegex = EditorGUILayout.ToggleLeft("Regex", m_UseRegex, GUILayout.Width(55));
        int enrichedCount = m_Enriched?.Count ?? 0;
        EditorGUILayout.LabelField(
            enrichedCount > 0 ? $"{enrichedCount} enriched" : "no enriched data",
            EditorStyles.miniLabel);
        if (GUILayout.Button("↺", GUILayout.Width(22))) LoadEnrichedData();
        EditorGUILayout.EndHorizontal();

        if (m_RegexError != null)
            EditorGUILayout.HelpBox($"Regex: {m_RegexError}", MessageType.Error);

        if (newSearch != m_Search || newPrefabs != m_PrefabsOnly ||
            newMode != m_SearchMode || newRegex != m_UseRegex || m_LastSearch == null)
        {
            m_Search      = newSearch;
            m_PrefabsOnly = newPrefabs;
            m_SearchMode  = newMode;
            m_UseRegex    = newRegex;
            m_LastSearch  = m_Search;
            RebuildResults();
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(
            $"{m_Results.Count} result{(m_Results.Count == 1 ? "" : "s")}" +
            (m_Results.Count >= MaxResults ? $" (capped at {MaxResults})" : "") +
            (m_Selection.Count > 0 ? $"  —  {m_Selection.Count} selected" : ""),
            EditorStyles.miniLabel);
        GUILayout.Label("Child depth:", EditorStyles.miniLabel, GUILayout.Width(70));
        int newDepth = EditorGUILayout.IntField(m_ChildDepth, GUILayout.Width(28));
        newDepth = Mathf.Clamp(newDepth, 1, 8);
        if (newDepth != m_ChildDepth)
        {
            m_ChildDepth = newDepth;
            m_ChildCache.Clear();
            m_Expanded.Clear();
        }
        EditorGUILayout.EndHorizontal();

        // ── Column layout ─────────────────────────────────────────────────────
        // -20 window padding, -16 vertical scrollbar, -4 safety margin
        float viewW    = EditorGUIUtility.currentViewWidth - 40f;
        float flexW    = Mathf.Max(120f, viewW - W_EXP - W_SEL - W_DIM * 3 - W_VOL - W_MASS);
        float nameColW = Mathf.Floor(flexW * 0.5f);

        // ── Table header ──────────────────────────────────────────────────────
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label("▶", EditorStyles.toolbarButton, GUILayout.Width(W_EXP));
        GUILayout.Label("✓", EditorStyles.toolbarButton, GUILayout.Width(W_SEL));
        SortHeader("Part Name",    SortColumn.PartName,    nameColW);
        SortHeader("Display Name", SortColumn.DisplayName, nameColW);
        SortHeader("X",            SortColumn.DimX,        W_DIM);
        SortHeader("Y",            SortColumn.DimY,        W_DIM);
        SortHeader("Z",            SortColumn.DimZ,        W_DIM);
        SortHeader("Vol",          SortColumn.Volume,      W_VOL);
        SortHeader("Mass",         SortColumn.Mass,        W_MASS);
        EditorGUILayout.EndHorizontal();

        // ── Results scroll ────────────────────────────────────────────────────
        m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.Height(300));
        foreach (var r in m_Results)
        {
            bool sel        = m_Selection.ContainsKey(r.guid);
            bool isExpanded = m_Expanded.Contains(r.guid);
            bool isLoading  = m_Loading.Contains(r.guid);

            EditorGUILayout.BeginVertical(sel ? GUI.skin.box : GUIStyle.none);

            // Row 1 — expand toggle + select + data fields
            EditorGUILayout.BeginHorizontal();
            string expLabel = isLoading ? LoadingLabel() : (isExpanded ? "▼" : "▶");
            if (GUILayout.Button(expLabel, EditorStyles.miniButton, GUILayout.Width(W_EXP_BTN)))
            {
                if (!isLoading)
                {
                    if (isExpanded) m_Expanded.Remove(r.guid);
                    else BeginExpandLoad(r.guid);
                }
            }
            GUILayout.Space(W_EXP - W_EXP_BTN);
            if (GUILayout.Button(sel ? "✓" : " ", EditorStyles.miniButton, GUILayout.Width(W_SEL_BTN)))
            {
                if (sel) m_Selection.Remove(r.guid);
                else     m_Selection[r.guid] = (r.path, r.partName, "");
            }
            GUILayout.Space(W_SEL - W_SEL_BTN);
            var lbl = sel ? EditorStyles.boldLabel : EditorStyles.label;
            GUILayout.Label(r.partName,       lbl, GUILayout.Width(nameColW));
            GUILayout.Label(r.displayName,    lbl, GUILayout.Width(nameColW));
            GUILayout.Label(FmtDim(r.dimX),   lbl, GUILayout.Width(W_DIM));
            GUILayout.Label(FmtDim(r.dimY),   lbl, GUILayout.Width(W_DIM));
            GUILayout.Label(FmtDim(r.dimZ),   lbl, GUILayout.Width(W_DIM));
            GUILayout.Label(FmtVol(r.volume), lbl, GUILayout.Width(W_VOL));
            GUILayout.Label(FmtMass(r.mass),  lbl, GUILayout.Width(W_MASS));
            EditorGUILayout.EndHorizontal();

            // Row 2 — path
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(W_EXP + W_SEL + 4f);
            GUILayout.Label(r.path, s_PathStyle);
            EditorGUILayout.EndHorizontal();

            // Child rows (lazy-loaded, shown when expanded)
            if (isExpanded && m_ChildCache.TryGetValue(r.guid, out var children))
            {
                if (children.Count == 0)
                {
                    EditorGUILayout.BeginHorizontal(s_ChildBgStyle);
                    GUILayout.Space(W_EXP + W_SEL + 8f);
                    GUILayout.Label("(no direct children)", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    foreach (var child in children)
                    {
                        if (m_PrefabsOnly && !child.isPrefab)
                            continue;

                        var childKey = r.guid + "|" + child.childPath;
                        bool childSel = m_Selection.ContainsKey(childKey);
                        int slashes = 0;
                        foreach (char c in child.childPath) if (c == '/') slashes++;
                        float indent = slashes * W_CHILD_INDENT;
                        int lastSlash = child.childPath.LastIndexOf('/');
                        var partSegment = lastSlash >= 0 ? child.childPath.Substring(lastSlash + 1) : child.childPath;

                        EditorGUILayout.BeginHorizontal(s_ChildBgStyle);
                        GUILayout.Space(W_EXP + 4f);
                        if (GUILayout.Button(childSel ? "✓" : " ", EditorStyles.miniButton, GUILayout.Width(W_SEL_BTN)))
                        {
                            if (childSel) m_Selection.Remove(childKey);
                            else          m_Selection[childKey] = (r.path, child.childPath, child.childPath);
                        }
                        GUILayout.Space(W_SEL - W_SEL_BTN);
                        var cLbl = childSel ? EditorStyles.boldLabel : EditorStyles.miniLabel;
                        GUILayout.Space(indent);
                        GUILayout.Label(partSegment, cLbl, GUILayout.Width(nameColW - indent));
                        GUILayout.Space(indent);
                        GUILayout.Label(child.displayName, cLbl, GUILayout.Width(nameColW - indent));
                        GUILayout.Label(FmtDim(child.dimX),   cLbl, GUILayout.Width(W_DIM));
                        GUILayout.Label(FmtDim(child.dimY),   cLbl, GUILayout.Width(W_DIM));
                        GUILayout.Label(FmtDim(child.dimZ),   cLbl, GUILayout.Width(W_DIM));
                        GUILayout.Label(FmtVol(child.volume), cLbl, GUILayout.Width(W_VOL));
                        GUILayout.Label(FmtMass(child.mass),  cLbl, GUILayout.Width(W_MASS));
                        EditorGUILayout.EndHorizontal();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();

        if (m_Loading.Count > 0) Repaint();

        EditorGUILayout.Space();

        // ── Selection summary ─────────────────────────────────────────────────
        GUILayout.Label("Selected", EditorStyles.boldLabel);
        if (m_Selection.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "Click ✓ to select a whole part, or ▶ to expand and select individual children.",
                MessageType.None);
        }
        else
        {
            foreach (var kv in m_Selection)
            {
                bool isChild = kv.Key.Contains("|");
                var label    = isChild ? $"  └ {kv.Value.partName}" : kv.Value.partName;
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            }
        }

        EditorGUILayout.Space();

        // ── Output folder ─────────────────────────────────────────────────────
        GUILayout.Label("Output", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Parts are grouped by source folder:  Output/Prefabs/SourceFolder/PartName.prefab",
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

        // ── Import buttons ────────────────────────────────────────────────────
        string error = Validate();
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Cancel", GUILayout.Height(32), GUILayout.Width(90)))
            Close();
        GUILayout.FlexibleSpace();
        GUI.enabled = error == null;
        var importLabel = m_Selection.Count > 1 ? $"Import {m_Selection.Count} Parts" : "Import Selected";
        if (GUILayout.Button(importLabel, GUILayout.Height(32), GUILayout.Width(150)))
            DoImport();
        if (GUILayout.Button("Import & Close", GUILayout.Height(32), GUILayout.Width(130)))
        {
            if (DoImport()) Close();
        }
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();
        if (error != null) EditorGUILayout.HelpBox(error, MessageType.Error);
    }

    void SortHeader(string label, SortColumn col, float width)
    {
        string text = m_SortCol == col ? $"{label} {(m_SortAsc ? "↑" : "↓")}" : label;
        if (GUILayout.Button(text, EditorStyles.toolbarButton, GUILayout.Width(width)))
        {
            if (m_SortCol == col) m_SortAsc = !m_SortAsc;
            else { m_SortCol = col; m_SortAsc = true; }
            ApplySort();
        }
    }

    static string FmtDim(float v)  => v > 0f ? $"{v:F2}m" : "—";
    static string FmtVol(float v)  => v > 0f ? $"{v:F2}" : "—";
    static string FmtMass(float v) => v > 0f ? $"{v:F1}" : "—";

    void RebuildResults()
    {
        m_Results.Clear();
        m_RegexError = null;
        var term = m_Search.Trim();

        Regex regex = null;
        if (m_UseRegex && !string.IsNullOrEmpty(term))
        {
            try   { regex = new Regex(term, RegexOptions.IgnoreCase); }
            catch (System.Exception e) { m_RegexError = e.Message; return; }
        }

        foreach (var kv in LoadGameAssets.knownAssetMap)
        {
            var path = kv.Value;
            if (m_PrefabsOnly && !path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                continue;

            var partName = Path.GetFileNameWithoutExtension(path);
            EnrichedPart enriched = null;
            m_Enriched?.TryGetValue(kv.Key, out enriched);
            var displayName = enriched?.DisplayName ?? "";

            if (!string.IsNullOrEmpty(term))
            {
                string target = m_SearchMode switch
                {
                    SearchMode.Path     => path,
                    SearchMode.PartName => partName,
                    _                   => displayName,
                };
                bool match = regex != null
                    ? regex.IsMatch(target)
                    : target.IndexOf(term, System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (!match) continue;
            }

            float[] d = enriched?.Dims;
            m_Results.Add(new ResultRow
            {
                guid        = kv.Key,
                path        = path,
                partName    = partName,
                displayName = displayName,
                dimX        = d != null && d.Length > 0 ? d[0] : 0f,
                dimY        = d != null && d.Length > 1 ? d[1] : 0f,
                dimZ        = d != null && d.Length > 2 ? d[2] : 0f,
                volume      = enriched?.Volume ?? 0f,
                mass        = enriched?.Mass   ?? 0f,
            });

            if (m_Results.Count >= MaxResults) break;
        }

        ApplySort();
    }

    void ApplySort()
    {
        m_Results.Sort((a, b) =>
        {
            int cmp = m_SortCol switch
            {
                SortColumn.PartName => string.Compare(a.partName, b.partName,
                                           System.StringComparison.OrdinalIgnoreCase),
                SortColumn.DimX    => a.dimX.CompareTo(b.dimX),
                SortColumn.DimY    => a.dimY.CompareTo(b.dimY),
                SortColumn.DimZ    => a.dimZ.CompareTo(b.dimZ),
                SortColumn.Volume  => a.volume.CompareTo(b.volume),
                SortColumn.Mass    => a.mass.CompareTo(b.mass),
                _                  => string.Compare(a.displayName, b.displayName,
                                           System.StringComparison.OrdinalIgnoreCase),
            };
            return m_SortAsc ? cmp : -cmp;
        });
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

    static string LastFolderSegment(string assetPath)
    {
        var dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? "";
        int slash = dir.LastIndexOf('/');
        return slash >= 0 ? dir.Substring(slash + 1) : dir;
    }

    bool DoImport()
    {
        EditorPrefs.SetString(PrefOutputFolder, m_OutputFolder);
        var outRoot = m_OutputFolder.TrimEnd('/');

        var prefabsRoot = $"{outRoot}/Prefabs";

        var conflicts = new List<string>();
        foreach (var kv in m_Selection)
        {
            var subFolder = LastFolderSegment(kv.Value.assetPath);
            var name = kv.Value.partName;
            if (File.Exists(Path.GetFullPath($"{prefabsRoot}/{subFolder}/{name}.prefab")))
                conflicts.Add(name);
        }

        if (conflicts.Count > 0)
        {
            var msg = conflicts.Count == 1
                ? $"{conflicts[0]} already exists. Overwrite?"
                : $"{conflicts.Count} parts already exist:\n{string.Join(", ", conflicts)}\n\nOverwrite all?";
            if (!EditorUtility.DisplayDialog("Overwrite?", msg, "Yes", "Cancel"))
                return false;
        }

        if (!AssetDatabase.IsValidFolder(prefabsRoot))
            AssetDatabase.CreateFolder(outRoot, "Prefabs");

        var created = new List<GameObject>();
        foreach (var kv in m_Selection)
        {
            int sep       = kv.Key.IndexOf("|", System.StringComparison.Ordinal);
            var guid      = sep >= 0 ? kv.Key.Substring(0, sep) : kv.Key;
            var childPath = kv.Value.childPath;
            var partName  = kv.Value.partName;
            var subFolder = LastFolderSegment(kv.Value.assetPath);

            var partFolder = $"{prefabsRoot}/{subFolder}";
            var prefabPath = $"{partFolder}/{partName}.prefab";

            if (!AssetDatabase.IsValidFolder(partFolder))
                AssetDatabase.CreateFolder(prefabsRoot, subFolder);

            if (File.Exists(Path.GetFullPath(prefabPath)))
                AssetDatabase.DeleteAsset(prefabPath);

            var go     = new GameObject(partName);
            var loader = go.AddComponent<AddressableLoader>();
            loader.assetGUID = guid;
            if (!string.IsNullOrEmpty(childPath))
                loader.childPath = childPath;

            PrefabUtility.SaveAsPrefabAsset(go, prefabPath);
            DestroyImmediate(go);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab != null) created.Add(prefab);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (created.Count > 0)
        {
            Selection.objects = created.ToArray();
            EditorGUIUtility.PingObject(created[created.Count - 1]);
        }

        var count = created.Count;
        bool firstTime = !EditorPrefs.GetBool(PrefImportedOnce, false);
        var resultMsg = $"Created {count} prefab{(count == 1 ? "" : "s")} in  {outRoot}/";
        if (firstTime)
        {
            resultMsg += "\n\nNext steps:\n" +
                         "1. Drag a prefab into your ship hierarchy in the scene\n" +
                         "2. Run  Shipbreaker → Force View Refresh  to see it render\n" +
                         "3. Position and build as normal";
            EditorPrefs.SetBool(PrefImportedOnce, true);
        }
        EditorUtility.DisplayDialog("Imported", resultMsg, "OK");

        m_Selection.Clear();
        return true;
    }

    class EnrichedPart
    {
        [JsonProperty("partName")]    public string  PartName;
        [JsonProperty("displayName")] public string  DisplayName;
        [JsonProperty("dims")]        public float[] Dims;
        [JsonProperty("volume")]      public float   Volume;
        [JsonProperty("mass")]        public float   Mass;
    }
}

#endif
