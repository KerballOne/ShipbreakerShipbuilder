using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public class JointAssistWindow : EditorWindow
{
    // Cut point state
    GameObject cutPointPrefab;
    bool pickingCutPoint;

    // Face snap state — each face stores the picked point, normal, and source object
    struct PickedFace { public Vector3 point; public Vector3 normal; public GameObject source; }
    PickedFace? snapFaceA;
    PickedFace? snapFaceB;
    int pickingSnapFace; // 0 = none, 1 = A, 2 = B
    float overlapAmount = 0.025f;

    // Joint placement state
    GameObject invisibleJointPrefab;
    string jointGroup          = "";
    float autoOverlapThreshold = 0.02f;
    float autoDedupRadius      = 0.05f;

    string statusMessage = "";
    MessageType statusType = MessageType.None;

    Vector2 scrollPos;

    const string PrefKey    = "JointAssist.InvisibleJointPrefabGUID";
    const string CutPrefKey = "JointAssist.CutPointPrefabGUID";

    [MenuItem("Shipbreaker/Shipbuilder Tools/Joint Assist", priority = 10)]
    static void Open() => GetWindow<JointAssistWindow>("Joint Assist");

    void OnEnable()
    {
        minSize = new Vector2(260f, 100f);
        invisibleJointPrefab = LoadPref(PrefKey);
        cutPointPrefab       = LoadPref(CutPrefKey);
        SceneView.duringSceneGui += OnSceneGUI;
    }

    void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        pickingCutPoint  = false;
        pickingSnapFace  = 0;
    }

    static GameObject LoadPref(string key)
    {
        var guid = EditorPrefs.GetString(key, "");
        if (string.IsNullOrEmpty(guid)) return null;
        var path = AssetDatabase.GUIDToAssetPath(guid);
        return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
    }

    static void SavePref(string key, GameObject go)
    {
        EditorPrefs.SetString(key, go != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(go)) : "");
    }

    void OnSelectionChange() => Repaint();

    void OnGUI()
    {
        scrollPos = GUILayout.BeginScrollView(
            scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

        // ── Cut Point Prefab ──────────────────────────────────────────────────
        EditorGUI.BeginChangeCheck();
        cutPointPrefab = (GameObject)EditorGUILayout.ObjectField(
            "Cut Point Prefab", cutPointPrefab, typeof(GameObject), false);
        if (EditorGUI.EndChangeCheck()) SavePref(CutPrefKey, cutPointPrefab);

        using (new EditorGUI.DisabledScope(cutPointPrefab == null))
        {
            var prevBG = GUI.backgroundColor;
            if (pickingCutPoint) GUI.backgroundColor = new Color(0.3f, 0.6f, 1f);
            if (GUILayout.Button(pickingCutPoint ? "Cancel Pick" : "Place Cut Point", GUILayout.Height(28)))
            {
                pickingCutPoint  = !pickingCutPoint;
                pickingSnapFace  = 0;
                if (pickingCutPoint) { statusMessage = ""; SceneView.lastActiveSceneView?.Focus(); }
            }
            GUI.backgroundColor = prevBG;
        }

        // ── Section break ─────────────────────────────────────────────────────
        GUILayout.Space(12);
        DrawSeparator();
        GUILayout.Space(8);

        // ── Face Snapping ─────────────────────────────────────────────────────
        EditorGUILayout.LabelField("Face Snapping", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        var activeColor  = new Color(0.3f, 0.6f, 1f);
        var pickedColor  = new Color(0.2f, 0.7f, 0.3f);
        var errorColor   = new Color(0.9f, 0.4f, 0.2f);

        int selCount = Selection.gameObjects.Length;
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(selCount != 2))
        {
            if (GUILayout.Button($"Auto-Detect Faces  ({selCount} selected)", GUILayout.Height(26)))
                AutoDetectFaces();
        }
        using (new EditorGUI.DisabledScope(!snapFaceA.HasValue && !snapFaceB.HasValue))
        {
            if (GUILayout.Button("⇆", GUILayout.Height(26), GUILayout.Width(28)))
            {
                var tmp = snapFaceA;
                snapFaceA = snapFaceB;
                snapFaceB = tmp;
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);
        DrawFacePickButton(1, "Face A", "Moving",     ref snapFaceA, activeColor, pickedColor);
        DrawFacePickButton(2, "Face B", "Flush with", ref snapFaceB, activeColor, pickedColor);

        bool bothPicked = snapFaceA.HasValue && snapFaceB.HasValue;

        if (bothPicked)
        {
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            var savedLW = EditorGUIUtility.labelWidth;
            var savedFW = EditorGUIUtility.fieldWidth;
            EditorGUIUtility.labelWidth = 75f;
            EditorGUIUtility.fieldWidth = 40f;
            overlapAmount = EditorGUILayout.FloatField("Overlap (m)", overlapAmount);
            EditorGUIUtility.labelWidth = savedLW;
            EditorGUIUtility.fieldWidth = savedFW;
            if (GUILayout.Button("Snap", GUILayout.MaxWidth(60)))
                ApplyFaceSnap(overlapAmount);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            if (GUILayout.Button("Snap Flush  (0 gap)", GUILayout.Height(32)))
                ApplyFaceSnap(0f);
        }

        // ── Section break ─────────────────────────────────────────────────────
        GUILayout.Space(12);
        DrawSeparator();
        GUILayout.Space(8);

        // ── Joint Placement ───────────────────────────────────────────────────
        EditorGUILayout.LabelField("Joint Placement", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        EditorGUI.BeginChangeCheck();
        invisibleJointPrefab = (GameObject)EditorGUILayout.ObjectField(
            "InvisibleJoint Prefab", invisibleJointPrefab, typeof(GameObject), false);
        if (EditorGUI.EndChangeCheck()) SavePref(PrefKey, invisibleJointPrefab);

        if (invisibleJointPrefab == null)
            EditorGUILayout.HelpBox("Assign an InvisibleJoint prefab above.", MessageType.Info);

        EditorGUILayout.Space(6);

        EditorGUILayout.LabelField("Auto-Placement", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox(
            "Select 2+ parts in the hierarchy, then click Auto-Place. " +
            "Existing joints at the same positions are not duplicated.",
            MessageType.None);

        jointGroup           = EditorGUILayout.TextField(
            new GUIContent("Joints Subfolder", "If set, joints are placed under Joints/<name>. Leave empty to place directly under Joints."),
            jointGroup);
        autoOverlapThreshold = EditorGUILayout.FloatField("Adjacency Threshold (m)", autoOverlapThreshold);
        autoDedupRadius      = EditorGUILayout.FloatField("Dedup Radius (m)",        autoDedupRadius);

        EditorGUILayout.Space(4);

        int autoSel = Selection.gameObjects.Length;
        bool canAuto = invisibleJointPrefab != null && autoSel >= 2;
        if (autoSel >= 2)
        {
            bool anyAsync   = Selection.gameObjects.Any(IsAsyncPart);
            int islandCount = Selection.gameObjects.Sum(g => GetIslandFSPs(g).Count);
            if (anyAsync)
                EditorGUILayout.HelpBox(
                    $"{autoSel} selected ({islandCount} islands) — async parts detected. Invisible Joints needed at interfaces.",
                    MessageType.Warning);
        }

        using (new EditorGUI.DisabledScope(!canAuto))
        {
            if (GUILayout.Button("Auto-Place Joints", GUILayout.Height(36)))
                AutoPlaceInvisibleJoints();
        }

        // ── Section break ─────────────────────────────────────────────────────
        GUILayout.Space(12);
        DrawSeparator();
        GUILayout.Space(8);

        // ── Scene Overlay ─────────────────────────────────────────────────────
        EditorGUILayout.LabelField("Scene Overlay", EditorStyles.boldLabel);
        if (GUILayout.Button("Redraw", GUILayout.Height(28)))
        {
            AddressableRendering.ForceResetUpdateFlag();
            AddressableRendering.ClearView();
            AddressableRendering.UpdateViewList();
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(statusMessage, statusType);
        }

        GUILayout.EndScrollView();
    }

    void DrawFacePickButton(int slot, string label, string staticPrefix, ref PickedFace? face, Color activeColor, Color pickedColor)
    {
        bool isPickingThis = pickingSnapFace == slot;
        var prevBG = GUI.backgroundColor;

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(staticPrefix, GUILayout.Width(68));

        string btnText;
        if (isPickingThis)
        {
            GUI.backgroundColor = activeColor;
            btnText = $"Cancel";
        }
        else if (face.HasValue)
        {
            GUI.backgroundColor = pickedColor;
            string name = face.Value.source != null ? face.Value.source.name : "?";
            if (name.Length > 20) name = name.Substring(0, 18) + "…";
            btnText = name;
        }
        else
        {
            btnText = $"Pick {label}";
        }

        if (GUILayout.Button(btnText, GUILayout.Height(26)))
        {
            if (isPickingThis)
            {
                pickingSnapFace = 0;
            }
            else
            {
                pickingSnapFace = slot;
                pickingCutPoint = false;
                statusMessage   = "";
                SceneView.lastActiveSceneView?.Focus();
            }
        }

        GUI.backgroundColor = prevBG;
        EditorGUILayout.EndHorizontal();
    }

    // ── Scene picking ─────────────────────────────────────────────────────────

    void OnSceneGUI(SceneView sv)
    {
        bool anyPicking = pickingCutPoint || pickingSnapFace != 0;
        if (!anyPicking) return;

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
            GameObject picked = HandleUtility.PickGameObject(e.mousePosition, false);

            bool hit = false;
            Vector3 hitPoint = Vector3.zero, hitNormal = Vector3.up;

            if (picked != null)
            {
                float bestDist = float.MaxValue;
                foreach (var mf in picked.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null) continue;
                    var lm = mf.transform.worldToLocalMatrix;
                    Vector3 lo = lm.MultiplyPoint3x4(ray.origin);
                    Vector3 ld = lm.MultiplyVector(ray.direction).normalized;
                    var tris    = mf.sharedMesh.triangles;
                    var verts   = mf.sharedMesh.vertices;
                    var normals = mf.sharedMesh.normals;
                    for (int ti = 0; ti < tris.Length; ti += 3)
                    {
                        Vector3 v0 = verts[tris[ti]], v1 = verts[tris[ti+1]], v2 = verts[tris[ti+2]];
                        if (!RayTriangle(lo, ld, v0, v1, v2, out float t, out float u, out float v)) continue;
                        if (t < 0 || t >= bestDist) continue;
                        bestDist  = t;
                        hitPoint  = mf.transform.TransformPoint(lo + ld * t);
                        Vector3 ln = normals.Length > 0
                            ? ((1 - u - v) * normals[tris[ti]] + u * normals[tris[ti+1]] + v * normals[tris[ti+2]]).normalized
                            : Vector3.Cross(v1 - v0, v2 - v0).normalized;
                        hitNormal = mf.transform.TransformDirection(ln).normalized;
                    }
                }
                hit = bestDist < float.MaxValue;
            }

            if (pickingCutPoint)
            {
                if (hit)
                {
                    Transform parent = ResolveParent(picked, "");
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(cutPointPrefab, parent);
                    inst.transform.localScale = Vector3.one;
                    inst.transform.position   = hitPoint;
                    inst.transform.rotation   = CutPointRotation(hitNormal);
                    Undo.RegisterCreatedObjectUndo(inst, "Place Cut Point");
                    Selection.activeGameObject = inst;
                    statusMessage = $"Placed Cut Point at {hitPoint:F3}.";
                    statusType    = MessageType.Info;
                }
                else
                {
                    statusMessage = picked == null ? "Nothing under cursor." : $"No mesh hit on '{picked.name}'.";
                    statusType    = MessageType.Warning;
                }
                pickingCutPoint = false;
            }
            else if (pickingSnapFace != 0)
            {
                int slot = pickingSnapFace;
                if (hit)
                {
                    var pf = new PickedFace { point = hitPoint, normal = hitNormal, source = picked };
                    if (slot == 1) snapFaceA = pf;
                    else           snapFaceB = pf;
                    statusMessage = $"Face {(slot == 1 ? "A" : "B")} picked on '{picked.name}'.";
                    statusType    = MessageType.Info;
                }
                else
                {
                    statusMessage = picked == null ? "Nothing under cursor." : $"No mesh hit on '{picked.name}'.";
                    statusType    = MessageType.Warning;
                }
                pickingSnapFace = 0;
            }

            Repaint();
            e.Use();
        }

        if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
        {
            pickingCutPoint = false;
            pickingSnapFace = 0;
            Repaint();
            e.Use();
        }

        sv.Repaint();
    }

    // ── Face snap ─────────────────────────────────────────────────────────────

    void AutoDetectFaces()
    {
        var sel = Selection.gameObjects;
        if (sel.Length != 2) return;

        Bounds bA = GetBounds(sel[0]), bB = GetBounds(sel[1]);
        Vector3 dir = GetDirection(bA, bB); // direction from A toward B

        // Face center on A: the face pointing toward B
        Vector3 faceAPoint = bA.center + dir * ReachInDir(bA, dir);
        // Face center on B: the face pointing toward A
        Vector3 faceBPoint = bB.center - dir * ReachInDir(bB, dir);

        snapFaceA = new PickedFace { point = faceAPoint, normal =  dir, source = sel[0] };
        snapFaceB = new PickedFace { point = faceBPoint, normal = -dir, source = sel[1] };

        statusMessage = $"Auto-detected faces: '{sel[0].name}' → '{sel[1].name}'.";
        statusType    = MessageType.Info;
        Repaint();
    }

    void ApplyFaceSnap(float overlap)
    {
        if (!snapFaceA.HasValue || !snapFaceB.HasValue) return;
        var fA = snapFaceA.Value;
        var fB = snapFaceB.Value;
        if (fA.source == null) { statusMessage = "Face A source object is missing."; statusType = MessageType.Warning; Repaint(); return; }

        Transform moveRoot = fA.source.transform;

        Undo.RecordObject(moveRoot, "Face Snap");

        // Rotate so fA.normal aligns flush with -fB.normal, pivoting around the picked face point.
        Quaternion alignRot = Quaternion.FromToRotation(fA.normal, -fB.normal);
        Vector3 pivotWorld  = fA.point;
        Vector3 toRoot      = moveRoot.position - pivotWorld;
        moveRoot.rotation   = alignRot * moveRoot.rotation;
        moveRoot.position   = pivotWorld + alignRot * toRoot;

        // Translate so the face point lands on B's face point (+ overlap along B's inward normal).
        Vector3 targetPos   = fB.point + fB.normal * overlap;
        moveRoot.position  += targetPos - pivotWorld;

        statusMessage = $"Snapped '{moveRoot.name}' to face on '{(fB.source != null ? fB.source.name : "?")}' ({overlap * 100f:F1} cm overlap).";
        statusType    = MessageType.Info;
        Repaint();
    }

    // ── Joint placement ───────────────────────────────────────────────────────

    void AutoPlaceInvisibleJoints()
    {
        var selected = Selection.gameObjects;
        Transform parent = ResolveParent(selected[0], jointGroup);

        var perObject = new List<List<Bounds>>();
        foreach (var go in selected)
            perObject.AddRange(GetIslandFSPs(go));
        int n = perObject.Count;

        var edges = new List<(float area, int i, int j, Vector3 pos, Vector3 sepDir)>();
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                float bestArea = -1f;
                Vector3 bestPos = Vector3.zero, bestSepDir = Vector3.zero;

                foreach (var wbA in perObject[i])
                {
                    foreach (var wbB in perObject[j])
                    {
                        var expanded = new Bounds(wbA.center, wbA.size + Vector3.one * autoOverlapThreshold * 2f);
                        if (!expanded.Intersects(wbB)) continue;

                        Vector3 sepDir = Vector3.zero;
                        float minGap = float.MaxValue;
                        foreach (var d in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
                        {
                            float fA = Vector3.Dot(wbA.center, d) + ReachInDir(wbA, d);
                            float fB = Vector3.Dot(wbB.center, d) - ReachInDir(wbB, d);
                            float g  = fB - fA;
                            if (g >= -autoOverlapThreshold && g < minGap) { minGap = g; sepDir = d; }
                        }
                        if (sepDir == Vector3.zero)
                            sepDir = ClosestCardinalDirection(wbB.center - wbA.center);

                        Vector3 oMin = Vector3.Max(wbA.min, wbB.min);
                        Vector3 oMax = Vector3.Min(wbA.max, wbB.max);
                        float area = sepDir.x != 0 ? Mathf.Max(0, oMax.y - oMin.y) * Mathf.Max(0, oMax.z - oMin.z)
                                   : sepDir.y != 0 ? Mathf.Max(0, oMax.x - oMin.x) * Mathf.Max(0, oMax.z - oMin.z)
                                   :                 Mathf.Max(0, oMax.x - oMin.x) * Mathf.Max(0, oMax.y - oMin.y);
                        if (area <= bestArea) continue;
                        bestArea = area;

                        float boundary = Vector3.Dot(wbB.center, sepDir) - ReachInDir(wbB, sepDir);
                        float px = oMin.x <= oMax.x ? (oMin.x + oMax.x) * 0.5f : (wbA.extents.x <= wbB.extents.x ? wbA.center.x : wbB.center.x);
                        float py = oMin.y <= oMax.y ? (oMin.y + oMax.y) * 0.5f : (wbA.extents.y <= wbB.extents.y ? wbA.center.y : wbB.center.y);
                        float pz = oMin.z <= oMax.z ? (oMin.z + oMax.z) * 0.5f : (wbA.extents.z <= wbB.extents.z ? wbA.center.z : wbB.center.z);
                        bestPos = new Vector3(px, py, pz);
                        bestPos -= sepDir * Vector3.Dot(bestPos, sepDir);
                        bestPos += sepDir * boundary;
                        bestSepDir = sepDir;
                    }
                }

                if (bestArea >= 0)
                    edges.Add((bestArea, i, j, bestPos, bestSepDir));
            }
        }

        edges.Sort((a, b) => b.area.CompareTo(a.area));
        var uf = new int[n];
        for (int k = 0; k < n; k++) uf[k] = k;

        int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
        void Union(int a, int b) { uf[Find(a)] = Find(b); }

        var placed = Object.FindObjectsOfType<InvisibleJointMarker>().Select(m => m.transform.position).ToList();
        int count  = 0;

        Undo.SetCurrentGroupName("Auto-Place InvisibleJoints");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (var (area, i, j, pos, sepDir) in edges)
        {
            if (Find(i) == Find(j)) continue;
            if (placed.Any(p => Vector3.Distance(p, pos) < autoDedupRadius)) { Union(i, j); continue; }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(invisibleJointPrefab, parent);
            inst.transform.localScale = Vector3.one;
            inst.transform.position   = pos;
            CenterColliderOnPos(inst);
            Undo.RegisterCreatedObjectUndo(inst, "Auto-Place Joint");
            placed.Add(pos);
            Union(i, j);
            count++;
        }

        Undo.CollapseUndoOperations(undoGroup);
        statusMessage = count > 0
            ? $"Placed {count} joint(s) to span {n} islands ({selected.Length} selected)."
            : "No overlapping/adjacent FakeStructurePart pairs found. Try increasing Adjacency Threshold or run Redraw first.";
        statusType = count > 0 ? MessageType.Info : MessageType.Warning;

        if (count > 0)
        {
            AddressableRendering.ForceResetUpdateFlag();
            AddressableRendering.ClearView();
            AddressableRendering.UpdateViewList();
        }

        Repaint();
    }

    static void CenterColliderOnPos(GameObject inst)
    {
        var mc = inst.GetComponent<MeshCollider>();
        if (mc != null && mc.sharedMesh != null)
            inst.transform.position -= inst.transform.TransformVector(mc.sharedMesh.bounds.center);
    }

    static void DrawSeparator()
    {
        var rect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 1f));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    Transform ResolveParent(GameObject go, string subFolder)
    {
        Transform root = go.transform;
        while (root.parent != null && root.GetComponent<BBI.Unity.Game.ModuleDefinition>() == null)
            root = root.parent;

        Transform joints = root.Find("Joints");
        if (joints == null)
        {
            var container = new GameObject("Joints");
            Undo.RegisterCreatedObjectUndo(container, "Create Joints Container");
            container.transform.SetParent(root, false);
            joints = container.transform;
        }

        if (string.IsNullOrWhiteSpace(subFolder))
            return joints;

        Transform sub = joints.Find(subFolder);
        if (sub != null) return sub;
        var subGo = new GameObject(subFolder);
        Undo.RegisterCreatedObjectUndo(subGo, "Create Joint Group");
        subGo.transform.SetParent(joints, false);
        return subGo.transform;
    }

    static Quaternion CutPointRotation(Vector3 normal)
    {
        if (Vector3.Dot(normal, Vector3.right) < -0.99f)
            return Quaternion.AngleAxis(180f, Vector3.up);
        return Quaternion.FromToRotation(Vector3.right, normal);
    }

    static bool IsAsyncPart(GameObject go)
    {
        for (var t = go.transform; t != null; t = t.parent)
            if (t.TryGetComponent<BBI.Unity.Game.AddressableLoader>(out _)) return true;
        return false;
    }

    // Collects top-level AddressableLoader transforms.
    static void CollectTopLevelLoaders(Transform t, List<Transform> result)
    {
        if (t.TryGetComponent<BBI.Unity.Game.AddressableLoader>(out _))
        {
            result.Add(t);
            return;
        }
        foreach (Transform child in t)
            CollectTopLevelLoaders(child, result);
    }

    static List<List<Bounds>> GetIslandFSPs(GameObject go)
    {
        var result  = new List<List<Bounds>>();
        var loaders = new List<Transform>();
        CollectTopLevelLoaders(go.transform, loaders);

        if (loaders.Count == 0)
        {
            var fsps = go.GetComponentsInChildren<FakeStructurePart>(true)
                         .Select(fsp => TransformBoundsToWorld(fsp.transform.localToWorldMatrix, fsp.localColliderBounds))
                         .ToList();
            if (fsps.Count > 0) result.Add(fsps);
            return result;
        }

        foreach (var loader in loaders)
        {
            Transform fake = null;
            for (int c = 0; c < loader.childCount; c++)
            {
                var ch = loader.GetChild(c);
                if (ch.TryGetComponent<FakePrefabDisplay>(out _) || ch.TryGetComponent<SelectAddressableParent>(out _))
                    { fake = ch; break; }
            }

            if (fake == null) { result.Add(new List<Bounds>()); continue; }

            var allFSPs = fake.GetComponentsInChildren<FakeStructurePart>(true)
                              .Select(fsp => TransformBoundsToWorld(fsp.transform.localToWorldMatrix, fsp.localColliderBounds))
                              .ToList();
            if (allFSPs.Count == 0) continue;

            float spatialGap = 0.05f;
            int[] ufS = Enumerable.Range(0, allFSPs.Count).ToArray();
            int SFind(int x) { while (ufS[x] != x) { ufS[x] = ufS[ufS[x]]; x = ufS[x]; } return x; }

            for (int a = 0; a < allFSPs.Count; a++)
            {
                var expanded = new Bounds(allFSPs[a].center, allFSPs[a].size + Vector3.one * spatialGap * 2f);
                for (int b = a + 1; b < allFSPs.Count; b++)
                    if (expanded.Intersects(allFSPs[b]))
                        ufS[SFind(a)] = SFind(b);
            }

            var spatialGroups = new Dictionary<int, List<Bounds>>();
            for (int a = 0; a < allFSPs.Count; a++)
            {
                int root = SFind(a);
                if (!spatialGroups.ContainsKey(root)) spatialGroups[root] = new List<Bounds>();
                spatialGroups[root].Add(allFSPs[a]);
            }
            foreach (var g in spatialGroups.Values)
                result.Add(g);
        }

        return result;
    }

    static IEnumerable<(Vector3 pos, Vector3 normal)> CollectCutPointFSPs(GameObject go)
    {
        var loaders = new List<Transform>();
        CollectTopLevelLoaders(go.transform, loaders);

        IEnumerable<FakeStructurePart> Source()
        {
            if (loaders.Count == 0)
            {
                foreach (var fsp in go.GetComponentsInChildren<FakeStructurePart>(true))
                    yield return fsp;
                yield break;
            }
            foreach (var loader in loaders)
            {
                Transform fake = null;
                for (int c = 0; c < loader.childCount; c++)
                {
                    var ch = loader.GetChild(c);
                    if (ch.TryGetComponent<FakePrefabDisplay>(out _) || ch.TryGetComponent<SelectAddressableParent>(out _))
                        { fake = ch; break; }
                }
                if (fake == null) continue;
                foreach (var fsp in fake.GetComponentsInChildren<FakeStructurePart>(true))
                    yield return fsp;
            }
        }

        foreach (var fsp in Source())
        {
            if (fsp.type != FakeStructurePart.JointType.CutPoint) continue;
            var wb = TransformBoundsToWorld(fsp.transform.localToWorldMatrix, fsp.localColliderBounds);
            var e  = wb.extents;
            Vector3 normal = e.x <= e.y && e.x <= e.z ? Vector3.right
                           : e.y <= e.z               ? Vector3.up
                           :                            Vector3.forward;
            yield return (wb.center, normal);
        }
    }

    static Bounds TransformBoundsToWorld(Matrix4x4 m, Bounds local)
    {
        Vector3 c = local.center, e = local.extents;
        var b = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
        for (int i = 0; i < 8; i++)
            b.Encapsulate(m.MultiplyPoint3x4(c + new Vector3(
                (i & 1) != 0 ? e.x : -e.x,
                (i & 2) != 0 ? e.y : -e.y,
                (i & 4) != 0 ? e.z : -e.z)));
        return b;
    }

    float ReachInDir(Bounds b, Vector3 dir)
        => Mathf.Abs(b.extents.x * dir.x) + Mathf.Abs(b.extents.y * dir.y) + Mathf.Abs(b.extents.z * dir.z);

    Vector3 GetDirection(Bounds bA, Bounds bB)
    {
        Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        float bestGap = float.MaxValue;
        Vector3 bestDir = Vector3.up;
        foreach (var a in axes)
        {
            float faceA = Vector3.Dot(bA.center, a) + ReachInDir(bA, a);
            float faceB = Vector3.Dot(bB.center, a) - ReachInDir(bB, a);
            float g = faceB - faceA;
            if (g >= 0 && g < bestGap) { bestGap = g; bestDir = a; }
        }
        if (bestGap == float.MaxValue)
            bestDir = ClosestCardinalDirection(bB.center - bA.center);
        return bestDir;
    }

    Bounds GetBounds(GameObject go)
    {
        var renderers = go.GetComponentsInChildren<Renderer>()
                          .Where(r => !(r is ParticleSystemRenderer)).ToArray();
        if (renderers.Length > 0)
        {
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }
        var cols = go.GetComponentsInChildren<Collider>();
        if (cols.Length > 0)
        {
            Bounds b = cols[0].bounds;
            for (int i = 1; i < cols.Length; i++) b.Encapsulate(cols[i].bounds);
            return b;
        }
        return new Bounds(go.transform.position, Vector3.zero);
    }

    Vector3 ClosestCardinalDirection(Vector3 v)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        if (ax > ay && ax > az) return v.x > 0 ? Vector3.right : Vector3.left;
        if (ay > az) return v.y > 0 ? Vector3.up : Vector3.down;
        return v.z > 0 ? Vector3.forward : Vector3.back;
    }

    static bool RayTriangle(Vector3 o, Vector3 d, Vector3 v0, Vector3 v1, Vector3 v2, out float t, out float u, out float v)
    {
        t = u = v = 0;
        Vector3 e1 = v1 - v0, e2 = v2 - v0;
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
