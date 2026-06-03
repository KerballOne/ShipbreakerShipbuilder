using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEditor;

public class JointAssistWindow : EditorWindow
{
    // Cut point state
    GameObject cutPointPrefab;
    bool pickingCutPoint;

    // Face snap state — each face stores the picked point, normal (in local space of source), and source object
    struct PickedFace { public Vector3 point; public Vector3 normal; public GameObject source; public Vector3 localNormal; }
    PickedFace? snapFaceA;
    PickedFace? snapFaceB;
    int pickingSnapFace; // 0 = none, 1 = A, 2 = B
    float overlapAmount = 0.025f;

    // Per-face snap point mode: false = click point, true = center of face
    bool snapPointModeA; // false = Click Point, true = Center of Face
    bool snapPointModeB;

    // Axis constraints for Part A (pos X Y Z, rot X Y Z) — all default true
    bool snapPosX = true, snapPosY = true, snapPosZ = true;
    bool snapRotX = true, snapRotY = true, snapRotZ = true;

    // Joint placement state
    GameObject invisibleJointPrefab;
    float autoOverlapThreshold = 0.02f;
    float autoDedupRadius      = 0.05f;

    // Joint compatibility check state
    float compatMeshThreshold = 0.02f;

    struct CompatResult
    {
        public enum State { None, Pass, Warn, Fail }
        public State state;
        public string message;
    }
    CompatResult compatSPMat   = new CompatResult { state = CompatResult.State.None };
    CompatResult compatMJC     = new CompatResult { state = CompatResult.State.None };
    CompatResult compatMesh    = new CompatResult { state = CompatResult.State.None };
    float        compatMeshDist = float.MaxValue;
    bool         compatMeshOverlap = false;
    GameObject   compatMeshGoA, compatMeshGoB;
    Vector3      compatMeshCenterA, compatMeshCenterB;
    Vector3      compatMeshNormalA, compatMeshNormalB;
    Vector3[]    compatOverlapPoly; // world-space vertices of the winning overlap polygon

