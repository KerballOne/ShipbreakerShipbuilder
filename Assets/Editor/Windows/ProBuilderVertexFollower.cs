using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

/// <summary>
/// v50
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
        public bool           subdivideApplied;
        public int[]          sharedVertexIds;   // set after Attach
        public Vector3[]      localPositions;    // set after Attach
        public Vector3        targetPosAtAttach;
        public int[]          movedVertexIds;    // set after Move Vertices, cleared after Attach
        public Vector3[]      movedLocalPositions;
        public int            previewVertCount = -1;  // set after Preview, -1 = not previewed
    }

    // ── Window state ──────────────────────────────────────────────────────────

    readonly List<PBEntry> _entries = new List<PBEntry>();
    GameObject             _targetGO;
    bool                   _attached;
    bool                   _verticesMoved;   // true after Move Vertices, before Attach
    Vector2                _scroll;
    string                 _diagnoseText;

    // IBV preview — rect outline points (cyan) and moveable vert points (red)
    List<Vector3> _ibvPreviewPoints;      // 4 rect corners for outline
    List<Vector3> _ibvPreviewMovePoints;  // verts that will be moved (red dots)


    // ── Entry colors (cycles for gizmos) ─────────────────────────────────────

    static readonly Color[] kEntryColors =
    {
        new Color(1f,   0.55f, 0f,   1f),  // orange
        new Color(0.2f, 1f,   0.3f, 1f),  // green
        new Color(0.8f, 0.2f, 1f,   1f),  // purple
        new Color(1f,   0.9f, 0.1f, 1f),  // yellow
    };
    static readonly Color kTargetColor = new Color(0.2f, 0.55f, 1f, 1f);  // blue
    static readonly Color kMovedColor  = new Color(1f,   0.2f, 0.2f, 1f); // red — moved, not yet attached

    // ── Window ────────────────────────────────────────────────────────────────

    [MenuItem("Shipbuilder/PB Vertex Follower", priority = 201)]
    static void Open() => GetWindow<ProBuilderVertexFollower>("PB Vertex Follower");

    void OnEnable()  => SceneView.duringSceneGui += OnSceneGUI;
    void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

    // ── GUI ───────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        // ── Section 1: Target ─────────────────────────────────────────────────
        EditorGUILayout.LabelField("1. Target Part", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        _targetGO = (GameObject)EditorGUILayout.ObjectField("Target", _targetGO, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck()) ClearAttach();

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
                var entry = new PBEntry { pbMesh = pb };
                entry.subdivideCount = _targetGO != null ? ComputeSubdivisions(entry) : 0;
                _entries.Add(entry);
                ClearAttach();
            }
        }

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            Color col = kEntryColors[i % kEntryColors.Length];

            EditorGUILayout.BeginHorizontal();
            var prevBG = GUI.backgroundColor;
            GUI.backgroundColor = col * 0.7f;
            string label = entry.pbMesh == null ? "(missing)" :
                entry.pbMesh.gameObject.name +
                (entry.sharedVertexIds != null ? $"  [{entry.sharedVertexIds.Length} attached]" :
                 entry.movedVertexIds  != null ? $"  [{entry.movedVertexIds.Length} moved]" :
                 entry.previewVertCount >= 0   ? $"  [{entry.previewVertCount} found]" : "");
            EditorGUILayout.LabelField(label, EditorStyles.helpBox);
            GUI.backgroundColor = prevBG;
            if (GUILayout.Button("✕", GUILayout.Width(28)))
            {
                _entries.RemoveAt(i);
                ClearAttach();
            }
            EditorGUILayout.EndHorizontal();

            if (_targetGO != null && entry.pbMesh != null && entry.subdivideCount > 0)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("  └ Subdivide", GUILayout.Width(95));
                GUILayout.FlexibleSpace();
                EditorGUI.BeginChangeCheck();
                if (GUILayout.Button("▼", GUILayout.Width(22)) && entry.subdivideCount > 1)
                    entry.subdivideCount--;
                entry.subdivideCount = EditorGUILayout.IntField(entry.subdivideCount, GUILayout.Width(28));
                if (GUILayout.Button("▲", GUILayout.Width(22)) && entry.subdivideCount < 8)
                    entry.subdivideCount++;
                if (EditorGUI.EndChangeCheck())
                    entry.subdivideApplied = false;
                if (GUILayout.Button("Detect", GUILayout.Width(50)))
                { _diagnoseText = ""; entry.subdivideCount = ComputeSubdivisions(entry, diagnose: true); entry.subdivideApplied = false; }
                var prevBG2 = GUI.backgroundColor;
                GUI.backgroundColor = entry.subdivideApplied
                    ? new Color(0.3f, 0.3f, 0.3f)
                    : new Color(0.9f, 0.25f, 0.25f);
                using (new EditorGUI.DisabledScope(entry.subdivideApplied))
                {
                    if (GUILayout.Button(entry.subdivideApplied ? "Applied" : "Apply", GUILayout.Width(54)))
                        DoSubdivideFace(entry);
                }
                GUI.backgroundColor = prevBG2;
                EditorGUILayout.EndHorizontal();
            }
        }

        if (_entries.Count == 0)
            EditorGUILayout.HelpBox("Select ProBuilder objects in the Hierarchy and click Add.", MessageType.None);

        EditorGUILayout.Space(4);

        // Preview button — previews all meshes at once
        bool hasPreview = _ibvPreviewPoints != null && _ibvPreviewPoints.Count > 0;
        using (new EditorGUI.DisabledScope(_targetGO == null || _entries.Count == 0))
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Preview"))
                DoPreviewAll();
            if (hasPreview && GUILayout.Button("✕", GUILayout.Width(26)))
            {
                _ibvPreviewPoints     = null;
                _ibvPreviewMovePoints = null;
                SceneView.RepaintAll();
            }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(6);

        // ── Section 3: Action ─────────────────────────────────────────────────
        EditorGUILayout.LabelField("3. Action", EditorStyles.boldLabel);

        bool hasVertsToMove = _targetGO != null && _entries.Exists(e => e.pbMesh != null);
        bool canAttachNow   = _verticesMoved && _targetGO != null && _entries.Exists(e => e.movedVertexIds != null && e.movedVertexIds.Length > 0);

        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(!hasVertsToMove || _verticesMoved))
        {
            if (GUILayout.Button("Move Vertices", GUILayout.Height(26)))
                DoMoveVertices();
        }
        using (new EditorGUI.DisabledScope(!canAttachNow))
        {
            if (GUILayout.Button("Attach to Target", GUILayout.Height(26)))
                DoAttachToTarget();
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(6);

        // ── Section 4: State ──────────────────────────────────────────────────
        EditorGUILayout.LabelField("4. State", EditorStyles.boldLabel);
        if (_attached)
        {
            int total = 0;
            foreach (var e in _entries)
                if (e.sharedVertexIds != null) total += e.sharedVertexIds.Length;
            EditorGUILayout.HelpBox($"{_entries.Count} mesh(es) attached — {total} total vertex group(s).", MessageType.None);
            SceneView.RepaintAll();
        }
        else if (_verticesMoved)
        {
            EditorGUILayout.HelpBox("Vertices moved. Click Attach to Target to lock them.", MessageType.None);
        }
        else
        {
            EditorGUILayout.HelpBox("Not attached.", MessageType.None);
        }

        EditorGUILayout.Space(6);

        // ── Section 5: Apply ─────────────────────────────────────────────────
        EditorGUILayout.LabelField("5. Apply", EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(_targetGO == null))
        {
            if (GUILayout.Button("Reselect Target Part", GUILayout.Height(24)))
                Selection.activeGameObject = _targetGO;
        }
        EditorGUILayout.LabelField("Move the target part, then click Apply.", EditorStyles.miniLabel);
        EditorGUILayout.Space(2);

        using (new EditorGUI.DisabledScope(!_attached))
        {
            var prevBG = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.9f, 0.25f, 0.25f);
            if (GUILayout.Button("Apply Deformation", GUILayout.Height(28)))
                DoApply();
            GUI.backgroundColor = prevBG;
        }

        EditorGUILayout.Space(4);
        if (GUILayout.Button("Clear Vertex Overlays"))
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

    int ComputeSubdivisions(PBEntry entry, bool diagnose = false)
    {
        if (_targetGO == null || entry.pbMesh == null) return 0;

        Bounds pbBounds     = GetHierarchyBounds(entry.pbMesh.gameObject);
        Bounds targetBounds = GetHierarchyBounds(_targetGO);
        Vector3 dir         = GetClosestFaceDirection(pbBounds, targetBounds);

        Vector2 pbFaceSize     = FaceSize(pbBounds, dir);
        Vector2 targetFaceSize = FaceSize(targetBounds, dir);

        int subX = targetFaceSize.x > 0f && pbFaceSize.x > targetFaceSize.x * 1.05f
            ? Mathf.CeilToInt(pbFaceSize.x / targetFaceSize.x) : 0;
        int subY = targetFaceSize.y > 0f && pbFaceSize.y > targetFaceSize.y * 1.05f
            ? Mathf.CeilToInt(pbFaceSize.y / targetFaceSize.y) : 0;
        int ratio = Mathf.Max(subX, subY);
        int result = Mathf.Clamp(ratio > 1 ? Mathf.CeilToInt(Mathf.Log(ratio, 2f)) : ratio, 0, 8);

        if (diagnose)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== Detect: '{entry.pbMesh.gameObject.name}' ===");
            sb.AppendLine($"  pbBounds  center:{pbBounds.center}  size:{pbBounds.size}");
            sb.AppendLine($"  tgtBounds center:{targetBounds.center}  size:{targetBounds.size}");
            sb.AppendLine($"  dir:{dir}");
            sb.AppendLine($"  pbFaceSize:({pbFaceSize.x:F6},{pbFaceSize.y:F6})  targetFaceSize:({targetFaceSize.x:F6},{targetFaceSize.y:F6})");
            sb.AppendLine($"  subX:{subX}  subY:{subY}  ratio:{ratio}  result:{result}");
            _diagnoseText = sb.ToString();
        }

        return result;
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

        var diagSb = new System.Text.StringBuilder();
        diagSb.AppendLine($"Subdivide '{pb.gameObject.name}'  dir:{dir}  passes:{entry.subdivideCount}");
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
        entry.subdivideApplied = true;
        ClearAttach();
        SceneView.RepaintAll();
        Repaint();
    }

    // ── Insert boundary vertices ──────────────────────────────────────────────

    // Computes the local-space rect corners for the target-facing side of a PB entry, or null on failure.
    List<Vector3> ComputeIBVPoints(PBEntry entry, out Face pbFace, out List<Face> allFacingFaces)
    {
        pbFace         = null;
        allFacingFaces = new List<Face>();
        if (_targetGO == null || entry.pbMesh == null) return null;

        var pb        = entry.pbMesh;
        var pbToWorld = pb.transform.localToWorldMatrix;
        var worldToPB = pb.transform.worldToLocalMatrix;

        Bounds  pbBounds     = GetHierarchyBounds(pb.gameObject);
        Bounds  targetBounds = GetHierarchyBounds(_targetGO);
        Vector3 dir          = GetClosestFaceDirection(pbBounds, targetBounds);

        // Find face whose center is furthest from the PB mesh center in the dir direction.
        // Subtract pbBounds.center so absolute world offset doesn't dominate.
        float bestAlign = float.MinValue;
        foreach (var face in pb.faces)
        {
            Vector3 localCenter = Vector3.zero;
            int     count       = 0;
            foreach (int idx in face.distinctIndexes) { localCenter += pb.positions[idx]; count++; }
            if (count == 0) continue;
            Vector3 worldCenter = pbToWorld.MultiplyPoint3x4(localCenter / count);
            float align = Vector3.Dot(worldCenter - pbBounds.center, dir);
            if (align > bestAlign) { bestAlign = align; pbFace = face; }
        }
        if (pbFace == null) return null;

        // Collect all faces on the same side (within 0.01m of bestAlign)
        foreach (var face in pb.faces)
        {
            Vector3 localCenter = Vector3.zero;
            int     count       = 0;
            foreach (int idx in face.distinctIndexes) { localCenter += pb.positions[idx]; count++; }
            if (count == 0) continue;
            Vector3 worldCenter = pbToWorld.MultiplyPoint3x4(localCenter / count);
            float align = Vector3.Dot(worldCenter - pbBounds.center, dir);
            if (align >= bestAlign - 0.01f) allFacingFaces.Add(face);
        }

        // Use dir as the projection plane normal so rotation of the PB mesh doesn't matter.
        Vector3 pbFaceNormalWorld = dir;
        Vector3 pbFacePtWorld     = pbToWorld.MultiplyPoint3x4(pb.positions[pbFace.distinctIndexes[0]]);

        List<Vector3> targetCorners = GetBoundsFaceCorners(targetBounds, -dir);
        if (targetCorners == null || targetCorners.Count == 0) return null;

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

        // Clamp projected rect to the AABB of the facing-side faces (allFacingFaces).
        // Using face-center alignment (not face normals) means all faces on the same side
        // contribute regardless of how subdivision has rotated their individual normals.
        Vector3 clampMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 clampMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var face in allFacingFaces)
        {
            foreach (int idx in face.distinctIndexes)
            {
                clampMin = Vector3.Min(clampMin, pb.positions[idx]);
                clampMax = Vector3.Max(clampMax, pb.positions[idx]);
            }
        }
        if (clampMin.x < float.MaxValue)
        {
            for (int i = 0; i < localPoints.Count; i++)
            {
                Vector3 p = localPoints[i];
                localPoints[i] = new Vector3(
                    Mathf.Clamp(p.x, clampMin.x, clampMax.x),
                    Mathf.Clamp(p.y, clampMin.y, clampMax.y),
                    Mathf.Clamp(p.z, clampMin.z, clampMax.z));
            }
        }

        return localPoints;
    }

    // Preview the target rect for all entries at once (cyan dots in scene)
    void DoPreviewAll()
    {
        _ibvPreviewPoints     = new List<Vector3>();
        _ibvPreviewMovePoints = new List<Vector3>();
        _diagnoseText         = "";
        foreach (var entry in _entries) entry.previewVertCount = -1;

        foreach (var entry in _entries)
        {
            if (entry.pbMesh == null) continue;
            Face       pbFace;
            List<Face> facingFaces;
            var localPoints = ComputeIBVPoints(entry, out pbFace, out facingFaces);
            if (localPoints == null) continue;

            var pb        = entry.pbMesh;
            var pbToWorld = pb.transform.localToWorldMatrix;

            // Rect outline points (4 corners)
            foreach (var p in localPoints)
                _ibvPreviewPoints.Add(pbToWorld.MultiplyPoint3x4(p));

            // Compute which verts would be moved — same logic as DoMoveVertices but read-only
            ComputeFaceAxes(pb, facingFaces, localPoints,
                out int planeAxis1, out int planeAxis2,
                out float rect1Min, out float rect1Max,
                out float rect2Min, out float rect2Max,
                out float face1Min, out float face1Max,
                out float face2Min, out float face2Max,
                out var faceVertSIs);

            // Collect verts that will be moved or attached — at their post-move positions
            float outerTol = 0.001f;
            int countBefore = _ibvPreviewMovePoints.Count;
            foreach (int si in faceVertSIs)
            {
                Vector3 pos = pb.positions[pb.sharedVertices[si][0]];
                float v1 = planeAxis1 == 0 ? pos.x : planeAxis1 == 1 ? pos.y : pos.z;
                float v2 = planeAxis2 == 0 ? pos.x : planeAxis2 == 1 ? pos.y : pos.z;

                // A face outer edge is a fixed anchor unless it coincides with a rect boundary line.
                // axis1 outer edge is fixed if its value doesn't match rect1Min or rect1Max.
                // axis2 outer edge is fixed if its value doesn't match rect2Min or rect2Max.
                bool onAxis1Outer = Mathf.Abs(v1 - face1Min) <= outerTol || Mathf.Abs(v1 - face1Max) <= outerTol;
                bool onAxis2Outer = Mathf.Abs(v2 - face2Min) <= outerTol || Mathf.Abs(v2 - face2Max) <= outerTol;
                // Use a larger coincidence tolerance to handle bounds imprecision (~5cm)
                float coincideTol = 0.05f;
                bool axis1EdgeCoincides = Mathf.Abs(v1 - rect1Min) <= coincideTol || Mathf.Abs(v1 - rect1Max) <= coincideTol;
                bool axis2EdgeCoincides = Mathf.Abs(v2 - rect2Min) <= coincideTol || Mathf.Abs(v2 - rect2Max) <= coincideTol;
                bool isFixedAnchor = (onAxis1Outer && !axis1EdgeCoincides) || (onAxis2Outer && !axis2EdgeCoincides);

                float new1 = isFixedAnchor ? v1 : Mathf.Clamp(v1, rect1Min, rect1Max);
                float new2 = isFixedAnchor ? v2 : Mathf.Clamp(v2, rect2Min, rect2Max);

                // Only show verts that pass the attach rule (within rect after clamping)
                if (new1 < rect1Min - outerTol || new1 > rect1Max + outerTol) continue;
                if (new2 < rect2Min - outerTol || new2 > rect2Max + outerTol) continue;

                Vector3 displayPos = pos;
                bool wouldMove = Mathf.Abs(new1 - v1) >= outerTol || Mathf.Abs(new2 - v2) >= outerTol;
                if (wouldMove)
                {
                    if (planeAxis1 == 0) displayPos.x = new1; else if (planeAxis1 == 1) displayPos.y = new1; else displayPos.z = new1;
                    if (planeAxis2 == 0) displayPos.x = new2; else if (planeAxis2 == 1) displayPos.y = new2; else displayPos.z = new2;
                }
                _ibvPreviewMovePoints.Add(pbToWorld.MultiplyPoint3x4(displayPos));
            }
            entry.previewVertCount = _ibvPreviewMovePoints.Count - countBefore;
            _diagnoseText += $"'{pb.gameObject.name}': {entry.previewVertCount} found  rect1:[{rect1Min:F3},{rect1Max:F3}] rect2:[{rect2Min:F3},{rect2Max:F3}]  face1:[{face1Min:F3},{face1Max:F3}] face2:[{face2Min:F3},{face2Max:F3}]\n";
        }

        SceneView.RepaintAll();
        Repaint();
    }

    // Runs the move step on all entries: clamps non-corner face verts outside the rect inward.
    // Stores results in entry.movedVertexIds / movedLocalPositions for the scene gizmo and Attach step.
    void DoMoveVertices()
    {
        var diagSb = new System.Text.StringBuilder();

        foreach (var entry in _entries)
        {
            if (entry.pbMesh == null) continue;

            List<Face> facingFaces;
            var localPoints = ComputeIBVPoints(entry, out _, out facingFaces);
            if (localPoints == null) continue;

            var pb = entry.pbMesh;
            Undo.RegisterCompleteObjectUndo(new Object[] { pb, pb.GetComponent<MeshFilter>().sharedMesh }, "Move Boundary Vertices");

            ComputeFaceAxes(pb, facingFaces, localPoints,
                out int planeAxis1, out int planeAxis2,
                out float rect1Min, out float rect1Max,
                out float rect2Min, out float rect2Max,
                out float face1Min, out float face1Max,
                out float face2Min, out float face2Max,
                out List<int> faceVertSIs);

            float outerTol = 0.001f;
            var movedIds    = new List<int>();
            var movedLocals = new List<Vector3>();

            foreach (int si in faceVertSIs)
            {
                Vector3 pos = pb.positions[pb.sharedVertices[si][0]];
                float v1 = planeAxis1 == 0 ? pos.x : planeAxis1 == 1 ? pos.y : pos.z;
                float v2 = planeAxis2 == 0 ? pos.x : planeAxis2 == 1 ? pos.y : pos.z;

                // A face outer edge is a fixed anchor unless it coincides with a rect boundary line.
                // axis1 outer edge is fixed if its value doesn't match rect1Min or rect1Max.
                // axis2 outer edge is fixed if its value doesn't match rect2Min or rect2Max.
                bool onAxis1Outer = Mathf.Abs(v1 - face1Min) <= outerTol || Mathf.Abs(v1 - face1Max) <= outerTol;
                bool onAxis2Outer = Mathf.Abs(v2 - face2Min) <= outerTol || Mathf.Abs(v2 - face2Max) <= outerTol;
                // Use a larger coincidence tolerance to handle bounds imprecision (~5cm)
                float coincideTol = 0.05f;
                bool axis1EdgeCoincides = Mathf.Abs(v1 - rect1Min) <= coincideTol || Mathf.Abs(v1 - rect1Max) <= coincideTol;
                bool axis2EdgeCoincides = Mathf.Abs(v2 - rect2Min) <= coincideTol || Mathf.Abs(v2 - rect2Max) <= coincideTol;
                bool isFixedAnchor = (onAxis1Outer && !axis1EdgeCoincides) || (onAxis2Outer && !axis2EdgeCoincides);

                float new1 = v1, new2 = v2;
                if (!isFixedAnchor)
                {
                    new1 = Mathf.Clamp(v1, rect1Min, rect1Max);
                    new2 = Mathf.Clamp(v2, rect2Min, rect2Max);
                }

                bool moved = Mathf.Abs(new1 - v1) >= outerTol || Mathf.Abs(new2 - v2) >= outerTol;
                if (moved)
                {
                    Vector3 newPos = pos;
                    if (planeAxis1 == 0) newPos.x = new1; else if (planeAxis1 == 1) newPos.y = new1; else newPos.z = new1;
                    if (planeAxis2 == 0) newPos.x = new2; else if (planeAxis2 == 1) newPos.y = new2; else newPos.z = new2;
                    pb.SetSharedVertexPosition(si, newPos);
                    pos = newPos;
                }

                // Only track verts that pass the attach rule (within rect after clamping)
                if (new1 < rect1Min - outerTol || new1 > rect1Max + outerTol) continue;
                if (new2 < rect2Min - outerTol || new2 > rect2Max + outerTol) continue;

                movedIds.Add(si);
                movedLocals.Add(pos);
            }

            pb.ToMesh();
            pb.Refresh();

            entry.movedVertexIds    = movedIds.ToArray();
            entry.movedLocalPositions = movedLocals.ToArray();
            diagSb.AppendLine($"'{pb.gameObject.name}': {movedIds.Count} verts processed");
        }

        _verticesMoved        = true;
        _ibvPreviewPoints     = null;
        _ibvPreviewMovePoints = null;

        _diagnoseText     = diagSb.ToString().TrimEnd();
        SceneView.RepaintAll();
        Repaint();
    }

    // Runs the attach step: promotes moved verts within rect boundary to sharedVertexIds.
    void DoAttachToTarget()
    {
        var diagSb = new System.Text.StringBuilder();

        foreach (var entry in _entries)
        {
            if (entry.pbMesh == null || entry.movedVertexIds == null) continue;

            List<Face> facingFaces;
            var localPoints = ComputeIBVPoints(entry, out _, out facingFaces);
            if (localPoints == null) continue;

            var pb = entry.pbMesh;
            ComputeFaceAxes(pb, facingFaces, localPoints,
                out int planeAxis1, out int planeAxis2,
                out float rect1Min, out float rect1Max,
                out float rect2Min, out float rect2Max,
                out float face1Min, out float face1Max,
                out float face2Min, out float face2Max,
                out _);

            float outerTol = 0.001f;
            var ids    = new List<int>();
            var locals = new List<Vector3>();

            for (int i = 0; i < entry.movedVertexIds.Length; i++)
            {
                int     si  = entry.movedVertexIds[i];
                Vector3 pos = entry.movedLocalPositions[i];
                float v1 = planeAxis1 == 0 ? pos.x : planeAxis1 == 1 ? pos.y : pos.z;
                float v2 = planeAxis2 == 0 ? pos.x : planeAxis2 == 1 ? pos.y : pos.z;

                if (v1 >= rect1Min - outerTol && v1 <= rect1Max + outerTol &&
                    v2 >= rect2Min - outerTol && v2 <= rect2Max + outerTol)
                {
                    ids.Add(si);
                    locals.Add(pos);
                }
            }

            entry.sharedVertexIds     = ids.ToArray();
            entry.localPositions      = locals.ToArray();
            entry.targetPosAtAttach   = _targetGO.transform.position;
            entry.movedVertexIds      = null;
            entry.movedLocalPositions = null;
            diagSb.AppendLine($"'{pb.gameObject.name}': {ids.Count} verts attached");
        }

        _attached      = true;
        _verticesMoved = false;
        _diagnoseText  = diagSb.ToString().TrimEnd();
        SceneView.RepaintAll();
        Repaint();
    }

    // Shared axis/rect/extent computation used by both DoMoveVertices and DoAttachToTarget
    static void ComputeFaceAxes(ProBuilderMesh pb, List<Face> facingFaces, List<Vector3> localPoints,
        out int planeAxis1, out int planeAxis2,
        out float rect1Min, out float rect1Max,
        out float rect2Min, out float rect2Max,
        out float face1Min, out float face1Max,
        out float face2Min, out float face2Max,
        out List<int> faceVertSIs)
    {
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
        planeAxis1 = (normalAxis == 0) ? 1 : 0;
        planeAxis2 = (normalAxis == 2) ? 1 : 2;

        rect1Min = float.MaxValue; rect1Max = float.MinValue;
        rect2Min = float.MaxValue; rect2Max = float.MinValue;
        foreach (var p in localPoints)
        {
            float v1 = planeAxis1 == 0 ? p.x : planeAxis1 == 1 ? p.y : p.z;
            float v2 = planeAxis2 == 0 ? p.x : planeAxis2 == 1 ? p.y : p.z;
            if (v1 < rect1Min) rect1Min = v1; if (v1 > rect1Max) rect1Max = v1;
            if (v2 < rect2Min) rect2Min = v2; if (v2 > rect2Max) rect2Max = v2;
        }

        var facePosIdxSet = new HashSet<int>();
        foreach (var face in facingFaces)
            foreach (int vi in face.distinctIndexes)
                facePosIdxSet.Add(vi);
        faceVertSIs = new List<int>();
        for (int si = 0; si < pb.sharedVertices.Count; si++)
            foreach (int vi in pb.sharedVertices[si])
                if (facePosIdxSet.Contains(vi)) { faceVertSIs.Add(si); break; }

        face1Min = float.MaxValue; face1Max = float.MinValue;
        face2Min = float.MaxValue; face2Max = float.MinValue;
        foreach (int si in faceVertSIs)
        {
            Vector3 p = pb.positions[pb.sharedVertices[si][0]];
            float v1 = planeAxis1 == 0 ? p.x : planeAxis1 == 1 ? p.y : p.z;
            float v2 = planeAxis2 == 0 ? p.x : planeAxis2 == 1 ? p.y : p.z;
            if (v1 < face1Min) face1Min = v1; if (v1 > face1Max) face1Max = v1;
            if (v2 < face2Min) face2Min = v2; if (v2 > face2Max) face2Max = v2;
        }
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
        _attached      = false;
        _verticesMoved = false;
        foreach (var e in _entries)
        {
            e.sharedVertexIds     = null;
            e.localPositions      = null;
            e.targetPosAtAttach   = Vector3.zero;
            e.movedVertexIds      = null;
            e.movedLocalPositions = null;
            e.previewVertCount    = -1;
        }
        _ibvPreviewPoints     = null;
        _ibvPreviewMovePoints = null;
        SceneView.RepaintAll();
    }


    // ── Scene Gizmos ──────────────────────────────────────────────────────────

    void OnSceneGUI(SceneView sv)
    {
        var prevColor = Handles.color;

        // Rect outline (cyan lines, no corner dots)
        if (_ibvPreviewPoints != null && _ibvPreviewPoints.Count >= 2)
        {
            Handles.color = Color.cyan;
            for (int i = 0; i < _ibvPreviewPoints.Count; i++)
                Handles.DrawLine(_ibvPreviewPoints[i], _ibvPreviewPoints[(i + 1) % _ibvPreviewPoints.Count]);
        }

        // Verts that will be moved (red dots) — shown during preview and after move
        var moveDotsSource = (_ibvPreviewMovePoints != null && _ibvPreviewMovePoints.Count > 0)
            ? _ibvPreviewMovePoints : null;
        if (moveDotsSource != null)
        {
            Handles.color = kMovedColor;
            foreach (var wp in moveDotsSource)
            {
                float size = UnityEditor.HandleUtility.GetHandleSize(wp) * 0.05f;
                Handles.DrawSolidDisc(wp, sv.camera.transform.forward, size);
            }
        }

        // After Move Vertices: show all tracked face verts in red until Attach is clicked
        if (_verticesMoved)
        {
            Handles.color = kMovedColor;
            foreach (var entry in _entries)
            {
                if (entry.pbMesh == null || entry.movedLocalPositions == null) continue;
                var pbToWorld = entry.pbMesh.transform.localToWorldMatrix;
                foreach (var localPos in entry.movedLocalPositions)
                {
                    Vector3 wp   = pbToWorld.MultiplyPoint3x4(localPos);
                    float   size = UnityEditor.HandleUtility.GetHandleSize(wp) * 0.05f;
                    Handles.DrawSolidDisc(wp, sv.camera.transform.forward, size);
                }
            }
        }

        if (!_attached) { Handles.color = prevColor; return; }

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
