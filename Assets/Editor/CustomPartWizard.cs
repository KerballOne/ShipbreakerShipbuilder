using System.Collections.Generic;
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

    // ── Single-mesh mode ──────────────────────────────────────────────────────
    string     m_PartName      = "MyPart";
    string     m_OutputFolder  = "Assets/_CustomShips/";
    Mesh       m_Mesh;
    Material   m_Material;
    Texture2D  m_BaseColorMap;
    Texture2D  m_NormalMap;
    Texture2D  m_MaskMap;

    // ── Hierarchy mode (MeshCurveDeformer output) ─────────────────────────────
    GameObject m_SourceObject;         // parent GO; wizard discovers all child MeshFilters

    // ── Material override ─────────────────────────────────────────────────────
    GameObject m_MaterialSourceObject; // copy material FROM this scene GO's MeshRenderer

    // ── SP Material override (refs[0]) ────────────────────────────────────────
    string  m_SpMatOverrideGuid   = "";
    string  m_SpMatOverrideName   = "";
    string  m_SpMatSearchFilter   = "";
    bool    m_SpMatDropdownOpen   = false;
    Vector2 m_SpMatScroll;

    // ── Blueprint override (refs[1]) ──────────────────────────────────────────
    string  m_BpOverrideGuid      = "";
    string  m_BpOverrideName      = "";
    string  m_BpSearchFilter      = "";
    bool    m_BpDropdownOpen      = false;
    Vector2 m_BpScroll;

    // Lazy-loaded lists from known_assets.json
    static List<(string name, string guid)> s_SpMatEntries;
    static List<(string name, string guid)> s_BpEntries;

    // ── Shared ────────────────────────────────────────────────────────────────
    string m_AddressableGroup = "";
    bool   m_KeepOpening      = false;
    string m_DisplayName      = "";
    int    m_MatTemplate      = 0;

    Vector2 m_Scroll;

    [MenuItem("Shipbreaker/Shipbuilder Tools/Create Custom Part Wizard", priority = -10)]
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

        // ── Mesh / Hierarchy ──────────────────────────────────────────────────
        EditorGUILayout.Space();
        GUILayout.Label("Mesh & Material", EditorStyles.boldLabel);

        bool hierarchyMode = m_SourceObject != null;

        using (new EditorGUI.DisabledScope(hierarchyMode))
            m_Mesh = (Mesh)EditorGUILayout.ObjectField("Mesh", m_Mesh, typeof(Mesh), false);

        if (m_Mesh != null && !hierarchyMode)
        {
            var meshPath = AssetDatabase.GetAssetPath(m_Mesh);
            var importer = AssetImporter.GetAtPath(meshPath) as ModelImporter;
            if (importer != null && !importer.isReadable)
                EditorGUILayout.HelpBox("Read/Write is disabled on this mesh — it will be enabled automatically on Create.", MessageType.Warning);
        }

        m_SourceObject = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Source Object (hierarchy)",
                "Drag a parent GameObject here to include all its child meshes as a single part. " +
                "Disables the single Mesh field above."),
            m_SourceObject, typeof(GameObject), true);

        if (hierarchyMode)
        {
            var meshFilters = m_SourceObject.GetComponentsInChildren<MeshFilter>(true);
            var names = new List<string>();
            foreach (var mf in meshFilters)
                if (mf.sharedMesh != null) names.Add(mf.gameObject.name);
            if (names.Count > 0)
                EditorGUILayout.HelpBox($"Found {names.Count} mesh(es): {string.Join(", ", names)}", MessageType.None);
            else
                EditorGUILayout.HelpBox("No MeshFilter components with meshes found in this hierarchy.", MessageType.Warning);
        }

        // ── Material ──────────────────────────────────────────────────────────
        EditorGUILayout.Space();

        m_Material = (Material)EditorGUILayout.ObjectField("Material", m_Material, typeof(Material), false);

        m_MaterialSourceObject = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Copy Material From",
                "Optional. Reads the MeshRenderer material from this scene object and duplicates it " +
                "into the output folder. Takes priority over the Material picker above."),
            m_MaterialSourceObject, typeof(GameObject), true);

        if (m_MaterialSourceObject != null)
            EditorGUILayout.HelpBox("Material will be duplicated from this object's MeshRenderer into the output folder.", MessageType.None);

        // ── Textures ──────────────────────────────────────────────────────────
        EditorGUILayout.Space();
        GUILayout.Label("Textures (Optional)", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Assigned to the resolved material using HDRP slot names.", MessageType.None);

        m_BaseColorMap = (Texture2D)EditorGUILayout.ObjectField("Base Color (_BaseColorMap)", m_BaseColorMap, typeof(Texture2D), false);
        m_NormalMap    = (Texture2D)EditorGUILayout.ObjectField("Normal Map  (_NormalMap)",   m_NormalMap,    typeof(Texture2D), false);
        m_MaskMap      = (Texture2D)EditorGUILayout.ObjectField("Mask Map    (_MaskMap)",     m_MaskMap,      typeof(Texture2D), false);
        EditorGUILayout.HelpBox("Mask Map: R=Metallic  G=AO  B=Detail  A=Smoothness", MessageType.None);

        // ── Game Properties ───────────────────────────────────────────────────
        EditorGUILayout.Space();
        GUILayout.Label("Game Properties (Optional)", EditorStyles.boldLabel);

        m_DisplayName = EditorGUILayout.TextField(
            new GUIContent("Display Name",
                "Name shown in the scanner HUD and salvage ledger. Creates OI_<PartName>.asset automatically. Leave blank to inherit from the template."),
            m_DisplayName);

        // ── SP Material ───────────────────────────────────────────────────────
        EditorGUILayout.Space();
        GUILayout.Label("SP Material", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Determines density, salvage destination, cut level, and payout. " +
            "Reactor Core routes to Barge and explodes when cut. " +
            "Thruster variants also route to Barge and satisfy the reactor coolant pipe mechanic. " +
            "Aluminum density is unconfirmed — load an aluminum-paneled ship to verify.",
            MessageType.None);
        m_MatTemplate = EditorGUILayout.Popup(
            new GUIContent("SP Material Template", "Game StructurePart material and blueprint to inherit."),
            m_MatTemplate, MatTemplateLabels);

        DrawRefOverride(
            "SP Material Override",
            "Governs physical/salvage properties: cut grade, salvage destination (Furnace/Barge/Processor), " +
            "mass density, joint behavior, vaporize/yank on cut, and the orange glow (CuttingTargetable). " +
            "Override to change how the part behaves when cut without affecting entity/network wiring.",
            ref m_SpMatOverrideGuid, ref m_SpMatOverrideName,
            ref m_SpMatSearchFilter, ref m_SpMatDropdownOpen, ref m_SpMatScroll,
            GetSpMatEntries);

        DrawRefOverride(
            "Blueprint Override",
            "Governs ECS entity setup: which game systems attach at runtime — fuel/coolant network membership, " +
            "pressure explosion logic, vitality/health, scanner HUD entry, physics rigidbody type, and " +
            "room/atmosphere sealing. Override to change network behavior (e.g. fuel pipe, cryo pipe).",
            ref m_BpOverrideGuid, ref m_BpOverrideName,
            ref m_BpSearchFilter, ref m_BpDropdownOpen, ref m_BpScroll,
            GetBpEntries);

        // ── Addressables ──────────────────────────────────────────────────────
        EditorGUILayout.Space();
        GUILayout.Label("Addressables (Optional)", EditorStyles.boldLabel);
        m_AddressableGroup = EditorGUILayout.TextField("Group Name", m_AddressableGroup);
        EditorGUILayout.HelpBox("Leave blank to skip Addressable registration. The group must already exist.", MessageType.None);

        // ── Advanced ──────────────────────────────────────────────────────────
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

        // Mesh vs hierarchy exclusivity
        if (m_Mesh != null && m_SourceObject != null)
            return "Set either a Mesh or a Source Object, not both.";
        if (m_SourceObject != null)
        {
            var mfs = m_SourceObject.GetComponentsInChildren<MeshFilter>(true);
            bool anyMesh = false;
            foreach (var mf in mfs) if (mf.sharedMesh != null) { anyMesh = true; break; }
            if (!anyMesh) return "Source Object has no MeshFilter components with meshes.";
        }

        // Material source object validation
        if (m_MaterialSourceObject != null)
        {
            var mr = m_MaterialSourceObject.GetComponentInChildren<MeshRenderer>();
            if (mr == null || mr.sharedMaterial == null)
                return "Copy Material From object has no MeshRenderer with a material.";
        }

        if ((m_BaseColorMap != null || m_NormalMap != null || m_MaskMap != null)
            && m_Material == null && m_MaterialSourceObject == null)
            return "A material (or Copy Material From object) must be set to assign textures.";

        return null;
    }

    void CreatePart()
    {
        EditorPrefs.SetString(PrefOutputFolder, m_OutputFolder);

        var outFolder     = m_OutputFolder.TrimEnd('/');
        var prefabsFolder = $"{outFolder}/Prefabs";
        var customFolder  = $"{prefabsFolder}/CUSTOM";
        var newPrefabPath = $"{customFolder}/{m_PartName}.prefab";

        if (File.Exists(Path.GetFullPath(newPrefabPath)))
        {
            if (!EditorUtility.DisplayDialog("Overwrite?", $"{newPrefabPath} already exists. Overwrite?", "Yes", "Cancel"))
                return;
            AssetDatabase.DeleteAsset(newPrefabPath);
        }

        var dataFolder = $"{outFolder}/Data";
        if (!AssetDatabase.IsValidFolder(prefabsFolder)) AssetDatabase.CreateFolder(outFolder, "Prefabs");
        if (!AssetDatabase.IsValidFolder(customFolder))  AssetDatabase.CreateFolder(prefabsFolder, "CUSTOM");
        if (!AssetDatabase.IsValidFolder(dataFolder))    AssetDatabase.CreateFolder(outFolder, "Data");

        // Ensure single mesh is readable
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

        // Resolve material: MaterialSourceObject > m_Material picker
        Material resolvedMaterial = m_Material;
        if (m_MaterialSourceObject != null)
        {
            var mr = m_MaterialSourceObject.GetComponentInChildren<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != null)
                resolvedMaterial = mr.sharedMaterial;
        }

        // Duplicate material into output folder so each part has its own editable copy
        if (resolvedMaterial != null)
        {
            var matFolder = $"{outFolder}/Materials";
            if (!AssetDatabase.IsValidFolder(matFolder))
                AssetDatabase.CreateFolder(outFolder, "Materials");
            var srcMatPath = AssetDatabase.GetAssetPath(resolvedMaterial);
            if (!string.IsNullOrEmpty(srcMatPath))
            {
                var matDest = $"{matFolder}/{m_PartName}_Mat.mat";
                AssetDatabase.CopyAsset(srcMatPath, matDest);
                AssetDatabase.SaveAssets();
                resolvedMaterial = AssetDatabase.LoadAssetAtPath<Material>(matDest);
            }
        }

        ObjectInfoAsset oiAsset = null;
        if (!string.IsNullOrWhiteSpace(m_DisplayName))
            oiAsset = CreateObjectInfoAsset(dataFolder);

        // Copy template — this wires EntityBlueprintComponent + AddressableComponentLoader correctly
        AssetDatabase.CopyAsset(MatTemplatePaths[m_MatTemplate], newPrefabPath);
        AssetDatabase.SaveAssets();

        using (var scope = new PrefabUtility.EditPrefabContentsScope(newPrefabPath))
        {
            var root = scope.prefabContentsRoot;
            root.name = m_PartName;

            if (m_SourceObject != null)
            {
                // Hierarchy mode: root becomes an empty transform container.
                // StructurePart + EntityBlueprintComponent stay on root from the template.
                var rootMF = root.GetComponent<MeshFilter>();
                if (rootMF) rootMF.sharedMesh = null;
                var rootMC = root.GetComponent<MeshCollider>();
                if (rootMC) rootMC.sharedMesh = null;
                var rootMR = root.GetComponent<MeshRenderer>();
                if (rootMR) rootMR.enabled = false;

                // Create one child per mesh in the source hierarchy
                var meshFilters = m_SourceObject.GetComponentsInChildren<MeshFilter>(true);
                foreach (var srcMF in meshFilters)
                {
                    if (srcMF.sharedMesh == null) continue;

                    var child = new GameObject(srcMF.gameObject.name);
                    child.transform.SetParent(root.transform, false);

                    // Preserve local offset relative to source parent
                    var srcRelPos = m_SourceObject.transform.InverseTransformPoint(srcMF.transform.position);
                    var srcRelRot = Quaternion.Inverse(m_SourceObject.transform.rotation) * srcMF.transform.rotation;
                    child.transform.localPosition = srcRelPos;
                    child.transform.localRotation = srcRelRot;
                    child.transform.localScale    = srcMF.transform.lossyScale;

                    child.AddComponent<MeshFilter>().sharedMesh = srcMF.sharedMesh;

                    var childMR = child.AddComponent<MeshRenderer>();
                    if (resolvedMaterial != null) childMR.sharedMaterial = resolvedMaterial;

                    var childMC = child.AddComponent<MeshCollider>();
                    childMC.sharedMesh = srcMF.sharedMesh;
                    childMC.convex     = true;
                }
            }
            else
            {
                // Single-mesh mode (unchanged)
                if (m_Mesh != null)
                {
                    var mf = root.GetComponent<MeshFilter>();
                    if (mf) mf.sharedMesh = m_Mesh;
                    var mc = root.GetComponent<MeshCollider>();
                    if (mc) mc.sharedMesh = m_Mesh;
                }

                if (resolvedMaterial != null)
                {
                    var mr = root.GetComponent<MeshRenderer>();
                    if (mr) mr.sharedMaterials = new[] { resolvedMaterial };
                }
            }

            if (!m_KeepOpening)
            {
                var opening = root.transform.Find("Opening");
                if (opening != null) DestroyImmediate(opening.gameObject);
            }

            if (oiAsset != null)
                SetMonoBehaviourField(root, "m_ObjectInfoAssetOverride", oiAsset);

            if (!string.IsNullOrEmpty(m_SpMatOverrideGuid))
                SetLoaderRef(root, 0, m_SpMatOverrideGuid);
            if (!string.IsNullOrEmpty(m_BpOverrideGuid))
                SetLoaderRef(root, 1, m_BpOverrideGuid);
        }

        // Apply texture overrides to the duplicated material
        if (resolvedMaterial != null)
        {
            if (m_BaseColorMap != null) resolvedMaterial.SetTexture("_BaseColorMap", m_BaseColorMap);
            if (m_NormalMap    != null) resolvedMaterial.SetTexture("_NormalMap",    m_NormalMap);
            if (m_MaskMap      != null) resolvedMaterial.SetTexture("_MaskMap",      m_MaskMap);
            EditorUtility.SetDirty(resolvedMaterial);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string resolvedGroup = m_AddressableGroup;
        if (string.IsNullOrWhiteSpace(resolvedGroup))
            resolvedGroup = GuessAddressableGroup(customFolder);

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
                    var guid  = AssetDatabase.AssetPathToGUID(newPrefabPath);
                    var entry = settings.CreateOrMoveEntry(guid, group);
                    entry.address = m_PartName;
                    settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entry, true);

                    if (oiAsset != null)
                    {
                        var oiPath = AssetDatabase.GetAssetPath(oiAsset);
                        if (!string.IsNullOrEmpty(oiPath))
                        {
                            var oiGuid  = AssetDatabase.AssetPathToGUID(oiPath);
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
        var oiPath  = $"{outFolder}/OI_{m_PartName}.asset";
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
        var after    = outFolder.Substring(idx + marker.Length);
        var slash    = after.IndexOf('/');
        var shipName = slash >= 0 ? after.Substring(0, slash) : after;
        if (string.IsNullOrEmpty(shipName)) return null;
        var settings = AddressableAssetSettingsDefaultObject.Settings;
        return (settings != null && settings.FindGroup(shipName) != null) ? shipName : null;
    }

    static void SetMonoBehaviourField(GameObject root, string fieldName, Object value)
    {
        foreach (var mb in root.GetComponents<MonoBehaviour>())
        {
            var so   = new SerializedObject(mb);
            var prop = so.FindProperty(fieldName);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
                so.ApplyModifiedProperties();
                return;
            }
        }
    }

    // Overwrites refs[index] in the AddressableComponentLoader on root with a new GUID string.
    // refs[0] = m_StructurePartAsset GUID, refs[1] = m_BlueprintAsset GUID.
    static void SetLoaderRef(GameObject root, int index, string guid)
    {
        foreach (var mb in root.GetComponents<MonoBehaviour>())
        {
            var so   = new SerializedObject(mb);
            var refs = so.FindProperty("refs");
            if (refs == null || !refs.isArray) continue;
            if (index >= refs.arraySize) continue;
            refs.GetArrayElementAtIndex(index).stringValue = guid;
            so.ApplyModifiedProperties();
            return;
        }
    }

    // ── Shared ref-override UI ────────────────────────────────────────────────

    void DrawRefOverride(
        string label, string tooltip,
        ref string guid, ref string displayName,
        ref string search, ref bool open, ref Vector2 scroll,
        System.Func<List<(string name, string guid)>> getEntries)
    {
        var entries = getEntries();

        EditorGUILayout.LabelField(new GUIContent(label, tooltip));
        EditorGUILayout.BeginHorizontal();

        string btnLabel = string.IsNullOrEmpty(displayName) ? "(none — use template)" : displayName;
        if (GUILayout.Button(btnLabel, EditorStyles.popup))
            open = !open;

        if (!string.IsNullOrEmpty(guid))
        {
            if (GUILayout.Button("✕", GUILayout.Width(24)))
            {
                guid = ""; displayName = ""; open = false; search = "";
            }
        }
        EditorGUILayout.EndHorizontal();

        if (open)
        {
            search = EditorGUILayout.TextField("Search", search);
            string filter = search.ToLowerInvariant();

            var filtered = new List<(string name, string guid)>();
            foreach (var e in entries)
                if (string.IsNullOrEmpty(filter) || e.name.ToLowerInvariant().Contains(filter))
                    filtered.Add(e);

            float rowH  = EditorGUIUtility.singleLineHeight + 2;
            float maxH  = 200f;
            float listH = Mathf.Min(maxH, filtered.Count * rowH + 4);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(listH));

            foreach (var (eName, eGuid) in filtered)
            {
                bool selected = eGuid == guid;
                var  style    = new GUIStyle(EditorStyles.label);
                if (selected) style.fontStyle = FontStyle.Bold;
                if (GUILayout.Button(eName, style, GUILayout.Height(rowH)))
                {
                    guid = eGuid; displayName = eName; open = false; search = "";
                }
            }

            GUILayout.EndScrollView();
        }

        if (!string.IsNullOrEmpty(guid))
            EditorGUILayout.HelpBox($"{displayName}\n{guid}", MessageType.None);
    }

    static List<(string name, string guid)> GetSpMatEntries()
    {
        if (s_SpMatEntries != null) return s_SpMatEntries;
        s_SpMatEntries = new List<(string, string)>();
        if (LoadGameAssets.knownAssetMap == null) return s_SpMatEntries;
        foreach (var kv in LoadGameAssets.knownAssetMap)
        {
            if (!kv.Value.Contains("SP_Mat") || !kv.Value.EndsWith(".asset")) continue;
            s_SpMatEntries.Add((Path.GetFileNameWithoutExtension(kv.Value), kv.Key));
        }
        s_SpMatEntries.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
        return s_SpMatEntries;
    }

    static List<(string name, string guid)> GetBpEntries()
    {
        if (s_BpEntries != null) return s_BpEntries;
        s_BpEntries = new List<(string, string)>();
        if (LoadGameAssets.knownAssetMap == null) return s_BpEntries;
        foreach (var kv in LoadGameAssets.knownAssetMap)
        {
            if (!kv.Value.EndsWith(".asset")) continue;
            string name = Path.GetFileNameWithoutExtension(kv.Value);
            if (!name.StartsWith("BP_")) continue;
            s_BpEntries.Add((name, kv.Key));
        }
        s_BpEntries.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
        return s_BpEntries;
    }
}
