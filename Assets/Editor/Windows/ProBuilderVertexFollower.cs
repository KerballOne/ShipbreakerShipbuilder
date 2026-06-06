using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

/// <summary>
/// Locks ProBuilder vertices that are within a threshold distance of a target part's mesh surface
/// (walking the full child hierarchy), then translates those vertices by the same delta when the
/// target is repositioned. Supports multiple PB meshes simultaneously, each tracked independently.
/// Workflow: assign target → add PB meshes → Attach → move target → Apply.
/// </summary>
public class ProBuilderVertexFollower : EditorWindow
{
    // ── Per-PB pairing data ───────────────────────────────────────────────────

    class PBEntry
    {
        public ProBuilderMesh pbMesh;
        public float          threshold      = 0.5f;  // Per-entry proximity threshold to target surface
        public float          edgeLockRadius = 0.5f;  // Max PB-local distance from the closest vertex (edge lock mode)
        public int            subdivideCount = 1;     // How many times to subdivide the closest face
        public int[]          sharedVertexIds;
        public Vector3[]      localPositions;          // PB-local positions at attach time
        public Vector3        targetPosAtAttach;       // Target transform.position at attach time
    }

    // ── Window state ──────────────────────────────────────────────────────────

    readonly List<PBEntry> _entries = new List<PBEntry>();
    GameObject             _targetGO;
    float                  _threshold      = 0.5f;
    float                  _edgeLockRadius = 0.5f;
    bool                   _edgeLock       = true;   // Default: only grab vertices near the closest interface edge
    bool                   _attached;
    Vector2                _scroll;
    string                 _diagnoseText;

    // ── Entry colors (cycles for gizmos) ─────────────────────────────────────

    static readonly Color[] kEntryColors =
    {
        new Color(1f,   0.55f, 0f,   1f),  // orange
        new Color(0.2f, 1f,   0.3f, 1f),  // green
        new Color(0.8f, 0.2f, 1f,   1f),  // purple
        new Color(1f,   0.9f, 0.1f, 1f),  // yellow
    };
    static readonly Color kTargetColor = new Color(0.2f, 0.55f, 1f, 1f); // blue

    // ── Window ────────────────────────────────────────────────────────────────

    [MenuItem("Shipbreaker/Shipbuilder Tools/PB Vertex Follower", priority = 25)]
    static void Open() => GetWindow<ProBuilderVertexFollower>("PB Vertex Follower");

    void OnEnable()  => SceneView.duringSceneGui += OnSceneGUI;
    void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

    // ── GUI ───────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        // ── Section 1: Target ─────────────────────────────────────────────────
        EditorGUILayout.LabelField("1. Target Part", EditorStyles.boldLabel);
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        _targetGO = (GameObject)EditorGUILayout.ObjectField("Target", _targetGO, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck()) ClearAttach();
        using (new EditorGUI.DisabledScope(_targetGO == null))
        {
            if (GUILayout.Button("Select", GUILayout.Width(54)))
                Selection.activeGameObject = _targetGO;
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6);

        // ── Section 2: PB Mesh List ───────────────────────────────────────────
        EditorGUILayout.LabelField("2. ProBuilder Meshes", EditorStyles.boldLabel);

        if (GUILayout.Button("Add Selected PB Meshes"))
        {
            foreach (var go in Selection.gameObjects)
            {
                var pb = go.GetComponent<ProBuilderMesh>();
                if (pb == null) continue;
                if (_entries.Exists(e => e.pbMesh == pb)) continue;
                _entries.Add(new PBEntry { pbMesh = pb, threshold = _threshold, edgeLockRadius = _edgeLockRadius });
                ClearAttach();
            }
        }

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            Color col = kEntryColors[i % kEntryColors.Length];

            // Row 1: name + remove button
            EditorGUILayout.BeginHorizontal();
            var prevBG = GUI.backgroundColor;
            GUI.backgroundColor = col * 0.7f;

