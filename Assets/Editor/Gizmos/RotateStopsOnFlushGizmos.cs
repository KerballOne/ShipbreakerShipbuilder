using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// When enabled, stops the user from dragging a selected object's rotation in the Scene view
// past the point where its mesh (or a child mesh) first touches another part's mesh in the
// scene. Rotation is clamped to the last known non-overlapping rotation each drag step, like
// a physical stop. See MeshOverlapTest for why this works off MeshFilter rather than Collider.
[InitializeOnLoad]
public static class RotateStopsOnFlushGizmos
{
    const string kPrefKey = "Shipbuilder.RotateStopsOnFlushEnabled";

    public static bool Enabled
    {
        get => EditorPrefs.GetBool(kPrefKey, false);
        set => EditorPrefs.SetBool(kPrefKey, value);
    }

    // Last known non-overlapping world rotation AND position per selected transform, refreshed
    // every Repaint — same reasoning as MoveCollideOnMeshGizmos: the Rotate handle applies its
    // delta internally before duringSceneGui's Repaint pass runs, so the only reliable signal
    // is diffing the transform across Repaints, not catching the raw drag event. Position is
    // tracked too because with Tool > Pivot Mode set to "Center", Unity's Rotate handle also
    // moves the object's position to keep it orbiting the selection's bounds center — ignoring
    // that (rotation-only resolve) made the object visibly jump position mid-snap.
    static readonly Dictionary<Transform, Quaternion> s_lastValidRot = new Dictionary<Transform, Quaternion>();
    static readonly Dictionary<Transform, Vector3> s_lastValidPos = new Dictionary<Transform, Vector3>();
    static Transform[] s_lastSelection = new Transform[0];

    static RotateStopsOnFlushGizmos()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    [MenuItem("Shipbuilder/Rotate Stops on Flush", priority = 183)]
    static void ToggleMenuItem() => Enabled = !Enabled;

    [MenuItem("Shipbuilder/Rotate Stops on Flush", validate = true)]
    static bool ToggleMenuItemValidate()
    {
        Menu.SetChecked("Shipbuilder/Rotate Stops on Flush", Enabled);
        return true;
    }

    static void OnSceneGUI(SceneView sv)
    {
        DrawButton(sv);

        if (!Enabled) return;
        if (Tools.current != Tool.Rotate) return; // button is only shown/toggleable in this state too
        if (Event.current.type != EventType.Repaint) return;

        var selection = Selection.transforms;
        if (selection.Length == 0) { s_lastValidRot.Clear(); s_lastValidPos.Clear(); s_lastSelection = selection; return; }

        // Selection changed — reseed instead of comparing against a stale/foreign transform's cache.
        bool selectionChanged = selection.Length != s_lastSelection.Length;
        if (!selectionChanged)
            for (int i = 0; i < selection.Length; i++)
                if (selection[i] != s_lastSelection[i]) { selectionChanged = true; break; }

        if (selectionChanged)
        {
            s_lastValidRot.Clear();
            s_lastValidPos.Clear();
            foreach (var t in selection)
            {
                s_lastValidRot[t] = t.rotation;
                s_lastValidPos[t] = t.position;
            }
            s_lastSelection = selection;
            return;
        }

        var allFilters = Object.FindObjectsOfType<MeshFilter>();

        foreach (var t in selection)
        {
            if (!s_lastValidRot.TryGetValue(t, out var lastValidRot))
            {
                lastValidRot = t.rotation;
                s_lastValidRot[t] = lastValidRot;
            }
            if (!s_lastValidPos.TryGetValue(t, out var lastValidPos))
            {
                lastValidPos = t.position;
                s_lastValidPos[t] = lastValidPos;
            }

            var wantedRot = t.rotation;
            var wantedPos = t.position;
            if (wantedRot == lastValidRot && wantedPos == lastValidPos) continue;

            var ownFilters = t.GetComponentsInChildren<MeshFilter>();
            if (ownFilters.Length == 0)
            {
                s_lastValidRot[t] = wantedRot;
                s_lastValidPos[t] = wantedPos;
                continue;
            }

            var otherFilters = MeshOverlapTest.GatherOtherFilters(allFilters, t);

            // Snap back to the known-good pose first so every probe below moves/rotates the
            // real transform (and its children's real meshes) to the exact pose being tested.
            t.rotation = lastValidRot;
            t.position = lastValidPos;

            var (resolvedRot, resolvedPos) = ResolveRotation(t, ownFilters, otherFilters,
                lastValidRot, lastValidPos, wantedRot, wantedPos);

            t.rotation = resolvedRot;
            t.position = resolvedPos;
            s_lastValidRot[t] = resolvedRot;
            s_lastValidPos[t] = resolvedPos;
        }
    }

    // Binary-searches the interpolation fraction between a known-free pose and the wanted pose
    // to find the furthest pose that doesn't overlap — instead of rejecting the whole step
    // (which would revert all the way back) when a fast mouse move rotates clean through
    // contact in one frame. Position is interpolated in lockstep with rotation (both directly
    // between the two known endpoints Unity already computed) because with Tool > Pivot Mode
    // set to "Center", the Rotate handle also moves position to keep the object orbiting the
    // selection's bounds center — a rotation-only resolve made it visibly jump position mid-
    // snap. A straight Lerp cuts the corner of the true circular arc slightly, but the error is
    // negligible at the small per-probe deltas this binary search deals with.
    const int kBinarySearchSteps = 10; // ~1/1024th of the step size; each step re-runs the mesh test
    static (Quaternion, Vector3) ResolveRotation(Transform t, MeshFilter[] ownFilters, List<MeshFilter> otherFilters,
        Quaternion fromRot, Vector3 fromPos, Quaternion toRot, Vector3 toPos)
    {
        (Quaternion, Vector3) AtFraction(float f) =>
            (Quaternion.Slerp(fromRot, toRot, f), Vector3.Lerp(fromPos, toPos, f));

        t.rotation = toRot; t.position = toPos;
        if (!MeshOverlapTest.MeshesOverlap(ownFilters, otherFilters)) return (toRot, toPos);

        float lo = 0f, hi = 1f; // lo = free fraction, hi = overlapping fraction
        for (int i = 0; i < kBinarySearchSteps; i++)
        {
            float mid = (lo + hi) * 0.5f;
            var (r, p) = AtFraction(mid);
            t.rotation = r; t.position = p;
            if (MeshOverlapTest.MeshesOverlap(ownFilters, otherFilters)) hi = mid;
            else lo = mid;
        }

        var (resultRot, resultPos) = AtFraction(lo);
        t.rotation = resultRot; t.position = resultPos;
        return (resultRot, resultPos);
    }

    static void DrawButton(SceneView sv)
    {
        if (Tools.current != Tool.Rotate) return; // only relevant while rotating

        Handles.BeginGUI();
        var wasEnabled = Enabled;
        var tip = new GUIContent("Rotate Stops on Flush",
            "Block rotating selected objects through other parts' meshes");
        var prevColor = GUI.backgroundColor;
        if (wasEnabled) GUI.backgroundColor = Color.red;
        bool newEnabled = GUI.Toggle(new Rect(5, 5, 160, 22), wasEnabled, tip, "Button");
        GUI.backgroundColor = prevColor;
        if (newEnabled != wasEnabled) Enabled = newEnabled;
        Handles.EndGUI();
    }
}
