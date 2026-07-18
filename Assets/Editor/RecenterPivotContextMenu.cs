using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Right-click a GameObject in the hierarchy → "Recenter Pivot to Mesh (Keep World Position)"
/// Shifts all immediate children so their combined renderer bounds center lands at the
/// GameObject's own origin, then compensates the GameObject's own position so nothing
/// moves in the editor or in-game. Use this on ACL-holding parents (e.g. StructurePart
/// GameObjects assigned via Component Copy Window / Assign by Name) whose pivot was never
/// recentered by ImportGamePartWizard, which causes cut/explosion FX to spawn away from
/// the visible mesh (FX position is read from transform.position).
/// </summary>
public static class RecenterPivotContextMenu
{
    const string GOMenuPath   = "GameObject/Shipbuilder/Recenter Pivot to Mesh (Keep World Position)";
    const string ShipMenuPath = "Shipbuilder/Recenter Pivot to Mesh (Keep World Position)";

    static bool Validate() =>
        Selection.gameObjects.Length > 0 &&
        Selection.gameObjects.All(go => go.transform.childCount > 0);

    [MenuItem(GOMenuPath, true)]
    static bool ValidateGO() => Validate();

    [MenuItem(GOMenuPath, false)]
    static void ExecuteGO() => Execute();

    [MenuItem(ShipMenuPath, true, priority = 130)]
    static bool ValidateShip() => Validate();

    [MenuItem(ShipMenuPath, false, priority = 130)]
    static void ExecuteShip() => Execute();

    static void Execute()
    {
        int recentered = 0, skippedNoBounds = 0, skippedAlreadyCentered = 0;

        foreach (var go in Selection.gameObjects)
        {
            var result = Recenter(go.transform);
            switch (result)
            {
                case Result.Recentered:      recentered++; break;
                case Result.NoBounds:         skippedNoBounds++; break;
                case Result.AlreadyCentered:  skippedAlreadyCentered++; break;
            }
        }

        string msg = $"Recentered {recentered} object(s).";
        if (skippedAlreadyCentered > 0) msg += $" {skippedAlreadyCentered} already centered.";
        if (skippedNoBounds > 0)        msg += $" {skippedNoBounds} had no renderers.";
        Debug.Log($"[RecenterPivot] {msg}");
        EditorUtility.DisplayDialog("Recenter Pivot", msg, "OK");
    }

    enum Result { Recentered, NoBounds, AlreadyCentered }

    static Result Recenter(Transform root)
    {
        // Compute renderer bounds center in root-local space (mirrors
        // ImportGamePartWizard.RecenterChildren's bounds math).
        bool   hasB   = false;
        Bounds bounds = default;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var wb = r.bounds;
            foreach (var corner in new[]
            {
                wb.min,
                wb.max,
                new Vector3(wb.min.x, wb.min.y, wb.max.z),
                new Vector3(wb.min.x, wb.max.y, wb.min.z),
                new Vector3(wb.max.x, wb.min.y, wb.min.z),
                new Vector3(wb.min.x, wb.max.y, wb.max.z),
                new Vector3(wb.max.x, wb.min.y, wb.max.z),
                new Vector3(wb.max.x, wb.max.y, wb.min.z),
            })
            {
                var lp = root.InverseTransformPoint(corner);
                if (!hasB) { bounds = new Bounds(lp, Vector3.zero); hasB = true; }
                else bounds.Encapsulate(lp);
            }
        }

        if (!hasB) return Result.NoBounds;

        Vector3 offset = bounds.center;
        if (offset.sqrMagnitude < 1e-8f) return Result.AlreadyCentered;

        Undo.RecordObject(root, "Recenter Pivot to Mesh");
        foreach (Transform child in root)
            Undo.RecordObject(child, "Recenter Pivot to Mesh");

        // Shift children opposite the offset so their bounds center lands on root origin.
        foreach (Transform child in root)
            child.localPosition -= offset;

        // Compensate root's world position so nothing visually moves.
        root.position += root.TransformVector(offset);

        EditorUtility.SetDirty(root.gameObject);
        Debug.Log($"[RecenterPivot] '{root.name}': shifted pivot by local offset {offset} (world position preserved).");
        return Result.Recentered;
    }
}