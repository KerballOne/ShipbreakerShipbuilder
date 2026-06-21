using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds "Lock In Rescale" to the Transform component's context (gear / right-click) menu.
/// Unlike the AddressableComponentLoader inspector button (which only shows on individual baked
/// parts), this works on ANY GameObject — including parent containers holding several baked prefabs.
/// </summary>
public static class RescaleLockerContextMenu
{
    const string TransformMenuPath = "CONTEXT/Transform/Lock In Rescale — Selected";
    const string GOMenuPath        = "GameObject/Shipbuilder/Lock In Rescale — Selected";
    const string ShipMenuPath      = "Shipbuilder/Lock In Rescale — Selected";

    [MenuItem(TransformMenuPath, true)]
    static bool ValidateTransform(MenuCommand command)
    {
        var t = command.context as Transform;
        return t != null
            && RescaleLocker.IsNonUnitScale(t)
            && RescaleLocker.CountAffected(t) > 0;
    }

    [MenuItem(TransformMenuPath, false)]
    static void ExecuteTransform(MenuCommand command)
    {
        var t = command.context as Transform;
        if (t == null) return;
        Run(t);
    }

    [MenuItem(GOMenuPath, true)]
    [MenuItem(ShipMenuPath, true, priority = 155)]
    static bool ValidateGO()
    {
        var go = Selection.activeGameObject;
        if (go == null) return false;
        var t = go.transform;
        return RescaleLocker.IsNonUnitScale(t) && RescaleLocker.CountAffected(t) > 0;
    }

    [MenuItem(GOMenuPath, false)]
    [MenuItem(ShipMenuPath, false, priority = 155)]
    static void ExecuteGO() => Run(Selection.activeGameObject?.transform);

    [MenuItem("Shipbuilder/Lock In Rescale — All Rescaled", priority = 156)]
    static void RunAll()
    {
        var rescaled = ShipValidator.FindScaleViolations();
        if (rescaled.Count == 0)
        {
            EditorUtility.DisplayDialog("Lock In Rescale — All",
                "No rescaled objects with StructureParts found in the scene.", "OK");
            return;
        }

        var list = string.Join("\n", rescaled.Select(t =>
        {
            var s = t.localScale;
            string scaleStr = RescaleLocker.IsUniformScale(t)
                ? $"{s.x:F3}" : $"({s.x:F3}, {s.y:F3}, {s.z:F3})";
            return $"• {t.name}  [{scaleStr}]";
        }));

        if (!EditorUtility.DisplayDialog("Lock In Rescale — All Rescaled",
                $"Found {rescaled.Count} rescaled object(s):\n\n{list}\n\n" +
                "Lock all into mesh geometry and reset transforms to (1,1,1)?",
                "Lock All", "Cancel"))
            return;

        LockAll(rescaled);
    }

    /// <summary>Shared by the menu item and BuildContent's auto-fix path.</summary>
    public static void LockAll(List<UnityEngine.Transform> rescaled)
    {
        int total = 0;
        foreach (var t in rescaled)
        {
            if (t == null) continue;
            total += RescaleLocker.LockRescale(t.gameObject);
        }
        EditorUtility.DisplayDialog("Rescale Locked",
            $"Locked rescale into {total} mesh(es) across {rescaled.Count} object(s).", "OK");
    }

    static void Run(Transform t)
    {
        if (t == null) return;

        int affected = RescaleLocker.CountAffected(t);
        var s = t.localScale;
        string scaleStr = RescaleLocker.IsUniformScale(t) ? $"{s.x:F3}" : $"({s.x:F3}, {s.y:F3}, {s.z:F3})";

        if (!EditorUtility.DisplayDialog("Lock In Rescale",
                $"Lock in the rescale {scaleStr} on '{t.name}' into mesh geometry?\n\n" +
                $"{affected} mesh(es) below this object will be updated, and all transforms reset to (1,1,1) " +
                "with child positions adjusted to preserve the layout.",
                "Lock In", "Cancel"))
            return;

        int baked = RescaleLocker.LockRescale(t.gameObject);
        EditorUtility.DisplayDialog("Rescale Locked",
            $"Locked rescale into {baked} mesh(es) on '{t.name}'.\nTransform reset to (1,1,1).", "OK");
    }
}
