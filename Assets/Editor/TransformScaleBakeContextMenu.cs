using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds "Lock In Rescale" to the Transform component's context (gear / right-click) menu.
/// Unlike the AddressableComponentLoader inspector button (which only shows on individual baked
/// parts), this works on ANY GameObject — including parent containers holding several baked prefabs.
/// </summary>
public static class TransformScaleBakeContextMenu
{
    const string Path = "CONTEXT/Transform/Lock In Rescale";

    [MenuItem(Path, true)]
    static bool Validate(MenuCommand command)
    {
        var t = command.context as Transform;
        return t != null
            && TransformScaleBaker.IsNonUnitScale(t)
            && TransformScaleBaker.CountAffected(t) > 0;
    }

    [MenuItem(Path, false)]
    static void Execute(MenuCommand command)
    {
        var t = command.context as Transform;
        if (t == null) return;

        int affected = TransformScaleBaker.CountAffected(t);
        var s = t.localScale;
        string scaleStr = TransformScaleBaker.IsUniformScale(t) ? $"{s.x:F3}" : $"({s.x:F3}, {s.y:F3}, {s.z:F3})";

        string warning = TransformScaleBaker.HasNonUniformScaleWithRotatedChildren(t)
            ? "\n\nWARNING: non-uniform scale with rotated sub-meshes — rotated meshes may be SKEWED. " +
              "Verify the result; scale uniformly if it distorts."
            : "";

        if (!EditorUtility.DisplayDialog("Lock In Rescale",
                $"Lock in the rescale {scaleStr} on '{t.name}' into mesh geometry?\n\n" +
                $"{affected} mesh(es) below this object will be updated, and all transforms reset to (1,1,1) " +
                "with child positions adjusted to preserve the layout." + warning,
                "Lock In", "Cancel"))
            return;

        int baked = TransformScaleBaker.LockRescale(t.gameObject);
        EditorUtility.DisplayDialog("Rescale Locked",
            $"Locked rescale into {baked} mesh(es) on '{t.name}'.\nTransform reset to (1,1,1).", "OK");
    }
}
