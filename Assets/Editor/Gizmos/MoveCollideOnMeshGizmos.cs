using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// When enabled, stops the user from dragging a selected object's position in the Scene view
// past the point where its mesh (or a child mesh) first touches another part's mesh in the
// scene. Position is clamped to the last known non-overlapping position each drag step, like
// a physical stop. See MeshOverlapTest for why this works off MeshFilter rather than Collider.
[InitializeOnLoad]
public static class MoveCollideOnMeshGizmos
{
    const string kPrefKey = "Shipbuilder.MoveCollideOnMeshEnabled";

    public static bool Enabled
    {
        get => EditorPrefs.GetBool(kPrefKey, false);
        set => EditorPrefs.SetBool(kPrefKey, value);
    }

    // Last known non-overlapping world position per selected transform, refreshed every
    // Repaint. Position handles (and other scene-view drag tools) apply their delta to
    // transform.position internally before duringSceneGui's Repaint pass runs, consuming the
    // raw MouseDrag event via their own hotControl — so the only reliable way to see the
    // result of a drag is to diff position across Repaint passes, not to catch MouseDrag.
    static readonly Dictionary<Transform, Vector3> s_lastValidPos = new Dictionary<Transform, Vector3>();
    static Transform[] s_lastSelection = new Transform[0];

    // Holding Ctrl temporarily INVERTS whatever the toggle button's persistent state is (so
    // either click the button to leave it on, or hold Ctrl for a quick momentary override in
    // either direction) — tracked via raw KeyDown/KeyUp rather than Event.current.control,
    // since control state must be observed on every event type (key events aren't Repaint) and
    // the Scene view needs an explicit repaint request to react immediately without also
    // requiring mouse movement to notice the key changed.
    static bool s_ctrlHeld;

    static MoveCollideOnMeshGizmos()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    [MenuItem("Shipbuilder/Move Collides on Mesh", priority = 182)]
    static void ToggleMenuItem() => Enabled = !Enabled;

    [MenuItem("Shipbuilder/Move Collides on Mesh", validate = true)]
    static bool ToggleMenuItemValidate()
    {
        Menu.SetChecked("Shipbuilder/Move Collides on Mesh", Enabled);
        return true;
    }

