using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Replaces a selected cylinder mesh with a parametric biarc tube that departs flush from
/// a picked source end-cap and arrives flush at a picked target face on any other mesh.
/// The biarc (two tangent-joined circular arcs) is solved automatically from the two
/// face normals; no manual axis/angle input is required.
/// </summary>
public class MeshCurveDeformer : EditorWindow
{
    // ── Pick state ────────────────────────────────────────────────────────────

    struct PickedFace
    {
        public Vector3 point;
        public Vector3 normal;
        public GameObject source;
    }

    PickedFace? _srcFace;   // end-cap of the cylinder being reshaped
    PickedFace? _dstFace;   // target face on another mesh

    bool _pickingSrc;
    bool _pickingDst;

    // Lock: remember which GO the tool is operating on so clicking a target face
    // doesn't steal Selection.activeGameObject.
    GameObject _lockedTarget;

    // Per-pick "straight" flags: when checked, that end's normal is ignored for path direction
    // (the tube goes straight A→B), but the end cap is still angled flush to the face normal.
    bool _srcStraight;
    bool _dstStraight;

    // ── UI params ─────────────────────────────────────────────────────────────

    int   _ringCount          = 16;
    int   _sidesPerRing       = 16;
    // Max concavity volume (m³) lost when the game makes the collider convex.
    // Tube is split into segments when any segment's concavity volume exceeds this.
    float _maxConcavityVolume = 0.05f;

    Vector2 _scroll;

    // ── Gizmo preview cache ───────────────────────────────────────────────────

    BiarcResult? _preview;

    // ── Constants ─────────────────────────────────────────────────────────────

    string _saveFolder;

    string SaveFolder => string.IsNullOrEmpty(_saveFolder) ? (_saveFolder = DefaultSaveFolder()) : _saveFolder;

    static string DefaultSaveFolder()
    {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
        foreach (var root in scene.GetRootGameObjects())
        {
            var srcPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            if (string.IsNullOrEmpty(srcPath)) continue;
            var dir = Path.GetDirectoryName(srcPath).Replace('\\', '/');
            var shipName = Path.GetFileName(dir);
            if (srcPath == $"{dir}/{shipName}.prefab" && AssetDatabase.IsValidFolder(dir))
                return dir + "/Meshes/CurvedMeshes";
        }
        return "Assets/_CustomShips/Meshes/CurvedMeshes";
    }

    static readonly Color kSrcColor  = new Color(1f, 0.55f, 0f, 1f);  // orange
    static readonly Color kDstColor  = new Color(0.2f, 0.55f, 1f, 1f); // blue
    static readonly Color kArcColor  = new Color(0.2f, 1f, 0.3f, 1f);  // green

    // ── Window ────────────────────────────────────────────────────────────────

    [MenuItem("Shipbuilder/Mesh Curve Deformer", priority = 200)]
    static void Open() => GetWindow<MeshCurveDeformer>("Mesh Curve Deformer");

    void OnEnable()  => SceneView.duringSceneGui += OnSceneGUI;
    void OnDisable() { SceneView.duringSceneGui -= OnSceneGUI; _pickingSrc = _pickingDst = false; }

    // ── GUI ───────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        // Prefer the locked target (set when source face is picked).
        // Also accept the current hierarchy selection when no lock is held, and update
        // the lock whenever a valid MeshFilter GO is selected so Apply always has a target.
        if (_lockedTarget == null && Selection.activeGameObject != null
            && Selection.activeGameObject.GetComponent<MeshFilter>() != null)
            _lockedTarget = Selection.activeGameObject;

        var target = _lockedTarget != null ? _lockedTarget : Selection.activeGameObject;
        var mf = target != null ? target.GetComponent<MeshFilter>() : null;
        bool validTarget = mf != null && mf.sharedMesh != null;

        if (!validTarget)
            EditorGUILayout.HelpBox("Select a GameObject with a MeshFilter.", MessageType.Warning);

        if (validTarget)
        {
            var s = target.transform.lossyScale;
            if (Mathf.Abs(s.x - 1f) > 1e-4f || Mathf.Abs(s.y - 1f) > 1e-4f || Mathf.Abs(s.z - 1f) > 1e-4f)
                EditorGUILayout.HelpBox(
                    $"Non-unit scale detected ({s.x:F3}, {s.y:F3}, {s.z:F3}). " +
                    "Run Lock In Rescale on this object before deforming meshes, and again before using Custom Part Wizard. " +
                    "In-game joints and mass require unit scale.",
                    MessageType.Warning);
        }

        EditorGUILayout.Space(4);

        // ── Source end pick ───────────────────────────────────────────────────
        EditorGUILayout.LabelField("1. Source End (cylinder end-cap to extend)", EditorStyles.boldLabel);
        bool prevSrcStraight = _srcStraight;
        DrawPickRow("Pick Source End", ref _pickingSrc, ref _pickingDst, _srcFace, ref _srcStraight,
            kSrcColor, () => { _srcFace = null; _preview = null; SceneView.RepaintAll(); });
        if (_srcStraight != prevSrcStraight) _preview = null;

        if (_srcFace.HasValue)
        {
            var f = _srcFace.Value;
            EditorGUILayout.HelpBox($"Point:  {f.point:F3}\nNormal: {f.normal:F3}", MessageType.None);
        }

        EditorGUILayout.Space(6);

        // ── Target face pick ──────────────────────────────────────────────────
        EditorGUILayout.LabelField("2. Target Face (mesh to connect to)", EditorStyles.boldLabel);
        bool prevDstStraight = _dstStraight;
        DrawPickRow("Pick Target Face", ref _pickingDst, ref _pickingSrc, _dstFace, ref _dstStraight,
            kDstColor, () => { _dstFace = null; _preview = null; SceneView.RepaintAll(); });
        if (_dstStraight != prevDstStraight) _preview = null;

        if (_dstFace.HasValue)
        {
            var f = _dstFace.Value;
            EditorGUILayout.HelpBox($"Point:  {f.point:F3}\nNormal: {f.normal:F3}", MessageType.None);
        }

        EditorGUILayout.Space(6);

