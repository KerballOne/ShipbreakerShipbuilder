using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;

public class JointAssistWindow : EditorWindow
{
    // Snap state
    GameObject[] snapCandidates;
    int movingIndex = -1;
    float overlapAmount = 0.025f;
    enum OverlapAxis { AutoDetect, PosX, NegX, PosY, NegY, PosZ, NegZ }
    OverlapAxis axis = OverlapAxis.AutoDetect;

    // Joint placement state
    GameObject invisibleJointPrefab;
    GameObject cutPointPrefab;
    bool useCutPoints;
    string jointGroup           = "";
    float autoOverlapThreshold  = 0.02f;
    float autoDedupRadius       = 0.05f;

    string statusMessage = "";
    MessageType statusType = MessageType.None;

    Vector2 scrollPos;

    const string PrefKey    = "JointAssist.InvisibleJointPrefabGUID";
    const string CutPrefKey = "JointAssist.CutPointPrefabGUID";

    GameObject ActivePrefab    => useCutPoints && cutPointPrefab != null ? cutPointPrefab : invisibleJointPrefab;
    bool       IsUsingCutPoints => useCutPoints && cutPointPrefab != null;

    [MenuItem("Shipbreaker/Shipbuilder Tools/Joint Assist", priority = 10)]
    static void Open() => GetWindow<JointAssistWindow>("Joint Assist");

    void OnEnable()
    {
        minSize = new Vector2(260f, 100f);
        invisibleJointPrefab = LoadPref(PrefKey);
        cutPointPrefab       = LoadPref(CutPrefKey);
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
        // Vertical scroll only — suppress horizontal bar
        scrollPos = GUILayout.BeginScrollView(
            scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

        var selBtnStyle = new GUIStyle(GUI.skin.button) { wordWrap = true, alignment = TextAnchor.MiddleLeft };

        // ── Face Snapping ─────────────────────────────────────────────────────
        EditorGUILayout.LabelField("Face Snapping", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Select 2 parts, click Snap, then choose which one moves.",
            MessageType.None);

        EditorGUILayout.Space(4);

        int selCount = Selection.gameObjects.Length;
        using (new EditorGUI.DisabledScope(selCount != 2))
        {
            if (GUILayout.Button($"Snap 2 Faces Together  ({selCount} selected)", GUILayout.Height(28)))
            {
                snapCandidates = Selection.gameObjects.ToArray();
                movingIndex = -1;
                bool hasFake = snapCandidates.Any(g => g.GetComponent<FakePrefabDisplay>() != null || g.GetComponent<SelectAddressableParent>() != null);
                if (hasFake)
                {
                    statusMessage = "One or more selected objects are fake display children — face snapping still works, but they will be destroyed on Redraw.";
                    statusType = MessageType.Warning;
                }
                Repaint();
            }
        }

        if (snapCandidates != null && snapCandidates.Length == 2 &&
            snapCandidates[0] != null && snapCandidates[1] != null)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Which object moves?", EditorStyles.miniLabel);

            var prevColor = GUI.backgroundColor;
            var selColor  = new Color(0.3f, 0.6f, 1f);

            GUI.backgroundColor = movingIndex == 0 ? selColor : prevColor;
            if (GUILayout.Button(snapCandidates[0].name, selBtnStyle))
                movingIndex = movingIndex == 0 ? -1 : 0;

            GUI.backgroundColor = movingIndex == 1 ? selColor : prevColor;
            if (GUILayout.Button(snapCandidates[1].name, selBtnStyle))
                movingIndex = movingIndex == 1 ? -1 : 1;

            GUI.backgroundColor = prevColor;

            EditorGUILayout.Space(4);
            axis = (OverlapAxis)EditorGUILayout.EnumPopup("Direction", axis);

            if (movingIndex >= 0)
                ShowPreview();

            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            var savedLW = EditorGUIUtility.labelWidth;
            var savedFW = EditorGUIUtility.fieldWidth;
            EditorGUIUtility.labelWidth = 75f;
            EditorGUIUtility.fieldWidth = 30f;
            overlapAmount = EditorGUILayout.FloatField("Overlap (m)", overlapAmount);
            EditorGUIUtility.labelWidth = savedLW;
            EditorGUIUtility.fieldWidth = savedFW;
            using (new EditorGUI.DisabledScope(movingIndex < 0))
            {
                if (GUILayout.Button("Snap to Overlap", GUILayout.MaxWidth(120)))
                    ApplyMove(overlapAmount);
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(movingIndex < 0))
            {
                if (GUILayout.Button("Snap Flush  (0 gap)", GUILayout.Height(32)))
                    ApplyMove(0f);
            }
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

        EditorGUI.BeginChangeCheck();
        cutPointPrefab = (GameObject)EditorGUILayout.ObjectField(
            "Cut Point Prefab", cutPointPrefab, typeof(GameObject), false);
        if (EditorGUI.EndChangeCheck()) SavePref(CutPrefKey, cutPointPrefab);

        if (invisibleJointPrefab == null && cutPointPrefab == null)
            EditorGUILayout.HelpBox("Assign at least one prefab above.", MessageType.Info);

        if (invisibleJointPrefab != null && cutPointPrefab != null)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Place as", GUILayout.Width(60));
            var prevBG = GUI.backgroundColor;
            GUI.backgroundColor = !useCutPoints ? new Color(0.3f, 0.6f, 1f) : prevBG;
            if (GUILayout.Button("Invisible Joint", EditorStyles.miniButtonLeft))  useCutPoints = false;
            GUI.backgroundColor =  useCutPoints ? new Color(0.3f, 0.6f, 1f) : prevBG;
            if (GUILayout.Button("Cut Point",       EditorStyles.miniButtonRight)) useCutPoints = true;
            GUI.backgroundColor = prevBG;
            EditorGUILayout.EndHorizontal();
        }
        else
            useCutPoints = cutPointPrefab != null;

        EditorGUILayout.Space(6);

        bool canPlace = movingIndex >= 0 && ActivePrefab != null;
        using (new EditorGUI.DisabledScope(!canPlace))
        {
            string lbl = IsUsingCutPoints ? "Place Cut Point at Snap Face" : "Place Invisible Joint at Snap Face";
            if (GUILayout.Button(lbl, GUILayout.Height(26)))
                PlaceJoint();
        }
        if (movingIndex < 0)
            EditorGUILayout.HelpBox("Set moving and target objects in the Snap section first.", MessageType.None);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Auto-Placement", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox(
            "Select 2+ parts in the hierarchy, then click Auto-Place. " +
            "Existing joints at the same positions are not duplicated.",
            MessageType.None);

