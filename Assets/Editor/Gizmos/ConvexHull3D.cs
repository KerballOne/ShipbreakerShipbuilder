using System.Collections.Generic;
using UnityEngine;

// Minimal incremental 3D convex hull (QuickHull-style), just enough to produce
// a wireframe edge list for visualization — not optimized for precision or huge point counts.
public static class ConvexHull3D
{
    struct Face
    {
        public int A, B, C;
        public Vector3 Normal;
    }

    // Meshes commonly have thousands of duplicate positions (UV seams, hard normals) and can
    // have tens of thousands of verts. Dedupe and cap the input so the incremental hull below
    // (which is roughly O(n * faces)) can't blow up into a multi-minute hang on the main thread.
    const int MaxInputPoints = 1500;

    public static List<(Vector3, Vector3)> ComputeEdges(Vector3[] points)
    {
        var edges = new List<(Vector3, Vector3)>();
        if (points == null || points.Length < 4) return edges;

        var deduped = DedupeAndCap(points);
        if (deduped.Length < 4) return edges;

        var faces = ComputeHullFaces(deduped);
        if (faces.Count == 0) return edges;
        points = deduped;

        var seen = new HashSet<(int, int)>();
        foreach (var f in faces)
        {
            AddEdge(seen, edges, points, f.A, f.B);
            AddEdge(seen, edges, points, f.B, f.C);
            AddEdge(seen, edges, points, f.C, f.A);
        }
        return edges;
    }

    static void AddEdge(HashSet<(int, int)> seen, List<(Vector3, Vector3)> edges, Vector3[] points, int i, int j)
    {
        var key = i < j ? (i, j) : (j, i);
        if (seen.Add(key))
            edges.Add((points[i], points[j]));
    }

    // Builds an initial tetrahedron, then repeatedly finds the farthest outside point
    // for each face and re-triangulates, discarding faces that become internal.
    static List<Face> ComputeHullFaces(Vector3[] points)
    {
        int n = points.Length;
        if (n < 4) return new List<Face>();

        // Find 4 non-coplanar points to seed the initial tetrahedron.
        int i0 = 0, i1 = -1, i2 = -1, i3 = -1;
        float bestDist = 0f;
        for (int i = 1; i < n; i++)
        {
            float d = (points[i] - points[i0]).sqrMagnitude;
            if (d > bestDist) { bestDist = d; i1 = i; }
        }
        if (i1 < 0) return new List<Face>();

        bestDist = 0f;
        for (int i = 0; i < n; i++)
        {
            if (i == i0 || i == i1) continue;
            float d = PointLineDistSqr(points[i], points[i0], points[i1]);
            if (d > bestDist) { bestDist = d; i2 = i; }
        }
        if (i2 < 0 || bestDist < 1e-12f) return new List<Face>();

        bestDist = 0f;
        var planeNormal = Vector3.Cross(points[i1] - points[i0], points[i2] - points[i0]).normalized;
        for (int i = 0; i < n; i++)
        {
            if (i == i0 || i == i1 || i == i2) continue;
            float d = Mathf.Abs(Vector3.Dot(points[i] - points[i0], planeNormal));
            if (d > bestDist) { bestDist = d; i3 = i; }
        }
        if (i3 < 0 || bestDist < 1e-9f) return new List<Face>();

        var faces = new List<Face>();
        AddFaceOriented(faces, points, i0, i1, i2, points[i3]);
        AddFaceOriented(faces, points, i0, i1, i3, points[i2]);
        AddFaceOriented(faces, points, i0, i2, i3, points[i1]);
        AddFaceOriented(faces, points, i1, i2, i3, points[i0]);

        // Iteratively expand: for each remaining point, if it's outside any current face,
        // remove all faces it can see and re-triangulate the resulting hole with a fan from that point.
        for (int i = 0; i < n; i++)
        {
            if (i == i0 || i == i1 || i == i2 || i == i3) continue;
            ExpandHull(faces, points, i);
        }

        return faces;
    }

