using System;
using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Scene-view hide/show tool built on SceneVisibilityManager (the hierarchy eye / hand icons).
// Nothing here touches GameObject.activeSelf, so the scene and prefab are never dirtied and the
// bundle is unaffected. State lives in Library/SceneVisibilityState.asset.
//
// A "part" is any real (saved) GameObject that renders something: it has a Renderer, or it is an
// AddressableLoader whose fake preview children render. Fake preview children (the DontSave clone
// trees AddressableRendering builds) are hidden together with their owner, and VisibilityFakeSync
// re-hides them after every view refresh, because a refresh destroys and rebuilds them.
public class VisibilityToolWindow : EditorWindow
{
    enum Affect { Visibility, Picking }
    enum CentreMode { Selection, SceneViewPivot, FixedPoint }
    enum FilterField { Name, Component }

    internal class Part
    {
        public GameObject go;
        public List<GameObject> fakes = new List<GameObject>();
        public Bounds bounds;
        public bool hasBounds;
    }

    class Snapshot
    {
        public Affect affect;
        public HashSet<int> off;
        public string label;
    }

    // ---- settings ----
    Affect affect = Affect.Visibility;
    GameObject limitTo;
    bool includeNonRendering;
    bool selIncludeChildren = true;

    CentreMode centreMode = CentreMode.Selection;
    Vector3 fixedPoint;
    float radius = 5f;
    float radiusMax = 50f;
    bool fromSelectionSurface = true;
    bool measureToPivot;
    bool keepSelectedVisible = true;
    bool liveArea;
    bool drawGizmo = true;

    FilterField filterField = FilterField.Name;
    string filterText = "";

    // ---- state ----
    List<Part> parts;
    bool partsDirty = true;
    readonly List<Snapshot> history = new List<Snapshot>();
    const int MaxHistory = 30;
    Vector2 scroll;
    string lastResult = "";

    static SceneVisibilityManager Svm => SceneVisibilityManager.instance;

    [MenuItem("Shipbuilder/Visibility Tool")]
    static void Open() => GetWindow<VisibilityToolWindow>("Visibility");

    void OnEnable()
    {
        EditorApplication.hierarchyChanged += MarkDirty;
        SceneView.duringSceneGui += OnSceneGUI;
        Selection.selectionChanged += OnSelectionChanged;
    }

    void OnDisable()
    {
        EditorApplication.hierarchyChanged -= MarkDirty;
        SceneView.duringSceneGui -= OnSceneGUI;
        Selection.selectionChanged -= OnSelectionChanged;
    }

    void MarkDirty() { partsDirty = true; Repaint(); }

    void OnSelectionChanged()
    {
        if (liveArea && centreMode == CentreMode.Selection) ApplyArea(AreaOp.Isolate, false);
        Repaint();
    }

    // ===================================================================== GUI

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        DrawSceneVisWarning();

