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

    // Id-based census graph, kept for the two-part path finder: per entity id its part name, census
    // position fitted into editor space, jointed neighbour ids, and the editor mesh it matched (if any).
    static Dictionary<int, string> s_rowName = new Dictionary<int, string>();
    static Dictionary<int, Vector3> s_rowPos = new Dictionary<int, Vector3>();
    static Dictionary<int, HashSet<int>> s_adj = new Dictionary<int, HashSet<int>>();
    static Dictionary<int, MeshFilter> s_idToMf = new Dictionary<int, MeshFilter>();
    static Dictionary<int, int> s_goToId = new Dictionary<int, int>();
    static List<int> s_path = new List<int>();
    // Census parts the path may not pass through when the matching toggle is on (end parts are always allowed).
    static HashSet<int> s_cutIds = new HashSet<int>();    // Jsa or name contains "CutPoint"
    static HashSet<int> s_yankIds = new HashSet<int>();   // census Yank column = 1 (grapple-breakable joint)
    public static bool HasYankColumn { get; private set; }

    // Path settings, set from the Joint Assist window's Joint Path section.
    public static bool SkipCut { get => EditorPrefs.GetBool("JCG.skipCut", false); set => EditorPrefs.SetBool("JCG.skipCut", value); }
    public static bool SkipYank { get => EditorPrefs.GetBool("JCG.skipYank", false); set => EditorPrefs.SetBool("JCG.skipYank", value); }
    public static bool ShowPath = true;
    static readonly Color kPathColor = new Color(1f, 0.9f, 0f, 0.9f);
    static string s_loadedFile; static DateTime s_loadedStamp;
    public static bool HasPath => s_path.Count > 0;
    public static string PathText { get; private set; } = "";

    public static void ClearPath() { s_path.Clear(); s_links.Clear(); PathText = ""; SceneView.RepaintAll(); }

    // Census entity id for a scene object: its own (or a child mesh's) match, else the census row of the
    // same name nearest to it, else any census row within 0.5 m (stock parts that have no editor mesh).
    static int ResolveId(GameObject go)
    {
        if (s_goToId.TryGetValue(go.GetInstanceID(), out var id)) return id;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            if (s_goToId.TryGetValue(mf.gameObject.GetInstanceID(), out id)) return id;
        var name = go.name.Replace("(Clone)", "").Trim(); var p = go.transform.position;
        int best = -1; float bd = 1f;
        foreach (var kv in s_rowName)
            if (kv.Value == name) { var d = (s_rowPos[kv.Key] - p).magnitude; if (d < bd) { bd = d; best = kv.Key; } }
        if (best >= 0) return best;
        bd = 0.5f;
        foreach (var kv in s_rowPos) { var d = (kv.Value - p).magnitude; if (d < bd) { bd = d; best = kv.Key; } }
        return best;
    }

    // Shortest joint chain (fewest joints) between the two selected parts, from the census graph.
    public static string FindPathFromSelection()
    {
        var newest = FindIdCensus();   // reload when F10 wrote a newer census
        if (newest != null && (newest != s_loadedFile || File.GetLastWriteTime(newest) != s_loadedStamp)) ReloadCsv();
        TryLoad();
        s_path.Clear(); s_links.Clear(); PathText = "";
        if (s_adj.Count == 0) return "No id-based census loaded (needs joint_census_live.csv or joint_census_spawn.csv with Id / NeighbourIds).";
        var sel = Selection.gameObjects;
        if (sel.Length != 2) return $"Select exactly two parts ({sel.Length} selected).";
        int a = ResolveId(sel[0]), b = ResolveId(sel[1]);
        if (a < 0) return $"'{sel[0].name}' is not in the census (not on the ship in that run, or no match).";
        if (b < 0) return $"'{sel[1].name}' is not in the census (not on the ship in that run, or no match).";
        if (a == b) return "Both selections are the same census part.";
        bool skipCut = SkipCut, skipYank = SkipYank && HasYankColumn;
        bool Blocked(int x) => x != a && x != b && ((skipCut && s_cutIds.Contains(x)) || (skipYank && s_yankIds.Contains(x)));
        var prev = new Dictionary<int, int> { [a] = a }; var q = new Queue<int>(); q.Enqueue(a);
        while (q.Count > 0 && !prev.ContainsKey(b))
        {
            var x = q.Dequeue();
            if (!s_adj.TryGetValue(x, out var nb)) continue;
            foreach (var y in nb) if (!prev.ContainsKey(y) && !Blocked(y)) { prev[y] = x; q.Enqueue(y); }
        }
        var skipped = (skipCut ? "cut points" : "") + (skipCut && skipYank ? " and " : "") + (skipYank ? "yankable parts" : "");
        var yankNote = SkipYank && !HasYankColumn ? " (Yankable parts NOT skipped: this census has no Yank column; press F10 again with the updated PartInfoLogger.)" : "";
        if (!prev.ContainsKey(b))
            return skipped.Length > 0
                ? $"No joint path without {skipped}: '{s_rowName[a]}' and '{s_rowName[b]}' come apart once those are cut or yanked.{yankNote}"
                : $"No joint path: '{s_rowName[a]}' and '{s_rowName[b]}' are in different clusters (not attached to each other).";
        for (int x = b; ; x = prev[x]) { s_path.Add(x); if (x == a) break; }
        s_path.Reverse();
        ComputeLinks();
        var sb = new System.Text.StringBuilder(); int far = 0;
        for (int i = 0; i < s_path.Count; i++)
        {
            var id = s_path[i];
            sb.Append($"{i}. {s_rowName[id]}" + (s_cutIds.Contains(id) ? "  [cut point]" : "") + (s_yankIds.Contains(id) ? "  [yankable]" : "") +
                      (s_idToMf.ContainsKey(id) ? "" : "  (stock part)"));
            if (i < s_links.Count)
            {
                var l = s_links[i];
                if (l == null) sb.Append("\n     | joint (no editor mesh to measure)");
                else { if (l.Value.D > kGapWarn) far++; sb.Append(l.Value.D > kGapWarn ? $"\n     | joint across a {l.Value.D:0.00} m GAP" : $"\n     | joint, touching ({l.Value.D * 100:0.0} cm)"); }
            }
            sb.Append('\n');
        }
        PathText = sb.ToString().TrimEnd();
        if (far > 0) PathText = $"{far} joint(s) on this chain span more than {kGapWarn * 100:0} cm between the editor meshes (red in the Scene view).\n\n" + PathText;
        SceneView.RepaintAll();
        return $"{s_path.Count - 2} part(s) between them, {s_path.Count - 1} joint(s) on the shortest chain" +
               (skipped.Length > 0 ? $" that avoids {skipped}." : ".") + yankNote;
    }

    // Selects the scene objects of the highlighted chain: matched editor meshes directly; stock parts by
    // the scene object of the same name nearest their census position (within 1 m), e.g. addressable loaders.
    // Scene object of a census part: its matched mesh, else the saved scene object of the same name nearest its
    // census position (within 1 m), e.g. an addressable loader whose preview draws the stock part.
    static GameObject SceneObjectFor(int id, List<Transform> scene)
    {
        if (s_idToMf.TryGetValue(id, out var mf) && mf != null) return mf.gameObject;
        var name = s_rowName[id]; var p = s_rowPos[id]; Transform best = null; float bd = 1f;
        foreach (var t in scene)
        {
            if (t.hideFlags != HideFlags.None || t.name.Replace("(Clone)", "").Trim() != name) continue;
            var d = (t.position - p).magnitude; if (d < bd) { bd = d; best = t; }
        }
        return best != null ? best.gameObject : null;
    }

    static List<Transform> SceneTransforms()
    {
        var scene = new List<Transform>();
        foreach (var root in GetActiveRootObjects()) scene.AddRange(root.GetComponentsInChildren<Transform>(false));
        return scene;
    }

    // World-space triangles of a part: its own mesh, or every mesh under it (incl. addressable preview children).
    static (Vector3[] V, int[] T) WorldMesh(GameObject go)
    {
        if (go == null) return (null, null);
        var own = go.GetComponent<MeshFilter>();
        var mfs = own != null && own.sharedMesh != null ? new[] { own } : go.GetComponentsInChildren<MeshFilter>(true);
        var V = new List<Vector3>(); var T = new List<int>();
        foreach (var mf in mfs)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            var m = mf.transform.localToWorldMatrix; int b = V.Count;
            foreach (var v in mf.sharedMesh.vertices) V.Add(m.MultiplyPoint3x4(v));
            foreach (var i in mf.sharedMesh.triangles) T.Add(b + i);
        }
        return V.Count > 0 ? (V.ToArray(), T.ToArray()) : (null, null);
    }

    // Closest point pair between two triangle meshes: vertices of each side against the other side's triangles,
    // only triangles whose box comes within the best distance found so far (seeded by a vertex-vertex pass).
    static (Vector3 A, Vector3 B, float D) ClosestPoints((Vector3[] V, int[] T) a, (Vector3[] V, int[] T) b)
    {
        int sa = Mathf.Max(1, a.V.Length / 1500), sb = Mathf.Max(1, b.V.Length / 1500);
        Vector3 pa = a.V[0], pb = b.V[0]; float best = float.MaxValue;
        for (int i = 0; i < a.V.Length; i += sa)
            for (int j = 0; j < b.V.Length; j += sb)
            { var d = (a.V[i] - b.V[j]).sqrMagnitude; if (d < best) { best = d; pa = a.V[i]; pb = b.V[j]; } }
        best = Mathf.Sqrt(best);
        void Refine((Vector3[] V, int[] T) pts, (Vector3[] V, int[] T) tris, bool ptsAreA)
        {
            for (int t = 0; t < tris.T.Length; t += 3)
            {
                Vector3 x = tris.V[tris.T[t]], y = tris.V[tris.T[t + 1]], z = tris.V[tris.T[t + 2]];
                var lo = Vector3.Min(Vector3.Min(x, y), z) - Vector3.one * best; var hi = Vector3.Max(Vector3.Max(x, y), z) + Vector3.one * best;
                foreach (var p in pts.V)
                {
                    if (p.x < lo.x || p.y < lo.y || p.z < lo.z || p.x > hi.x || p.y > hi.y || p.z > hi.z) continue;
                    var q = ClosestOnTriangle(p, x, y, z); var d = (p - q).magnitude;
                    if (d < best) { best = d; if (ptsAreA) { pa = p; pb = q; } else { pa = q; pb = p; } lo = Vector3.Min(Vector3.Min(x, y), z) - Vector3.one * best; hi = Vector3.Max(Vector3.Max(x, y), z) + Vector3.one * best; }
                }
            }
        }
        Refine(a, b, true); Refine(b, a, false);
        return (pa, pb, best);
    }

    // Ericson, Real-Time Collision Detection 5.1.5.
    static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        Vector3 bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        Vector3 cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    // Contact for each joint on the path: closest mesh points of the two parts (null when a part has no mesh in the editor).
    static List<(Vector3 A, Vector3 B, float D)?> s_links = new List<(Vector3, Vector3, float)?>();
    const float kGapWarn = 0.05f;   // spawn coplanar threshold: a joint across more than this is not a direct contact

    static void ComputeLinks()
    {
        s_links.Clear();
        var scene = SceneTransforms();
        var meshes = s_path.Select(id => WorldMesh(SceneObjectFor(id, scene))).ToList();
        for (int i = 0; i + 1 < s_path.Count; i++)
            s_links.Add(meshes[i].V != null && meshes[i + 1].V != null ? ClosestPoints(meshes[i], meshes[i + 1]) : ((Vector3, Vector3, float)?)null);
    }

    public static string SelectPathParts()
    {
        if (s_path.Count == 0) return "No path to select. Click Find Path first.";
        var scene = SceneTransforms();
        var picked = new List<GameObject>(); int missing = 0;
        foreach (var id in s_path)
        {
            var go = SceneObjectFor(id, scene);
            if (go != null) picked.Add(go); else missing++;
        }
        Selection.objects = picked.Distinct().Cast<UnityEngine.Object>().ToArray();
        return $"Selected {Selection.objects.Length} of {s_path.Count} path parts" + (missing > 0 ? $"; {missing} not found in the scene." : ".");
    }

    static void DrawPath()
    {
        var col = kPathColor;
        var gapCol = new Color(1f, 0.25f, 0.2f, 1f);
        var pts = s_path.Select(id => s_rowPos[id]).ToArray();
        for (int i = 0; i < s_path.Count; i++)
        {
            var id = s_path[i];
            if (s_idToMf.TryGetValue(id, out var mf) && mf != null && mf.sharedMesh != null)
                using (new Handles.DrawingScope(col, mf.transform.localToWorldMatrix))
                    Handles.DrawLines(GetSegments(mf.sharedMesh, false));
            else
                using (new Handles.DrawingScope(col))
                    Handles.DrawWireCube(pts[i], Vector3.one * 0.5f);
        }
        // Joints: drawn between the closest points of the two meshes. Inside each part, a dotted line links
        // where the chain enters to where it leaves. Labels sit at each part's entry point.
        for (int i = 0; i < s_path.Count; i++)
        {
            var into = i > 0 && s_links[i - 1] != null ? s_links[i - 1].Value.B : (Vector3?)null;
            var outOf = i < s_links.Count && s_links[i] != null ? s_links[i].Value.A : (Vector3?)null;
            var at = into ?? outOf ?? pts[i];
            Handles.Label(at, $"{i}. {s_rowName[s_path[i]]}", EditorStyles.whiteMiniLabel);
            if (into.HasValue && outOf.HasValue)
                using (new Handles.DrawingScope(col)) Handles.DrawDottedLine(into.Value, outOf.Value, 3f);
        }
        for (int i = 0; i < s_links.Count; i++)
        {
            var l = s_links[i];
            if (l == null)
            {
                using (new Handles.DrawingScope(new Color(col.r, col.g, col.b, 0.4f))) Handles.DrawDottedLine(pts[i], pts[i + 1], 4f);
                continue;
            }
            bool gap = l.Value.D > kGapWarn; var c = gap ? gapCol : col;
            using (new Handles.DrawingScope(c))
            {
                float s = HandleUtility.GetHandleSize(l.Value.A) * 0.06f;
                Handles.SphereHandleCap(0, l.Value.A, Quaternion.identity, s, EventType.Repaint);
                Handles.SphereHandleCap(0, l.Value.B, Quaternion.identity, s, EventType.Repaint);
                Handles.DrawAAPolyLine(gap ? 6f : 4f, l.Value.A, l.Value.B);
            }
            if (gap) Handles.Label((l.Value.A + l.Value.B) * 0.5f, $"gap {l.Value.D:0.00} m", EditorStyles.boldLabel);
        }
    }

    public static void ReloadCsv()
    {
        s_loaded = false;
        s_rowName.Clear(); s_rowPos.Clear(); s_adj.Clear(); s_idToMf.Clear(); s_goToId.Clear(); s_path.Clear(); s_links.Clear(); PathText = "";
        s_cutIds.Clear(); s_yankIds.Clear(); HasYankColumn = false;
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
            iId = header.IndexOf("Id"), iNb = header.IndexOf("NeighbourIds"), iJsa = header.IndexOf("Jsa"), iYank = header.IndexOf("Yank");
        HasYankColumn = iYank >= 0;
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
            var jsa = iJsa >= 0 && f.Count > iJsa ? f[iJsa] : "";
            if (jsa.IndexOf("CutPoint", StringComparison.OrdinalIgnoreCase) >= 0 || f[iName].IndexOf("CutPoint", StringComparison.OrdinalIgnoreCase) >= 0)
                s_cutIds.Add(id);
            if (iYank >= 0 && f.Count > iYank && f[iYank] == "1") s_yankIds.Add(id);
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
            s_goToId[s.Go.GetInstanceID()] = bestId;
            var mf = s.Go.GetComponent<MeshFilter>();
            if (mf != null) { s_drawable.Add((mf, info)); s_idToMf[bestId] = mf; }
        }
        // Graph for the path finder (joints listed by either side count).
        foreach (var r in rows)
        {
            s_rowName[r.Id] = r.Name;
            s_rowPos[r.Id] = rot.MultiplyVector(r.Pos - gOrigin) + uOrigin;
            if (!s_adj.TryGetValue(r.Id, out var set)) s_adj[r.Id] = set = new HashSet<int>();
            foreach (var n in r.Nb)
            {
                if (!parent.ContainsKey(n) || n == r.Id) continue;
                set.Add(n);
                if (!s_adj.TryGetValue(n, out var back)) s_adj[n] = back = new HashSet<int>();
                back.Add(r.Id);
            }
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
            if (idPath != null && TryLoadIdCensus(idPath)) { LoadFailed = false; s_loadedFile = idPath; s_loadedStamp = File.GetLastWriteTime(idPath); return; }

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
        // Joint path (Joint Assist window): drawn on its own, whether or not the census overlay is on.
        if (ShowPath && s_path.Count > 0) DrawPath();

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
