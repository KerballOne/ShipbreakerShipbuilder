using BBI.Unity.Game;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

public class AddressableLightConfigWindow : EditorWindow
{
    // ── Room type table ──────────────────────────────────────────────────────

    private static readonly string[] kRoomTypeNames = new[]
    {
        "Random", "Cabin", "Cockpit", "CrawlSpace",
        "Crew", "Exterior", "Hallway", "Laboratory", "Reactor", "Thruster"
    };
    private static readonly string[] kRoomTypeGuids = new[]
    {
        "", "f0a77774d6b016c47b20edaf131513eb", "90f3deeb4b0f55d49ad310f31a88ac48",
        "2f3bc468c54fe4b40957bba0c4a30554", "333310621e1631b4ca424782f0249391",
        "e3ee9f9259c6b9642a83d64db6854c21", "3128a1a8f55929d4499b54bf22ba4977",
        "9013ed4567b0fb342ae050e67e5c5676", "a6cf17f810c6ce040a55c09aa4724347",
        "9d740d2b85c06344c98176c5538b5b2b"
    };

    // ── Color palette table (DynamicLightColorAsset name × RoomType name → light Color)
    // Sourced from runtime DumpLightColorAssets log. Missing entry = purple default.
    // null entry = purple default (RoomType not in asset's ColorMap).
    private static readonly Color kPurple = new Color(0.995f, 0.363f, 1.000f);

    private static readonly Dictionary<string, Dictionary<string, Color>> kColorTable =
        new Dictionary<string, Dictionary<string, Color>>
    {
        { "Industrial_DynamicLightColorAsset", new Dictionary<string, Color> {
            { "Cabin",      new Color(1.00f, 0.76f, 0.51f) },
            { "Cockpit",    new Color(1.00f, 0.76f, 0.51f) },
            { "CrawlSpace", new Color(1.00f, 0.76f, 0.51f) },
            { "Crew",       new Color(1.00f, 0.76f, 0.51f) },
            { "Hallway",    new Color(1.00f, 0.76f, 0.51f) },
            { "Laboratory", new Color(1.00f, 0.76f, 0.51f) },
            { "Reactor",    new Color(1.00f, 0.76f, 0.51f) },
            { "Thruster",   new Color(1.00f, 0.76f, 0.51f) },
        }},
        { "Commercial_DynamicLightColorAsset", new Dictionary<string, Color> {
            { "Cabin",      new Color(0.53f, 0.67f, 1.00f) },
            { "Cockpit",    new Color(0.53f, 0.67f, 1.00f) },
            { "CrawlSpace", new Color(0.55f, 0.38f, 0.27f) },
            { "Crew",       new Color(0.53f, 0.67f, 1.00f) },
            { "Hallway",    new Color(0.53f, 0.67f, 1.00f) },
            { "Laboratory", new Color(0.53f, 0.67f, 1.00f) },
            { "Reactor",    new Color(0.00f, 0.38f, 1.00f) },
            { "Thruster",   new Color(1.00f, 0.00f, 0.00f) },
        }},
        { "Cool_DynamicLightColorAsset", new Dictionary<string, Color> {
            { "Cabin",      new Color(0.95f, 0.97f, 0.72f) },
            { "Cockpit",    new Color(0.59f, 0.97f, 0.83f) },
            { "CrawlSpace", new Color(0.55f, 0.38f, 0.27f) },
            { "Crew",       new Color(0.95f, 0.97f, 0.72f) },
            { "Exterior",   new Color(0.70f, 0.90f, 0.92f) },
            { "Hallway",    new Color(0.69f, 0.95f, 0.92f) },
            { "Laboratory", new Color(0.95f, 0.97f, 0.72f) },
            { "Reactor",    new Color(0.00f, 0.38f, 1.00f) },
            { "Thruster",   new Color(1.00f, 0.76f, 0.51f) },
        }},
        { "Science_DynamicLightColorAsset", new Dictionary<string, Color> {
            { "Cabin",      new Color(0.45f, 0.86f, 1.00f) },
            { "Cockpit",    new Color(0.45f, 0.86f, 1.00f) },
            { "CrawlSpace", new Color(0.55f, 0.38f, 0.27f) },
            { "Crew",       new Color(0.45f, 0.86f, 1.00f) },
            { "Hallway",    new Color(0.45f, 0.86f, 1.00f) },
            { "Laboratory", new Color(0.45f, 0.86f, 1.00f) },
            { "Reactor",    new Color(0.00f, 0.38f, 1.00f) },
            { "Thruster",   new Color(1.00f, 0.76f, 0.51f) },
        }},
        { "GhostShip_DynamicLightColorAsset", new Dictionary<string, Color> {
            { "Cabin",      Color.black },
            { "Cockpit",    new Color(0.10f, 0.43f, 0.90f) },
            { "CrawlSpace", new Color(0.10f, 0.43f, 0.90f) },
            { "Crew",       new Color(0.10f, 0.43f, 0.90f) },
            { "Hallway",    Color.black },
            { "Laboratory", new Color(0.10f, 0.43f, 0.90f) },
            { "Reactor",    new Color(0.10f, 0.43f, 0.90f) },
            { "Thruster",   new Color(0.10f, 0.43f, 0.90f) },
        }},
    };

