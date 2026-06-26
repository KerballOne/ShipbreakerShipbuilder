using System.Collections.Generic;
using System.IO;
using BBI.Unity.Game;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

public class CustomPartWizard : EditorWindow
{
    const string PrefOutputFolder    = "CustomPartWizard.OutputFolder";
    const string PrefSourceObjectID  = "CustomPartWizard.SourceObjectID";

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

    // ── Loader source ─────────────────────────────────────────────────────────
    GameObject m_LoaderSourceObject;   // copy AddressableComponentLoader/SOLoader from this GO

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
    bool   m_CopySpToChildren = true;
    string m_DisplayName      = "";
    int    m_MatTemplate      = 0;
    bool   m_TexturesFoldout  = false;

    Vector2 m_Scroll;

    static string DefaultOutputFolder()
    {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        foreach (var root in scene.GetRootGameObjects())
        {
            var srcPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            if (string.IsNullOrEmpty(srcPath)) continue;
            var dir = System.IO.Path.GetDirectoryName(srcPath).Replace('\\', '/');
            var shipName = System.IO.Path.GetFileName(dir);
            if (srcPath == $"{dir}/{shipName}.prefab" && AssetDatabase.IsValidFolder(dir))
                return dir + "/";
        }
        return "Assets/_CustomShips/";
    }

    [MenuItem("Shipbuilder/Create Custom Part Wizard", priority = 101)]
    static void Open()
    {
        var w = GetWindow<CustomPartWizard>("Custom Part Wizard");
        var savedCPW = EditorPrefs.GetString(PrefOutputFolder, "");
        w.m_OutputFolder = (string.IsNullOrEmpty(savedCPW) || savedCPW == "Assets/_CustomShips/")
            ? DefaultOutputFolder() : savedCPW;
    }

