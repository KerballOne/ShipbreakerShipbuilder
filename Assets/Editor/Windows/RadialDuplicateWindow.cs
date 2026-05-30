using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

public class RadialDuplicateWindow : EditorWindow
{
    enum Axis { X, Y, Z }
    enum ReferenceSpace { World, Parent, PrevSibling, NextSibling }

    Axis _axis = Axis.Y;
    ReferenceSpace _space = ReferenceSpace.Parent;
    int _count = 4;

    [MenuItem("Shipbreaker/Shipbuilder Tools/Radial Duplicate", priority = 10)]
    static void Open() => GetWindow<RadialDuplicateWindow>("Radial Duplicate");

    void OnGUI()
    {
        _space = (ReferenceSpace)EditorGUILayout.EnumPopup("Reference axis", _space);
        _axis = (Axis)EditorGUILayout.EnumPopup("Axis", _axis);
        _count = Mathf.Max(2, EditorGUILayout.IntField("Total copies", _count));

        int extra = _count - 1;
        EditorGUILayout.HelpBox(
            $"Creates {extra} additional {(extra == 1 ? "copy" : "copies")} of each selected object, " +
            $"rotated {360f / _count:F1}° apart around {_space} {_axis}.",
            MessageType.Info);

        using (new EditorGUI.DisabledScope(Selection.gameObjects.Length == 0))
        {
            if (GUILayout.Button("Duplicate Radially"))
                Execute();
        }

        if (Selection.gameObjects.Length == 0)
            EditorGUILayout.HelpBox("Select one or more objects in the hierarchy first.", MessageType.Warning);
    }

    Transform ResolveRefTransform(GameObject src)
    {
        switch (_space)
        {
            case ReferenceSpace.Parent:
                return src.transform.parent;
            case ReferenceSpace.PrevSibling:
                int prevIdx = src.transform.GetSiblingIndex() - 1;
                return src.transform.parent != null && prevIdx >= 0
                    ? src.transform.parent.GetChild(prevIdx)
                    : null;
            case ReferenceSpace.NextSibling:
                int nextIdx = src.transform.GetSiblingIndex() + 1;
                return src.transform.parent != null && nextIdx < src.transform.parent.childCount
                    ? src.transform.parent.GetChild(nextIdx)
                    : null;
            default:
                return null;
        }
    }

    void Execute()
    {
        Vector3 axisVec;
        if (_axis == Axis.X) axisVec = Vector3.right;
        else if (_axis == Axis.Z) axisVec = Vector3.forward;
        else axisVec = Vector3.up;

        float step = 360f / _count;
        var created = new List<GameObject>();

        Undo.SetCurrentGroupName("Radial Duplicate");
        int group = Undo.GetCurrentGroup();

        foreach (var src in Selection.gameObjects)
        {
            Transform refT = ResolveRefTransform(src);

            Vector3 pivot          = refT != null ? refT.position : Vector3.zero;
            Vector3 srcWorldPos    = src.transform.position;
            Quaternion srcWorldRot = src.transform.rotation;
            Vector3 srcScale       = src.transform.localScale;
            Vector3 srcOffset      = srcWorldPos - pivot;

            for (int i = 1; i < _count; i++)
            {
                var q        = Quaternion.AngleAxis(step * i, axisVec);
                var worldPos = pivot + q * srcOffset;
                var worldRot = q * srcWorldRot;

                var copy = Instantiate(src, src.transform.parent);
                copy.name = src.name;
                copy.transform.SetPositionAndRotation(worldPos, worldRot);
                copy.transform.localScale = srcScale;
                if (PrefabUtility.IsPartOfAnyPrefab(copy))
                    PrefabUtility.UnpackPrefabInstance(copy, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                Undo.RegisterCreatedObjectUndo(copy, "Radial Duplicate");
                created.Add(copy);
            }
        }

        Undo.CollapseUndoOperations(group);
        Selection.objects = created.ToArray();
    }
}
