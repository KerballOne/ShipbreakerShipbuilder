using UnityEditor;
using UnityEngine;

public class CylindricalColliderSegments : EditorWindow
{
    GameObject m_Prefab;
    Mesh       m_ColMesh;
    float      m_SegmentAngle = 45f;

    int WedgeCount => Mathf.Max(1, Mathf.RoundToInt(360f / m_SegmentAngle));

    [MenuItem("Shipbreaker/Shipbuilder Tools/Cylindrical Collider Segments", priority = 10)]
    static void Open()
    {
        var w = GetWindow<CylindricalColliderSegments>("Cylindrical Collider Segments");
        w.minSize = new Vector2(340, 170);
    }

    void OnGUI()
    {
        EditorGUILayout.Space(6);
        m_Prefab       = (GameObject)EditorGUILayout.ObjectField("Base Part Prefab",  m_Prefab,  typeof(GameObject), false);
        m_ColMesh      = (Mesh)      EditorGUILayout.ObjectField("Collider Segment",  m_ColMesh, typeof(Mesh),       false);
        m_SegmentAngle = EditorGUILayout.FloatField("Segment Angle (deg)", m_SegmentAngle);

        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.IntField("Wedge Count", WedgeCount);

        EditorGUILayout.Space(8);

        bool ready = m_Prefab != null && m_ColMesh != null && m_SegmentAngle > 0f;
        using (new EditorGUI.DisabledScope(!ready))
        {
            if (GUILayout.Button("Setup Colliders", GUILayout.Height(32)))
                Apply();
        }

        if (!ready)
        {
            EditorGUILayout.HelpBox(
                m_Prefab       == null   ? "Assign a base part prefab." :
                m_ColMesh      == null   ? "Assign a collider segment mesh." :
                m_SegmentAngle <= 0f     ? "Segment angle must be > 0." : "",
                MessageType.Info);
        }
    }

    void Apply()
    {
        string prefabPath = AssetDatabase.GetAssetPath(m_Prefab);
        if (string.IsNullOrEmpty(prefabPath))
        {
            Debug.LogError("Prefab is not a project asset.");
            return;
        }

        // Ensure the source mesh asset is read-enabled
        string meshPath = AssetDatabase.GetAssetPath(m_ColMesh);
        if (!string.IsNullOrEmpty(meshPath))
        {
            var importer = AssetImporter.GetAtPath(meshPath) as ModelImporter;
            if (importer != null && !importer.isReadable)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
        }

        using var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath);
        var root = scope.prefabContentsRoot;

        // Remove previously generated segment collider children
        string prefix = root.name + "_Col_";
        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            var child = root.transform.GetChild(i);
            if (child.name.StartsWith(prefix))
                Object.DestroyImmediate(child.gameObject);
        }

        // Keep root convex MeshCollider enabled as a trigger so barge deposit
        // detection fires; segment children handle all physical collisions instead.
        var rootCol = root.GetComponent<MeshCollider>();
        if (rootCol != null)
        {
            rootCol.enabled   = true;
            rootCol.isTrigger = true;
        }

        int wedgeCount = WedgeCount;
        for (int i = 0; i < wedgeCount; i++)
        {
            float angleDeg = i * m_SegmentAngle;

            var child = new GameObject($"{prefix}{i:D2}");
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = Vector3.zero;
            child.transform.localScale    = Vector3.one;
            child.transform.localRotation = Quaternion.Euler(0f, 0f, angleDeg);

            var mc = child.AddComponent<MeshCollider>();
            mc.sharedMesh = m_ColMesh;
            mc.convex     = true;
        }

        Debug.Log($"Cylindrical Collider Segments: added {wedgeCount} segments to '{m_Prefab.name}', root MeshCollider set to trigger.");
    }
}
