#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using BBI.Unity.Game;
using System.Text;

public static class DumpAddressableHierarchy
{
    [MenuItem("Shipbreaker/Dump Selected Addressable Hierarchy", priority = 20)]
    static void DumpSelected()
    {
        var go = Selection.activeGameObject;
        if (go == null) { Debug.LogWarning("Select a GameObject with AddressableLoader first."); return; }

        var loader = go.GetComponent<AddressableLoader>();
        if (loader == null) { Debug.LogWarning($"{go.name} has no AddressableLoader component."); return; }

        var guid = loader.assetGUID;
        var name = go.name;
        Debug.Log($"[DumpHierarchy] Looking up locations for {name} ({guid})...");

        var locOp = Addressables.LoadResourceLocationsAsync(guid, typeof(GameObject));
        locOp.Completed += locRes =>
        {
            if (locRes.Status != AsyncOperationStatus.Succeeded || locRes.Result == null || locRes.Result.Count == 0)
            {
                Debug.LogError($"[DumpHierarchy] No location found for {guid}. Run Shipbreaker → Reload Assets first.");
                return;
            }

            Debug.Log($"[DumpHierarchy] Found location, loading asset...");

            var loadOp = Addressables.LoadAssetAsync<GameObject>(locRes.Result[0]);
            loadOp.Completed += res =>
            {
                if (res.Status != AsyncOperationStatus.Succeeded || res.Result == null)
                {
                    Debug.LogError($"[DumpHierarchy] Asset load failed for {guid}: {res.OperationException?.Message}");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine($"[DumpHierarchy] Child paths for {name} (GUID: {guid}) — paste into Child Path field:");
                WalkHierarchy(res.Result.transform, "", sb);
                Debug.Log(sb.ToString());
            };
        };
    }

    static void WalkHierarchy(Transform t, string path, StringBuilder sb)
    {
        foreach (Transform child in t)
        {
            var childPath = path == "" ? child.name : path + "/" + child.name;
            sb.AppendLine($"  {childPath}");
            WalkHierarchy(child, childPath, sb);
        }
    }

    [MenuItem("Shipbreaker/Dump Selected Addressable Hierarchy", validate = true)]
    static bool ValidateDump() => Selection.activeGameObject?.GetComponent<AddressableLoader>() != null;
}
#endif
