using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds "Bake Transform Scale" to the Transform component's context (gear / right-click) menu.
/// Unlike the AddressableComponentLoader inspector button (which only shows on individual baked
/// parts), this works on ANY GameObject — including parent containers holding several baked prefabs.
/// </summary>
public static class TransformScaleBakeContextMenu
{
    const string Path = "CONTEXT/Transform/Bake Transform Scale";

    [MenuItem(Path, true)]
    static bool Validate(MenuCommand command)
    {
        var t = command.context as Transform;
        // Enable for any non-unit scale with at least one mesh to bake below it.
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

        if (!EditorUtility.DisplayDialog("Bake Transform Scale",
                $"Bake the scale {scaleStr} on '{t.name}' into mesh geometry?\n\n" +
                $"{affected} mesh(es) below this object will be baked, and all transforms reset to (1,1,1) " +
                "with child positions adjusted to preserve the layout." + warning,
                "Bake", "Cancel"))
            return;

        int baked = TransformScaleBaker.BakeScale(t.gameObject);
        EditorUtility.DisplayDialog("Bake Complete",
            $"Baked scale into {baked} mesh(es) on '{t.name}'.\nTransform reset to (1,1,1).", "OK");
    }
}