            string label = entry.pbMesh == null ? "(missing)" :
                entry.pbMesh.gameObject.name +
                (entry.sharedVertexIds != null ? $"  [{entry.sharedVertexIds.Length} verts]" : "");
            EditorGUILayout.LabelField(label, EditorStyles.helpBox);

            GUI.backgroundColor = prevBG;
            if (GUILayout.Button("✕", GUILayout.Width(28)))
            {
                _entries.RemoveAt(i);
                ClearAttach();
            }
            EditorGUILayout.EndHorizontal();

            // Row 2: per-entry threshold + edge lock radius
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            float newT = EditorGUILayout.Slider("Threshold (m)", entry.threshold, 0.001f, 5f);
            if (EditorGUI.EndChangeCheck()) { entry.threshold = newT; ClearAttach(); }

            if (_edgeLock)
            {
                EditorGUI.BeginChangeCheck();
                float newR = EditorGUILayout.Slider("Edge Radius (m)", entry.edgeLockRadius, 0.001f, 5f);
                if (EditorGUI.EndChangeCheck()) { entry.edgeLockRadius = newR; ClearAttach(); }
            }

            EditorGUILayout.BeginHorizontal();
            entry.subdivideCount = EditorGUILayout.IntSlider("Subdivide", entry.subdivideCount, 0, 8);
            using (new EditorGUI.DisabledScope(_targetGO == null || entry.pbMesh == null))
            {
                if (GUILayout.Button("Auto", GUILayout.Width(42)))
                    AutoSetupEntry(entry);
                if (GUILayout.Button($"Apply ×{entry.subdivideCount}", GUILayout.Width(72)))
                    DoSubdivideFace(entry);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
        }

        if (_entries.Count == 0)
            EditorGUILayout.HelpBox("Select ProBuilder objects in the Hierarchy and click Add.", MessageType.None);

        EditorGUILayout.Space(6);

        // ── Section 3: Settings ───────────────────────────────────────────────
        EditorGUILayout.LabelField("3. Settings", EditorStyles.boldLabel);
        _threshold = EditorGUILayout.Slider("Default Threshold (m)", _threshold, 0.001f, 5f);

        EditorGUI.BeginChangeCheck();
        _edgeLock = EditorGUILayout.Toggle("Edge Lock Mode", _edgeLock);
        if (EditorGUI.EndChangeCheck()) ClearAttach();

        if (_edgeLock)
        {
            _edgeLockRadius = EditorGUILayout.Slider("Default Edge Radius (m)", _edgeLockRadius, 0.001f, 5f);
            EditorGUILayout.HelpBox(
                "Edge Lock: captures only vertices near the closest interface point, ignoring far corners of large panels.",
                MessageType.None);
        }
        else
        {
            EditorGUILayout.HelpBox("Face Lock: captures all vertices within threshold, regardless of edge proximity.", MessageType.None);
        }

        EditorGUILayout.Space(4);

        bool canAttach = _targetGO != null && _entries.Count > 0 && _entries.Exists(e => e.pbMesh != null);
        using (new EditorGUI.DisabledScope(!canAttach))
        {
            if (GUILayout.Button("Attach", GUILayout.Height(28)))
                DoAttach();
        }

        EditorGUILayout.Space(6);

        // ── Section 4: State ──────────────────────────────────────────────────
        if (_attached)
        {
            EditorGUILayout.LabelField("4. Attached State", EditorStyles.boldLabel);
            int total = 0;
            foreach (var e in _entries)
                if (e.sharedVertexIds != null) total += e.sharedVertexIds.Length;
            EditorGUILayout.HelpBox($"{_entries.Count} PB mesh(es) attached — {total} total vertex group(s).", MessageType.None);
            SceneView.RepaintAll();
        }

        EditorGUILayout.Space(6);

        // ── Section 5: Apply / Clear ──────────────────────────────────────────
        EditorGUILayout.LabelField("5. Apply", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(!_attached))
        {
            if (GUILayout.Button("Apply", GUILayout.Height(28)))
                DoApply();
        }

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Clear"))
            ClearAttach();

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Diagnose Distances"))
            DoDiagnose();