        // ── Preview info ──────────────────────────────────────────────────────
        bool straightMode = _srcStraight && _dstStraight;
        if (_srcFace.HasValue && _dstFace.HasValue)
        {
            if (!_preview.HasValue && !straightMode)
            {
                // Biarc: use normals for tangents only on the ends that aren't marked straight
                Vector3 T0 = _srcStraight ? (_dstFace.Value.point - _srcFace.Value.point).normalized : _srcFace.Value.normal;
                Vector3 T1 = _dstStraight ? (_srcFace.Value.point - _dstFace.Value.point).normalized : -_dstFace.Value.normal;
                _preview = SolveBiarc(_srcFace.Value.point, T0, _dstFace.Value.point, T1);
            }

            EditorGUILayout.LabelField("Path Preview", EditorStyles.boldLabel);
            if (straightMode)
            {
                float len = (_dstFace.Value.point - _srcFace.Value.point).magnitude;
                EditorGUILayout.HelpBox($"Straight tube  |  length: {len:F3}", MessageType.None);
            }
            else if (_preview.HasValue)
            {
                var b = _preview.Value;
                EditorGUILayout.HelpBox(
                    $"Arc 1 — radius: {b.r1:F3}  angle: {b.angle1 * Mathf.Rad2Deg:F1}°  length: {b.r1 * b.angle1:F3}\n" +
                    $"Arc 2 — radius: {b.r2:F3}  angle: {b.angle2 * Mathf.Rad2Deg:F1}°  length: {b.r2 * b.angle2:F3}\n" +
                    $"Total length: {b.r1 * b.angle1 + b.r2 * b.angle2:F3}",
                    MessageType.None);
            }
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Tube Settings", EditorStyles.boldLabel);
        int newRingCount = EditorGUILayout.IntSlider("Ring Count", _ringCount, 4, 64);
        if (newRingCount != _ringCount) { _ringCount = newRingCount; _preview = null; }
        _sidesPerRing = EditorGUILayout.IntSlider("Sides Per Ring", _sidesPerRing, 4, 32);

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("Split Settings", EditorStyles.boldLabel);
        float newVol = EditorGUILayout.FloatField("Max Concavity Volume (m³)", _maxConcavityVolume);
        if (newVol > 0f) _maxConcavityVolume = newVol;
        EditorGUILayout.HelpBox(
            "Tube segments are split when the volume lost to convex-hull conversion exceeds this.\n" +
            "≈ tubeArea × chordDepth per segment.  Lower = more splits, higher = fewer.",
            MessageType.None);

        EditorGUILayout.Space(8);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("Output:", EditorStyles.miniLabel, GUILayout.Width(46));
        _saveFolder = EditorGUILayout.TextField(SaveFolder);
        if (GUILayout.Button("Browse", GUILayout.Width(60)))
        {
            var picked = EditorUtility.OpenFolderPanel("Select Output Folder", Application.dataPath, "");
            if (!string.IsNullOrEmpty(picked) && picked.StartsWith(Application.dataPath))
                _saveFolder = "Assets" + picked.Substring(Application.dataPath.Length).Replace('\\', '/');
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);
        bool canApply = validTarget && _srcFace.HasValue && _dstFace.HasValue &&
                        (straightMode || _preview.HasValue);
        using (new EditorGUI.DisabledScope(!canApply))
        {
            if (GUILayout.Button("Apply", GUILayout.Height(30)))
                Apply(target, mf);
        }

        if (GUILayout.Button("Clear Picks"))
        {
            _srcFace = _dstFace = null;
            _preview = null;
            _pickingSrc = _pickingDst = false;
            _srcStraight = _dstStraight = false;
            _lockedTarget = null;
            SceneView.RepaintAll();
        }

        EditorGUILayout.EndScrollView();
    }

    void DrawPickRow(string label, ref bool pickingThis, ref bool pickingOther,
        PickedFace? current, ref bool straight, Color activeColor, System.Action onClear)
    {
        EditorGUILayout.BeginHorizontal();
        var prevBG = GUI.backgroundColor;

        string btnText;
        int btnHeight = 26;
        var btnStyle = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, wordWrap = true };

        if (pickingThis)
        {
            GUI.backgroundColor = new Color(0.3f, 0.6f, 1f);
            btnText = "Cancel";
        }
        else if (current.HasValue && current.Value.source != null)
        {
            GUI.backgroundColor = activeColor * 0.8f;
            btnText = current.Value.source.name;
        }
        else
        {
            btnText = label;
        }

        if (GUILayout.Button(btnText, btnStyle, GUILayout.Height(btnHeight)))
        {
            pickingThis = !pickingThis;
            if (pickingThis) { pickingOther = false; SceneView.lastActiveSceneView?.Focus(); }
        }
        GUI.backgroundColor = prevBG;

        straight = EditorGUILayout.ToggleLeft("Straight", straight, GUILayout.Width(68));

