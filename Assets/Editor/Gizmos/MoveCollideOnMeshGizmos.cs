using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// When enabled, stops the user from dragging a selected object's position in the Scene view
// past the point where its mesh (or a child mesh) first touches another part's mesh in the
// scene. Position is clamped to the last known non-overlapping position each drag step, like
// a physical stop. Works directly off MeshFilter/MeshRenderer rather than Collider, since
// addressable parts only get a real Collider baked on at runtime — in the editor they're just
// a MeshRenderer+MeshFilter preview (see AddressableRendering's FakePrefabDisplay), so a
// Collider-based (PhysX) check silently does nothing for them.
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
        DrawButton(sv);

        if (!Enabled) return;
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

            var otherFilters = new List<MeshFilter>();
            foreach (var mf in allFilters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.transform.IsChildOf(t) || t.IsChildOf(mf.transform)) continue;
                otherFilters.Add(mf);
            }

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
        if (!MeshesOverlap(ownFilters, otherFilters)) return from + axisDelta;

        float lo = 0f, hi = 1f; // lo = free fraction, hi = overlapping fraction
        for (int i = 0; i < kBinarySearchSteps; i++)
        {
            float mid = (lo + hi) * 0.5f;
            t.position = from + axisDelta * mid;
            if (MeshesOverlap(ownFilters, otherFilters)) hi = mid;
            else lo = mid;
        }

        var result = from + axisDelta * lo;
        t.position = result;
        return result;
    }

    // Per-probe triangle-pair budget across the whole call — broad-phase bounds narrow the
    // candidates down first, but a large/high-poly part pair can still generate more pairs
    // than is safe to fully scan every Repaint. Matches the accepted tradeoff already used by
    // JointAssistWindow's mesh check: if the budget runs out, treat the remainder as non-
    // overlapping (a rare false negative) rather than stalling the editor mid-drag.
    const int kMaxTrianglePairsPerProbe = 20000;

    static bool MeshesOverlap(MeshFilter[] ownFilters, List<MeshFilter> otherFilters)
    {
        int pairBudget = kMaxTrianglePairsPerProbe;

        foreach (var ownMf in ownFilters)
        {
            if (ownMf.sharedMesh == null) continue;
            var ownBounds = TransformBoundsToWorld(ownMf.transform.localToWorldMatrix, ownMf.sharedMesh.bounds);

            foreach (var otherMf in otherFilters)
            {
                var otherBounds = TransformBoundsToWorld(otherMf.transform.localToWorldMatrix, otherMf.sharedMesh.bounds);
                if (!ownBounds.Intersects(otherBounds)) continue; // broad phase

                if (TrianglesOverlap(ownMf, otherMf, ref pairBudget))
                    return true;
                if (pairBudget <= 0) return false;
            }
        }

        return false;
    }

    static Bounds TransformBoundsToWorld(Matrix4x4 m, Bounds localBounds)
    {
        var c = localBounds.center; var e = localBounds.extents;
        var corners = new Vector3[8];
        int i = 0;
        for (int sx = -1; sx <= 1; sx += 2)
        for (int sy = -1; sy <= 1; sy += 2)
        for (int sz = -1; sz <= 1; sz += 2)
            corners[i++] = m.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(sx, sy, sz)));

        var b = new Bounds(corners[0], Vector3.zero);
        for (int k = 1; k < 8; k++) b.Encapsulate(corners[k]);
        return b;
    }

    // Narrow phase: two meshes overlap if any edge of a triangle from one crosses a triangle
    // from the other (segment-vs-triangle, both directions covers full triangle-triangle
    // intersection). Reuses the same Möller–Trumbore-style test JointAssistWindow uses for its
    // ray-based joint check, just clamped to the edge's own length instead of an infinite ray.
    static bool TrianglesOverlap(MeshFilter a, MeshFilter b, ref int pairBudget)
    {
        var meshA = a.sharedMesh; var meshB = b.sharedMesh;
        var vertsA = meshA.vertices; var trisA = meshA.triangles;
        var vertsB = meshB.vertices; var trisB = meshB.triangles;
        var mA = a.transform.localToWorldMatrix; var mB = b.transform.localToWorldMatrix;

        for (int ia = 0; ia < trisA.Length; ia += 3)
        {
            var a0 = mA.MultiplyPoint3x4(vertsA[trisA[ia]]);
            var a1 = mA.MultiplyPoint3x4(vertsA[trisA[ia + 1]]);
            var a2 = mA.MultiplyPoint3x4(vertsA[trisA[ia + 2]]);

            for (int ib = 0; ib < trisB.Length; ib += 3)
            {
                if (--pairBudget <= 0) return false;

                var b0 = mB.MultiplyPoint3x4(vertsB[trisB[ib]]);
                var b1 = mB.MultiplyPoint3x4(vertsB[trisB[ib + 1]]);
                var b2 = mB.MultiplyPoint3x4(vertsB[trisB[ib + 2]]);

                if (SegmentTriangle(a0, a1, b0, b1, b2)) return true;
                if (SegmentTriangle(a1, a2, b0, b1, b2)) return true;
                if (SegmentTriangle(a2, a0, b0, b1, b2)) return true;
                if (SegmentTriangle(b0, b1, a0, a1, a2)) return true;
                if (SegmentTriangle(b1, b2, a0, a1, a2)) return true;
                if (SegmentTriangle(b2, b0, a0, a1, a2)) return true;
            }
        }

        return false;
    }

    static bool SegmentTriangle(Vector3 p0, Vector3 p1, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        Vector3 d = p1 - p0;
        float len = d.magnitude;
        if (len < 1e-9f) return false;
        d /= len;

        Vector3 e1 = v1 - v0, e2 = v2 - v0;
        Vector3 h = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, h);
        if (det > -1e-6f && det < 1e-6f) return false;
        float f = 1f / det;
        Vector3 s = p0 - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = f * Vector3.Dot(d, q);
        if (v < 0f || u + v > 1f) return false;
        float t = f * Vector3.Dot(e2, q);
        return t > 1e-6f && t < len;
    }

    static void DrawButton(SceneView sv)
    {
        if (Tools.current != Tool.Move) return; // only relevant while dragging position

        Handles.BeginGUI();
        var wasEnabled = Enabled;
        var tip = new GUIContent("Move Collides on Mesh",
            "Block dragging selected objects through other parts' meshes");
        var prevColor = GUI.backgroundColor;
        if (wasEnabled) GUI.backgroundColor = Color.red;
        bool newEnabled = GUI.Toggle(new Rect(5, 5, 160, 22), wasEnabled, tip, "Button");
        GUI.backgroundColor = prevColor;
        if (newEnabled != wasEnabled) Enabled = newEnabled;
        Handles.EndGUI();
    }
}
