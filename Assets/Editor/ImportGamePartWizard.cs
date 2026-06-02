#if UNITY_EDITOR

using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    int          m_MaxResults      = 250;

    enum SearchMode { PartName, DisplayName, Path, GUID }
    enum SortColumn { DisplayName, PartName, DimX, DimY, DimZ, Volume, Mass }

    const float W_SEL          = 26f;
    const float W_SEL_BTN      = 22f;
    const float W_CHILD_INDENT = 12f;
    const float W_DIM          = 54f;
    const float W_VOL          = 60f;
    const float W_MASS         = 60f;
    const float W_PREVIEW      = 26f;

    string     m_Search      = "";
    string     m_LastSearch  = null;
    bool       m_PrefabsOnly = true;
    SearchMode m_SearchMode  = SearchMode.PartName;
    bool       m_UseRegex    = false;
    string     m_RegexError  = null;
    Vector2    m_Scroll;
    string     m_OutputFolder = "Assets/_CustomShips/";
    string     m_StatusLine   = "";

    SortColumn m_SortCol    = SortColumn.PartName;
    bool       m_SortAsc    = true;
    int        m_ChildDepth = 2;

    struct SearchEntry { public string term; public SearchMode mode; }
    readonly List<SearchEntry> m_SearchHistory = new List<SearchEntry>();
    int                        m_SearchHistoryIndex = -1;
    const int                  MaxSearchHistory = 20;
    bool                       m_NavigatedThisFrame = false;
    bool                       m_SearchPending      = false;
    string                     m_ActivePreviewKey   = null;


    // Key: guid for addressable root items, "guid|childPath" for children, "local:path" for local prefabs
    readonly Dictionary<string, (string assetPath, string partName, string childPath, bool isLocal, string guid, RowType rowType)> m_Selection =
        new Dictionary<string, (string, string, string, bool, string, RowType)>();

    enum RowType { Addressable, LocalAddressable, LocalBaked }

    struct ResultRow
    {
        public string  guid, path, partName, displayName;
        public float   dimX, dimY, dimZ, volume, mass;
        public bool    isLocal => rowType != RowType.Addressable;
        public RowType rowType;
    }
    readonly List<ResultRow> m_Results = new List<ResultRow>();

    struct ChildRow
    {
        public string childPath, displayName;
        public float  dimX, dimY, dimZ, volume, mass;
        public bool   isPrefab;
    }

    readonly HashSet<string>                    m_Expanded   = new HashSet<string>();
    readonly HashSet<string>                    m_Loading    = new HashSet<string>();
    readonly Dictionary<string, List<ChildRow>> m_ChildCache = new Dictionary<string, List<ChildRow>>();

    Dictionary<string, EnrichedPart> m_Enriched;
    Dictionary<string, EnrichedPart> m_EnrichedByName;

    static GUIStyle s_PathStyle;
    static GUIStyle s_ChildBgStyle;
    static GUIStyle s_SelectedRowStyle;
    static GUIStyle s_LocalBakedRowStyle;
    static GUIStyle s_LocalAddressableRowStyle;
    static GUIStyle s_IconButtonStyle;
    static GUIStyle s_IconButtonActiveStyle;
    static GUIStyle s_Row2LabelStyle;
    static GUIStyle s_LegendAddressableStyle;
    static GUIStyle s_LegendBakedStyle;

    static string LoadingLabel()
    {
        int frame = (int)(EditorApplication.timeSinceStartup * 3.0) % 3;
        return frame == 0 ? "·" : frame == 1 ? "··" : "···";
    }

    void EnsureStyles()
    {
        if (s_PathStyle != null && s_SelectedRowStyle?.normal.background != null && s_IconButtonStyle != null && s_IconButtonActiveStyle != null && s_Row2LabelStyle != null) return;

        s_PathStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false };

        int row2Size = Mathf.RoundToInt(EditorStyles.miniLabel.fontSize * 1.5f * 0.8f);
        s_Row2LabelStyle = new GUIStyle(EditorStyles.miniLabel) { fontSize = row2Size };

        var legendAddrTex = new Texture2D(1, 1);
        legendAddrTex.SetPixel(0, 0, new Color(0.28f, 0.26f, 0.14f, 1f));
        legendAddrTex.Apply();
        s_LegendAddressableStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            fontSize = row2Size,
            padding  = new RectOffset(4, 4, 1, 1),
        };
        s_LegendAddressableStyle.normal.background = legendAddrTex;

        var legendBakedTex = new Texture2D(1, 1);
        legendBakedTex.SetPixel(0, 0, new Color(0.18f, 0.28f, 0.18f, 1f));
        legendBakedTex.Apply();
        s_LegendBakedStyle = new GUIStyle(EditorStyles.miniLabel)
        {
            fontSize = row2Size,
            padding  = new RectOffset(4, 4, 1, 1),
        };
        s_LegendBakedStyle.normal.background = legendBakedTex;

        s_IconButtonStyle = new GUIStyle(EditorStyles.miniButton)
        {
            padding = new RectOffset(1, 1, 1, 1),
            margin  = EditorStyles.miniButton.margin,
        };

        var activeIconTex = new Texture2D(1, 1);
        activeIconTex.SetPixel(0, 0, new Color(0.15f, 0.55f, 1.0f, 1f));
        activeIconTex.Apply();
        s_IconButtonActiveStyle = new GUIStyle(s_IconButtonStyle);
        s_IconButtonActiveStyle.normal.background   = activeIconTex;
        s_IconButtonActiveStyle.onNormal.background = activeIconTex;
        s_IconButtonActiveStyle.hover.background    = activeIconTex;
        s_IconButtonActiveStyle.onHover.background  = activeIconTex;
        s_IconButtonActiveStyle.active.background   = activeIconTex;
        s_IconButtonActiveStyle.focused.background  = activeIconTex;

        var selTex = new Texture2D(1, 1);
        selTex.SetPixel(0, 0, new Color(0.17f, 0.36f, 0.53f, 1f));
        selTex.Apply();
        s_SelectedRowStyle = new GUIStyle(GUIStyle.none);
        s_SelectedRowStyle.normal.background = selTex;

        var childTex = new Texture2D(1, 1);
        childTex.SetPixel(0, 0, new Color(0.22f, 0.22f, 0.22f, 1f));
        childTex.Apply();
        s_ChildBgStyle = new GUIStyle(GUIStyle.none);
        s_ChildBgStyle.normal.background = childTex;

        var bakedTex = new Texture2D(1, 1);
        bakedTex.SetPixel(0, 0, new Color(0.18f, 0.28f, 0.18f, 1f));
        bakedTex.Apply();
        s_LocalBakedRowStyle = new GUIStyle(GUIStyle.none);
        s_LocalBakedRowStyle.normal.background = bakedTex;

        var addrTex = new Texture2D(1, 1);
        addrTex.SetPixel(0, 0, new Color(0.28f, 0.26f, 0.14f, 1f));
        addrTex.Apply();
        s_LocalAddressableRowStyle = new GUIStyle(GUIStyle.none);
        s_LocalAddressableRowStyle.normal.background = addrTex;
    }

    [MenuItem("Shipbreaker/Shipbuilder Tools/Import Game Part Wizard", priority = -20)]
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

        Debug.Log($"[ImportGamePartWizard] Loaded {m_Enriched.Count} enriched entries.");
        m_LastSearch = null;
    }

    EnrichedPart FindEnrichedByName(string name)
    {
        if (m_EnrichedByName == null) return null;
        m_EnrichedByName.TryGetValue(name, out var ep);
        return ep;
    }

    // Expand all result rows that share the same path as the given guid (handles duplicate guid entries)
    void ExpandAllByPath(string guid)
    {
        m_Expanded.Add(guid);
        var path = m_Results.Find(r => r.guid == guid).path;
        if (!string.IsNullOrEmpty(path))
            foreach (var r in m_Results)
                if (!r.isLocal && r.path == path) { m_Expanded.Add(r.guid); m_ChildCache[r.guid] = m_ChildCache[guid]; }
    }

    void BeginExpandLoad(string guid, string partName = null)
    {
        string label = string.IsNullOrEmpty(partName) ? guid : partName;
        if (m_ChildCache.ContainsKey(guid))
        {
            ExpandAllByPath(guid);
            m_StatusLine = $"{m_ChildCache[guid].Count} children expanded with depth of {m_ChildDepth} for {label}";
            Repaint();
            return;
        }
        m_Loading.Add(guid);
        int capturedDepth = m_ChildDepth;
        m_StatusLine = $"Loading children with depth of {capturedDepth} for {label}…";
        Repaint();

        var locOp = Addressables.LoadResourceLocationsAsync(guid, typeof(GameObject));
        locOp.Completed += locRes =>
        {
            if (locRes.Status != AsyncOperationStatus.Succeeded || locRes.Result?.Count == 0)
            {
                m_Loading.Remove(guid);
                m_StatusLine = $"Failed to load children for {label}.";
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
                    ExpandAllByPath(guid);
                    int visible = children.Count(c => !m_PrefabsOnly || c.isPrefab);
                    m_StatusLine = visible == 0
                        ? $"No prefab children found (depth {capturedDepth}) for {label}"
                        : $"{visible} children expanded with depth of {capturedDepth} for {label}";
                }
                else
                {
                    // Load failed but still mark expanded so row doesn't stay stuck loading
                    m_Expanded.Add(guid);
                    m_ChildCache[guid] = new List<ChildRow>();
                    m_StatusLine = $"Could not load prefab for {label}.";
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
                displayName = ep?.DisplayName ?? "",
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

        bool hasGameAssets = LoadGameAssets.knownAssetMap != null && LoadGameAssets.knownAssetMap.Count > 0;

        // ── Search ───────────────────────────────────────────────────────────
        GUILayout.Label("Search Game Library", EditorStyles.boldLabel);

        // Row 1: mode + search field + search button + regex + prefabs only + history nav
        m_NavigatedThisFrame = false;
        m_SearchPending      = false;
        EditorGUILayout.BeginHorizontal();
        var newMode    = (SearchMode)EditorGUILayout.EnumPopup(m_SearchMode, GUILayout.Width(110));
        // Reserve layout space, then draw TextField manually so we control Return handling
        var searchRect = GUILayoutUtility.GetRect(GUIContent.none, EditorStyles.textField, GUILayout.ExpandWidth(true), GUILayout.Height(EditorGUIUtility.singleLineHeight));
        bool enterPressed = Event.current.type == EventType.KeyDown &&
            (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
            GUI.GetNameOfFocusedControl() == "SearchField";
        if (enterPressed)
        {
            m_SearchPending = true;
            // Neutralise the Return so TextField doesn't defocus
            Event.current.keyCode = KeyCode.None;
            Event.current.character = '\0';
        }
        GUI.SetNextControlName("SearchField");
        var newSearch = GUI.TextField(searchRect, m_Search, EditorStyles.textField);
        if (GUILayout.Button(EditorGUIUtility.IconContent("Search Icon"), EditorStyles.miniButton, GUILayout.Width(24), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
            m_SearchPending = true;
        GUI.enabled = m_SearchHistoryIndex > 0;
        if (GUILayout.Button("◀", EditorStyles.miniButtonLeft,  GUILayout.Width(20))) { NavigateHistory(-1); m_NavigatedThisFrame = true; GUIUtility.keyboardControl = 0; }
        GUI.enabled = m_SearchHistoryIndex < m_SearchHistory.Count - 1;
        if (GUILayout.Button("▶", EditorStyles.miniButtonRight, GUILayout.Width(20))) { NavigateHistory( 1); m_NavigatedThisFrame = true; GUIUtility.keyboardControl = 0; }
        GUI.enabled = true;
        var newPrefabs = EditorGUILayout.ToggleLeft("Prefabs only", m_PrefabsOnly, GUILayout.Width(95));
        var newRegex   = EditorGUILayout.ToggleLeft("Regex",        m_UseRegex,    GUILayout.Width(55));
        EditorGUILayout.EndHorizontal();

        // Row 2: results · limit · depth · preview hint
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label($"{m_Results.Count} result{(m_Results.Count == 1 ? "" : "s")}", s_Row2LabelStyle, GUILayout.ExpandWidth(false));
        GUILayout.Space(16f);
        GUILayout.Label("limited to", s_Row2LabelStyle, GUILayout.ExpandWidth(false));
        int newLimit = EditorGUILayout.IntField(m_MaxResults, GUILayout.Width(44));
        newLimit = Mathf.Clamp(newLimit, 10, 2000);
        if (newLimit != m_MaxResults) { m_MaxResults = newLimit; RebuildResults(); }
        GUILayout.Label("results", s_Row2LabelStyle, GUILayout.ExpandWidth(false));
        GUILayout.Space(16f);
        GUILayout.Label("Right-click to expand depth:", s_Row2LabelStyle, GUILayout.ExpandWidth(false));
        int newDepth = EditorGUILayout.IntField(m_ChildDepth, GUILayout.Width(28));
        newDepth = Mathf.Clamp(newDepth, 1, 8);
        if (newDepth != m_ChildDepth) { m_ChildDepth = newDepth; m_ChildCache.Clear(); m_Expanded.Clear(); }
        GUILayout.Space(16f);
        GUILayout.Label("Click", s_Row2LabelStyle, GUILayout.ExpandWidth(false));
        GUILayout.Label(EditorGUIUtility.IconContent("visibilityOn"), s_IconButtonStyle, GUILayout.Width(EditorGUIUtility.singleLineHeight), GUILayout.Height(EditorGUIUtility.singleLineHeight));
        GUILayout.Label("to preview", s_Row2LabelStyle, GUILayout.ExpandWidth(false));
        GUILayout.FlexibleSpace();
        GUILayout.Label("Local results:", s_Row2LabelStyle, GUILayout.ExpandWidth(false));
        GUILayout.Space(4f);
        GUILayout.Label("Addressable", s_LegendAddressableStyle, GUILayout.ExpandWidth(false));
        GUILayout.Space(4f);
        GUILayout.Label("Baked", s_LegendBakedStyle, GUILayout.ExpandWidth(false));
        EditorGUILayout.EndHorizontal();

        if (m_RegexError != null)
            EditorGUILayout.HelpBox($"Regex: {m_RegexError}", MessageType.Error);

        if (!hasGameAssets)
            EditorGUILayout.HelpBox("No game assets loaded. Run  Shipbreaker → Reload Assets  to include game library parts.", MessageType.Warning);

        bool filtersChanged = newPrefabs != m_PrefabsOnly || newMode != m_SearchMode
                           || newRegex != m_UseRegex;
        m_PrefabsOnly = newPrefabs;
        m_UseRegex    = newRegex;

        if (m_NavigatedThisFrame)
        {
            // Navigation set m_Search/m_SearchMode already — don't let stale newSearch stomp them
            m_SearchMode = newMode;
            m_LastSearch = m_Search;
            RebuildResults();
            Repaint();
        }
        else if (m_SearchPending)
        {
            m_Search     = newSearch;
            m_SearchMode = newMode;
            m_LastSearch = m_Search;
            RebuildResults();
        }
        else if (filtersChanged || m_LastSearch == null)
        {
            m_SearchMode = newMode;
            m_LastSearch = m_Search;
            RebuildResults();
        }
        else
        {
            m_Search = newSearch;
        }


        // ── Column layout ────────────────────────────────────────────────────
        // position.width is the window width; subtract scrollbar (15) and a 2px border
        float viewW       = position.width - 17f;
        float fixedW      = W_SEL + W_PREVIEW + W_DIM * 3 + W_VOL + W_MASS;
        float flexW       = Mathf.Max(120f, viewW - fixedW);
        float displayColW = Mathf.Floor(flexW * 0.25f);
        float nameColW    = flexW - displayColW;

        // ── Table header ─────────────────────────────────────────────────────
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.Label(EditorGUIUtility.IconContent("visibilityOn"), EditorStyles.toolbarButton, GUILayout.Width(W_PREVIEW));
        GUILayout.Label("✓", EditorStyles.toolbarButton, GUILayout.Width(W_SEL));
        SortHeader("Part Name", SortColumn.PartName,    nameColW);
        SortHeader("Display",   SortColumn.DisplayName, displayColW);
        SortHeader("X",         SortColumn.DimX,        W_DIM);
        SortHeader("Y",         SortColumn.DimY,        W_DIM);
        SortHeader("Z",         SortColumn.DimZ,        W_DIM);
        SortHeader("Vol",       SortColumn.Volume,      W_VOL);
        SortHeader("Mass",      SortColumn.Mass,        W_MASS);
        EditorGUILayout.EndHorizontal();

        // ── Results scroll ────────────────────────────────────────────────────
        m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUI.skin.scrollView, GUILayout.ExpandHeight(true));
        float prevRowBottomY = 0f;
        foreach (var r in m_Results)
        {
            bool sel        = m_Selection.ContainsKey(r.isLocal ? "local:" + r.path : r.guid);
            bool isExpanded = !r.isLocal && m_Expanded.Contains(r.guid);
            bool isLoading  = !r.isLocal && m_Loading.Contains(r.guid);

            var rowBg = sel
                ? s_SelectedRowStyle
                : r.rowType == RowType.LocalBaked       ? s_LocalBakedRowStyle
                : r.rowType == RowType.LocalAddressable ? s_LocalAddressableRowStyle
                : GUIStyle.none;

            float rowTopY = prevRowBottomY;
            EditorGUILayout.BeginVertical(rowBg);

            // Row 1 — select + preview + data fields
            EditorGUILayout.BeginHorizontal();

            bool canPreviewRow = IsPreviewablePath(r.path)
                && (!r.isLocal ? !string.IsNullOrEmpty(r.guid) : !string.IsNullOrEmpty(r.path));
            string rowPreviewKey = r.isLocal ? r.path : r.guid;
            bool   isActivePrev  = m_ActivePreviewKey == rowPreviewKey;
            var previewIcon = !canPreviewRow
                ? EditorGUIUtility.IconContent("scenevis_hidden")
                : isActivePrev
                    ? EditorGUIUtility.IconContent("visibilityOn")
                    : EditorGUIUtility.IconContent("animationvisibilitytoggleon");
            GUI.enabled = canPreviewRow;
            if (GUILayout.Button(previewIcon, isActivePrev ? s_IconButtonActiveStyle : s_IconButtonStyle, GUILayout.Width(W_SEL_BTN), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
            {
                if (isActivePrev) { m_ActivePreviewKey = null; UnityEditor.SceneManagement.StageUtility.GoToMainStage(); }
                else              { m_ActivePreviewKey = rowPreviewKey; OpenPreview(r.rowType, r.isLocal ? r.path : r.guid); }
            }
            GUILayout.Space(W_PREVIEW - W_SEL_BTN);
            GUI.enabled = true;

            string selKey      = r.isLocal ? "local:" + r.path : r.guid;
            bool   isSelectable = r.path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase);
            GUI.enabled = isSelectable;
            if (GUILayout.Button(sel ? "✓" : (isSelectable ? " " : "✕"), EditorStyles.miniButton, GUILayout.Width(W_SEL_BTN), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
            {
                if (sel) m_Selection.Remove(selKey);
                else     m_Selection[selKey] = (r.path, r.partName, "", r.isLocal, r.guid, r.rowType);
            }
            GUI.enabled = true;
            GUILayout.Space(W_SEL - W_SEL_BTN);

            EditorGUILayout.SelectableLabel(r.partName,    EditorStyles.label, GUILayout.Width(nameColW),    GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.SelectableLabel(r.displayName, EditorStyles.label, GUILayout.Width(displayColW), GUILayout.Height(EditorGUIUtility.singleLineHeight));
            GUILayout.Label(FmtDim(r.dimX),   EditorStyles.label, GUILayout.Width(W_DIM));
            GUILayout.Label(FmtDim(r.dimY),   EditorStyles.label, GUILayout.Width(W_DIM));
            GUILayout.Label(FmtDim(r.dimZ),   EditorStyles.label, GUILayout.Width(W_DIM));
            GUILayout.Label(FmtVol(r.volume), EditorStyles.label, GUILayout.Width(W_VOL));
            GUILayout.Label(FmtMass(r.mass),  EditorStyles.label, GUILayout.Width(W_MASS));
            EditorGUILayout.EndHorizontal();

            // Row 2 — guid  path (inline, selectable)
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(W_SEL + W_PREVIEW + 4f);
            if (!r.isLocal)
            {
                EditorGUILayout.SelectableLabel(r.guid + "  " + r.path, s_PathStyle, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            else if (r.rowType == RowType.LocalAddressable)
            {
                // Extract inner addressable GUID from the wrapper prefab for display
                var wrapper    = AssetDatabase.LoadAssetAtPath<GameObject>(r.path);
                var loader     = wrapper?.GetComponentInChildren<AddressableLoader>(true);
                var innerGuid  = loader?.assetGUID ?? "";
                var row2Text   = string.IsNullOrEmpty(innerGuid) ? r.path : innerGuid + "  " + r.path;
                EditorGUILayout.SelectableLabel(row2Text, s_PathStyle, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            else
            {
                EditorGUILayout.SelectableLabel(r.path, s_PathStyle, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();

            // Child rows — outside parent's BeginVertical so parent selection BG doesn't bleed through
            if (isExpanded && m_ChildCache.TryGetValue(r.guid, out var children))
            {
                if (children.Count == 0)
                {
                    EditorGUILayout.BeginHorizontal(s_ChildBgStyle);
                    GUILayout.Space(W_PREVIEW + W_SEL + 8f);
                    GUILayout.Label("(no direct children)", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    bool anyVisible = children.Any(c => !m_PrefabsOnly || c.isPrefab);
                    if (!anyVisible)
                    {
                        EditorGUILayout.BeginHorizontal(s_ChildBgStyle);
                        GUILayout.Space(W_PREVIEW + W_SEL + 8f);
                        GUILayout.Label("(no prefab children)", EditorStyles.miniLabel);
                        EditorGUILayout.EndHorizontal();
                    }
                    foreach (var child in children)
                    {
                        if (m_PrefabsOnly && !child.isPrefab) continue;

                        var childKey = r.guid + "|" + child.childPath;
                        bool childSel = m_Selection.ContainsKey(childKey);
                        int slashes = 0;
                        foreach (char c in child.childPath) if (c == '/') slashes++;
                        float indent = (slashes + 1) * W_CHILD_INDENT;
                        int lastSlash = child.childPath.LastIndexOf('/');
                        var partSegment = lastSlash >= 0 ? child.childPath.Substring(lastSlash + 1) : child.childPath;

                        EditorGUILayout.BeginHorizontal(childSel ? s_SelectedRowStyle : s_ChildBgStyle);
                        // Eye column: disabled "no preview" icon
                        GUI.enabled = false;
                        GUILayout.Button(EditorGUIUtility.IconContent("scenevis_hidden"), s_IconButtonStyle, GUILayout.Width(W_SEL_BTN), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        GUI.enabled = true;
                        GUILayout.Space(W_PREVIEW - W_SEL_BTN);
                        // Check button
                        if (GUILayout.Button(childSel ? "✓" : " ", EditorStyles.miniButton, GUILayout.Width(W_SEL_BTN), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                        {
                            if (childSel) m_Selection.Remove(childKey);
                            else          m_Selection[childKey] = (r.path, child.childPath, child.childPath, false, r.guid, RowType.Addressable);
                        }
                        GUILayout.Space(W_SEL - W_SEL_BTN + indent);
                        EditorGUILayout.SelectableLabel(partSegment,       EditorStyles.miniLabel, GUILayout.Width(nameColW - indent), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        EditorGUILayout.SelectableLabel(child.displayName, EditorStyles.miniLabel, GUILayout.Width(displayColW),        GUILayout.Height(EditorGUIUtility.singleLineHeight));
                        GUILayout.Label(FmtDim(child.dimX),   EditorStyles.miniLabel, GUILayout.Width(W_DIM));
                        GUILayout.Label(FmtDim(child.dimY),   EditorStyles.miniLabel, GUILayout.Width(W_DIM));
                        GUILayout.Label(FmtDim(child.dimZ),   EditorStyles.miniLabel, GUILayout.Width(W_DIM));
                        GUILayout.Label(FmtVol(child.volume), EditorStyles.miniLabel, GUILayout.Width(W_VOL));
                        GUILayout.Label(FmtMass(child.mass),  EditorStyles.miniLabel, GUILayout.Width(W_MASS));
                        EditorGUILayout.EndHorizontal();

                        if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
                        {
                            var childRowRect = GUILayoutUtility.GetLastRect();
                            childRowRect.x = 0; childRowRect.width = EditorGUIUtility.currentViewWidth;
                            if (childRowRect.Contains(Event.current.mousePosition))
                            {
                                var capturedName    = partSegment;
                                var capturedParentGuid = r.guid;
                                var menu = new GenericMenu();
                                menu.AddItem(new GUIContent("Collapse Parent"), false, () => { m_Expanded.Remove(capturedParentGuid); Repaint(); });
                                menu.AddSeparator("");
                                menu.AddItem(new GUIContent("Copy"), false, () => GUIUtility.systemCopyBuffer = capturedName);
                                menu.AddItem(new GUIContent("Search"), false, () => { m_Search = capturedName; m_SearchMode = SearchMode.PartName; m_LastSearch = null; GUIUtility.keyboardControl = 0; Repaint(); });
                                menu.ShowAsContext();
                                Event.current.Use();
                            }
                        }
                    }
                }
            }

            // Right-click context menu — checked after full row is laid out
            prevRowBottomY = GUILayoutUtility.GetLastRect().yMax;
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
            {
                float mouseY = Event.current.mousePosition.y;
                if (mouseY >= rowTopY && mouseY <= prevRowBottomY)
                {
                    var capturedPartName = r.partName;
                    var capturedDisplay  = r.displayName;
                    var capturedPath     = r.path;
                    var capturedLocal    = r.isLocal;
                    // For LocalAddressable, extract inner GUID; for addressable rows use r.guid directly
                    string capturedGuid  = r.guid;
                    if (r.rowType == RowType.LocalAddressable)
                    {
                        var wrapperObj = AssetDatabase.LoadAssetAtPath<GameObject>(r.path);
                        capturedGuid   = wrapperObj?.GetComponentInChildren<AddressableLoader>(true)?.assetGUID ?? "";
                    }
                    var menu = new GenericMenu();
                    if (!r.isLocal)
                    {
                        if (isLoading)
                            menu.AddDisabledItem(new GUIContent("Loading…"));
                        else if (isExpanded)
                            menu.AddItem(new GUIContent("Collapse"), false, () => { m_Expanded.Remove(r.guid); Repaint(); });
                        else
                            menu.AddItem(new GUIContent("Expand Children"), false, () => BeginExpandLoad(r.guid, capturedPartName));
                        menu.AddSeparator("");
                    }
                    if (capturedLocal)
                    {
                        menu.AddItem(new GUIContent("Show in Project"), false, () =>
                        {
                            var asset = AssetDatabase.LoadAssetAtPath<Object>(capturedPath);
                            if (asset != null) { EditorUtility.FocusProjectWindow(); EditorGUIUtility.PingObject(asset); Selection.activeObject = asset; }
                        });
                        menu.AddSeparator("");
                    }
                    menu.AddItem(new GUIContent("Copy/Part Name"), false, () => GUIUtility.systemCopyBuffer = capturedPartName);
                    if (!string.IsNullOrEmpty(capturedGuid))
                        menu.AddItem(new GUIContent("Copy/GUID"), false, () => GUIUtility.systemCopyBuffer = capturedGuid);
                    menu.AddItem(new GUIContent("Copy/Path"), false, () => GUIUtility.systemCopyBuffer = capturedPath);
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Search/Part Name"), false, () => { m_Search = capturedPartName; m_SearchMode = SearchMode.PartName; m_LastSearch = null; GUIUtility.keyboardControl = 0; Repaint(); });
                    if (!string.IsNullOrEmpty(capturedGuid))
                        menu.AddItem(new GUIContent("Search/GUID"), false, () => { m_Search = capturedGuid; m_SearchMode = SearchMode.GUID; m_LastSearch = null; GUIUtility.keyboardControl = 0; Repaint(); });
                    menu.AddItem(new GUIContent("Search/Path"), false, () => { m_Search = capturedPath; m_SearchMode = SearchMode.Path; m_LastSearch = null; GUIUtility.keyboardControl = 0; Repaint(); });
                    if (!string.IsNullOrEmpty(capturedDisplay))
                        menu.AddItem(new GUIContent("Search/Display Name"), false, () => { m_Search = capturedDisplay; m_SearchMode = SearchMode.DisplayName; m_LastSearch = null; GUIUtility.keyboardControl = 0; Repaint(); });
                    menu.ShowAsContext();
                    Event.current.Use();
                }
            }
        }
        EditorGUILayout.EndScrollView();

        if (m_Loading.Count > 0) Repaint();

        // ── Separator ────────────────────────────────────────────────────────
        EditorGUILayout.Space(4f);
        var separatorRect = EditorGUILayout.GetControlRect(false, 1f);
        EditorGUI.DrawRect(separatorRect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        EditorGUILayout.Space(2f);

        // ── Selection summary ─────────────────────────────────────────────────
        int selCount = m_Selection.Count;
        GUILayout.Label(selCount > 0 ? $"{selCount} Selected" : "0 Selected", EditorStyles.boldLabel);
        if (selCount == 0)
        {
            EditorGUILayout.HelpBox(
                "Click ✓ to select a part. Green rows are local project prefabs — use 'Place in Scene'.\nGrey rows are game addressables — use 'Import Selected' to create a loader prefab.",
                MessageType.None);
        }
        else
        {
            string keyToRemove   = null;
            float  prevSelBottomY = GUILayoutUtility.GetLastRect().yMax;
            foreach (var kv in m_Selection)
            {
                bool isChild    = kv.Key.Contains("|");
                var  suffix     = kv.Value.isLocal ? " [local]" : "";
                bool canPreview = !isChild && (!kv.Value.isLocal
                    ? !string.IsNullOrEmpty(kv.Value.guid)
                    : !string.IsNullOrEmpty(kv.Value.assetPath));
                string selPreviewKey   = kv.Value.isLocal ? kv.Value.assetPath : kv.Value.guid;
                bool   isActiveSelPrev = m_ActivePreviewKey == selPreviewKey;
                var    eyeIcon = !canPreview
                    ? EditorGUIUtility.IconContent("scenevis_hidden")
                    : isActiveSelPrev
                        ? EditorGUIUtility.IconContent("visibilityOn")
                        : EditorGUIUtility.IconContent("animationvisibilitytoggleon");

                float selRowTopY = prevSelBottomY;

                // Row 1 — eye + X + name
                EditorGUILayout.BeginHorizontal();
                GUI.enabled = canPreview;
                if (GUILayout.Button(eyeIcon, isActiveSelPrev ? s_IconButtonActiveStyle : s_IconButtonStyle, GUILayout.Width(W_SEL_BTN), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                {
                    if (isActiveSelPrev) { m_ActivePreviewKey = null; UnityEditor.SceneManagement.StageUtility.GoToMainStage(); }
                    else                 { m_ActivePreviewKey = selPreviewKey; OpenPreview(kv.Value.rowType, kv.Value.isLocal ? kv.Value.assetPath : kv.Value.guid); }
                }
                GUILayout.Space(W_PREVIEW - W_SEL_BTN);
                GUI.enabled = true;
                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(W_SEL_BTN), GUILayout.Height(EditorGUIUtility.singleLineHeight)))
                    keyToRemove = kv.Key;
                GUILayout.Space(W_SEL - W_SEL_BTN);
                var partLabel = isChild ? $"  └ {kv.Value.partName}" : kv.Value.partName;
                EditorGUILayout.SelectableLabel(partLabel + suffix, EditorStyles.label, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.EndHorizontal();

                // Row 2 — guid + path
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(W_PREVIEW + W_SEL + 4f);
                if (!kv.Value.isLocal && !string.IsNullOrEmpty(kv.Value.guid))
                    EditorGUILayout.SelectableLabel(kv.Value.guid + "  " + kv.Value.assetPath, s_PathStyle, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                else if (!string.IsNullOrEmpty(kv.Value.assetPath))
                    EditorGUILayout.SelectableLabel(kv.Value.assetPath, s_PathStyle, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                EditorGUILayout.EndHorizontal();

                // Right-click context menu
                prevSelBottomY = GUILayoutUtility.GetLastRect().yMax;
                if (Event.current.type == EventType.MouseDown && Event.current.button == 1)
                {
                    float mouseY = Event.current.mousePosition.y;
                    if (mouseY >= selRowTopY && mouseY <= prevSelBottomY)
                    {
                        var capturedPartName  = kv.Value.partName;
                        var capturedGuid      = kv.Value.guid;
                        var capturedPath      = kv.Value.assetPath;
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Copy/Part Name"), false, () => GUIUtility.systemCopyBuffer = capturedPartName);
                        if (!string.IsNullOrEmpty(capturedGuid))
                            menu.AddItem(new GUIContent("Copy/GUID"),  false, () => GUIUtility.systemCopyBuffer = capturedGuid);
                        if (!string.IsNullOrEmpty(capturedPath))
                            menu.AddItem(new GUIContent("Copy/Path"),  false, () => GUIUtility.systemCopyBuffer = capturedPath);
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent("Search/Part Name"), false, () => { m_Search = capturedPartName; m_SearchMode = SearchMode.PartName; m_LastSearch = null; GUIUtility.keyboardControl = 0; Repaint(); });
                        if (!string.IsNullOrEmpty(capturedGuid))
                            menu.AddItem(new GUIContent("Search/GUID"),  false, () => { m_Search = capturedGuid; m_SearchMode = SearchMode.GUID; m_LastSearch = null; GUIUtility.keyboardControl = 0; Repaint(); });
                        if (!string.IsNullOrEmpty(capturedPath))
                            menu.AddItem(new GUIContent("Search/Path"),  false, () => { m_Search = capturedPath; m_SearchMode = SearchMode.Path; m_LastSearch = null; GUIUtility.keyboardControl = 0; Repaint(); });
                        menu.ShowAsContext();
                        Event.current.Use();
                    }
                }
            }
            if (keyToRemove != null) m_Selection.Remove(keyToRemove);
        }

        EditorGUILayout.Space();

        // ── Output folder + action buttons ───────────────────────────────────
        bool hasPureAddressable = false;
        bool hasBakeable        = false;
        int  importCount        = 0;
        int  bakeCount          = 0;
        foreach (var kv in m_Selection)
        {
            if (!kv.Value.isLocal)                                 { hasPureAddressable = hasBakeable = true; importCount++; bakeCount++; }
            else if (kv.Value.rowType == RowType.LocalAddressable) { hasBakeable = true; bakeCount++; }
        }
        bool needsFolder = hasPureAddressable || hasBakeable;

        EditorGUILayout.BeginHorizontal();

        if (needsFolder)
        {
            GUILayout.Label("Output:", EditorStyles.miniLabel, GUILayout.Width(46));
            m_OutputFolder = EditorGUILayout.TextField(m_OutputFolder);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                var picked = EditorUtility.OpenFolderPanel("Select Output Folder", Application.dataPath, "");
                if (!string.IsNullOrEmpty(picked) && picked.StartsWith(Application.dataPath))
                    m_OutputFolder = "Assets" + picked.Substring(Application.dataPath.Length).Replace('\\', '/');
            }
            GUILayout.Space(1f);
            EditorGUI.DrawRect(EditorGUILayout.GetControlRect(false, 22f, GUILayout.Width(1)), new Color(0.4f, 0.4f, 0.4f, 1f));
            GUILayout.Space(1f);
        }
        else
        {
            GUILayout.FlexibleSpace();
        }

        if (hasPureAddressable)
        {
            string importError = ValidateImport();
            GUI.enabled = importError == null;
            var importLabel = importCount > 1 ? $"Import {importCount} Parts" : "Import Selected";
            if (GUILayout.Button(importLabel, GUILayout.Height(22), GUILayout.Width(150)))
                DoImport();
            GUI.enabled = true;
        }

        if (hasBakeable)
        {
            var bakeLabel = bakeCount > 1 ? $"Bake {bakeCount} Parts" : "Bake Selected";
            if (GUILayout.Button(new GUIContent(bakeLabel,
                "Bakes selected addressables into self-contained prefabs (real meshes/materials, " +
                "per-mesh StructureParts copying each source SP material). No AddressableLoader/runtime dependency."),
                GUILayout.Height(22), GUILayout.Width(150)))
                DoBake();
        }

        if (m_Selection.Count > 0)
        {
            if (GUILayout.Button("Place in Scene", GUILayout.Height(22), GUILayout.Width(130)))
                DoPlaceLocal();
        }

        EditorGUILayout.EndHorizontal();

        // ── Status line ───────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(m_StatusLine))
            EditorGUILayout.LabelField(m_StatusLine, EditorStyles.miniLabel);
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

    static bool IsPreviewablePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var ext = Path.GetExtension(path);
        return ext.Equals(".prefab", System.StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".fbx",    System.StringComparison.OrdinalIgnoreCase);
    }

    static string FmtDim(float v)  => v > 0f ? $"{v:F2}m" : "—";
    static string FmtVol(float v)  => v > 0f ? $"{v:F2}" : "—";
    static string FmtMass(float v) => v > 0f ? $"{v:F1}" : "—";

    void PushSearchHistory(string term)
    {
        if (string.IsNullOrEmpty(term)) return;
        var entry = new SearchEntry { term = term, mode = m_SearchMode };
        if (m_SearchHistory.Count > 0 && m_SearchHistoryIndex >= 0)
        {
            var cur = m_SearchHistory[m_SearchHistoryIndex];
            if (cur.term == entry.term && cur.mode == entry.mode) return;
        }
        if (m_SearchHistoryIndex < m_SearchHistory.Count - 1)
            m_SearchHistory.RemoveRange(m_SearchHistoryIndex + 1, m_SearchHistory.Count - m_SearchHistoryIndex - 1);
        m_SearchHistory.Add(entry);
        if (m_SearchHistory.Count > MaxSearchHistory)
            m_SearchHistory.RemoveAt(0);
        m_SearchHistoryIndex = m_SearchHistory.Count - 1;
    }

    void NavigateHistory(int delta)
    {
        int next = Mathf.Clamp(m_SearchHistoryIndex + delta, 0, m_SearchHistory.Count - 1);
        if (next == m_SearchHistoryIndex) return;
        m_SearchHistoryIndex = next;
        var e        = m_SearchHistory[m_SearchHistoryIndex];
        m_Search     = e.term;
        m_SearchMode = e.mode;
        m_LastSearch = null;
    }

    void RebuildResults()
    {
        m_Results.Clear();
        m_RegexError = null;
        var term = m_Search.Trim();
        PushSearchHistory(term);

        Regex regex = null;
        if (m_UseRegex && !string.IsNullOrEmpty(term))
        {
            try   { regex = new Regex(term, RegexOptions.IgnoreCase); }
            catch (System.Exception e) { m_RegexError = e.Message; return; }
        }

        // ── Game addressables ─────────────────────────────────────────────────
        if (LoadGameAssets.knownAssetMap != null)
        {
            foreach (var kv in LoadGameAssets.knownAssetMap)
            {
                var path = kv.Value;
                if (m_PrefabsOnly && !path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                    continue;

                var partName = Path.GetFileNameWithoutExtension(path);
                EnrichedPart enriched = null;
                m_Enriched?.TryGetValue(kv.Key, out enriched);
                var displayName = enriched?.DisplayName ?? "";

                if (!MatchesTerm(term, regex, partName, displayName, path, kv.Key)) continue;

                float[] d = enriched?.Dims;
                if (m_Results.Count >= m_MaxResults) break;
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
                    rowType     = RowType.Addressable,
                });
            }
        }

        // ── Local project prefabs ─────────────────────────────────────────────
        var localGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_CustomShips" });
        foreach (var g in localGuids)
        {
            var path     = AssetDatabase.GUIDToAssetPath(g);
            var partName = Path.GetFileNameWithoutExtension(path);

            if (!MatchesTerm(term, regex, partName, "", path)) continue;

            if (m_Results.Count >= m_MaxResults) break;
            var prefab    = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var hasLoader = prefab != null && prefab.GetComponentInChildren<AddressableLoader>(true) != null;
            m_Results.Add(new ResultRow
            {
                guid        = g,
                path        = path,
                partName    = partName,
                displayName = "",
                rowType     = hasLoader ? RowType.LocalAddressable : RowType.LocalBaked,
            });
        }

        ApplySort();
    }

    bool MatchesTerm(string term, Regex regex, string partName, string displayName, string path, string guid = "")
    {
        if (string.IsNullOrEmpty(term)) return true;
        string target = m_SearchMode switch
        {
            SearchMode.Path        => path,
            SearchMode.DisplayName => displayName,
            SearchMode.GUID        => guid,
            _                      => partName,
        };
        return regex != null
            ? regex.IsMatch(target)
            : target.IndexOf(term, System.StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void ApplySort()
    {
        m_Results.Sort((a, b) =>
        {
            // Local prefabs always sort after game addressables
            if (a.isLocal != b.isLocal) return a.isLocal ? 1 : -1;

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

    string ValidateImport()
    {
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

    void OpenPreview(RowType rowType, string guidOrPath)
    {
        if (rowType == RowType.LocalAddressable)
        {
            // Local wrapper prefab — extract the inner AddressableLoader GUID and preview via EditorCache.
            var wrapper = AssetDatabase.LoadAssetAtPath<GameObject>(guidOrPath);
            var innerLoader = wrapper != null ? wrapper.GetComponentInChildren<AddressableLoader>(true) : null;
            var innerGuid = innerLoader?.assetGUID ?? innerLoader?.refs?[0];
            if (!string.IsNullOrEmpty(innerGuid))
            {
                OpenPreview(RowType.Addressable, innerGuid);
                return;
            }
            m_StatusLine = "Could not find AddressableLoader GUID in local prefab.";
            return;
        }

        if (rowType == RowType.LocalBaked)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(guidOrPath);
            if (prefab == null) { m_StatusLine = $"Could not load prefab at '{guidOrPath}'."; return; }
            CustomStage.go = prefab;
            UnityEditor.SceneManagement.StageUtility.GoToStage(ScriptableObject.CreateInstance<CustomStage>(), true);
            return;
        }

        // Addressable — prefer EditorCache prefab (fake shaders render correctly).
        var cachePath = $"Assets/EditorCache/{guidOrPath}.prefab";
        var cached = AssetDatabase.LoadAssetAtPath<GameObject>(cachePath);
        if (cached != null)
        {
            CustomStage.go = cached;
            UnityEditor.SceneManagement.StageUtility.GoToStage(ScriptableObject.CreateInstance<CustomStage>(), true);
            return;
        }

        Addressables.LoadAssetAsync<GameObject>(new AssetReferenceGameObject(guidOrPath)).Completed += res =>
        {
            if (res.Status != AsyncOperationStatus.Succeeded || res.Result == null)
            {
                m_StatusLine = $"Failed to load addressable '{guidOrPath}'.";
                Repaint();
                return;
            }
            CustomStage.go = res.Result;
            UnityEditor.SceneManagement.StageUtility.GoToStage(ScriptableObject.CreateInstance<CustomStage>(), true);
        };
    }

    void DoPlaceLocal()
    {
        int count = 0;
        var placed = new List<GameObject>();
        Transform placementParent = Selection.activeGameObject?.transform;

        foreach (var kv in m_Selection)
        {
            if (kv.Value.isLocal)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(kv.Value.assetPath);
                if (prefab == null) continue;
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                if (inst == null) continue;
                Undo.RegisterCreatedObjectUndo(inst, "Place Part");
                if (placementParent != null)
                    inst.transform.SetParent(placementParent, false);
                placed.Add(inst);
                count++;
            }
            else
            {
                // Pure addressable — create a loader node directly in the scene (unsaved).
                int sep       = kv.Key.IndexOf("|", System.StringComparison.Ordinal);
                var guid      = sep >= 0 ? kv.Key.Substring(0, sep) : kv.Key;
                var childPath = kv.Value.childPath;
                var partName  = kv.Value.partName;
                var go = new GameObject(partName);
                Undo.RegisterCreatedObjectUndo(go, "Place Part");
                if (placementParent != null)
                    go.transform.SetParent(placementParent, false);
                var loader = go.AddComponent<AddressableLoader>();
                loader.assetGUID = guid;
                if (!string.IsNullOrEmpty(childPath)) loader.childPath = childPath;
                placed.Add(go);
                count++;
            }
        }
        if (placed.Count > 0)
            Selection.objects = placed.ToArray();
        m_StatusLine = count > 0
            ? $"Placed {count} part{(count == 1 ? "" : "s")} in scene."
            : "Could not place any parts.";
        Repaint();
    }

    bool DoImport()
    {
        EditorPrefs.SetString(PrefOutputFolder, m_OutputFolder);
        var outRoot    = m_OutputFolder.TrimEnd('/');
        var prefabsRoot = $"{outRoot}/Prefabs";

        var created = new List<GameObject>();
        int skipped = 0;
        foreach (var kv in m_Selection)
        {
            if (kv.Value.isLocal) continue;

            int sep       = kv.Key.IndexOf("|", System.StringComparison.Ordinal);
            var guid      = sep >= 0 ? kv.Key.Substring(0, sep) : kv.Key;
            var childPath = kv.Value.childPath;
            var partName  = kv.Value.partName;
            var subFolder = LastFolderSegment(kv.Value.assetPath);

            var partFolder = $"{prefabsRoot}/{subFolder}";
            var prefabPath = $"{partFolder}/{partName}.prefab";

            if (!AssetDatabase.IsValidFolder(prefabsRoot))
                AssetDatabase.CreateFolder(outRoot, "Prefabs");
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
            else skipped++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (created.Count > 0)
        {
            Selection.objects = created.ToArray();
            EditorGUIUtility.PingObject(created[created.Count - 1]);
            EditorPrefs.SetBool(PrefImportedOnce, true);
        }

        m_StatusLine = skipped > 0
            ? $"Imported {created.Count} prefab{(created.Count == 1 ? "" : "s")}; {skipped} failed. Saved to {outRoot}/"
            : $"Imported {created.Count} prefab{(created.Count == 1 ? "" : "s")} to {outRoot}/";

        m_Selection.Clear();
        Repaint();
        return true;
    }

    async void DoBake()
    {
        EditorPrefs.SetString(PrefOutputFolder, m_OutputFolder);
        var outRoot     = m_OutputFolder.TrimEnd('/');
        var prefabsRoot = $"{outRoot}/Prefabs";

        // Snapshot the addressable selections (the dictionary may change while we await).
        var jobs = new List<(string guid, string childPath, string partName, string subFolder)>();
        foreach (var kv in m_Selection)
        {
            if (!kv.Value.isLocal)
            {
                int sep  = kv.Key.IndexOf("|", System.StringComparison.Ordinal);
                var guid = sep >= 0 ? kv.Key.Substring(0, sep) : kv.Key;
                jobs.Add((guid, kv.Value.childPath, kv.Value.partName, LastFolderSegment(kv.Value.assetPath)));
            }
            else if (kv.Value.rowType == RowType.LocalAddressable)
            {
                // Extract the inner addressable GUID from the wrapper prefab
                var wrapper    = AssetDatabase.LoadAssetAtPath<GameObject>(kv.Value.assetPath);
                var loader     = wrapper?.GetComponentInChildren<AddressableLoader>(true);
                var innerGuid  = loader?.assetGUID ?? "";
                if (string.IsNullOrEmpty(innerGuid))
                {
                    Debug.LogWarning($"[ImportGamePartWizard] Could not extract GUID from LocalAddressable '{kv.Value.partName}' — skipping.");
                    continue;
                }
                jobs.Add((innerGuid, kv.Value.childPath, kv.Value.partName, LastFolderSegment(kv.Value.assetPath)));
            }
        }

        if (jobs.Count == 0) { m_StatusLine = "No addressable parts selected to bake."; Repaint(); return; }

        AddressableBaker.ClearCaches();
        AddressableBaker.EnsureFolder(prefabsRoot);

        var created = new List<GameObject>();
        int failed = 0;

        foreach (var job in jobs)
        {
            m_StatusLine = $"Baking {job.partName} …";
            Repaint();

            GameObject source;
            try { source = await AddressableBaker.LoadAddressableAsync(job.guid, job.childPath); }
            catch (System.Exception ex) { Debug.LogError($"[ImportGamePartWizard] Bake load failed for {job.partName}: {ex.Message}"); failed++; continue; }

            if (source == null) { Debug.LogError($"[ImportGamePartWizard] Could not load addressable {job.guid} ({job.partName})"); failed++; continue; }

            // Baked prefab uses a distinct "_Baked" name so it never clobbers the addressable-loader
            // prefab of the same part (which may already be placed in the scene).
            var bakedName    = $"{job.partName}_Baked";
            var partFolder   = $"{prefabsRoot}/{job.subFolder}";
            var assetFolder  = $"{partFolder}/{bakedName}_Assets";
            var prefabPath   = $"{partFolder}/{bakedName}.prefab";
            AddressableBaker.EnsureFolder(partFolder);
            AddressableBaker.EnsureFolder(assetFolder);

            if (File.Exists(Path.GetFullPath(prefabPath)))
                AssetDatabase.DeleteAsset(prefabPath);

            var root = new GameObject(bakedName);
            try
            {
                // Bake the sub-hierarchy. SP_Mat references are collected as GUIDs (can't store the
                // bundle StructurePartAsset directly), to be resolved at load by AddressableComponentLoader.
                var spMatRefs = new List<(Component component, string field, string guid)>();
                AddressableBaker.BakeTree(source, root.transform, assetFolder, spMatRefs);

                if (spMatRefs.Count > 0)
                {
                    var acl = root.AddComponent<AddressableComponentLoader>();
                    acl.componentValues = spMatRefs
                        .Select(r => new AddressableComponentValue { component = r.component, field = r.field, address = r.guid })
                        .ToList();
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ImportGamePartWizard] Bake failed for {job.partName}: {ex}");
                failed++;
                Object.DestroyImmediate(root);
                continue;
            }
            Object.DestroyImmediate(root);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab != null) created.Add(prefab);
            else failed++;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (created.Count > 0)
        {
            Selection.objects = created.ToArray();
            EditorGUIUtility.PingObject(created[created.Count - 1]);
        }

        m_StatusLine = failed > 0
            ? $"Baked {created.Count} prefab(s); {failed} failed. Saved to {prefabsRoot}/"
            : $"Baked {created.Count} prefab(s) to {prefabsRoot}/";
        Repaint();
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