    // jsa_compat.json: key = "JsaName1|JsaName2" (sorted), value = true/false
    Dictionary<string, bool> jsaCompatTable;

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
        DrawFacePickButton(1, "Face A", "Moving",     ref snapFaceA, activeColor, pickedColor, ref snapPointModeA);
        DrawFacePickButton(2, "Face B", "Flush with", ref snapFaceB, activeColor, pickedColor, ref snapPointModeB);

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
            "Invisible Joints are created as siblings of the first selected part. " +
            "Existing joints at the same positions are not duplicated.",
            MessageType.None);

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

        // ── Joint Compatibility ───────────────────────────────────────────────
        GUILayout.Space(12);
        DrawSeparator();
        GUILayout.Space(8);

        EditorGUILayout.LabelField("Joint Compatibility", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);

        compatMeshThreshold = EditorGUILayout.FloatField("Mesh Proximity Threshold (m)", compatMeshThreshold);

        int compatSel = Selection.gameObjects.Length;
        using (new EditorGUI.DisabledScope(compatSel < 2))
        {
            if (GUILayout.Button($"Check Compatibility  ({compatSel} selected)", GUILayout.Height(30)))
                RunCompatibilityCheck();
        }

        DrawCompatResult(compatSPMat,  "SP_Mat / JSA");
        DrawCompatResult(compatMJC,    "MandatoryJointContainer");
        DrawCompatMeshResult();

        // ── Scene Overlay ─────────────────────────────────────────────────────
        GUILayout.Space(12);
        DrawSeparator();
        GUILayout.Space(8);
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

    void DrawFacePickButton(int slot, string label, string staticPrefix, ref PickedFace? face, Color activeColor, Color pickedColor, ref bool centerMode)
    {
        bool isPickingThis = pickingSnapFace == slot;
        var prevBG = GUI.backgroundColor;

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(staticPrefix, GUILayout.Width(68));

        string btnText;
        bool showTwoRows = false;
        int buttonHeight = 26;

        if (isPickingThis)
        {
            GUI.backgroundColor = activeColor;
            btnText = "Cancel";
        }
        else if (face.HasValue && face.Value.source != null)
        {
            GUI.backgroundColor = pickedColor;
            string faceName = face.Value.source.name;

            // Only show ancestor row if the other face is also selected (we can calculate the ancestor)
            GameObject otherSource = slot == 1 ? (snapFaceB?.source) : (snapFaceA?.source);
            if (otherSource != null)
            {
                Transform ancestor = FindMoveRoot(face.Value.source, otherSource);
                string ancestorName = ancestor != null ? ancestor.name : "?";

                // Build button text and measure it to truncate dynamically
                btnText = $"{ancestorName}\n└ {faceName}";
                showTwoRows = true;
                buttonHeight = 35;

                // Measure text and truncate if needed
                btnText = TruncateButtonText(ancestorName, faceName, 0.8f);
            }
            else
            {
                btnText = faceName;
            }
        }
        else
        {
            btnText = $"Pick {label}";
        }

        // Create left-aligned button style with normal size when 1 row, smaller when 2 rows
        var buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.wordWrap = true;
        buttonStyle.alignment = TextAnchor.MiddleLeft;
        if (showTwoRows)
        {
            buttonStyle.fontSize = Mathf.RoundToInt(GUI.skin.button.fontSize * 0.8f);
            buttonStyle.padding = new RectOffset(4, 4, 0, 0);
            buttonStyle.margin = new RectOffset(0, 0, 0, 0);
        }

        if (GUILayout.Button(btnText, buttonStyle, GUILayout.Height(buttonHeight)))
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

        // Snap point mode row
        EditorGUILayout.BeginHorizontal();
        GUILayout.Space(72); // align under button
        string[] modeLabels = { "Click Point", "Center of Face" };
        int modeIdx = centerMode ? 1 : 0;
        int newIdx = EditorGUILayout.Popup(modeIdx, modeLabels);
        centerMode = newIdx == 1;
        EditorGUILayout.EndHorizontal();

        // Axis checkboxes for Part A only
        if (slot == 1)
        {
            // Position row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(72);
            GUILayout.Label("Pos:", GUILayout.Width(28));
            snapPosX = GUILayout.Toggle(snapPosX, "X", GUILayout.Width(26));
            snapPosY = GUILayout.Toggle(snapPosY, "Y", GUILayout.Width(26));
            snapPosZ = GUILayout.Toggle(snapPosZ, "Z", GUILayout.Width(26));
            EditorGUILayout.EndHorizontal();

            // Rotation row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(72);
            GUILayout.Label("Rot:", GUILayout.Width(28));
            snapRotX = GUILayout.Toggle(snapRotX, "X", GUILayout.Width(26));
            snapRotY = GUILayout.Toggle(snapRotY, "Y", GUILayout.Width(26));
            snapRotZ = GUILayout.Toggle(snapRotZ, "Z", GUILayout.Width(26));
            EditorGUILayout.EndHorizontal();
        }
    }

    // ── Compatibility check ───────────────────────────────────────────────────

    void RunCompatibilityCheck()
    {
        var sel = Selection.gameObjects;
        if (sel.Length < 2) return;

        compatOverlapPoly = null;

        // ── #1  SP_Mat / JSA ──────────────────────────────────────────────────
        compatSPMat = CheckSPMatCompat(sel);

        // ── #2  MandatoryJointContainer ───────────────────────────────────────
        compatMJC = CheckMJC(sel);

        // ── #3  Mesh proximity ────────────────────────────────────────────────
        (compatMesh, compatMeshDist, compatMeshOverlap) = CheckMeshProximity(sel);

        Repaint();
    }

    // #1: Read AddressableSOLoader.refs[0] from each selected subtree, look up
    // the asset name, then check against jsa_compat.json if available.
    CompatResult CheckSPMatCompat(GameObject[] sel)
    {
        // Collect one SP_Mat GUID per side (first AddressableSOLoader.refs[0] found)
        var guids = sel.Select(go => GetSPMatGuid(go)).ToList();
        var names = guids.Select(g => GuidToAssetName(g)).ToList();

        if (guids.All(g => g == null))
            return new CompatResult { state = CompatResult.State.Warn, message = "SP_Mat: No AddressableSOLoader found on any selected object." };

        // Build display string of what we found on each side
        var sideLabels = sel.Zip(names, (go, n) => $"{go.name} → {n ?? "?"}").ToList();
        string sides = string.Join("\n", sideLabels);

        // Try JSA compat table
        EnsureJsaCompatTable();
        if (jsaCompatTable != null && guids.Count >= 2 && names[0] != null && names[1] != null)
        {
            // Strip .asset suffix, sort, look up
            string a = StripAsset(names[0]), b = StripAsset(names[1]);
            string key = string.Compare(a, b, System.StringComparison.Ordinal) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
            if (jsaCompatTable.TryGetValue(key, out bool compat))
            {
                var state = compat ? CompatResult.State.Pass : CompatResult.State.Fail;
                string verdict = compat ? "Compatible" : "Incompatible — will NOT auto-joint";
                return new CompatResult { state = state, message = $"SP_Mat: {verdict}\n{sides}" };
            }
            return new CompatResult { state = CompatResult.State.Warn, message = $"SP_Mat: Pair not in jsa_compat.json (run PartInfoLogger in-game to populate)\n{sides}" };
        }

        // No table — just report the names
        string tableNote = jsaCompatTable == null
            ? " (no jsa_compat.json — run PartInfoLogger in-game to get verdicts)"
            : " (only one side found)";
        return new CompatResult { state = CompatResult.State.Warn, message = $"SP_Mat:{tableNote}\n{sides}" };
    }

    static string GetSPMatGuid(GameObject go)
    {
        // Walk subtree for AddressableSOLoader; refs[0] is the SP_Mat GUID
        foreach (var loader in go.GetComponentsInChildren<BBI.Unity.Game.AddressableSOLoader>(true))
            if (loader.refs != null && loader.refs.Count > 0 && !string.IsNullOrEmpty(loader.refs[0]))
                return loader.refs[0];
        return null;
    }

    static string GuidToAssetName(string guid)
    {
        if (guid == null) return null;
        var path = AssetDatabase.GUIDToAssetPath(guid);
        if (!string.IsNullOrEmpty(path)) return Path.GetFileName(path);
        // Fall back to known_assets.json lookup
        var knownPath = Path.Combine(Application.dataPath, "..", "known_assets.json");
        if (!File.Exists(knownPath)) return null;
        try
        {
            var json = File.ReadAllText(knownPath);
            // Fast string search for the GUID rather than a full parse
            int idx = json.IndexOf(guid, System.StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return null;
            int q1 = json.IndexOf('"', colon + 1);
            int q2 = json.IndexOf('"', q1 + 1);
            if (q1 < 0 || q2 < 0) return null;
            return Path.GetFileName(json.Substring(q1 + 1, q2 - q1 - 1));
        }
        catch { return null; }
    }

    void EnsureJsaCompatTable()
    {
        if (jsaCompatTable != null) return;
        var path = Path.Combine(Application.dataPath, "..", "jsa_compat.json");
        if (!File.Exists(path)) return;
        try
        {
            jsaCompatTable = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(path));
        }
        catch { }
    }

    static string StripAsset(string name)
        => name != null && name.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - 6) : name;

    // #2: Both sides must share the same MandatoryJointContainer ancestor.
    // Also accept: neither side has one (they'll rely on JSA pairing instead).
    static CompatResult CheckMJC(GameObject[] sel)
    {
        var mjcPerSide = sel.Select(go =>
            go.GetComponentInParent<BBI.Unity.Game.MandatoryJointContainer>()).ToList();

        bool anyHasMJC = mjcPerSide.Any(m => m != null);
        if (!anyHasMJC)
            return new CompatResult { state = CompatResult.State.Pass, message = "MJC: Neither side has a MandatoryJointContainer — jointing via JSA pairing." };

        // Check that all selected objects share the same MJC
        var distinct = mjcPerSide.Where(m => m != null).Distinct().ToList();
        if (distinct.Count == 1 && mjcPerSide.All(m => m == distinct[0]))
            return new CompatResult { state = CompatResult.State.Pass, message = $"MJC: Shared — '{distinct[0].gameObject.name}' covers all selected parts." };

        // Some have MJC, some don't, or they have different MJCs
        var labels = sel.Zip(mjcPerSide, (go, m) =>
            m != null ? $"{go.name} → {m.gameObject.name}" : $"{go.name} → none").ToList();
        bool someMissing = mjcPerSide.Any(m => m == null);
        string msg = someMissing
            ? "MJC: Mismatch — some sides lack a MandatoryJointContainer."
            : "MJC: Different containers — parts will NOT be mandatory-jointed together.";
        return new CompatResult { state = CompatResult.State.Fail, message = $"{msg}\n{string.Join("\n", labels)}" };
    }

    // #3: Find the minimum face-center-to-plane distance across all StructurePart
    // MeshFilter pairs between the two sides. Antiparallel normals only (faces pointing at each other).
    (CompatResult result, float dist, bool overlap) CheckMeshProximity(GameObject[] sel)
    {
        // Gather StructurePart MeshFilters from each side
        var sidesFilters = sel.Select(go => CollectSPMeshFilters(go)).ToList();

        if (sidesFilters.Count < 2 || sidesFilters[0].Count == 0 || sidesFilters[1].Count == 0)
            return (new CompatResult { state = CompatResult.State.Warn,
                message = "Mesh: No StructurePart MeshFilters found on one or both sides." }, float.MaxValue, false);

        // Winner = largest overlapping area between antiparallel triangle pairs.
        // Pairs with no tangential overlap are skipped entirely.
        float bestArea   = -1f;
        float winnerDist = float.MaxValue;
        bool  overlap    = false;

        var polyBuf = new List<Vector2>(8); // reused scratch buffer for SH clipping

        foreach (var mfA in sidesFilters[0])
        {
            if (mfA.sharedMesh == null) continue;
            var meshA  = mfA.sharedMesh;
            var trisA  = meshA.triangles;
            var vertsA = meshA.vertices;
            var normsA = meshA.normals;
            var mA     = mfA.transform.localToWorldMatrix;

            foreach (var mfB in sidesFilters[1])
            {
                if (mfB.sharedMesh == null) continue;
                var meshB  = mfB.sharedMesh;
                var trisB  = meshB.triangles;
                var vertsB = meshB.vertices;
                var normsB = meshB.normals;
                var mB     = mfB.transform.localToWorldMatrix;

                for (int ia = 0; ia < trisA.Length; ia += 3)
                {
                    Vector3 wa0 = mA.MultiplyPoint3x4(vertsA[trisA[ia]]);
                    Vector3 wa1 = mA.MultiplyPoint3x4(vertsA[trisA[ia+1]]);
                    Vector3 wa2 = mA.MultiplyPoint3x4(vertsA[trisA[ia+2]]);
                    Vector3 centerA = (wa0 + wa1 + wa2) / 3f;
                    Vector3 lnA = normsA.Length > 0
                        ? ((normsA[trisA[ia]] + normsA[trisA[ia+1]] + normsA[trisA[ia+2]]) / 3f).normalized
                        : Vector3.Cross(vertsA[trisA[ia+1]] - vertsA[trisA[ia]], vertsA[trisA[ia+2]] - vertsA[trisA[ia]]).normalized;
                    Vector3 wnA = mA.MultiplyVector(lnA).normalized;

                    // Build a tangent frame on face A's plane
                    Vector3 tanA  = Vector3.Cross(wnA, Mathf.Abs(wnA.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                    Vector3 bitanA = Vector3.Cross(wnA, tanA).normalized;

                    // Triangle A in 2D plane space
                    Vector2 a0 = new Vector2(Vector3.Dot(wa0 - centerA, tanA), Vector3.Dot(wa0 - centerA, bitanA));
                    Vector2 a1 = new Vector2(Vector3.Dot(wa1 - centerA, tanA), Vector3.Dot(wa1 - centerA, bitanA));
                    Vector2 a2 = new Vector2(Vector3.Dot(wa2 - centerA, tanA), Vector3.Dot(wa2 - centerA, bitanA));

                    for (int ib = 0; ib < trisB.Length; ib += 3)
                    {
                        Vector3 wb0 = mB.MultiplyPoint3x4(vertsB[trisB[ib]]);
                        Vector3 wb1 = mB.MultiplyPoint3x4(vertsB[trisB[ib+1]]);
                        Vector3 wb2 = mB.MultiplyPoint3x4(vertsB[trisB[ib+2]]);
                        Vector3 centerB = (wb0 + wb1 + wb2) / 3f;
                        Vector3 lnB = normsB.Length > 0
                            ? ((normsB[trisB[ib]] + normsB[trisB[ib+1]] + normsB[trisB[ib+2]]) / 3f).normalized
                            : Vector3.Cross(vertsB[trisB[ib+1]] - vertsB[trisB[ib]], vertsB[trisB[ib+2]] - vertsB[trisB[ib]]).normalized;
                        Vector3 wnB = mB.MultiplyVector(lnB).normalized;

                        // Antiparallel: faces must point toward each other
                        if (Vector3.Dot(wnA, wnB) > -0.9f) continue;

                        // Plane distance along A's normal
                        float dAB = Mathf.Abs(Vector3.Dot(centerB - centerA, wnA));
                        float dBA = Mathf.Abs(Vector3.Dot(centerA - centerB, wnB));
                        float d = Mathf.Min(dAB, dBA);

                        // Project B's verts onto A's plane and into A's 2D frame
                        Vector2 b0 = new Vector2(Vector3.Dot(wb0 - centerA, tanA), Vector3.Dot(wb0 - centerA, bitanA));
                        Vector2 b1 = new Vector2(Vector3.Dot(wb1 - centerA, tanA), Vector3.Dot(wb1 - centerA, bitanA));
                        Vector2 b2 = new Vector2(Vector3.Dot(wb2 - centerA, tanA), Vector3.Dot(wb2 - centerA, bitanA));

                        // Clip triangle B against triangle A using Sutherland-Hodgman
                        float area = TriTriOverlapArea(a0, a1, a2, b0, b1, b2, polyBuf);
                        if (area <= 0f) continue;

                        // Score = area / (plane distance + 1mm): rewards large overlap AND proximity.
                        // A flush touching pair always beats a distant coplanar pair of equal area.
                        float score = area / (d + 0.001f);
                        if (score > bestArea)
                        {
                            bestArea   = score;
                            winnerDist = d;
                            // overlap = B's center is behind A's face plane (penetrating)
                            overlap = Vector3.Dot(centerB - centerA, wnA) < 0f;
                            compatMeshGoA     = mfA.gameObject;
                            compatMeshGoB     = mfB.gameObject;
                            compatMeshCenterA = centerA;
                            compatMeshCenterB = centerB;
                            compatMeshNormalA = wnA;
                            compatMeshNormalB = wnB;
                            // Reconstruct polygon in world space from polyBuf (2D plane coords → 3D)
                            compatOverlapPoly = new Vector3[polyBuf.Count];
                            for (int pi = 0; pi < polyBuf.Count; pi++)
                                compatOverlapPoly[pi] = centerA
                                    + polyBuf[pi].x * tanA
                                    + polyBuf[pi].y * bitanA;
                        }
                    }
                }
            }
        }

        if (bestArea < 0f)
            return (new CompatResult { state = CompatResult.State.Warn,
                message = "Mesh: No overlapping face pairs found between the two sides." }, float.MaxValue, false);

        bool jointable = winnerDist <= compatMeshThreshold;
        var state = jointable ? CompatResult.State.Pass : CompatResult.State.Fail;
        string verdict = jointable ? "Within threshold — likely jointable" : "Too far apart — unlikely to joint";
        return (new CompatResult { state = state, message = $"Mesh: {verdict}" }, winnerDist, overlap);
    }

    // Returns the area of the intersection polygon of two 2D triangles.
    // Uses Sutherland-Hodgman: clip subject (B) against each edge of clip (A).
    static float TriTriOverlapArea(Vector2 a0, Vector2 a1, Vector2 a2,
                                   Vector2 b0, Vector2 b1, Vector2 b2,
                                   List<Vector2> buf)
    {
        buf.Clear();
        buf.Add(b0); buf.Add(b1); buf.Add(b2);

        Vector2[] clip = { a0, a1, a2 };
        var input = new List<Vector2>(8);

        for (int e = 0; e < 3; e++)
        {
            if (buf.Count == 0) return 0f;
            Vector2 edgeA = clip[e], edgeB = clip[(e + 1) % 3];
            // Inside = left of directed edge (edgeA → edgeB)
            input.Clear();
            input.AddRange(buf);
            buf.Clear();

            for (int i = 0; i < input.Count; i++)
            {
                Vector2 cur  = input[i];
                Vector2 prev = input[(i + input.Count - 1) % input.Count];
                bool curIn  = Cross2D(edgeB - edgeA, cur  - edgeA) >= 0f;
                bool prevIn = Cross2D(edgeB - edgeA, prev - edgeA) >= 0f;

                if (curIn)
                {
                    if (!prevIn) buf.Add(LineIntersect2D(prev, cur, edgeA, edgeB));
                    buf.Add(cur);
                }
                else if (prevIn)
                {
                    buf.Add(LineIntersect2D(prev, cur, edgeA, edgeB));
                }
            }
        }

        if (buf.Count < 3) return 0f;

        // Shoelace area
        float area = 0f;
        for (int i = 0; i < buf.Count; i++)
        {
            Vector2 p = buf[i], q = buf[(i + 1) % buf.Count];
            area += p.x * q.y - q.x * p.y;
        }
        return Mathf.Abs(area) * 0.5f;
    }

    static float Cross2D(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    static Vector2 LineIntersect2D(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        Vector2 d1 = p2 - p1, d2 = p4 - p3;
        float denom = Cross2D(d1, d2);
        if (Mathf.Abs(denom) < 1e-10f) return (p1 + p2) * 0.5f;
        float t = Cross2D(p3 - p1, d2) / denom;
        return p1 + t * d1;
    }

    static List<MeshFilter> CollectSPMeshFilters(GameObject go)
    {
        var result = new List<MeshFilter>();
        foreach (var sp in go.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true))
        {
            var mf = sp.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                result.Add(mf);
        }
        // Also check fake hierarchy (AddressableLoader children)
        var loaders = new List<Transform>();
        CollectTopLevelLoaders(go.transform, loaders);
        foreach (var loader in loaders)
        {
            for (int c = 0; c < loader.childCount; c++)
            {
                var ch = loader.GetChild(c);
                if (ch.GetComponent<FakePrefabDisplay>() == null && ch.GetComponent<SelectAddressableParent>() == null) continue;
                foreach (var sp in ch.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true))
                {
                    var mf = sp.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null && !result.Contains(mf))
                        result.Add(mf);
                }
            }
        }
        return result;
    }

    void DrawCompatResult(CompatResult r, string _)
    {
        if (r.state == CompatResult.State.None) return;
        var msgType = r.state == CompatResult.State.Pass ? MessageType.Info
                    : r.state == CompatResult.State.Warn ? MessageType.Warning
                    : MessageType.Error;
        EditorGUILayout.HelpBox(r.message, msgType);
    }

    void DrawCompatMeshResult()
    {
        if (compatMesh.state == CompatResult.State.None) return;

        // Draw verdict helpbox
        var msgType = compatMesh.state == CompatResult.State.Pass ? MessageType.Info
                    : compatMesh.state == CompatResult.State.Warn ? MessageType.Warning
                    : MessageType.Error;
        EditorGUILayout.HelpBox(compatMesh.message, msgType);

        if (compatMeshDist == float.MaxValue) return;

        // Distance row with colored number and select button
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel("Closest face pair");
        var prevColor = GUI.color;
        GUI.color = compatMeshOverlap ? new Color(1f, 0.35f, 0.35f) : Color.white;
        GUILayout.Label($"{compatMeshDist * 100f:F2} cm{(compatMeshOverlap ? "  ⚠ overlapping" : "")}", EditorStyles.boldLabel);
        GUI.color = prevColor;
        using (new EditorGUI.DisabledScope(compatMeshGoA == null || compatMeshGoB == null))
        {
            if (GUILayout.Button("Select", GUILayout.Width(52)))
                Selection.objects = new Object[] { compatMeshGoA, compatMeshGoB };
        }
        EditorGUILayout.EndHorizontal();

        var miniStyle = EditorStyles.miniLabel;
        EditorGUILayout.LabelField($"  A: {compatMeshCenterA:F3}  n={compatMeshNormalA:F2}", miniStyle);
        EditorGUILayout.LabelField($"  B: {compatMeshCenterB:F3}  n={compatMeshNormalB:F2}", miniStyle);
    }

    // ── Scene picking ─────────────────────────────────────────────────────────

    void OnSceneGUI(SceneView sv)
    {
        // Draw the winning overlap polygon from the last compatibility check
        if (compatOverlapPoly != null && compatOverlapPoly.Length >= 3)
        {
            var prevColor = Handles.color;
            Handles.color = new Color(0.2f, 1f, 0.4f, 0.35f);
            Handles.DrawAAConvexPolygon(compatOverlapPoly);
            Handles.color = new Color(0.2f, 1f, 0.4f, 1f);
            for (int i = 0; i < compatOverlapPoly.Length; i++)
                Handles.DrawLine(compatOverlapPoly[i], compatOverlapPoly[(i + 1) % compatOverlapPoly.Length]);
            Handles.color = prevColor;
        }

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
                    // Store the normal in local space so it updates when the object rotates
                    Vector3 localNormal = picked.transform.worldToLocalMatrix.MultiplyVector(hitNormal);
                    var pf = new PickedFace { point = hitPoint, normal = hitNormal, source = picked, localNormal = localNormal };
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

    static Transform FindMoveRoot(GameObject partA, GameObject partBSource)
    {
        if (partBSource == null) return partA.transform;

        // Collect all ancestors of B (including B itself)
        var bAncestors = new HashSet<Transform>();
        for (var t = partBSource.transform; t != null; t = t.parent)
            bAncestors.Add(t);

        // Walk up A until A's parent is in B's ancestor chain (i.e., A and B share a parent)
        Transform a = partA.transform;
        while (a.parent != null && !bAncestors.Contains(a.parent))
            a = a.parent;
        return a;
    }

    string TruncateButtonText(string ancestorName, string faceName, float fontSizeScale)
    {
        // Measure available width from the last BeginHorizontal (approximate)
        // Account for label (68px) + padding (8px) = 76px used, so remaining width is window - 76
        float availableWidth = EditorGUIUtility.currentViewWidth - 90f; // Conservative margin
        availableWidth = Mathf.Max(availableWidth, 100f); // Minimum readable width

        // Create a temporary style to measure text
        var tempStyle = new GUIStyle(GUI.skin.button);
        tempStyle.fontSize = Mathf.RoundToInt(GUI.skin.button.fontSize * fontSizeScale);
        tempStyle.padding = new RectOffset(4, 4, 0, 0);

        // Measure the two lines and truncate if needed
        string line1 = ancestorName;
        string line2 = $"└ {faceName}";

        // Truncate line 1
        var content1 = new GUIContent(line1);
        Vector2 size1 = tempStyle.CalcSize(content1);
        if (size1.x > availableWidth)
        {
            while (line1.Length > 1 && tempStyle.CalcSize(new GUIContent(line1 + "…")).x > availableWidth)
                line1 = line1.Substring(0, line1.Length - 1);
            line1 += "…";
        }

        // Truncate line 2
        var content2 = new GUIContent(line2);
        Vector2 size2 = tempStyle.CalcSize(content2);
        if (size2.x > availableWidth)
        {
            // Start from the face name part (after "└ ")
            string facePart = faceName;
            while (facePart.Length > 1 && tempStyle.CalcSize(new GUIContent($"└ {facePart}…")).x > availableWidth)
                facePart = facePart.Substring(0, facePart.Length - 1);
            line2 = $"└ {facePart}…";
        }

        return $"{line1}\n{line2}";
    }

    Vector3 FindFaceCenter(GameObject source, Vector3 normal)
    {
        // Find all triangles with the same normal and average their centers
        Vector3 faceSum = Vector3.zero;
        int triCount = 0;
        const float normalTolerance = 0.1f;

        foreach (var mf in source.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            var mesh = mf.sharedMesh;
            var tris = mesh.triangles;
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var m = mf.transform.localToWorldMatrix;

            for (int ti = 0; ti < tris.Length; ti += 3)
            {
                Vector3 v0 = verts[tris[ti]], v1 = verts[tris[ti+1]], v2 = verts[tris[ti+2]];
                Vector3 ln = normals.Length > 0
                    ? ((normals[tris[ti]] + normals[tris[ti+1]] + normals[tris[ti+2]]) / 3f).normalized
                    : Vector3.Cross(v1 - v0, v2 - v0).normalized;
                Vector3 wn = m.MultiplyVector(ln).normalized;

                // Check if this triangle has the same normal as the target
                float normalDot = Vector3.Dot(wn, normal);
                if (normalDot < 1f - normalTolerance) continue;

                // For center mode, pick any triangle on the face as a representative
                // (we'll use the first one found with matching normal)
                Vector3 triCenter = m.MultiplyPoint3x4((v0 + v1 + v2) / 3f);
                faceSum += triCenter;
                triCount++;
            }
        }

        return triCount > 0 ? faceSum / triCount : Vector3.zero;
    }

    Vector3 GetFacePoint(PickedFace face, bool centerMode)
    {
        if (!centerMode) return face.point;
        // Center of face: find all coplanar triangles with the same normal and average their centers
        Vector3 faceSum = Vector3.zero;
        int triCount = 0;
        const float normalTolerance = 0.1f;
        const float distanceTolerance = 0.01f;

        foreach (var mf in face.source.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            var mesh = mf.sharedMesh;
            var tris = mesh.triangles;
            var verts = mesh.vertices;
            var normals = mesh.normals;
            var m = mf.transform.localToWorldMatrix;

            for (int ti = 0; ti < tris.Length; ti += 3)
            {
                Vector3 v0 = verts[tris[ti]], v1 = verts[tris[ti+1]], v2 = verts[tris[ti+2]];
                Vector3 ln = normals.Length > 0
                    ? ((normals[tris[ti]] + normals[tris[ti+1]] + normals[tris[ti+2]]) / 3f).normalized
                    : Vector3.Cross(v1 - v0, v2 - v0).normalized;
                Vector3 wn = m.MultiplyVector(ln).normalized;

                // Check if this triangle has the same normal as the clicked face
                float normalDot = Vector3.Dot(wn, face.normal);
                if (normalDot < 1f - normalTolerance) continue;

                // Check if this triangle is coplanar with the clicked point
                Vector3 triCenter = m.MultiplyPoint3x4((v0 + v1 + v2) / 3f);
                float dist = Mathf.Abs(Vector3.Dot(triCenter - face.point, face.normal));
                if (dist > distanceTolerance) continue;

                // This triangle is part of the same face
                faceSum += triCenter;
                triCount++;
            }
        }

        return triCount > 0 ? faceSum / triCount : face.point;
    }

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

        snapFaceA = new PickedFace { point = faceAPoint, normal =  dir, source = sel[0], localNormal = sel[0].transform.worldToLocalMatrix.MultiplyVector(dir) };
        snapFaceB = new PickedFace { point = faceBPoint, normal = -dir, source = sel[1], localNormal = sel[1].transform.worldToLocalMatrix.MultiplyVector(-dir) };

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

        Transform moveRoot = FindMoveRoot(fA.source, fB.source);
        Undo.RecordObject(moveRoot, "Face Snap");

        // Compute current face normals from local normals (accounts for rotation)
        Vector3 currentNormalA = fA.source.transform.localToWorldMatrix.MultiplyVector(fA.localNormal).normalized;
        Vector3 currentNormalB = fB.source.transform.localToWorldMatrix.MultiplyVector(fB.localNormal).normalized;

        // For Part A: if in Center of Face mode, recompute center in current state.
        // If in Click Point mode, preserve the original offset but apply it relative to the current position.
        Vector3 ptA;
        if (snapPointModeA)
        {
            ptA = FindFaceCenter(fA.source, currentNormalA);
        }
        else
        {
            // Click Point mode: compute offset from original source position to the clicked point,
            // then apply that offset to the current source position
            Vector3 offset = fA.point - fA.source.transform.position;
            ptA = fA.source.transform.position + offset;
        }
        Vector3 ptB = GetFacePoint(fB, snapPointModeB);

        // Rotation alignment (axis-constrained) - align the current face normal to the opposite of fB's normal
        Quaternion alignRot = Quaternion.FromToRotation(currentNormalA, -currentNormalB);

        // Apply axis constraints by filtering the rotation
        if (!snapRotX || !snapRotY || !snapRotZ)
        {
            // Convert to euler, zero disabled axes, convert back
            Vector3 currentEuler = moveRoot.rotation.eulerAngles;
            Vector3 targetEuler = (alignRot * moveRoot.rotation).eulerAngles;

            if (!snapRotX) targetEuler.x = currentEuler.x;
            if (!snapRotY) targetEuler.y = currentEuler.y;
            if (!snapRotZ) targetEuler.z = currentEuler.z;

            alignRot = Quaternion.Euler(targetEuler) * Quaternion.Inverse(moveRoot.rotation);
        }

        // Pivot rotation around ptA
        Vector3 toRoot   = moveRoot.position - ptA;
        Quaternion newRot = alignRot * moveRoot.rotation;
        Vector3 newPos    = ptA + alignRot * toRoot;

        // Translation to match face points
        Vector3 targetPos = ptB + fB.normal * overlap;
        Vector3 delta     = targetPos - ptA;
        if (!snapPosX) delta.x = 0;
        if (!snapPosY) delta.y = 0;
        if (!snapPosZ) delta.z = 0;
        newPos += delta;

        moveRoot.rotation = newRot;
        moveRoot.position = newPos;

        statusMessage = $"Snapped '{moveRoot.name}' to face on '{(fB.source != null ? fB.source.name : "?")}' ({overlap * 100f:F1} cm overlap).";
        statusType    = MessageType.Info;
        Repaint();
    }

    // ── Joint placement ───────────────────────────────────────────────────────

    void AutoPlaceInvisibleJoints()
    {
        var selected = Selection.gameObjects;
        // Create joints as siblings of the first selected part
        Transform parent = selected[0].transform.parent;

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
