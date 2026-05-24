using System.IO;
using BBI.Unity.Game;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

public class CustomPartWizard : EditorWindow
{
    const string ShellConnectorPath = "Assets/_CustomShips/FirstShip/Components/Shell/ShellConnector.prefab";

    string m_PartName = "MyPart";
    string m_OutputFolder = "Assets/_CustomShips/";
    Mesh m_Mesh;
    Material m_Material;
    Texture2D m_BaseColorMap;
    Texture2D m_NormalMap;
    Texture2D m_MaskMap;
    string m_AddressableGroup = "";
    bool m_KeepOpening = false;
    string m_DisplayName = "";
    SalvageableComponentAsset m_SalvageableOverride;

    Vector2 m_Scroll;

    [MenuItem("Shipbuilder/Create Custom Part Wizard")]
    static void Open() => GetWindow<CustomPartWizard>("Custom Part Wizard");

    void OnGUI()
    {
        m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

        GUILayout.Label("Create Custom Ship Part", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Creates a new part prefab based on ShellConnector with all required game components pre-wired. " +
            "Assign your mesh and material, then add the resulting prefab as a child of your ship root prefab.",
            MessageType.Info);

        EditorGUILayout.Space();
        GUILayout.Label("Part Settings", EditorStyles.boldLabel);

        m_PartName = EditorGUILayout.TextField("Part Name", m_PartName);

        EditorGUILayout.BeginHorizontal();
        m_OutputFolder = EditorGUILayout.TextField("Output Folder", m_OutputFolder);
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            var picked = EditorUtility.OpenFolderPanel("Select Output Folder", Application.dataPath, "");
            if (!string.IsNullOrEmpty(picked) && picked.StartsWith(Application.dataPath))
                m_OutputFolder = "Assets" + picked.Substring(Application.dataPath.Length).Replace('\\', '/');
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        GUILayout.Label("Mesh & Material", EditorStyles.boldLabel);

        m_Mesh = (Mesh)EditorGUILayout.ObjectField("Mesh", m_Mesh, typeof(Mesh), false);
        if (m_Mesh != null)
        {
            var meshPath = AssetDatabase.GetAssetPath(m_Mesh);
            var importer = AssetImporter.GetAtPath(meshPath) as ModelImporter;
            if (importer != null && !importer.isReadable)
                EditorGUILayout.HelpBox("Read/Write is disabled on this mesh — it will be enabled automatically on Create.", MessageType.Warning);
        }

        m_Material = (Material)EditorGUILayout.ObjectField("Material", m_Material, typeof(Material), false);

        EditorGUILayout.Space();
        GUILayout.Label("Textures (Optional)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Assigned to the selected material using HDRP slot names. Requires a material to be set above.", MessageType.None);

        m_BaseColorMap = (Texture2D)EditorGUILayout.ObjectField("Base Color (_BaseColorMap)", m_BaseColorMap, typeof(Texture2D), false);
        m_NormalMap    = (Texture2D)EditorGUILayout.ObjectField("Normal Map  (_NormalMap)",   m_NormalMap,    typeof(Texture2D), false);
        m_MaskMap      = (Texture2D)EditorGUILayout.ObjectField("Mask Map    (_MaskMap)",     m_MaskMap,      typeof(Texture2D), false);
        EditorGUILayout.HelpBox("Mask Map: R=Metallic  G=AO  B=Detail  A=Smoothness", MessageType.None);

        EditorGUILayout.Space();
        GUILayout.Label("Game Properties (Optional)", EditorStyles.boldLabel);

        m_DisplayName = EditorGUILayout.TextField(
            new GUIContent("Display Name",
                "Name shown in the scanner HUD and salvage ledger. Creates OI_<PartName>.asset automatically. Leave blank to inherit from the template."),
            m_DisplayName);

        if (!string.IsNullOrWhiteSpace(m_DisplayName) && string.IsNullOrWhiteSpace(m_AddressableGroup))
            EditorGUILayout.HelpBox("Set an Addressable Group so the ObjectInfoAsset is included in the mod bundle.", MessageType.Warning);

        m_SalvageableOverride = (SalvageableComponentAsset)EditorGUILayout.ObjectField(
            new GUIContent("Salvageable Asset",
                "Assigns a custom salvage value to this part. Creates SP_<PartName>.asset in the output folder and wires it into the part. Requires an Addressable Group."),
            m_SalvageableOverride, typeof(SalvageableComponentAsset), false);

        if (m_SalvageableOverride != null)
            EditorGUILayout.HelpBox(
                $"SP_{m_PartName}.asset will be created with only the Salvageable field set. " +
                "Open it afterwards and assign IRigidbodyAsset (and other fields) to match the part material you want.",
                MessageType.Warning);

        EditorGUILayout.Space();
        GUILayout.Label("Addressables (Optional)", EditorStyles.boldLabel);
        m_AddressableGroup = EditorGUILayout.TextField("Group Name", m_AddressableGroup);
        EditorGUILayout.HelpBox("Leave blank to skip Addressable registration. The group must already exist.", MessageType.None);

        EditorGUILayout.Space();
        GUILayout.Label("Advanced", EditorStyles.boldLabel);
        m_KeepOpening = EditorGUILayout.Toggle("Keep 'Opening' child", m_KeepOpening);
        EditorGUILayout.HelpBox(
            "'Opening' marks a pressure/atmosphere boundary on the ShellConnector template. " +
            "Keep it only if this part is an airlock or section connector. Remove it for solid parts like engine bells.",
            MessageType.None);

        EditorGUILayout.Space();

        string error = Validate();
        GUI.enabled = error == null;
        if (GUILayout.Button("Create Part Prefab", GUILayout.Height(32)))
            CreatePart();
        GUI.enabled = true;

        if (error != null)
            EditorGUILayout.HelpBox(error, MessageType.Error);

        EditorGUILayout.EndScrollView();
    }