        jointGroup = EditorGUILayout.TextField(
            new GUIContent("Joints Subfolder", "If set, joints are placed under Joints/<name> for organization. Leave empty to place directly under Joints."),
            jointGroup);
        autoOverlapThreshold = EditorGUILayout.FloatField("Adjacency Threshold (m)", autoOverlapThreshold);
        autoDedupRadius      = EditorGUILayout.FloatField("Dedup Radius (m)",        autoDedupRadius);

        EditorGUILayout.Space(4);

        int autoSel = Selection.gameObjects.Length;
        bool canAuto = ActivePrefab != null && autoSel >= 2;
        if (autoSel >= 2)
        {
            bool anyAsync  = Selection.gameObjects.Any(IsAsyncPart);
            bool cutPoints = Selection.gameObjects
                .SelectMany(g => g.GetComponentsInChildren<FakeStructurePart>(true))
                .Any(fsp => fsp.type == FakeStructurePart.JointType.CutPoint);
            int islandCount = Selection.gameObjects.Sum(g => GetIslandFSPs(g).Count);

            string advice;
            if (anyAsync)
                advice = cutPoints
                    ? $"{autoSel} selected ({islandCount} islands) — async parts detected. Invisible Joints needed at interfaces. Cut points found — baked cuttable seams available."
                    : $"{autoSel} selected ({islandCount} islands) — async parts detected. Invisible Joints needed at interfaces.";
            else
                advice = cutPoints
                    ? $"{autoSel} selected ({islandCount} islands) — all baked. Cut points found — may form cuttable seams if SP_Mats are compatible."
                    : $"{autoSel} selected ({islandCount} islands) — all baked. May auto-joint if SP_Mats are compatible.";

            EditorGUILayout.HelpBox(advice, anyAsync ? MessageType.Warning : MessageType.Info);
        }
        else
            EditorGUILayout.HelpBox("Select 2 or more parts in the hierarchy first.", MessageType.Warning);

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

