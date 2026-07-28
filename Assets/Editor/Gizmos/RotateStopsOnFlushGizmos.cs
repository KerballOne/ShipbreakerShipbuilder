using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// STEP 1 of a from-scratch rebuild (see user direction: get free rotation + stable highlight
// working and verified before adding any snap-on-release logic). This version does ONLY two
// things:
//   1. Rotate tool works completely normally — this script never writes to any transform.
//   2. While the selected object overlaps another mesh, highlight the two contact faces
//      (orange = selected/rotating object, blue = the other part) and keep that same pair
//      shown continuously for as long as they remain overlapping (or until nothing overlaps).
// No drag-start/drag-end detection, no position pinning, no snapping — those come later, once
// this baseline is confirmed actually working.
[InitializeOnLoad]
public static class RotateStopsOnFlushGizmos
{
    const string kPrefKey = "Shipbuilder.RotateStopsOnFlushEnabled";

    public static bool Enabled
    {
        get => EditorPrefs.GetBool(kPrefKey, false);
        set => EditorPrefs.SetBool(kPrefKey, value);
    }

    struct PickedFace { public Vector3 point; public Vector3 normal; public Transform source; public Vector3 localNormal; }

    static readonly Color kFaceColorA     = new Color(1.0f, 0.55f, 0.0f, 1.00f); // orange — selected object
    static readonly Color kFaceColorAFill = new Color(1.0f, 0.55f, 0.0f, 0.20f);
    static readonly Color kFaceColorB     = new Color(0.2f, 0.55f, 1.0f, 1.00f); // blue — other object
    static readonly Color kFaceColorBFill = new Color(0.2f, 0.55f, 1.0f, 0.20f);
    static readonly Color kFaceColorFlush     = new Color(0.2f, 1.0f, 0.2f, 1.00f); // green — already flush
    static readonly Color kFaceColorFlushFill = new Color(0.2f, 1.0f, 0.2f, 0.20f);

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
        if (Tools.current != Tool.Rotate) return;
        if (Event.current.type != EventType.Repaint) return;

        var selection = Selection.transforms;
        if (selection.Length == 0) return;

        var allFilters = Object.FindObjectsOfType<MeshFilter>();