        EditorGUILayout.LabelField("Scope", EditorStyles.boldLabel);
        affect = (Affect)EditorGUILayout.EnumPopup(new GUIContent("Affect",
            "Visibility = hide/show (eye icon). Picking = lock/unlock clicking in the Scene view (hand icon)."), affect);
        limitTo = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Limit to",
            "Only act on parts under this object. Empty = whole open scene(s), or the prefab being edited."),
            limitTo, typeof(GameObject), true);
        EditorGUI.BeginChangeCheck();
        includeNonRendering = EditorGUILayout.Toggle(new GUIContent("Include non-rendering",
            "Also treat objects with no renderer (rooms, collider-only objects, empties) as parts."), includeNonRendering);
        if (EditorGUI.EndChangeCheck()) partsDirty = true;

        EnsureParts();
        int offCount = parts.Count(IsOff);
        EditorGUILayout.LabelField($"{parts.Count} parts in scope, {offCount} {OffWord}");

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(affect == Affect.Visibility ? "Show all" : "Unlock all")) Run("all on", () => SetParts(parts, false));
            if (GUILayout.Button(affect == Affect.Visibility ? "Hide all" : "Lock all")) Run("all off", () => SetParts(parts, true));
            if (GUILayout.Button("Invert")) Run("invert", Invert);
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = history.Count > 0;
            if (GUILayout.Button(history.Count > 0 ? $"Back ({history[history.Count - 1].label})" : "Back")) Back();
            GUI.enabled = true;
            if (GUILayout.Button(new GUIContent("Re-hide previews",
                "Re-apply hidden state to addressable preview meshes. Runs on its own after each view refresh.")))
            {
                int n = VisibilityFakeSync.SyncAll();
                lastResult = $"Re-applied to {n} preview objects.";
            }
        }

        Space();
        DrawSelectionSection();
        Space();
        DrawAreaSection();
        Space();
        DrawFilterSection();

        if (!string.IsNullOrEmpty(lastResult))
        {
            Space();
            EditorGUILayout.HelpBox(lastResult, MessageType.None);
        }

        EditorGUILayout.EndScrollView();
    }

    void DrawSelectionSection()
    {
        EditorGUILayout.LabelField("Selection", EditorStyles.boldLabel);
        selIncludeChildren = EditorGUILayout.Toggle(new GUIContent("Include children",
            "Selected objects count together with every part under them."), selIncludeChildren);

        var sel = SelectedParts();
        EditorGUILayout.LabelField($"{sel.Count} parts selected");
        GUI.enabled = sel.Count > 0;
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(OffVerb + " selected")) Run("off selected", () => SetParts(sel, true));
            if (GUILayout.Button(OnVerb + " selected")) Run("on selected", () => SetParts(sel, false));
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(OffVerb + " all but selected")) Run("off others", () => SetParts(Others(sel), true));
            if (GUILayout.Button(OnVerb + " all but selected")) Run("on others", () => SetParts(Others(sel), false));
        }
        if (GUILayout.Button(new GUIContent("Isolate selected",
            affect == Affect.Visibility ? "Show the selection, hide everything else in scope." : "Unlock the selection, lock everything else.")))
            Run("isolate", () => { SetParts(sel, false); SetParts(Others(sel), true); });
        GUI.enabled = true;
    }

    enum AreaOp { Isolate, OffInside, OnInside, OffOutside, OnOutside }

    void DrawAreaSection()
    {
        EditorGUILayout.LabelField("By distance", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();

        centreMode = (CentreMode)EditorGUILayout.EnumPopup(new GUIContent("Centre",
            "Selection = follows what is selected. Scene View Pivot = the point the Scene view orbits. Fixed Point = the point below."), centreMode);
        if (centreMode == CentreMode.FixedPoint)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                fixedPoint = EditorGUILayout.Vector3Field(GUIContent.none, fixedPoint);
                if (GUILayout.Button("From selection", GUILayout.Width(100)))
                {
                    var b = SelectionBounds();
                    if (b.HasValue) fixedPoint = b.Value.center;
                }
            }
        }
        if (centreMode == CentreMode.Selection)
            fromSelectionSurface = EditorGUILayout.Toggle(new GUIContent("From selection surface",
                "On: radius grows out from the selection's outer box, so a big selection keeps its neighbours. Off: radius from its centre."),
                fromSelectionSurface);
        measureToPivot = EditorGUILayout.Toggle(new GUIContent("Measure to part pivot",
            "Off: a part counts as inside when any of its box is within the radius. On: only its pivot point counts."), measureToPivot);

        using (new EditorGUILayout.HorizontalScope())
        {
            radius = EditorGUILayout.Slider("Radius (m)", radius, 0f, radiusMax);
            radiusMax = Mathf.Max(1f, EditorGUILayout.FloatField(radiusMax, GUILayout.Width(45)));
        }
        keepSelectedVisible = EditorGUILayout.Toggle(new GUIContent("Keep selected on",
            "Selected parts are never hidden/locked by distance actions."), keepSelectedVisible);
        drawGizmo = EditorGUILayout.Toggle(new GUIContent("Draw radius", "Draw the radius in the Scene view, with a drag handle."), drawGizmo);

        bool changed = EditorGUI.EndChangeCheck();

        EditorGUI.BeginChangeCheck();
        liveArea = EditorGUILayout.Toggle(new GUIContent("Live isolate",
            "Re-run 'Isolate within radius' whenever the radius, centre or selection changes."), liveArea);
        if (EditorGUI.EndChangeCheck() && liveArea) { PushSnapshot("live area"); changed = true; }

        if (changed)
        {
            if (liveArea) ApplyArea(AreaOp.Isolate, false);
            SceneView.RepaintAll();
        }

        var centre = AreaCentre();
        if (centre == null)
        {
            EditorGUILayout.HelpBox("Select something to set the centre.", MessageType.Info);
            return;
        }

        if (GUILayout.Button(new GUIContent("Isolate within radius",
            $"{OnVerb} parts inside the radius, {OffVerb.ToLower()} everything else in scope.")))
            ApplyArea(AreaOp.Isolate, true);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(OffVerb + " inside")) ApplyArea(AreaOp.OffInside, true);
            if (GUILayout.Button(OnVerb + " inside")) ApplyArea(AreaOp.OnInside, true);
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(OffVerb + " outside")) ApplyArea(AreaOp.OffOutside, true);
            if (GUILayout.Button(OnVerb + " outside")) ApplyArea(AreaOp.OnOutside, true);
        }
    }

    void DrawFilterSection()
    {
        EditorGUILayout.LabelField("By name or component", EditorStyles.boldLabel);
        filterField = (FilterField)EditorGUILayout.EnumPopup("Match", filterField);
        filterText = EditorGUILayout.TextField(new GUIContent("Contains",
            "Comma-separated, case-insensitive. A part matches if any term is found. Prefix a term with ! to exclude."), filterText);

        var terms = ParseTerms(filterText, out var excludes);
        if (terms.Count == 0 && excludes.Count == 0) return;

        var matches = parts.Where(p => Matches(p, terms, excludes)).ToList();
        EditorGUILayout.LabelField($"{matches.Count} parts match");
        GUI.enabled = matches.Count > 0;
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(OffVerb)) Run("off matching", () => SetParts(matches, true));
            if (GUILayout.Button(OnVerb)) Run("on matching", () => SetParts(matches, false));
            if (GUILayout.Button("Isolate")) Run("isolate matching", () => { SetParts(matches, false); SetParts(Others(matches), true); });
            if (GUILayout.Button("Select")) Selection.objects = matches.Select(p => (UnityEngine.Object)p.go).ToArray();
        }
        GUI.enabled = true;
    }

    void DrawSceneVisWarning()
    {
        var sv = SceneView.lastActiveSceneView;
        if (sv == null || affect != Affect.Visibility) return;
        var prop = typeof(SceneView).GetProperty("sceneVisActive");
        if (prop == null) return;
        if (!(bool)prop.GetValue(sv))
        {
            EditorGUILayout.HelpBox("Scene visibility is switched off in the Scene view toolbar, so hidden parts still draw.", MessageType.Warning);
            if (GUILayout.Button("Switch it on")) { prop.SetValue(sv, true); sv.Repaint(); }
        }
    }

    string OffVerb => affect == Affect.Visibility ? "Hide" : "Lock";
    string OnVerb => affect == Affect.Visibility ? "Show" : "Unlock";
    string OffWord => affect == Affect.Visibility ? "hidden" : "locked";

    static void Space() => GUILayout.Space(8);

    // ===================================================================== actions

    void Run(string label, Action action)
    {
        EnsureParts();
        PushSnapshot(label);
        int before = parts.Count(IsOff);
        action();
        int after = parts.Count(IsOff);
        lastResult = $"{label}: {after} of {parts.Count} parts {OffWord} (was {before}).";
        SceneView.RepaintAll();
    }

    void ApplyArea(AreaOp op, bool snapshot)
    {
        EnsureParts();
        var centre = AreaCentre();
        if (centre == null) return;

        var selSet = keepSelectedVisible ? new HashSet<Part>(SelectedParts()) : new HashSet<Part>();
        var inside = new List<Part>();
        var outside = new List<Part>();
        foreach (var p in parts)
        {
            if (selSet.Contains(p)) continue;
            (Distance(centre.Value, p) <= radius ? inside : outside).Add(p);
        }

        Action act = () =>
        {
            switch (op)
            {
                case AreaOp.Isolate: SetParts(inside, false); SetParts(outside, true); SetParts(selSet, false); break;
                case AreaOp.OffInside: SetParts(inside, true); break;
                case AreaOp.OnInside: SetParts(inside, false); break;
                case AreaOp.OffOutside: SetParts(outside, true); break;
                case AreaOp.OnOutside: SetParts(outside, false); break;
            }
        };

        if (snapshot) Run(op.ToString(), act);
        else { act(); SceneView.RepaintAll(); }
    }

    void Invert()
    {
        var off = parts.Where(IsOff).ToList();
        var on = parts.Where(p => !IsOff(p)).ToList();
        SetParts(off, false);
        SetParts(on, true);
    }

    bool IsOff(Part p) => affect == Affect.Visibility ? Svm.IsHidden(p.go, false) : Svm.IsPickingDisabled(p.go, false);

    void SetParts(IEnumerable<Part> list, bool off)
    {
        var gos = new List<GameObject>();
        foreach (var p in list)
        {
            if (p.go == null) continue;
            gos.Add(p.go);
            foreach (var f in p.fakes) if (f != null) gos.Add(f);
        }
        if (gos.Count == 0) return;
        var arr = gos.ToArray();
        if (affect == Affect.Visibility) { if (off) Svm.Hide(arr, false); else Svm.Show(arr, false); }
        else { if (off) Svm.DisablePicking(arr, false); else Svm.EnablePicking(arr, false); }
    }

    void PushSnapshot(string label)
    {
        EnsureParts();
        history.Add(new Snapshot
        {
            affect = affect,
            label = label,
            off = new HashSet<int>(parts.Where(IsOff).Select(p => p.go.GetInstanceID())),
        });
        if (history.Count > MaxHistory) history.RemoveAt(0);
    }

    void Back()
    {
        if (history.Count == 0) return;
        var snap = history[history.Count - 1];
        history.RemoveAt(history.Count - 1);
        EnsureParts();
        var keep = affect;
        affect = snap.affect;
        SetParts(parts.Where(p => !snap.off.Contains(p.go.GetInstanceID())), false);
        SetParts(parts.Where(p => snap.off.Contains(p.go.GetInstanceID())), true);
        affect = keep;
        lastResult = $"Back: restored state before '{snap.label}'.";
        SceneView.RepaintAll();
    }

    // ===================================================================== parts, selection, distance

    void EnsureParts()
    {
        if (!partsDirty && parts != null && parts.All(p => p.go != null)) return;
        parts = CollectParts(ScopeRoots(limitTo), includeNonRendering);
        partsDirty = false;
    }

    static List<GameObject> ScopeRoots(GameObject limit)
    {
        if (limit != null) return new List<GameObject> { limit };
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null) return new List<GameObject> { stage.prefabContentsRoot };
        var roots = new List<GameObject>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (s.isLoaded) roots.AddRange(s.GetRootGameObjects());
        }
        return roots;
    }

    internal static List<GameObject> AllScopeRoots() => ScopeRoots(null);

    internal static List<Part> CollectParts(List<GameObject> roots, bool includeNonRendering)
    {
        var result = new List<Part>();
        foreach (var r in roots)
            if (r != null && !IsFakeRoot(r)) Walk(r.transform, includeNonRendering, result);
        return result;
    }

    static bool IsFakeRoot(GameObject g) =>
        (g.hideFlags & HideFlags.DontSave) != 0 || g.GetComponent<FakePrefabDisplay>() != null;

    static void Walk(Transform t, bool includeNonRendering, List<Part> result)
    {
        var part = new Part { go = t.gameObject };
        bool hasRenderer = t.GetComponent<Renderer>() != null;

        foreach (Transform c in t)
        {
            if (IsFakeRoot(c.gameObject))
                foreach (var ft in c.GetComponentsInChildren<Transform>(true)) part.fakes.Add(ft.gameObject);
            else
                Walk(c, includeNonRendering, result);
        }

        bool fakesRender = part.fakes.Any(f => f.GetComponent<Renderer>() != null);
        if (!hasRenderer && !fakesRender && !includeNonRendering && t.GetComponent<AddressableLoader>() == null) return;

        foreach (var r in t.GetComponents<Renderer>()) Grow(part, r.bounds);
        foreach (var f in part.fakes)
            foreach (var r in f.GetComponents<Renderer>()) Grow(part, r.bounds);
        if (!part.hasBounds)
        {
            var col = t.GetComponent<Collider>();
            if (col != null) Grow(part, col.bounds);
            else Grow(part, new Bounds(t.position, Vector3.zero));
        }
        result.Add(part);
    }

    static void Grow(Part p, Bounds b)
    {
        if (!p.hasBounds) { p.bounds = b; p.hasBounds = true; }
        else p.bounds.Encapsulate(b);
    }

    // Selected real objects; a selected preview mesh stands for the addressable that owns it.
    HashSet<GameObject> SelectedRealObjects()
    {
        var set = new HashSet<GameObject>();
        foreach (var g in Selection.gameObjects)
        {
            var t = g.transform;
            Transform realOwner = t;
            for (var a = t; a != null; a = a.parent)
                if (IsFakeRoot(a.gameObject)) realOwner = a.parent;
            if (realOwner != null) set.Add(realOwner.gameObject);
        }
        return set;
    }

    List<Part> SelectedParts()
    {
        EnsureParts();
        var sel = SelectedRealObjects();
        if (sel.Count == 0) return new List<Part>();
        var list = new List<Part>();
        foreach (var p in parts)
        {
            if (sel.Contains(p.go)) { list.Add(p); continue; }
            if (!selIncludeChildren) continue;
            for (var a = p.go.transform.parent; a != null; a = a.parent)
                if (sel.Contains(a.gameObject)) { list.Add(p); break; }
        }
        return list;
    }

    List<Part> Others(List<Part> sel)
    {
        var set = new HashSet<Part>(sel);
        return parts.Where(p => !set.Contains(p)).ToList();
    }

    Bounds? SelectionBounds()
    {
        var sel = SelectedParts();
        Bounds? b = null;
        foreach (var p in sel)
        {
            if (b == null) b = p.bounds;
            else { var bb = b.Value; bb.Encapsulate(p.bounds); b = bb; }
        }
        if (b == null)
            foreach (var g in SelectedRealObjects())
            {
                var pb = new Bounds(g.transform.position, Vector3.zero);
                if (b == null) b = pb; else { var bb = b.Value; bb.Encapsulate(pb); b = bb; }
            }
        return b;
    }

    // Centre box for distance actions: a zero-size box at a point, or the selection's box.
    Bounds? AreaCentre()
    {
        switch (centreMode)
        {
            case CentreMode.SceneViewPivot:
                var sv = SceneView.lastActiveSceneView;
                return sv != null ? new Bounds(sv.pivot, Vector3.zero) : (Bounds?)null;
            case CentreMode.FixedPoint:
                return new Bounds(fixedPoint, Vector3.zero);
            default:
                var b = SelectionBounds();
                if (b == null) return null;
                return fromSelectionSurface ? b : new Bounds(b.Value.center, Vector3.zero);
        }
    }

    float Distance(Bounds c, Part p)
    {
        var target = measureToPivot ? new Bounds(p.go.transform.position, Vector3.zero) : p.bounds;
        return BoxGap(c, target);
    }

    static float BoxGap(Bounds a, Bounds b)
    {
        float dx = Mathf.Max(0f, Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
        float dy = Mathf.Max(0f, Mathf.Max(a.min.y - b.max.y, b.min.y - a.max.y));
        float dz = Mathf.Max(0f, Mathf.Max(a.min.z - b.max.z, b.min.z - a.max.z));
        return Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    static List<string> ParseTerms(string text, out List<string> excludes)
    {
        var inc = new List<string>();
        excludes = new List<string>();
        foreach (var raw in (text ?? "").Split(','))
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;
            if (s.StartsWith("!")) { if (s.Length > 1) excludes.Add(s.Substring(1).ToLowerInvariant()); }
            else inc.Add(s.ToLowerInvariant());
        }
        return inc;
    }

    bool Matches(Part p, List<string> terms, List<string> excludes)
    {
        IEnumerable<string> keys;
        if (filterField == FilterField.Name)
        {
            var names = new List<string> { p.go.name };
            var loader = p.go.GetComponent<AddressableLoader>();
            if (loader != null && p.fakes.Count > 0) names.Add(p.fakes[0].name);
            keys = names;
        }
        else
        {
            keys = p.go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
        }
        var lower = keys.Select(k => k.ToLowerInvariant()).ToList();
        if (excludes.Any(e => lower.Any(k => k.Contains(e)))) return false;
        if (terms.Count == 0) return true;
        return terms.Any(t => lower.Any(k => k.Contains(t)));
    }

    // ===================================================================== scene view

    void OnSceneGUI(SceneView sv)
    {
        if (!drawGizmo) return;
        var centre = AreaCentre();
        if (centre == null) return;
        var c = centre.Value;

        Handles.color = new Color(0.3f, 0.8f, 1f, 0.9f);
        if (c.size == Vector3.zero)
        {
            EditorGUI.BeginChangeCheck();
            float r = Handles.RadiusHandle(Quaternion.identity, c.center, radius);
            if (EditorGUI.EndChangeCheck())
            {
                radius = Mathf.Max(0f, r);
                radiusMax = Mathf.Max(radiusMax, radius);
                if (liveArea) ApplyArea(AreaOp.Isolate, false);
                Repaint();
            }
        }
        else
        {
            Handles.DrawWireCube(c.center, c.size);
            Handles.color = new Color(0.3f, 0.8f, 1f, 0.5f);
            Handles.DrawWireCube(c.center, c.size + Vector3.one * (radius * 2f));
        }
    }
}

