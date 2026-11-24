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

    // Largest cluster size present, for clamping the Cluster Size stepper — clusters vary
    // wildly in size (a single stray part vs. the whole ship), unlike neighbor count's 0-5 cap.
    public static int MaxClusterSize { get; private set; } = 0;

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
        s_parts.Clear(); s_byObj.Clear(); s_drawable.Clear(); s_clusterBounds.Clear(); SourceDescription = "";
        s_clusterSizes.Clear();
        s_clusterNames.Clear();
        ClusterCount = 0;
        MaxClusterSize = 0;
        LoadFailed = false;
        TryLoad();
    }

    // Per-scene-object census info (GameObject instance ID -> info), filled when the census has entity
    // ids (joint_census_live.csv / joint_census_spawn.csv from PartInfoLogger's SpawnDiagnostics).
    // Names repeat on this ship (tunnel tiles, cables...), so these are matched by name AND position
    // after a rigid game->editor fit, not by name alone.
    static Dictionary<int, PartInfo> s_byObj = new Dictionary<int, PartInfo>();
    // Built once per load so the scene GUI never walks the whole hierarchy per repaint.
    static List<(MeshFilter Mf, PartInfo Info)> s_drawable = new List<(MeshFilter, PartInfo)>();
    static Dictionary<int, Bounds> s_clusterBounds = new Dictionary<int, Bounds>();
    public static string SourceDescription { get; private set; } = "";

    static bool Lookup(GameObject go, out PartInfo info)
    {
        if (s_byObj.Count > 0) return s_byObj.TryGetValue(go.GetInstanceID(), out info);
        return s_parts.TryGetValue(go.name.Replace("(Clone)", "").Trim(), out info);
    }

    // Newest of the id-based census files, or null.
    static string FindIdCensus()
    {
        var root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        return new[] { "joint_census_live.csv", "joint_census_spawn.csv" }
            .Select(f => Path.Combine(root, f)).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
    }

    static Vector3 ParseVec(string s)
    {
        var p = s.Split(' ');
        if (p.Length < 3) return Vector3.zero;
        float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x);
        float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y);
        float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z);
        return new Vector3(x, y, z);
    }

    // Rotation whose columns are an orthonormal frame built from three points.
    static Matrix4x4 Frame(Vector3 a, Vector3 b, Vector3 c)
    {
        var e1 = (b - a).normalized;
        var e2 = Vector3.Cross(e1, c - a).normalized;
        var e3 = Vector3.Cross(e1, e2);
        var m = Matrix4x4.identity;
        m.SetColumn(0, e1); m.SetColumn(1, e2); m.SetColumn(2, e3);
        return m;
    }

    static bool TryLoadIdCensus(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) return false;
        var header = SplitCsvLine(lines[0]);
        int iName = header.IndexOf("PartName"), iPos = header.IndexOf("WorldPos"), iCount = header.IndexOf("JointedNeighbours"),
            iId = header.IndexOf("Id"), iNb = header.IndexOf("NeighbourIds");
        if (iName < 0 || iPos < 0 || iId < 0 || iNb < 0) return false;

        var rows = new List<(string Name, Vector3 Pos, int Count, int Id, int[] Nb)>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var f = SplitCsvLine(lines[i]);
            if (f.Count <= Math.Max(iNb, iId)) continue;
            if (!int.TryParse(f[iId], out var id) || id < 0) continue;
            if (f[iName].StartsWith("InvisibleJoint", StringComparison.OrdinalIgnoreCase)) continue;
            int.TryParse(iCount >= 0 ? f[iCount] : "0", out var count);
            var nb = f[iNb].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var v) ? v : -1).Where(v => v >= 0).ToArray();
            rows.Add((f[iName], ParseVec(f[iPos]), count, id, nb));
        }
        if (rows.Count == 0) return false;

        // Clusters: union-find over entity ids (exact, unlike names).
        var parent = rows.ToDictionary(r => r.Id, r => r.Id);
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        foreach (var r in rows)
            foreach (var n in r.Nb)
                if (parent.ContainsKey(n)) { var a = Find(r.Id); var b = Find(n); if (a != b) parent[a] = b; }
        var groups = rows.GroupBy(r => Find(r.Id)).OrderBy(g => g.Count()).ToList();
        var rowCluster = new Dictionary<int, int>();
        MaxClusterSize = 0;
        for (int c = 0; c < groups.Count; c++)
        {
            s_clusterSizes[c] = groups[c].Count();
            s_clusterNames[c] = groups[c].Select(r => r.Name).ToList();
            MaxClusterSize = Math.Max(MaxClusterSize, groups[c].Count());
            foreach (var r in groups[c]) rowCluster[r.Id] = c;
        }
        ClusterCount = groups.Count;

        // Scene parts the overlay can draw.
        var scene = new List<(GameObject Go, string Name, Vector3 Pos)>();
        foreach (var root in GetActiveRootObjects())
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(false))
                if (mf.GetComponent<StructurePart>() != null)
                    scene.Add((mf.gameObject, mf.gameObject.name.Replace("(Clone)", "").Trim(), mf.transform.position));

        // Rigid game->editor fit from names that are unique on both sides (RANSAC over point triples).
        var gameByName = rows.GroupBy(r => r.Name).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Pos);
        var pairs = scene.GroupBy(s => s.Name).Where(g => g.Count() == 1 && gameByName.ContainsKey(g.Key))
                         .Select(g => (G: gameByName[g.Key], U: g.First().Pos)).ToList();
        Matrix4x4 rot = Matrix4x4.identity; Vector3 gOrigin = Vector3.zero, uOrigin = Vector3.zero;
        int bestInliers = -1;
        if (pairs.Count >= 3)
        {
            var rng = new System.Random(1);
            for (int it = 0; it < 400; it++)
            {
                var a = pairs[rng.Next(pairs.Count)]; var b = pairs[rng.Next(pairs.Count)]; var c = pairs[rng.Next(pairs.Count)];
                if ((a.G - b.G).magnitude < 3f || Vector3.Cross(b.G - a.G, c.G - a.G).magnitude < 3f) continue;
                var r = Frame(a.U, b.U, c.U) * Frame(a.G, b.G, c.G).transpose;
                int inl = pairs.Count(p => (r.MultiplyVector(p.G - a.G) + a.U - p.U).sqrMagnitude < 0.01f);
                if (inl > bestInliers) { bestInliers = inl; rot = r; gOrigin = a.G; uOrigin = a.U; }
            }
        }

        // Match each scene part to the nearest unused census row of the same name.
        var rowsByName = rows.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Select(r => (r.Id, r.Count, P: rot.MultiplyVector(r.Pos - gOrigin) + uOrigin)).ToList());
        var used = new HashSet<int>();
        int matched = 0;
        foreach (var s in scene)
        {
            if (!rowsByName.TryGetValue(s.Name, out var cands)) continue;
            float best = 0.25f * 0.25f; int bestId = -1, bestCount = 0;
            foreach (var c in cands)
            {
                if (used.Contains(c.Id)) continue;
                var d = (c.P - s.Pos).sqrMagnitude;
                if (d < best) { best = d; bestId = c.Id; bestCount = c.Count; }
            }
            if (bestId < 0) continue;
            used.Add(bestId); matched++;
            var cl = rowCluster[bestId];
            var info = new PartInfo { NeighborCount = bestCount, ClusterId = cl, ClusterSize = s_clusterSizes[cl] };
            s_byObj[s.Go.GetInstanceID()] = info;
            var mf = s.Go.GetComponent<MeshFilter>();
            if (mf != null) s_drawable.Add((mf, info));
        }
        // Every cluster can be framed from its census positions (fitted into editor space), even when
        // none of its parts exist as meshes in the editor (stock parts loaded from the game).
        foreach (var r in rows)
        {
            var p = rot.MultiplyVector(r.Pos - gOrigin) + uOrigin;
            var c = rowCluster[r.Id];
            if (s_clusterBounds.TryGetValue(c, out var b)) { b.Encapsulate(p); s_clusterBounds[c] = b; }
            else s_clusterBounds[c] = new Bounds(p, Vector3.one);
        }
        SourceDescription = $"{Path.GetFileName(path)} ({File.GetLastWriteTime(path):MM-dd HH:mm}): {rows.Count} parts, {ClusterCount} clusters; " +
                            $"{matched}/{scene.Count} scene parts matched (fit {Math.Max(0, bestInliers)}/{pairs.Count} unique-name anchors)";
        return true;
    }

    static void TryLoad()
    {
        if (s_loaded) return;
        s_loaded = true;

        try
        {
            var idPath = FindIdCensus();
            if (idPath != null && TryLoadIdCensus(idPath)) { LoadFailed = false; return; }

            var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "joint_census.csv"));
            SourceDescription = "joint_census.csv (old name-only format)";
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
            MaxClusterSize = 0;
            for (int newId = 0; newId < orderedRaw.Count; newId++)
            {
                var rawId = orderedRaw[newId];
                rawToNew[rawId] = newId;
                s_clusterSizes[newId] = rawClusters[rawId].Count;
                s_clusterNames[newId] = rawClusters[rawId];
                if (rawClusters[rawId].Count > MaxClusterSize) MaxClusterSize = rawClusters[rawId].Count;
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
            s_parts.Clear(); s_byObj.Clear();
            s_clusterSizes.Clear();
            s_clusterNames.Clear();
            ClusterCount = 0;
            MaxClusterSize = 0;
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

    // Exact match against the selected cluster size — unlike neighbor count there's no natural
    // small cap, so no "N or more" bucketing; the stepper's range is clamped to MaxClusterSize.
    static bool MatchesSelectedClusterSize(int clusterSize) =>
        clusterSize == GameRenderWindow.jointCensusSizeIndex;

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
        foreach (var (mf, info) in Drawable())
        {
            if (mf == null || info.ClusterId != clusterId) continue;
            matchedObjects.Add(mf.gameObject);
            var renderer = mf.GetComponent<Renderer>();
            if (renderer == null) continue;
            if (bounds == null) bounds = renderer.bounds;
            else { var b = bounds.Value; b.Encapsulate(renderer.bounds); bounds = b; }
        }
        // Id-based census: frame from the census positions so clusters made only of stock parts
        // (no editor mesh) still frame.
        if (s_clusterBounds.TryGetValue(clusterId, out var cb))
        {
            if (bounds == null) bounds = cb;
            else { var b = bounds.Value; b.Encapsulate(cb); bounds = b; }
        }
        return (matchedObjects, bounds);
    }

    // Selects every GameObject in the given cluster in the Hierarchy, without touching the Scene view camera.
    public static void SelectClusterObjects(int clusterId)
    {
        TryLoad();
        if (s_parts.Count == 0 && s_byObj.Count == 0) return;

        var (matchedObjects, _) = GetClusterObjects(clusterId);
        if (matchedObjects.Count > 0)
            Selection.objects = matchedObjects.ToArray();
    }

    // Fits the Scene view camera to the bounds of every part in the given cluster.
    // Returns a short status string describing what happened, for surfacing in the UI.
    public static string FrameCluster(int clusterId)
    {
        TryLoad();
        if (s_parts.Count == 0 && s_byObj.Count == 0) return "No joint census data loaded.";

        var (matchedObjects, bounds) = GetClusterObjects(clusterId);

        if (bounds == null)
            return matchedObjects.Count == 0
                ? $"No scene GameObjects matched cluster {clusterId} by name."
                : $"Matched {matchedObjects.Count} part(s) in cluster {clusterId} but none had a Renderer to frame.";

        if (matchedObjects.Count > 0) Selection.objects = matchedObjects.ToArray();

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
        int total = GetClusterSize(clusterId);
        return matchedObjects.Count == total
            ? $"Framed cluster {clusterId}: {total} part(s), all selected."
            : $"Framed cluster {clusterId}: {total} part(s); {matchedObjects.Count} have editor meshes and are selected, the rest are stock parts (framed by census position).";
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

        TryLoad();
        if (s_parts.Count == 0 && s_byObj.Count == 0) return;

        bool clusterMode = !GameRenderWindow.jointCensusShowByClusterSize;
        int selectedCluster = GameRenderWindow.jointCensusClusterIndex;

        // Selected cluster's census extent (covers stock parts the editor has no mesh for).
        if (clusterMode && s_clusterBounds.TryGetValue(selectedCluster, out var cb))
            using (new Handles.DrawingScope(GameRenderWindow.jointCensusClusterColor))
                Handles.DrawWireCube(cb.center, cb.size);

        foreach (var (meshFilter, info) in Drawable())
        {
            {
                if (meshFilter == null) continue;

                Color col;
                if (clusterMode)
                {
                    if (info.ClusterId != selectedCluster) continue;
                    col = GameRenderWindow.jointCensusClusterColor;
                }
                else
                {
                    // Highlights every part belonging to ANY cluster of the selected size,
                    // not just one specific cluster — that's the point of this mode.
                    if (!MatchesSelectedClusterSize(info.ClusterSize)) continue;
                    col = GameRenderWindow.jointCensusSizeColor;
                }

                var mesh = meshFilter.sharedMesh;
                if (mesh == null) continue;

                var m = meshFilter.transform.localToWorldMatrix;

                var segs = GetSegments(mesh, GameRenderWindow.drawJointCensusHull);
                using (new Handles.DrawingScope(col, m))
                    Handles.DrawLines(segs);   // one batched call per part instead of one per edge
            }
        }
    }

    // Edge list flattened to the pairs array Handles.DrawLines wants, cached per mesh + mode.
    static Dictionary<(int, bool), Vector3[]> s_segCache = new Dictionary<(int, bool), Vector3[]>();
    static Vector3[] GetSegments(Mesh mesh, bool hull)
    {
        var key = (mesh.GetInstanceID(), hull);
        if (s_segCache.TryGetValue(key, out var segs)) return segs;
        var edges = hull ? GetHullEdges(mesh) : GetWireEdges(mesh);
        segs = new Vector3[edges.Count * 2];
        for (int i = 0; i < edges.Count; i++) { segs[i * 2] = edges[i].Item1; segs[i * 2 + 1] = edges[i].Item2; }
        s_segCache[key] = segs;
        return segs;
    }

    // Drawable scene parts with their census info. Id-based census fills this at load; for the old
    // name-only census it is built once here (not per repaint).
    static List<(MeshFilter Mf, PartInfo Info)> Drawable()
    {
        if (s_drawable.Count > 0 || s_parts.Count == 0) return s_drawable;
        foreach (var root in GetActiveRootObjects())
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(false))
                if (mf.GetComponent<StructurePart>() != null && Lookup(mf.gameObject, out var info))
                    s_drawable.Add((mf, info));
        return s_drawable;
    }
}