    // ── Prefab cache: assetGUID → loaded GameObject ──────────────────────────

    private readonly Dictionary<string, GameObject> _prefabCache  = new Dictionary<string, GameObject>();
    private readonly HashSet<string>                 _loadingGuids = new HashSet<string>();

    // ── State ────────────────────────────────────────────────────────────────

    private List<AddressableLoader> _targets = new List<AddressableLoader>();
    private Vector2 _scroll;

    private int   _batchRoomIdx = 0;
    private float _batchDamaged = 0.2f;
    private float _batchBroken  = 0.1f;

    [MenuItem("Shipbuilder/Addressable Light Config")]
    public static void Open()
    {
        var window = GetWindow<AddressableLightConfigWindow>("Light Config");
        window.minSize = new Vector2(440, 320);
        window.Refresh();
    }

    private void OnSelectionChange() => Refresh();
    private void OnFocus()           => Refresh();

    private void Refresh()
    {
        _targets.Clear();
        foreach (var go in Selection.gameObjects)
        {
            foreach (var loader in go.GetComponentsInChildren<AddressableLoader>(true))
            {
                if (!_targets.Contains(loader))
                    _targets.Add(loader);
            }
        }
        // Kick off prefab loads for any new targets
        foreach (var loader in _targets)
            EnsurePrefabLoaded(loader.assetGUID);
        Repaint();
    }

    private void EnsurePrefabLoaded(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return;
        if (_prefabCache.ContainsKey(guid) || _loadingGuids.Contains(guid)) return;
        _loadingGuids.Add(guid);

        var locOp = Addressables.LoadResourceLocationsAsync(guid, typeof(GameObject));
        locOp.Completed += locRes =>
        {
            if (locRes.Status != AsyncOperationStatus.Succeeded
                || locRes.Result == null || locRes.Result.Count == 0)
            {
                _loadingGuids.Remove(guid);
                return;
            }
            Addressables.LoadAssetAsync<GameObject>(locRes.Result[0]).Completed += res =>
            {
                _loadingGuids.Remove(guid);
                if (res.Result != null)
                {
                    _prefabCache[guid] = res.Result;
                    Repaint();
                }
            };
        };
    }

    // Returns the DynamicLightColorAsset name for a loader's prefab, or null if not loaded yet.
    private string GetColorAssetName(AddressableLoader loader)
    {
        if (!_prefabCache.TryGetValue(loader.assetGUID, out var prefab) || prefab == null)
            return null;
        var dl = prefab.GetComponentInChildren<DynamicLight>(true);
        if (dl == null) return null;
        var asset = dl.DynamicColorAsset;
        return asset != null ? asset.name : null;
    }

    private static Color GetExpectedColor(string colorAssetName, string roomTypeName)
    {
        if (string.IsNullOrEmpty(colorAssetName) || string.IsNullOrEmpty(roomTypeName))
            return kPurple;
        if (kColorTable.TryGetValue(colorAssetName, out var roomMap)
            && roomMap.TryGetValue(roomTypeName, out var col))
            return col;
        return kPurple;
    }

    private static int GuidToIndex(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return 0;
        for (int i = 1; i < kRoomTypeGuids.Length; i++)
            if (kRoomTypeGuids[i] == guid) return i;
        return 0;
    }

    private static bool DrawChanceSliders(ref float damaged, ref float broken)
    {
        EditorGUI.BeginChangeCheck();
        float newDamaged = EditorGUILayout.Slider("Damaged chance", damaged, 0f, 1f);
        float maxBroken  = Mathf.Max(0f, 1f - newDamaged);
        float newBroken  = EditorGUILayout.Slider("Broken chance", Mathf.Min(broken, maxBroken), 0f, maxBroken);
        float normal     = Mathf.Max(0f, 1f - newDamaged - newBroken);
        EditorGUILayout.LabelField("Normal (implied)", $"{normal * 100f:F0}%");
        bool changed = EditorGUI.EndChangeCheck();
        if (changed) { damaged = newDamaged; broken = newBroken; }
        return changed;
    }