    string Validate()
    {
        if (string.IsNullOrWhiteSpace(m_PartName))
            return "Part Name is required.";
        if (string.IsNullOrWhiteSpace(m_OutputFolder))
            return "Output Folder is required.";
        if (!AssetDatabase.IsValidFolder(m_OutputFolder.TrimEnd('/')))
            return $"Output folder does not exist: {m_OutputFolder}";
        if (!File.Exists(Path.GetFullPath(ShellConnectorPath)))
            return $"ShellConnector prefab not found at:\n{ShellConnectorPath}";
        if ((m_BaseColorMap != null || m_NormalMap != null || m_MaskMap != null) && m_Material == null)
            return "A material must be selected to assign textures.";
        if (m_SalvageableOverride != null && string.IsNullOrWhiteSpace(m_AddressableGroup))
            return "Addressable Group is required when specifying a Salvageable Asset.";
        return null;
    }

    void CreatePart()
    {
        var outFolder = m_OutputFolder.TrimEnd('/');
        var newPrefabPath = $"{outFolder}/{m_PartName}.prefab";

        if (File.Exists(Path.GetFullPath(newPrefabPath)))
        {
            if (!EditorUtility.DisplayDialog("Overwrite?", $"{newPrefabPath} already exists. Overwrite?", "Yes", "Cancel"))
                return;
            AssetDatabase.DeleteAsset(newPrefabPath);
        }

        // Ensure mesh is readable before the game tries to calculate volume from it
        if (m_Mesh != null)
        {
            var meshPath = AssetDatabase.GetAssetPath(m_Mesh);
            var importer = AssetImporter.GetAtPath(meshPath) as ModelImporter;
            if (importer != null && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }

        // Create assets that need to exist before the prefab scope opens
        string spAddress = null;
        if (m_SalvageableOverride != null)
            spAddress = CreateStructurePartAsset(outFolder);

        ObjectInfoAsset oiAsset = null;
        if (!string.IsNullOrWhiteSpace(m_DisplayName))
            oiAsset = CreateObjectInfoAsset(outFolder);

        AssetDatabase.CopyAsset(ShellConnectorPath, newPrefabPath);
        AssetDatabase.SaveAssets();

        using (var scope = new PrefabUtility.EditPrefabContentsScope(newPrefabPath))
        {
            var root = scope.prefabContentsRoot;
            root.name = m_PartName;

            if (m_Mesh != null)
            {
                var mf = root.GetComponent<MeshFilter>();
                if (mf) mf.sharedMesh = m_Mesh;

                var mc = root.GetComponent<MeshCollider>();
                if (mc) mc.sharedMesh = m_Mesh;
            }

            if (m_Material != null)
            {
                var mr = root.GetComponent<MeshRenderer>();
                if (mr) mr.sharedMaterials = new[] { m_Material };
            }

            if (!m_KeepOpening)
            {
                var opening = root.transform.Find("Opening");
                if (opening != null)
                    DestroyImmediate(opening.gameObject);
            }

            // Assign ObjectInfoAsset to the StructurePart override slot
            if (oiAsset != null)
                SetMonoBehaviourField(root, "m_ObjectInfoAssetOverride", oiAsset);

            // Redirect AddressableSOLoader refs[0] to the new StructurePartAsset
            if (spAddress != null)
            {
                var loader = root.GetComponent<AddressableSOLoader>();
                if (loader != null)
                {
                    var loaderSO = new SerializedObject(loader);
                    var refsProp = loaderSO.FindProperty("refs");
                    if (refsProp != null && refsProp.arraySize > 0)
                    {
                        refsProp.GetArrayElementAtIndex(0).stringValue = spAddress;
                        loaderSO.ApplyModifiedProperties();
                    }
                }
            }
        }

        // Assign textures to the material asset
        if (m_Material != null)
        {
            if (m_BaseColorMap != null) m_Material.SetTexture("_BaseColorMap", m_BaseColorMap);
            if (m_NormalMap    != null) m_Material.SetTexture("_NormalMap",    m_NormalMap);
            if (m_MaskMap      != null) m_Material.SetTexture("_MaskMap",      m_MaskMap);
            EditorUtility.SetDirty(m_Material);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Addressable registration
        if (!string.IsNullOrWhiteSpace(m_AddressableGroup))
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("[CustomPartWizard] Addressable settings not found; skipping Addressable registration.");
            }
            else
            {
                var group = settings.FindGroup(m_AddressableGroup);
                if (group == null)
                {
                    Debug.LogWarning($"[CustomPartWizard] Addressable group '{m_AddressableGroup}' not found; skipping.");
                }
                else
                {
                    var guid = AssetDatabase.AssetPathToGUID(newPrefabPath);
                    var entry = settings.CreateOrMoveEntry(guid, group);
                    entry.address = m_PartName;
                    settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entry, true);

                    // Register ObjectInfoAsset in the same group so it's bundled with the prefab
                    if (oiAsset != null)
                    {
                        var oiPath = AssetDatabase.GetAssetPath(oiAsset);
                        if (!string.IsNullOrEmpty(oiPath))
                        {
                            var oiGuid = AssetDatabase.AssetPathToGUID(oiPath);
                            var oiEntry = settings.CreateOrMoveEntry(oiGuid, group);
                            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, oiEntry, true);
                        }
                    }

                    AssetDatabase.SaveAssets();
                }
            }
        }

        var newPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(newPrefabPath);
        Selection.activeObject = newPrefab;
        EditorGUIUtility.PingObject(newPrefab);

        EditorUtility.DisplayDialog("Done",
            $"Created {m_PartName}.prefab\n\n" +
            "Next steps:\n" +
            "1. Open the prefab and adjust the Transform (position/rotation)\n" +
            "2. Add it as a child of your ship root prefab\n" +
            "3. Use Shipbuilder → Build (or Build and Run) to deploy and test in-game" +
            (m_SalvageableOverride != null ? $"\n\nRemember: open SP_{m_PartName}.asset and assign IRigidbodyAsset (and other data fields) to match the part material." : ""),
            "OK");
    }

    // Creates OI_<PartName>.asset in the output folder with m_ObjectName set to m_DisplayName.
    ObjectInfoAsset CreateObjectInfoAsset(string outFolder)
    {
        var oiPath = $"{outFolder}/OI_{m_PartName}.asset";
        var oiAsset = ScriptableObject.CreateInstance<ObjectInfoAsset>();
        AssetDatabase.CreateAsset(oiAsset, oiPath);

        var oiSO = new SerializedObject(oiAsset);
        oiSO.FindProperty("m_Data.m_ObjectName").stringValue = m_DisplayName;
        oiSO.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();

        return oiAsset;
    }

    // Creates SP_<PartName>.asset in the output folder, sets the salvageable reference,
    // and registers it in Addressables. Returns the Addressable address (= asset GUID).
    string CreateStructurePartAsset(string outFolder)
    {
        var spPath = $"{outFolder}/SP_{m_PartName}.asset";
        var spAsset = ScriptableObject.CreateInstance<StructurePartAsset>();
        AssetDatabase.CreateAsset(spAsset, spPath);

        var spSO = new SerializedObject(spAsset);
        spSO.FindProperty("Data.m_SalvageableAsset").objectReferenceValue = m_SalvageableOverride;
        spSO.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();

        var spGuid = AssetDatabase.AssetPathToGUID(spPath);

        var settings = AddressableAssetSettingsDefaultObject.Settings;
        var group = settings?.FindGroup(m_AddressableGroup);
        if (group != null)
        {
            var spEntry = settings.CreateOrMoveEntry(spGuid, group);
            spEntry.address = spGuid;
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, spEntry, true);
        }
        else
        {
            Debug.LogWarning($"[CustomPartWizard] Group '{m_AddressableGroup}' not found; SP_{m_PartName}.asset not registered in Addressables.");
        }

        return spGuid;
    }

    // Finds the first MonoBehaviour on root that has the named serialized field and sets it.
    static void SetMonoBehaviourField(GameObject root, string fieldName, Object value)
    {
        foreach (var mb in root.GetComponents<MonoBehaviour>())
        {
            var so = new SerializedObject(mb);
            var prop = so.FindProperty(fieldName);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
                so.ApplyModifiedProperties();
                return;
            }
        }
    }
}
