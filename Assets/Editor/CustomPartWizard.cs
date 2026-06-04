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
    bool   m_TexturesFoldout  = false;

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

        // ── Part Settings ─────────────────────────────────────────────────────
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Part Settings", EditorStyles.boldLabel);
        EditorGUILayout.Separator();

        m_PartName    = EditorGUILayout.TextField("Part Name", m_PartName);
        m_DisplayName = EditorGUILayout.TextField(
            new GUIContent("Display Name (optional)",
                "Name shown in the scanner HUD and salvage ledger. Creates OI_<PartName>.asset. Leave blank to inherit from template."),
            m_DisplayName);

        EditorGUILayout.BeginHorizontal();
        m_OutputFolder = EditorGUILayout.TextField("Output Folder", m_OutputFolder);
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            var picked = EditorUtility.OpenFolderPanel("Select Output Folder", Application.dataPath, "");
            if (!string.IsNullOrEmpty(picked) && picked.StartsWith(Application.dataPath))
                m_OutputFolder = "Assets" + picked.Substring(Application.dataPath.Length).Replace('\\', '/');
        }
        EditorGUILayout.EndHorizontal();

        // ── Mesh & Material ───────────────────────────────────────────────────
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Mesh & Material", EditorStyles.boldLabel);
        EditorGUILayout.Separator();

        // Mesh — mutually exclusive with Copy Mesh From.
        // Always clear the other field when either is assigned; never rely on change detection.
        m_Mesh = (Mesh)EditorGUILayout.ObjectField("Mesh", m_Mesh, typeof(Mesh), false);
        if (m_Mesh != null) m_SourceObject = null;

        if (m_Mesh != null)
        {
            var meshPath = AssetDatabase.GetAssetPath(m_Mesh);
            var importer = AssetImporter.GetAtPath(meshPath) as ModelImporter;
            if (importer != null && !importer.isReadable)
                EditorGUILayout.HelpBox("Read/Write is disabled — will be enabled automatically on Create.", MessageType.Warning);
        }

        m_SourceObject = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Copy Mesh From",
                "Drag a parent GameObject to include all its child meshes as a single part. Clears the Mesh field above."),
            m_SourceObject, typeof(GameObject), true);
        if (m_SourceObject != null) m_Mesh = null;

        if (m_SourceObject != null)
        {
            var meshFilters = m_SourceObject.GetComponentsInChildren<MeshFilter>(true);
            var names = new List<string>();
            foreach (var mf in meshFilters)
                if (mf.sharedMesh != null) names.Add(mf.gameObject.name);
            EditorGUILayout.HelpBox(
                names.Count > 0
                    ? $"Found {names.Count} mesh(es): {string.Join(", ", names)}"
                    : "No MeshFilter components with meshes found in this hierarchy.",
                names.Count > 0 ? MessageType.None : MessageType.Warning);
        }

        EditorGUILayout.Space(2);

        // Material — mutually exclusive with Copy Material From.
        m_Material = (Material)EditorGUILayout.ObjectField("Material", m_Material, typeof(Material), false);
        if (m_Material != null) m_MaterialSourceObject = null;

        m_MaterialSourceObject = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Copy Material From",
                "Reads the MeshRenderer material from this scene object and duplicates it into the output folder. Clears the Material field above."),
            m_MaterialSourceObject, typeof(GameObject), true);
        if (m_MaterialSourceObject != null)
        {
            m_Material = null;
            var mr = m_MaterialSourceObject.GetComponentInChildren<MeshRenderer>();
            string matName = (mr != null && mr.sharedMaterial != null) ? mr.sharedMaterial.name : "(no material found)";
            EditorGUILayout.HelpBox($"Material: {matName}", MessageType.None);
        }

        // Textures — collapsible
        EditorGUILayout.Space(2);
        m_TexturesFoldout = EditorGUILayout.Foldout(m_TexturesFoldout, "Textures (optional)", true);
        if (m_TexturesFoldout)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox("Assigned to the resolved material via HDRP slot names.  Mask: R=Metallic  G=AO  B=Detail  A=Smoothness", MessageType.None);
            m_BaseColorMap = (Texture2D)EditorGUILayout.ObjectField("Base Color (_BaseColorMap)", m_BaseColorMap, typeof(Texture2D), false);
            m_NormalMap    = (Texture2D)EditorGUILayout.ObjectField("Normal Map  (_NormalMap)",   m_NormalMap,    typeof(Texture2D), false);
            m_MaskMap      = (Texture2D)EditorGUILayout.ObjectField("Mask Map    (_MaskMap)",     m_MaskMap,      typeof(Texture2D), false);
            EditorGUI.indentLevel--;
        }

        // ── SP Material ───────────────────────────────────────────────────────
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("SP Material", EditorStyles.boldLabel);
        EditorGUILayout.Separator();

        m_MatTemplate = EditorGUILayout.Popup("Template", m_MatTemplate, MatTemplateLabels);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("SP Material Override", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Governs physical/salvage properties: cut grade, salvage destination (Furnace/Barge/Processor), " +
            "mass density, joint behavior, vaporize/yank on cut, and the orange glow (CuttingTargetable). " +
            "Leave blank to inherit from template.",
            MessageType.None);
        DrawRefOverride(
            ref m_SpMatOverrideGuid, ref m_SpMatOverrideName,
            ref m_SpMatSearchFilter, ref m_SpMatDropdownOpen, ref m_SpMatScroll,
            GetSpMatEntries);

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Blueprint Override", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Governs ECS entity setup: fuel/coolant network membership, pressure explosion logic, " +
            "vitality/health, scanner HUD entry, physics rigidbody type, and room/atmosphere sealing. " +
            "Leave blank to inherit from template.",
            MessageType.None);
        DrawRefOverride(
            ref m_BpOverrideGuid, ref m_BpOverrideName,
            ref m_BpSearchFilter, ref m_BpDropdownOpen, ref m_BpScroll,
            GetBpEntries);

        // ── Addressables ──────────────────────────────────────────────────────
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Addressables (Optional)", EditorStyles.boldLabel);
        EditorGUILayout.Separator();

        m_AddressableGroup = EditorGUILayout.TextField("Group Name", m_AddressableGroup);
        EditorGUILayout.HelpBox("Leave blank to skip registration. The group must already exist.", MessageType.None);

        // ── Advanced ──────────────────────────────────────────────────────────
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Advanced", EditorStyles.boldLabel);
        EditorGUILayout.Separator();

        m_KeepOpening = EditorGUILayout.Toggle("Keep 'Opening' child", m_KeepOpening);
        EditorGUILayout.HelpBox(
            "'Opening' marks a pressure/atmosphere boundary on the ShellConnector template. " +
            "Keep only for airlocks or section connectors. Remove for solid parts like engine bells.",
            MessageType.None);

        // ── Create ────────────────────────────────────────────────────────────
        EditorGUILayout.Space(8);
        string error = Validate();
        using (new EditorGUI.DisabledScope(error != null))
        {
            if (GUILayout.Button("Create Part Prefab", GUILayout.Height(32)))
                CreatePart();
        }

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
                // Root becomes an empty transform container — game components stay from template.
                var rootMF = root.GetComponent<MeshFilter>();
                if (rootMF) rootMF.sharedMesh = null;
                var rootMC = root.GetComponent<MeshCollider>();
                if (rootMC) rootMC.sharedMesh = null;
                var rootMR = root.GetComponent<MeshRenderer>();
                if (rootMR) rootMR.enabled = false;

                // Mirror the source object itself (not just its children) as the first child,
                // then recurse into its children — preserving the full parent-child depth.
                CopyNodeIntoPrefab(m_SourceObject.transform, root.transform, resolvedMaterial);
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

    // Creates a node for src itself under destParent (preserving local transform),
    // adds mesh components if the source has a MeshFilter, then recurses into children.
    static void CopyNodeIntoPrefab(Transform src, Transform destParent, Material mat)
    {
        var node = new GameObject(src.name);
        node.transform.SetParent(destParent, false);
        node.transform.localPosition = src.localPosition;
        node.transform.localRotation = src.localRotation;
        node.transform.localScale    = src.localScale;

        var srcMF = src.GetComponent<MeshFilter>();
        if (srcMF != null && srcMF.sharedMesh != null)
        {
            node.AddComponent<MeshFilter>().sharedMesh = srcMF.sharedMesh;
            var mr = node.AddComponent<MeshRenderer>();
            if (mat != null) mr.sharedMaterial = mat;
            var mc = node.AddComponent<MeshCollider>();
            mc.sharedMesh = srcMF.sharedMesh;
            mc.convex     = true;
        }

        foreach (Transform srcChild in src)
            CopyNodeIntoPrefab(srcChild, node.transform, mat);
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
        ref string guid, ref string displayName,
        ref string search, ref bool open, ref Vector2 scroll,
        System.Func<List<(string name, string guid)>> getEntries)
    {
        var entries = getEntries();

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
