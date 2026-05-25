using System.Collections.Generic;
using System.IO;
using BBI.Unity.Game;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(SelectAddressableParent))]
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
    }
}
