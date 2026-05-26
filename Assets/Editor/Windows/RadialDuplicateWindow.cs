using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public class RadialDuplicateWindow : EditorWindow
{
    enum Axis { X, Y, Z }

    Axis _axis = Axis.Y;
    int _count = 4;

    [MenuItem("Shipbreaker/Shipbuilder Tools/Radial Duplicate", priority = 10)]
    static void Open() => GetWindow<RadialDuplicateWindow>("Radial Duplicate");

    void OnGUI()
    {
        _axis = (Axis)EditorGUILayout.EnumPopup("Axis", _axis);
        _count = Mathf.Max(2, EditorGUILayout.IntField("Total copies", _count));

        int extra = _count - 1;
        EditorGUILayout.HelpBox(
            $"Creates {extra} additional {(extra == 1 ? "copy" : "copies")} of each selected object, " +
            $"rotated {360f / _count:F1}° apart around world {_axis}.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(Selection.gameObjects.Length == 0))
        {
            if (GUILayout.Button("Duplicate Radially"))
                Execute();
        }

        if (Selection.gameObjects.Length == 0)
            EditorGUILayout.HelpBox("Select one or more objects in the hierarchy first.", MessageType.Warning);
    }

    void Execute()
    {
        Vector3 axisVec = _axis switch
        {
            Axis.X => Vector3.right,
            Axis.Z => Vector3.forward,
            _ => Vector3.up,
        };

        float step = 360f / _count;
        var created = new List<GameObject>();

        Undo.SetCurrentGroupName("Radial Duplicate");
        int group = Undo.GetCurrentGroup();

        foreach (var src in Selection.gameObjects)
        {
            for (int i = 1; i < _count; i++)
            {
                var q = Quaternion.AngleAxis(step * i, axisVec);
                var copy = Instantiate(src, src.transform.parent);
                copy.name = src.name;
                copy.transform.SetPositionAndRotation(
                    q * src.transform.position,
                    q * src.transform.rotation);
                copy.transform.localScale = src.transform.localScale;
                Undo.RegisterCreatedObjectUndo(copy, "Radial Duplicate");
                created.Add(copy);
            }
        }

        Undo.CollapseUndoOperations(group);
        Selection.objects = created.ToArray();
    }
}