    void OnEnable()
    {
        var saved = EditorPrefs.GetString(PrefOutputFolder, "");
        m_OutputFolder = (string.IsNullOrEmpty(saved) || saved == "Assets/_CustomShips/")
            ? DefaultOutputFolder() : saved;
        int id = EditorPrefs.GetInt(PrefSourceObjectID, 0);
        if (id != 0)
            m_SourceObject = EditorUtility.InstanceIDToObject(id) as GameObject;
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

        var newSourceObject = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Copy Mesh From",
                "Drag a parent GameObject to include all its child meshes as a single part. Clears the Mesh field above."),
            m_SourceObject, typeof(GameObject), true);
        if (newSourceObject != m_SourceObject)
        {
            m_SourceObject = newSourceObject;
            EditorPrefs.SetInt(PrefSourceObjectID, m_SourceObject != null ? m_SourceObject.GetInstanceID() : 0);
        }
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

            var ls = m_SourceObject.transform.lossyScale;
            if (Mathf.Abs(ls.x - 1f) > 1e-4f || Mathf.Abs(ls.y - 1f) > 1e-4f || Mathf.Abs(ls.z - 1f) > 1e-4f)
                EditorGUILayout.HelpBox(
                    $"Non-unit scale ({ls.x:F3}, {ls.y:F3}, {ls.z:F3}). Run Lock In Rescale before using this object as a mesh source — in-game joints and mass will be wrong otherwise.",
                    MessageType.Warning);
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

        var newLoaderSrc = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Copy Loader From",
                "Optional. Copies the AddressableComponentLoader or AddressableSOLoader from this " +
                "prefab/scene object onto the new part. Overrides the template's loader entirely, " +
                "preserving the exact SP material, blueprint, and loader format of the source part."),
            m_LoaderSourceObject, typeof(GameObject), true);
        if (newLoaderSrc != m_LoaderSourceObject) m_LoaderSourceObject = newLoaderSrc;

        if (m_LoaderSourceObject != null)
        {
            string loaderName = FindLoaderMonoBehaviour(m_LoaderSourceObject) != null
                ? FindLoaderMonoBehaviour(m_LoaderSourceObject).GetType().Name
                : "(no loader found)";
            EditorGUILayout.HelpBox($"Loader source: {m_LoaderSourceObject.name}  [{loaderName}]", MessageType.None);
        }

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

        m_CopySpToChildren = EditorGUILayout.Toggle(
            new GUIContent("Copy SP to Children",
                "When using Copy Mesh From, also adds StructurePart + EntityBlueprintComponent + loader " +
                "to each child node that has a mesh, using the same SP_Mat and blueprint as the root. " +
                "Required for child cutpoint nodes to participate in in-game jointing."),
            m_CopySpToChildren);

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
                // Remove any children the template brought in — we replace them entirely.
                var existingChildren = new List<GameObject>();
                foreach (Transform c in root.transform)
                    existingChildren.Add(c.gameObject);
                foreach (var c in existingChildren)
                    DestroyImmediate(c);

                // Root inherits the source object's local transform.
                root.transform.localPosition = m_SourceObject.transform.localPosition;
                root.transform.localRotation = m_SourceObject.transform.localRotation;
                root.transform.localScale    = m_SourceObject.transform.localScale;

                var srcMF = m_SourceObject.GetComponent<MeshFilter>();
                if (srcMF != null && srcMF.sharedMesh != null)
                {
                    // Root has geometry — ensure mesh components exist (some templates omit them).
                    var rootMF = root.GetComponent<MeshFilter>()   ?? root.AddComponent<MeshFilter>();
                    var rootMC = root.GetComponent<MeshCollider>()  ?? root.AddComponent<MeshCollider>();
                    var rootMR = root.GetComponent<MeshRenderer>()  ?? root.AddComponent<MeshRenderer>();

                    var rootMesh = srcMF.sharedMesh;
                    if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(rootMesh)))
                    {
                        var guids = AssetDatabase.FindAssets($"{rootMesh.name} t:Mesh");
                        foreach (var guid in guids)
                        {
                            var candidate = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                            if (candidate != null && candidate.name == rootMesh.name) { rootMesh = candidate; break; }
                        }
                    }
                    rootMF.sharedMesh = rootMesh;
                    rootMC.sharedMesh = rootMesh;
                    rootMC.convex     = true;
                    rootMR.enabled    = true;
                    if (resolvedMaterial != null) rootMR.sharedMaterials = new[] { resolvedMaterial };
                }
                else
                {
                    // Root is a pure container (no mesh) — remove any stale mesh components from the template.
                    var staleMF = root.GetComponent<MeshFilter>();
                    var staleMR = root.GetComponent<MeshRenderer>();
                    var staleMC = root.GetComponent<MeshCollider>();
                    if (staleMF != null) DestroyImmediate(staleMF);
                    if (staleMR != null) DestroyImmediate(staleMR);
                    if (staleMC != null) DestroyImmediate(staleMC);
                }

                // Mirror only the children (and their descendants) — preserving depth.
                var rootLoaderForChildren = m_CopySpToChildren ? FindLoaderMonoBehaviour(root) : null;
                foreach (Transform srcChild in m_SourceObject.transform)
                    CopyNodeIntoPrefab(srcChild, root.transform, resolvedMaterial, rootLoaderForChildren);
            }
            else
            {
                // Single-mesh mode
                if (m_Mesh != null)
                {
                    var mf = root.GetComponent<MeshFilter>();
                    if (mf) mf.sharedMesh = m_Mesh;

                    int subMeshCount = m_Mesh.subMeshCount;
                    if (subMeshCount <= 1)
                    {
                        // Single submesh — one collider on root as before
                        var mc = root.GetComponent<MeshCollider>();
                        if (mc) mc.sharedMesh = m_Mesh;
                    }
                    else
                    {
                        // Multiple submeshes — remove root collider, create one child per submesh
                        var rootMc = root.GetComponent<MeshCollider>();
                        if (rootMc != null) DestroyImmediate(rootMc);

                        // Load all submesh assets from the FBX
                        var meshAssetPath = AssetDatabase.GetAssetPath(m_Mesh);
                        var allAssets = AssetDatabase.LoadAllAssetsAtPath(meshAssetPath);

                        for (int i = 0; i < subMeshCount; i++)
                        {
                            var colGO = new GameObject($"{m_PartName}_Col_{i:D2}");
                            colGO.transform.SetParent(root.transform, false);
                            var mc = colGO.AddComponent<MeshCollider>();
                            mc.convex = true;

                            // Find the matching submesh asset by index
                            Mesh subMesh = null;
                            foreach (var asset in allAssets)
                            {
                                if (asset is Mesh candidate && candidate != m_Mesh)
                                {
                                    // Submesh assets are named with index suffix or match seg naming
                                    if (candidate.name.EndsWith($"_seg{i+1:D2}") ||
                                        candidate.name.EndsWith($"{i+1:D2}") ||
                                        candidate.name == $"{m_Mesh.name}_{i}")
                                    {
                                        subMesh = candidate;
                                        break;
                                    }
                                }
                            }
                            // Fall back to main mesh — Unity uses submesh index via the collider
                            mc.sharedMesh = subMesh != null ? subMesh : m_Mesh;
                        }
                    }
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

            // Loader copy takes priority over individual SP/blueprint overrides
            if (m_LoaderSourceObject != null)
                CopyLoaderFromSource(m_LoaderSourceObject, root);
            else
            {
                if (!string.IsNullOrEmpty(m_SpMatOverrideGuid))
                    SetLoaderRef(root, 0, m_SpMatOverrideGuid);
                if (!string.IsNullOrEmpty(m_BpOverrideGuid))
                    SetLoaderRef(root, 1, m_BpOverrideGuid);
            }
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
    // rootLoader: when non-null, also wires StructurePart+EBC+loader onto mesh-bearing children.
    static void CopyNodeIntoPrefab(Transform src, Transform destParent, Material mat, MonoBehaviour rootLoader = null)
    {
        var node = new GameObject(src.name);
        node.transform.SetParent(destParent, false);
        node.transform.localPosition = src.localPosition;
        node.transform.localRotation = src.localRotation;
        node.transform.localScale    = src.localScale;

        var srcMF = src.GetComponent<MeshFilter>();
        if (srcMF != null && srcMF.sharedMesh != null)
        {
            // If the mesh has no asset path it's an in-memory instance — find the saved asset by name.
            var mesh = srcMF.sharedMesh;
            if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(mesh)))
            {
                var guids = AssetDatabase.FindAssets($"{mesh.name} t:Mesh");
                foreach (var guid in guids)
                {
                    var candidate = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(guid));
                    if (candidate != null && candidate.name == mesh.name) { mesh = candidate; break; }
                }
            }
            node.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = node.AddComponent<MeshRenderer>();
            if (mat != null) mr.sharedMaterial = mat;
            var mc = node.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex     = true;

            if (rootLoader != null)
            {
                node.AddComponent(typeof(BBI.Unity.Game.StructurePart));
                node.AddComponent(typeof(BBI.Unity.Game.EntityBlueprintComponent));
                CopyLoaderOntoGameObject(rootLoader, node);
            }
        }

        foreach (Transform srcChild in src)
            CopyNodeIntoPrefab(srcChild, node.transform, mat, rootLoader);
    }

    // Adds the same loader type as srcLoader onto dest and copies address/ref/field strings.
    // Component fileID references are NOT copied — the dest loader wires up its own components at runtime.
    static void CopyLoaderOntoGameObject(MonoBehaviour srcLoader, GameObject dest)
    {
        var loaderType = srcLoader.GetType();
        var destLoader = dest.AddComponent(loaderType) as MonoBehaviour;
        if (destLoader == null) return;

        var srcSO  = new SerializedObject(srcLoader);
        var destSO = new SerializedObject(destLoader);

        foreach (var propName in new[] { "refs", "addresses", "fields", "comp", "field" })
        {
            var srcProp  = srcSO.FindProperty(propName);
            var destProp = destSO.FindProperty(propName);
            if (srcProp == null || destProp == null || !srcProp.isArray) continue;
            destProp.arraySize = srcProp.arraySize;
            for (int i = 0; i < srcProp.arraySize; i++)
            {
                var srcEl  = srcProp.GetArrayElementAtIndex(i);
                var destEl = destProp.GetArrayElementAtIndex(i);
                if (srcEl.propertyType == SerializedPropertyType.String)
                    destEl.stringValue = srcEl.stringValue;
            }
        }

        var srcCV  = srcSO.FindProperty("componentValues");
        var destCV = destSO.FindProperty("componentValues");
        if (srcCV != null && srcCV.isArray && destCV != null)
        {
            destCV.arraySize = srcCV.arraySize;
            for (int i = 0; i < srcCV.arraySize; i++)
            {
                var srcEntry  = srcCV.GetArrayElementAtIndex(i);
                var destEntry = destCV.GetArrayElementAtIndex(i);
                foreach (var field in new[] { "address", "field" })
                {
                    var s = srcEntry.FindPropertyRelative(field);
                    var d = destEntry.FindPropertyRelative(field);
                    if (s != null && d != null) d.stringValue = s.stringValue;
                }
            }
        }

        destSO.ApplyModifiedProperties();
    }

    // Returns the loader MonoBehaviour (either AddressableComponentLoader or AddressableSOLoader)
    // from the given GO, identified by having either a "refs", "addresses", or "componentValues" property.
    static MonoBehaviour FindLoaderMonoBehaviour(GameObject go)
    {
        foreach (var mb in go.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            var so = new SerializedObject(mb);
            if (so.FindProperty("refs")            != null) return mb;
            if (so.FindProperty("addresses")       != null) return mb;
            if (so.FindProperty("componentValues") != null) return mb;
        }
        return null;
    }

    // Copies the loader GUIDs from the source GO's loader onto the destination root's loader.
    // Only transfers the address/ref strings and field names — never the component fileID references,
    // which are source-prefab-specific and would point at wrong objects in the new prefab.
    // The dest loader's own component references (pointing at StructurePart/EBC on the dest root)
    // are preserved intact.
    static void CopyLoaderFromSource(GameObject srcGO, GameObject destRoot)
    {
        var srcLoader = FindLoaderMonoBehaviour(srcGO);
        if (srcLoader == null) return;

        var destLoader = FindLoaderMonoBehaviour(destRoot);
        if (destLoader == null) return;

        var srcSO  = new SerializedObject(srcLoader);
        var destSO = new SerializedObject(destLoader);

        // Copy addresses (old loader) or refs (new loader) — the GUID strings only
        var srcAddresses = srcSO.FindProperty("addresses");
        var srcRefs      = srcSO.FindProperty("refs");
        var srcFields    = srcSO.FindProperty("fields");

        var destAddresses = destSO.FindProperty("addresses");
        var destRefs      = destSO.FindProperty("refs");
        var destFields    = destSO.FindProperty("fields");

        // Copy address GUIDs
        if (srcAddresses != null && srcAddresses.isArray && destAddresses != null && destAddresses.isArray)
        {
            destAddresses.arraySize = srcAddresses.arraySize;
            for (int i = 0; i < srcAddresses.arraySize; i++)
                destAddresses.GetArrayElementAtIndex(i).stringValue =
                    srcAddresses.GetArrayElementAtIndex(i).stringValue;
        }
        else if (srcRefs != null && srcRefs.isArray && destRefs != null && destRefs.isArray)
        {
            destRefs.arraySize = srcRefs.arraySize;
            for (int i = 0; i < srcRefs.arraySize; i++)
                destRefs.GetArrayElementAtIndex(i).stringValue =
                    srcRefs.GetArrayElementAtIndex(i).stringValue;
        }

        // Copy field names
        if (srcFields != null && srcFields.isArray && destFields != null && destFields.isArray)
        {
            destFields.arraySize = srcFields.arraySize;
            for (int i = 0; i < srcFields.arraySize; i++)
                destFields.GetArrayElementAtIndex(i).stringValue =
                    srcFields.GetArrayElementAtIndex(i).stringValue;
        }

        // For old loader: update componentValues[i].address and .field to match,
        // but leave componentValues[i].component pointing at the dest's own components.
        var srcCV  = srcSO.FindProperty("componentValues");
        var destCV = destSO.FindProperty("componentValues");
        if (srcCV != null && srcCV.isArray && destCV != null && destCV.isArray
            && srcCV.arraySize == destCV.arraySize)
        {
            for (int i = 0; i < srcCV.arraySize; i++)
            {
                var srcEntry  = srcCV.GetArrayElementAtIndex(i);
                var destEntry = destCV.GetArrayElementAtIndex(i);
                var srcAddr   = srcEntry.FindPropertyRelative("address");
                var destAddr  = destEntry.FindPropertyRelative("address");
                var srcFld    = srcEntry.FindPropertyRelative("field");
                var destFld   = destEntry.FindPropertyRelative("field");
                if (srcAddr != null && destAddr != null) destAddr.stringValue = srcAddr.stringValue;
                if (srcFld  != null && destFld  != null) destFld.stringValue  = srcFld.stringValue;
                // NOTE: do NOT copy "component" — it contains source fileIDs
            }
        }

        destSO.ApplyModifiedProperties();
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

    // Overwrites the SP mat or blueprint GUID in whichever loader component is present.
    // Handles both loader formats:
    //   New loader (65951d54): writes to "refs[index]"
    //   Old loader (daf0f29e): writes to "addresses[index]" and "componentValues[index].address"
    static void SetLoaderRef(GameObject root, int index, string guid)
    {
        foreach (var mb in root.GetComponents<MonoBehaviour>())
        {
            var so = new SerializedObject(mb);

            // Try new loader format first
            var refs = so.FindProperty("refs");
            if (refs != null && refs.isArray && index < refs.arraySize)
            {
                refs.GetArrayElementAtIndex(index).stringValue = guid;
                so.ApplyModifiedProperties();
                return;
            }

            // Try old loader format
            var addresses = so.FindProperty("addresses");
            if (addresses != null && addresses.isArray && index < addresses.arraySize)
            {
                addresses.GetArrayElementAtIndex(index).stringValue = guid;

                // Also update componentValues[index].address for consistency
                var compValues = so.FindProperty("componentValues");
                if (compValues != null && compValues.isArray && index < compValues.arraySize)
                {
                    var entry = compValues.GetArrayElementAtIndex(index);
                    var addrProp = entry.FindPropertyRelative("address");
                    if (addrProp != null) addrProp.stringValue = guid;
                }

                so.ApplyModifiedProperties();
                return;
            }
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