        using (new EditorGUI.DisabledScope(!current.HasValue))
        {
            if (GUILayout.Button("✕", GUILayout.Width(28), GUILayout.Height(26)))
                onClear();
        }
        EditorGUILayout.EndHorizontal();
    }

    // ── Scene GUI ─────────────────────────────────────────────────────────────

    void OnSceneGUI(SceneView sv)
    {
        DrawFaceGizmo(_srcFace, kSrcColor, sv);
        DrawFaceGizmo(_dstFace, kDstColor, sv);

        // Path preview
        if (_srcFace.HasValue && _dstFace.HasValue)
        {
            var prevC = Handles.color;
            Handles.color = kArcColor;
            if (_srcStraight && _dstStraight)
            {
                Handles.DrawAAPolyLine(12f, _srcFace.Value.point, _dstFace.Value.point);
            }
            else if (_preview.HasValue)
            {
                var pts = SampleBiarc(_preview.Value, Mathf.Max(_ringCount, 16));
                if (pts.Count >= 2) Handles.DrawAAPolyLine(12f, pts.ToArray());
            }
            Handles.color = prevC;
        }

        bool anyPicking = _pickingSrc || _pickingDst;
        if (!anyPicking) return;

        // Consume the default control so Unity's scene-click selection handler doesn't fire.
        // This prevents clicking a target face from changing Selection.activeGameObject.
        int controlId = GUIUtility.GetControlID(FocusType.Passive);
        HandleUtility.AddDefaultControl(controlId);

        // Crosshair
        Handles.BeginGUI();
        var r = sv.position;
        EditorGUI.DrawRect(new Rect(r.width * 0.5f - 10, r.height * 0.5f - 1, 20, 2), Color.cyan);
        EditorGUI.DrawRect(new Rect(r.width * 0.5f - 1, r.height * 0.5f - 10, 2, 20), Color.cyan);
        Handles.EndGUI();

        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
        {
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            GameObject pickedGO = HandleUtility.PickGameObject(e.mousePosition, false);

            bool hit = false;
            Vector3 hitPoint = Vector3.zero, hitNormal = Vector3.up;
            MeshFilter bestMF = null;

            if (pickedGO != null)
            {
                float bestDist = float.MaxValue;
                foreach (var meshFilter in pickedGO.GetComponentsInChildren<MeshFilter>())
                {
                    if (meshFilter.sharedMesh == null) continue;
                    var lm = meshFilter.transform.worldToLocalMatrix;
                    Vector3 lo = lm.MultiplyPoint3x4(ray.origin);
                    Vector3 ld = lm.MultiplyVector(ray.direction).normalized;
                    var tris    = meshFilter.sharedMesh.triangles;
                    var verts   = meshFilter.sharedMesh.vertices;
                    var normals = meshFilter.sharedMesh.normals;
                    for (int ti = 0; ti < tris.Length; ti += 3)
                    {
                        Vector3 v0 = verts[tris[ti]], v1 = verts[tris[ti + 1]], v2 = verts[tris[ti + 2]];
                        if (!RayTriangle(lo, ld, v0, v1, v2, out float t, out float u, out float vb)) continue;
                        if (t < 0 || t >= bestDist) continue;
                        bestDist = t;
                        bestMF   = meshFilter;
                        hitPoint = meshFilter.transform.TransformPoint(lo + ld * t);
                        Vector3 ln = normals.Length > 0
                            ? ((1 - u - vb) * normals[tris[ti]] + u * normals[tris[ti + 1]] + vb * normals[tris[ti + 2]]).normalized
                            : Vector3.Cross(v1 - v0, v2 - v0).normalized;
                        hitNormal = meshFilter.transform.TransformDirection(ln).normalized;
                    }
                }
                hit = bestDist < float.MaxValue;
            }

            if (hit)
            {
                if (_pickingSrc)
                {
                    // Source end: snap to geometric face center so the tube departs from
                    // the true cap center regardless of where on the cap the user clicked.
                    Vector3 snapPoint = hitPoint;
                    if (bestMF != null)
                    {
                        var tempFace = new PickedFace { point = hitPoint, normal = hitNormal, source = pickedGO };
                        snapPoint = GetFaceCenter(bestMF, tempFace);
                    }
                    _srcFace      = new PickedFace { point = snapPoint, normal = hitNormal, source = pickedGO };
                    _lockedTarget = Selection.activeGameObject;
                }
                else
                {
                    // Target face: use exact click point — user is choosing where to connect to.
                    _dstFace = new PickedFace { point = hitPoint, normal = hitNormal, source = pickedGO };
                }
                _preview = null;
            }

            _pickingSrc = _pickingDst = false;
            Repaint();
            e.Use();
        }

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            _pickingSrc = _pickingDst = false;
            Repaint();
            e.Use();
        }

        sv.Repaint();
    }

    void DrawFaceGizmo(PickedFace? face, Color color, SceneView sv)
    {
        if (!face.HasValue) return;
        var prevC = Handles.color;
        Handles.color = color;
        float size = HandleUtility.GetHandleSize(face.Value.point);
        Handles.DrawSolidDisc(face.Value.point, sv.camera.transform.forward, size * 0.05f);
        Handles.DrawLine(face.Value.point, face.Value.point + face.Value.normal * size * 0.5f);
        Handles.color = prevC;
    }

    // ── Biarc solver ──────────────────────────────────────────────────────────

    struct ArcSegment
    {
        public Vector3 center;
        public float   radius;
        public Vector3 axisNormal; // plane normal of the arc
        public Vector3 startDir;   // unit vector from center to arc start
        public float   angle;      // signed sweep angle (radians)
        public bool    isStraight; // degenerate: zero radius → straight line
        public Vector3 straightStart;
        public Vector3 straightEnd;
    }

    struct BiarcResult
    {
        public ArcSegment arc1;
        public ArcSegment arc2;
        public float r1, angle1, r2, angle2;
        public Vector3 joinPoint;
    }

    // Solve a biarc from P0 (tangent T0) to P1 (tangent T1).
    // T0 = departure direction, T1 = arrival direction.
    // Returns null if the inputs are degenerate (P0 == P1).
    static BiarcResult? SolveBiarc(Vector3 P0, Vector3 T0, Vector3 P1, Vector3 T1)
    {
        T0 = T0.normalized;
        T1 = T1.normalized;

        if ((P1 - P0).sqrMagnitude < 1e-8f) return null;

        // Biarc join point J (standard formula)
        Vector3 V  = P1 - P0;
        float   vt = Vector3.Dot(V, T0 + T1);
        Vector3 J;

        if (Mathf.Abs(vt) < 1e-6f)
        {
            // Parallel or anti-parallel tangents — use midpoint
            J = (P0 + P1) * 0.5f;
        }
        else
        {
            float t = Vector3.Dot(V, V) / (2f * vt);
            J = (P0 + P1 + t * (T0 - T1)) * 0.5f;
        }

        ArcSegment a1 = BuildArc(P0, T0, J);
        // Tangent at J from arc1
        Vector3 TatJ = TangentAtArcEnd(a1, P0, J);
        ArcSegment a2 = BuildArc(J, TatJ, P1);

        // Verify arrival tangent matches T1 (within tolerance)
        // If not, we still proceed — the geometry is as close as possible.

        return new BiarcResult
        {
            arc1       = a1,
            arc2       = a2,
            r1         = a1.isStraight ? 0f : a1.radius,
            angle1     = Mathf.Abs(a1.angle),
            r2         = a2.isStraight ? 0f : a2.radius,
            angle2     = Mathf.Abs(a2.angle),
            joinPoint  = J
        };
    }

    // Build a circular arc from point A (tangent TA) to point B.
    // The arc lies in the plane defined by A, B, and the perpendicular to TA at A.
    static ArcSegment BuildArc(Vector3 A, Vector3 TA, Vector3 B)
    {
        TA = TA.normalized;
        Vector3 AB    = B - A;
        Vector3 perpTA = Vector3.Cross(Vector3.Cross(TA, AB).normalized, TA).normalized;

        float denom = 2f * Vector3.Dot(AB, perpTA);
        if (Mathf.Abs(denom) < 1e-7f)
        {
            // Degenerate: A, B co-linear with TA → straight segment
            return new ArcSegment { isStraight = true, straightStart = A, straightEnd = B, radius = 0f };
        }

        float   s      = Vector3.Dot(AB, AB) / denom;
        Vector3 center = A + s * perpTA;
        float   radius = (center - A).magnitude;

        // Plane normal of the arc = normalise(cross(TA, perpTA)) = cross direction
        Vector3 planeNormal = Vector3.Cross(TA, perpTA).normalized;

        // Vectors from center to start/end
        Vector3 toA = (A - center).normalized;
        Vector3 toB = (B - center).normalized;

        // Signed angle: positive = same handedness as planeNormal
        float angle = SignedAngle(toA, toB, planeNormal);

        return new ArcSegment
        {
            center      = center,
            radius      = radius,
            axisNormal  = planeNormal,
            startDir    = toA,
            angle       = angle,
            isStraight  = false
        };
    }

    // Returns the tangent direction at the end point of an arc, given the arc and its start point.
    static Vector3 TangentAtArcEnd(ArcSegment arc, Vector3 start, Vector3 end)
    {
        if (arc.isStraight)
            return (arc.straightEnd - arc.straightStart).normalized;

        // Tangent = derivative of position on circle = cross(planeNormal, (end - center).normalized) * sign(angle)
        Vector3 toEnd = (end - arc.center).normalized;
        Vector3 tangent = Vector3.Cross(arc.axisNormal, toEnd).normalized;
        if (arc.angle < 0f) tangent = -tangent;
        return tangent;
    }

    static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
    {
        float unsigned = Vector3.Angle(from, to);
        float sign     = Mathf.Sign(Vector3.Dot(Vector3.Cross(from, to), axis));
        return sign * unsigned * Mathf.Deg2Rad;
    }

    // Sample N+1 world-space center-line points along the biarc
    static List<Vector3> SampleBiarc(BiarcResult b, int n)
    {
        var pts  = new List<Vector3>();
        int n1   = Mathf.Max(2, Mathf.RoundToInt(n * b.angle1 / Mathf.Max(b.angle1 + b.angle2, 1e-6f)));
        int n2   = Mathf.Max(2, n - n1 + 1);
        SampleArc(b.arc1, n1, pts, false);
        SampleArc(b.arc2, n2, pts, true);   // skip first point (= last of arc1 = join point)
        return pts;
    }

    static void SampleArc(ArcSegment arc, int n, List<Vector3> pts, bool skipFirst)
    {
        if (arc.isStraight)
        {
            if (!skipFirst) pts.Add(arc.straightStart);
            pts.Add(arc.straightEnd);
            return;
        }
        int start = skipFirst ? 1 : 0;
        for (int i = start; i <= n; i++)
        {
            float t   = (float)i / n;
            float ang = t * arc.angle;
            Vector3 dir = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, arc.axisNormal) * arc.startDir;
            pts.Add(arc.center + dir * arc.radius);
        }
    }

    // ── Tube mesh generation ──────────────────────────────────────────────────

    // Returns the geometric center of the face the user clicked.
    // Finds all triangles that: (a) share the same world normal, AND (b) are coplanar with
    // the click point (lie on the same plane). Averages their centroids.
    // No proximity-to-click-point filter — that caused inconsistent results on large faces.
    static Vector3 GetFaceCenter(MeshFilter mf, PickedFace face)
    {
        const float normalTol = 0.1f;   // dot-product tolerance for normal match
        const float planeTol  = 0.02f;  // world-unit tolerance for coplanar test

        Vector3 faceSum  = Vector3.zero;
        int     triCount = 0;

        var mesh    = mf.sharedMesh;
        var tris    = mesh.triangles;
        var verts   = mesh.vertices;
        var normals = mesh.normals;
        var m       = mf.transform.localToWorldMatrix;

        for (int ti = 0; ti < tris.Length; ti += 3)
        {
            Vector3 v0 = verts[tris[ti]], v1 = verts[tris[ti+1]], v2 = verts[tris[ti+2]];
            Vector3 ln = normals.Length > 0
                ? ((normals[tris[ti]] + normals[tris[ti+1]] + normals[tris[ti+2]]) / 3f).normalized
                : Vector3.Cross(v1 - v0, v2 - v0).normalized;
            Vector3 wn = m.MultiplyVector(ln).normalized;

            // Same normal direction
            if (Vector3.Dot(wn, face.normal) < 1f - normalTol) continue;

            // Coplanar with click point: any vertex of this triangle lies on the same plane
            Vector3 wv0 = m.MultiplyPoint3x4(v0);
            if (Mathf.Abs(Vector3.Dot(wv0 - face.point, face.normal)) > planeTol) continue;

            faceSum += m.MultiplyPoint3x4((v0 + v1 + v2) / 3f);
            triCount++;
        }

        return triCount > 0 ? faceSum / triCount : face.point;
    }

    // Detect tube radius: finds the true face center (not click point), then measures
    // max radial distance of cap-plane vertices from that center.
    static float DetectTubeRadius(MeshFilter mf, PickedFace src)
    {
        Vector3 capCenter = GetFaceCenter(mf, src);
        Vector3 axisDir   = src.normal;

        var mesh  = mf.sharedMesh;
        var mv    = mesh.vertices;
        float srcA    = Vector3.Dot(src.point, axisDir);
        float maxR    = 0f;

        // Measure only cap-plane vertices (coplanar with source face, within distTol)
        const float distTol = 0.05f;
        bool anyCapVert = false;
        for (int i = 0; i < mv.Length; i++)
        {
            Vector3 wv   = mf.transform.TransformPoint(mv[i]);
            float   a    = Vector3.Dot(wv, axisDir);
            if (Mathf.Abs(a - srcA) > distTol) continue;
            anyCapVert = true;
            Vector3 toVert = wv - capCenter;
            float   along  = Vector3.Dot(toVert, axisDir);
            float   r      = (toVert - along * axisDir).magnitude;
            if (r > maxR) maxR = r;
        }

        // Fallback: measure all vertices radially from the cap center
        if (!anyCapVert || maxR < 1e-4f)
        {
            for (int i = 0; i < mv.Length; i++)
            {
                Vector3 wv     = mf.transform.TransformPoint(mv[i]);
                Vector3 toVert = wv - capCenter;
                float   along  = Vector3.Dot(toVert, axisDir);
                float   r      = (toVert - along * axisDir).magnitude;
                if (r > maxR) maxR = r;
            }
        }

        return Mathf.Max(maxR, 0.01f);
    }

    Mesh BuildTubeMesh(BiarcResult biarc, float tubeRadius, Vector3 P0, Vector3 T0, GameObject root = null)
    {
        int totalRings = _ringCount + 1; // +1 so we have ringCount segments
        int sides      = _sidesPerRing;

        // Sample ring centers and tangents along the biarc
        var centers  = new List<Vector3>();
        var tangents = new List<Vector3>();

        int n1 = Mathf.Max(2, Mathf.RoundToInt(_ringCount * biarc.angle1 / Mathf.Max(biarc.angle1 + biarc.angle2, 1e-6f)));
        int n2 = Mathf.Max(2, _ringCount - n1 + 1);
        SampleArcWithTangents(biarc.arc1, n1, centers, tangents, false);
        SampleArcWithTangents(biarc.arc2, n2, centers, tangents, true);

        totalRings = centers.Count;

        // Parallel-transport frame
        Vector3 initRight = Vector3.Cross(T0, Vector3.up).normalized;
        if (initRight.sqrMagnitude < 0.01f)
            initRight = Vector3.Cross(T0, Vector3.forward).normalized;
        Vector3 initUp = Vector3.Cross(initRight, T0).normalized;

        var rights = new Vector3[totalRings];
        var ups    = new Vector3[totalRings];
        rights[0]  = initRight;
        ups[0]     = initUp;
        for (int i = 1; i < totalRings; i++)
        {
            Quaternion rot = Quaternion.FromToRotation(tangents[i - 1], tangents[i]);
            rights[i] = rot * rights[i - 1];
            ups[i]    = rot * ups[i - 1];
        }

        // Build vertex buffer: [ring * sides] + 2 cap centers
        int ringVertCount = totalRings * sides;
        int capStartIdx   = ringVertCount;
        var verts   = new Vector3[ringVertCount + 2];
        var normals = new Vector3[ringVertCount + 2];
        var uvs     = new Vector2[ringVertCount + 2];

        for (int ri = 0; ri < totalRings; ri++)
        {
            for (int si = 0; si < sides; si++)
            {
                float ang = 2f * Mathf.PI * si / sides;
                Vector3 radial = Mathf.Cos(ang) * rights[ri] + Mathf.Sin(ang) * ups[ri];
                int idx = ri * sides + si;
                verts[idx]   = centers[ri] + radial * tubeRadius;
                normals[idx] = radial;
                uvs[idx]     = new Vector2((float)si / sides, (float)ri / (totalRings - 1));
            }
        }

        // Cap centers
        verts[capStartIdx]     = centers[0];
        normals[capStartIdx]   = -tangents[0].normalized;
        uvs[capStartIdx]       = new Vector2(0.5f, 0f);

        verts[capStartIdx + 1]   = centers[totalRings - 1];
        normals[capStartIdx + 1] = tangents[totalRings - 1].normalized;
        uvs[capStartIdx + 1]     = new Vector2(0.5f, 1f);

        // Build triangle list
        var tris = new List<int>();

        // Tube wall quads
        for (int ri = 0; ri < totalRings - 1; ri++)
        {
            for (int si = 0; si < sides; si++)
            {
                int sn = (si + 1) % sides;
                int a = ri * sides + si,  b = ri * sides + sn;
                int c = (ri + 1) * sides + si, d = (ri + 1) * sides + sn;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
        }

        // Start cap — winding CW when viewed from outside (cap normal = -tangents[0])
        for (int si = 0; si < sides; si++)
        {
            int sn = (si + 1) % sides;
            tris.Add(capStartIdx);
            tris.Add(sn);
            tris.Add(si);
        }

        // End cap
        int lastRing = (totalRings - 1) * sides;
        for (int si = 0; si < sides; si++)
        {
            int sn = (si + 1) % sides;
            tris.Add(capStartIdx + 1);
            tris.Add(lastRing + sn);
            tris.Add(lastRing + si);
        }

        // Mesh is kept in world space. Child GO is placed at world identity before parenting.
        var mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices  = verts;
        mesh.normals   = normals;
        mesh.uv        = uvs;
        mesh.triangles = tris.ToArray();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(false);
        return mesh;
    }

    // Straight tube from P0 to P1, with each end cap angled flush to its face normal.
    // The tube axis runs P0→P1. Each ring is a circle perpendicular to the axis.
    // The start cap ring vertices are projected onto the plane (P0, N0) and the end cap
    // ring vertices are projected onto the plane (P1, N1), so each end is flush to its face.
    Mesh BuildStraightTubeMesh(float tubeRadius, Vector3 P0, Vector3 N0, Vector3 P1, Vector3 N1, GameObject root)
    {
        int rings = _ringCount + 1;
        int sides = _sidesPerRing;

        Vector3 axis  = (P1 - P0).normalized;
        float   len   = (P1 - P0).magnitude;

        // Build a consistent cross-section frame
        Vector3 right = Vector3.Cross(axis, Vector3.up).normalized;
        if (right.sqrMagnitude < 0.01f) right = Vector3.Cross(axis, Vector3.forward).normalized;
        Vector3 up = Vector3.Cross(right, axis).normalized;

        int ringVertCount = rings * sides;
        int capStartIdx   = ringVertCount;
        var verts   = new Vector3[ringVertCount + 2];
        var normals = new Vector3[ringVertCount + 2];
        var uvs     = new Vector2[ringVertCount + 2];

        for (int ri = 0; ri < rings; ri++)
        {
            float t = (float)ri / (rings - 1); // 0..1 along axis
            Vector3 ringCenter = P0 + axis * (t * len);

            for (int si = 0; si < sides; si++)
            {
                float ang    = 2f * Mathf.PI * si / sides;
                Vector3 radial = Mathf.Cos(ang) * right + Mathf.Sin(ang) * up;
                Vector3 pos  = ringCenter + radial * tubeRadius;

                // Project first ring onto start-cap plane, last ring onto end-cap plane
                if (ri == 0)
                    pos = ProjectOntoPlane(pos, axis, P0, N0);
                else if (ri == rings - 1)
                    pos = ProjectOntoPlane(pos, axis, P1, N1);

                int idx = ri * sides + si;
                verts[idx]   = pos;
                normals[idx] = radial;
                uvs[idx]     = new Vector2((float)si / sides, t);
            }
        }

        // Cap centers: project center points onto their respective cap planes
        Vector3 capStart = ProjectOntoPlane(P0, axis, P0, N0);
        Vector3 capEnd   = ProjectOntoPlane(P1, axis, P1, N1);

        verts[capStartIdx]     = capStart;
        normals[capStartIdx]   = -N0;
        uvs[capStartIdx]       = new Vector2(0.5f, 0f);

        verts[capStartIdx + 1]   = capEnd;
        normals[capStartIdx + 1] = N1;
        uvs[capStartIdx + 1]     = new Vector2(0.5f, 1f);

        var tris = new List<int>();
        for (int ri = 0; ri < rings - 1; ri++)
        {
            for (int si = 0; si < sides; si++)
            {
                int sn = (si + 1) % sides;
                int a = ri * sides + si,  b = ri * sides + sn;
                int c = (ri + 1) * sides + si, d = (ri + 1) * sides + sn;
                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
        }
        for (int si = 0; si < sides; si++)
        {
            int sn = (si + 1) % sides;
            tris.Add(capStartIdx); tris.Add(si);   tris.Add(sn);
        }
        int lastRingBase = (rings - 1) * sides;
        for (int si = 0; si < sides; si++)
        {
            int sn = (si + 1) % sides;
            tris.Add(capStartIdx + 1); tris.Add(lastRingBase + sn); tris.Add(lastRingBase + si);
        }

        // Mesh stays in world space — the child GameObject is placed at world identity
        // so no local-space transform is needed. See Apply() for parenting logic.
        var mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices  = verts;
        mesh.normals   = normals;
        mesh.uv        = uvs;
        mesh.triangles = tris.ToArray();
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(false);
        return mesh;
    }

    // Move a point along `axis` until it lies on the plane defined by (planePt, planeNormal).
    // If the axis is nearly parallel to the plane, returns the original point unchanged.
    static Vector3 ProjectOntoPlane(Vector3 point, Vector3 axis, Vector3 planePt, Vector3 planeNormal)
    {
        float denom = Vector3.Dot(axis, planeNormal);
        if (Mathf.Abs(denom) < 1e-6f) return point;
        float t = Vector3.Dot(planePt - point, planeNormal) / denom;
        return point + axis * t;
    }

    static void SampleArcWithTangents(ArcSegment arc, int n, List<Vector3> centers, List<Vector3> tangents, bool skipFirst)
    {
        if (arc.isStraight)
        {
            if (!skipFirst)
            {
                centers.Add(arc.straightStart);
                tangents.Add((arc.straightEnd - arc.straightStart).normalized);
            }
            centers.Add(arc.straightEnd);
            tangents.Add((arc.straightEnd - arc.straightStart).normalized);
            return;
        }
        int start = skipFirst ? 1 : 0;
        for (int i = start; i <= n; i++)
        {
            float t   = (float)i / n;
            float ang = t * arc.angle;
            Vector3 dir = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, arc.axisNormal) * arc.startDir;
            centers.Add(arc.center + dir * arc.radius);
            // Tangent = cross(axisNormal, dir) * sign(angle)
            Vector3 tang = Vector3.Cross(arc.axisNormal, dir).normalized;
            if (arc.angle < 0f) tang = -tang;
            tangents.Add(tang);
        }
    }

    // ── Apply ─────────────────────────────────────────────────────────────────

    void Apply(GameObject root, MeshFilter mf)
    {
        if (!_srcFace.HasValue || !_dstFace.HasValue) return;

        bool straightMode = _srcStraight && _dstStraight;
        if (!straightMode && !_preview.HasValue) return;

        Undo.SetCurrentGroupName("Apply Mesh Curve");
        int group = Undo.GetCurrentGroup();

        float tubeRadius = DetectTubeRadius(mf, _srcFace.Value);
        Mesh tubeMesh = straightMode
            ? BuildStraightTubeMesh(tubeRadius, _srcFace.Value.point, _srcFace.Value.normal,
                                    _dstFace.Value.point, _dstFace.Value.normal, root)
            : BuildTubeMesh(_preview.Value, tubeRadius, _srcFace.Value.point, _srcFace.Value.normal, root);

        EnsureSaveFolder();
        string suffix  = straightMode ? "straight" : "biarc";
        string baseName = SanitizeName($"{root.name}_{suffix}");
        tubeMesh.name   = baseName;

        // The original GameObject and its mesh are always left untouched.
        // The new tube is created as a child, using the original's material.
        var mr  = root.GetComponent<MeshRenderer>();
        Material mat = mr != null ? mr.sharedMaterial : null;

        List<Mesh> pieces = SplitByVolume(tubeMesh, _preview, straightMode, tubeRadius, _maxConcavityVolume);

        for (int i = 0; i < pieces.Count; i++)
        {
            string meshName = pieces.Count == 1 ? baseName : $"{baseName}_Part{i}";
            pieces[i].name  = meshName;
            string path     = $"{SaveFolder}/{meshName}.asset";
            SaveMesh(pieces[i], path);

            var child = new GameObject(meshName);
            Undo.RegisterCreatedObjectUndo(child, "Apply Mesh Curve");
            // Mesh verts are in world space; place child at world identity then parent
            // with worldPositionStays=true so the mesh renders at the correct world position
            // regardless of the parent's scale/rotation.
            child.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            child.transform.localScale = Vector3.one;
            child.transform.SetParent(root.transform, true);

            child.AddComponent<MeshFilter>().sharedMesh = pieces[i];
            var childMR = child.AddComponent<MeshRenderer>();
            if (mat != null) childMR.sharedMaterial = mat;
            var childMC = child.AddComponent<MeshCollider>();
            childMC.sharedMesh = pieces[i];
            childMC.convex     = true;
        }

        Undo.CollapseUndoOperations(group);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    // ── Volume-based splitting ────────────────────────────────────────────────
    //
    // For each arc in the biarc, compute the concavity volume that would be lost when
    // Unity makes the collider convex.  For a circular arc segment:
    //   chordDepth = R * (1 - cos(θ/2))        (max distance from chord to arc)
    //   concavityVol ≈ π * r² * chordDepth     (tube cross-section × depth)
    //
    // If the full arc volume exceeds the threshold, split it into N equal pieces where
    // each piece's volume is ≤ threshold, then slice the mesh at each split point.

    List<Mesh> SplitByVolume(Mesh mesh, BiarcResult? biarc, bool isStraight,
        float tubeRadius, float maxVol)
    {
        if (isStraight || !biarc.HasValue)
            return new List<Mesh> { mesh }; // straight tube is always convex

        var b = biarc.Value;
        float tubeArea = Mathf.PI * tubeRadius * tubeRadius;

        // Collect split planes along the biarc center-line.
        // For each arc: determine how many equal segments are needed so each fits within maxVol.
        var splitPlanes = new List<(Vector3 point, Vector3 normal)>();

        AddArcSplitPlanes(b.arc1, tubeArea, maxVol, splitPlanes);
        AddArcSplitPlanes(b.arc2, tubeArea, maxVol, splitPlanes);

        if (splitPlanes.Count == 0)
            return new List<Mesh> { mesh };

        // Slice the mesh at each split plane in sequence
        var pieces = new List<Mesh> { mesh };
        foreach (var (pt, n) in splitPlanes)
        {
            var next = new List<Mesh>();
            foreach (var piece in pieces)
            {
                SliceByPlane(piece, pt, n, out Mesh a, out Mesh bMesh);
                if (a != null) next.Add(a);
                if (bMesh != null) next.Add(bMesh);
                if (a == null && bMesh == null) next.Add(piece);
            }
            pieces = next;
        }

        return pieces;
    }

    static void AddArcSplitPlanes(ArcSegment arc, float tubeArea, float maxVol,
        List<(Vector3, Vector3)> planes)
    {
        if (arc.isStraight) return;

        float R     = arc.radius;
        float theta = Mathf.Abs(arc.angle); // total arc angle in radians

        // Volume of concavity for a segment of angle θ:  π r² * R(1 - cos(θ/2))
        // We need: R(1-cos(θ/2)) * tubeArea ≤ maxVol
        // → 1-cos(θ/2) ≤ maxVol / (tubeArea * R)
        // → cos(θ/2) ≥ 1 - maxVol/(tubeArea*R)
        // → θ/2 ≤ acos(1 - maxVol/(tubeArea*R))
        // → θ_max = 2*acos(1 - maxVol/(tubeArea*R))

        float ratio    = maxVol / Mathf.Max(tubeArea * R, 1e-6f);
        float cosArg   = Mathf.Clamp(1f - ratio, -1f, 1f);
        float thetaMax = 2f * Mathf.Acos(cosArg);

        if (theta <= thetaMax) return; // whole arc fits — no split needed

        int   segs  = Mathf.CeilToInt(theta / thetaMax);
        float step  = theta / segs;

        // Emit a split plane at each interior segment boundary
        for (int i = 1; i < segs; i++)
        {
            float ang = i * step * (arc.angle < 0f ? -1f : 1f);
            Vector3 dir      = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, arc.axisNormal) * arc.startDir;
            Vector3 splitPt  = arc.center + dir * arc.radius;
            // Plane normal = tangent direction at this point (perpendicular to the radius in the arc plane)
            Vector3 splitN   = Vector3.Cross(arc.axisNormal, dir).normalized;
            if (arc.angle < 0f) splitN = -splitN;
            planes.Add((splitPt, splitN));
        }
    }

    static void TryAddEdge(Dictionary<(int, int), int> map, int a, int b, int tri)
    {
        var key = a < b ? (a, b) : (b, a);
        if (!map.ContainsKey(key)) map[key] = tri;
    }

    static Vector3 TriNormal(Vector3[] verts, int[] tris, int ti)
    {
        Vector3 v0 = verts[tris[ti * 3]], v1 = verts[tris[ti * 3 + 1]], v2 = verts[tris[ti * 3 + 2]];
        return Vector3.Cross(v1 - v0, v2 - v0).normalized;
    }

    void SliceByPlane(Mesh mesh, Vector3 planePt, Vector3 planeNormal, out Mesh meshA, out Mesh meshB)
    {
        var verts = mesh.vertices;
        var tris  = mesh.triangles;
        var norms = mesh.normals;
        var uvs   = mesh.uv;
        bool hasNormals = norms != null && norms.Length == verts.Length;
        bool hasUVs     = uvs   != null && uvs.Length   == verts.Length;

        var vA = new List<Vector3>(); var nA = new List<Vector3>(); var uA = new List<Vector2>(); var tA = new List<int>();
        var vB = new List<Vector3>(); var nB = new List<Vector3>(); var uB = new List<Vector2>(); var tB = new List<int>();

        for (int ti = 0; ti < tris.Length; ti += 3)
        {
            int i0 = tris[ti], i1 = tris[ti+1], i2 = tris[ti+2];
            Vector3 p0 = verts[i0], p1 = verts[i1], p2 = verts[i2];
            float d0 = SignedDist(p0, planePt, planeNormal);
            float d1 = SignedDist(p1, planePt, planeNormal);
            float d2 = SignedDist(p2, planePt, planeNormal);
            Vector3 no0 = hasNormals?norms[i0]:Vector3.up, no1 = hasNormals?norms[i1]:Vector3.up, no2 = hasNormals?norms[i2]:Vector3.up;
            Vector2 u0  = hasUVs?uvs[i0]:Vector2.zero,    u1  = hasUVs?uvs[i1]:Vector2.zero,    u2  = hasUVs?uvs[i2]:Vector2.zero;

            if (d0>=0 && d1>=0 && d2>=0) AddTri(vA,nA,uA,tA, p0,p1,p2, no0,no1,no2, u0,u1,u2);
            else if (d0<0 && d1<0 && d2<0) AddTri(vB,nB,uB,tB, p0,p1,p2, no0,no1,no2, u0,u1,u2);
            else
            {
                var pts  = new[]{p0,p1,p2};
                var ns2  = new[]{no0,no1,no2};
                var uv2  = new[]{u0,u1,u2};
                var ds   = new[]{d0,d1,d2};
                ClipTriangle(pts, ns2, uv2, ds, vA,nA,uA,tA, vB,nB,uB,tB);
            }
        }

        meshA = vA.Count >= 3 ? BuildMesh(vA,nA,uA,tA) : null;
        meshB = vB.Count >= 3 ? BuildMesh(vB,nB,uB,tB) : null;
    }

    static float SignedDist(Vector3 pt, Vector3 planePt, Vector3 n) => Vector3.Dot(pt - planePt, n);

    static void AddTri(List<Vector3> vL, List<Vector3> nL, List<Vector2> uL, List<int> tL,
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 n0, Vector3 n1, Vector3 n2, Vector2 u0, Vector2 u1, Vector2 u2)
    {
        int b = vL.Count;
        vL.Add(p0); vL.Add(p1); vL.Add(p2);
        nL.Add(n0); nL.Add(n1); nL.Add(n2);
        uL.Add(u0); uL.Add(u1); uL.Add(u2);
        tL.Add(b); tL.Add(b+1); tL.Add(b+2);
    }

    static void ClipTriangle(Vector3[] pts, Vector3[] ns, Vector2[] uvs, float[] ds,
        List<Vector3> vA, List<Vector3> nA, List<Vector2> uA, List<int> tA,
        List<Vector3> vB, List<Vector3> nB, List<Vector2> uB, List<int> tB)
    {
        var posV=new List<Vector3>(); var posN=new List<Vector3>(); var posU=new List<Vector2>();
        var negV=new List<Vector3>(); var negN=new List<Vector3>(); var negU=new List<Vector2>();
        for (int i=0;i<3;i++)
        {
            int j=(i+1)%3;
            bool pi=ds[i]>=0, pj=ds[j]>=0;
            if (pi){posV.Add(pts[i]);posN.Add(ns[i]);posU.Add(uvs[i]);}
            else   {negV.Add(pts[i]);negN.Add(ns[i]);negU.Add(uvs[i]);}
            if (pi!=pj)
            {
                float t=ds[i]/(ds[i]-ds[j]);
                posV.Add(Vector3.Lerp(pts[i],pts[j],t)); posN.Add(Vector3.Lerp(ns[i],ns[j],t).normalized); posU.Add(Vector2.Lerp(uvs[i],uvs[j],t));
                negV.Add(Vector3.Lerp(pts[i],pts[j],t)); negN.Add(Vector3.Lerp(ns[i],ns[j],t).normalized); negU.Add(Vector2.Lerp(uvs[i],uvs[j],t));
            }
        }
        Fan(posV,posN,posU,vA,nA,uA,tA);
        Fan(negV,negN,negU,vB,nB,uB,tB);
    }

    static void Fan(List<Vector3> pts, List<Vector3> ns, List<Vector2> uvs,
        List<Vector3> vL, List<Vector3> nL, List<Vector2> uL, List<int> tL)
    {
        for (int i=1;i<pts.Count-1;i++)
            AddTri(vL,nL,uL,tL, pts[0],pts[i],pts[i+1], ns[0],ns[i],ns[i+1], uvs[0],uvs[i],uvs[i+1]);
    }

    static Mesh BuildMesh(List<Vector3> v, List<Vector3> n, List<Vector2> u, List<int> t)
    {
        var m = new Mesh();
        m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.vertices  = v.ToArray();
        m.normals   = n.ToArray();
        m.uv        = u.ToArray();
        m.triangles = t.ToArray();
        m.RecalculateBounds();
        m.RecalculateTangents();
        m.UploadMeshData(false);
        return m;
    }

    // ── Asset helpers ─────────────────────────────────────────────────────────

    static void SaveMesh(Mesh mesh, string path)
    {
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (existing != null) { EditorUtility.CopySerialized(mesh, existing); return; }
        AssetDatabase.CreateAsset(mesh, path);
    }

    void EnsureSaveFolder()
    {
        var folder = SaveFolder;
        if (AssetDatabase.IsValidFolder(folder)) return;
        EnsureFolder(Path.GetDirectoryName(folder).Replace('\\', '/'));
        AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
    }

    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        EnsureFolder(Path.GetDirectoryName(folder).Replace('\\', '/'));
        AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace('\\', '/'), Path.GetFileName(folder));
    }

    static string SanitizeName(string s) =>
        s.Replace(' ', '_').Replace('.', 'd').Replace('/', '-');

    // ── Ray-triangle intersection (Möller–Trumbore, verbatim from JointAssistWindow) ──

    static bool RayTriangle(Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2,
        out float t, out float u, out float v)
    {
        t = u = v = 0;
        Vector3 e1 = v1-v0, e2 = v2-v0;
        Vector3 h  = Vector3.Cross(d, e2);
        float a    = Vector3.Dot(e1, h);
        if (a > -1e-6f && a < 1e-6f) return false;
        float f    = 1f / a;
        Vector3 s  = o - v0;
        u = f * Vector3.Dot(s, h);
        if (u < 0 || u > 1) return false;
        Vector3 q  = Vector3.Cross(s, e1);
        v = f * Vector3.Dot(d, q);
        if (v < 0 || u + v > 1) return false;
        t = f * Vector3.Dot(e2, q);
        return t > 1e-6f;
    }
}
