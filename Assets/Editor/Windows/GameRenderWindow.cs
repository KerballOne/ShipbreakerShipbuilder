using UnityEngine;
using UnityEditor;
using BBI.Unity.Game;

public class GameRenderWindow : EditorWindow
{
    public static int maxLoopDepth = 8;
    public static bool showHidden = false;
    public static bool drawRooms = true;
    public static Color roomColorInclude = new Color(0, 1, 0, .2f);
    public static Color roomColorExclude = new Color(1, 0, 0, .2f);
    public static bool drawRoomOverlaps = true;
    public static Color roomOverlapColor = new Color(.14f, .63f, .58f, .35f);
    public static bool drawRoomOverlapFlows = false;
    public static Color roomOverlapFlowColor = new Color(1, .5f, 0, 1);

    public static bool drawJoints = true;
    public static Color jointRootColor     = new Color(0f,  0.8f, 1f,  0.5f);
    public static Color jointStandardColor = new Color(0.2f,0.9f, 0.2f,0.25f);
    public static Color jointCutColor      = new Color(1f,  0.5f, 0f,  0.5f);

    public static bool drawBakedJoints = true;
    public static Color bakedJointColor = new Color(1f, 0.85f, 0f, 0.85f);

    public static bool drawJointCensus = false;
    public static bool drawJointCensusHull = true;
    public static bool drawJointCensusWireframe = false;
    public static bool jointCensusShowByNeighborCount = true;
    public static int jointCensusNeighborIndex = 0; // 0-5, where 5 means "5 or more"
    public static Color jointCensusNeighborColor = new Color(1f, 0f, 0f, 0.5f);
    public static bool jointCensusGroupByCluster = false;
    public static int jointCensusClusterIndex = 0;
    public static Color jointCensusClusterColor = new Color(0f, 1f, 1f, 0.6f);
    string lastFrameStatus;

    const string K = "GRW.";

    [MenuItem("Shipbuilder/Render Overlays", priority = 183)]
    public static void ShowRenderController()
    {
        EditorWindow.CreateInstance<GameRenderWindow>().Show();
    }

    [MenuItem("Shipbuilder/Actions/Cancel Refresh %&c", priority = 12)]
    public static void CancelRefresh()
    {
        AddressableRendering.ForceResetUpdateFlag();
        AddressableRendering.ClearView();
    }

    void OnEnable()
    {
        maxLoopDepth          = EditorPrefs.GetInt(K + "maxLoopDepth", maxLoopDepth);
        showHidden            = EditorPrefs.GetBool(K + "showHidden", showHidden);
        drawRooms             = EditorPrefs.GetBool(K + "drawRooms", drawRooms);
        roomColorInclude      = LoadColor(K + "roomColorInclude",      roomColorInclude);
        roomColorExclude      = LoadColor(K + "roomColorExclude",      roomColorExclude);
        drawRoomOverlaps      = EditorPrefs.GetBool(K + "drawRoomOverlaps", drawRoomOverlaps);
        roomOverlapColor      = LoadColor(K + "roomOverlapColor",      roomOverlapColor);
        drawRoomOverlapFlows  = EditorPrefs.GetBool(K + "drawRoomOverlapFlows", drawRoomOverlapFlows);
        roomOverlapFlowColor  = LoadColor(K + "roomOverlapFlowColor",  roomOverlapFlowColor);
        drawJoints            = EditorPrefs.GetBool(K + "drawJoints", drawJoints);
        jointRootColor        = LoadColor(K + "jointRootColor",        jointRootColor);
        jointStandardColor    = LoadColor(K + "jointStandardColor",    jointStandardColor);
        jointCutColor         = LoadColor(K + "jointCutColor",         jointCutColor);
        drawBakedJoints       = EditorPrefs.GetBool(K + "drawBakedJoints", drawBakedJoints);
        bakedJointColor       = LoadColor(K + "bakedJointColor",       bakedJointColor);
        drawJointCensus           = EditorPrefs.GetBool(K + "drawJointCensus", drawJointCensus);
        drawJointCensusHull       = EditorPrefs.GetBool(K + "drawJointCensusHull", drawJointCensusHull);
        drawJointCensusWireframe  = EditorPrefs.GetBool(K + "drawJointCensusWireframe", drawJointCensusWireframe);
        jointCensusShowByNeighborCount = EditorPrefs.GetBool(K + "jointCensusShowByNeighborCount", jointCensusShowByNeighborCount);
        jointCensusNeighborIndex       = EditorPrefs.GetInt(K + "jointCensusNeighborIndex", jointCensusNeighborIndex);
        jointCensusNeighborColor       = LoadColor(K + "jointCensusNeighborColor", jointCensusNeighborColor);
        jointCensusGroupByCluster = EditorPrefs.GetBool(K + "jointCensusGroupByCluster", jointCensusGroupByCluster);
        jointCensusClusterIndex   = EditorPrefs.GetInt(K + "jointCensusClusterIndex", jointCensusClusterIndex);
        jointCensusClusterColor   = LoadColor(K + "jointCensusClusterColor", jointCensusClusterColor);
    }

