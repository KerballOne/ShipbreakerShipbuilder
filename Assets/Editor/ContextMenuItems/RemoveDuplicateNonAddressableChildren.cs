#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using BBI.Unity.Game;

public static class RemoveDuplicateNonAddressableChildren
{
    const string GOMenuPath   = "GameObject/Shipbuilder/Remove Duplicate Non-Addressable Children";
    const string ShipMenuPath = "Shipbuilder/Remove Duplicate Non-Addressable Children";

    struct PrefabRecord
    {
        public string assetPath;
        public Transform parent;
        public int siblingIndex;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public Vector3 localScale;
        public GameObject sceneGO; // the unpacked scene GO to replace
    }

    [MenuItem(GOMenuPath, true)]
    [MenuItem(ShipMenuPath, true, priority = 144)]
    static bool Validate() => Selection.gameObjects.Length > 0;

    [MenuItem(GOMenuPath, false, 49)]
    [MenuItem(ShipMenuPath, false, priority = 144)]
    static void Execute()
    {
        // Dry run to collect what would be removed
        var toDelete = new List<(Transform child, Transform parent)>();
        foreach (var root in Selection.gameObjects)
            CollectDuplicates(root.transform, toDelete);

        if (toDelete.Count == 0)
        {
            EditorUtility.DisplayDialog("Remove Duplicates", "No duplicate non-addressable children found.", "OK");
            return;
        }

        const int maxLines = 20;
        var lines = new System.Text.StringBuilder();
        for (int i = 0; i < Mathf.Min(toDelete.Count, maxLines); i++)
            lines.AppendLine($"  • {toDelete[i].child.name}  (parent: {toDelete[i].parent.name})");
        if (toDelete.Count > maxLines)
            lines.AppendLine($"  … and {toDelete.Count - maxLines} more");

        bool confirmed = EditorUtility.DisplayDialog(
            "Remove Duplicate Non-Addressable Children",
            $"Remove {toDelete.Count} duplicate(s)?\n\n{lines}",
            "Remove", "Cancel");

        if (!confirmed) return;

        Undo.SetCurrentGroupName("Remove Duplicate Non-Addressable Children");
        int group = Undo.GetCurrentGroup();

        // Record all prefab instance roots we're about to unpack so we can re-link them after
        var prefabRecords = new List<PrefabRecord>();
        foreach (var root in Selection.gameObjects)
            CollectPrefabRoots(root.transform, prefabRecords);

        // Unpack so we can destroy children inside
        foreach (var rec in prefabRecords)
            if (rec.sceneGO != null && PrefabUtility.IsPartOfPrefabInstance(rec.sceneGO))
                PrefabUtility.UnpackPrefabInstance(rec.sceneGO, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);

        // Re-collect and delete duplicates
        var toDeleteFinal = new List<(Transform child, Transform parent)>();
        foreach (var root in Selection.gameObjects)
            CollectDuplicates(root.transform, toDeleteFinal);

        foreach (var (child, _) in toDeleteFinal)
        {
            Debug.Log($"[RemoveDuplicates] Removing '{child.name}'");
            Undo.DestroyObjectImmediate(child.gameObject);
        }

        // Re-link unpacked roots back to their prefab assets
        foreach (var rec in prefabRecords)
        {
            if (rec.sceneGO == null || string.IsNullOrEmpty(rec.assetPath)) continue;
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(rec.assetPath);
            if (prefabAsset == null) continue;

            // Replace the unpacked GO with a fresh prefab instance
            var newInstance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, rec.parent);
            newInstance.transform.localPosition = rec.localPosition;
            newInstance.transform.localRotation = rec.localRotation;
            newInstance.transform.localScale    = rec.localScale;
            newInstance.transform.SetSiblingIndex(rec.siblingIndex);
            Undo.RegisterCreatedObjectUndo(newInstance, "Re-link Prefab");
            Undo.DestroyObjectImmediate(rec.sceneGO);
        }

        Undo.CollapseUndoOperations(group);
        Debug.Log($"[RemoveDuplicates] Removed {toDeleteFinal.Count} duplicate(s), re-linked {prefabRecords.Count} prefab instance(s).");
    }

    static void CollectPrefabRoots(Transform t, List<PrefabRecord> records)
    {
        // Walk the subtree; when we find the nearest prefab instance root, record it and stop recursing.
        // Use GetNearestPrefabInstanceRoot (not Outermost) so nested prefabs inside a ship root are
        // unpacked individually rather than unlinking the entire ship prefab.
        if (PrefabUtility.IsPartOfPrefabInstance(t.gameObject) &&
            PrefabUtility.GetNearestPrefabInstanceRoot(t.gameObject) == t.gameObject)
        {
            records.Add(new PrefabRecord
            {
                assetPath     = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject),
                parent        = t.parent,
                siblingIndex  = t.GetSiblingIndex(),
                localPosition = t.localPosition,
                localRotation = t.localRotation,
                localScale    = t.localScale,
                sceneGO       = t.gameObject,
            });
            return;
        }

        foreach (Transform child in t)
            CollectPrefabRoots(child, records);
    }

    static readonly System.Text.RegularExpressions.Regex GuidPattern =
        new System.Text.RegularExpressions.Regex(@"^[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}$");

    static void CollectDuplicates(Transform t, List<(Transform, Transform)> results)
    {
        var seen = new Dictionary<string, Transform>();

        foreach (Transform child in t)
        {
            if (GuidPattern.IsMatch(child.name))
            {
                bool isAddressable = child.GetComponent<AddressableLoader>() != null;
                if (seen.TryGetValue(child.name, out var existing))
                {
                    bool existingIsAddressable = existing.GetComponent<AddressableLoader>() != null;
                    if (!isAddressable)
                        results.Add((child, t));
                    else if (!existingIsAddressable)
                    {
                        results.Add((existing, t));
                        seen[child.name] = child;
                    }
                    // both or neither addressable: ambiguous, leave for manual review
                }
                else
                {
                    seen[child.name] = child;
                }
            }

            CollectDuplicates(child, results);
        }
    }
}
#endif