    private static void DrawColorPreview(Color col, string label)
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel(label);

        // Swatch
        var swatchRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, GUILayout.Width(40));
        // Draw a dark border then the color
        EditorGUI.DrawRect(swatchRect, Color.black);
        var inner = new Rect(swatchRect.x + 1, swatchRect.y + 1, swatchRect.width - 2, swatchRect.height - 2);
        // Gamma-correct for display: the stored values are linear HDR
        EditorGUI.DrawRect(inner, col.gamma);

        // Hex label
        Color32 c32 = (Color32)col.gamma;
        EditorGUILayout.LabelField($"#{c32.r:X2}{c32.g:X2}{c32.b:X2}  ({col.r:F2}, {col.g:F2}, {col.b:F2})",
            EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(4);

        if (_targets.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "Select one or more GameObjects in the Hierarchy that contain AddressableLoader components.",
                MessageType.Info);
            return;
        }

        // ── Batch apply ──────────────────────────────────────────────────────
        EditorGUILayout.LabelField($"Batch Apply to {_targets.Count} fixture(s)", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        _batchRoomIdx = EditorGUILayout.Popup("Room Type", _batchRoomIdx, kRoomTypeNames);
        DrawChanceSliders(ref _batchDamaged, ref _batchBroken);
        EditorGUILayout.Space(2);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("Apply Room Type to All"))  ApplyToAll(roomType: true,  chances: false);
        if (GUILayout.Button("Apply Chances to All"))    ApplyToAll(roomType: false, chances: true);
        EditorGUILayout.EndHorizontal();
        if (GUILayout.Button("Apply All to All"))        ApplyToAll(roomType: true,  chances: true);
        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(6);

        // ── Per-fixture list ─────────────────────────────────────────────────
        EditorGUILayout.LabelField("Individual Fixtures", EditorStyles.boldLabel);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        foreach (var loader in _targets)
        {
            if (loader == null) continue;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.ObjectField(loader.gameObject.name, loader, typeof(AddressableLoader), true);

            EditorGUI.BeginChangeCheck();
            int   newRoomIdx = EditorGUILayout.Popup("Room Type", GuidToIndex(loader.lightRoomTypeGUID), kRoomTypeNames);
            float damaged    = loader.lightDamagedChance;
            float broken     = loader.lightBrokenChance;
            bool chancesChanged = DrawChanceSliders(ref damaged, ref broken);
            bool anyChanged     = EditorGUI.EndChangeCheck() || chancesChanged;

            if (anyChanged)
            {
                Undo.RecordObject(loader, "Set Light Config");
                loader.lightRoomTypeGUID  = kRoomTypeGuids[newRoomIdx];
                loader.lightDamagedChance = damaged;
                loader.lightBrokenChance  = broken;
                EditorUtility.SetDirty(loader);
            }

            // Color preview
            string colorAssetName = GetColorAssetName(loader);
            if (colorAssetName == null)
            {
                string status = _loadingGuids.Contains(loader.assetGUID) ? "loading…" : "prefab not loaded";
                EditorGUILayout.LabelField("Expected color", status, EditorStyles.miniLabel);
            }
            else
            {
                string roomName = newRoomIdx > 0 ? kRoomTypeNames[newRoomIdx] : null;
                if (roomName == null)
                {
                    EditorGUILayout.LabelField("Expected color", "Random room type — varies at runtime", EditorStyles.miniLabel);
                }
                else
                {
                    Color col = GetExpectedColor(colorAssetName, roomName);
                    bool isPurple = col == kPurple;
                    DrawColorPreview(col, "Expected color");
                    if (isPurple)
                        EditorGUILayout.HelpBox($"No entry for {roomName} in {colorAssetName} — will show purple default.", MessageType.Warning);
                }
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(2);
        }

        EditorGUILayout.EndScrollView();
    }

    private void ApplyToAll(bool roomType, bool chances)
    {
        foreach (var loader in _targets)
        {
            if (loader == null) continue;
            Undo.RecordObject(loader, "Batch Set Light Config");
            if (roomType) loader.lightRoomTypeGUID  = kRoomTypeGuids[_batchRoomIdx];
            if (chances)  { loader.lightDamagedChance = _batchDamaged; loader.lightBrokenChance = _batchBroken; }
            EditorUtility.SetDirty(loader);
        }
    }
}
