using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

// Resizes selected RoomOpeningDefinition(s) to match their parent mesh's bounds, minus a
// per-axis offset. Selection may be the RoomOpeningDefinition GameObject itself or any
// ancestor/descendant containing one.
public class RoomOpeningResizeWindow : EditorWindow
{
    class Entry
    {
        public RoomOpeningDefinition rod;
        public Transform parent;
        public Vector3 meshSize; // parent mesh local bounds size, scaled by parent lossyScale
    }

    List<Entry> m_Entries = new List<Entry>();
    float m_Scale = 0.95f;
    bool m_CenterOnParent = true;

    [MenuItem("Shipbuilder/Resize Room Openings To Parent Mesh", priority = 184)]
    static void Open() => GetWindow<RoomOpeningResizeWindow>("Resize Room Openings");

    void OnEnable()
    {
        Selection.selectionChanged += Refresh;
        Refresh();
    }

    void OnDisable()
    {
        Selection.selectionChanged -= Refresh;
    }

    void Refresh()
    {
        var found = new List<RoomOpeningDefinition>();
        foreach (var go in Selection.gameObjects)
        {
            if (go.TryGetComponent<RoomOpeningDefinition>(out var self) && !found.Contains(self))
                found.Add(self);
            foreach (var rod in go.GetComponentsInChildren<RoomOpeningDefinition>(true))
                if (!found.Contains(rod))
                    found.Add(rod);
        }

        m_Entries.Clear();
        foreach (var rod in found)
        {
            var parent = rod.transform.parent;
            if (parent == null || !TryGetMeshLocalSize(parent, out var size))
                continue;
            m_Entries.Add(new Entry { rod = rod, parent = parent, meshSize = size });
        }

        Repaint();
    }

    static bool TryGetMeshLocalSize(Transform t, out Vector3 size)
    {
        if (t.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
        {
            size = Vector3.Scale(mf.sharedMesh.bounds.size, t.lossyScale);
            return true;
        }
        if (t.TryGetComponent<SkinnedMeshRenderer>(out var smr) && smr.sharedMesh != null)
        {
            size = Vector3.Scale(smr.sharedMesh.bounds.size, t.lossyScale);
            return true;
        }
        size = Vector3.zero;
        return false;
    }

    void OnGUI()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Room Opening Resize", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);

        if (m_Entries.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "Select GameObject(s) with a RoomOpeningDefinition (or containing one as a child) " +
                "whose parent has a MeshFilter or SkinnedMeshRenderer.",
                MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField($"{m_Entries.Count} Room Opening(s) found:", EditorStyles.miniBoldLabel);
        foreach (var e in m_Entries)
        {
            using (new EditorGUILayout.HorizontalScope("box"))
            {
                EditorGUILayout.ObjectField(e.parent.gameObject, typeof(GameObject), true);
                EditorGUILayout.LabelField($"{e.meshSize.x:F3} x {e.meshSize.y:F3} x {e.meshSize.z:F3}", GUILayout.Width(160));
            }
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Size Scale (0-1)", EditorStyles.miniBoldLabel);
        m_Scale = Mathf.Clamp01(EditorGUILayout.FloatField(m_Scale));

        m_CenterOnParent = EditorGUILayout.ToggleLeft("Center on parent (Position 0,0,0)", m_CenterOnParent);

        EditorGUILayout.Space(6);
        if (GUILayout.Button("Run"))
            Apply();
    }

    void Apply()
    {
        Undo.SetCurrentGroupName("Resize Room Openings To Parent Mesh");
        int group = Undo.GetCurrentGroup();

        foreach (var e in m_Entries)
        {
            var newSize = e.meshSize * m_Scale;
            newSize.x = Mathf.Max(0f, newSize.x);
            newSize.y = Mathf.Max(0f, newSize.y);
            newSize.z = Mathf.Max(0f, newSize.z);

            var so = new SerializedObject(e.rod);
            so.Update();
            var sizeProp = so.FindProperty("m_Size");
            if (sizeProp != null)
            {
                Undo.RecordObject(e.rod, "Resize Room Opening");
                sizeProp.vector3Value = newSize;
                so.ApplyModifiedProperties();
            }

            if (m_CenterOnParent && e.rod.transform.localPosition != Vector3.zero)
            {
                Undo.RecordObject(e.rod.transform, "Resize Room Opening");
                e.rod.transform.localPosition = Vector3.zero;
            }
        }

        Undo.CollapseUndoOperations(group);
        Repaint();
    }
}
