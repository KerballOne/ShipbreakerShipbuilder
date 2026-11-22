using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using BBI.Unity.Game;
using UnityEngine.AddressableAssets;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;

// Assets / Create / Shipbreaker / Create hardpoint asset
//
// Makes the module list a HardPoint component points at: one ModuleEntryDefinition per prefab reference (weighted),
// an optional ModuleEntryEmpty ("spawn nothing"), the ModuleEntryContainer holding them and the ModuleListAsset,
// and puts the list in the default Addressables group.
//
// Optional "only on ships with this property": the game offers an entry only to ships that carry every property listed
// on the entry, and it compares properties by asset GUID. Give the GUID of a game property (for example the ghost ship
// property from known_assets.json) and every prefab entry is tied to it; an Empty entry is added so that ships without
// the property spawn nothing here.
public class Create_Hardpoint : EditorWindow
{
    [MenuItem("Assets/Create/Shipbreaker/Create hardpoint asset")]
    public static void CreateHardpointsset()
    {
        EditorWindow.CreateInstance<Create_Hardpoint>().Show();
    }

    string hardpointName;
    HardpointAssetReference[] assetReferences = new HardpointAssetReference[0];
    bool assetReferencesOpen;
    float weightEmpty;
    string requiredPropertyGuid = "";

