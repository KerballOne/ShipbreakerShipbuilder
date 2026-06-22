#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes a scene-placed addressable GO in-place: loads its source addressable, runs AddressableBaker,
/// saves a prefab, then instantiates it at the original world transform and destroys the original.
/// </summary>
public static class BakeInPlaceEditor
{
    const string FallbackRoot = "Assets/_CustomShips";

    // ── Menu entries ──────────────────────────────────────────────────────────

    [MenuItem("Shipbuilder/Bake Addressable In Place", priority = 142)]
    static void MenuBake() => BakeSelection();

    [MenuItem("Shipbuilder/Bake Addressable In Place", validate = true)]
    static bool MenuBakeValidate() => SelectionHasAddressable();

    [MenuItem("GameObject/Shipbuilder/Bake Addressable In Place", priority = 20)]
    static void ContextBake() => BakeSelection();

    [MenuItem("GameObject/Shipbuilder/Bake Addressable In Place", validate = true)]
    static bool ContextBakeValidate() => SelectionHasAddressable();

    // ── Core ──────────────────────────────────────────────────────────────────

    static bool SelectionHasAddressable() =>
        Selection.gameObjects.Any(go => FindAddressableInfo(go, out _, out _));

    static async void BakeSelection()
    {
        var targets = Selection.gameObjects
            .Where(go => go != null && FindAddressableInfo(go, out _, out _))
            .ToList();

        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("Bake In Place", "No selected object has an addressable source (needs SelectAddressableParent or AddressableLoader).", "OK");
            return;
        }

        string shipDir = DetectShipDirectory();
        string prefabsRoot = $"{shipDir}/Prefabs";

        // Ensure addressable catalogs are loaded — same check the ImportGamePartWizard uses.
        if (!LoadGameAssets.CheckHandlesValid())
        {
            Debug.Log("[BakeInPlace] Handles invalid — reloading assets before bake.");
            LoadGameAssets.ReloadAssets();
            // WaitForCompletion on the catalog handles so they're ready before LoadAddressableAsync runs.
            if (LoadGameAssets.gameAssetResourceHandle.IsValid())
                LoadGameAssets.gameAssetResourceHandle.WaitForCompletion();
            if (LoadGameAssets.customAssetResourceHandle.IsValid())
                LoadGameAssets.customAssetResourceHandle.WaitForCompletion();
        }

        AddressableBaker.ClearCaches();
        AddressableBaker.EnsureFolder(prefabsRoot);

        int succeeded = 0, failed = 0;
        var lines = new System.Text.StringBuilder();

