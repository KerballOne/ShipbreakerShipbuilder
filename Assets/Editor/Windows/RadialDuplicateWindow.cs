using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEditor;

public class RadialDuplicateWindow : EditorWindow
{
    enum Axis { X, Y, Z }
    enum ReferenceSpace { World, Parent, PrevSibling, NextSibling }

    Axis _axis = Axis.Y;
    ReferenceSpace _space = ReferenceSpace.Parent;
    int _count = 4;
    bool _rotateRoomVolumes = false;
    string _log = "";
    Vector2 _logScroll;

    [MenuItem("Shipbuilder/Radial Duplicate", priority = 182)]
    static void Open() => GetWindow<RadialDuplicateWindow>("Radial Duplicate");

    void OnGUI()
    {
        _space = (ReferenceSpace)EditorGUILayout.EnumPopup("Reference axis", _space);
        _axis = (Axis)EditorGUILayout.EnumPopup("Axis", _axis);
        _count = Mathf.Max(2, EditorGUILayout.IntField("Total copies", _count));
        _rotateRoomVolumes = EditorGUILayout.Toggle("Rotate Room Sub Volumes", _rotateRoomVolumes);

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

        if (!string.IsNullOrEmpty(_log))
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Last operation log", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Copy to Clipboard", GUILayout.Width(130)))
                    GUIUtility.systemCopyBuffer = _log;
                if (GUILayout.Button("Clear", GUILayout.Width(60)))
                    _log = "";
            }
            float logHeight = Mathf.Clamp(EditorStyles.helpBox.CalcHeight(new GUIContent(_log), position.width - 20), 60, 300);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(logHeight));
            EditorGUILayout.SelectableLabel(_log, EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }
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

    static GameObject[] GetSelectionRoots(GameObject[] selection)
    {
        var set = new HashSet<GameObject>(selection);
        var roots = new List<GameObject>();
        foreach (var go in selection)
        {
            bool hasAncestorInSet = false;
            var t = go.transform.parent;
            while (t != null)
            {
                if (set.Contains(t.gameObject)) { hasAncestorInSet = true; break; }
                t = t.parent;
            }
            if (!hasAncestorInSet)
                roots.Add(go);
        }
        return roots.ToArray();
    }

    void Execute()
    {
        Vector3 axisVec;
        if (_axis == Axis.X) axisVec = Vector3.right;
        else if (_axis == Axis.Z) axisVec = Vector3.forward;
        else axisVec = Vector3.up;

        float step = 360f / _count;
        var created = new List<GameObject>();
        var sb = new StringBuilder();

        Undo.SetCurrentGroupName("Radial Duplicate");
        int group = Undo.GetCurrentGroup();

        var roots = GetSelectionRoots(Selection.gameObjects);
        foreach (var src in roots)
        {
            Transform refT = ResolveRefTransform(src);

            Vector3 pivot          = refT != null ? refT.position : Vector3.zero;
            Vector3 srcWorldPos    = src.transform.position;
            Quaternion srcWorldRot = src.transform.rotation;
            Vector3 srcScale       = src.transform.localScale;
            Vector3 srcOffset      = srcWorldPos - pivot;

            Vector3 worldAxis = refT != null ? refT.rotation * axisVec : axisVec;

            sb.AppendLine($"=== Source: {src.name} ===");
            sb.AppendLine($"  RefTransform: {(refT != null ? refT.name : "none")}");
            sb.AppendLine($"  Pivot: {pivot:F3}");
            sb.AppendLine($"  WorldAxis: {worldAxis:F3}");
            sb.AppendLine($"  SrcWorldPos: {srcWorldPos:F3}");
            sb.AppendLine($"  SrcOffset: {srcOffset:F3}");

            if (_rotateRoomVolumes)
            {
                var srcRsvs = src.GetComponentsInChildren<BBI.Unity.Game.RoomSubVolumeDefinition>();
                sb.AppendLine($"  SubVolumes ({srcRsvs.Length}):");
                foreach (var rsv in srcRsvs)
                {
                    var so = new SerializedObject(rsv);
                    sb.AppendLine($"    [{rsv.gameObject.name}] Center={so.FindProperty("m_Center").vector3Value:F3}  Size={so.FindProperty("m_Size").vector3Value:F3}  Mode={so.FindProperty("m_Mode").enumValueIndex}");
                }
            }

            for (int i = 1; i < _count; i++)
            {
                var q        = Quaternion.AngleAxis(step * i, worldAxis);
                var worldPos = pivot + q * srcOffset;
                var worldRot = q * srcWorldRot;

                sb.AppendLine($"  Copy {i} ({step * i:F1}°): pos={worldPos:F3}");

                var copy = Instantiate(src, src.transform.parent);
                copy.name = src.name;
                copy.transform.SetPositionAndRotation(worldPos, worldRot);
                copy.transform.localScale = srcScale;
                if (_rotateRoomVolumes)
                {
                    var srcRsvs = src.GetComponentsInChildren<BBI.Unity.Game.RoomSubVolumeDefinition>();
                    var copyRsvs = copy.GetComponentsInChildren<BBI.Unity.Game.RoomSubVolumeDefinition>();
                    for (int r = 0; r < Mathf.Min(srcRsvs.Length, copyRsvs.Length); r++)
                    {
                        var srcSo = new SerializedObject(srcRsvs[r]);
                        var copySo = new SerializedObject(copyRsvs[r]);
                        var srcCenter = srcSo.FindProperty("m_Center").vector3Value;
                        var newCenter = srcCenter;
                        copySo.FindProperty("m_Center").vector3Value = newCenter;
                        copySo.FindProperty("m_Size").vector3Value = srcSo.FindProperty("m_Size").vector3Value;
                        copySo.FindProperty("m_Mode").enumValueIndex = srcSo.FindProperty("m_Mode").enumValueIndex;
                        copySo.ApplyModifiedPropertiesWithoutUndo();
                        sb.AppendLine($"    RSV[{r}] srcCenter={srcCenter:F3} -> newCenter={newCenter:F3}  (localAxis={axisVec:F3} angle={step*i:F1})");
                    }
                }
                if (PrefabUtility.IsPartOfAnyPrefab(copy))
                    PrefabUtility.UnpackPrefabInstance(copy, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                // src may already have a live AddressableRendering preview child under it
                // (a FakePrefabDisplay, hideFlags=DontSave) — Instantiate() deep-clones that
                // along with everything else. Strip clones of it so the next view refresh
                // doesn't end up with two preview children under this copy's AddressableLoader.
                int removedFakes = 0;
                foreach (var fake in copy.GetComponentsInChildren<FakePrefabDisplay>(true))
                {
                    if (fake == null) continue;
                    Object.DestroyImmediate(fake.gameObject);
                    removedFakes++;
                }
                if (removedFakes > 0)
                    sb.AppendLine($"    Stripped {removedFakes} cloned preview child(ren)");

                int removed = RemoveDuplicateNonAddressableChildren.RemoveDuplicatesUnder(copy.transform);
                if (removed > 0)
                    sb.AppendLine($"    Removed {removed} duplicate non-addressable child(ren)");
                Undo.RegisterCreatedObjectUndo(copy, "Radial Duplicate");
                created.Add(copy);
            }
        }

        Undo.CollapseUndoOperations(group);
        Selection.objects = created.ToArray();
        _log = sb.ToString();
        Repaint();
    }
}