        if (!string.IsNullOrEmpty(_diagnoseText))
        {
            EditorGUILayout.Space(2);
            var style = new GUIStyle(EditorStyles.helpBox) { wordWrap = false };
            float h = style.CalcHeight(new GUIContent(_diagnoseText), EditorGUIUtility.currentViewWidth - 8);
            EditorGUILayout.SelectableLabel(_diagnoseText, style, GUILayout.Height(h));
        }

        EditorGUILayout.EndScrollView();
    }

    // ── Subdivide closest face ────────────────────────────────────────────────

    // Auto: find closest PB face to target, measure true point-to-point gap,
    // check threshold, then compute how many subdivisions align PB face size to target face size.
    void AutoSetupEntry(PBEntry entry)
    {
        if (_targetGO == null || entry.pbMesh == null) return;

        var pb          = entry.pbMesh;
        var pbToWorld   = pb.transform.localToWorldMatrix;

        // Get bounds of PB mesh and target hierarchy
        Bounds pbBounds     = GetHierarchyBounds(pb.gameObject);
        Bounds targetBounds = GetHierarchyBounds(_targetGO);

        // Find closest face direction (same axis logic as JointAssistWindow.AutoDetectFaces)
        Vector3 dir = GetClosestFaceDirection(pbBounds, targetBounds);

        // PB face: the face pointing toward the target
        // Target face: the face pointing toward the PB
        // Shortest gap = distance between the two facing planes
        float pbFacePos     = Vector3.Dot(pbBounds.center, dir)     + FaceReach(pbBounds, dir);
        float targetFacePos = Vector3.Dot(targetBounds.center, dir) - FaceReach(targetBounds, dir);
        float gap           = Mathf.Abs(targetFacePos - pbFacePos);

        // Compute the size of the PB facing face and the target facing face
        // "Face size" = extent in the two axes perpendicular to dir
        Vector2 pbFaceSize     = FaceSize(pbBounds, dir);
        Vector2 targetFaceSize = FaceSize(targetBounds, dir);

        // Subdivisions needed so PB face segments are <= target face size in each dimension
        int subX = pbFaceSize.x > targetFaceSize.x && targetFaceSize.x > 0f
            ? Mathf.CeilToInt(pbFaceSize.x / targetFaceSize.x)
            : 0;
        int subY = pbFaceSize.y > targetFaceSize.y && targetFaceSize.y > 0f
            ? Mathf.CeilToInt(pbFaceSize.y / targetFaceSize.y)
            : 0;
        int subdivisions = Mathf.Max(subX, subY);

        // Each subdivision halves segment size, so we need log2(ratio) passes
        // subX/subY already give us the ratio — convert to passes
        int passes = subdivisions > 1 ? Mathf.CeilToInt(Mathf.Log(subdivisions, 2f)) : subdivisions;
        passes = Mathf.Clamp(passes, 0, 8);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Auto-setup '{pb.gameObject.name}'");
        sb.AppendLine($"  dir: {dir}  gap: {gap:F4}m  threshold: {entry.threshold:F3}m");
        sb.AppendLine($"  PB face: {pbFaceSize.x:F3}×{pbFaceSize.y:F3}m");
        sb.AppendLine($"  Target face: {targetFaceSize.x:F3}×{targetFaceSize.y:F3}m");
        sb.AppendLine($"  Ratio: {subX}×{subY} → {passes} subdivision pass(es)");

        if (gap > entry.threshold)
        {
            sb.AppendLine($"  WARNING: gap {gap:F4}m exceeds threshold {entry.threshold:F3}m — faces may not be adjacent.");
        }

        entry.subdivideCount = passes;
        _diagnoseText = sb.ToString().TrimEnd();
        Repaint();
    }

    void DoSubdivideFace(PBEntry entry)
    {
        if (_targetGO == null || entry.pbMesh == null || entry.subdivideCount == 0) return;

        var pb        = entry.pbMesh;
        var pbToWorld = pb.transform.localToWorldMatrix;

        // Record undo once before all passes — ToMesh/Refresh only at the end
        Undo.RegisterCompleteObjectUndo(new Object[] { pb, pb.GetComponent<MeshFilter>().sharedMesh }, "Subdivide Closest Face");

        Bounds pbBounds     = GetHierarchyBounds(pb.gameObject);
        Bounds targetBounds = GetHierarchyBounds(_targetGO);
        Vector3 dir         = GetClosestFaceDirection(pbBounds, targetBounds);

        // First pass: find the single closest face to the target
        Face  seedFace  = null;
        float bestAlign = float.MinValue;
        foreach (var face in pb.faces)
        {
            Vector3 localCenter = Vector3.zero;
            int     count       = 0;
            foreach (int idx in face.distinctIndexes) { localCenter += pb.positions[idx]; count++; }
            if (count == 0) continue;
            float align = Vector3.Dot(pbToWorld.MultiplyPoint3x4(localCenter), dir);
            if (align > bestAlign) { bestAlign = align; seedFace = face; }
        }
        if (seedFace == null) return;

        // Each pass subdivides all faces from the previous pass, keeping the region symmetric
        var currentFaces = new List<Face> { seedFace };
        for (int iter = 0; iter < entry.subdivideCount; iter++)
        {
            var newFaces = ConnectElements.Connect(pb, currentFaces);
            currentFaces = newFaces != null ? new List<Face>(newFaces) : new List<Face>();
            if (currentFaces.Count == 0) break;
        }

        // Single ToMesh/Refresh after all passes
        pb.ToMesh();
        pb.Refresh();

        ClearAttach();
        SceneView.RepaintAll();
        Repaint();
    }

    // ── Bounds helpers (mirror of JointAssistWindow logic) ────────────────────

    static Bounds GetHierarchyBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>();
        var valid     = new List<Renderer>();
        foreach (var r in renderers)
            if (!(r is ParticleSystemRenderer)) valid.Add(r);

        if (valid.Count > 0)
        {
            Bounds b = valid[0].bounds;
            for (int i = 1; i < valid.Count; i++) b.Encapsulate(valid[i].bounds);
            return b;
        }
        return new Bounds(go.transform.position, Vector3.zero);
    }

    static float FaceReach(Bounds b, Vector3 dir)
        => Mathf.Abs(b.extents.x * dir.x) + Mathf.Abs(b.extents.y * dir.y) + Mathf.Abs(b.extents.z * dir.z);

    // Returns the two perpendicular extents of the face in the given direction
    static Vector2 FaceSize(Bounds b, Vector3 dir)
    {
        // The face perpendicular to dir has extents in the other two axes
        // dir is a cardinal axis, so one component is ~1 and two are ~0
        float ax = Mathf.Abs(dir.x), ay = Mathf.Abs(dir.y), az = Mathf.Abs(dir.z);
        if (ax >= ay && ax >= az) return new Vector2(b.size.y, b.size.z);  // X-facing face
        if (ay >= ax && ay >= az) return new Vector2(b.size.x, b.size.z);  // Y-facing face
        return new Vector2(b.size.x, b.size.y);                             // Z-facing face
    }

    static Vector3 GetClosestFaceDirection(Bounds bA, Bounds bB)
    {
        Vector3[] axes    = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        float     bestGap = float.MaxValue;
        Vector3   bestDir = Vector3.forward;

        foreach (var a in axes)
        {
            float faceA = Vector3.Dot(bA.center, a) + FaceReach(bA, a);
            float faceB = Vector3.Dot(bB.center, a) - FaceReach(bB, a);
            float g     = faceB - faceA;
            if (g >= 0f && g < bestGap) { bestGap = g; bestDir = a; }
        }

        // Fallback: no positive-gap axis found (overlapping) — use closest center direction
        if (bestGap == float.MaxValue)
        {
            Vector3 delta = bB.center - bA.center;
            bestDir = ClosestCardinalAxis(delta);
        }

        return bestDir;
    }

    static Vector3 ClosestCardinalAxis(Vector3 v)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        if (ax >= ay && ax >= az) return v.x >= 0 ? Vector3.right   : Vector3.left;
        if (ay >= ax && ay >= az) return v.y >= 0 ? Vector3.up      : Vector3.down;
        return                           v.z >= 0 ? Vector3.forward : Vector3.back;
    }

    // ── Attach ────────────────────────────────────────────────────────────────

    void DoAttach()
    {
        var targetRenderers = CollectRenderers(_targetGO);
        var targetMFs       = CollectMeshFilters(_targetGO);

        if (targetRenderers.Count == 0)
            Debug.LogWarning("PB Vertex Follower: Target has no renderers — falling back to pivot distance.");

        foreach (var entry in _entries)
        {
            if (entry.pbMesh == null) continue;
            AttachEntry(entry, targetRenderers, targetMFs);
        }

        _attached = true;
        SceneView.RepaintAll();
        Repaint();
    }

    void AttachEntry(PBEntry entry, List<Renderer> targetRenderers, List<MeshFilter> targetMFs)
    {
        var positions   = entry.pbMesh.positions;
        var sharedVerts = entry.pbMesh.sharedVertices;
        var pbToWorld   = entry.pbMesh.transform.localToWorldMatrix;

        // First pass: collect all verts within threshold distance to target surface.
        var candidates = new List<(int si, Vector3 localPos, float dist)>();

        for (int si = 0; si < sharedVerts.Count; si++)
        {
            int idx = sharedVerts[si][0];
            if (idx < 0 || idx >= positions.Count) continue;
            Vector3 localPos = positions[idx];
            Vector3 worldPos = pbToWorld.MultiplyPoint3x4(localPos);

            float dist;
            if (targetRenderers.Count > 0)
                ClosestPointOnHierarchy(worldPos, targetRenderers, targetMFs, out dist);
            else
                dist = Vector3.Distance(worldPos, _targetGO.transform.position);

            if (dist <= entry.threshold)
                candidates.Add((si, localPos, dist));
        }

        // Edge lock: keep only candidates within edgeLockRadius (PB local space) of the
        // single closest candidate. This prevents far corners of large panels from being
        // included just because their face is broadly near the target surface.
        if (_edgeLock && candidates.Count > 0)
        {
            // Find the candidate closest to the target surface
            int    bestIdx  = 0;
            float  bestDist = candidates[0].dist;
            for (int k = 1; k < candidates.Count; k++)
                if (candidates[k].dist < bestDist) { bestDist = candidates[k].dist; bestIdx = k; }

            Vector3 anchorLocal = candidates[bestIdx].localPos;
            float   r           = entry.edgeLockRadius;

            candidates.RemoveAll(c => Vector3.Distance(c.localPos, anchorLocal) > r);
        }

        var ids    = new List<int>(candidates.Count);
        var locals = new List<Vector3>(candidates.Count);
        foreach (var c in candidates) { ids.Add(c.si); locals.Add(c.localPos); }

        entry.sharedVertexIds   = ids.ToArray();
        entry.localPositions    = locals.ToArray();
        entry.targetPosAtAttach = _targetGO.transform.position;
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    void DoApply()
    {
        if (!_attached || _targetGO == null) return;

        // Snapshot target position once — all entries use the same delta
        Vector3 currentTargetPos = _targetGO.transform.position;

        foreach (var entry in _entries)
        {
            if (entry.pbMesh == null || entry.sharedVertexIds == null || entry.sharedVertexIds.Length == 0)
                continue;

            ApplyEntry(entry, currentTargetPos);
        }

        SceneView.RepaintAll();
    }

    void ApplyEntry(PBEntry entry, Vector3 currentTargetPos)
    {
        Vector3 worldDelta = currentTargetPos - entry.targetPosAtAttach;
        Vector3 localDelta = entry.pbMesh.transform.InverseTransformVector(worldDelta);

        Undo.RecordObject(entry.pbMesh, "PB Vertex Apply");
        Undo.RecordObject(entry.pbMesh.GetComponent<MeshFilter>().sharedMesh, "PB Vertex Apply");

        for (int i = 0; i < entry.sharedVertexIds.Length; i++)
        {
            Vector3 newLocalPos = entry.localPositions[i] + localDelta;
            entry.pbMesh.SetSharedVertexPosition(entry.sharedVertexIds[i], newLocalPos);
            entry.localPositions[i] = newLocalPos;
        }

        entry.pbMesh.ToMesh();
        entry.pbMesh.Refresh();

        // Update so next Apply is incremental from here
        entry.targetPosAtAttach = currentTargetPos;
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    void ClearAttach()
    {
        _attached = false;
        foreach (var e in _entries)
        {
            e.sharedVertexIds = null;
            e.localPositions  = null;
            e.targetPosAtAttach = Vector3.zero;
        }
        SceneView.RepaintAll();
    }

    // ── Diagnose ──────────────────────────────────────────────────────────────

    void DoDiagnose()
    {
        if (_targetGO == null) { _diagnoseText = "No target assigned."; Repaint(); return; }

        var targetRenderers = CollectRenderers(_targetGO);
        var targetMFs       = CollectMeshFilters(_targetGO);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Target: '{_targetGO.name}'  {targetRenderers.Count} renderers, {targetMFs.Count} readable meshes");
        sb.AppendLine();

        foreach (var entry in _entries)
        {
            if (entry.pbMesh == null) { sb.AppendLine("(missing entry)"); continue; }

            var positions   = entry.pbMesh.positions;
            var sharedVerts = entry.pbMesh.sharedVertices;
            var pbToWorld   = entry.pbMesh.transform.localToWorldMatrix;

            float minDist = float.MaxValue;
            float maxDist = 0f;

            for (int si = 0; si < sharedVerts.Count; si++)
            {
                int idx = sharedVerts[si][0];
                if (idx < 0 || idx >= positions.Count) continue;
                Vector3 worldPos = pbToWorld.MultiplyPoint3x4(positions[idx]);
                float dist;
                if (targetRenderers.Count > 0)
                    ClosestPointOnHierarchy(worldPos, targetRenderers, targetMFs, out dist);
                else
                    dist = Vector3.Distance(worldPos, _targetGO.transform.position);

                if (dist < minDist) minDist = dist;
                if (dist > maxDist) maxDist = dist;
            }

            string status   = minDist <= entry.threshold ? "OK" : $"NEEDS > {minDist:F3}m";
            string edgeInfo = _edgeLock ? $"  edge radius: {entry.edgeLockRadius:F3}m" : "  (face lock)";
            sb.AppendLine($"{entry.pbMesh.gameObject.name}");
            sb.AppendLine($"  verts: {sharedVerts.Count}  min: {minDist:F4}m  max: {maxDist:F4}m");
            sb.AppendLine($"  threshold: {entry.threshold:F3}m  [{status}]{edgeInfo}");
        }

        _diagnoseText = sb.ToString().TrimEnd();
        Repaint();
    }

    // ── Scene Gizmos ──────────────────────────────────────────────────────────

    void OnSceneGUI(SceneView sv)
    {
        if (!_attached) return;

        var prevColor = Handles.color;

        for (int ei = 0; ei < _entries.Count; ei++)
        {
            var entry = _entries[ei];
            if (entry.pbMesh == null || entry.sharedVertexIds == null) continue;

            Color col = kEntryColors[ei % kEntryColors.Length];
            var pbToWorld = entry.pbMesh.transform.localToWorldMatrix;

            Vector3 targetPos = _targetGO != null ? _targetGO.transform.position : Vector3.zero;

            for (int i = 0; i < entry.sharedVertexIds.Length; i++)
            {
                Vector3 worldPos = pbToWorld.MultiplyPoint3x4(entry.localPositions[i]);
                float   size     = UnityEditor.HandleUtility.GetHandleSize(worldPos) * 0.05f;

                Handles.color = new Color(col.r, col.g, col.b, 0.4f);
                Handles.DrawLine(worldPos, targetPos);

                Handles.color = col;
                Handles.DrawSolidDisc(worldPos, sv.camera.transform.forward, size);
            }

            // Disc at target pivot
            float anchorSize = UnityEditor.HandleUtility.GetHandleSize(targetPos) * 0.07f;
            Handles.color = kTargetColor;
            Handles.DrawSolidDisc(targetPos, sv.camera.transform.forward, anchorSize);
        }

        Handles.color = prevColor;
    }

    // ── Mesh helpers ──────────────────────────────────────────────────────────

    // Collect all non-particle Renderers from the target hierarchy for bounds-based distance.
    // Works on non-readable meshes and deeply nested addressable hierarchies.
    static List<Renderer> CollectRenderers(GameObject root)
    {
        var result = new List<Renderer>();
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is ParticleSystemRenderer)) result.Add(r);
        }
        return result;
    }

    // Collect readable MeshFilters for more accurate triangle-level distance when available.
    static List<MeshFilter> CollectMeshFilters(GameObject root)
    {
        var result = new List<MeshFilter>();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh != null && mf.sharedMesh.isReadable)
                result.Add(mf);
        }
        return result;
    }

    // Returns closest point and distance using renderers bounds (always works) with
    // triangle-level refinement when readable meshes are available.
    static Vector3 ClosestPointOnHierarchy(Vector3 worldPoint, List<Renderer> renderers,
        List<MeshFilter> mfs, out float dist)
    {
        // First pass: per-child bounds closest point (works on any mesh).
        // Using individual child bounds rather than aggregate — aggregate bounds of a complex
        // object like an airlock can be much larger than any single face, giving inflated distances.
        float   bestDist = float.MaxValue;
        Vector3 bestPt   = worldPoint;

        foreach (var r in renderers)
        {
            Vector3 pt = r.bounds.ClosestPoint(worldPoint);
            float   d  = (pt - worldPoint).magnitude;
            if (d < bestDist) { bestDist = d; bestPt = pt; }
        }

        // Second pass: refine with triangle-level accuracy if readable meshes exist
        if (mfs.Count > 0)
        {
            foreach (var mf in mfs)
            {
                float   d;
                Vector3 pt = ClosestPointOnMesh(worldPoint, mf, out d);
                if (d < bestDist) { bestDist = d; bestPt = pt; }
            }
        }

        dist = bestDist;
        return bestPt;
    }

    static Vector3 ClosestPointOnMesh(Vector3 worldPoint, MeshFilter mf, out float dist)
    {
        var     mesh     = mf.sharedMesh;
        var     tris     = mesh.triangles;
        var     verts    = mesh.vertices;
        var     m        = mf.transform.localToWorldMatrix;
        float   bestDist = float.MaxValue;
        Vector3 bestPt   = worldPoint;

        for (int ti = 0; ti < tris.Length; ti += 3)
        {
            Vector3 a  = m.MultiplyPoint3x4(verts[tris[ti]]);
            Vector3 b  = m.MultiplyPoint3x4(verts[tris[ti + 1]]);
            Vector3 c  = m.MultiplyPoint3x4(verts[tris[ti + 2]]);
            Vector3 pt = ClosestPointOnTriangle(worldPoint, a, b, c);
            float   d  = (pt - worldPoint).magnitude;
            if (d < bestDist) { bestDist = d; bestPt = pt; }
        }

        dist = bestDist;
        return bestPt;
    }

    static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return a;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return b;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + (d1 / (d1 - d3)) * ab;

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return c;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + (d2 / (d2 - d6)) * ac;

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            return b + ((d4 - d3) / ((d4 - d3) + (d5 - d6))) * (c - b);

        float denom = 1f / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }
}