    static void OnSceneGUI(SceneView sv)
    {
        // Track Ctrl on every event type (not just Repaint) so a key press/release is noticed
        // immediately, then request a repaint so the effect updates without requiring mouse
        // movement to trigger the next Repaint naturally. Also fall back to Event.current.control
        // during Repaint as a safety net: if focus leaves the Scene view while Ctrl is held (e.g.
        // clicking into the Inspector), the KeyUp event never reaches duringSceneGui and
        // s_ctrlHeld would otherwise stay stuck true — Repaint's own modifier snapshot corrects it.
        var e = Event.current;
        if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.LeftControl || e.keyCode == KeyCode.RightControl) && !s_ctrlHeld)
        {
            s_ctrlHeld = true;
            sv.Repaint();
        }
        else if (e.type == EventType.KeyUp && (e.keyCode == KeyCode.LeftControl || e.keyCode == KeyCode.RightControl) && s_ctrlHeld)
        {
            s_ctrlHeld = false;
            sv.Repaint();
        }
        else if (e.type == EventType.Repaint && s_ctrlHeld != e.control)
        {
            s_ctrlHeld = e.control;
        }

        DrawButton(sv);

        bool effectiveEnabled = s_ctrlHeld ? !Enabled : Enabled;
        if (!effectiveEnabled) return;
        if (Tools.current != Tool.Move) return; // button is only shown/toggleable in this state too
        if (Event.current.type != EventType.Repaint) return;

        var selection = Selection.transforms;
        if (selection.Length == 0) { s_lastValidPos.Clear(); s_lastSelection = selection; return; }

        // Selection changed — reseed instead of comparing against a stale/foreign transform's cache.
        bool selectionChanged = selection.Length != s_lastSelection.Length;
        if (!selectionChanged)
            for (int i = 0; i < selection.Length; i++)
                if (selection[i] != s_lastSelection[i]) { selectionChanged = true; break; }

        if (selectionChanged)
        {
            s_lastValidPos.Clear();
            foreach (var t in selection)
                s_lastValidPos[t] = t.position;
            s_lastSelection = selection;
            return;
        }

        // All other MeshFilters in the scene, gathered once per Repaint rather than once per
        // probe — this is the expensive-ish part (Object.FindObjectsOfType), and every probe
        // below reuses the same candidate list since only the dragged transform(s) move.
        var allFilters = Object.FindObjectsOfType<MeshFilter>();

        foreach (var t in selection)
        {
            if (!s_lastValidPos.TryGetValue(t, out var lastValid))
            {
                lastValid = t.position;
                s_lastValidPos[t] = lastValid;
            }

            var wanted = t.position;
            if (wanted == lastValid) continue;

            var ownFilters = t.GetComponentsInChildren<MeshFilter>();
            if (ownFilters.Length == 0) { s_lastValidPos[t] = wanted; continue; }

            var otherFilters = MeshOverlapTest.GatherOtherFilters(allFilters, t);

            // Snap back to the known-good pose first so every probe below moves the real
            // transform (and its children's real meshes) to the exact position being tested.
            t.position = lastValid;

            // Resolve the drag per axis rather than all-or-nothing: a part resting flush
            // against another (e.g. sitting on top, blocked along Y) must still be able to
            // slide along X/Z. Each world axis is tested independently against lastValid, so
            // being blocked on one axis doesn't revert progress already made on the others.
            var resolved = lastValid;
            var delta = wanted - lastValid;

            resolved = ResolveAxis(t, ownFilters, otherFilters, resolved, new Vector3(delta.x, 0, 0));
            resolved = ResolveAxis(t, ownFilters, otherFilters, resolved, new Vector3(0, delta.y, 0));
            resolved = ResolveAxis(t, ownFilters, otherFilters, resolved, new Vector3(0, 0, delta.z));

            t.position = resolved;
            s_lastValidPos[t] = resolved;
        }
    }

    // Binary-searches along a single-axis delta from a known-free 'from' position to find the
    // furthest point along that axis that doesn't overlap — instead of rejecting the whole step
    // (which would revert all the way back) when a fast mouse move jumps clean through a surface.
    const int kBinarySearchSteps = 10; // ~1/1024th of the step size; each step re-runs the mesh test
    static Vector3 ResolveAxis(Transform t, MeshFilter[] ownFilters, List<MeshFilter> otherFilters,
        Vector3 from, Vector3 axisDelta)
    {
        if (axisDelta == Vector3.zero) return from;

        t.position = from + axisDelta;
        if (!MeshOverlapTest.MeshesOverlap(ownFilters, otherFilters)) return from + axisDelta;

        float lo = 0f, hi = 1f; // lo = free fraction, hi = overlapping fraction
        for (int i = 0; i < kBinarySearchSteps; i++)
        {
            float mid = (lo + hi) * 0.5f;
            t.position = from + axisDelta * mid;
            if (MeshOverlapTest.MeshesOverlap(ownFilters, otherFilters)) hi = mid;
            else lo = mid;
        }

        var result = from + axisDelta * lo;
        t.position = result;
        return result;
    }

    static void DrawButton(SceneView sv)
    {
        if (Tools.current != Tool.Move) return; // only relevant while dragging position

        Handles.BeginGUI();
        var wasEnabled = Enabled;
        var tip = new GUIContent("Move Collides on Mesh",
            "Block dragging selected objects through other parts' meshes. Hold Ctrl to temporarily invert.");
        var prevColor = GUI.backgroundColor;
        // Red reflects the EFFECTIVE (Ctrl-inverted) state, since that's what's actually acting
        // on the drag right now — not just the persistent toggle, so holding Ctrl gives visible
        // feedback even without clicking the button.
        if (s_ctrlHeld ? !wasEnabled : wasEnabled) GUI.backgroundColor = Color.red;
        bool newEnabled = GUI.Toggle(new Rect(5, 5, 160, 22), wasEnabled, tip, "Button");
        GUI.backgroundColor = prevColor;
        if (newEnabled != wasEnabled) Enabled = newEnabled;
        Handles.EndGUI();
    }
}
