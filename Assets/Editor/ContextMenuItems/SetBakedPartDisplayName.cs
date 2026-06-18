#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

public static class SetBakedPartDisplayName
{
    // ── Project window (prefab assets) ──────────────────────────────────────

    public static bool IsBakedPrefabPublic(string path) => IsBakedPrefab(path);

    static bool IsBakedPrefab(string path)
    {
        if (!path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase)) return false;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) return false;
        return prefab.GetComponentsInChildren<StructurePart>(true).Length > 0;
    }

    [MenuItem("Assets/Shipbreaker/Set Display Name…", true)]
    static bool ValidateAssets() => Selection.objects.Length > 0 &&
        System.Array.Exists(Selection.objects, o => IsBakedPrefab(AssetDatabase.GetAssetPath(o)));

    [MenuItem("Assets/Shipbreaker/Set Display Name…", false, 100)]
    static void ExecuteAssets()
    {
        var prefabPaths = new List<string>();
        foreach (var obj in Selection.objects)
        {
            var path = AssetDatabase.GetAssetPath(obj);
            if (IsBakedPrefab(path))
                prefabPaths.Add(path);
        }
        if (prefabPaths.Count == 0) return;

        string existing = GetExistingDisplayName(prefabPaths[0]);
        SetDisplayNameWizard.OpenForPrefabs(prefabPaths, existing);
    }

    // ── Scene hierarchy (live GameObjects) ──────────────────────────────────

    [MenuItem("Shipbreaker/Shipbuilder Tools/Set Display Name on Selected…", true)]
    static bool ValidateScene() => Selection.gameObjects.Length > 0 &&
        System.Array.Exists(Selection.gameObjects, go =>
            go.GetComponentsInChildren<StructurePart>(true).Length > 0);

    [MenuItem("Shipbreaker/Shipbuilder Tools/Set Display Name on Selected…", false, 100)]
    static void ExecuteScene()
    {
        // Collect all StructureParts under every selected GameObject
        var parts = new List<StructurePart>();
        foreach (var go in Selection.gameObjects)
            foreach (var sp in go.GetComponentsInChildren<StructurePart>(true))
                if (!parts.Contains(sp)) parts.Add(sp);

        if (parts.Count == 0) return;

        // Pre-fill from first part that already has an override
        string existing = "";
        foreach (var sp in parts)
        {
            var so = new SerializedObject(sp);
            if (so.FindProperty("m_ObjectInfoAssetOverride")?.objectReferenceValue is ObjectInfoAsset oi)
            {
                existing = new SerializedObject(oi).FindProperty("m_Data.m_ObjectName")?.stringValue ?? "";
                if (!string.IsNullOrEmpty(existing)) break;
            }
        }

        var dataFolder = FindShipDataFolder(parts);
        SetDisplayNameWizard.OpenForSceneParts(parts, existing, dataFolder);
    }

    public static string GetExistingDisplayName(string prefabPath)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) return "";
        var sp = prefab.GetComponentInChildren<StructurePart>(true);
        if (sp == null) return "";
        var so = new SerializedObject(sp);
        var oiProp = so.FindProperty("m_ObjectInfoAssetOverride");
        if (oiProp?.objectReferenceValue is ObjectInfoAsset oi)
        {
            var oiSO = new SerializedObject(oi);
            return oiSO.FindProperty("m_Data.m_ObjectName")?.stringValue ?? "";
        }
        return "";
    }

    // Walk up to the prefab root in the scene, find its source prefab asset path,
    // and return the ship subfolder (e.g. "Assets/_CustomShips/Rocinante").
    public static string FindShipDataFolder(List<StructurePart> parts)
    {
        foreach (var sp in parts)
        {
            if (sp == null) continue;
            // Walk up to find the outermost prefab root in the scene
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(sp.gameObject);
            if (root == null) root = sp.gameObject;
            var srcPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            if (string.IsNullOrEmpty(srcPath)) continue;
            // srcPath e.g. "Assets/_CustomShips/Rocinante/Rocinante.prefab"
            // Go up until we're one level below _CustomShips
            var dir = Path.GetDirectoryName(srcPath)?.Replace('\\', '/') ?? "";
            while (!string.IsNullOrEmpty(dir))
            {
                var parent = Path.GetDirectoryName(dir)?.Replace('\\', '/') ?? "";
                if (parent.EndsWith("/_CustomShips") || parent == "Assets/_CustomShips")
                    return dir + "/Data";
                dir = parent;
            }
        }
        return "Assets/_CustomShips/Data";
    }

    public static void ApplyDisplayNameToSceneParts(List<StructurePart> parts, string displayName)
    {
        var dataFolder = FindShipDataFolder(parts);
        // Ensure the Data folder exists (create each missing segment)
        var segments = dataFolder.Split('/');
        var current  = segments[0];
        for (int i = 1; i < segments.Length; i++)
        {
            var next = current + "/" + segments[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, segments[i]);
            current = next;
        }

        var safeName = string.Join("_", displayName.Split(Path.GetInvalidFileNameChars()));
        var oiPath   = $"{dataFolder}/OI_{safeName}.asset";

        var oi = AssetDatabase.LoadAssetAtPath<ObjectInfoAsset>(oiPath);
        if (oi == null)
        {
            oi = ScriptableObject.CreateInstance<ObjectInfoAsset>();
            AssetDatabase.CreateAsset(oi, oiPath);
            AssetDatabase.ImportAsset(oiPath, ImportAssetOptions.ForceSynchronousImport);
            oi = AssetDatabase.LoadAssetAtPath<ObjectInfoAsset>(oiPath);
        }

        if (oi == null) { Debug.LogError($"[SetDisplayName] Failed to create OI asset at {oiPath}"); return; }

        var oiSO     = new SerializedObject(oi);
        var nameProp = oiSO.FindProperty("m_Data.m_ObjectName");
        if (nameProp == null) { Debug.LogError("[SetDisplayName] Could not find m_Data.m_ObjectName on ObjectInfoAsset"); return; }
        nameProp.stringValue = displayName;
        oiSO.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        oi = AssetDatabase.LoadAssetAtPath<ObjectInfoAsset>(oiPath);

        Undo.SetCurrentGroupName("Set Display Name");
        int group = Undo.GetCurrentGroup();
        int count = 0;
        foreach (var sp in parts)
        {
            if (sp == null) continue;
            Undo.RecordObject(sp, "Set Display Name");
            var so   = new SerializedObject(sp);
            var prop = so.FindProperty("m_ObjectInfoAssetOverride");
            if (prop == null) { Debug.LogError($"[SetDisplayName] m_ObjectInfoAssetOverride missing on {sp.gameObject.name}"); continue; }
            prop.objectReferenceValue = oi;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(sp);
            count++;
        }
        Undo.CollapseUndoOperations(group);
        Debug.Log($"[SetDisplayName] Applied \"{displayName}\" to {count} StructurePart(s) in scene.");
    }

    public static void ApplyExistingOI(string prefabPath, ObjectInfoAsset oi)
    {
        using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
        {
            var parts = scope.prefabContentsRoot.GetComponentsInChildren<StructurePart>(true);
            foreach (var sp in parts)
            {
                var so   = new SerializedObject(sp);
                var prop = so.FindProperty("m_ObjectInfoAssetOverride");
                if (prop == null) continue;
                prop.objectReferenceValue = oi;
                so.ApplyModifiedProperties();
            }
            Debug.Log($"[SetDisplayName] {Path.GetFileNameWithoutExtension(prefabPath)}: wired existing OI '{oi.name}' to {parts.Length} StructurePart(s).");
        }
    }

    public static void ApplyExistingOIToSceneParts(List<StructurePart> parts, ObjectInfoAsset oi)
    {
        Undo.SetCurrentGroupName("Set Display Name");
        int group = Undo.GetCurrentGroup();
        int count = 0;
        foreach (var sp in parts)
        {
            if (sp == null) continue;
            Undo.RecordObject(sp, "Set Display Name");
            var so   = new SerializedObject(sp);
            var prop = so.FindProperty("m_ObjectInfoAssetOverride");
            if (prop == null) continue;
            prop.objectReferenceValue = oi;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(sp);
            count++;
        }
        Undo.CollapseUndoOperations(group);
        Debug.Log($"[SetDisplayName] Wired existing OI '{oi.name}' to {count} StructurePart(s) in scene.");
    }

    public static void ApplyDisplayName(string prefabPath, string displayName)
    {
        var prefabName   = Path.GetFileNameWithoutExtension(prefabPath);
        var prefabDir    = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/') ?? "";
        var assetsFolder = $"{prefabDir}/{prefabName}_Assets";
        var oiPath       = $"{assetsFolder}/OI_{prefabName}.asset";

        if (!AssetDatabase.IsValidFolder(assetsFolder))
            AssetDatabase.CreateFolder(prefabDir, $"{prefabName}_Assets");

        var oi = AssetDatabase.LoadAssetAtPath<ObjectInfoAsset>(oiPath);
        if (oi == null)
        {
            oi = ScriptableObject.CreateInstance<ObjectInfoAsset>();
            AssetDatabase.CreateAsset(oi, oiPath);
            AssetDatabase.ImportAsset(oiPath, ImportAssetOptions.ForceSynchronousImport);
            oi = AssetDatabase.LoadAssetAtPath<ObjectInfoAsset>(oiPath);
        }

        if (oi == null) { Debug.LogError($"[SetDisplayName] Failed to create OI asset at {oiPath}"); return; }

        var oiSO = new SerializedObject(oi);
        var nameProp = oiSO.FindProperty("m_Data.m_ObjectName");
        if (nameProp == null) { Debug.LogError("[SetDisplayName] Could not find property m_Data.m_ObjectName on ObjectInfoAsset"); return; }
        nameProp.stringValue = displayName;
        oiSO.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();

        oi = AssetDatabase.LoadAssetAtPath<ObjectInfoAsset>(oiPath);

        using (var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath))
        {
            var root  = scope.prefabContentsRoot;
            var parts = root.GetComponentsInChildren<StructurePart>(true);
            Debug.Log($"[SetDisplayName] {prefabName}: found {parts.Length} StructurePart(s), wiring OI '{displayName}'");
            foreach (var sp in parts)
            {
                var so   = new SerializedObject(sp);
                var prop = so.FindProperty("m_ObjectInfoAssetOverride");
                if (prop == null) { Debug.LogError($"[SetDisplayName] m_ObjectInfoAssetOverride missing on {sp.gameObject.name}"); continue; }
                prop.objectReferenceValue = oi;
                so.ApplyModifiedProperties();
            }
        }
    }
}

