using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(StructurePart))]
public class StructurePartEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        DrawBakeScaleButton();
    }

    void DrawBakeScaleButton()
    {
        var go = ((StructurePart)target).gameObject;
        var t  = go.transform;

        if (!RescaleLocker.IsNonUnitScale(t)) return;

        int affected = RescaleLocker.CountAffected(t);
        var s = t.localScale;
        string scaleStr = RescaleLocker.IsUniformScale(t)
            ? $"{s.x:F3}"
            : $"({s.x:F3}, {s.y:F3}, {s.z:F3})";

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            $"Rescale {scaleStr} detected. Lock it into the mesh geometry so joints, mass and collision " +
            $"are correct in-game ({affected} mesh{(affected == 1 ? "" : "es")} affected). The transform " +
            "resets to (1,1,1) and child positions are adjusted to keep the layout.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(affected == 0))
        {
            if (GUILayout.Button("Lock In Rescale"))
            {
                int baked = RescaleLocker.LockRescale(go);
                EditorUtility.DisplayDialog("Rescale Locked",
                    $"Locked rescale into {baked} mesh(es) on '{go.name}'.\nTransform reset to (1,1,1).", "OK");
            }
        }
    }
}