    void OnGUI()
    {
        {
            GUILayout.Label("Create hardpoint asset", EditorStyles.boldLabel);

            GUILayout.Label("Hardpoint name", EditorStyles.label);
            hardpointName = GUILayout.TextField(hardpointName);

            assetReferences = HardpointAssetReferenceArrayField("Asset references", ref assetReferencesOpen, assetReferences);

            GUILayout.Label("Spawn empty weight", EditorStyles.label);
            weightEmpty = EditorGUILayout.FloatField(weightEmpty);

            GUILayout.Label("Only on ships with this property (game asset GUID, optional)", EditorStyles.label);
            requiredPropertyGuid = GUILayout.TextField(requiredPropertyGuid);
            EditorGUILayout.HelpBox("Leave empty for a list that works on every ship. With a GUID, the prefab entries spawn only on ships " +
                "that have that property, and an Empty entry is added for all other ships.", MessageType.None);

            if (GUILayout.Button($"Create {hardpointName} hardpoint") && !string.IsNullOrWhiteSpace(hardpointName))
            {
                string folderPath = AssetDatabase.GetAssetPath(Selection.activeInstanceID);
                if (folderPath.Contains("."))
                    folderPath = folderPath.Remove(folderPath.LastIndexOf('/'));

                var createdGUID = CreateHardpointAsset(folderPath, hardpointName, assetReferences, weightEmpty, requiredPropertyGuid);
                Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(createdGUID));
                EditorUtility.FocusProjectWindow();
            }
        }
    }

    public static string CreateHardpointAsset(string folderPath, string hardpointName, HardpointAssetReference[] assetReferences, float weightEmpty)
    {
        return CreateHardpointAsset(folderPath, hardpointName, assetReferences, weightEmpty, null);
    }

    public static string CreateHardpointAsset(string folderPath, string hardpointName, HardpointAssetReference[] assetReferences, float weightEmpty, string requiredPropertyGuid)
    {
        requiredPropertyGuid = (requiredPropertyGuid ?? "").Trim();
        ModulePropertyAsset required = null;
        if (requiredPropertyGuid.Length > 0)
        {
            required = GetOrCreatePropertyReference(folderPath, requiredPropertyGuid);
            if (weightEmpty <= 0) weightEmpty = 1;   // ships without the property need something to pick
        }

        var moduleEntryContainer = CreateInstance<ModuleEntryContainer>();

        for (int i = 0; i < assetReferences.Length; i++)
        {
            var assetRef = assetReferences[i];
            var moduleEntryDefinition = CreateInstance<ModuleEntryDefinition>();
            moduleEntryDefinition.ModuleDefRef = new AssetReferenceGameObject(assetRef.assetRef);
            moduleEntryDefinition.Weight = assetRef.weight;
            if (required != null) moduleEntryDefinition.Properties = new[] { required };

            // one file per entry: a single entry keeps the old name, several get a number
            string suffix = assetReferences.Length > 1 ? "_" + (i + 1) : "";
            AssetDatabase.CreateAsset(moduleEntryDefinition, $"{folderPath}/{hardpointName}_ModuleEntryDefinition{suffix}.asset");

            moduleEntryContainer.Add(moduleEntryDefinition);
        }

        if (weightEmpty > 0)
        {
            var moduleEntryEmpty = CreateInstance<ModuleEntryEmpty>();
            moduleEntryEmpty.Weight = weightEmpty;

            AssetDatabase.CreateAsset(moduleEntryEmpty, $"{folderPath}/{hardpointName}_ModuleEntryEmpty.asset");

            moduleEntryContainer.Add(moduleEntryEmpty);
        }

        AssetDatabase.CreateAsset(moduleEntryContainer, $"{folderPath}/{hardpointName}_ModuleEntryContainer.asset");

        var moduleListAsset = CreateInstance<ModuleListAsset>();
        moduleListAsset.Data.ModuleEntryContainer = moduleEntryContainer;

        AssetDatabase.CreateAsset(moduleListAsset, $"{folderPath}/{hardpointName}_ModuleListAsset.asset");

        var moduleListAssetGuid = AssetDatabase.GUIDFromAssetPath($"{folderPath}/{hardpointName}_ModuleListAsset.asset").ToString();

        // Addressables
        var addressableSettings = AddressableAssetSettingsDefaultObject.Settings;
        var entriesAdded = new List<AddressableAssetEntry>();

        entriesAdded.Add(addressableSettings.CreateOrMoveEntry(moduleListAssetGuid, addressableSettings.DefaultGroup));

        addressableSettings.DefaultGroup.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entriesAdded, false, true);
        addressableSettings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entriesAdded, true, false);

        AssetDatabase.SaveAssets();

        return moduleListAssetGuid;
    }

    // A local stand-in for a GAME property asset. The game matches properties by asset GUID, so the stand-in must have
    // the game asset's GUID as its own GUID (a hand-written GUID field would be overwritten by Unity). Reused if the
    // project already has an asset with that GUID.
    public static ModulePropertyAsset GetOrCreatePropertyReference(string folderPath, string gameGuid)
    {
        var existing = AssetDatabase.GUIDToAssetPath(gameGuid);
        if (!string.IsNullOrEmpty(existing))
        {
            var found = AssetDatabase.LoadAssetAtPath<ModulePropertyAsset>(existing);
            if (found == null) throw new System.Exception($"GUID {gameGuid} is already used by {existing}, which is not a ModulePropertyAsset.");
            return found;
        }

        string path = $"{folderPath}/ShipProperty_{gameGuid.Substring(0, 8)}_Reference.asset";
        var asset = CreateInstance<ModulePropertyAsset>();
        AssetDatabase.CreateAsset(asset, path);
        // priority 1: on a ship that has the property, entries carrying it win over entries without it
        var so = new SerializedObject(asset);
        var prio = so.FindProperty("Data.m_Priority");
        if (prio != null) { prio.intValue = 1; so.ApplyModifiedPropertiesWithoutUndo(); }
        AssetDatabase.SaveAssets();

        string meta = path + ".meta";
        var text = File.ReadAllText(meta);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^guid: \w+", "guid: " + gameGuid);
        File.WriteAllText(meta, text);
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        var created = AssetDatabase.LoadAssetAtPath<ModulePropertyAsset>(path);
        if (created == null || AssetDatabase.AssetPathToGUID(path) != gameGuid)
            throw new System.Exception($"Could not give {path} the GUID {gameGuid}.");
        // the field the game actually compares; equal to the asset's own GUID now, so nothing will rewrite it differently
        var so2 = new SerializedObject(created);
        var idField = so2.FindProperty("m_AssetGUID");
        if (idField != null && idField.stringValue != gameGuid)
        {
            idField.stringValue = gameGuid;
            so2.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(created);
            AssetDatabase.SaveAssets();
        }
        return created;
    }

    public static HardpointAssetReference[] HardpointAssetReferenceArrayField(string label, ref bool open, HardpointAssetReference[] array) {
        open = EditorGUILayout.Foldout(open, label);
        int newSize = array.Length;

        if (open) {
            newSize = EditorGUILayout.IntField("Size", newSize);
            newSize = newSize < 0 ? 0 : newSize;

            if (newSize != array.Length) {
                array = ResizeArray<HardpointAssetReference>(array, newSize);
            }

            for (var i = 0; i < newSize; i++) {
                EditorGUILayout.BeginHorizontal();
                array[i].assetRef = EditorGUILayout.TextField("Reference: ", array[i].assetRef, GUILayout.ExpandWidth(true));
                array[i].weight = EditorGUILayout.FloatField("Weight: ", array[i].weight, GUILayout.ExpandWidth(false));
                EditorGUILayout.EndHorizontal();
            }
        }
        return array;
    }

    private static T[] ResizeArray<T>(T[] array, int size) where T : new()
    {
        T[] newArray = new T[size];

        for (var i = 0; i < size; i++) {
            if (i < array.Length) {
                newArray[i] = array[i];
            }
            else
            {
                newArray[i] = new T();
            }
        }

        return newArray;
    }

    [System.Serializable]
    public class HardpointAssetReference
    {
        public string assetRef;
        public float weight;
    }
}
