#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using BBI.Unity.Game;
using System.Text;

public static class DumpAddressableHierarchy
{
    [MenuItem("Shipbreaker/Dump Selected Components", priority = 200)]
    static void DumpSelectedComponents()
    {
        var go = Selection.activeGameObject;
        if (go == null) { Debug.LogWarning("[DumpComponents] Select a GameObject first."); return; }

        var sb = new StringBuilder();
        sb.AppendLine($"[DumpComponents] Component tree for '{go.name}' (EditorCache/scene version):");
        WalkComponents(go.transform, "", sb);
        Debug.Log(sb.ToString());
    }

    [MenuItem("Shipbreaker/Dump Addressable Components (live)", priority = 200)]
    static void DumpAddressableComponents()
    {
        var go = Selection.activeGameObject;
        if (go == null) { Debug.LogWarning("[DumpAddressable] Select an AddressableLoader GameObject first."); return; }

        var loader = go.GetComponent<AddressableLoader>();
        if (loader == null) { Debug.LogWarning($"[DumpAddressable] {go.name} has no AddressableLoader."); return; }

        var guid = loader.assetGUID ?? (loader.refs?.Count > 0 ? loader.refs[0] : null);
        if (string.IsNullOrEmpty(guid)) { Debug.LogWarning("[DumpAddressable] No GUID found on AddressableLoader."); return; }

        Debug.Log($"[DumpAddressable] Loading {go.name} ({guid}) from Addressables...");

        var locOp = Addressables.LoadResourceLocationsAsync(guid, typeof(GameObject));
        locOp.Completed += locRes =>
        {
            if (locRes.Status != AsyncOperationStatus.Succeeded || locRes.Result == null || locRes.Result.Count == 0)
            {
                Debug.LogError($"[DumpAddressable] No location for {guid}. Run Shipbreaker → Actions → Reload Assets first.");
                return;
            }

            var loadOp = Addressables.LoadAssetAsync<GameObject>(locRes.Result[0]);
            loadOp.Completed += res =>
            {
                if (res.Status != AsyncOperationStatus.Succeeded || res.Result == null)
                {
                    Debug.LogError($"[DumpAddressable] Load failed: {res.OperationException?.Message}");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"[DumpAddressable] LIVE component tree for '{go.name}' (GUID: {guid}):");
                WalkComponents(res.Result.transform, "", sb);
                Debug.Log(sb.ToString());
            };
        };
    }

    static void WalkComponents(Transform t, string indent, StringBuilder sb)
    {
        var comps = t.GetComponents<Component>();
        var names = new System.Collections.Generic.List<string>();
        foreach (var c in comps)
            if (c != null) names.Add(c.GetType().Name);

        sb.AppendLine($"{indent}{t.name}  [{string.Join(", ", names)}]");
        foreach (Transform child in t)
            WalkComponents(child, indent + "  ", sb);
    }
}
#endif