        foreach (var original in targets)
        {
            if (!FindAddressableInfo(original, out string guid, out string childPath))
                continue;

            string partName = original.name.Replace("(Clone)", "").Trim();
            string subFolder = ResolveSubFolder(guid, partName);
            string safeName = SanitizeFolderName(partName);
            string partFolder = $"{prefabsRoot}/{subFolder}";
            string assetFolder = $"{partFolder}/{safeName}_Assets";
            string prefabPath = $"{partFolder}/{safeName}_Baked.prefab";

            AddressableBaker.EnsureFolder(partFolder);
            AddressableBaker.EnsureFolder(assetFolder);

            // Load the addressable source
            GameObject source;
            try { source = await AddressableBaker.LoadAddressableAsync(guid, childPath); }
            catch (System.Exception ex)
            {
                Debug.LogError($"[BakeInPlace] Failed to load addressable '{guid}' for '{partName}': {ex.Message}");
                lines.AppendLine($"✗ {partName}\n  GUID: {guid}\n  Error: {ex.Message}");
                failed++;
                continue;
            }

            if (source == null)
            {
                Debug.LogError($"[BakeInPlace] Addressable '{guid}' returned null for '{partName}'.");
                lines.AppendLine($"✗ {partName}\n  GUID: {guid}\n  Error: addressable returned null (see Console)");
                failed++;
                continue;
            }

            // Delete existing baked prefab so we get a clean bake
            if (File.Exists(Path.GetFullPath(prefabPath)))
                AssetDatabase.DeleteAsset(prefabPath);

            var root = new GameObject(safeName + "_Baked");
            try
            {
                var spMatRefs = new List<(Component component, string field, string guid)>();
                AddressableBaker.BakeTree(source, root.transform, assetFolder, spMatRefs);

                if (spMatRefs.Count > 0)
                {
                    var acl = root.AddComponent<AddressableComponentLoader>();
                    acl.componentValues = spMatRefs
                        .Select(r => new AddressableComponentValue { component = r.component, field = r.field, address = r.guid })
                        .ToList();
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[BakeInPlace] Bake failed for '{partName}': {ex}");
                lines.AppendLine($"✗ {partName}\n  Error: {ex.Message}\n  See Console for full stack");
                Object.DestroyImmediate(root);
                failed++;
                continue;
            }
            Object.DestroyImmediate(root);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[BakeInPlace] Saved prefab not found at '{prefabPath}'.");
                lines.AppendLine($"✗ {partName}\n  Prefab not found at: {prefabPath}");
                failed++;
                continue;
            }

            // Instantiate at original world transform, under the same parent.
            // The original addressable GO is left in place — remove it manually once satisfied.
            var parent     = original.transform.parent;
            var worldPos   = original.transform.position;
            var worldRot   = original.transform.rotation;
            var worldScale = original.transform.lossyScale;
            int siblingIndex = original.transform.GetSiblingIndex();

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            instance.transform.position = worldPos;
            instance.transform.rotation = worldRot;
            // Restore lossy scale as localScale relative to new parent
            if (parent != null)
            {
                var parentScale = parent.lossyScale;
                instance.transform.localScale = new Vector3(
                    parentScale.x != 0 ? worldScale.x / parentScale.x : 1f,
                    parentScale.y != 0 ? worldScale.y / parentScale.y : 1f,
                    parentScale.z != 0 ? worldScale.z / parentScale.z : 1f);
            }
            else
            {
                instance.transform.localScale = worldScale;
            }
            instance.transform.SetSiblingIndex(siblingIndex + 1);
            Undo.RegisterCreatedObjectUndo(instance, "Bake Addressable In Place");

            EditorGUIUtility.PingObject(instance);
            lines.AppendLine($"✓ {partName}\n  → {prefabPath}\n  Original left in place — delete manually.");
            succeeded++;
            Debug.Log($"[BakeInPlace] '{partName}' baked → '{prefabPath}'. Original addressable left in place.");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        string summary = succeeded > 0
            ? $"Baked {succeeded} object(s) in place." + (failed > 0 ? $" {failed} failed." : "")
            : $"All {failed} bake(s) failed.";
        string msg = summary + "\n\n" + lines.ToString().TrimEnd();
        EditorUtility.DisplayDialog("Bake In Place", msg, "OK");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the addressable GUID (and optional childPath) for a scene GO.
    /// Checks SelectAddressableParent first, then walks up to an AddressableLoader.
    /// </summary>
    static bool FindAddressableInfo(GameObject go, out string guid, out string childPath)
    {
        guid = null;
        childPath = null;

        // Rendered addressable children carry SelectAddressableParent with the source GUID
        var sap = go.GetComponentInParent<SelectAddressableParent>();
        if (sap != null && !string.IsNullOrEmpty(sap.sourceGUID))
        {
            guid = sap.sourceGUID;
            return true;
        }

        // Unrendered addressable loader on the GO itself or a parent
        var loader = go.GetComponentInParent<AddressableLoader>();
        if (loader != null)
        {
            guid = !string.IsNullOrEmpty(loader.assetGUID)
                ? loader.assetGUID
                : (loader.refs != null && loader.refs.Count > 0 ? loader.refs[0] : null);
            childPath = loader.childPath;
            return !string.IsNullOrEmpty(guid);
        }

        return false;
    }

    /// Resolves the last folder segment of the addressable's source asset path — same
    /// approach as ImportGamePartWizard.LastFolderSegment. Falls back to the part name.
    static string ResolveSubFolder(string guid, string fallbackName)
    {
        var locOp = UnityEngine.AddressableAssets.Addressables.LoadResourceLocationsAsync(guid, typeof(GameObject));
        var locs = locOp.WaitForCompletion();
        if (locs != null && locs.Count > 0)
        {
            var primaryKey = locs[0].PrimaryKey ?? "";
            var dir = Path.GetDirectoryName(primaryKey)?.Replace('\\', '/') ?? "";
            int slash = dir.LastIndexOf('/');
            string segment = slash >= 0 ? dir.Substring(slash + 1) : dir;
            if (!string.IsNullOrEmpty(segment)) return SanitizeFolderName(segment);
        }
        return SanitizeFolderName(fallbackName);
    }

    /// <summary>
    /// Mirrors ImportGamePartWizard.DetectShipOutputFolder — walks scene roots for a
    /// Assets/_CustomShips/&lt;Name&gt;/&lt;Name&gt;.prefab instance and returns its directory.
    /// </summary>
    static string DetectShipDirectory()
    {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        foreach (var root in scene.GetRootGameObjects())
        {
            var srcPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            if (string.IsNullOrEmpty(srcPath)) continue;
            var dir = Path.GetDirectoryName(srcPath).Replace('\\', '/');
            var shipName = Path.GetFileName(dir);
            if (srcPath == $"{dir}/{shipName}.prefab" && AssetDatabase.IsValidFolder(dir))
                return dir;
        }
        return FallbackRoot;
    }

    static string SanitizeFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }
}
#endif