    void OnGUI()
    {
        if (GUILayout.Button("Redraw"))
        {
            AddressableRendering.ForceResetUpdateFlag();
            AddressableRendering.ClearView();
            AddressableRendering.UpdateViewList();
        }

        if (GUILayout.Button("Clear View"))
            AddressableRendering.ClearView();

        EditorGUI.BeginChangeCheck();

        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Max render depth", EditorStyles.boldLabel, GUILayout.Width(130));
        maxLoopDepth = EditorGUILayout.IntField(maxLoopDepth, GUILayout.Width(40));
        showHidden = GUILayout.Toggle(showHidden, "Show hidden", GUILayout.ExpandWidth(false));
        EditorGUILayout.EndHorizontal();

        GUILayout.Label("Room volumes", EditorStyles.boldLabel);
        drawRooms = GUILayout.Toggle(drawRooms, "Draw Rooms");
        GUILayout.Label("Room volume colors", EditorStyles.label);
        roomColorInclude = EditorGUILayout.ColorField(roomColorInclude);
        roomColorExclude = EditorGUILayout.ColorField(roomColorExclude);

        GUILayout.Label("Room overlaps", EditorStyles.boldLabel);
        drawRoomOverlaps = GUILayout.Toggle(drawRoomOverlaps, "Draw Room Overlaps");
        GUILayout.Label("Overlap Color", EditorStyles.label);
        roomOverlapColor = EditorGUILayout.ColorField(roomOverlapColor);
        drawRoomOverlapFlows = GUILayout.Toggle(drawRoomOverlapFlows, "Draw Room Overlap Flows");
        GUILayout.Label("Overlap Color", EditorStyles.label);
        roomOverlapFlowColor = EditorGUILayout.ColorField(roomOverlapFlowColor);

        GUILayout.Label("Joints", EditorStyles.boldLabel);
        drawJoints = GUILayout.Toggle(drawJoints, "Draw Joints");
        GUILayout.Label("Root — cross-part joint surface", EditorStyles.label);
        jointRootColor = EditorGUILayout.ColorField(jointRootColor);
        GUILayout.Label("Standard — internal structural", EditorStyles.label);
        jointStandardColor = EditorGUILayout.ColorField(jointStandardColor);
        GUILayout.Label("Cut point", EditorStyles.label);
        jointCutColor = EditorGUILayout.ColorField(jointCutColor);

        GUILayout.Label("Baked joints (InvisibleJoint)", EditorStyles.boldLabel);
        drawBakedJoints = GUILayout.Toggle(drawBakedJoints, "Draw Baked Joints");
        GUILayout.Label("StructureParts baked directly in the ship prefab", EditorStyles.label);
        bakedJointColor = EditorGUILayout.ColorField(bakedJointColor);

        GUILayout.Label("Joint census overlay", EditorStyles.boldLabel);
        GUILayout.Label("Colors parts by jointed-neighbor count from joint_census.csv (PartInfoLogger)", EditorStyles.label);
        var prevDrawJointCensus = drawJointCensus;
        drawJointCensus = GUILayout.Toggle(drawJointCensus, "Draw Joint Census");
        if (drawJointCensus != prevDrawJointCensus)
        {
            if (drawJointCensus)
                JointCensusGizmos.ReloadCsv();
            SceneView.RepaintAll();
        }
        EditorGUILayout.BeginHorizontal();
        drawJointCensusHull = GUILayout.Toggle(drawJointCensusHull, "Convex Hull", GUILayout.ExpandWidth(false));
        drawJointCensusWireframe = GUILayout.Toggle(drawJointCensusWireframe, "Mesh Wireframe", GUILayout.ExpandWidth(false));
        EditorGUILayout.EndHorizontal();
        jointCensusShowByNeighborCount = GUILayout.Toggle(jointCensusShowByNeighborCount, "Show by Neighbor Count");
        if (jointCensusShowByNeighborCount)
        {
            EditorGUILayout.BeginHorizontal();
            GUI.enabled = jointCensusNeighborIndex > 0;
            if (GUILayout.Button("◀", GUILayout.Width(30))) jointCensusNeighborIndex--;
            GUI.enabled = true;
            var label = jointCensusNeighborIndex >= 5 ? "5+ neighbors" : $"{jointCensusNeighborIndex} neighbor{(jointCensusNeighborIndex == 1 ? "" : "s")}";
            GUILayout.Label(label, GUILayout.Width(110));
            GUI.enabled = jointCensusNeighborIndex < 5;
            if (GUILayout.Button("▶", GUILayout.Width(30))) jointCensusNeighborIndex++;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
            jointCensusNeighborIndex = EditorGUILayout.IntSlider(jointCensusNeighborIndex, 0, 5);
            jointCensusNeighborColor = EditorGUILayout.ColorField(jointCensusNeighborColor);
        }
        GUILayout.Label("Group by connected cluster", EditorStyles.label);
        GUILayout.Label("Isolates disconnected clusters of the ship's joint graph, sorted fewest parts first", EditorStyles.wordWrappedMiniLabel);
        jointCensusGroupByCluster = GUILayout.Toggle(jointCensusGroupByCluster, "Group by Cluster");
        if (jointCensusGroupByCluster)
        {
            var prevClusterIndex = jointCensusClusterIndex;

            EditorGUILayout.BeginHorizontal();
            var maxIndex = Mathf.Max(0, JointCensusGizmos.ClusterCount - 1);
            GUI.enabled = jointCensusClusterIndex > 0;
            if (GUILayout.Button("◀", GUILayout.Width(30))) jointCensusClusterIndex--;
            GUI.enabled = true;
            var clusterSize = JointCensusGizmos.GetClusterSize(jointCensusClusterIndex);
            GUILayout.Label($"Cluster {jointCensusClusterIndex} / {maxIndex} ({clusterSize} part{(clusterSize == 1 ? "" : "s")})", GUILayout.Width(190));
            GUI.enabled = jointCensusClusterIndex < maxIndex;
            if (GUILayout.Button("▶", GUILayout.Width(30))) jointCensusClusterIndex++;
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
            jointCensusClusterIndex = EditorGUILayout.IntSlider(jointCensusClusterIndex, 0, maxIndex);
            jointCensusClusterIndex = Mathf.Clamp(jointCensusClusterIndex, 0, maxIndex);
            GUILayout.Label("Cluster color", EditorStyles.label);
            jointCensusClusterColor = EditorGUILayout.ColorField(jointCensusClusterColor);

            if (jointCensusClusterIndex != prevClusterIndex)
                JointCensusGizmos.SelectClusterObjects(jointCensusClusterIndex);

            var names = JointCensusGizmos.GetClusterPartNames(jointCensusClusterIndex);
            var preview = string.Join("\n", names.GetRange(0, Mathf.Min(3, names.Count)));
            if (names.Count > 3) preview += $"\n… and {names.Count - 3} more";
            EditorGUILayout.LabelField("Parts in cluster", EditorStyles.miniBoldLabel);
            EditorGUILayout.SelectableLabel(preview, EditorStyles.textArea, GUILayout.Height(EditorGUIUtility.singleLineHeight * Mathf.Min(4, Mathf.Max(1, names.Count))));

            if (GUILayout.Button("Frame Cluster in Scene View"))
                lastFrameStatus = JointCensusGizmos.FrameCluster(jointCensusClusterIndex);

            if (!string.IsNullOrEmpty(lastFrameStatus))
                EditorGUILayout.HelpBox(lastFrameStatus, MessageType.Info);
        }

        if (drawJointCensus && GUILayout.Button("Reload joint_census.csv"))
            JointCensusGizmos.ReloadCsv();
        if (drawJointCensus && JointCensusGizmos.LoadFailed)
            EditorGUILayout.HelpBox(
                "joint_census.csv not found or unreadable. Run PartInfoLogger's JointCensus in-game first.",
                MessageType.Info);

        if (EditorGUI.EndChangeCheck())
            SaveAll();

        if (drawJoints && AddressableRendering.jointData.Count == 0)
            EditorGUILayout.HelpBox(
                "No joint data found. Delete Assets/EditorCache/ and click Redraw to rebuild with joint data.",
                MessageType.Warning);
    }