public class SetDisplayNameWizard : ScriptableWizard
{
    public ObjectInfoAsset existingAsset = null;
    public string          displayName   = "";

    List<string>        m_PrefabPaths;
    List<StructurePart> m_SceneParts;
    [SerializeField] string m_DataFolder;
    [SerializeField] string m_Header;

    public static void OpenForPrefabs(List<string> prefabPaths, string existing)
    {
        var wizard = DisplayWizard<SetDisplayNameWizard>("Set Display Name", "Apply");
        wizard.m_PrefabPaths = prefabPaths;
        wizard.displayName   = existing;
        string names = prefabPaths.Count == 1
            ? Path.GetFileNameWithoutExtension(prefabPaths[0])
            : $"{prefabPaths.Count} prefabs";
        wizard.m_Header = $"Target: {names}";
        wizard.TryPrefillExistingAsset(prefabPaths[0]);
    }

    public static void OpenForSceneParts(List<StructurePart> parts, string existing, string dataFolder)
    {
        var wizard = DisplayWizard<SetDisplayNameWizard>("Set Display Name", "Apply");
        wizard.m_SceneParts = parts;
        wizard.m_DataFolder = dataFolder;
        wizard.displayName  = existing;
        wizard.m_Header     = $"{parts.Count} StructurePart(s) selected";
        wizard.TryPrefillExistingAssetForScene(parts);
    }

