using UnityEngine;
using UnityEditor;
using BBI.Unity.Game;

public class GameRenderWindow : EditorWindow
{
    public static int maxLoopDepth = 8;
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

    const string K = "GRW.";

    [MenuItem("Shipbreaker/Show Render Controller", priority = 100)]
    public static void ShowRenderController()
    {
        EditorWindow.CreateInstance<GameRenderWindow>().Show();
    }

    void OnEnable()
    {
        maxLoopDepth          = EditorPrefs.GetInt(K + "maxLoopDepth", maxLoopDepth);
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
    }

    void OnGUI()
    {
        if (GUILayout.Button("Redraw"))
            AddressableRendering.UpdateViewList();

        if (GUILayout.Button("Clear View"))
            AddressableRendering.ClearView();

        EditorGUI.BeginChangeCheck();

        GUILayout.Label("Max render depth", EditorStyles.boldLabel);
        maxLoopDepth = EditorGUILayout.IntField(maxLoopDepth);

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
        EditorGUILayout.HelpBox(
            "Cyan = root (cross-part joint surface)\nGreen = internal structural\nOrange = cut point",
            MessageType.None);
        GUILayout.Label("Root color", EditorStyles.label);
        jointRootColor = EditorGUILayout.ColorField(jointRootColor);
        GUILayout.Label("Standard color", EditorStyles.label);
        jointStandardColor = EditorGUILayout.ColorField(jointStandardColor);
        GUILayout.Label("Cut point color", EditorStyles.label);
        jointCutColor = EditorGUILayout.ColorField(jointCutColor);

        GUILayout.Label("Baked joints (InvisibleJoint)", EditorStyles.boldLabel);
        drawBakedJoints = GUILayout.Toggle(drawBakedJoints, "Draw Baked Joints");
        EditorGUILayout.HelpBox("Solid-filled cubes for StructureParts baked directly in the ship prefab (e.g. InvisibleJoint nodes).", MessageType.None);
        GUILayout.Label("Color", EditorStyles.label);
        bakedJointColor = EditorGUILayout.ColorField(bakedJointColor);

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