    static void ExpandHull(List<Face> faces, Vector3[] points, int pointIndex)
    {
        var p = points[pointIndex];
        var visible = new List<int>();
        for (int fi = 0; fi < faces.Count; fi++)
        {
            var f = faces[fi];
            if (Vector3.Dot(p - points[f.A], f.Normal) > 1e-6f)
                visible.Add(fi);
        }
        if (visible.Count == 0) return; // point is inside/on hull

        // Collect horizon edges: edges of visible faces not shared by another visible face.
        var edgeCount = new Dictionary<(int, int), int>();
        foreach (var fi in visible)
        {
            var f = faces[fi];
            CountEdge(edgeCount, f.A, f.B);
            CountEdge(edgeCount, f.B, f.C);
            CountEdge(edgeCount, f.C, f.A);
        }

        var horizon = new List<(int, int)>();
        foreach (var fi in visible)
        {
            var f = faces[fi];
            TryAddHorizon(edgeCount, horizon, f.A, f.B);
            TryAddHorizon(edgeCount, horizon, f.B, f.C);
            TryAddHorizon(edgeCount, horizon, f.C, f.A);
        }

        // Remove visible faces (descending index to keep indices valid while removing).
        visible.Sort();
        for (int k = visible.Count - 1; k >= 0; k--)
            faces.RemoveAt(visible[k]);

        // Fan new faces from the new point to each horizon edge.
        var centroid = Vector3.zero;
        foreach (var f in faces) { centroid += points[f.A] + points[f.B] + points[f.C]; }
        var interior = faces.Count > 0 ? centroid / (faces.Count * 3) : p;

        foreach (var (a, b) in horizon)
            AddFaceOriented(faces, points, pointIndex, a, b, interior);
    }

    static void CountEdge(Dictionary<(int, int), int> edgeCount, int i, int j)
    {
        var key = i < j ? (i, j) : (j, i);
        edgeCount[key] = edgeCount.TryGetValue(key, out var c) ? c + 1 : 1;
    }

    static void TryAddHorizon(Dictionary<(int, int), int> edgeCount, List<(int, int)> horizon, int i, int j)
    {
        var key = i < j ? (i, j) : (j, i);
        if (edgeCount.TryGetValue(key, out var c) && c == 1)
            horizon.Add((i, j));
    }

    // Adds a face (a,b,c) oriented so its normal points away from `awayFrom`.
    static void AddFaceOriented(List<Face> faces, Vector3[] points, int a, int b, int c, Vector3 awayFrom)
    {
        var normal = Vector3.Cross(points[b] - points[a], points[c] - points[a]).normalized;
        if (Vector3.Dot(awayFrom - points[a], normal) > 0f)
        {
            // flip so normal points away from awayFrom
            (b, c) = (c, b);
            normal = -normal;
        }
        faces.Add(new Face { A = a, B = b, C = c, Normal = normal });
    }

    static float PointLineDistSqr(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var t = Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-12f);
        var proj = a + t * ab;
        return (p - proj).sqrMagnitude;
    }

    // Merges near-duplicate positions (quantized to a small grid) then, if still over the cap,
    // keeps only every Nth remaining point. Cheap approximation is fine — this is for visualization.
    static Vector3[] DedupeAndCap(Vector3[] points)
    {
        var seen = new HashSet<Vector3Int>();
        var result = new List<Vector3>(points.Length);
        const float cell = 0.001f; // 1mm quantization
        foreach (var p in points)
        {
            var key = new Vector3Int(
                Mathf.RoundToInt(p.x / cell),
                Mathf.RoundToInt(p.y / cell),
                Mathf.RoundToInt(p.z / cell));
            if (seen.Add(key))
                result.Add(p);
        }

        if (result.Count <= MaxInputPoints)
            return result.ToArray();

        var stride = Mathf.CeilToInt(result.Count / (float)MaxInputPoints);
        var capped = new List<Vector3>(MaxInputPoints + 4);
        for (int i = 0; i < result.Count; i += stride)
            capped.Add(result[i]);
        return capped.ToArray();
    }
}
