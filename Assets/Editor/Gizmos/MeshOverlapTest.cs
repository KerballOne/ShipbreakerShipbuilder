using System.Collections.Generic;
using UnityEngine;

// Shared mesh-vs-mesh overlap test used by MoveCollideOnMeshGizmos and RotateStopsOnFlushGizmos.
// Works directly off MeshFilter/MeshRenderer rather than Collider, since addressable parts only
// get a real Collider baked on at runtime — in the editor they're just a MeshRenderer+MeshFilter
// preview (see AddressableRendering's FakePrefabDisplay), so a Collider-based (PhysX) check
// silently does nothing for them.
static class MeshOverlapTest
{
    // Per-probe triangle-pair budget across the whole call — broad-phase bounds narrow the
    // candidates down first, but a large/high-poly part pair can still generate more pairs
    // than is safe to fully scan every Repaint. Matches the accepted tradeoff already used by
    // JointAssistWindow's mesh check: if the budget runs out, treat the remainder as non-
    // overlapping (a rare false negative) rather than stalling the editor mid-drag.
    const int kMaxTrianglePairsPerProbe = 20000;

    public static List<MeshFilter> GatherOtherFilters(MeshFilter[] allFilters, Transform root)
    {
        var otherFilters = new List<MeshFilter>();
        foreach (var mf in allFilters)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            if (mf.transform.IsChildOf(root) || root.IsChildOf(mf.transform)) continue;
            otherFilters.Add(mf);
        }
        return otherFilters;
    }

    public static bool MeshesOverlap(MeshFilter[] ownFilters, List<MeshFilter> otherFilters)
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
}