    void SaveAll()
    {
        EditorPrefs.SetInt(K + "maxLoopDepth", maxLoopDepth);
        EditorPrefs.SetBool(K + "showHidden", showHidden);
        EditorPrefs.SetBool(K + "drawRooms", drawRooms);
        SaveColor(K + "roomColorInclude",     roomColorInclude);
        SaveColor(K + "roomColorExclude",     roomColorExclude);
        EditorPrefs.SetBool(K + "drawRoomOverlaps", drawRoomOverlaps);
        SaveColor(K + "roomOverlapColor",     roomOverlapColor);
        EditorPrefs.SetBool(K + "drawRoomOverlapFlows", drawRoomOverlapFlows);
        SaveColor(K + "roomOverlapFlowColor", roomOverlapFlowColor);
        EditorPrefs.SetBool(K + "drawJoints", drawJoints);
        SaveColor(K + "jointRootColor",       jointRootColor);
        SaveColor(K + "jointStandardColor",   jointStandardColor);
        SaveColor(K + "jointCutColor",        jointCutColor);
        EditorPrefs.SetBool(K + "drawBakedJoints", drawBakedJoints);
        SaveColor(K + "bakedJointColor",      bakedJointColor);
        EditorPrefs.SetBool(K + "drawJointCensus", drawJointCensus);
        EditorPrefs.SetBool(K + "drawJointCensusHull", drawJointCensusHull);
        EditorPrefs.SetBool(K + "drawJointCensusWireframe", drawJointCensusWireframe);
        EditorPrefs.SetBool(K + "jointCensusShowByNeighborCount", jointCensusShowByNeighborCount);
        EditorPrefs.SetInt(K + "jointCensusNeighborIndex", jointCensusNeighborIndex);
        SaveColor(K + "jointCensusNeighborColor", jointCensusNeighborColor);
        EditorPrefs.SetBool(K + "jointCensusGroupByCluster", jointCensusGroupByCluster);
        EditorPrefs.SetInt(K + "jointCensusClusterIndex", jointCensusClusterIndex);
        SaveColor(K + "jointCensusClusterColor", jointCensusClusterColor);
    }

    static void SaveColor(string key, Color c)
    {
        EditorPrefs.SetFloat(key + ".r", c.r);
        EditorPrefs.SetFloat(key + ".g", c.g);
        EditorPrefs.SetFloat(key + ".b", c.b);
        EditorPrefs.SetFloat(key + ".a", c.a);
    }

    static Color LoadColor(string key, Color def)
    {
        return new Color(
            EditorPrefs.GetFloat(key + ".r", def.r),
            EditorPrefs.GetFloat(key + ".g", def.g),
            EditorPrefs.GetFloat(key + ".b", def.b),
            EditorPrefs.GetFloat(key + ".a", def.a));
    }
}
