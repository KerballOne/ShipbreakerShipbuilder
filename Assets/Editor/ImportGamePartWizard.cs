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
    const int    MaxResults        = 200;

    enum SearchMode { PartName, DisplayName, Path, GUID }
    enum SortColumn { DisplayName, PartName, DimX, DimY, DimZ, Volume, Mass }

    const float W_EXP          = 28f;
    const float W_SEL          = 26f;
    const float W_EXP_BTN      = 24f;
    const float W_SEL_BTN      = 22f;
    const float W_CHILD_INDENT = 12f;
    const float W_DIM          = 54f;
    const float W_VOL          = 60f;
    const float W_MASS         = 60f;

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
    int        m_ChildDepth = 1;

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
    static GUIStyle s_LocalBakedRowStyle;
    static GUIStyle s_LocalAddressableRowStyle;

    static string LoadingLabel()
    {
        int frame = (int)(EditorApplication.timeSinceStartup * 3.0) % 3;
        return frame == 0 ? "·" : frame == 1 ? "··" : "···";
    }

    void EnsureStyles()
    {
        if (s_PathStyle != null && s_LocalBakedRowStyle?.normal.background != null) return;

        s_PathStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = false };

        var childTex = new Texture2D(1, 1);
        childTex.SetPixel(0, 0, new Color(0.18f, 0.22f, 0.28f, 1f));
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

        bool hasGameAssets = LoadGameAssets.knownAssetMap != null && LoadGameAssets.knownAssetMap.Count > 0;

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
        if (GUILayout.Button("↺", GUILayout.Width(22))) { LoadEnrichedData(); m_LastSearch = null; }
        EditorGUILayout.EndHorizontal();

        if (m_RegexError != null)
            EditorGUILayout.HelpBox($"Regex: {m_RegexError}", MessageType.Error);

        if (!hasGameAssets)
            EditorGUILayout.HelpBox("No game assets loaded. Run  Shipbreaker → Reload Assets  to include game library parts.", MessageType.Warning);

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

        // ── Column layout ────────────────────────────────────────────────────
        float viewW    = EditorGUIUtility.currentViewWidth - 40f;
        float flexW    = Mathf.Max(120f, viewW - W_EXP - W_SEL - W_DIM * 3 - W_VOL - W_MASS);
        float nameColW = Mathf.Floor(flexW * 0.5f);

        // ── Table header ─────────────────────────────────────────────────────
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
        m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll, GUILayout.ExpandHeight(true));
        foreach (var r in m_Results)
        {
            bool sel        = m_Selection.ContainsKey(r.isLocal ? "local:" + r.path : r.guid);
            bool isExpanded = !r.isLocal && m_Expanded.Contains(r.guid);
            bool isLoading  = !r.isLocal && m_Loading.Contains(r.guid);

            var rowBg = sel
                ? GUI.skin.box
                : r.rowType == RowType.LocalBaked       ? s_LocalBakedRowStyle
                : r.rowType == RowType.LocalAddressable ? s_LocalAddressableRowStyle
                : GUIStyle.none;

            EditorGUILayout.BeginVertical(rowBg);

            // Row 1 — expand toggle + select + data fields
            EditorGUILayout.BeginHorizontal();
            if (!r.isLocal)
            {
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
            }
            else
            {
                GUILayout.Space(W_EXP);
            }

            string selKey = r.isLocal ? "local:" + r.path : r.guid;
            if (GUILayout.Button(sel ? "✓" : " ", EditorStyles.miniButton, GUILayout.Width(W_SEL_BTN)))
            {
                if (sel) m_Selection.Remove(selKey);
                else     m_Selection[selKey] = (r.path, r.partName, "", r.isLocal, r.guid, r.rowType);
            }
            GUILayout.Space(W_SEL - W_SEL_BTN);

            GUILayout.Label(r.partName,       EditorStyles.label, GUILayout.Width(nameColW));
            GUILayout.Label(r.displayName,    EditorStyles.label, GUILayout.Width(nameColW));
            GUILayout.Label(FmtDim(r.dimX),   EditorStyles.label, GUILayout.Width(W_DIM));
            GUILayout.Label(FmtDim(r.dimY),   EditorStyles.label, GUILayout.Width(W_DIM));
            GUILayout.Label(FmtDim(r.dimZ),   EditorStyles.label, GUILayout.Width(W_DIM));
            GUILayout.Label(FmtVol(r.volume), EditorStyles.label, GUILayout.Width(W_VOL));
            GUILayout.Label(FmtMass(r.mass),  EditorStyles.label, GUILayout.Width(W_MASS));
            EditorGUILayout.EndHorizontal();

            // Row 2 — guid + path
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(W_EXP + W_SEL + 4f);
            if (!r.isLocal)
            {
                GUILayout.Label(r.guid, s_PathStyle);
                GUILayout.Space(8f);
                GUILayout.Label(r.path, s_PathStyle);
            }
            else
            {
                GUILayout.Label(r.path, s_PathStyle);
            }
            EditorGUILayout.EndHorizontal();

            // Child rows (addressable parts only)
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
                        if (m_PrefabsOnly && !child.isPrefab) continue;

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
                            else          m_Selection[childKey] = (r.path, child.childPath, child.childPath, false, r.guid, RowType.Addressable);
                        }
                        GUILayout.Space(W_SEL - W_SEL_BTN);
                        GUILayout.Space(indent);
                        GUILayout.Label(partSegment,       EditorStyles.miniLabel, GUILayout.Width(nameColW - indent));
                        GUILayout.Space(indent);
                        GUILayout.Label(child.displayName, EditorStyles.miniLabel, GUILayout.Width(nameColW - indent));
                        GUILayout.Label(FmtDim(child.dimX),   EditorStyles.miniLabel, GUILayout.Width(W_DIM));
                        GUILayout.Label(FmtDim(child.dimY),   EditorStyles.miniLabel, GUILayout.Width(W_DIM));
                        GUILayout.Label(FmtDim(child.dimZ),   EditorStyles.miniLabel, GUILayout.Width(W_DIM));
                        GUILayout.Label(FmtVol(child.volume), EditorStyles.miniLabel, GUILayout.Width(W_VOL));
                        GUILayout.Label(FmtMass(child.mass),  EditorStyles.miniLabel, GUILayout.Width(W_MASS));
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
                "Click ✓ to select a part. Green rows are local project prefabs — use 'Place in Scene'.\nGrey rows are game addressables — use 'Import Selected' to create a loader prefab.",
                MessageType.None);
        }
        else
        {
            foreach (var kv in m_Selection)
            {
                bool isChild = kv.Key.Contains("|");
                var label    = isChild ? $"  └ {kv.Value.partName}" : kv.Value.partName;
                var suffix   = kv.Value.isLocal ? " [local]" : "";

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.SelectableLabel(label + suffix, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (!kv.Value.isLocal && !string.IsNullOrEmpty(kv.Value.guid))
                    EditorGUILayout.SelectableLabel(kv.Value.guid, EditorStyles.miniLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight), GUILayout.Width(240));
                bool canPreview = !isChild && (!kv.Value.isLocal
                    ? !string.IsNullOrEmpty(kv.Value.guid)
                    : !string.IsNullOrEmpty(kv.Value.assetPath));
                if (canPreview && GUILayout.Button("⬡ Preview", EditorStyles.miniButton, GUILayout.Width(70)))
                    OpenPreview(kv.Value.rowType, kv.Value.isLocal ? kv.Value.assetPath : kv.Value.guid);
                EditorGUILayout.EndHorizontal();
            }
        }

        EditorGUILayout.Space();

        // ── Output folder (addressable imports only) ──────────────────────────
        bool hasAddressableSelected = false;
        foreach (var kv in m_Selection)
            if (!kv.Value.isLocal) { hasAddressableSelected = true; break; }

        if (hasAddressableSelected)
        {
            GUILayout.Label("Output", EditorStyles.boldLabel);
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
        }

        // ── Action buttons ────────────────────────────────────────────────────
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        if (m_Selection.Count > 0)
        {
            if (GUILayout.Button("Place in Scene", GUILayout.Height(32), GUILayout.Width(130)))
                DoPlaceLocal();
        }

        if (hasAddressableSelected)
        {
            string importError = ValidateImport();
            GUI.enabled = importError == null;
            var importLabel = m_Selection.Count > 1 ? $"Import {m_Selection.Count} Parts" : "Import Selected";
            if (GUILayout.Button(importLabel, GUILayout.Height(32), GUILayout.Width(150)))
                DoImport();
            GUI.enabled = true;
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

                if (m_Results.Count >= MaxResults) break;
            }
        }

        // ── Local project prefabs ─────────────────────────────────────────────
        var localGuids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_CustomShips" });
        foreach (var g in localGuids)
        {
            var path     = AssetDatabase.GUIDToAssetPath(g);
            var partName = Path.GetFileNameWithoutExtension(path);

            if (!MatchesTerm(term, regex, partName, "", path)) continue;

            var prefab  = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var hasLoader = prefab != null && prefab.GetComponentInChildren<AddressableLoader>(true) != null;
            m_Results.Add(new ResultRow
            {
                guid        = g,
                path        = path,
                partName    = partName,
                displayName = "",
                rowType     = hasLoader ? RowType.LocalAddressable : RowType.LocalBaked,
            });

            if (m_Results.Count >= MaxResults) break;
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