        foreach (var t in selection)
        {
            var ownFilters = t.GetComponentsInChildren<MeshFilter>();
            if (ownFilters.Length == 0) continue;

            var otherFilters = GatherOtherFilters(allFilters, t);
            var faces = FindFirstOverlapFaces(t, otherFilters);

            if (faces.HasValue)
            {
                // Already-flush (coplanar + genuinely touching) is highlighted green on BOTH
                // faces instead of orange/blue — a visually distinct "you're already there"
                // signal rather than "here's what would become flush."
                if (faces.Value.isFlush)
                {
                    DrawPickedFaceHighlight(faces.Value.a, kFaceColorFlush, kFaceColorFlushFill);
                    DrawPickedFaceHighlight(faces.Value.b, kFaceColorFlush, kFaceColorFlushFill);
                }
                else
                {
                    DrawPickedFaceHighlight(faces.Value.a, kFaceColorA, kFaceColorAFill);
                    DrawPickedFaceHighlight(faces.Value.b, kFaceColorB, kFaceColorBFill);
                }
            }
        }
    }

    static List<MeshFilter> GatherOtherFilters(MeshFilter[] allFilters, Transform root)
    {
        var result = new List<MeshFilter>();
        foreach (var mf in allFilters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            if (mf.transform.IsChildOf(root) || root.IsChildOf(mf.transform)) continue;
            result.Add(mf);
        }
        return result;
    }

    // Finds the first other individual mesh that overlaps root's mesh, and returns a face pair
    // built directly from the ACTUAL overlapping triangle pair's own normals — not a bounding-box
    // approximation. An earlier version used JointAssistWindow's bounding-box face-picking
    // (GetDirection/ReachInDir), but for elongated/rotated parts that picks whichever face has
    // the smallest box-to-box gap across the WHOLE object, which is often a completely different
    // face than where the meshes are actually touching (confirmed visually: it kept highlighting
    // the top/end face instead of the real contact corner). Reading the normal directly off the
    // real overlapping triangle pair reflects the true contact point instead.
    static (PickedFace a, PickedFace b, bool isFlush)? FindFirstOverlapFaces(Transform root, List<MeshFilter> otherFilters)
    {
        var ownFilters = root.GetComponentsInChildren<MeshFilter>();

        foreach (var otherMf in otherFilters)
        {
            if (otherMf == null || otherMf.sharedMesh == null) continue;

            var singleList = new List<MeshFilter> { otherMf };
            if (!AnyMeshesOverlap(ownFilters, singleList, out _, out _, out var hitPointB, out var hitNormalB))
                continue;

            var otherTransform = otherMf.transform;

            // Face B (the target/"wall") is fixed from the first hit — that part is already
            // correct. For face A (the rotating object's own face), instead of trusting just
            // that single first-hit triangle, collect EVERY triangle on A that overlaps ANY
            // triangle on B, cluster them into coplanar candidate faces (by shared normal), and
            // sort by how close to anti-parallel each candidate's normal is to B's — i.e. how
            // "flush-compatible" it is — THEN pick the largest by overlap area among those that
            // are closest. Sorting by area alone picked a large flat face that only grazed B's
            // plane at one corner (full triangle area counted despite minimal real overlap);
            // preferring near-anti-parallel candidates first avoids that, since the true contact
            // face is the one actually facing the wall, not whichever happens to have big
            // triangles nearby.
            var candidates = CollectCandidateFacesOnA(ownFilters, otherMf, hitNormalB);

            Debug.Log($"[RotateStopsOnFlush] '{root.name}' vs '{otherTransform.name}': {candidates.Count} candidate face(s) on A:");
            foreach (var c in candidates)
                Debug.Log($"[RotateStopsOnFlush]   candidate normal={c.normal} area={c.area:F4} point={c.point} " +
                    $"dotToFlush={Vector3.Dot(c.normal, -hitNormalB):F3} isFlush={c.isFlush}");

            if (candidates.Count == 0) continue;

            // A candidate already flush (coplanar + genuinely touching) wins outright — it IS
            // the answer, not just a strong contender to be ranked against area/dot-product
            // heuristics that exist only to guess at non-flush contact.
            CandidateFace best = default;
            bool foundFlush = false;
            foreach (var c in candidates)
            {
                if (!c.isFlush) continue;
                if (!foundFlush || c.area > best.area) { best = c; foundFlush = true; }
            }

            if (!foundFlush)
            {
                // Group candidates within a small tolerance of the best "flush-ness" dot product,
                // then pick the largest-area candidate among that closest group.
                float bestDot = float.MinValue;
                foreach (var c in candidates)
                {
                    float d = Vector3.Dot(c.normal, -hitNormalB);
                    if (d > bestDot) bestDot = d;
                }
                const float dotTolerance = 0.1f;

                float bestArea = -1f;
                foreach (var c in candidates)
                {
                    float d = Vector3.Dot(c.normal, -hitNormalB);
                    if (d < bestDot - dotTolerance) continue; // not among the most flush-compatible
                    if (c.area > bestArea) { bestArea = c.area; best = c; }
                }
            }

            var faceA = new PickedFace { point = best.point, normal = best.normal, source = root,
                localNormal = root.worldToLocalMatrix.MultiplyVector(best.normal) };
            var faceB = new PickedFace { point = hitPointB, normal = hitNormalB, source = otherTransform,
                localNormal = otherTransform.worldToLocalMatrix.MultiplyVector(hitNormalB) };

            Debug.Log($"[RotateStopsOnFlush] PICKED candidate: normal={best.normal} area={best.area:F4} " +
                $"dotToFlush={Vector3.Dot(best.normal, -hitNormalB):F3} isFlush={foundFlush}");
            return (faceA, faceB, foundFlush);
        }

        return null;
    }

    struct CandidateFace { public Vector3 normal; public Vector3 point; public float area; public bool isFlush; }

    // Scans every triangle pair between A and B's mesh (not stopping at the first hit) and
    // groups A's overlapping triangles into candidate faces by (near-)shared normal, summing
    // each group's triangle area as its "size".
    static List<CandidateFace> CollectCandidateFacesOnA(MeshFilter[] ownFilters, MeshFilter otherMf, Vector3 targetNormalB)
    {
        var groups = new List<CandidateFace>();
        const float normalGroupTolerance = 0.05f; // ~18 degrees

        var meshB = otherMf.sharedMesh;
        var vertsB = meshB.vertices; var trisB = meshB.triangles;
        var mB = otherMf.transform.localToWorldMatrix;

        foreach (var ownMf in ownFilters)
        {
            if (ownMf.sharedMesh == null) continue;
            var meshA = ownMf.sharedMesh;
            var vertsA = meshA.vertices; var trisA = meshA.triangles;
            var mA = ownMf.transform.localToWorldMatrix;

            for (int ia = 0; ia < trisA.Length; ia += 3)
            {
                var a0 = mA.MultiplyPoint3x4(vertsA[trisA[ia]]);
                var a1 = mA.MultiplyPoint3x4(vertsA[trisA[ia + 1]]);
                var a2 = mA.MultiplyPoint3x4(vertsA[trisA[ia + 2]]);
                var normalA = Vector3.Cross(a1 - a0, a2 - a0).normalized;
                float area = Vector3.Cross(a1 - a0, a2 - a0).magnitude * 0.5f;
                if (area < 1e-8f) continue;

                bool overlapsB = false;
                bool isFlush = false;
                for (int ib = 0; ib < trisB.Length && !overlapsB; ib += 3)
                {
                    var b0 = mB.MultiplyPoint3x4(vertsB[trisB[ib]]);
                    var b1 = mB.MultiplyPoint3x4(vertsB[trisB[ib + 1]]);
                    var b2 = mB.MultiplyPoint3x4(vertsB[trisB[ib + 2]]);

                    if (SegmentTriangle(a0, a1, b0, b1, b2) || SegmentTriangle(a1, a2, b0, b1, b2) ||
                        SegmentTriangle(a2, a0, b0, b1, b2) || SegmentTriangle(b0, b1, a0, a1, a2) ||
                        SegmentTriangle(b1, b2, a0, a1, a2) || SegmentTriangle(b2, b0, a0, a1, a2))
                    {
                        overlapsB = true;
                        break;
                    }

                    // SegmentTriangle looks for an edge crossing THROUGH a triangle's interior —
                    // true 3D interpenetration. Two faces that are already exactly coplanar and
                    // merely touching (not interpenetrating) never produce that crossing, since
                    // their edges lie flat in the same plane rather than piercing through it — so
                    // without this fallback, an already-flush face is invisible to the overlap
                    // test entirely, and some unrelated triangle wins by default. This narrowly
                    // covers just that case: near-anti-parallel normals (roughly coplanar-facing),
                    // near-zero perpendicular distance to B's plane (actually touching, not just
                    // nearby), and a real 2D footprint overlap when projected onto that plane.
                    if (IsCoplanarTouching(a0, a1, a2, normalA, b0, b1, b2))
                    {
                        overlapsB = true;
                        isFlush = true;
                        break;
                    }
                }
                if (!overlapsB) continue;

                var center = (a0 + a1 + a2) / 3f;
                bool merged = false;
                for (int g = 0; g < groups.Count; g++)
                {
                    if (Vector3.Dot(groups[g].normal, normalA) < 1f - normalGroupTolerance) continue;
                    var existing = groups[g];
                    existing.area += area;
                    existing.isFlush |= isFlush;
                    groups[g] = existing;
                    merged = true;
                    break;
                }
                if (!merged)
                    groups.Add(new CandidateFace { normal = normalA, point = center, area = area, isFlush = isFlush });
            }
        }

        return groups;
    }

    // Narrow check for "already flush" — see the call site comment on why SegmentTriangle alone
    // (true 3D interpenetration) misses this case entirely. Deliberately scoped tight: normals
    // must be near-anti-parallel, the triangle must sit almost exactly on B's plane, and the two
    // triangles must genuinely overlap when projected into that shared plane (not just be close).
    static bool IsCoplanarTouching(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 normalA,
        Vector3 b0, Vector3 b1, Vector3 b2)
    {
        const float normalDotThreshold = 0.995f; // ~5.7 degrees from perfectly anti-parallel
        const float planeDistThreshold = 0.005f; // meters

        Vector3 normalB = Vector3.Cross(b1 - b0, b2 - b0).normalized;
        float dot = Vector3.Dot(normalA, -normalB);
        Vector3 centerA = (a0 + a1 + a2) / 3f;
        float distToPlaneB = Mathf.Abs(Vector3.Dot(centerA - b0, normalB));

        // DEBUG: log any near-miss so we can see the real numbers instead of guessing at
        // tolerances — "near-miss" here means at least roughly facing each other and within a
        // generous debug distance, even if it fails the actual thresholds below.
        if (dot > 0.5f && distToPlaneB < 0.5f)
            Debug.Log($"[RotateStopsOnFlush] IsCoplanarTouching near-miss: dot={dot:F4} (need >={normalDotThreshold}) " +
                $"distToPlaneB={distToPlaneB:F4} (need <={planeDistThreshold})");

        if (dot < normalDotThreshold) return false;
        if (distToPlaneB > planeDistThreshold) return false;

        // Project both triangles into a 2D basis on B's plane and test for real 2D overlap
        // (separating axis theorem over each triangle's 3 edge normals) rather than assuming
        // "close enough" — two coplanar but non-overlapping triangles must not count.
        Vector3 u = Vector3.Cross(normalB, Mathf.Abs(normalB.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
        Vector3 v = Vector3.Cross(normalB, u);
        Vector2 Project(Vector3 p) => new Vector2(Vector3.Dot(p - b0, u), Vector3.Dot(p - b0, v));

        Vector2[] triA = { Project(a0), Project(a1), Project(a2) };
        Vector2[] triB = { Project(b0), Project(b1), Project(b2) };
        return Triangles2DOverlap(triA, triB);
    }

    static bool Triangles2DOverlap(Vector2[] triA, Vector2[] triB)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var tri = pass == 0 ? triA : triB;
            for (int i = 0; i < 3; i++)
            {
                Vector2 edge = tri[(i + 1) % 3] - tri[i];
                Vector2 axis = new Vector2(-edge.y, edge.x);

                float minA = float.MaxValue, maxA = float.MinValue;
                foreach (var p in triA) { float d = Vector2.Dot(p, axis); minA = Mathf.Min(minA, d); maxA = Mathf.Max(maxA, d); }
                float minB = float.MaxValue, maxB = float.MinValue;
                foreach (var p in triB) { float d = Vector2.Dot(p, axis); minB = Mathf.Min(minB, d); maxB = Mathf.Max(maxB, d); }

                if (maxA < minB || maxB < minA) return false; // separating axis found
            }
        }
        return true;
    }

    const int kMaxTrianglePairsPerProbe = 20000;

    static bool AnyMeshesOverlap(MeshFilter[] ownFilters, List<MeshFilter> otherFilters,
        out Vector3 hitPointA, out Vector3 hitNormalA, out Vector3 hitPointB, out Vector3 hitNormalB)
    {
        int pairBudget = kMaxTrianglePairsPerProbe;

        foreach (var ownMf in ownFilters)
        {
            if (ownMf.sharedMesh == null) continue;
            var ownBounds = TransformBoundsToWorld(ownMf.transform.localToWorldMatrix, ownMf.sharedMesh.bounds);

            foreach (var otherMf in otherFilters)
            {
                var otherBounds = TransformBoundsToWorld(otherMf.transform.localToWorldMatrix, otherMf.sharedMesh.bounds);
                if (!ownBounds.Intersects(otherBounds)) continue;

                if (TrianglesOverlap(ownMf, otherMf, ref pairBudget,
                        out hitPointA, out hitNormalA, out hitPointB, out hitNormalB))
                    return true;
                if (pairBudget <= 0)
                {
                    hitPointA = hitNormalA = hitPointB = hitNormalB = Vector3.zero;
                    return false;
                }
            }
        }

        hitPointA = hitNormalA = hitPointB = hitNormalB = Vector3.zero;
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

    static bool TrianglesOverlap(MeshFilter a, MeshFilter b, ref int pairBudget,
        out Vector3 hitPointA, out Vector3 hitNormalA, out Vector3 hitPointB, out Vector3 hitNormalB)
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
                if (--pairBudget <= 0)
                {
                    hitPointA = hitNormalA = hitPointB = hitNormalB = Vector3.zero;
                    return false;
                }

                var b0 = mB.MultiplyPoint3x4(vertsB[trisB[ib]]);
                var b1 = mB.MultiplyPoint3x4(vertsB[trisB[ib + 1]]);
                var b2 = mB.MultiplyPoint3x4(vertsB[trisB[ib + 2]]);

                if (SegmentTriangle(a0, a1, b0, b1, b2) || SegmentTriangle(a1, a2, b0, b1, b2) ||
                    SegmentTriangle(a2, a0, b0, b1, b2) || SegmentTriangle(b0, b1, a0, a1, a2) ||
                    SegmentTriangle(b1, b2, a0, a1, a2) || SegmentTriangle(b2, b0, a0, a1, a2))
                {
                    hitPointA = (a0 + a1 + a2) / 3f;
                    hitNormalA = Vector3.Cross(a1 - a0, a2 - a0).normalized;
                    hitPointB = (b0 + b1 + b2) / 3f;
                    hitNormalB = Vector3.Cross(b1 - b0, b2 - b0).normalized;
                    return true;
                }
            }
        }

        hitPointA = hitNormalA = hitPointB = hitNormalB = Vector3.zero;
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

    static void DrawPickedFaceHighlight(PickedFace face, Color outline, Color fill)
    {
        if (face.source == null) return;
        Vector3 wn = face.source.localToWorldMatrix.MultiplyVector(face.localNormal).normalized;
        const float normalTol = 0.15f;
        const float distTol = 0.01f;

        foreach (var mf in face.source.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            var mesh = mf.sharedMesh;
            var tris = mesh.triangles;
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var m = mf.transform.localToWorldMatrix;

            for (int ti = 0; ti < tris.Length; ti += 3)
            {
                Vector3 lv0 = verts[tris[ti]], lv1 = verts[tris[ti + 1]], lv2 = verts[tris[ti + 2]];
                Vector3 ln = normals.Length > 0
                    ? ((normals[tris[ti]] + normals[tris[ti + 1]] + normals[tris[ti + 2]]) / 3f).normalized
                    : Vector3.Cross(lv1 - lv0, lv2 - lv0).normalized;
                Vector3 triWn = m.MultiplyVector(ln).normalized;
                if (Vector3.Dot(triWn, wn) < 1f - normalTol) continue;

                Vector3 wv0 = m.MultiplyPoint3x4(lv0);
                Vector3 wv1 = m.MultiplyPoint3x4(lv1);
                Vector3 wv2 = m.MultiplyPoint3x4(lv2);
                Vector3 center = (wv0 + wv1 + wv2) / 3f;
                if (Mathf.Abs(Vector3.Dot(center - face.point, wn)) > distTol) continue;

                Handles.color = fill;
                Handles.DrawAAConvexPolygon(wv0, wv1, wv2);
                Handles.color = outline;
                Handles.DrawLine(wv0, wv1);
                Handles.DrawLine(wv1, wv2);
                Handles.DrawLine(wv2, wv0);
            }
        }
    }

    static void DrawButton(SceneView sv)
    {
        if (Tools.current != Tool.Rotate) return;

        Handles.BeginGUI();
        var wasEnabled = Enabled;
        var tip = new GUIContent("Rotate Stops on Flush",
            "STEP 1: highlight-only test build, no snapping yet");
        var prevColor = GUI.backgroundColor;
        if (wasEnabled) GUI.backgroundColor = Color.red;
        bool newEnabled = GUI.Toggle(new Rect(5, 5, 160, 22), wasEnabled, tip, "Button");
        GUI.backgroundColor = prevColor;
        if (newEnabled != wasEnabled) Enabled = newEnabled;
        Handles.EndGUI();
    }
}