    void TryPrefillExistingAsset(string prefabPath)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) return;
        var sp = prefab.GetComponentInChildren<StructurePart>(true);
        if (sp == null) return;
        var so = new SerializedObject(sp);
        existingAsset = so.FindProperty("m_ObjectInfoAssetOverride")?.objectReferenceValue as ObjectInfoAsset;
    }

    void TryPrefillExistingAssetForScene(List<StructurePart> parts)
    {
        foreach (var sp in parts)
        {
            if (sp == null) continue;
            var so = new SerializedObject(sp);
            var oi = so.FindProperty("m_ObjectInfoAssetOverride")?.objectReferenceValue as ObjectInfoAsset;
            if (oi != null) { existingAsset = oi; return; }
        }
    }

    void RefreshFromSelection()
    {
        // Try scene hierarchy first
        var sceneParts = new List<StructurePart>();
        foreach (var go in Selection.gameObjects)
            foreach (var sp in go.GetComponentsInChildren<StructurePart>(true))
                if (!sceneParts.Contains(sp)) sceneParts.Add(sp);

        if (sceneParts.Count > 0)
        {
            m_SceneParts  = sceneParts;
            m_PrefabPaths = null;
            m_DataFolder  = SetBakedPartDisplayName.FindShipDataFolder(sceneParts);
            m_Header      = $"{sceneParts.Count} StructurePart(s) selected";
            displayName   = "";
            existingAsset = null;
            TryPrefillExistingAssetForScene(sceneParts);
            if (existingAsset != null)
                displayName = new SerializedObject(existingAsset).FindProperty("m_Data.m_ObjectName")?.stringValue ?? "";
            return;
        }

        // Fall back to project window prefabs
        var paths = new List<string>();
        foreach (var obj in Selection.objects)
        {
            var path = AssetDatabase.GetAssetPath(obj);
            if (SetBakedPartDisplayName.IsBakedPrefabPublic(path)) paths.Add(path);
        }
        if (paths.Count > 0)
        {
            m_PrefabPaths = paths;
            m_SceneParts  = null;
            m_DataFolder  = "";
            string names  = paths.Count == 1 ? Path.GetFileNameWithoutExtension(paths[0]) : $"{paths.Count} prefabs";
            m_Header      = $"Target: {names}";
            displayName   = SetBakedPartDisplayName.GetExistingDisplayName(paths[0]);
            existingAsset = null;
            TryPrefillExistingAsset(paths[0]);
        }
    }

    // Fully custom GUI — suppresses the Script row ScriptableWizard draws by default.
    protected override bool DrawWizardGUI()
    {
        var labelWidth = EditorGUIUtility.labelWidth;

        // Header info + refresh button
        using (new EditorGUILayout.HorizontalScope())
        {
            if (!string.IsNullOrEmpty(m_Header))
                EditorGUILayout.HelpBox(m_Header, MessageType.None);
            if (GUILayout.Button("↺", GUILayout.Width(28), GUILayout.ExpandHeight(true)))
                RefreshFromSelection();
        }

        EditorGUILayout.Space(4);

        // ── Section 1: pick existing ─────────────────────────────────────────
        EditorGUILayout.LabelField("Use Existing Asset", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            existingAsset = (ObjectInfoAsset)EditorGUILayout.ObjectField(
                "Object Info Asset", existingAsset, typeof(ObjectInfoAsset), false);
            EditorGUI.BeginDisabledGroup(existingAsset == null);
            if (GUILayout.Button("✕", GUILayout.Width(24))) existingAsset = null;
            EditorGUI.EndDisabledGroup();
        }

        EditorGUILayout.Space(6);
        var lineRect = EditorGUILayout.GetControlRect(false, 1f);
        EditorGUI.DrawRect(lineRect, new Color(0.35f, 0.35f, 0.35f, 1f));
        EditorGUILayout.Space(6);

        // ── Section 2: create / update by name ──────────────────────────────
        EditorGUI.BeginDisabledGroup(existingAsset != null);
        EditorGUILayout.LabelField("Create / Update by Name", EditorStyles.boldLabel);
        displayName = EditorGUILayout.TextField("Display Name", displayName);

        // Resolve and show the output path
        string oiPath = ResolveNewOIPath();
        EditorGUILayout.LabelField("Output Path", oiPath ?? "—", EditorStyles.miniLabel);
        EditorGUI.EndDisabledGroup();

        EditorGUIUtility.labelWidth = labelWidth;
        return false; // returning false means we manage dirty state ourselves
    }

    string ResolveNewOIPath()
    {
        var name = (displayName ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return null;
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

        if (m_SceneParts != null)
        {
            var folder = string.IsNullOrEmpty(m_DataFolder) ? "Assets/_CustomShips/Data" : m_DataFolder;
            return $"{folder}/OI_{safe}.asset";
        }
        if (m_PrefabPaths != null && m_PrefabPaths.Count > 0)
        {
            var prefabPath = m_PrefabPaths[0];
            var prefabName = Path.GetFileNameWithoutExtension(prefabPath);
            var prefabDir  = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/') ?? "";
            return $"{prefabDir}/{prefabName}_Assets/OI_{prefabName}.asset";
        }
        return null;
    }

    void OnWizardCreate()
    {
        if (existingAsset != null)
        {
            if (m_PrefabPaths != null && m_PrefabPaths.Count > 0)
            {
                foreach (var path in m_PrefabPaths)
                    SetBakedPartDisplayName.ApplyExistingOI(path, existingAsset);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log($"[SetDisplayName] Wired existing OI '{existingAsset.name}' to {m_PrefabPaths.Count} prefab(s).");
            }
            else if (m_SceneParts != null && m_SceneParts.Count > 0)
            {
                SetBakedPartDisplayName.ApplyExistingOIToSceneParts(m_SceneParts, existingAsset);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(displayName)) { Debug.LogWarning("[SetDisplayName] Display name is empty and no existing asset selected — aborted."); return; }
        var trimmed = displayName.Trim();

        if (m_PrefabPaths != null && m_PrefabPaths.Count > 0)
        {
            foreach (var path in m_PrefabPaths)
                SetBakedPartDisplayName.ApplyDisplayName(path, trimmed);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[SetDisplayName] Applied \"{trimmed}\" to {m_PrefabPaths.Count} prefab(s).");
        }
        else if (m_SceneParts != null && m_SceneParts.Count > 0)
        {
            SetBakedPartDisplayName.ApplyDisplayNameToSceneParts(m_SceneParts, trimmed);
        }
    }

    void OnWizardOtherButton() { }
}
#endif
