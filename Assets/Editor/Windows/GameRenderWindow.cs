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
    public static bool drawJointCensusHull = true; // true = Convex Hull, false = Mesh Wireframe (mutually exclusive)
    public static bool jointCensusShowByClusterSize = true; // true = Cluster Size, false = Cluster (mutually exclusive)
    public static int jointCensusSizeIndex = 0; // exact cluster-part-count to highlight, clamped to JointCensusGizmos.MaxClusterSize
    public static Color jointCensusSizeColor = new Color(1f, 0f, 0f, 0.5f);
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
        jointCensusShowByClusterSize = EditorPrefs.GetBool(K + "jointCensusShowByClusterSize", jointCensusShowByClusterSize);
        jointCensusSizeIndex         = EditorPrefs.GetInt(K + "jointCensusSizeIndex", jointCensusSizeIndex);
        jointCensusSizeColor         = LoadColor(K + "jointCensusSizeColor", jointCensusSizeColor);
        jointCensusClusterIndex   = EditorPrefs.GetInt(K + "jointCensusClusterIndex", jointCensusClusterIndex);
        jointCensusClusterColor   = LoadColor(K + "jointCensusClusterColor", jointCensusClusterColor);
    }

    const float ColorColumnWidth = 60f;
    const float StepperArrowWidth = 24f;

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

        Separator();

        GUILayout.Label("Room volumes", EditorStyles.boldLabel);
        drawRooms = ToggleWithColor(drawRooms, "Draw Rooms", ref roomColorInclude);
        roomColorExclude = LabeledColorFieldFixed(new GUIContent("Exclude Color"), roomColorExclude);

        GUILayout.Label("Room overlaps", EditorStyles.boldLabel);
        drawRoomOverlaps = ToggleWithColor(drawRoomOverlaps, "Draw Room Overlaps", ref roomOverlapColor);
        drawRoomOverlapFlows = ToggleWithColor(drawRoomOverlapFlows, "Draw Room Overlap Flows", ref roomOverlapFlowColor);

        Separator();

        GUILayout.Label("Joints", EditorStyles.boldLabel);
        drawJoints = GUILayout.Toggle(drawJoints, "Draw Joints");
        jointRootColor = LabeledColorFieldFixed(new GUIContent("Root", "Cross-part joint surface"), jointRootColor);
        jointStandardColor = LabeledColorFieldFixed(new GUIContent("Standard", "Internal structural joint"), jointStandardColor);
        jointCutColor = LabeledColorFieldFixed(new GUIContent("Cut point"), jointCutColor);

        drawBakedJoints = ToggleWithColor(drawBakedJoints, new GUIContent("Draw Baked Joints", "StructureParts baked directly in the ship prefab (InvisibleJoint)"), ref bakedJointColor);

        Separator();

        GUILayout.Label("Joint census overlay", EditorStyles.boldLabel);
        var prevDrawJointCensus = drawJointCensus;
        drawJointCensus = GUILayout.Toggle(drawJointCensus, new GUIContent("Draw Joint Census", "Colors parts by jointed-neighbor count or connected cluster, from joint_census.csv (PartInfoLogger)"));
        if (drawJointCensus != prevDrawJointCensus)
        {
            if (drawJointCensus)
                JointCensusGizmos.ReloadCsv();
            SceneView.RepaintAll();
        }

        drawJointCensusHull = DrawSwitch(drawJointCensusHull, "Convex Hull", "Mesh Wireframe");
        jointCensusShowByClusterSize = DrawSwitch(jointCensusShowByClusterSize, "Cluster Size", "Cluster");

        if (jointCensusShowByClusterSize)
        {
            var maxSize = Mathf.Max(1, JointCensusGizmos.MaxClusterSize);
            jointCensusSizeIndex = Mathf.Clamp(jointCensusSizeIndex, 1, maxSize);

            EditorGUILayout.BeginHorizontal();
            DrawStepperArrows(ref jointCensusSizeIndex, 1, maxSize);
            var label = $"size {jointCensusSizeIndex} ({(jointCensusSizeIndex == 1 ? "isolated" : "clusters")})";
            GUILayout.Label(new GUIContent(label, "Highlights every part belonging to ANY cluster with exactly this many parts"), GUILayout.Width(150));
            GUILayout.FlexibleSpace();
            jointCensusSizeColor = ColorFieldFixed(GUIContent.none, jointCensusSizeColor);
            EditorGUILayout.EndHorizontal();
            jointCensusSizeIndex = EditorGUILayout.IntSlider(jointCensusSizeIndex, 1, maxSize);
        }
        else
        {
            var prevClusterIndex = jointCensusClusterIndex;
            var maxIndex = Mathf.Max(0, JointCensusGizmos.ClusterCount - 1);

            EditorGUILayout.BeginHorizontal();
            DrawStepperArrows(ref jointCensusClusterIndex, 0, maxIndex);
            var clusterSize = JointCensusGizmos.GetClusterSize(jointCensusClusterIndex);
            GUILayout.Label(new GUIContent($"Cluster {jointCensusClusterIndex} / {maxIndex} ({clusterSize} part{(clusterSize == 1 ? "" : "s")})",
                "Isolates disconnected clusters of the ship's joint graph, sorted fewest parts first"), GUILayout.Width(190));
            GUILayout.FlexibleSpace();
            jointCensusClusterColor = ColorFieldFixed(GUIContent.none, jointCensusClusterColor);
            EditorGUILayout.EndHorizontal();
            jointCensusClusterIndex = EditorGUILayout.IntSlider(jointCensusClusterIndex, 0, maxIndex);
            jointCensusClusterIndex = Mathf.Clamp(jointCensusClusterIndex, 0, maxIndex);

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
        {
            SaveAll();
            SceneView.RepaintAll();
        }

        if (drawJoints && AddressableRendering.jointData.Count == 0)
            EditorGUILayout.HelpBox(
                "No joint data found. Delete Assets/EditorCache/ and click Redraw to rebuild with joint data.",
                MessageType.Warning);
    }

    static void Separator()
    {
        GUILayout.Space(6);
        var rect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        GUILayout.Space(6);
    }

    static Color ColorFieldFixed(GUIContent label, Color color) =>
        EditorGUILayout.ColorField(label, color, true, true, false, GUILayout.Width(ColorColumnWidth));

    // Label on the left, fixed-width color field flush right — matches the toggle rows' layout.
    static Color LabeledColorFieldFixed(GUIContent label, Color color)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(label);
        GUILayout.FlexibleSpace();
        var result = ColorFieldFixed(GUIContent.none, color);
        EditorGUILayout.EndHorizontal();
        return result;
    }

    // Toggle on the left, its color field on the same row to the right — no separate label line.
    static bool ToggleWithColor(bool value, string label, ref Color color) =>
        ToggleWithColor(value, new GUIContent(label), ref color);

    static bool ToggleWithColor(bool value, GUIContent label, ref Color color)
    {
        EditorGUILayout.BeginHorizontal();
        var result = GUILayout.Toggle(value, label);
        GUILayout.FlexibleSpace();
        color = ColorFieldFixed(GUIContent.none, color);
        EditorGUILayout.EndHorizontal();
        return result;
    }

    // Two-way switch rendered as a horizontal toolbar toggle group — leftLabel selected means true.
    // Uses a tinted background on the selected segment so the active side is unambiguous.
    static bool DrawSwitch(bool leftSelected, string leftLabel, string rightLabel)
    {
        var selectedIndex = leftSelected ? 0 : 1;
        var prevColor = GUI.backgroundColor;
        EditorGUILayout.BeginHorizontal();
        GUI.backgroundColor = selectedIndex == 0 ? new Color(0.3f, 0.6f, 1f, 1f) : prevColor;
        if (GUILayout.Toggle(selectedIndex == 0, leftLabel, EditorStyles.miniButtonLeft)) selectedIndex = 0;
        GUI.backgroundColor = selectedIndex == 1 ? new Color(0.3f, 0.6f, 1f, 1f) : prevColor;
        if (GUILayout.Toggle(selectedIndex == 1, rightLabel, EditorStyles.miniButtonRight)) selectedIndex = 1;
        GUI.backgroundColor = prevColor;
        EditorGUILayout.EndHorizontal();
        return selectedIndex == 0;
    }

    // Renders ◀ ▶ buttons flush against each other (no gap) that step `value` within [min, max].
    static void DrawStepperArrows(ref int value, int min, int max)
    {
        GUI.enabled = value > min;
        if (GUILayout.Button("◀", EditorStyles.miniButtonLeft, GUILayout.Width(StepperArrowWidth))) value--;
        GUI.enabled = value < max;
        if (GUILayout.Button("▶", EditorStyles.miniButtonRight, GUILayout.Width(StepperArrowWidth))) value++;
        GUI.enabled = true;
        value = Mathf.Clamp(value, min, max);
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
        EditorPrefs.SetBool(K + "jointCensusShowByClusterSize", jointCensusShowByClusterSize);
        EditorPrefs.SetInt(K + "jointCensusSizeIndex", jointCensusSizeIndex);
        SaveColor(K + "jointCensusSizeColor", jointCensusSizeColor);
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
