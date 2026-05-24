using System.IO;
using BBI.Unity.Game;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

public class CustomPartWizard : EditorWindow
{
    const string PrefOutputFolder = "CustomPartWizard.OutputFolder";

    // Labels must not contain '/' (Unity treats it as a submenu separator).
    // Format: Material  density  SP_Mat origin  Destination
    static readonly string[] MatTemplateLabels = {
        // Processor
        "Nanocarbon  50 kg|m3  Panel Ext  Processor",
        // Furnace
        "Aluminum  50 kg|m3  Chassis Aluminum  Furnace",
        "Glass  50 kg|m3  Glass Panel  Furnace",
        "Steel  200 kg|m3  Chassis Int  Furnace",
        // Barge
        "Nanocarbon  50 kg|m3  Panel Ext  Barge",
        "Steel  200 kg|m3  Chassis Int  Barge",
        "Reactor Core  200 kg|m3  Reactor  Barge",
        "Thruster Nozzle  50 kg|m3  Class X  Barge",
        "Quasar Thruster  50 kg|m3  Class X Engine  Barge",
    };
    static readonly string[] MatTemplatePaths = {
        // Processor
        "Assets/_CustomShips/FirstShip/Components/Shell/ShellConnector.prefab",
        // Furnace
        "Assets/_CustomShips/_Common/Templates/AluminumConnector.prefab",
        "Assets/_CustomShips/_Common/Templates/GlassConnector.prefab",
        "Assets/_CustomShips/_Common/Templates/ChassisConnector.prefab",
        // Barge
        "Assets/_CustomShips/_Common/Templates/BargeConnectorLight.prefab",
        "Assets/_CustomShips/_Common/Templates/BargeConnectorSteel.prefab",
        "Assets/_CustomShips/_Common/Templates/BargeConnector.prefab",
        "Assets/_CustomShips/_Common/Templates/ThrusterConnectorX.prefab",
        "Assets/_CustomShips/_Common/Templates/QuasarThrusterConnector.prefab",
    };

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
    int m_MatTemplate = 0;

    Vector2 m_Scroll;

    [MenuItem("Shipbuilder/Create Custom Part Wizard")]
    static void Open()
    {
        var w = GetWindow<CustomPartWizard>("Custom Part Wizard");
        w.m_OutputFolder = EditorPrefs.GetString(PrefOutputFolder, "Assets/_CustomShips/");
    }

    void OnGUI()
    {
        m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

        GUILayout.Label("Create Custom Ship Part", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Creates a new part prefab with all required game components pre-wired. " +
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

        EditorGUILayout.Space();
        GUILayout.Label("SP Material", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Determines density, salvage destination, cut level, and payout. " +
            "Reactor Core routes to Barge and explodes when cut. " +
            "Thruster variants also route to Barge and satisfy the reactor coolant pipe mechanic. " +
            "Aluminum density is unconfirmed — load an aluminum-paneled ship to verify.",
            MessageType.None);
        m_MatTemplate = EditorGUILayout.Popup(
            new GUIContent("SP Material", "Game StructurePart material to inherit."),
            m_MatTemplate, MatTemplateLabels);

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
        var templatePath = MatTemplatePaths[m_MatTemplate];
        if (!File.Exists(Path.GetFullPath(templatePath)))
            return $"Template prefab not found at:\n{templatePath}";
        if ((m_BaseColorMap != null || m_NormalMap != null || m_MaskMap != null) && m_Material == null)
            return "A material must be selected to assign textures.";
        return null;
    }

    void CreatePart()
    {
        EditorPrefs.SetString(PrefOutputFolder, m_OutputFolder);

        var outFolder = m_OutputFolder.TrimEnd('/');
        var newPrefabPath = $"{outFolder}/{m_PartName}.prefab";

        if (File.Exists(Path.GetFullPath(newPrefabPath)))
        {
            if (!EditorUtility.DisplayDialog("Overwrite?", $"{newPrefabPath} already exists. Overwrite?", "Yes", "Cancel"))
                return;
            AssetDatabase.DeleteAsset(newPrefabPath);
        }

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

        ObjectInfoAsset oiAsset = null;
        if (!string.IsNullOrWhiteSpace(m_DisplayName))
            oiAsset = CreateObjectInfoAsset(outFolder);

        AssetDatabase.CopyAsset(MatTemplatePaths[m_MatTemplate], newPrefabPath);
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

            if (oiAsset != null)
                SetMonoBehaviourField(root, "m_ObjectInfoAssetOverride", oiAsset);
        }

        if (m_Material != null)
        {
            if (m_BaseColorMap != null) m_Material.SetTexture("_BaseColorMap", m_BaseColorMap);
            if (m_NormalMap    != null) m_Material.SetTexture("_NormalMap",    m_NormalMap);
            if (m_MaskMap      != null) m_Material.SetTexture("_MaskMap",      m_MaskMap);
            EditorUtility.SetDirty(m_Material);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string resolvedGroup = m_AddressableGroup;
        if (string.IsNullOrWhiteSpace(resolvedGroup))
            resolvedGroup = GuessAddressableGroup(outFolder);

        if (!string.IsNullOrWhiteSpace(resolvedGroup))
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogWarning("[CustomPartWizard] Addressable settings not found; skipping Addressable registration.");
            }
            else
            {
                var group = settings.FindGroup(resolvedGroup);
                if (group == null)
                {
                    Debug.LogWarning($"[CustomPartWizard] Addressable group '{resolvedGroup}' not found; skipping.");
                }
                else
                {
                    var guid = AssetDatabase.AssetPathToGUID(newPrefabPath);
                    var entry = settings.CreateOrMoveEntry(guid, group);
                    entry.address = m_PartName;
                    settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entry, true);

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
            "3. Use Shipbuilder → Build (or Build and Run) to deploy and test in-game",
            "OK");
    }

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

    static string GuessAddressableGroup(string outFolder)
    {
        const string marker = "_CustomShips/";
        var idx = outFolder.IndexOf(marker, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var after = outFolder.Substring(idx + marker.Length);
        var slash = after.IndexOf('/');
        var shipName = slash >= 0 ? after.Substring(0, slash) : after;
        if (string.IsNullOrEmpty(shipName)) return null;
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        return (settings != null && settings.FindGroup(shipName) != null) ? shipName : null;
    }

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
