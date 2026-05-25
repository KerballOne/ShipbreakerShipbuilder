#if UNITY_EDITOR
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

[InitializeOnLoad]
public static class ShipbreakerToolbar
{
    static readonly System.Type ToolbarType =
        typeof(Editor).Assembly.GetType("UnityEditor.Toolbar");
    static readonly System.Type GUIViewType =
        typeof(Editor).Assembly.GetType("UnityEditor.GUIView");

    static ScriptableObject s_Toolbar;

    static ShipbreakerToolbar()
    {
        EditorApplication.update += Init;
    }

    static void Init()
    {
        if (s_Toolbar != null) return;

        var found = Resources.FindObjectsOfTypeAll(ToolbarType);
        if (found.Length == 0) return;
        s_Toolbar = (ScriptableObject)found[0];

        var backend = GUIViewType
            .GetField("m_WindowBackend", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(s_Toolbar);
        if (backend == null) return;

        var visualTree = backend.GetType()
            .GetProperty("visualTree", BindingFlags.Public | BindingFlags.Instance)
            ?.GetValue(backend) as VisualElement;
        if (visualTree == null) return;

        VisualElement overlay = null;
        foreach (var child in visualTree.Children())
            if (child.name.StartsWith("rootVisualContainer"))
                { overlay = child; break; }
        if (overlay == null) return;

        var btn = new IMGUIContainer(DrawButton);
        btn.style.position = Position.Absolute;
        btn.style.left     = 500;
        btn.style.top      = 5;
        btn.style.width    = 34;
        btn.style.height   = 22;
        overlay.Add(btn);
    }

    static void DrawButton()
    {
        var tip = new GUIContent("↺", "Force View Refresh  (Ctrl+Alt+R)");
        if (GUI.Button(new Rect(0, 0, 34, 22), tip, EditorStyles.toolbarButton))
            EditorApplication.ExecuteMenuItem("Shipbreaker/Force View Refresh");
    }
}
#endif