    static void DrawSeparator()
    {
        var rect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 1f));
    }

    // ── Snap helpers ──────────────────────────────────────────────────────────

    GameObject ObjectToMove => movingIndex >= 0 && snapCandidates != null ? snapCandidates[movingIndex] : null;
    GameObject TargetObject => movingIndex >= 0 && snapCandidates != null ? snapCandidates[1 - movingIndex] : null;

    void ShowPreview()
    {
        var a = ObjectToMove; var b = TargetObject;
        if (a == null || b == null) return;
        Bounds bA = GetBounds(a), bB = GetBounds(b);
        Vector3 dir = GetDirection(bA, bB);
        float gap = CalculateGap(bA, bB, dir);
        EditorGUILayout.HelpBox(
            $"Direction: {FormatDir(dir)}   Current gap: {gap * 100f:F1} cm",
            MessageType.None);
    }

    void ApplyMove(float overlap)
    {
        var a = ObjectToMove; var b = TargetObject;
        if (a == null || b == null) return;
        Bounds bA = GetBounds(a), bB = GetBounds(b);
        Vector3 dir = GetDirection(bA, bB);
        float move = CalculateGap(bA, bB, dir) + overlap;
        Undo.RecordObject(a.transform, "Joint Assist Snap");
        a.transform.position += dir * move;
        statusMessage = $"Moved '{a.name}' {move * 100f:F1} cm toward '{b.name}'.";
        statusType = MessageType.Info;
        Repaint();
    }

    // ── Joint placement ───────────────────────────────────────────────────────

    void PlaceJoint()
    {
        var a = ObjectToMove; var b = TargetObject;
        var prefab = ActivePrefab;
        if (a == null || b == null || prefab == null) return;
        Bounds bA = GetBounds(a), bB = GetBounds(b);
        Vector3 dir = GetDirection(bA, bB);
        Vector3 pos = bB.center - dir * ReachInDir(bB, dir);
        Transform parent = ResolveParent(a);
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        inst.transform.localScale = Vector3.one;
        inst.transform.position = pos;
        if (IsUsingCutPoints)
            inst.transform.rotation = CutPointRotation(dir);
        else
            CenterColliderOnPos(inst);
        Undo.RegisterCreatedObjectUndo(inst, "Place Joint");
        Selection.activeGameObject = inst;
        statusMessage = $"Placed {(IsUsingCutPoints ? "Cut Point" : "Invisible Joint")} at {pos:F3}.";
        statusType = MessageType.Info;
        Repaint();
    }

    // Collects top-level AddressableLoader transforms (stops recursion at each loader found).
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

    // Returns FSP world bounds grouped by island for a selected GameObject.
    // Each direct child of an EditorCache fake that contains FSPs is a separate island.
    // If the fake is flat (no child grouping), the whole loader is one island.
    static List<List<Bounds>> GetIslandFSPs(GameObject go)
    {
        var result = new List<List<Bounds>>();
        var loaders = new List<Transform>();
        CollectTopLevelLoaders(go.transform, loaders);

        if (loaders.Count == 0)
        {
            // Baked object — all its own FSPs are one island.
            var fsps = go.GetComponentsInChildren<FakeStructurePart>(true)
                         .Select(fsp => TransformBoundsToWorld(fsp.transform.localToWorldMatrix, fsp.localColliderBounds))
                         .ToList();
            if (fsps.Count > 0) result.Add(fsps);
            return result;
        }

        foreach (var loader in loaders)
        {
            // Find the EditorCache fake — direct child with FakePrefabDisplay or SelectAddressableParent.
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

            // Spatial union-find: FSPs whose expanded bounds touch are in the same island.
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

    void AutoPlaceInvisibleJoints()
    {
        var selected = Selection.gameObjects;
        Transform parent = ResolveParent(selected[0]);

        // Expand each selected object into its async islands.
        var perObject = new List<List<Bounds>>();
        foreach (var go in selected)
            perObject.AddRange(GetIslandFSPs(go));
        int n = perObject.Count;

        // Build all candidate edges (one best interface per object pair), sorted by overlap area descending.
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

        // Kruskal's: sort edges by area descending, union-find to connect components with minimum joints.
        edges.Sort((a, b) => b.area.CompareTo(a.area));
        var uf = new int[n];
        for (int k = 0; k < n; k++) uf[k] = k;

        int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
        void Union(int a, int b) { uf[Find(a)] = Find(b); }

        var placed = IsUsingCutPoints
            ? new List<Vector3>()
            : Object.FindObjectsOfType<InvisibleJointMarker>().Select(m => m.transform.position).ToList();
        int count = 0;

        Undo.SetCurrentGroupName("Auto-Place InvisibleJoints");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (var (area, i, j, pos, sepDir) in edges)
        {
            if (Find(i) == Find(j)) continue; // already connected
            if (placed.Any(p => Vector3.Distance(p, pos) < autoDedupRadius)) { Union(i, j); continue; }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(ActivePrefab, parent);
            inst.transform.localScale = Vector3.one;
            inst.transform.position = pos;
            if (IsUsingCutPoints)
                inst.transform.rotation = CutPointRotation(sepDir);
            else
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    Transform ResolveParent(GameObject go)
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

        if (string.IsNullOrWhiteSpace(jointGroup))
            return joints;

        Transform sub = joints.Find(jointGroup);
        if (sub != null) return sub;
        var subGo = new GameObject(jointGroup);
        Undo.RegisterCreatedObjectUndo(subGo, "Create Joint Group");
        subGo.transform.SetParent(joints, false);
        return subGo.transform;
    }

    static Quaternion CutPointRotation(Vector3 sepDir)
    {
        // Align the cut point's local X (thin axis, 0.125m extent) with sepDir so it straddles the seam.
        // FromToRotation is ambiguous at 180° but sepDir is always a cardinal, so this is safe for ±Y/±Z;
        // for ±X we need an explicit axis to avoid the degenerate case.
        if (Vector3.Dot(sepDir, Vector3.right) < -0.99f)
            return Quaternion.AngleAxis(180f, Vector3.up);
        return Quaternion.FromToRotation(Vector3.right, sepDir);
    }

    static bool IsAsyncPart(GameObject go)
    {
        for (var t = go.transform; t != null; t = t.parent)
            if (t.TryGetComponent<BBI.Unity.Game.AddressableLoader>(out _)) return true;
        return false;
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

    Vector3 GetDirection(Bounds bA, Bounds bB)
    {
        if (axis != OverlapAxis.AutoDetect)
        {
            return axis switch
            {
                OverlapAxis.PosX => Vector3.right,
                OverlapAxis.NegX => Vector3.left,
                OverlapAxis.PosY => Vector3.up,
                OverlapAxis.NegY => Vector3.down,
                OverlapAxis.PosZ => Vector3.forward,
                OverlapAxis.NegZ => Vector3.back,
                _ => Vector3.up
            };
        }
        Vector3[] axes = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        float bestGap = float.MaxValue;
        Vector3 bestDir = Vector3.up;
        foreach (var a in axes)
        {
            float g = CalculateGap(bA, bB, a);
            if (g >= 0 && g < bestGap) { bestGap = g; bestDir = a; }
        }
        if (bestGap == float.MaxValue)
            bestDir = ClosestCardinalDirection(bB.center - bA.center);
        return bestDir;
    }

    float CalculateGap(Bounds bA, Bounds bB, Vector3 dir)
    {
        float faceA = Vector3.Dot(bA.center, dir) + ReachInDir(bA, dir);
        float faceB = Vector3.Dot(bB.center, dir) - ReachInDir(bB, dir);
        return faceB - faceA;
    }

    float ReachInDir(Bounds b, Vector3 dir)
        => Mathf.Abs(b.extents.x * dir.x) + Mathf.Abs(b.extents.y * dir.y) + Mathf.Abs(b.extents.z * dir.z);

    Vector3 ClosestCardinalDirection(Vector3 v)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        if (ax > ay && ax > az) return v.x > 0 ? Vector3.right : Vector3.left;
        if (ay > az) return v.y > 0 ? Vector3.up : Vector3.down;
        return v.z > 0 ? Vector3.forward : Vector3.back;
    }

    string FormatDir(Vector3 d)
    {
        if (d == Vector3.right)   return "+X";
        if (d == Vector3.left)    return "-X";
        if (d == Vector3.up)      return "+Y";
        if (d == Vector3.down)    return "-Y";
        if (d == Vector3.forward) return "+Z";
        if (d == Vector3.back)    return "-Z";
        return d.ToString("F2");
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
}
