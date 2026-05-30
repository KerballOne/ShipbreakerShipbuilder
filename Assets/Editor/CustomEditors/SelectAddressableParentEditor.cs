using System.Collections.Generic;
using System.IO;
using BBI.Unity.Game;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SelectAddressableParent))]
[CanEditMultipleObjects]
public class SelectAddressableParentEditor : Editor
{
    static Dictionary<string, EnrichedEntry> s_ByName;

    class EnrichedEntry
    {
        public string displayName, partName;
        public float[] dims;
        public float volume, mass;
    }

    class RawEntry
    {
        [JsonProperty("partName")]    public string  partName;
        [JsonProperty("displayName")] public string  displayName;
        [JsonProperty("dims")]        public float[] dims;
        [JsonProperty("volume")]      public float   volume;
        [JsonProperty("mass")]        public float   mass;
    }

    void OnEnable() => EnsureEnriched();

    static void EnsureEnriched()
    {
        if (s_ByName != null) return;
        s_ByName = new Dictionary<string, EnrichedEntry>(System.StringComparer.OrdinalIgnoreCase);
        var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "known_assets_enriched.json"));
        if (!File.Exists(path)) return;
        try
        {
            var raw = JsonConvert.DeserializeObject<Dictionary<string, RawEntry>>(File.ReadAllText(path));
            foreach (var kv in raw)
            {
                if (string.IsNullOrEmpty(kv.Value?.partName)) continue;
                s_ByName[kv.Value.partName] = new EnrichedEntry
                {
                    displayName = kv.Value.displayName,
                    partName    = kv.Value.partName,
                    dims        = kv.Value.dims,
                    volume      = kv.Value.volume,
                    mass        = kv.Value.mass,
                };
            }
        }
        catch { }
    }

    public override void OnInspectorGUI()
    {
        var go = ((SelectAddressableParent)target).gameObject;

        EditorGUILayout.LabelField("Part Info", EditorStyles.boldLabel);

        EnrichedEntry entry = null;
        bool found = s_ByName != null && s_ByName.TryGetValue(go.name, out entry);

        if (found)
        {
            EditorGUILayout.LabelField("Display Name", entry.displayName ?? go.name);
            EditorGUILayout.LabelField("Part Name",    entry.partName);
            if (entry.dims != null && entry.dims.Length >= 3)
                EditorGUILayout.LabelField("Dims (m)",
                    $"X {entry.dims[0]:F2}  Y {entry.dims[1]:F2}  Z {entry.dims[2]:F2}");
            EditorGUILayout.LabelField("Volume", $"{entry.volume:F4} m³");
            EditorGUILayout.LabelField("Mass",   $"{entry.mass:F2} kg");
        }
        else
        {
            EditorGUILayout.LabelField("Name", go.name);
            EditorGUILayout.HelpBox("No enriched data found for this part.", MessageType.None);
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Refresh Enriched Data"))
        {
            s_ByName = null;
            EnsureEnriched();
        }

        EditorGUILayout.Space();

        if (GUILayout.Button("Select Parent AddressableLoader"))
        {
            var loader = go.GetComponentInParent<AddressableLoader>();
            if (loader != null)
                Selection.objects = new Object[] { loader.gameObject };
        }

        EditorGUILayout.Space();

        var parentLoader = go.GetComponentInParent<AddressableLoader>();
        if (parentLoader != null)
        {
            // Collect full paths for all selected SelectAddressableParent objects under the same loader.
            var paths = new List<string>();
            foreach (var t in targets)
            {
                var tgo = ((SelectAddressableParent)t).gameObject;
                var tLoader = tgo.GetComponentInParent<AddressableLoader>();
                if (tLoader == parentLoader)
                    paths.Add(GetPathRelativeToLoader(tgo, tLoader));
            }

            bool allPersisted = paths.Count > 0 && paths.TrueForAll(p =>
                parentLoader.disabledChildren != null && parentLoader.disabledChildren.Contains(p));
            bool anyPersisted = paths.Exists(p =>
                parentLoader.disabledChildren != null && parentLoader.disabledChildren.Contains(p));

            if (targets.Length == 1 && !go.activeSelf && !allPersisted)
                EditorGUILayout.HelpBox("This object is inactive. Persist it so it survives view refresh.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(allPersisted))
            {
                if (GUILayout.Button(allPersisted ? "Already in Disabled Children" : $"Persist Disabled ({paths.Count}) to AddressableLoader"))
                {
                    Undo.RecordObject(parentLoader, "Persist Disabled Child");
                    if (parentLoader.disabledChildren == null)
                        parentLoader.disabledChildren = new List<string>();
                    foreach (var p in paths)
                        if (!parentLoader.disabledChildren.Contains(p))
                            parentLoader.disabledChildren.Add(p);
                    EditorUtility.SetDirty(parentLoader);
                }
            }

            if (anyPersisted)
            {
                if (GUILayout.Button($"Remove from Disabled Children ({paths.Count})"))
                {
                    Undo.RecordObject(parentLoader, "Remove Disabled Child");
                    foreach (var p in paths)
                        parentLoader.disabledChildren.Remove(p);
                    EditorUtility.SetDirty(parentLoader);
                }
            }
        }
    }

    static string GetPathRelativeToLoader(GameObject go, AddressableLoader loader)
    {
        // Walk up to the loader, collecting name segments.
        // The immediate child of the loader is the fake root (named after the GUID/assetPath)
        // and is NOT included in the path because CollectChildrenByPath is called on that root.
        var parts = new List<string>();
        var t = go.transform;
        while (t != null && t.parent != null && t.parent.gameObject != loader.gameObject)
        {
            parts.Insert(0, t.name);
            t = t.parent;
        }
        return string.Join("/", parts);
    }
}
