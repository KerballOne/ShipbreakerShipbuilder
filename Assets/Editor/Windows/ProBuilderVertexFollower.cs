using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

/// <summary>
/// v47
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
        public int            subdivideCount = 1;
        public int[]          sharedVertexIds;
        public Vector3[]      localPositions;
        public Vector3        targetPosAtAttach;
    }

    // ── Window state ──────────────────────────────────────────────────────────

    readonly List<PBEntry> _entries = new List<PBEntry>();
    GameObject             _targetGO;
    bool                   _attached;
    Vector2                _scroll;
    string                 _diagnoseText;

    // IBV preview — world-space points, cleared when IBV is applied or mesh changes
    List<Vector3> _ibvPreviewPoints;
    ProBuilderMesh _ibvPreviewMesh;

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

        EditorGUILayout.LabelField("PB Vertex Follower  v47", EditorStyles.miniLabel);

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
                _entries.Add(new PBEntry { pbMesh = pb });
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

            EditorGUI.indentLevel++;
            using (new EditorGUI.DisabledScope(_targetGO == null || entry.pbMesh == null))
            {
                // Subdivide row
                EditorGUILayout.BeginHorizontal();
                entry.subdivideCount = EditorGUILayout.IntSlider("Subdivide", entry.subdivideCount, 0, 8);
                if (GUILayout.Button("Auto", GUILayout.Width(42)))
                    AutoSetupEntry(entry);
                if (GUILayout.Button($"Apply ×{entry.subdivideCount}", GUILayout.Width(72)))
                    DoSubdivideFace(entry);
                EditorGUILayout.EndHorizontal();

                // IBV row
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Preview IBV"))
                    DoPreviewIBV(entry);
                bool hasPreview = _ibvPreviewMesh == entry.pbMesh && _ibvPreviewPoints != null && _ibvPreviewPoints.Count > 0;
                using (new EditorGUI.DisabledScope(!hasPreview))
                {
                    if (GUILayout.Button("Confirm IBV", GUILayout.Width(90)))
                        DoConfirmIBV(entry);
                }
                if (hasPreview && GUILayout.Button("✕", GUILayout.Width(26)))
                {
                    _ibvPreviewPoints = null;
                    _ibvPreviewMesh   = null;
                    SceneView.RepaintAll();
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;
        }

        if (_entries.Count == 0)
            EditorGUILayout.HelpBox("Select ProBuilder objects in the Hierarchy and click Add.", MessageType.None);

        EditorGUILayout.Space(6);

        // ── Section 3: Attach ─────────────────────────────────────────────────
        EditorGUILayout.LabelField("3. Attach", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Use Preview IBV → Confirm IBV on each mesh, then click Attach to lock the inserted vertices to the target.", MessageType.None);

        bool canAttach = _targetGO != null && _entries.Count > 0 && _entries.Exists(e => e.sharedVertexIds != null && e.sharedVertexIds.Length > 0);
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
        sb.AppendLine($"  dir: {dir}  gap: {gap:F4}m");
        sb.AppendLine($"  PB face: {pbFaceSize.x:F3}×{pbFaceSize.y:F3}m");
        sb.AppendLine($"  Target face: {targetFaceSize.x:F3}×{targetFaceSize.y:F3}m");
        sb.AppendLine($"  Ratio: {subX}×{subY} → {passes} subdivision pass(es)");

        entry.subdivideCount = passes;
        _diagnoseText = sb.ToString().TrimEnd();
        Repaint();
    }

    void DoSubdivideFace(PBEntry entry)
    {
        _diagnoseText = $"v16 DoSubdivideFace called: target={_targetGO != null} pb={entry.pbMesh != null} count={entry.subdivideCount}";
        Repaint();
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

        var diagSb = new System.Text.StringBuilder();
        diagSb.AppendLine($"v14 Subdivide '{pb.gameObject.name}'  dir:{dir}  passes:{entry.subdivideCount}");
        diagSb.AppendLine($"  seed face verts: {seedFace.distinctIndexes.Count}");

        var currentFaces = new List<Face> { seedFace };
        for (int iter = 0; iter < entry.subdivideCount; iter++)
        {
            diagSb.AppendLine($"  pass {iter + 1}: {currentFaces.Count} face(s) in");
            var newFaces = ConnectElements.Connect(pb, currentFaces);
            pb.ToMesh();
            pb.Refresh();
            int resultCount = newFaces != null ? newFaces.Length : 0;
            diagSb.AppendLine($"  pass {iter + 1}: Subdivide returned {resultCount} faces");
            currentFaces = newFaces != null ? new List<Face>(newFaces) : new List<Face>();
            if (currentFaces.Count == 0) break;
        }

        _diagnoseText = diagSb.ToString().TrimEnd();
        ClearAttach();
        SceneView.RepaintAll();
        Repaint();
    }

    // ── Insert boundary vertices ──────────────────────────────────────────────

    // Computes the world-space IBV points without modifying the mesh.
    // Returns the local-space points for insertion, or null on failure.
    List<Vector3> ComputeIBVPoints(PBEntry entry, out Face pbFace, out List<Face> allFacingFaces, out string log)
    {
        pbFace         = null;
        allFacingFaces = new List<Face>();
        log            = "";
        if (_targetGO == null || entry.pbMesh == null) return null;

        var pb        = entry.pbMesh;
        var pbToWorld = pb.transform.localToWorldMatrix;
        var worldToPB = pb.transform.worldToLocalMatrix;

        Bounds  pbBounds     = GetHierarchyBounds(pb.gameObject);
        Bounds  targetBounds = GetHierarchyBounds(_targetGO);
        Vector3 dir          = GetClosestFaceDirection(pbBounds, targetBounds);

        // Find the furthest face alignment value, then collect ALL faces within a tolerance
        // (the whole target-facing side, which may be many triangles after subdivision)
        float bestAlign = float.MinValue;
        foreach (var face in pb.faces)
        {
            Vector3 localCenter = Vector3.zero;
            int     count       = 0;
            foreach (int idx in face.distinctIndexes) { localCenter += pb.positions[idx]; count++; }
            if (count == 0) continue;
            float align = Vector3.Dot(pbToWorld.MultiplyPoint3x4(localCenter), dir);
            if (align > bestAlign) { bestAlign = align; pbFace = face; }
        }
        if (pbFace == null) { log = "No PB face found."; return null; }

        // Collect all faces on the same side (within 0.01m of bestAlign)
        foreach (var face in pb.faces)
        {
            Vector3 localCenter = Vector3.zero;
            int     count       = 0;
            foreach (int idx in face.distinctIndexes) { localCenter += pb.positions[idx]; count++; }
            if (count == 0) continue;
            float align = Vector3.Dot(pbToWorld.MultiplyPoint3x4(localCenter), dir);
            if (align >= bestAlign - 0.01f) allFacingFaces.Add(face);
        }

        Vector3 pbFaceNormalWorld = pbToWorld.MultiplyVector(FaceNormal(pb, pbFace)).normalized;
        Vector3 pbFacePtWorld     = pbToWorld.MultiplyPoint3x4(pb.positions[pbFace.distinctIndexes[0]]);

        List<Vector3> targetCorners = GetBoundsFaceCorners(targetBounds, -dir);
        if (targetCorners == null || targetCorners.Count == 0) { log = "Could not determine target face corners."; return null; }

        // Project target corners onto PB face plane along gap direction
        var localPoints = new List<Vector3>();
        foreach (var worldPt in targetCorners)
        {
            float denom = Vector3.Dot(pbFaceNormalWorld, dir);
            Vector3 projected;
            if (Mathf.Abs(denom) > 0.0001f)
            {
                float t = Vector3.Dot(pbFacePtWorld - worldPt, pbFaceNormalWorld) / denom;
                projected = worldPt + t * dir;
            }
            else
            {
                float t2 = Vector3.Dot(pbFacePtWorld - worldPt, pbFaceNormalWorld);
                projected = worldPt + t2 * pbFaceNormalWorld;
            }
            localPoints.Add(worldToPB.MultiplyPoint3x4(projected));
        }

        // Clamp to AABB of all facing faces combined
        Vector3 faceMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 faceMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var f in allFacingFaces)
            foreach (int idx in f.distinctIndexes)
            {
                faceMin = Vector3.Min(faceMin, pb.positions[idx]);
                faceMax = Vector3.Max(faceMax, pb.positions[idx]);
            }
        for (int i = 0; i < localPoints.Count; i++)
        {
            Vector3 p = localPoints[i];
            localPoints[i] = new Vector3(
                Mathf.Clamp(p.x, faceMin.x, faceMax.x),
                Mathf.Clamp(p.y, faceMin.y, faceMax.y),
                Mathf.Clamp(p.z, faceMin.z, faceMax.z));
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"v47  IBV preview for '{pb.gameObject.name}'  dir: {dir}");
        sb.AppendLine($"  target bounds center: {targetBounds.center:F3}  extents: {targetBounds.extents:F3}");
        sb.AppendLine($"  target corners (world):");
        foreach (var p in targetCorners) sb.AppendLine($"    {p:F3}");
        sb.AppendLine($"  points to insert (local, clamped):");
        foreach (var p in localPoints) sb.AppendLine($"    {p:F3}");
        sb.AppendLine("Click 'Confirm IBV' to apply, or ✕ to cancel.");
        log = sb.ToString().TrimEnd();

        return localPoints;
    }

    void DoPreviewIBV(PBEntry entry)
    {
        Face         pbFace;
        List<Face>   facingFaces;
        string       log;
        var localPoints = ComputeIBVPoints(entry, out pbFace, out facingFaces, out log);

        if (localPoints == null) { _diagnoseText = log; _ibvPreviewPoints = null; _ibvPreviewMesh = null; SceneView.RepaintAll(); Repaint(); return; }

        _diagnoseText = log;

        var pbToWorld = entry.pbMesh.transform.localToWorldMatrix;
        _ibvPreviewPoints = new List<Vector3>();
        foreach (var p in localPoints)
            _ibvPreviewPoints.Add(pbToWorld.MultiplyPoint3x4(p));
        _ibvPreviewMesh = entry.pbMesh;

        SceneView.RepaintAll();
        Repaint();
    }

    // Read-only: computes where existing face verts would move to match the boundary slice values.
    // Returns the would-be local positions (same logic as DoConfirmIBV but without SetSharedVertexPosition).
    List<Vector3> ComputeIBVSnappedPositions(ProBuilderMesh pb, List<Face> facingFaces, List<Vector3> localPoints, out string log)
    {
        var diagSb = new System.Text.StringBuilder();
        var result = new List<Vector3>();

        // Detect face normal axis from all facing faces combined
        float rangeX = 0f, rangeY = 0f, rangeZ = 0f;
        Vector3 p0 = pb.positions[facingFaces[0].distinctIndexes[0]];
        foreach (var face in facingFaces)
        foreach (int vi in face.distinctIndexes)
        {
            Vector3 p = pb.positions[vi];
            rangeX = Mathf.Max(rangeX, Mathf.Abs(p.x - p0.x));
            rangeY = Mathf.Max(rangeY, Mathf.Abs(p.y - p0.y));
            rangeZ = Mathf.Max(rangeZ, Mathf.Abs(p.z - p0.z));
        }
        int normalAxis = (rangeX <= rangeY && rangeX <= rangeZ) ? 0 :
                         (rangeY <= rangeX && rangeY <= rangeZ) ? 1 : 2;

        float lpRangeX = 0f, lpRangeY = 0f, lpRangeZ = 0f;
        foreach (var p in localPoints)
        {
            lpRangeX = Mathf.Max(lpRangeX, Mathf.Abs(p.x - localPoints[0].x));
            lpRangeY = Mathf.Max(lpRangeY, Mathf.Abs(p.y - localPoints[0].y));
            lpRangeZ = Mathf.Max(lpRangeZ, Mathf.Abs(p.z - localPoints[0].z));
        }
        if (normalAxis == 0) lpRangeX = -1f;
        else if (normalAxis == 1) lpRangeY = -1f;
        else lpRangeZ = -1f;

        int sliceAxis = (lpRangeX >= lpRangeY && lpRangeX >= lpRangeZ) ? 0 :
                        (lpRangeY >= lpRangeX && lpRangeY >= lpRangeZ) ? 1 : 2;

        System.Func<Vector3, float> getSliceVal = v =>
            sliceAxis == 0 ? v.x : sliceAxis == 1 ? v.y : v.z;
        System.Func<Vector3, float, Vector3> setSliceVal = (v, val) =>
            sliceAxis == 0 ? new Vector3(val, v.y, v.z) :
            sliceAxis == 1 ? new Vector3(v.x, val, v.z) :
                             new Vector3(v.x, v.y, val);

        var sliceValues = new List<float>();
        foreach (var p in localPoints)
        {
            float v = getSliceVal(p);
            bool found = false;
            foreach (var sv in sliceValues) if (Mathf.Abs(sv - v) < 0.001f) { found = true; break; }
            if (!found) sliceValues.Add(v);
        }

        var faceVertSIs = new List<int>();
        var facePosIdxSet = new HashSet<int>();
        foreach (var face in facingFaces)
            foreach (int vi in face.distinctIndexes)
                facePosIdxSet.Add(vi);
        for (int si = 0; si < pb.sharedVertices.Count; si++)
            foreach (int vi in pb.sharedVertices[si])
                if (facePosIdxSet.Contains(vi)) { faceVertSIs.Add(si); break; }

        // Log all distinct slice-axis values on the face for diagnosis
        var allFaceVals = new List<float>();
        foreach (int si in faceVertSIs)
        {
            float v = getSliceVal(pb.positions[pb.sharedVertices[si][0]]);
            bool found = false;
            foreach (float dv in allFaceVals) if (Mathf.Abs(dv - v) < 0.0001f) { found = true; break; }
            if (!found) allFaceVals.Add(v);
        }
        allFaceVals.Sort();
        diagSb.AppendLine($"Preview snap: normal axis {normalAxis}, slice axis {sliceAxis}, {faceVertSIs.Count} face verts");
        diagSb.AppendLine($"  distinct slice vals: {string.Join(", ", allFaceVals.ConvertAll(v => v.ToString("F4")))}");

        var claimedSIs = new HashSet<int>();
        foreach (float sliceVal in sliceValues)
        {
            // Find the closest existing Z value to the target slice
            float bestDist = float.MaxValue;
            foreach (int si in faceVertSIs)
            {
                if (claimedSIs.Contains(si)) continue;
                float d = Mathf.Abs(getSliceVal(pb.positions[pb.sharedVertices[si][0]]) - sliceVal);
                if (d < bestDist) bestDist = d;
            }

            // Grab ALL verts at that exact Z level (they share the same value after subdivision)
            float band = bestDist + 0.001f;
            foreach (int si in faceVertSIs)
            {
                if (claimedSIs.Contains(si)) continue;
                Vector3 pos = pb.positions[pb.sharedVertices[si][0]];
                if (Mathf.Abs(getSliceVal(pos) - sliceVal) <= band)
                {
                    Vector3 snapped = setSliceVal(pos, sliceVal);
                    result.Add(snapped);
                    claimedSIs.Add(si);
                    diagSb.AppendLine($"  SI {si} {pos:F3} → {snapped:F3}");
                }
            }
        }

        log = diagSb.ToString().TrimEnd();
        return result;
    }

    void DoConfirmIBV(PBEntry entry)
    {
        List<Face> facingFaces;
        string     log;
        var localPoints = ComputeIBVPoints(entry, out _, out facingFaces, out log);
        if (localPoints == null) { _diagnoseText = log; Repaint(); return; }

        var pb     = entry.pbMesh;
        var diagSb = new System.Text.StringBuilder();

        Undo.RegisterCompleteObjectUndo(new Object[] { pb, pb.GetComponent<MeshFilter>().sharedMesh }, "Insert Boundary Vertices");

        // -- Step 1: detect face normal axis (least variation) and the two face-plane axes --
        float rangeX = 0f, rangeY = 0f, rangeZ = 0f;
        Vector3 p0 = pb.positions[facingFaces[0].distinctIndexes[0]];
        foreach (var face in facingFaces)
        foreach (int vi in face.distinctIndexes)
        {
            Vector3 p = pb.positions[vi];
            rangeX = Mathf.Max(rangeX, Mathf.Abs(p.x - p0.x));
            rangeY = Mathf.Max(rangeY, Mathf.Abs(p.y - p0.y));
            rangeZ = Mathf.Max(rangeZ, Mathf.Abs(p.z - p0.z));
        }
        int normalAxis = (rangeX <= rangeY && rangeX <= rangeZ) ? 0 :
                         (rangeY <= rangeX && rangeY <= rangeZ) ? 1 : 2;
        int planeAxis1 = (normalAxis == 0) ? 1 : 0;
        int planeAxis2 = (normalAxis == 2) ? 1 : 2;

        // -- Step 2: compute rect bounds from the 4 projected target points --
        float rect1Min = float.MaxValue, rect1Max = float.MinValue;
        float rect2Min = float.MaxValue, rect2Max = float.MinValue;
        foreach (var p in localPoints)
        {
            float v1 = planeAxis1 == 0 ? p.x : planeAxis1 == 1 ? p.y : p.z;
            float v2 = planeAxis2 == 0 ? p.x : planeAxis2 == 1 ? p.y : p.z;
            if (v1 < rect1Min) rect1Min = v1; if (v1 > rect1Max) rect1Max = v1;
            if (v2 < rect2Min) rect2Min = v2; if (v2 > rect2Max) rect2Max = v2;
        }
        diagSb.AppendLine($"  normal:{normalAxis}  rect planeAxis1={planeAxis1}[{rect1Min:F3},{rect1Max:F3}]  planeAxis2={planeAxis2}[{rect2Min:F3},{rect2Max:F3}]");

        // -- Step 3: collect all face SIs (interior only — all indices must be on facing faces) --
        var facePosIdxSet = new HashSet<int>();
        foreach (var face in facingFaces)
            foreach (int vi in face.distinctIndexes)
                facePosIdxSet.Add(vi);

        var faceVertSIs = new List<int>();
        for (int si = 0; si < pb.sharedVertices.Count; si++)
            foreach (int vi in pb.sharedVertices[si])
                if (facePosIdxSet.Contains(vi)) { faceVertSIs.Add(si); break; }
        diagSb.AppendLine($"  face SIs: {faceVertSIs.Count}");

        // -- Step 4: find the full face extent along each plane axis --
        float face1Min = float.MaxValue, face1Max = float.MinValue;
        float face2Min = float.MaxValue, face2Max = float.MinValue;
        foreach (int si in faceVertSIs)
        {
            Vector3 p = pb.positions[pb.sharedVertices[si][0]];
            float v1 = planeAxis1 == 0 ? p.x : planeAxis1 == 1 ? p.y : p.z;
            float v2 = planeAxis2 == 0 ? p.x : planeAxis2 == 1 ? p.y : p.z;
            if (v1 < face1Min) face1Min = v1; if (v1 > face1Max) face1Max = v1;
            if (v2 < face2Min) face2Min = v2; if (v2 > face2Max) face2Max = v2;
        }
        diagSb.AppendLine($"  face extents: axis1[{face1Min:F3},{face1Max:F3}]  axis2[{face2Min:F3},{face2Max:F3}]");

        // -- Step 5: clamp and attach --
        // Moving rule:  only skip the far outer side edges (face2Min / face2Max) — permanent panel anchors.
        // Attaching rule: include any vert that falls within the target rect on axis1 (transit axis).
        //   axis1 outer rows (face1Min / face1Max) are included when they lie inside rect1 bounds.
        //   axis2 outer edges (face2Min / face2Max) are always excluded — they hold the panel in place.
        float outerTol = 0.001f;
        var ids    = new List<int>();
        var locals = new List<Vector3>();

        foreach (int si in faceVertSIs)
        {
            Vector3 pos = pb.positions[pb.sharedVertices[si][0]];
            float v1 = planeAxis1 == 0 ? pos.x : planeAxis1 == 1 ? pos.y : pos.z;
            float v2 = planeAxis2 == 0 ? pos.x : planeAxis2 == 1 ? pos.y : pos.z;

            // Axis2 outer edges are fixed anchors — never move or attach
            if (Mathf.Abs(v2 - face2Min) <= outerTol || Mathf.Abs(v2 - face2Max) <= outerTol) continue;

            // Axis1 outer rows: include only if they fall inside the target rect on axis1
            bool onAxis1Outer = Mathf.Abs(v1 - face1Min) <= outerTol || Mathf.Abs(v1 - face1Max) <= outerTol;
            if (onAxis1Outer && (v1 < rect1Min - outerTol || v1 > rect1Max + outerTol)) continue;

            // Clamp to rect bounds — only move if outside
            float new1 = Mathf.Clamp(v1, rect1Min, rect1Max);
            float new2 = Mathf.Clamp(v2, rect2Min, rect2Max);

            Vector3 newPos = pos;
            bool moved = Mathf.Abs(new1 - v1) >= outerTol || Mathf.Abs(new2 - v2) >= outerTol;
            if (moved)
            {
                if (planeAxis1 == 0) newPos.x = new1; else if (planeAxis1 == 1) newPos.y = new1; else newPos.z = new1;
                if (planeAxis2 == 0) newPos.x = new2; else if (planeAxis2 == 1) newPos.y = new2; else newPos.z = new2;
                pb.SetSharedVertexPosition(si, newPos);
            }

            // Track all — moved or not — so Apply translates the whole attached region
            ids.Add(si);
            locals.Add(newPos);
            diagSb.AppendLine($"  SI {si}  ({v1:F3},{v2:F3}) -> ({new1:F3},{new2:F3}){(moved ? "" : " [no move]")}");
        }

        diagSb.AppendLine($"  total attachment SIs: {ids.Count}");
        pb.ToMesh();
        pb.Refresh();

        _diagnoseText = diagSb.ToString().TrimEnd();

        entry.sharedVertexIds   = ids.ToArray();
        entry.localPositions    = locals.ToArray();
        entry.targetPosAtAttach = _targetGO.transform.position;
        _attached = true;

        _ibvPreviewPoints = null;
        _ibvPreviewMesh   = null;

        SceneView.RepaintAll();
        Repaint();
    }

    // Returns world-space corners of the face of bounds b pointing in direction faceDir
    static List<Vector3> GetBoundsFaceCorners(Bounds b, Vector3 faceDir)
    {
        // faceDir is cardinal — find the two perpendicular axes
        float ax = Mathf.Abs(faceDir.x), ay = Mathf.Abs(faceDir.y), az = Mathf.Abs(faceDir.z);
        Vector3 u, v;
        if (ax >= ay && ax >= az) { u = Vector3.up;      v = Vector3.forward; }
        else if (ay >= ax)        { u = Vector3.right;   v = Vector3.forward; }
        else                      { u = Vector3.right;   v = Vector3.up;      }

        Vector3 center = b.center + faceDir * FaceReach(b, faceDir);
        Vector3 eu = new Vector3(b.extents.x * Mathf.Abs(u.x), b.extents.y * Mathf.Abs(u.y), b.extents.z * Mathf.Abs(u.z));
        Vector3 ev = new Vector3(b.extents.x * Mathf.Abs(v.x), b.extents.y * Mathf.Abs(v.y), b.extents.z * Mathf.Abs(v.z));
        float   ru = eu.magnitude, rv = ev.magnitude;

        return new List<Vector3>
        {
            center + u * ru + v * rv,
            center - u * ru + v * rv,
            center - u * ru - v * rv,
            center + u * ru - v * rv,
        };
    }

    // Compute approximate face normal from a PB face's vertices
    static Vector3 FaceNormal(ProBuilderMesh pb, Face face)
    {
        var idx = face.distinctIndexes;
        if (idx.Count < 3) return Vector3.up;
        Vector3 a = pb.positions[idx[0]];
        Vector3 b = pb.positions[idx[1]];
        Vector3 c = pb.positions[idx[2]];
        return Vector3.Cross(b - a, c - a).normalized;
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
        foreach (var entry in _entries)
        {
            if (entry.pbMesh == null || entry.sharedVertexIds == null || entry.sharedVertexIds.Length == 0) continue;
            entry.targetPosAtAttach = _targetGO.transform.position;
        }

        _attached = true;
        SceneView.RepaintAll();
        Repaint();
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


    // ── Scene Gizmos ──────────────────────────────────────────────────────────

    void OnSceneGUI(SceneView sv)
    {
        var prevColor = Handles.color;

        // IBV preview dots — drawn regardless of attach state
        if (_ibvPreviewPoints != null && _ibvPreviewMesh != null)
        {
            Handles.color = Color.cyan;
            foreach (var wp in _ibvPreviewPoints)
            {
                float size = UnityEditor.HandleUtility.GetHandleSize(wp) * 0.07f;
                Handles.DrawSolidDisc(wp, sv.camera.transform.forward, size);
            }
            if (_ibvPreviewPoints.Count >= 2)
            {
                for (int i = 0; i < _ibvPreviewPoints.Count; i++)
                    Handles.DrawLine(_ibvPreviewPoints[i], _ibvPreviewPoints[(i + 1) % _ibvPreviewPoints.Count]);
            }
            Handles.color = prevColor;
        }

        if (!_attached) return;

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

}
