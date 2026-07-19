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

    // ── FBX submesh mode ──────────────────────────────────────────────────────
    Object m_FbxSource;                // FBX asset; each submesh becomes a convex collider child
    Mesh   m_VisualMesh;               // optional full hull mesh for MeshFilter+MeshRenderer on root

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
    int    m_MatTemplate         = 0;
    bool   m_TexturesFoldout     = false;
    bool   m_OverridesFoldout    = false;
    bool   m_AddressablesFoldout = false;

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

        // FBX/model mode — each mesh in the file → separate convex collider child GO
        // Can be combined with Mesh (which provides the root visual + trigger collider)
        var newFbxGO = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Model",
                "Drag an FBX model asset here (not a scene object). Each mesh in the file becomes a " +
                "separate convex MeshCollider child. Combine with the Mesh field to set the root visual mesh."),
            m_FbxSource as GameObject, typeof(GameObject), false);
        var newFbx = newFbxGO as Object;
        if (newFbx != m_FbxSource)
        {
            m_FbxSource = newFbx;
            if (m_FbxSource != null)
            {
                m_SourceObject = null;
                AutoPopulatePartName(m_FbxSource.name);
                AutoPopulateFromFbx(AssetDatabase.GetAssetPath(m_FbxSource));
            }
        }

        if (m_FbxSource != null)
        {
            var fbxPath  = AssetDatabase.GetAssetPath(m_FbxSource);
            var fbxSubs  = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
            int segCount = 0;
            Mesh visualCandidate = null;
            foreach (var a in fbxSubs)
            {
                if (!(a is Mesh fm)) continue;
                if (fm != null)
                    segCount++;
                else if (visualCandidate == null)
                    visualCandidate = fm;
            }
            string info = segCount > 0
                ? $"Found {segCount} submesh(es)."
                : "No meshes found in model.";
            EditorGUILayout.HelpBox(info, MessageType.None);
        }

        EditorGUILayout.Space(2);

        // Mesh — root visual + trigger collider. Mutually exclusive with Copy Mesh From.
        // Can be combined with Model to add convex collider children.
        var meshLabel = m_FbxSource != null ? "Single Mesh" : "Mesh";
        var newMesh = (Mesh)EditorGUILayout.ObjectField(meshLabel, m_Mesh, typeof(Mesh), false);
        if (newMesh != m_Mesh && newMesh != null && m_FbxSource == null)
            AutoPopulatePartName(newMesh.name);
        m_Mesh = newMesh;
        if (m_FbxSource != null) m_Mesh = null;
        else if (m_Mesh != null) m_SourceObject = null;


        var newSourceObject = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Copy Mesh From",
                "Drag a parent GameObject to include all its child meshes as a single part. Clears Model and Mesh."),
            m_SourceObject, typeof(GameObject), true);
        if (newSourceObject != m_SourceObject)
        {
            m_SourceObject = newSourceObject;
            EditorPrefs.SetInt(PrefSourceObjectID, m_SourceObject != null ? m_SourceObject.GetInstanceID() : 0);
            if (m_SourceObject != null)
                AutoPopulatePartName(m_SourceObject.name);
        }
        if (m_SourceObject != null) { m_Mesh = null; m_FbxSource = null; }

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

        // ── Textures (optional, collapsed by default) ───────────────────────────
        EditorGUILayout.Space(6);
        int texCount = (m_BaseColorMap != null ? 1 : 0) + (m_NormalMap != null ? 1 : 0) + (m_MaskMap != null ? 1 : 0);
        string texturesLabel = texCount > 0 ? $"Textures ({texCount} selected)" : "Textures (optional)";
        m_TexturesFoldout = EditorGUILayout.Foldout(m_TexturesFoldout, texturesLabel, true);
        if (m_TexturesFoldout)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox("Assigned to the resolved material via HDRP slot names.  Mask: R=Metallic  G=AO  B=Detail  A=Smoothness", MessageType.None);

            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 90f;
            m_BaseColorMap = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Base Color", "_BaseColorMap"), m_BaseColorMap, typeof(Texture2D), false);
            m_NormalMap = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Normal Map", "_NormalMap"), m_NormalMap, typeof(Texture2D), false);
            m_MaskMap = (Texture2D)EditorGUILayout.ObjectField(
                new GUIContent("Mask Map", "_MaskMap"), m_MaskMap, typeof(Texture2D), false);
            EditorGUIUtility.labelWidth = prevLabelWidth;

            EditorGUI.indentLevel--;
        }

        // ── Overrides (optional, collapsed by default) ──────────────────────────
        EditorGUILayout.Space(4);
        m_OverridesFoldout = EditorGUILayout.Foldout(m_OverridesFoldout, "SP Material & Blueprint Overrides (optional)", true);
        if (m_OverridesFoldout)
        {
            EditorGUI.indentLevel++;

            EditorGUILayout.LabelField("SP Material Override", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Governs physical/cutting properties: cut grade/stage (CuttingTargetable), mass density and " +
                "rigidbody behavior, joint behavior and room/atmosphere sealing (JointSetup), and " +
                "vaporize/yank-on-cut (BreakableJoint). Leave blank to inherit from template.",
                MessageType.None);
            DrawRefOverride(
                ref m_SpMatOverrideGuid, ref m_SpMatOverrideName,
                ref m_SpMatSearchFilter, ref m_SpMatDropdownOpen, ref m_SpMatScroll,
                GetSpMatEntries);

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Blueprint Override", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Governs ECS entity setup: fuel/coolant network membership, salvage destination " +
                "(Furnace/Barge/Processor), pressure explosion logic, vitality/health, and scanner HUD entry. " +
                "Leave blank to inherit from template.",
                MessageType.None);
            DrawRefOverride(
                ref m_BpOverrideGuid, ref m_BpOverrideName,
                ref m_BpSearchFilter, ref m_BpDropdownOpen, ref m_BpScroll,
                GetBpEntries);

            EditorGUI.indentLevel--;
        }

        // ── Addressables (optional, collapsed by default) ───────────────────────
        EditorGUILayout.Space(4);
        m_AddressablesFoldout = EditorGUILayout.Foldout(m_AddressablesFoldout, "Addressables (optional)", true);
        if (m_AddressablesFoldout)
        {
            EditorGUI.indentLevel++;
            m_AddressableGroup = EditorGUILayout.TextField("Group Name", m_AddressableGroup);
            EditorGUILayout.HelpBox("Leave blank to skip registration. The group must already exist.", MessageType.None);
            EditorGUI.indentLevel--;
        }

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

        // Mesh source exclusivity — Mesh+Model is allowed (Mesh auto-populated as visual preview)
        bool meshAlone = m_Mesh != null && m_FbxSource == null;
        int meshSources = (meshAlone ? 1 : 0) + (m_SourceObject != null ? 1 : 0) + (m_FbxSource != null ? 1 : 0);
        if (meshSources > 1)
            return "Set only one of: Mesh, Copy Mesh From, or Model.";
        if (m_SourceObject != null)
        {
            var mfs = m_SourceObject.GetComponentsInChildren<MeshFilter>(true);
            bool anyMesh = false;
            foreach (var mf in mfs) if (mf.sharedMesh != null) { anyMesh = true; break; }
            if (!anyMesh) return "Source Object has no MeshFilter components with meshes.";
        }
        if (m_FbxSource != null)
        {
            var fbxSubs = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(m_FbxSource));
            bool anyMesh = false;
            foreach (var a in fbxSubs) if (a is Mesh) { anyMesh = true; break; }
            if (!anyMesh) return "Model has no meshes.";
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

        // Duplicate material only when it came from a scene object (Copy Material From).
        // Materials auto-populated from the FBX sidecar are already project assets — use them directly.
        if (resolvedMaterial != null && m_MaterialSourceObject != null)
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
            else if (m_FbxSource != null)
            {
                // FBX model mode — each _seg mesh becomes a fully self-contained child GO
                // (MeshFilter + MeshRenderer + MeshCollider + SP + EBC) so demo charges,
                // cutting, and salvage all work. Root is a container only (no mesh/collider).
                var fbxPath   = AssetDatabase.GetAssetPath(m_FbxSource);
                var fbxSubs   = AssetDatabase.LoadAllAssetsAtPath(fbxPath);
                var segMeshes = new List<Mesh>();
                foreach (var a in fbxSubs)
                {
                    if (a is Mesh fm)
                        segMeshes.Add(fm);
                }

                // The importer already wires correct per-submesh materials (via
                // CustomMeshPostprocessor.OnAssignMaterialModel) onto MeshRenderers in the
                // imported FBX's GameObject hierarchy — read them from there instead of
                // assuming one material per mesh, and reuse the source GO's name.
                //
                // Also read each node's own local transform (position/rotation/scale) here.
                // Every mesh sub-asset's vertex data is expressed relative to that node's own
                // local origin — NOT a shared scene-wide frame — so placing every segment at
                // identity transform (as this code used to) only produces correctly-assembled
                // geometry when every source object happens to share one origin. Once a part's
                // origin is individually recentered in Blender (Object > Set Origin > Origin to
                // Geometry — see USER_GUIDE.md §1.1), each object's own origin differs, and only
                // reading its transform alongside its mesh reassembles the parts correctly.
                var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                var meshToRenderer = new Dictionary<Mesh, MeshRenderer>();
                var meshToName     = new Dictionary<Mesh, string>();
                var meshToTransform = new Dictionary<Mesh, Transform>();
                if (fbxRoot != null)
                {
                    foreach (var mf in fbxRoot.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh == null || meshToRenderer.ContainsKey(mf.sharedMesh)) continue;
                        var mr = mf.GetComponent<MeshRenderer>();
                        if (mr != null) meshToRenderer[mf.sharedMesh] = mr;
                        meshToName[mf.sharedMesh] = mf.gameObject.name;
                        meshToTransform[mf.sharedMesh] = mf.transform;
                    }
                }

                // Ensure Read/Write is enabled on the FBX — game reads mesh vertices for volume/mass
                var modelImporter = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                if (modelImporter != null && !modelImporter.isReadable)
                {
                    modelImporter.isReadable = true;
                    modelImporter.SaveAndReimport();
                }

                // Read GUIDs from template loader before stripping (override takes priority)
                string spGuid2 = !string.IsNullOrEmpty(m_SpMatOverrideGuid) ? m_SpMatOverrideGuid : null;
                string bpGuid2 = !string.IsNullOrEmpty(m_BpOverrideGuid)    ? m_BpOverrideGuid    : null;
                if (spGuid2 == null || bpGuid2 == null)
                {
                    var existingLoader = FindLoaderMonoBehaviour(root);
                    if (existingLoader != null)
                    {
                        var lso   = new SerializedObject(existingLoader);
                        var refs  = lso.FindProperty("refs");
                        var addrs = lso.FindProperty("addresses");
                        if (refs != null && refs.isArray && refs.arraySize >= 2)
                        {
                            if (spGuid2 == null) spGuid2 = refs.GetArrayElementAtIndex(0).stringValue;
                            if (bpGuid2 == null) bpGuid2 = refs.GetArrayElementAtIndex(1).stringValue;
                        }
                        else if (addrs != null && addrs.isArray && addrs.arraySize >= 2)
                        {
                            if (spGuid2 == null) spGuid2 = addrs.GetArrayElementAtIndex(0).stringValue;
                            if (bpGuid2 == null) bpGuid2 = addrs.GetArrayElementAtIndex(1).stringValue;
                        }
                    }
                }
                Debug.Log($"[CPW] segmented GUIDs: sp={spGuid2} bp={bpGuid2}");

                // Root: strip everything, then add a fresh ACL — root is a container only
                foreach (var comp in root.GetComponents<Component>())
                {
                    if (comp is Transform) continue;
                    DestroyImmediate(comp);
                }
                var rootLoaderMB = root.AddComponent<AddressableComponentLoader>();

                // Children: each segment = full self-contained salvageable GO
                for (int i = 0; i < segMeshes.Count; i++)
                {
                    var segMesh = segMeshes[i];
                    if (segMesh == null) continue;

                    string segName = meshToName.TryGetValue(segMesh, out var srcName)
                        ? srcName : $"{m_PartName}_{i:D2}";
                    var segGO = new GameObject(segName);
                    segGO.transform.SetParent(root.transform, false);

                    // Apply the source FBX node's own local transform — see comment above
                    // meshToTransform's declaration for why this can no longer be skipped.
                    if (meshToTransform.TryGetValue(segMesh, out var srcTransform) && srcTransform != null)
                    {
                        segGO.transform.localPosition = srcTransform.localPosition;
                        segGO.transform.localRotation = srcTransform.localRotation;
                        segGO.transform.localScale    = srcTransform.localScale;
                    }

                    var mf        = segGO.AddComponent<MeshFilter>();
                    mf.sharedMesh = segMesh;

                    var mr = segGO.AddComponent<MeshRenderer>();
                    // Build a materials array matching the mesh's submesh count so every
                    // submesh renders — a single fallback material otherwise leaves any
                    // submesh beyond index 0 with no material assigned (invisible in HDRP).
                    int subMeshCount = Mathf.Max(1, segMesh.subMeshCount);
                    var segMaterials = new Material[subMeshCount];
                    Material[] sourceMaterials = null;
                    if (meshToRenderer.TryGetValue(segMesh, out var srcRenderer) && srcRenderer != null)
                        sourceMaterials = srcRenderer.sharedMaterials;
                    for (int m = 0; m < subMeshCount; m++)
                    {
                        Material candidate = null;
                        if (sourceMaterials != null && m < sourceMaterials.Length)
                            candidate = sourceMaterials[m];
                        segMaterials[m] = candidate != null ? candidate : resolvedMaterial;
                    }
                    mr.sharedMaterials = segMaterials;

                    var mc        = segGO.AddComponent<MeshCollider>();
                    mc.convex     = true;
                    mc.sharedMesh = segMesh;

                    segGO.AddComponent(typeof(BBI.Unity.Game.StructurePart));
                    var ebc = segGO.AddComponent(typeof(BBI.Unity.Game.EntityBlueprintComponent)) as MonoBehaviour;
                    // AutoInitialize must be true so the game wires the blueprint at runtime via ACL
                    if (ebc != null)
                    {
                        var ebcSO = new SerializedObject(ebc);
                        var autoProp = ebcSO.FindProperty("m_AutoInitialize");
                        if (autoProp != null) { autoProp.boolValue = true; ebcSO.ApplyModifiedPropertiesWithoutUndo(); }
                    }
                }

                // Add MandatoryJointContainer on root so joints work correctly
                root.AddComponent<BBI.Unity.Game.MandatoryJointContainer>();

                // Auto-register all child SP+EBC into the root ACL
                Debug.Log($"[CPW] segmented ACL populate: rootLoader={rootLoaderMB != null}, segCount={segMeshes.Count}, spGuid={spGuid2}, bpGuid={bpGuid2}");

                if (rootLoaderMB != null && segMeshes.Count > 0)
                {
                    int added = 0;
                    foreach (Transform child in root.transform)
                    {
                        var sp = child.GetComponent<BBI.Unity.Game.StructurePart>();
                        var eb = child.GetComponent<BBI.Unity.Game.EntityBlueprintComponent>();
                        Debug.Log($"[CPW]   child={child.name} sp={sp != null} eb={eb != null}");
                        if (sp != null && spGuid2 != null)
                        {
                            rootLoaderMB.componentValues.Add(new AddressableComponentValue { component = sp, field = "m_StructurePartAsset", address = spGuid2 });
                            added++;
                        }
                        if (eb != null && bpGuid2 != null)
                        {
                            rootLoaderMB.componentValues.Add(new AddressableComponentValue { component = eb, field = "m_BlueprintAsset", address = bpGuid2 });
                            added++;
                        }
                    }
                    Debug.Log($"[CPW] added {added} ACL entries, componentValues.Count={rootLoaderMB.componentValues.Count}");
                    EditorUtility.SetDirty(rootLoaderMB);
                }
            }
            else
            {
                // Single-mesh mode
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

    // Fills Part Name from the selected Model/Mesh asset's name whenever the selection changes.
    void AutoPopulatePartName(string assetName)
    {
        m_PartName = assetName;
        // Part Name field is drawn earlier in OnGUI than the Model/Mesh fields that call this,
        // so the TextField already rendered with the old value this frame — force a redraw.
        Repaint();
    }

    void AutoPopulateFromFbx(string fbxAssetPath)
    {
        // Clear any previously auto-set mesh — segments are self-rendering, no separate root mesh needed
        m_Mesh = null;

        // Get material name from sidecar — FBX uses external materials so no Material subasset exists
        var sidecar = CustomMeshPostprocessor.ReadSidecar(fbxAssetPath);
        if (sidecar == null || sidecar.Count == 0)
        {
            Debug.Log($"[CPW] AutoPopulate: no sidecar found for {fbxAssetPath} — export from Blender first");
            return;
        }
        // Use first material name in the sidecar
        string blenderMatName = null;
        foreach (var k in sidecar.Keys) { blenderMatName = k; break; }

        var matFolder  = CustomMeshPostprocessor.GetMaterialFolder(fbxAssetPath);
        var texFolder  = CustomMeshPostprocessor.GetTextureFolder(fbxAssetPath);
        var shipRoot   = matFolder.Substring(0, matFolder.LastIndexOf('/'));

        var matPath  = $"{matFolder}/{blenderMatName}.mat";
        var resolved = AssetDatabase.LoadAssetAtPath<Material>(matPath);

        // Fallback: search under the ship root (catches mats created by older postprocessor version)
        if (resolved == null)
        {
            foreach (var g in AssetDatabase.FindAssets($"{blenderMatName} t:Material", new[] { shipRoot }))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g));
                if (candidate != null && candidate.name == blenderMatName) { resolved = candidate; break; }
            }
        }

        if (resolved == null)
        {
            Debug.Log($"[CPW] AutoPopulate: mat '{blenderMatName}' not found. matFolder={matFolder} shipRoot={shipRoot}");
            return;
        }

        m_Material             = resolved;
        m_MaterialSourceObject = null;
        m_MaskMap              = null;

        System.Collections.Generic.Dictionary<string, string> texMap = null;
        sidecar.TryGetValue(blenderMatName, out texMap);

        m_BaseColorMap = FindTexFromSidecar(texMap, "BaseColor", texFolder);
        m_NormalMap    = FindTexFromSidecar(texMap, "Normal",    texFolder);
        m_MaskMap      = FindTexFromSidecar(texMap, "MaskMap",   texFolder);
    }

    static Texture2D FindTexFromSidecar(
        System.Collections.Generic.Dictionary<string, string> texMap,
        string suffix, string texFolder)
    {
        if (texMap != null && texMap.TryGetValue(suffix, out var fname))
            return AssetDatabase.LoadAssetAtPath<Texture2D>($"{texFolder}/{fname}");
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

    // Space-separated search terms, all must match (AND); a term prefixed with "-" must NOT
    // match instead (e.g. "panel -aft" finds names containing "panel" but not "aft").
    // Underscores/spaces are stripped from both the name and each term before comparing, so
    // "fuelxl" matches "SP_Element_Fuel_XL" without needing exact underscore placement.
    static bool MatchesSearch(string name, string search)
    {
        if (string.IsNullOrEmpty(search)) return true;
        string lname = NormalizeForSearch(name);
        foreach (var raw in search.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            bool exclude = raw.Length > 1 && raw[0] == '-';
            string term = NormalizeForSearch(exclude ? raw.Substring(1) : raw);
            if (term.Length == 0) continue;
            bool contains = lname.Contains(term);
            if (exclude && contains) return false;
            if (!exclude && !contains) return false;
        }
        return true;
    }

    static string NormalizeForSearch(string s) =>
        s.ToLowerInvariant().Replace("_", "").Replace(" ", "");

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
            search = EditorGUILayout.TextField(
                new GUIContent("Search", "Space-separated terms, all must match. Prefix a term with - to exclude it, e.g. \"panel -aft\"."),
                search);

            var filtered = new List<(string name, string guid)>();
            foreach (var e in entries)
                if (MatchesSearch(e.name, search))
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
            if (!kv.Value.Contains("/StructurePartAsset/") || !kv.Value.EndsWith(".asset")) continue;
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
