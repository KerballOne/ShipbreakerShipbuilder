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
    float overlapAmount = 0f;

    // Persistent crosshair hit points in world space (set on click, cleared with faces)
    bool snapHitAValid, snapHitBValid;
    Vector3 snapHitA, snapHitB;

    static readonly Color kFaceColorA       = new Color(1.0f, 0.55f, 0.0f, 1.00f); // orange
    static readonly Color kFaceColorAFill   = new Color(1.0f, 0.55f, 0.0f, 0.20f);
    static readonly Color kFaceColorB       = new Color(0.2f, 0.55f, 1.0f, 1.00f); // blue
    static readonly Color kFaceColorBFill   = new Color(0.2f, 0.55f, 1.0f, 0.20f);

    // Per-face snap point mode: false = click point, true = center of face
    bool snapPointModeA = true; // false = Click Point, true = Center of Face
    bool snapPointModeB = true;

    // How many ancestors to walk up from Face A's source before moving.
    // Initialized to the auto-sibling depth when both faces are resolved; ▼ reduces toward 1.
    int snapAncestorsUpA = 1;
    int snapAncestorsUpACeiling = 1; // the auto-sibling depth — ▲ is disabled at this value

    // Axis constraints for Part A (pos X Y Z, rot X Y Z) — all default true
    bool snapPosX = true, snapPosY = true, snapPosZ = true;
    bool snapRotX = true, snapRotY = true, snapRotZ = true;
    // Scale snap axes — default unchecked; mutually exclusive with position per axis
    bool snapScaleX = false, snapScaleY = false, snapScaleZ = false;

    // Joint placement state
    GameObject invisibleJointPrefab;
    float autoOverlapThreshold = 0.02f;
    float autoDedupRadius      = 0.05f;

    // Joint compatibility check state
    float compatCoplanarThreshold   = 0.025f; // mirrors game's coplanarDistanceThreshold
    float compatCollisionThreshold  = 0.01f;  // positive = allow this much penetration before flagging red

    struct CompatResult
    {
        public enum State { None, Pass, Warn, Fail }
        public State state;
        public string message;
    }
    CompatResult compatSPMat  = new CompatResult { state = CompatResult.State.None };
    CompatResult compatMJC    = new CompatResult { state = CompatResult.State.None };
    CompatResult compatMesh   = new CompatResult { state = CompatResult.State.None };

    // Master visibility toggle for the scene-view overlay (both jointFaces and collisionTris
    // below). Set true automatically after a Check that finds anything to show; can be
    // toggled off/on manually via the "Hide/Show Overlay" button without re-running Check.
    bool showOverlay = false;

    // Exact clipped joint polygons from CollectJointPolys/CheckMeshJoints — mirrors the game's
    // TryFindJointPolygonsJob for correctness. Drives the Mesh check's pass/fail and total
    // overlap-area (m²) message; NOT drawn directly in the scene view (superseded visually by
    // jointFaces below, which shows the same kind of overlap at finer per-triangle-pair
    // granularity). Kept separate so the pass/fail check stays exact even if the overlay's
    // coarser per-triangle test is later tuned differently.
    List<Vector3[]> jointPolygons;

    // Joint overlap highlight (PCH-style, scene-view only) — for each coplanar triangle pair,
    // only the actual overlapping region (not the whole face), colored by how far apart the
    // two triangles' planes are (green/yellow/red bands against Collision Threshold).
    struct JointFaceHighlight { public Vector3[] poly; public float planeDist; }
    List<JointFaceHighlight> jointFaces;

    // Collision (interpenetration) highlight — candidate triangles recursively subdivided and
    // culled down to only the sub-triangles whose centroid is actually inside the other mesh,
    // colored by penetration depth (green/yellow/red bands against Collision Threshold).
    struct CollisionSubTri { public Vector3 v0, v1, v2; public float depth; }
    List<CollisionSubTri> collisionTris;

    // jsa_compat.json: key = "JsaName1|JsaName2" (sorted), value = true/false
    Dictionary<string, bool> jsaCompatTable;

    string statusMessage = "";
    MessageType statusType = MessageType.None;

    Vector2 scrollPos;

    const string PrefKey    = "JointAssist.InvisibleJointPrefabGUID";
    const string CutPrefKey = "JointAssist.CutPointPrefabGUID";

    [MenuItem("Shipbuilder/Joint Assist", priority = 181)]
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

    void ReloadEnrichedData()
    {
        enrichedJsaMap    = null;
        enrichedJsaByName = null;
        spMatJsaMap       = null;
        EnsureEnrichedData();
        Repaint();
    }

    void OnGUI()
    {
        scrollPos = GUILayout.BeginScrollView(
            scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);

        // Keep label width proportional so fields shrink with the window
        float lw = Mathf.Clamp(EditorGUIUtility.currentViewWidth * 0.45f, 80f, 160f);
        EditorGUIUtility.labelWidth = lw;

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
            if (GUILayout.Button($"Auto-Detect Faces  ({selCount})", GUILayout.Height(26), GUILayout.MinWidth(0)))
                AutoDetectFaces();
        }
        using (new EditorGUI.DisabledScope(!snapFaceA.HasValue && !snapFaceB.HasValue))
        {
            if (GUILayout.Button("⇆", GUILayout.Height(26), GUILayout.Width(28)))
            {
                var tmp = snapFaceA; snapFaceA = snapFaceB; snapFaceB = tmp;
                var tmpH = snapHitA; snapHitA = snapHitB; snapHitB = tmpH;
                var tmpV = snapHitAValid; snapHitAValid = snapHitBValid; snapHitBValid = tmpV;
                ResetAncestorDepth();
                SceneView.RepaintAll();
            }
            if (GUILayout.Button("✕", GUILayout.Height(26), GUILayout.Width(28)))
            {
                snapFaceA = snapFaceB = null;
                snapHitAValid = snapHitBValid = false;
                pickingSnapFace = 0;
                SceneView.RepaintAll();
            }
        }
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(4);
        DrawFacePickButton(1, "Face A", "Moving",     ref snapFaceA, activeColor, kFaceColorA, ref snapPointModeA);
        DrawFacePickButton(2, "Face B", "Flush with", ref snapFaceB, activeColor, kFaceColorB, ref snapPointModeB);

        bool bothPicked = snapFaceA.HasValue && snapFaceB.HasValue;

        if (bothPicked)
        {
            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            overlapAmount = EditorGUILayout.FloatField(
                new GUIContent("Gap (m)", ">0 = gap between faces\n<0 = overlap/penetration\n=0 = flush"),
                overlapAmount, GUILayout.MinWidth(0));
            if (GUILayout.Button("Snap", GUILayout.MaxWidth(60), GUILayout.MinWidth(0)))
                ApplyFaceSnap(overlapAmount);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            if (GUILayout.Button("Snap Flush  (0 gap)", GUILayout.Height(32), GUILayout.MinWidth(0)))
                ApplyFaceSnap(0f);
        }

        // ── Section break ─────────────────────────────────────────────────────
        GUILayout.Space(12);
        DrawSeparator();
        GUILayout.Space(8);

        // ── Joint Compatibility ───────────────────────────────────────────────
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Joint Compatibility", EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Reload Enriched Data", EditorStyles.miniButton, GUILayout.MinWidth(0)))
            ReloadEnrichedData();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(4);

        compatCoplanarThreshold  = EditorGUILayout.FloatField(new GUIContent("Coplanar Threshold (m)",
            "How far apart two faces' planes can be and still be considered a candidate mating pair. " +
            "Used to gather coplanar joint polygons/faces and to inflate each mesh's bounds for the quick-reject test."),
            compatCoplanarThreshold);
        compatCollisionThreshold = EditorGUILayout.FloatField(new GUIContent("Collision Threshold (m)",
            "Controls the overlay's color bands: green within this distance (gap or overlap), yellow within 2x, red beyond. " +
            "Also the minimum penetration depth for a sub-triangle to count as a real collision."),
            compatCollisionThreshold);

        int compatSel = Selection.gameObjects.Length;
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(compatSel < 2))
        {
            if (GUILayout.Button($"Check  ({compatSel})", GUILayout.Height(28), GUILayout.MinWidth(0)))
                RunCompatibilityCheck();
        }
        if (jointFaces != null)
        {
            var prevBG = GUI.backgroundColor;
            GUI.backgroundColor = showOverlay ? new Color(0.2f, 0.8f, 0.4f) : GUI.backgroundColor;
            string overlayLabel = showOverlay
                ? $"Hide Overlay  ({jointFaces.Count})"
                : $"Show Overlay  ({jointFaces.Count})";
            if (GUILayout.Button(overlayLabel, GUILayout.Height(28), GUILayout.ExpandWidth(false)))
            {
                showOverlay = !showOverlay;
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = prevBG;
        }
        EditorGUILayout.EndHorizontal();

        DrawCompatResult(compatSPMat);
        DrawCompatResult(compatMJC);
        bool jsaMjcBothFail = compatSPMat.state == CompatResult.State.Fail
                           && compatMJC.state    == CompatResult.State.Fail;
        EditorGUILayout.Space(4);
        DrawSeparator();
        EditorGUILayout.Space(4);
        DrawCompatMeshResult(compatMesh, jsaMjcBothFail);

        // ── Section break ─────────────────────────────────────────────────────
        GUILayout.Space(12);
        DrawSeparator();
        GUILayout.Space(8);

        // ── Joint & Cut Point Placement ───────────────────────────────────────
        EditorGUILayout.LabelField("Joint & Cut Point Placement", EditorStyles.boldLabel);
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
            EditorGUILayout.HelpBox(
                $"{autoSel} selected ({islandCount} islands){(anyAsync ? " — async parts detected. Invisible Joints needed at interfaces." : "")}",
                MessageType.None);
        }

        using (new EditorGUI.DisabledScope(!canAuto))
        {
            if (GUILayout.Button("Auto-Place Joints", GUILayout.Height(36), GUILayout.MinWidth(0)))
                AutoPlaceInvisibleJoints();
        }

        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(statusMessage, statusType);
        }

        EditorGUILayout.Space(8);

        EditorGUI.BeginChangeCheck();
        cutPointPrefab = (GameObject)EditorGUILayout.ObjectField(
            "Cut Point Prefab", cutPointPrefab, typeof(GameObject), false);
        if (EditorGUI.EndChangeCheck()) SavePref(CutPrefKey, cutPointPrefab);

        using (new EditorGUI.DisabledScope(cutPointPrefab == null))
        {
            var prevBG = GUI.backgroundColor;
            if (pickingCutPoint) GUI.backgroundColor = new Color(0.3f, 0.6f, 1f);
            if (GUILayout.Button(pickingCutPoint ? "Cancel Pick" : "Place Cut Point", GUILayout.Height(28), GUILayout.MinWidth(0)))
            {
                pickingCutPoint  = !pickingCutPoint;
                pickingSnapFace  = 0;
                if (pickingCutPoint) { statusMessage = ""; SceneView.lastActiveSceneView?.Focus(); }
            }
            GUI.backgroundColor = prevBG;
        }

        // ── Scene Overlay ─────────────────────────────────────────────────────
        GUILayout.Space(12);
        DrawSeparator();
        GUILayout.Space(8);
        EditorGUILayout.LabelField("Scene Overlay", EditorStyles.boldLabel);
        if (GUILayout.Button("Redraw", GUILayout.Height(28), GUILayout.MinWidth(0)))
        {
            AddressableRendering.ForceResetUpdateFlag();
            AddressableRendering.ClearView();
            AddressableRendering.UpdateViewList();
        }

        GUILayout.EndScrollView();
    }

    void DrawFacePickButton(int slot, string label, string staticPrefix, ref PickedFace? face, Color activeColor, Color pickedColor, ref bool centerMode)
    {
        bool isPickingThis = pickingSnapFace == slot;
        var prevBG = GUI.backgroundColor;

        // For slot 1 (Part A): compute ancestor context when both faces are picked
        bool showAncestorControls = false;
        Transform currentAncestor = null;

        if (slot == 1 && face.HasValue && face.Value.source != null && snapFaceB.HasValue && snapFaceB.Value.source != null)
        {
            showAncestorControls = true;
            currentAncestor = FindMoveRoot(face.Value.source, snapFaceB.Value.source, snapAncestorsUpA);
        }

        EditorGUILayout.BeginHorizontal();

        // Left column: label + ▲▼ arrows (Part A only, below the label text)
        if (slot == 1)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(68));
            EditorGUILayout.LabelField(staticPrefix, GUILayout.Width(68));
            EditorGUILayout.BeginHorizontal(GUILayout.Width(68));
            var arrowStyle = new GUIStyle(GUI.skin.button) { fontSize = 10, padding = new RectOffset(0, 0, 1, 1) };
            using (new EditorGUI.DisabledScope(!showAncestorControls || snapAncestorsUpA >= snapAncestorsUpACeiling))
            {
                if (GUILayout.Button("▲", arrowStyle, GUILayout.Width(26), GUILayout.Height(16)))
                    snapAncestorsUpA++;
            }
            using (new EditorGUI.DisabledScope(!showAncestorControls || snapAncestorsUpA <= 0))
            {
                if (GUILayout.Button("▼", arrowStyle, GUILayout.Width(26), GUILayout.Height(16)))
                    snapAncestorsUpA--;
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }
        else
        {
            EditorGUILayout.LabelField(staticPrefix, GUILayout.Width(68));
        }

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

            GameObject otherSource = slot == 1 ? (snapFaceB?.source) : (snapFaceA?.source);
            if (otherSource != null)
            {
                // Part A: use current snapAncestorsUpA (already resolved to currentAncestor).
                // Part B: show the auto-sibling for display only (B never moves).
                Transform ancestor = slot == 1
                    ? currentAncestor
                    : FindMoveRoot(face.Value.source, otherSource, AutoSiblingDepth(face.Value.source, otherSource));
                string ancestorName = ancestor != null ? ancestor.name : "?";

                showTwoRows = true;
                buttonHeight = 35;

                string depthTag = slot == 1 && snapAncestorsUpA > 0 ? $"[{snapAncestorsUpA}] " : "";
                btnText = TruncateButtonText(ancestorName, depthTag + faceName, 0.8f);
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

        // Create left-aligned button style
        var buttonStyle = new GUIStyle(GUI.skin.button);
        buttonStyle.wordWrap = true;
        buttonStyle.alignment = TextAnchor.MiddleLeft;
        if (showTwoRows)
        {
            buttonStyle.fontSize = Mathf.RoundToInt(GUI.skin.button.fontSize * 0.8f);
            buttonStyle.padding = new RectOffset(4, 4, 0, 0);
            buttonStyle.margin = new RectOffset(0, 0, 0, 0);
        }

        if (GUILayout.Button(btnText, buttonStyle, GUILayout.Height(buttonHeight), GUILayout.MinWidth(0)))
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
        GUILayout.Space(68);
        string[] modeLabels = { "Click Point", "Center of Face" };
        int modeIdx = centerMode ? 1 : 0;
        int newIdx = EditorGUILayout.Popup(modeIdx, modeLabels, GUILayout.MinWidth(0));
        centerMode = newIdx == 1;
        EditorGUILayout.EndHorizontal();

        // Axis checkboxes for Part A only
        if (slot == 1)
        {
            // Position row — mutually exclusive with scale per axis
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(68);
            GUILayout.Label("Position:", GUILayout.Width(56));
            bool newPosX = GUILayout.Toggle(snapPosX, "X", GUILayout.Width(26));
            bool newPosY = GUILayout.Toggle(snapPosY, "Y", GUILayout.Width(26));
            bool newPosZ = GUILayout.Toggle(snapPosZ, "Z", GUILayout.Width(26));
            if (newPosX != snapPosX) { snapPosX = newPosX; if (newPosX) snapScaleX = false; }
            if (newPosY != snapPosY) { snapPosY = newPosY; if (newPosY) snapScaleY = false; }
            if (newPosZ != snapPosZ) { snapPosZ = newPosZ; if (newPosZ) snapScaleZ = false; }
            EditorGUILayout.EndHorizontal();

            // Rotation row
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(68);
            GUILayout.Label("Rotation:", GUILayout.Width(56));
            snapRotX = GUILayout.Toggle(snapRotX, "X", GUILayout.Width(26));
            snapRotY = GUILayout.Toggle(snapRotY, "Y", GUILayout.Width(26));
            snapRotZ = GUILayout.Toggle(snapRotZ, "Z", GUILayout.Width(26));
            EditorGUILayout.EndHorizontal();

            // Scale row — mutually exclusive with position per axis
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(68);
            GUILayout.Label("Scale:", GUILayout.Width(56));
            bool newScaleX = GUILayout.Toggle(snapScaleX, "X", GUILayout.Width(26));
            bool newScaleY = GUILayout.Toggle(snapScaleY, "Y", GUILayout.Width(26));
            bool newScaleZ = GUILayout.Toggle(snapScaleZ, "Z", GUILayout.Width(26));
            if (newScaleX != snapScaleX) { snapScaleX = newScaleX; if (newScaleX) snapPosX = false; }
            if (newScaleY != snapScaleY) { snapScaleY = newScaleY; if (newScaleY) snapPosY = false; }
            if (newScaleZ != snapScaleZ) { snapScaleZ = newScaleZ; if (newScaleZ) snapPosZ = false; }
            EditorGUILayout.EndHorizontal();
        }
    }

    // ── Compatibility check ───────────────────────────────────────────────────

    void RunCompatibilityCheck()
    {
        var sel = Selection.gameObjects;
        if (sel.Length < 2) return;

        jointPolygons = null;
        jointFaces    = null;
        collisionTris = null;
        showOverlay = false;
        // Re-read data files each check so in-game updates are picked up without reopening the window
        enrichedJsaMap = null;
        spMatJsaMap    = null;
        jsaCompatTable = null;
        knownAssetsMap = null;

        // ── #1  SP_Mat / JSA ──────────────────────────────────────────────────
        compatSPMat = CheckSPMatCompat(sel);

        // ── #2  MandatoryJointContainer ───────────────────────────────────────
        compatMJC = CheckMJC(sel);

        // ── #3  Mesh joint polygons ───────────────────────────────────────────
        (compatMesh, jointPolygons) = CheckMeshJoints(sel);

        // ── #3b  Joint overlap highlight (PCH-style, scene-view visualization) ──
        if (sel.Length >= 2)
        {
            var sidesFilters = sel.Select(go => CollectSPMeshFilters(go)).ToList();
            if (sidesFilters.Count >= 2 && sidesFilters[0].Count > 0 && sidesFilters[1].Count > 0)
            {
                jointFaces = new List<JointFaceHighlight>();
                CollectJointFaces(sidesFilters[0].Select(p => p.mf).ToList(), sidesFilters[1].Select(p => p.mf).ToList(), jointFaces);
                CollectJointFaces(sidesFilters[1].Select(p => p.mf).ToList(), sidesFilters[0].Select(p => p.mf).ToList(), jointFaces);
            }
        }
        if (jointFaces != null && jointFaces.Count > 0)
        {
            showOverlay = true;
            SceneView.RepaintAll();
        }

        // ── #4  Collision triangles ───────────────────────────────────────────
        collisionTris = FindCollisionTris(sel);
        SceneView.RepaintAll();

        Repaint();
    }

    // #1: Collect all MeshFilters (SPs) from each side, resolve JSA per SP via its
    // nearest prefab root name, then check all A×B JSA pairs against jsa_compat.json.
    CompatResult CheckSPMatCompat(GameObject[] sel)
    {
        EnsureEnrichedData();
        EnsureJsaCompatTable();

        var sidesFilters = sel.Select(go => CollectSPMeshFilters(go)).ToList();
        if (sidesFilters.Count < 2)
            return new CompatResult { state = CompatResult.State.Warn, message = "SP_Mat: Need at least 2 selected objects." };

        // Resolve unique JSA names per side — use owner GO so fake-child MFs walk the right hierarchy
        var jsasA = sidesFilters[0].Select(p => GetJsaName(p.owner)).Where(j => j != null).Distinct().ToList();
        var jsasB = sidesFilters[1].Select(p => GetJsaName(p.owner)).Where(j => j != null).Distinct().ToList();

        if (jsasA.Count == 0 && jsasB.Count == 0)
            return new CompatResult { state = CompatResult.State.Warn,
                message = "SP_Mat: Could not determine JSA for any SP in either selection.\nRun PartInfoLogger in-game with these parts loaded." };

        string labelA = jsasA.Count > 0 ? string.Join(", ", jsasA) : "?";
        string labelB = jsasB.Count > 0 ? string.Join(", ", jsasB) : "?";
        string sides = $"{sel[0].name} → {labelA}\n{sel[1].name} → {labelB}";

        if (jsaCompatTable == null)
            return new CompatResult { state = CompatResult.State.Warn,
                message = $"SP_Mat: No jsa_compat.json\n{sides}" };

        if (jsasA.Count == 0 || jsasB.Count == 0)
            return new CompatResult { state = CompatResult.State.Warn,
                message = $"SP_Mat: JSA unknown for one side — load more ship types in-game\n{sides}" };

        // Check all pairs — any compatible pair means jointing can occur
        bool anyCompat = false, anyIncompat = false, anyUnknown = false;
        foreach (var a in jsasA)
        {
            foreach (var b in jsasB)
            {
                string key = string.Compare(a, b, System.StringComparison.Ordinal) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
                if (jsaCompatTable.TryGetValue(key, out bool compat))
                { if (compat) anyCompat = true; else anyIncompat = true; }
                else anyUnknown = true;
            }
        }

        if (anyCompat)
        {
            var asyncNames = sel.Where(go => {
                for (var t = go.transform; t != null; t = t.parent)
                    if (t.TryGetComponent<BBI.Unity.Game.AddressableLoader>(out _)) return true;
                return false;
            }).Select(go => go.name).ToList();

            if (asyncNames.Count > 0)
            {
                var asyncLines = string.Join("\n", asyncNames.Select(n => $"{n} is addressable (async)"));
                return new CompatResult { state = CompatResult.State.Fail, message = $"SP_Mat: Compatible\n{sides}\n{asyncLines}" };
            }
            return new CompatResult { state = CompatResult.State.Pass, message = $"SP_Mat: Compatible\n{sides}\nWill Auto-Joint" };
        }
        if (anyIncompat && !anyUnknown)
            return new CompatResult { state = CompatResult.State.Fail, message = $"SP_Mat: Incompatible — will NOT auto-joint\n{sides}" };
        return new CompatResult { state = CompatResult.State.Warn,
            message = $"SP_Mat: Some pairs not in jsa_compat.json — load more ship types\n{sides}" };
    }

    // Resolve JSA name for the SP that owns this MeshFilter, by walking up to
    // its nearest prefab instance root and matching against enriched data.
    string GetJsaNameFromMF(MeshFilter mf) => GetJsaName(mf.gameObject);

    // Try to resolve JSA name for a GO via two paths:
    // 1. AddressableSOLoader.refs[0] → SP_Mat GUID → enriched JSON jsaName (custom baked parts)
    // 2. AddressableLoader.assetGUID → enriched JSON jsaName (game addressable parts)
    string GetJsaName(GameObject go)
    {
        var log = $"[JAW:GetJsaName] go='{go.name}' mapSize={enrichedJsaMap?.Count.ToString() ?? "null"} byNameSize={enrichedJsaByName?.Count.ToString() ?? "null"}";

        // Path 1: custom parts with AddressableSOLoader
        foreach (var loader in go.GetComponentsInChildren<BBI.Unity.Game.AddressableSOLoader>(true))
        {
            if (loader.refs == null || loader.refs.Count == 0 || string.IsNullOrEmpty(loader.refs[0])) continue;
            var jsa = EnrichedJsaName(loader.refs[0]);
            log += $"\n  P1: SOLoader ref={loader.refs[0]} → {jsa ?? "miss"}";
            if (jsa != null) { Debug.Log(log); return jsa; }
        }

        // Path 1.5: baked parts — look up this GO's StructurePart in the ancestor ACL,
        // which stores the SP_Mat address regardless of GO name.
        if (go.GetComponent<BBI.Unity.Game.StructurePart>() != null ||
            go.GetComponent<FakeStructurePart>() != null)
        {
            BBI.Unity.Game.AddressableComponentLoader acl = null;
            for (var t = go.transform.parent; t != null; t = t.parent)
                if (t.TryGetComponent<BBI.Unity.Game.AddressableComponentLoader>(out acl)) break;
            if (acl != null)
            {
                string goName = System.Text.RegularExpressions.Regex.Replace(go.name.Trim(), @"\s*\(\d+\)$", "").Trim();
                goName = System.Text.RegularExpressions.Regex.Replace(goName, @"\s*-\s*\d+$", "").Trim();
                // First pass: match by GO identity or name (pasted ACL may point at donor GOs, so fall through to second pass)
                foreach (var cv in acl.componentValues)
                {
                    if (cv.component == null) continue;
                    string cvName = System.Text.RegularExpressions.Regex.Replace(cv.component.gameObject.name.Trim(), @"\s*\(\d+\)$", "").Trim();
                    cvName = System.Text.RegularExpressions.Regex.Replace(cvName, @"\s*-\s*\d+$", "").Trim();
                    if (cv.component.gameObject != go && cvName != goName) continue;
                    var jsa = EnrichedJsaName(cv.address);
                    log += $"\n  P1.5a: ACL entry on '{cv.component.gameObject.name}' addr={cv.address} → {jsa ?? "miss"}";
                    if (jsa != null) { Debug.Log(log); return jsa; }
                }
                // Second pass: ACL was pasted from a donor (component refs point at wrong GOs).
                // Try any StructurePart entry — all SP entries in a homogeneous ACL share the same SP_Mat.
                foreach (var cv in acl.componentValues)
                {
                    if (cv.component == null) continue;
                    if (!(cv.component is BBI.Unity.Game.StructurePart)) continue;
                    var jsa = EnrichedJsaName(cv.address);
                    log += $"\n  P1.5b: any-SP ACL entry on '{cv.component.gameObject.name}' addr={cv.address} → {jsa ?? "miss"}";
                    if (jsa != null) { Debug.Log(log); return jsa; }
                }
            }
        }

        // Path 2: game addressable parts — walk up to find AddressableLoader
        for (var t = go.transform; t != null; t = t.parent)
        {
            if (!t.TryGetComponent<BBI.Unity.Game.AddressableLoader>(out var al)) continue;
            if (string.IsNullOrEmpty(al.assetGUID)) continue;
            var jsa = EnrichedJsaName(al.assetGUID);
            log += $"\n  P2: AddressableLoader on '{t.name}' guid={al.assetGUID} → {jsa ?? "miss"}";
            if (jsa != null) { Debug.Log(log); return jsa; }
            break;
        }

        // Path 3: walk up to the nearest prefab instance root matching names against enriched partName.
        var nearestPrefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
        log += $"\n  nearestPrefabRoot={nearestPrefabRoot?.name ?? "null"}";
        if (nearestPrefabRoot != null && enrichedJsaByName != null)
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                string n = System.Text.RegularExpressions.Regex.Replace(t.name, @"\s*\(\d+\)$", "").Trim();
                n = n.Replace("_Baked", "");
                enrichedJsaByName.TryGetValue(n, out var jsaByName);
                log += $"\n  P3: name='{n}' → {jsaByName ?? "miss"}";
                if (jsaByName != null) { Debug.Log(log); return jsaByName; }
                if (t == nearestPrefabRoot.transform) break;
            }

            // Path 4: prefab asset GUID → known_assets name → enrichedJsaByName
            var rootsToCheck = new HashSet<GameObject>();
            for (var t = go.transform; t != null; t = t.parent)
            {
                var r = PrefabUtility.GetNearestPrefabInstanceRoot(t.gameObject);
                if (r != null) rootsToCheck.Add(r);
                if (t == nearestPrefabRoot.transform) break;
            }
            foreach (var checkRoot in rootsToCheck)
            {
                var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(checkRoot);
                if (string.IsNullOrEmpty(assetPath)) continue;
                var prefabGuid = AssetDatabase.AssetPathToGUID(assetPath);
                string assetFileName = Path.GetFileNameWithoutExtension(assetPath);
                string n = (KnownAssetName(prefabGuid) ?? assetFileName).Replace("_Baked", "");
                enrichedJsaByName.TryGetValue(n, out var jsaByName);
                log += $"\n  P4: root='{checkRoot.name}' assetFile='{assetFileName}' knownName='{KnownAssetName(prefabGuid) ?? "null"}' n='{n}' → {jsaByName ?? "miss"}";
                if (jsaByName != null) { Debug.Log(log); return jsaByName; }
            }
        }

        Debug.LogWarning(log + "\n  → FAILED (returning null)");
        return null;
    }

    // Cache for known_assets.json guid→name lookups
    Dictionary<string, string> knownAssetsMap;

    string KnownAssetName(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        if (knownAssetsMap == null)
        {
            knownAssetsMap = new Dictionary<string, string>();
            var path = Path.Combine(Application.dataPath, "..", "known_assets.json");
            if (File.Exists(path))
            {
                try
                {
                    var raw = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                    if (raw != null)
                        foreach (var kv in raw)
                            knownAssetsMap[kv.Key] = Path.GetFileNameWithoutExtension(kv.Value);
                }
                catch { }
            }
        }
        knownAssetsMap.TryGetValue(guid, out var name);
        return name;
    }

    // enriched JSON lookups: guid → jsaName, partName → jsaName
    Dictionary<string, string> enrichedJsaMap;   // prefab guid → jsaName
    Dictionary<string, string> enrichedJsaByName; // partName/displayName → jsaName
    Dictionary<string, string> spMatJsaMap;        // SP asset guid → jsaName (via sp_jsa_map.json + known_assets)

    void EnsureEnrichedData()
    {
        if (enrichedJsaMap != null) return;
        enrichedJsaMap    = new Dictionary<string, string>();
        enrichedJsaByName = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        spMatJsaMap       = new Dictionary<string, string>();

        var path = Path.Combine(Application.dataPath, "..", "known_assets_enriched.json");
        if (File.Exists(path))
        {
            try
            {
                var raw = JsonConvert.DeserializeObject<Dictionary<string, Newtonsoft.Json.Linq.JObject>>(File.ReadAllText(path));
                if (raw != null)
                    foreach (var kv in raw)
                    {
                        var jsaTok = kv.Value["jsaName"];
                        var jsa = jsaTok != null ? jsaTok.ToString() : null;
                        if (string.IsNullOrEmpty(jsa)) continue;
                        enrichedJsaMap[kv.Key] = jsa!;
                        var nameTok = kv.Value["partName"];
                        if (nameTok != null && !string.IsNullOrEmpty(nameTok.ToString()))
                            enrichedJsaByName[nameTok.ToString()] = jsa!;
                        var displayTok = kv.Value["displayName"];
                        if (displayTok != null && !string.IsNullOrEmpty(displayTok.ToString()))
                            enrichedJsaByName[displayTok.ToString()] = jsa!;
                    }
            }
            catch { }
        }

        // Build spMatJsaMap: SP asset GUID → jsaName
        // Source 1: sp_jsa_map.json (SP asset name → jsaName, dumped at runtime by PartInfoLogger)
        // Source 2: enriched spMatName field (fallback for parts already logged with that field)
        try
        {
            var spNameToJsa = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

            // Load sp_jsa_map.json
            var spJsaPath = Path.Combine(Application.dataPath, "..", "sp_jsa_map.json");
            if (File.Exists(spJsaPath))
            {
                var spMap = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(spJsaPath));
                if (spMap != null)
                    foreach (var kv in spMap)
                        spNameToJsa[kv.Key] = kv.Value;
            }

            // Also pull spMatName from enriched entries as a fallback
            var enrichedPath = Path.Combine(Application.dataPath, "..", "known_assets_enriched.json");
            if (File.Exists(enrichedPath))
            {
                var raw2 = JsonConvert.DeserializeObject<Dictionary<string, Newtonsoft.Json.Linq.JObject>>(File.ReadAllText(enrichedPath));
                if (raw2 != null)
                    foreach (var kv in raw2)
                    {
                        var jsaTok = kv.Value["jsaName"];
                        var spTok  = kv.Value["spMatName"];
                        if (jsaTok == null || spTok == null) continue;
                        var jsa = jsaTok.ToString(); var sp = spTok.ToString();
                        if (!string.IsNullOrEmpty(jsa) && !string.IsNullOrEmpty(sp) && !spNameToJsa.ContainsKey(sp))
                            spNameToJsa[sp] = jsa;
                    }
            }

            // Map SP asset GUIDs via known_assets.json filename → spNameToJsa
            var kaPath = Path.Combine(Application.dataPath, "..", "known_assets.json");
            if (File.Exists(kaPath) && spNameToJsa.Count > 0)
            {
                var ka = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(kaPath));
                if (ka != null)
                    foreach (var kv in ka)
                    {
                        var assetName = Path.GetFileNameWithoutExtension(kv.Value);
                        if (!string.IsNullOrEmpty(assetName) && spNameToJsa.TryGetValue(assetName, out var jsa2))
                            spMatJsaMap[kv.Key] = jsa2;
                    }
            }
        }
        catch { }
    }

    string EnrichedJsaName(string guid)
    {
        if (enrichedJsaMap == null) return null;
        if (enrichedJsaMap.TryGetValue(guid, out var name)) return name;
        if (spMatJsaMap != null && spMatJsaMap.TryGetValue(guid, out name)) return name;
        return null;
    }

    void EnsureJsaCompatTable()
    {
        if (jsaCompatTable != null) return;
        var path = Path.Combine(Application.dataPath, "..", "jsa_compat.json");
        if (!File.Exists(path)) return;
        try { jsaCompatTable = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(path)); }
        catch { }
    }

    // #2: Detect async (addressable) parts — they cannot auto-joint; require InvisibleJoint.
    // #3: Both sides must share the same MandatoryJointContainer ancestor.
    // Also accept: neither side has one (they'll rely on JSA pairing instead).
    static CompatResult CheckMJC(GameObject[] sel)
    {
        var mjcPerSide = sel.Select(go =>
            go.GetComponentInParent<BBI.Unity.Game.MandatoryJointContainer>()).ToList();

        bool anyHasMJC = mjcPerSide.Any(m => m != null);
        if (!anyHasMJC)
            return new CompatResult { state = CompatResult.State.Warn, message = "MJC: Neither side has a MandatoryJointContainer." };

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

    // #3: Mirrors the game's TryFindJointPolygonsJob logic.
    // For each SP pair (A, B): collect vertices of A that are within coplanarThreshold
    // of B's plane AND whose normals are codirectional with B's normal, and vice versa.
    // Project each set onto the shared plane, compute their convex hulls, clip them.
    // Any pair with a non-zero intersection polygon is a qualifying joint; the check reports
    // total overlap area (sum of clipped polygon areas) rather than a raw polygon count.
    (CompatResult result, List<Vector3[]> polys) CheckMeshJoints(GameObject[] sel)
    {
        var sidesFilters = sel.Select(go => CollectSPMeshFilters(go)).ToList();
        if (sidesFilters.Count < 2 || sidesFilters[0].Count == 0 || sidesFilters[1].Count == 0)
            return (new CompatResult { state = CompatResult.State.Warn,
                message = "Mesh: No StructurePart MeshFilters found on one or both sides." }, null);

        var polys    = new List<Vector3[]>();
        var polyBuf  = new List<Vector2>(32);
        var ptsA2D   = new List<Vector2>(64);
        var ptsB2D   = new List<Vector2>(64);
        var areaSum  = new float[1];

        // Run both directions so every triangle on either side gets a chance
        // to be the reference plane — catches cases where only B's triangles
        // face the right way toward A.
        CollectJointPolys(sidesFilters[0].Select(p => p.mf).ToList(), sidesFilters[1].Select(p => p.mf).ToList(), polys, polyBuf, ptsA2D, ptsB2D, areaSum);
        CollectJointPolys(sidesFilters[1].Select(p => p.mf).ToList(), sidesFilters[0].Select(p => p.mf).ToList(), polys, polyBuf, ptsA2D, ptsB2D, areaSum);

        if (polys.Count == 0)
            return (new CompatResult { state = CompatResult.State.Fail,
                message = "Mesh: No joint polygons found — parts will not auto-joint." }, null);

        return (new CompatResult { state = CompatResult.State.Pass,
            message = $"Mesh: {areaSum[0]:0.####} m² of overlap found ({polys.Count} joint polygon{(polys.Count == 1 ? "" : "s")})." }, polys);
    }

    // Use triangles of listA as reference planes, collect coplanar verts from both sides,
    // compute convex hulls, clip — store any non-zero intersection polygon.
    void CollectJointPolys(List<MeshFilter> listA, List<MeshFilter> listB,
        List<Vector3[]> polys, List<Vector2> polyBuf, List<Vector2> ptsA2D, List<Vector2> ptsB2D, float[] areaSum)
    {
        foreach (var mfA in listA)
        {
            if (mfA.sharedMesh == null) continue;
            var meshA  = mfA.sharedMesh;
            var vertsA = meshA.vertices;
            var normsA = meshA.normals;
            var mA     = mfA.transform.localToWorldMatrix;
            var boundsA = TransformBoundsToWorld(mA, meshA.bounds);

            var wVertsA = new Vector3[vertsA.Length];
            var wNormsA = new Vector3[vertsA.Length];
            for (int i = 0; i < vertsA.Length; i++)
            {
                wVertsA[i] = mA.MultiplyPoint3x4(vertsA[i]);
                wNormsA[i] = mA.MultiplyVector(normsA.Length > 0 ? normsA[i] : Vector3.up).normalized;
            }

            foreach (var mfB in listB)
            {
                if (mfB.sharedMesh == null) continue;
                var meshB  = mfB.sharedMesh;
                var boundsB = TransformBoundsToWorld(mfB.transform.localToWorldMatrix, meshB.bounds);
                // Quick reject — if bounds don't overlap (with coplanar threshold margin) skip
                var expandedA = new Bounds(boundsA.center, boundsA.size + Vector3.one * compatCoplanarThreshold * 2f);
                if (!expandedA.Intersects(boundsB)) continue;
                var vertsB = meshB.vertices;
                var mB     = mfB.transform.localToWorldMatrix;

                var wVertsB = new Vector3[vertsB.Length];
                for (int i = 0; i < vertsB.Length; i++)
                    wVertsB[i] = mB.MultiplyPoint3x4(vertsB[i]);

                var trisA = meshA.triangles;
                for (int ia = 0; ia < trisA.Length; ia += 3)
                {
                    Vector3 wa0 = wVertsA[trisA[ia]], wa1 = wVertsA[trisA[ia+1]], wa2 = wVertsA[trisA[ia+2]];
                    Vector3 geomNorm = Vector3.Cross(wa1 - wa0, wa2 - wa0);
                    if (geomNorm.sqrMagnitude < 1e-10f) continue;
                    Vector3 faceNorm   = ((wNormsA[trisA[ia]] + wNormsA[trisA[ia+1]] + wNormsA[trisA[ia+2]]) / 3f).normalized;
                    Vector3 planeOrigin = (wa0 + wa1 + wa2) / 3f;

                    Vector3 tan   = Vector3.Cross(faceNorm, Mathf.Abs(faceNorm.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                    Vector3 bitan = Vector3.Cross(faceNorm, tan).normalized;

                    ptsA2D.Clear();
                    for (int i = 0; i < wVertsA.Length; i++)
                    {
                        if (Mathf.Abs(Vector3.Dot(wVertsA[i] - planeOrigin, faceNorm)) > compatCoplanarThreshold) continue;
                        ptsA2D.Add(new Vector2(Vector3.Dot(wVertsA[i] - planeOrigin, tan),
                                               Vector3.Dot(wVertsA[i] - planeOrigin, bitan)));
                    }
                    if (ptsA2D.Count < 3) continue;

                    ptsB2D.Clear();
                    float signedDistSum = 0f;
                    int signedDistCount = 0;
                    for (int i = 0; i < wVertsB.Length; i++)
                    {
                        float sd = Vector3.Dot(wVertsB[i] - planeOrigin, faceNorm);
                        if (Mathf.Abs(sd) > compatCoplanarThreshold) continue;
                        ptsB2D.Add(new Vector2(Vector3.Dot(wVertsB[i] - planeOrigin, tan),
                                               Vector3.Dot(wVertsB[i] - planeOrigin, bitan)));
                        signedDistSum += sd;
                        signedDistCount++;
                    }
                    if (ptsB2D.Count < 3) continue;

                    var hullA = ConvexHull2D(ptsA2D);
                    var hullB = ConvexHull2D(ptsB2D);
                    if (hullA.Count < 3 || hullB.Count < 3) continue;

                    float clipArea = ClipPolygons(hullA, hullB, polyBuf);
                    if (clipArea <= 1e-6f) continue;
                    areaSum[0] += clipArea;

                    var poly = new Vector3[polyBuf.Count];
                    for (int pi = 0; pi < polyBuf.Count; pi++)
                        poly[pi] = planeOrigin + polyBuf[pi].x * tan + polyBuf[pi].y * bitan;
                    polys.Add(poly);
                }
            }
        }
    }

    // PCH-style highlight pass: for each coplanar triangle pair (A, B) within
    // compatCoplanarThreshold, clip A against B in A's tangent plane and record only the
    // actual overlapping region (not the whole face), colored by how far apart the two
    // triangles' planes are. Reuses the same box-extrapolation quick reject as CollectJointPolys.
    void CollectJointFaces(List<MeshFilter> listA, List<MeshFilter> listB, List<JointFaceHighlight> faces)
    {
        var polyBuf = new List<Vector2>(8);
        var ptsA2D  = new List<Vector2>(3);
        var ptsB2D  = new List<Vector2>(3);

        foreach (var mfA in listA)
        {
            if (mfA.sharedMesh == null) continue;
            var meshA  = mfA.sharedMesh;
            var vertsA = meshA.vertices;
            var normsA = meshA.normals;
            var mA     = mfA.transform.localToWorldMatrix;
            var boundsA = TransformBoundsToWorld(mA, meshA.bounds);

            var wVertsA = new Vector3[vertsA.Length];
            var wNormsA = new Vector3[vertsA.Length];
            for (int i = 0; i < vertsA.Length; i++)
            {
                wVertsA[i] = mA.MultiplyPoint3x4(vertsA[i]);
                wNormsA[i] = mA.MultiplyVector(normsA.Length > 0 ? normsA[i] : Vector3.up).normalized;
            }

            foreach (var mfB in listB)
            {
                if (mfB.sharedMesh == null) continue;
                var meshB  = mfB.sharedMesh;
                var boundsB = TransformBoundsToWorld(mfB.transform.localToWorldMatrix, meshB.bounds);
                // Same box-extrapolation quick reject as CollectJointPolys — inflate A's box
                // by the coplanar threshold ("extrapolate the plane into a box") before testing.
                var expandedA = new Bounds(boundsA.center, boundsA.size + Vector3.one * compatCoplanarThreshold * 2f);
                if (!expandedA.Intersects(boundsB)) continue;
                var vertsB = meshB.vertices;
                var mB     = mfB.transform.localToWorldMatrix;

                var wVertsB = new Vector3[vertsB.Length];
                for (int i = 0; i < vertsB.Length; i++)
                    wVertsB[i] = mB.MultiplyPoint3x4(vertsB[i]);

                var trisA = meshA.triangles;
                var trisB = meshB.triangles;
                for (int ia = 0; ia < trisA.Length; ia += 3)
                {
                    Vector3 wa0 = wVertsA[trisA[ia]], wa1 = wVertsA[trisA[ia+1]], wa2 = wVertsA[trisA[ia+2]];
                    Vector3 geomNorm = Vector3.Cross(wa1 - wa0, wa2 - wa0);
                    if (geomNorm.sqrMagnitude < 1e-10f) continue;
                    Vector3 faceNorm    = ((wNormsA[trisA[ia]] + wNormsA[trisA[ia+1]] + wNormsA[trisA[ia+2]]) / 3f).normalized;
                    Vector3 planeOrigin = (wa0 + wa1 + wa2) / 3f;

                    // Triangle must itself be (near-)coplanar — all three verts within threshold
                    // of their own average plane — otherwise it's not a flat mating face.
                    if (Mathf.Abs(Vector3.Dot(wa0 - planeOrigin, faceNorm)) > compatCoplanarThreshold) continue;
                    if (Mathf.Abs(Vector3.Dot(wa1 - planeOrigin, faceNorm)) > compatCoplanarThreshold) continue;
                    if (Mathf.Abs(Vector3.Dot(wa2 - planeOrigin, faceNorm)) > compatCoplanarThreshold) continue;

                    Vector3 tan   = Vector3.Cross(faceNorm, Mathf.Abs(faceNorm.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
                    Vector3 bitan = Vector3.Cross(faceNorm, tan).normalized;

                    ptsA2D.Clear();
                    ptsA2D.Add(new Vector2(Vector3.Dot(wa0 - planeOrigin, tan), Vector3.Dot(wa0 - planeOrigin, bitan)));
                    ptsA2D.Add(new Vector2(Vector3.Dot(wa1 - planeOrigin, tan), Vector3.Dot(wa1 - planeOrigin, bitan)));
                    ptsA2D.Add(new Vector2(Vector3.Dot(wa2 - planeOrigin, tan), Vector3.Dot(wa2 - planeOrigin, bitan)));
                    // ClipPolygons (Sutherland-Hodgman) requires the clip polygon to be CCW —
                    // tan/bitan handedness doesn't track the triangle's own winding, so force it.
                    if (SignedArea2D(ptsA2D) < 0f) ptsA2D.Reverse();

                    for (int ib = 0; ib < trisB.Length; ib += 3)
                    {
                        Vector3 wb0 = wVertsB[trisB[ib]], wb1 = wVertsB[trisB[ib+1]], wb2 = wVertsB[trisB[ib+2]];

                        float sd0 = Vector3.Dot(wb0 - planeOrigin, faceNorm);
                        float sd1 = Vector3.Dot(wb1 - planeOrigin, faceNorm);
                        float sd2 = Vector3.Dot(wb2 - planeOrigin, faceNorm);
                        // Only require the triangle's average plane to be within threshold —
                        // matching CollectJointPolys's per-vertex gather, requiring all three
                        // verts individually within threshold is too strict and misses most
                        // real mating triangles whose plane is close but not vertex-perfect.
                        float sdAvg = (sd0 + sd1 + sd2) / 3f;
                        if (Mathf.Abs(sdAvg) > compatCoplanarThreshold) continue;

                        ptsB2D.Clear();
                        ptsB2D.Add(new Vector2(Vector3.Dot(wb0 - planeOrigin, tan), Vector3.Dot(wb0 - planeOrigin, bitan)));
                        ptsB2D.Add(new Vector2(Vector3.Dot(wb1 - planeOrigin, tan), Vector3.Dot(wb1 - planeOrigin, bitan)));
                        ptsB2D.Add(new Vector2(Vector3.Dot(wb2 - planeOrigin, tan), Vector3.Dot(wb2 - planeOrigin, bitan)));

                        if (ClipPolygons(ptsA2D, ptsB2D, polyBuf) <= 1e-9f) continue;

                        var poly = new Vector3[polyBuf.Count];
                        for (int pi = 0; pi < polyBuf.Count; pi++)
                            poly[pi] = planeOrigin + polyBuf[pi].x * tan + polyBuf[pi].y * bitan;

                        faces.Add(new JointFaceHighlight
                        {
                            poly = poly,
                            // Signed plane separation: positive = gap, negative = penetration.
                            // Coloring bands on |planeDist| against compatCollisionThreshold.
                            planeDist = sdAvg
                        });
                    }
                }
            }
        }
    }

    // Gift-wrapping convex hull of 2D points. Returns hull in CCW order.
    static List<Vector2> ConvexHull2D(List<Vector2> pts)
    {
        int n = pts.Count;
        if (n < 3) return new List<Vector2>(pts);
        // Find leftmost point
        int start = 0;
        for (int i = 1; i < n; i++)
            if (pts[i].x < pts[start].x) start = i;

        var hull = new List<Vector2>();
        int cur = start;
        do
        {
            hull.Add(pts[cur]);
            int next = (cur + 1) % n;
            for (int i = 0; i < n; i++)
                if (Cross2D(pts[next] - pts[cur], pts[i] - pts[cur]) < 0)
                    next = i;
            cur = next;
        } while (cur != start && hull.Count <= n);
        return hull;
    }

    // Sutherland-Hodgman: clip subject polygon against clip polygon, return area.
    // Result polygon left in buf.
    static float ClipPolygons(List<Vector2> clip, List<Vector2> subject, List<Vector2> buf)
    {
        buf.Clear();
        buf.AddRange(subject);
        var input = new List<Vector2>(buf.Count + 4);

        for (int e = 0; e < clip.Count; e++)
        {
            if (buf.Count == 0) return 0f;
            Vector2 eA = clip[e], eB = clip[(e + 1) % clip.Count];
            input.Clear();
            input.AddRange(buf);
            buf.Clear();
            for (int i = 0; i < input.Count; i++)
            {
                Vector2 cur  = input[i];
                Vector2 prev = input[(i + input.Count - 1) % input.Count];
                bool curIn  = Cross2D(eB - eA, cur  - eA) >= 0f;
                bool prevIn = Cross2D(eB - eA, prev - eA) >= 0f;
                if (curIn)  { if (!prevIn) buf.Add(LineIntersect2D(prev, cur, eA, eB)); buf.Add(cur); }
                else if (prevIn) buf.Add(LineIntersect2D(prev, cur, eA, eB));
            }
        }
        if (buf.Count < 3) return 0f;
        float area = 0f;
        for (int i = 0; i < buf.Count; i++)
        { Vector2 p = buf[i], q = buf[(i + 1) % buf.Count]; area += p.x * q.y - q.x * p.y; }
        return Mathf.Abs(area) * 0.5f;
    }

    // Returns the area of the intersection polygon of two 2D triangles.
    // Uses Sutherland-Hodgman: clip subject (B) against each edge of clip (A).
    // Returns penetration depth of point p inside mesh (0 if outside).
    // Casts a ray in +X and counts triangle intersections (odd = inside).
    // Depth = distance to the nearest intersected face along the ray.
    // Returns the true penetration depth: minimum exit distance across all 6 cardinal
    // directions. Using just +X gives the wrong answer when parts overlap along Y or Z.
    static float PointMeshDepth(Vector3 p, Vector3[] wVerts, int[] tris)
    {
        float minDepth = float.MaxValue;
        foreach (var dir in new[] {
            Vector3.right, Vector3.left,
            Vector3.up,    Vector3.down,
            Vector3.forward, Vector3.back })
        {
            int hits = 0;
            float nearest = float.MaxValue;
            for (int i = 0; i < tris.Length; i += 3)
            {
                if (RayTriangle(p, dir,
                    wVerts[tris[i]], wVerts[tris[i+1]], wVerts[tris[i+2]],
                    out float t, out _, out _) && t >= 0f)
                {
                    hits++;
                    if (t < nearest) nearest = t;
                }
            }
            if (hits % 2 == 1 && nearest < minDepth)
                minDepth = nearest;
        }
        return minDepth == float.MaxValue ? 0f : minDepth;
    }

    static float Cross2D(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    static float SignedArea2D(List<Vector2> poly)
    {
        float area = 0f;
        for (int i = 0; i < poly.Count; i++)
        { Vector2 p = poly[i], q = poly[(i + 1) % poly.Count]; area += p.x * q.y - q.x * p.y; }
        return area * 0.5f;
    }

    static Vector2 LineIntersect2D(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        Vector2 d1 = p2 - p1, d2 = p4 - p3;
        float denom = Cross2D(d1, d2);
        if (Mathf.Abs(denom) < 1e-10f) return (p1 + p2) * 0.5f;
        float t = Cross2D(p3 - p1, d2) / denom;
        return p1 + t * d1;
    }

    // Max sub-triangle edge length when subdividing candidate collision triangles — bounds
    // how closely the highlighted area can hug the true (unknown, no 3D boolean available)
    // overlap boundary, and bounds the subdivision cost (4x tris per halving).
    const float kCollisionSubdivEdge = 0.02f;
    const int   kCollisionSubdivMaxDepth = 5;

    // For each SP pair across the two sides whose bounds intersect, collect subdivided
    // sub-triangles from A whose centroid is inside B's mesh beyond Collision Threshold
    // (and vice versa) — approximates the true overlap boundary without a full 3D mesh
    // boolean (Unity has no built-in equivalent to Blender's Boolean INTERSECT solver).
    List<CollisionSubTri> FindCollisionTris(GameObject[] sel)
    {
        var result = new List<CollisionSubTri>();
        var sidesFilters = sel.Select(go => CollectSPMeshFilters(go)).ToList();
        if (sidesFilters.Count < 2) return result;

        foreach (var (mfA, _) in sidesFilters[0])
        {
            if (mfA.sharedMesh == null) continue;
            var meshA  = mfA.sharedMesh;
            var vertsA = meshA.vertices;
            var trisA  = meshA.triangles;
            var mA     = mfA.transform.localToWorldMatrix;
            var boundsA = TransformBoundsToWorld(mA, meshA.bounds);

            foreach (var (mfB, _) in sidesFilters[1])
            {
                if (mfB.sharedMesh == null) continue;
                var meshB  = mfB.sharedMesh;
                var vertsB = meshB.vertices;
                var trisB  = meshB.triangles;
                var mB     = mfB.transform.localToWorldMatrix;
                var boundsB = TransformBoundsToWorld(mB, meshB.bounds);

                if (!boundsA.Intersects(boundsB)) continue;

                // Pre-transform B and A verts for ray-cast inside test
                var wVertsB = new Vector3[vertsB.Length];
                for (int i = 0; i < vertsB.Length; i++) wVertsB[i] = mB.MultiplyPoint3x4(vertsB[i]);
                var wVertsA = new Vector3[vertsA.Length];
                for (int i = 0; i < vertsA.Length; i++) wVertsA[i] = mA.MultiplyPoint3x4(vertsA[i]);

                for (int i = 0; i < trisA.Length; i += 3)
                {
                    Vector3 w0 = wVertsA[trisA[i]], w1 = wVertsA[trisA[i+1]], w2 = wVertsA[trisA[i+2]];
                    Vector3 c = (w0 + w1 + w2) / 3f;
                    if (!boundsB.Contains(c)) continue;
                    if (PointMeshDepth(c, wVertsB, trisB) > compatCollisionThreshold)
                        SubdivideAndCullTri(w0, w1, w2, wVertsB, trisB, 0, result);
                }

                for (int i = 0; i < trisB.Length; i += 3)
                {
                    Vector3 w0 = wVertsB[trisB[i]], w1 = wVertsB[trisB[i+1]], w2 = wVertsB[trisB[i+2]];
                    Vector3 c = (w0 + w1 + w2) / 3f;
                    if (!boundsA.Contains(c)) continue;
                    if (PointMeshDepth(c, wVertsA, trisA) > compatCollisionThreshold)
                        SubdivideAndCullTri(w0, w1, w2, wVertsA, trisA, 0, result);
                }
            }
        }
        return result;
    }

    // Recursively splits a triangle at its edge midpoints (4-way subdivision) until edges are
    // below kCollisionSubdivEdge or max depth is reached, keeping only leaf sub-triangles whose
    // centroid still tests as inside otherWVerts/otherTris beyond Collision Threshold — this
    // trims the highlighted area down toward the true overlap boundary.
    static void SubdivideAndCullTri(Vector3 v0, Vector3 v1, Vector3 v2,
        Vector3[] otherWVerts, int[] otherTris, int depth, List<CollisionSubTri> result)
    {
        float maxEdge = Mathf.Max((v1 - v0).magnitude, (v2 - v1).magnitude, (v0 - v2).magnitude);
        if (depth >= kCollisionSubdivMaxDepth || maxEdge <= kCollisionSubdivEdge)
        {
            Vector3 c = (v0 + v1 + v2) / 3f;
            float d = PointMeshDepth(c, otherWVerts, otherTris);
            if (d > 0f) result.Add(new CollisionSubTri { v0 = v0, v1 = v1, v2 = v2, depth = d });
            return;
        }

        Vector3 m01 = (v0 + v1) * 0.5f, m12 = (v1 + v2) * 0.5f, m20 = (v2 + v0) * 0.5f;
        SubdivideAndCullTri(v0, m01, m20, otherWVerts, otherTris, depth + 1, result);
        SubdivideAndCullTri(m01, v1, m12, otherWVerts, otherTris, depth + 1, result);
        SubdivideAndCullTri(m20, m12, v2, otherWVerts, otherTris, depth + 1, result);
        SubdivideAndCullTri(m01, m12, m20, otherWVerts, otherTris, depth + 1, result);
    }

    // Returns (MeshFilter, ownerGO) pairs. ownerGO is the AddressableLoader GO for fake-child
    // MeshFilters so GetJsaName can walk the correct scene hierarchy for GUID lookup.
    static List<(MeshFilter mf, GameObject owner)> CollectSPMeshFilters(GameObject go)
    {
        var result  = new List<(MeshFilter, GameObject)>();
        var claimed = new HashSet<MeshFilter>();

        // Fake hierarchy first so loader-owned MFs get the correct owner (AddressableLoader GO).
        // The direct StructurePart scan below would otherwise claim them with mf.gameObject,
        // causing GetJsaName to miss the AddressableLoader walk-up (Path 2).
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
                    if (mf != null && mf.sharedMesh != null && claimed.Add(mf))
                        result.Add((mf, loader.gameObject));
                }
                foreach (var fsp in ch.GetComponentsInChildren<FakeStructurePart>(true))
                {
                    var mf = fsp.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null && claimed.Add(mf))
                        result.Add((mf, loader.gameObject));
                }
            }
        }

        // Direct (non-loader-owned) StructureParts and FakeStructureParts
        foreach (var sp in go.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true))
        {
            var mf = sp.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && claimed.Add(mf))
                result.Add((mf, mf.gameObject));
        }
        foreach (var fsp in go.GetComponentsInChildren<FakeStructurePart>(true))
        {
            var mf = fsp.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && claimed.Add(mf))
                result.Add((mf, mf.gameObject));
        }

        return result;
    }

    void DrawCompatResult(CompatResult r)
    {
        if (r.state == CompatResult.State.None) return;
        if (r.state == CompatResult.State.Pass)
        {
            DrawCompatRow("d_winbtn_mac_max", r.message);
        }
        else if (r.state == CompatResult.State.Warn)
            DrawCompatRow("console.infoicon.sml", r.message);
        else
            DrawCompatRow("d_winbtn_mac_close_h", r.message);
    }

    void DrawCompatMeshResult(CompatResult r, bool jsaMjcBothFail)
    {
        if (r.state == CompatResult.State.None) return;
        if (r.state == CompatResult.State.Pass)
            DrawCompatRow("d_winbtn_mac_max", r.message);
        else if (r.state == CompatResult.State.Fail)
            DrawCompatRow("d_winbtn_mac_close_h", r.message);
        else
            DrawCompatRow("console.warnicon.sml", r.message);
    }

    void DrawCompatRow(string iconName, string message)
    {
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label(EditorGUIUtility.IconContent(iconName), GUILayout.Width(20), GUILayout.Height(20));
        var style = new GUIStyle(EditorStyles.helpBox) { richText = true };
        string msg = message
            .Replace("Compatible",               "<color=#44cc44>Compatible</color>")
            .Replace("Will Auto-Joint",          "<color=#44cc44>Will Auto-Joint</color>")
            .Replace("covers all selected parts","<color=#44cc44>covers all selected parts</color>")
            .Replace("is addressable (async)",   "<color=#cc4444>is addressable (async)</color>");
        EditorGUILayout.LabelField(msg, style);
        EditorGUILayout.EndHorizontal();
    }


    void DrawPickedFaceHighlight(PickedFace? face, Color outline, Color fill)
    {
        if (!face.HasValue || face.Value.source == null) return;
        var f = face.Value;
        // Recompute current world normal from stored local normal
        Vector3 wn = f.source.transform.localToWorldMatrix.MultiplyVector(f.localNormal).normalized;
        const float normalTol = 0.15f;
        const float distTol   = 0.01f;

        foreach (var mf in f.source.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            var mesh    = mf.sharedMesh;
            var tris    = mesh.triangles;
            var verts   = mesh.vertices;
            var normals = mesh.normals;
            var m       = mf.transform.localToWorldMatrix;

            for (int ti = 0; ti < tris.Length; ti += 3)
            {
                Vector3 lv0 = verts[tris[ti]], lv1 = verts[tris[ti+1]], lv2 = verts[tris[ti+2]];
                Vector3 ln  = normals.Length > 0
                    ? ((normals[tris[ti]] + normals[tris[ti+1]] + normals[tris[ti+2]]) / 3f).normalized
                    : Vector3.Cross(lv1 - lv0, lv2 - lv0).normalized;
                Vector3 triWn = m.MultiplyVector(ln).normalized;
                if (Vector3.Dot(triWn, wn) < 1f - normalTol) continue;

                Vector3 wv0 = m.MultiplyPoint3x4(lv0);
                Vector3 wv1 = m.MultiplyPoint3x4(lv1);
                Vector3 wv2 = m.MultiplyPoint3x4(lv2);
                Vector3 center = (wv0 + wv1 + wv2) / 3f;
                if (Mathf.Abs(Vector3.Dot(center - f.point, wn)) > distTol) continue;

                Handles.color = fill;
                Handles.DrawAAConvexPolygon(wv0, wv1, wv2);
                Handles.color = outline;
                Handles.DrawLine(wv0, wv1);
                Handles.DrawLine(wv1, wv2);
                Handles.DrawLine(wv2, wv0);
            }
        }
    }

    // ── Scene picking ─────────────────────────────────────────────────────────

    void OnSceneGUI(SceneView sv)
    {
        // Draw joint overlap highlights (PCH-style) — only the actual overlapping region
        // between coplanar triangle pairs, banded by |plane separation| (gap or penetration)
        // against the Collision Threshold: green within it, yellow within 2x, red beyond.
        if (showOverlay && jointFaces != null)
        {
            var prevColor = Handles.color;
            foreach (var f in jointFaces)
            {
                if (f.poly == null || f.poly.Length < 3) continue;
                float absDist = Mathf.Abs(f.planeDist);
                Color c = absDist <= compatCollisionThreshold      ? new Color(0.2f, 1f, 0.4f)
                        : absDist <= compatCollisionThreshold * 2f ? new Color(1f, 0.85f, 0.2f)
                                                                    : new Color(1f, 0.2f, 0.2f);
                Handles.color = new Color(c.r, c.g, c.b, 0.35f);
                Handles.DrawAAConvexPolygon(f.poly);
                Handles.color = new Color(c.r, c.g, c.b, 1f);
                for (int i = 0; i < f.poly.Length; i++)
                    Handles.DrawLine(f.poly[i], f.poly[(i + 1) % f.poly.Length]);
            }
            Handles.color = prevColor;
        }

        // Draw collision sub-triangles — subdivided-and-culled interpenetration region,
        // banded by penetration depth against Collision Threshold: green/yellow/red.
        if (showOverlay && collisionTris != null && collisionTris.Count > 0)
        {
            var prevColor = Handles.color;
            foreach (var tri in collisionTris)
            {
                Color c = tri.depth <= compatCollisionThreshold      ? new Color(0.2f, 1f, 0.4f)
                        : tri.depth <= compatCollisionThreshold * 2f ? new Color(1f, 0.85f, 0.2f)
                                                                      : new Color(1f, 0.2f, 0.2f);
                Handles.color = new Color(c.r, c.g, c.b, 0.4f);
                Handles.DrawAAConvexPolygon(tri.v0, tri.v1, tri.v2);
                Handles.color = new Color(c.r, c.g, c.b, 0.8f);
                Handles.DrawLine(tri.v0, tri.v1);
                Handles.DrawLine(tri.v1, tri.v2);
                Handles.DrawLine(tri.v2, tri.v0);
            }
            Handles.color = prevColor;
        }

        // Draw picked face highlights and crosshair dots
        var prevC = Handles.color;
        DrawPickedFaceHighlight(snapFaceA, kFaceColorA, kFaceColorAFill);
        DrawPickedFaceHighlight(snapFaceB, kFaceColorB, kFaceColorBFill);
        Handles.color = new Color(0.6f, 0.6f, 0.6f, 1f);
        if (snapHitAValid && snapFaceA.HasValue)
        {
            Vector3 dotA = snapPointModeA ? GetFacePoint(snapFaceA.Value, true) : snapHitA;
            Handles.DrawSolidDisc(dotA, sv.camera.transform.forward, HandleUtility.GetHandleSize(dotA) * 0.04f);
        }
        if (snapHitBValid && snapFaceB.HasValue)
        {
            Vector3 dotB = snapPointModeB ? GetFacePoint(snapFaceB.Value, true) : snapHitB;
            Handles.DrawSolidDisc(dotB, sv.camera.transform.forward, HandleUtility.GetHandleSize(dotB) * 0.04f);
        }
        Handles.color = prevC;

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
                    if (slot == 1) { snapFaceA = pf; snapHitA = hitPoint; snapHitAValid = true; }
                    else           { snapFaceB = pf; snapHitB = hitPoint; snapHitBValid = true; }
                    ResetAncestorDepth();
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

    // Walk exactly maxAncestors steps up from partA (0 = the source itself, clamped at root).
    static Transform FindMoveRoot(GameObject partA, GameObject partBSource, int maxAncestors)
    {
        Transform a = partA.transform;
        for (int i = 0; i < maxAncestors && a.parent != null; i++)
            a = a.parent;
        return a;
    }

    // Compute the auto-sibling depth: hops up from partA until its parent is shared with partB.
    static int AutoSiblingDepth(GameObject partA, GameObject partBSource)
    {
        if (partBSource == null) return 1;
        var bAncestors = new HashSet<Transform>();
        for (var t = partBSource.transform; t != null; t = t.parent)
            bAncestors.Add(t);
        int depth = 0;
        Transform cur = partA.transform;
        while (cur.parent != null && !bAncestors.Contains(cur.parent))
        {
            cur = cur.parent;
            depth++;
        }
        return Mathf.Max(0, depth);
    }

    // Call whenever both faces are freshly set — resets the ancestor slider to the auto-sibling ceiling.
    void ResetAncestorDepth()
    {
        if (snapFaceA.HasValue && snapFaceA.Value.source != null &&
            snapFaceB.HasValue && snapFaceB.Value.source != null)
        {
            snapAncestorsUpACeiling = AutoSiblingDepth(snapFaceA.Value.source, snapFaceB.Value.source);
            snapAncestorsUpA = snapAncestorsUpACeiling;
        }
    }

    string TruncateButtonText(string ancestorName, string faceName, float fontSizeScale)
    {
        // Left column (68px) + scrollbar (~15px) + window/scroll margins (~20px) = ~103px consumed.
        float availableWidth = EditorGUIUtility.currentViewWidth - 103f;
        availableWidth = Mathf.Max(availableWidth, 40f);

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
        ResetAncestorDepth();

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

        Transform moveRoot = FindMoveRoot(fA.source, fB.source, snapAncestorsUpA);
        Undo.RecordObject(moveRoot, "Face Snap");

        // Compute current face normals from local normals (accounts for rotation)
        Vector3 currentNormalA = fA.source.transform.localToWorldMatrix.MultiplyVector(fA.localNormal).normalized;
        Vector3 currentNormalB = fB.source.transform.localToWorldMatrix.MultiplyVector(fB.localNormal).normalized;

        // For Part A: if in Center of Face mode, recompute center with current normal + coplanar filter.
        // If in Click Point mode, the clicked point is fixed in local space of the source.
        Vector3 ptA;
        if (snapPointModeA)
        {
            // Use a temporary face with the current world normal so GetFacePoint picks the right triangles
            var fACurrent = new PickedFace { point = fA.point, normal = currentNormalA, source = fA.source, localNormal = fA.localNormal };
            ptA = GetFacePoint(fACurrent, true);
        }
        else
        {
            ptA = fA.point;
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

        bool anyScale = snapScaleX || snapScaleY || snapScaleZ;

        if (anyScale)
        {
            // Scale snap is measured BEFORE translation so the span reflects the object's
            // current size, not the distance to face B.
            // Apply rotation first so bounds are measured in the post-rotation orientation.
            moveRoot.rotation = newRot;
            moveRoot.position = newPos; // rotation pivot position only, no translation yet

            Bounds rootBounds = GetBounds(moveRoot.gameObject);

            // Face A normal in world space after rotation is applied.
            Vector3 faceANormalWorld = fA.source.transform.localToWorldMatrix.MultiplyVector(fA.localNormal).normalized;

            // Current extent of the object along the face normal.
            float faceExtent   = ReachInDir(rootBounds, faceANormalWorld);
            float faceCenter1D = Vector3.Dot(rootBounds.center, faceANormalWorld);
            float currentFacePos = faceCenter1D + faceExtent; // face-A side world position
            float backFacePos    = faceCenter1D - faceExtent; // opposite (stationary) side

            // Where face A needs to end up.
            float targetFacePos = Vector3.Dot(ptB + fB.normal * overlap, faceANormalWorld);

            // Required span from back face to target face.
            float currentSpan = currentFacePos - backFacePos;
            float targetSpan  = targetFacePos  - backFacePos;

            if (Mathf.Abs(currentSpan) > 1e-5f && Mathf.Abs(targetSpan) > 1e-5f)
            {
                float scaleFactor = targetSpan / currentSpan;

                // Map the world face normal to local space to find which local axis to scale.
                Vector3 localSnapDir = moveRoot.worldToLocalMatrix.MultiplyVector(faceANormalWorld).normalized;

                Vector3 newScale = moveRoot.localScale;
                bool scaledAny = false;
                if (snapScaleX && Mathf.Abs(localSnapDir.x) > 0.5f) { newScale.x = moveRoot.localScale.x * scaleFactor; scaledAny = true; }
                if (snapScaleY && Mathf.Abs(localSnapDir.y) > 0.5f) { newScale.y = moveRoot.localScale.y * scaleFactor; scaledAny = true; }
                if (snapScaleZ && Mathf.Abs(localSnapDir.z) > 0.5f) { newScale.z = moveRoot.localScale.z * scaleFactor; scaledAny = true; }

                if (scaledAny)
                {
                    moveRoot.localScale = newScale;

                    // Unity scales around the transform pivot. The back face was at offset
                    // (backFacePos - pivotPos1D) from the pivot; after scaling that offset
                    // grows by scaleFactor. Correct position so the back face stays put.
                    float pivotPos1D      = Vector3.Dot(moveRoot.position, faceANormalWorld);
                    float backOffset      = backFacePos - pivotPos1D;
                    float backAfterScale  = pivotPos1D + backOffset * scaleFactor;
                    float correction1D    = backFacePos - backAfterScale;
                    moveRoot.position    += faceANormalWorld * correction1D;
                }
            }
        }
        else
        {
            // Pure position/rotation snap — apply rotation then translate.
            Vector3 targetPos = ptB + fB.normal * overlap;
            Vector3 delta     = targetPos - ptA;
            if (!snapPosX) delta.x = 0;
            if (!snapPosY) delta.y = 0;
            if (!snapPosZ) delta.z = 0;
            newPos += delta;

            moveRoot.rotation = newRot;
            moveRoot.position = newPos;
        }

        statusMessage = $"Snapped '{moveRoot.name}' to face on '{(fB.source != null ? fB.source.name : "?")}' ({overlap * 100f:F1} cm {(overlap > 0 ? "gap" : overlap < 0 ? "overlap" : "flush")}).";
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
            var bounds = new List<Bounds>();
            foreach (var fsp in go.GetComponentsInChildren<FakeStructurePart>(true))
                bounds.Add(TransformBoundsToWorld(fsp.transform.localToWorldMatrix, fsp.localColliderBounds));
            foreach (var sp in go.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true))
            {
                Bounds b;
                if (sp.TryGetComponent<MeshCollider>(out var mc) && mc.sharedMesh != null)
                    b = TransformBoundsToWorld(sp.transform.localToWorldMatrix, mc.sharedMesh.bounds);
                else if (sp.TryGetComponent<BoxCollider>(out var bc))
                    b = TransformBoundsToWorld(sp.transform.localToWorldMatrix, new Bounds(bc.center, bc.size));
                else continue;
                bounds.Add(b);
            }
            if (bounds.Count > 0) result.Add(bounds);
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
