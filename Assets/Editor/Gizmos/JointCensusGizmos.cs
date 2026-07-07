using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBI.Unity.Game;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Draws a colored tint over each part in the scene based on its jointed-neighbor count
// or its connected structural cluster, as dumped to joint_census.csv by PartInfoLogger's
// runtime JointCensus feature.
[InitializeOnLoad]
public static class JointCensusGizmos
{
    struct PartInfo
    {
        public int NeighborCount;
        public int ClusterId;   // -1 if unknown/no cluster
        public int ClusterSize;
    }

    static Dictionary<string, PartInfo> s_parts = new Dictionary<string, PartInfo>();
    static bool s_loaded = false;
    public static bool LoadFailed { get; private set; } = false;

    // Cluster IDs from the CSV are already ordered smallest-first by PartInfoLogger;
    // this is just the count of distinct clusters found, for clamping the UI stepper.
    public static int ClusterCount { get; private set; } = 0;

    static Dictionary<int, int> s_clusterSizes = new Dictionary<int, int>();
    public static int GetClusterSize(int clusterId) =>
        s_clusterSizes.TryGetValue(clusterId, out var size) ? size : 0;

    // All part names (from the CSV, independent of whether they currently exist in the scene)
    // belonging to a given cluster — used for diagnostics when scene matching comes up short.
    static Dictionary<int, List<string>> s_clusterNames = new Dictionary<int, List<string>>();
    public static List<string> GetClusterPartNames(int clusterId) =>
        s_clusterNames.TryGetValue(clusterId, out var names) ? names : new List<string>();

    static JointCensusGizmos()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    public static void ReloadCsv()
    {
        s_loaded = false;
        s_parts.Clear();
        s_clusterSizes.Clear();
        s_clusterNames.Clear();
        ClusterCount = 0;
        LoadFailed = false;
        TryLoad();
    }