// Preview meshes under an AddressableLoader are destroyed and rebuilt by every view refresh,
// which drops their hidden/locked state. After hierarchy changes this copies the owner's state
// back onto its previews, whether the owner was hidden by this tool or by the hierarchy eye.
[InitializeOnLoad]
public static class VisibilityFakeSync
{
    static bool scheduled;

    static VisibilityFakeSync()
    {
        EditorApplication.hierarchyChanged += Schedule;
    }

    static void Schedule()
    {
        if (scheduled) return;
        scheduled = true;
        EditorApplication.delayCall += () => { scheduled = false; SyncAll(); };
    }

    public static int SyncAll()
    {
        var svm = SceneVisibilityManager.instance;
        int changed = 0;
        var parts = VisibilityToolWindow.CollectParts(VisibilityToolWindow.AllScopeRoots(), false);
        var hide = new List<GameObject>();
        var lockPick = new List<GameObject>();
        foreach (var p in parts)
        {
            if (p.fakes.Count == 0 || p.go == null) continue;
            bool hidden = svm.IsHidden(p.go, false);
            bool locked = svm.IsPickingDisabled(p.go, false);
            if (!hidden && !locked) continue;
            foreach (var f in p.fakes)
            {
                if (f == null) continue;
                if (hidden && !svm.IsHidden(f, false)) hide.Add(f);
                if (locked && !svm.IsPickingDisabled(f, false)) lockPick.Add(f);
            }
        }
        if (hide.Count > 0) svm.Hide(hide.ToArray(), false);
        if (lockPick.Count > 0) svm.DisablePicking(lockPick.ToArray(), false);
        changed = hide.Count + lockPick.Count;
        return changed;
    }
}