    static void TryLoad()
    {
        if (s_loaded) return;
        s_loaded = true;

        try
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "joint_census.csv"));
            if (!File.Exists(path))
            {
                LoadFailed = true;
                return;
            }

            // Name -> has a StructurePart component in the current scene. Non-structural
            // GameObjects that merely share a name with a CSV row (e.g. trigger colliders)
            // must not count as real parts for clustering/highlighting purposes.
            var hasStructurePart = new Dictionary<string, bool>();
            foreach (var root in GetActiveRootObjects())
            {
                foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(false))
                {
                    var goName = meshFilter.gameObject.name.Replace("(Clone)", "").Trim();
                    var isSp = meshFilter.GetComponent<StructurePart>() != null;
                    if (isSp || !hasStructurePart.ContainsKey(goName))
                        hasStructurePart[goName] = isSp;
                }
            }

            var lines = File.ReadAllLines(path);
            // rawClusterId -> row names in that cluster (excluding IJ markers)
            var rawClusters = new Dictionary<int, List<string>>();
            var pending = new List<(string Name, int Count, int RawClusterId)>();

            for (int i = 1; i < lines.Length; i++) // skip header
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var fields = SplitCsvLine(line);
                if (fields.Count < 2) continue;

                var name = fields[0];
                if (!int.TryParse(fields[1], out var count)) continue;

                // InvisibleJoint bridge markers aren't real parts — PartInfoLogger's neighbor
                // counts already exclude them, but its cluster union-find can still lump
                // orphaned/unjointed IJs together, so filter them out here too.
                if (name.StartsWith("InvisibleJoint", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip rows for names that no longer exist in the scene as a StructurePart —
                // either stale/removed parts, or non-structural GameObjects (e.g. trigger/holo
                // colliders) that merely share a name with a CSV row. Only keep rows that
                // resolve to a real, current StructurePart.
                if (!hasStructurePart.TryGetValue(name, out var isSp) || !isSp)
                    continue;

                int rawClusterId = -1;
                if (fields.Count >= 4 && int.TryParse(fields[2], out var cid))
                    rawClusterId = cid;

                pending.Add((name, count, rawClusterId));
                if (rawClusterId >= 0)
                {
                    if (!rawClusters.TryGetValue(rawClusterId, out var members))
                        rawClusters[rawClusterId] = members = new List<string>();
                    members.Add(name);
                }
            }

            // Renumber surviving (non-empty, non-IJ-only) clusters densely, smallest first,
            // so the picker never shows a cluster that has nothing left to highlight.
            var orderedRaw = rawClusters.Keys.OrderBy(k => rawClusters[k].Count).ToList();
            var rawToNew = new Dictionary<int, int>();
            for (int newId = 0; newId < orderedRaw.Count; newId++)
            {
                var rawId = orderedRaw[newId];
                rawToNew[rawId] = newId;
                s_clusterSizes[newId] = rawClusters[rawId].Count;
                s_clusterNames[newId] = rawClusters[rawId];
            }

            foreach (var (name, count, rawClusterId) in pending)
            {
                int newClusterId = -1, clusterSize = 0;
                if (rawClusterId >= 0 && rawToNew.TryGetValue(rawClusterId, out var mapped))
                {
                    newClusterId = mapped;
                    clusterSize = s_clusterSizes[mapped];
                }
                s_parts[name] = new PartInfo { NeighborCount = count, ClusterId = newClusterId, ClusterSize = clusterSize };
            }

            ClusterCount = orderedRaw.Count;
            LoadFailed = false;
        }
        catch
        {
            LoadFailed = true;
            s_parts.Clear();
            s_clusterSizes.Clear();
            s_clusterNames.Clear();
            ClusterCount = 0;
        }
    }

    // Splits a CSV line honoring simple double-quote escaping, matching CsvEscape in PartInfoLogger.
    static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            string field;
            if (i < line.Length && line[i] == '"')
            {
                var end = line.IndexOf('"', i + 1);
                while (end >= 0 && end + 1 < line.Length && line[end + 1] == '"')
                    end = line.IndexOf('"', end + 2);
                if (end < 0) end = line.Length - 1;
                field = line.Substring(i + 1, end - i - 1).Replace("\"\"", "\"");
                i = end + 2; // skip closing quote + comma
            }
            else
            {
                var next = line.IndexOf(',', i);
                if (next < 0) next = line.Length;
                field = line.Substring(i, next - i);
                i = next + 1;
            }
            fields.Add(field);
            if (i > line.Length) break;
        }
        return fields;
    }

    // Neighbor count is clamped to [0,5] for bucket matching — 5 means "5 or more".
    static bool MatchesSelectedNeighborCount(int count)
    {
        var selected = GameRenderWindow.jointCensusNeighborIndex;
        var clamped = Mathf.Min(count, 5);
        return clamped == selected;
    }

    static GameObject[] GetActiveRootObjects()
    {
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null)
            return stage.scene.GetRootGameObjects();
        return SceneManager.GetActiveScene().GetRootGameObjects();
    }

    // Finds every scene GameObject belonging to the given cluster, along with a combined
    // world-space bounds (null if none had a Renderer).
    static (List<GameObject> Objects, Bounds? Bounds) GetClusterObjects(int clusterId)
    {
        var matchedObjects = new List<GameObject>();
        Bounds? bounds = null;
        foreach (var root in GetActiveRootObjects())
        {
            foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(false))
            {
                if (meshFilter.GetComponent<StructurePart>() == null) continue;

                var name = meshFilter.gameObject.name.Replace("(Clone)", "").Trim();
                if (!s_parts.TryGetValue(name, out var info)) continue;
                if (info.ClusterId != clusterId) continue;
                matchedObjects.Add(meshFilter.gameObject);

                var renderer = meshFilter.GetComponent<Renderer>();
                if (renderer == null) continue;

                if (bounds == null) bounds = renderer.bounds;
                else { var b = bounds.Value; b.Encapsulate(renderer.bounds); bounds = b; }
            }
        }
        return (matchedObjects, bounds);
    }

    // Selects every GameObject in the given cluster in the Hierarchy, without touching the Scene view camera.
    public static void SelectClusterObjects(int clusterId)
    {
        TryLoad();
        if (s_parts.Count == 0) return;

        var (matchedObjects, _) = GetClusterObjects(clusterId);
        if (matchedObjects.Count > 0)
            Selection.objects = matchedObjects.ToArray();
    }

    // Fits the Scene view camera to the bounds of every part in the given cluster.
    // Returns a short status string describing what happened, for surfacing in the UI.
    public static string FrameCluster(int clusterId)
    {
        TryLoad();
        if (s_parts.Count == 0) return "No joint_census.csv data loaded.";

        var (matchedObjects, bounds) = GetClusterObjects(clusterId);

        if (bounds == null)
            return matchedObjects.Count == 0
                ? $"No scene GameObjects matched cluster {clusterId} by name."
                : $"Matched {matchedObjects.Count} part(s) in cluster {clusterId} but none had a Renderer to frame.";

        Selection.objects = matchedObjects.ToArray();

        var sceneView = SceneView.lastActiveSceneView;
        if (sceneView == null)
        {
            foreach (SceneView sv in SceneView.sceneViews)
            {
                sceneView = sv;
                break;
            }
        }
        if (sceneView == null)
            return "No open Scene View window to frame.";

        // Expand the bounds before framing so the camera backs off ~2x rather than
        // fitting tightly — easier to see a tiny single-part cluster in context.
        var expanded = bounds.Value;
        expanded.Expand(expanded.size.magnitude * 0.5f);
        sceneView.Frame(expanded, false);
        sceneView.Repaint();
        return $"Framed {matchedObjects.Count} part(s) in cluster {clusterId}.";
    }

    // Convex hull edges (local space) cached by mesh instance ID — hull computation is
    // too expensive to redo every OnSceneGUI call.
    static Dictionary<int, List<(Vector3, Vector3)>> s_hullCache = new Dictionary<int, List<(Vector3, Vector3)>>();

    static List<(Vector3, Vector3)> GetHullEdges(Mesh mesh)
    {
        var key = mesh.GetInstanceID();
        if (s_hullCache.TryGetValue(key, out var cached))
            return cached;

        var edges = ConvexHull3D.ComputeEdges(mesh.vertices);
        s_hullCache[key] = edges;
        return edges;
    }

    // Raw mesh triangle edges (local space), deduplicated, cached by mesh instance ID.
    static Dictionary<int, List<(Vector3, Vector3)>> s_wireCache = new Dictionary<int, List<(Vector3, Vector3)>>();

    static List<(Vector3, Vector3)> GetWireEdges(Mesh mesh)
    {
        var key = mesh.GetInstanceID();
        if (s_wireCache.TryGetValue(key, out var cached))
            return cached;

        var edges = new List<(Vector3, Vector3)>();
        var verts = mesh.vertices;
        var tris = mesh.triangles;
        var seen = new HashSet<(int, int)>();
        for (int i = 0; i < tris.Length; i += 3)
        {
            AddWireEdge(seen, edges, verts, tris[i], tris[i + 1]);
            AddWireEdge(seen, edges, verts, tris[i + 1], tris[i + 2]);
            AddWireEdge(seen, edges, verts, tris[i + 2], tris[i]);
        }
        s_wireCache[key] = edges;
        return edges;
    }

    static void AddWireEdge(HashSet<(int, int)> seen, List<(Vector3, Vector3)> edges, Vector3[] verts, int i, int j)
    {
        var key = i < j ? (i, j) : (j, i);
        if (seen.Add(key))
            edges.Add((verts[i], verts[j]));
    }

    static void OnSceneGUI(SceneView sv)
    {
        if (!GameRenderWindow.drawJointCensus) return;
        if (!GameRenderWindow.drawJointCensusHull && !GameRenderWindow.drawJointCensusWireframe) return;

        TryLoad();
        if (s_parts.Count == 0) return;

        bool clusterMode = GameRenderWindow.jointCensusGroupByCluster;
        int selectedCluster = GameRenderWindow.jointCensusClusterIndex;

        foreach (var root in GetActiveRootObjects())
        {
            foreach (var meshFilter in root.GetComponentsInChildren<MeshFilter>(false))
            {
                if (meshFilter.GetComponent<StructurePart>() == null) continue;

                var name = meshFilter.gameObject.name.Replace("(Clone)", "").Trim();
                if (!s_parts.TryGetValue(name, out var info)) continue;

                Color col;
                if (clusterMode)
                {
                    if (info.ClusterId != selectedCluster) continue;
                    col = GameRenderWindow.jointCensusClusterColor;
                }
                else
                {
                    if (!MatchesSelectedNeighborCount(info.NeighborCount)) continue;
                    col = GameRenderWindow.jointCensusNeighborColor;
                }

                var mesh = meshFilter.sharedMesh;
                if (mesh == null) continue;

                var m = meshFilter.transform.localToWorldMatrix;

                if (GameRenderWindow.drawJointCensusHull)
                {
                    var hullEdges = GetHullEdges(mesh);
                    using (new Handles.DrawingScope(col, m))
                        foreach (var (a, b) in hullEdges)
                            Handles.DrawLine(a, b);
                }

                if (GameRenderWindow.drawJointCensusWireframe)
                {
                    var wireEdges = GetWireEdges(mesh);
                    using (new Handles.DrawingScope(col, m))
                        foreach (var (a, b) in wireEdges)
                            Handles.DrawLine(a, b);
                }
            }
        }
    }
}
