using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEditor;

public class JointAssistWindow : EditorWindow
{
    // Cut point state — the assigned prefab can be EITHER a real baked local prefab (a real
    // StructurePart baked in directly — instantiated as-is) OR a pure-addressable wrapper prefab
    // (its root/descendant carries an AddressableLoader with an assetGUID — placement then creates
    // a fresh AddressableLoader node with that same GUID, resolved by the game's own Addressables
    // system at runtime, so the result is genuinely interactable in-game). A plain FakeStructurePart
    // prefab (the earlier approach) is only an editor-preview stand-in for a real StructurePart
    // living inside addressable content (see Assets/Scripts/EditorFakes/FakeStructurePart.cs +
    // AddressableRendering.cs) — it renders visually but is never actually interactable, since
    // there's no real StructurePart behind it. Assigning an addressable-wrapper prefab here (rather
    // than typing a raw GUID) avoids that trap while still letting you pick by name in the object
    // picker. Same underlying placement pattern as ImportGamePartWizard.DoPlaceLocal.
    GameObject cutPointPrefab;
    bool pickingCutPoint;

    // Face snap state — each face stores the picked point, normal (in local space of source), and source object
    struct PickedFace { public Vector3 point; public Vector3 normal; public GameObject source; public Vector3 localNormal; }
    PickedFace? snapFaceA;
    PickedFace? snapFaceB;
    int pickingSnapFace; // 0 = none, 1 = A, 2 = B
    float overlapAmount = 0f;

    // Which geometry Face Snap picks/aligns against: the render mesh (MeshFilter.sharedMesh, what
    // the game's jointing algorithm actually reads — see [[project_game_jointing_algorithm]]) or
    // the live MeshCollider (a real PhysX query via Collider.Raycast/bounds — not the game's
    // jointing geometry, but useful as a physical-collision proxy when picking faces).
    bool snapUseColliderHull = false;

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
    float compatCoplanarThreshold     = 0.025f; // mirrors game's coplanarDistanceThreshold
    // Game uses TWO different threshold pairs at different call sites (decompiled
    // BBI.Unity.Game): JointingService.JointStructureParts (general/runtime, player
    // cutting/placing/grappling) uses 0.1 coplanar / 0.8 codirectional; ShipRandomizationHelper's
    // JointPartsAsync (ship-spawn/generation) uses a STRICTER 0.05 / 0.9. This was previously
    // hardcoded (kCodirectionalDotThresholdRuntime = 0.8f, no UI control at all) — now exposed so
    // both halves of either pair can actually be tested together against real in-game behavior.
    float compatCodirectionalThreshold = 0.8f;

    struct CompatResult
    {
        public enum State { None, Pass, Warn, Fail }
        public State state;
        public string message;
    }
    CompatResult compatSPMat  = new CompatResult { state = CompatResult.State.None };
    CompatResult compatMJC    = new CompatResult { state = CompatResult.State.None };
    CompatResult compatHull   = new CompatResult { state = CompatResult.State.None };

    // Master visibility toggle for the scene-view overlay. Set true automatically after a Check
    // that finds anything to show; can be toggled off/on manually via the "Hide/Show Overlay"
    // button without re-running Check.
    bool showOverlay = false;

    // Joint overlap highlight (scene-view only) — the merged coplanar overlap polygon per
    // MeshFilter pair, computed by TryFindJointPolygon (a port of the game's actual jointing
    // algorithm — see [[project_game_jointing_algorithm]]). planeDist is currently unused
    // (reserved for a future gap/depth color-band, same idea the old per-triangle version had).
    struct JointFaceHighlight { public Vector3[] poly; public float planeDist; }
    List<JointFaceHighlight> jointFaces;

    // Near-miss diagnostic highlight (scene-view only) — shown only when NO pair on the whole
    // selection actually joints, to help the user see the SINGLE closest candidate pair overall
    // (whichever of the two failure axes it's closest on) and by how much. Never shown for a
    // pair that already joints (jointFaces takes priority); does not influence the pass/fail
    // Mesh result at all — purely informational. Both triangles (A's and B's) are highlighted
    // together in the same color, since they're one real candidate pair, not two statistics.
    struct NearMissHighlight
    {
        public NearMissAxis axis; // Distance/Angle = two faces; PointPair = one arrow along the separation axis
        public Vector3 triA0, triA1, triA2, triB0, triB1, triB2; // Distance/Angle only
        public Vector3 pointA, pointB;                           // PointPair only
        public float measuredValue, thresholdValue;
    }
    NearMissHighlight? nearMissHighlight;

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
        StopAsyncCheck();
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

        snapUseColliderHull = GUILayout.Toolbar(snapUseColliderHull ? 1 : 0,
            new[] { "Mesh Renderer", "Convex Hull" }) == 1;
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

        compatCoplanarThreshold  = EditorGUILayout.FloatField(new GUIContent("Distance Threshold (m)",
            "How far apart two faces' planes can be and still be considered a candidate mating pair. " +
            "Used to gather coplanar joint polygons/faces and to inflate each mesh's bounds for the quick-reject test."),
            compatCoplanarThreshold);
        // Stored internally as a dot-product threshold (what the matching algorithm actually
        // needs), but shown/edited here in degrees — "how far off from exactly opposite the two
        // faces' normals may be" is far more intuitive than a raw cosine value. Game uses ~36.9°
        // (dot 0.8) for general/runtime jointing (player cutting/placing/grappling) and a
        // stricter ~25.8° (dot 0.9) for ship-spawn/generation (ShipRandomizationHelper) — set to
        // match whichever scenario you're checking.
        float codirectionalThresholdDeg = Mathf.Acos(Mathf.Clamp(compatCodirectionalThreshold, -1f, 1f)) * Mathf.Rad2Deg;
        codirectionalThresholdDeg = EditorGUILayout.FloatField(new GUIContent("Angle Threshold (°)",
            "How far off from exactly opposite two faces' normals may be and still be considered a candidate " +
            "mating pair. Game uses ~36.9° for general/runtime jointing (player cutting/placing/grappling) and " +
            "a stricter ~25.8° for ship-spawn/generation (ShipRandomizationHelper) — set to match whichever " +
            "scenario you're checking."),
            codirectionalThresholdDeg);
        compatCodirectionalThreshold = Mathf.Cos(Mathf.Clamp(codirectionalThresholdDeg, 0f, 180f) * Mathf.Deg2Rad);

        int compatSel = Selection.gameObjects.Length;
        EditorGUILayout.BeginHorizontal();
        // No toolbar Cancel button while checkRunning: EditorUtility.DisplayCancelableProgressBar
        // (in StepAsyncCheck) is a MODAL window that captures input focus for as long as the Check
        // is running, so a button drawn here would be behind it and unclickable. The progress
        // bar's own Cancel/X is the only reachable way to cancel — this toolbar just disables
        // itself and shows that the Check is in progress.
        using (new EditorGUI.DisabledScope(compatSel < 2 || checkRunning))
        {
            if (GUILayout.Button(checkRunning ? "Checking..." : $"Check  ({compatSel})", GUILayout.Height(28), GUILayout.MinWidth(0)))
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
        // The Mesh row reports the actual game jointing algorithm's result — coplanar
        // render-mesh triangle-pair search + 2D convex hull + polygon clip, matching
        // BBI.Unity.Game.JointHelper.TryFindJointPolygonsJob (see [[project_game_jointing_algorithm]]).
        DrawCompatMeshResult(compatHull, jsaMjcBothFail);

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
        cutPointPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Cut Point Prefab",
            "A baked local prefab (a real StructurePart — placed as-is) OR a pure-addressable " +
            "wrapper prefab (has an AddressableLoader with an assetGUID somewhere in it — placement " +
            "creates a fresh AddressableLoader node with that same GUID, resolved by the game's own " +
            "Addressables system at runtime, so it's genuinely interactable in-game)."),
            cutPointPrefab, typeof(GameObject), false);
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

    // True while an async Check is running — disables the Check button (shown as Cancel) and
    // gates the progress bar.
    bool checkRunning;
    float checkProgress;
    string checkProgressLabel = "";
    IEnumerator checkEnumerator;
    // Set the instant the modal progress bar's Cancel is clicked, checked inside
    // TryFindJointPolygon's inner scan loop so a slow single MeshFilter pair can be interrupted
    // mid-scan (that method has no yield points of its own — it isn't a coroutine).
    bool checkCanceled;

    // TEMP DEBUG (this session only — investigating overlay tilt, remove after):
    const string kDebugLogPath = "C:/Users/user/source/repos/ShipbreakerShipbuilder/joint_tilt_debug.log";
    static void DebugLog(string s) => System.IO.File.AppendAllText(kDebugLogPath, s + "\n");
    // Raw hullA/hullB (pre-clip), reconstructed to 3D, for visual inspection — are the two
    // hulls actually the same real seam, or two unrelated surfaces flattened onto one plane?
    static List<Vector3> debugHullA3D = new List<Vector3>();
    static List<Vector3> debugHullB3D = new List<Vector3>();

    // GameObjects from the last Check, kept only so a budget-exceeded re-run can restart on the
    // exact same selection without requiring the user to still have it selected in the Hierarchy.
    GameObject[] lastCheckSel;

    void RunCompatibilityCheck() => RunCompatibilityCheck(kDefaultHullPairBudget);

    void RunCompatibilityCheck(int budget)
    {
        var sel = Selection.gameObjects;
        if (sel.Length < 2 || checkRunning) return;
        RunCompatibilityCheckInternal(sel, budget);
    }

    void RunCompatibilityCheckInternal(GameObject[] sel, int budget)
    {
        if (checkRunning) return;
        lastCheckSel = sel;
        hullPairBudgetMax = budget;

        // TEMP DEBUG (this session only): reset the log each Check so it only shows this run.
        System.IO.File.WriteAllText(kDebugLogPath, $"=== Check run {System.DateTime.Now:HH:mm:ss} ===\n");

        // Re-read data files each check so in-game updates are picked up without reopening the window
        enrichedJsaMap = null;
        spMatJsaMap    = null;
        jsaCompatTable = null;
        knownAssetsMap = null;

        // ── #1  SP_Mat / JSA ──────────────────────────────────────────────────
        compatSPMat = CheckSPMatCompat(sel);

        // ── #2  MandatoryJointContainer ───────────────────────────────────────
        compatMJC = CheckMJC(sel);

        // ── #3  Mesh joint polygons — the actual game algorithm, run async/cancelable ────
        // The heavy work (coplanar triangle-pair search across every MeshFilter pair) runs as
        // an IEnumerator driven by EditorApplication.update so the Editor UI stays responsive
        // and the user can cancel mid-run, per this session's plan. Existing overlay/result
        // fields are only swapped in once the run completes normally — a cancel leaves the
        // previously-displayed Check results untouched rather than showing a partial result.
        hullPairBudgetExceeded = false;
        worstCaseRequiredPairs = 0;
        checkCanceled = false;
        checkEnumerator = MeshJointCheckRoutine(sel);
        checkRunning = true;
        checkProgress = 0f;
        checkProgressLabel = "Starting...";
        EditorApplication.update += StepAsyncCheck;
        Repaint();
    }

    void StepAsyncCheck()
    {
        bool cancel = EditorUtility.DisplayCancelableProgressBar("Joint Compatibility Check", checkProgressLabel, checkProgress);
        if (cancel) { checkCanceled = true; StopAsyncCheck(); Repaint(); return; }

        bool more;
        try { more = checkEnumerator.MoveNext(); }
        catch { StopAsyncCheck(); throw; }

        if (!more) StopAsyncCheck();
        Repaint();
    }

    void StopAsyncCheck()
    {
        EditorApplication.update -= StepAsyncCheck;
        EditorUtility.ClearProgressBar();
        checkRunning = false;
        checkEnumerator = null;

        // Triangle-pair budget was exhausted on at least one MeshFilter pair — the Mesh result may
        // be a false negative. Rather than exposing the budget as a permanent UI knob, offer to
        // just re-run with exactly the budget that pair needed (plus headroom), on confirmation.
        if (hullPairBudgetExceeded && worstCaseRequiredPairs > hullPairBudgetMax)
        {
            long suggested = worstCaseRequiredPairs + worstCaseRequiredPairs / 10; // +10% headroom
            bool rerun = EditorUtility.DisplayDialog("Joint Compatibility Check",
                $"The triangle-pair budget ({hullPairBudgetMax:N0}) was exceeded on at least one part pair — " +
                $"that pair's result may be a false negative. It needs at least {worstCaseRequiredPairs:N0} pairs " +
                $"to fully scan.\n\nRe-run the Check with a budget of {suggested:N0}?",
                "Re-run", "Cancel");
            if (rerun && lastCheckSel != null)
            {
                int newBudget = suggested > int.MaxValue ? int.MaxValue : (int)suggested;
                EditorApplication.delayCall += () => RunCompatibilityCheckInternal(lastCheckSel, newBudget);
            }
        }
    }

    // Combined port of the game's TryFindJointPolygonsJob (see [[project_game_jointing_algorithm]])
    // across every MeshFilter pair on each side — computes both the "Collider" summary result
    // and the scene-view overlay polygons in one pass, since they now share the same
    // TryFindJointPolygon data (unlike the old design, which ran CollectJointFacesHull twice more
    // for the overlay on top of a separate Physics.ComputePenetration pass). Yields periodically
    // so StepAsyncCheck/EditorApplication.update can keep the Editor UI responsive and cancelable.
    IEnumerator MeshJointCheckRoutine(GameObject[] sel)
    {
        var newJointFaces = new List<JointFaceHighlight>();
        var sidesFilters = sel.Select(go => CollectSPMeshFilters(go)).ToList();

        if (sidesFilters.Count < 2 || sidesFilters[0].Count == 0 || sidesFilters[1].Count == 0)
        {
            compatHull = new CompatResult { state = CompatResult.State.Warn,
                message = "Mesh: No StructurePart MeshFilters found on one or both sides." };
            ApplyCheckResults(newJointFaces);
            yield break;
        }

        var mfsA = sidesFilters[0].Select(p => p.mf).ToList();
        var mfsB = sidesFilters[1].Select(p => p.mf).ToList();

        float minGap = float.MaxValue;
        int overlapCount = 0;
        float areaSum = 0f;
        var overlapPoly = new List<Vector2>();

        // Track the SINGLE closest near-miss seen across the WHOLE Check (comparing both axes on
        // a common relative-overshoot footing, same as inside TryFindJointPolygon) — only ever
        // shown to the user if the Check ends with zero real joints found (see
        // ApplyCheckResults). Read-only diagnostics; never affects overlapCount/pass-fail.
        float bestMissOvershoot = float.MaxValue;
        NearMissHighlight? bestNearMiss = null;

        int totalPairs = mfsA.Count * mfsB.Count;
        int pairIndex = 0;
        int pairsSinceYield = 0;
        const int kPairsPerYield = 25;

        foreach (var mfA in mfsA)
        {
            var boundsA = TransformBoundsToWorld(mfA.transform.localToWorldMatrix, mfA.sharedMesh.bounds);
            float margin = compatCoplanarThreshold * 2f;
            var expandedA = boundsA; expandedA.Expand(margin);

            foreach (var mfB in mfsB)
            {
                pairIndex++;
                // Fallback progress for pairs whose scan is short enough to never hit the
                // per-triangle-pair polling cadence inside TryFindJointPolygon (which overwrites
                // these with real triangle-pair progress once a scan is actually underway).
                checkProgress = totalPairs > 0 ? (float)pairIndex / totalPairs : 1f;
                string pairLabel = $"Mesh pair {pairIndex}/{totalPairs}";
                checkProgressLabel = $"Checking {pairLabel}...";

                var boundsB = TransformBoundsToWorld(mfB.transform.localToWorldMatrix, mfB.sharedMesh.bounds);
                if (!expandedA.Intersects(boundsB)) { continue; }

                if (TryFindJointPolygon(mfA, mfB, out var planeOrigin, out var faceNorm, out var tan, out var bitan, overlapPoly,
                    out var nearMiss, pairLabel))
                {
                    overlapCount++;
                    float area = Mathf.Abs(SignedArea2D(overlapPoly));
                    areaSum += area;

                    var poly = new Vector3[overlapPoly.Count];
                    for (int pi = 0; pi < overlapPoly.Count; pi++)
                        poly[pi] = planeOrigin + overlapPoly[pi].x * tan + overlapPoly[pi].y * bitan;
                    newJointFaces.Add(new JointFaceHighlight { poly = poly, planeDist = 0f });
                }
                else
                {
                    // No coplanar overlap found for this pair — approximate a gap via bounds
                    // distance so a clearly-separated pair still reports something useful.
                    float gap = BoundsDistance(boundsA, boundsB);
                    if (gap < minGap) minGap = gap;

                    // Track the SINGLE closest near-miss across the whole Check (both axes
                    // compared on the same relative-overshoot footing) — purely diagnostic, does
                    // not affect overlapCount/pass-fail.
                    if (nearMiss.found)
                    {
                        float thresholdMag = Mathf.Max(1e-4f, Mathf.Abs(nearMiss.thresholdValue));
                        float overshoot = Mathf.Abs(nearMiss.measuredValue - nearMiss.thresholdValue) / thresholdMag;
                        if (overshoot < bestMissOvershoot)
                        {
                            bestMissOvershoot = overshoot;
                            bestNearMiss = new NearMissHighlight
                            {
                                axis = nearMiss.axis,
                                triA0 = nearMiss.triA0, triA1 = nearMiss.triA1, triA2 = nearMiss.triA2,
                                triB0 = nearMiss.triB0, triB1 = nearMiss.triB1, triB2 = nearMiss.triB2,
                                pointA = nearMiss.pointA, pointB = nearMiss.pointB,
                                measuredValue = nearMiss.measuredValue, thresholdValue = nearMiss.thresholdValue
                            };
                        }
                    }
                }

                // Budget is spent per-pair now (reset inside TryFindJointPolygon each call), so one
                // pair exceeding its budget only makes THAT pair's result partial — it must not
                // abort scanning of the rest of the selection's pairs.
                if (checkCanceled) yield break; // leaves previously-displayed results untouched

                if (++pairsSinceYield >= kPairsPerYield) { pairsSinceYield = 0; yield return null; }
            }
        }

        string polySuffix = newJointFaces.Count > 0
            ? $", {areaSum:0.0000} m² across {newJointFaces.Count} polygon{(newJointFaces.Count == 1 ? "" : "s")}"
            : "";
        if (hullPairBudgetExceeded)
            polySuffix += $" (partial — triangle-pair budget exceeded; needs at least {worstCaseRequiredPairs:N0})";

        // Diagnostic near-miss line, appended into the SAME Mesh message box (a real Pass already
        // has its authoritative answer and doesn't need this). Purely informational — never
        // changes overlapCount/pass-fail. Only ONE line is ever shown — whichever
        // single axis (distance or angle) the closest candidate pair actually missed on — colored
        // to match its scene-view highlight. The codirectional value is a dot product (cosine of
        // the angle between the two face normals), not a raw angle — converted to a degrees-off
        // figure here since "-0.9 dot" isn't meaningful to read directly.
        string nearMissLine = "";
        if (overlapCount == 0 && bestNearMiss.HasValue)
        {
            var nm = bestNearMiss.Value;
            if (nm.axis == NearMissAxis.Angle)
            {
                float measuredDeg = Mathf.Acos(Mathf.Clamp(-nm.measuredValue, -1f, 1f)) * Mathf.Rad2Deg;
                float thresholdDeg = Mathf.Acos(Mathf.Clamp(-nm.thresholdValue, -1f, 1f)) * Mathf.Rad2Deg;
                nearMissLine = $"\n<color=#c9b458>Closest: {measuredDeg:0.0}° off (max {thresholdDeg:0.0}°).</color>";
            }
            else
            {
                // Distance (a real per-pair near-miss) and PointPair (the bounding-box gate's
                // whole-set rejection) both report a plain distance — same message either way,
                // since both mean "these two points are this far apart."
                nearMissLine = $"\n<color=#e0776b>Closest: {nm.measuredValue:0.000} m apart (max {nm.thresholdValue:0.000} m).</color>";
            }
        }

        // Only a genuine coplanar polygon match (overlapCount > 0) — the actual criterion the
        // game's TryFindJointPolygonsJob uses to form a joint — can report Pass. Bounding-box
        // proximity is a crude whole-part-bounds proxy, not a real per-triangle
        // coplanar test; treating it as Pass was confirmed to produce false positives on real
        // non-jointing part pairs (bounding boxes overlap/touch, but no valid coplanar mesh
        // surface match exists between the actual render geometry).
        if (overlapCount > 0)
        {
            compatHull = new CompatResult { state = CompatResult.State.Pass,
                message = $"Mesh: {overlapCount} coplanar pair{(overlapCount == 1 ? "" : "s")}{polySuffix}." };
        }
        else
        {
            // Both the "bounding boxes close" and "N m gap" text previously reported here were
            // low-value noise (a crude whole-part-bounds proxy, not the real reason a pair
            // doesn't joint) — the actual actionable info is nearMissLine above, appended below.
            compatHull = new CompatResult { state = CompatResult.State.Fail,
                message = $"Mesh: no valid coplanar match — parts will not auto-joint{polySuffix}.{nearMissLine}" };
        }

        var newNearMiss = bestNearMiss;
        ApplyCheckResults(newJointFaces, newNearMiss);
    }

    // Swaps the freshly-computed overlay/results in only once a Check completes normally —
    // called from the routine's final yield-break, never on cancel (StopAsyncCheck doesn't call
    // this), so a canceled Check leaves the previously-displayed overlay untouched.
    void ApplyCheckResults(List<JointFaceHighlight> newJointFaces, NearMissHighlight? newNearMiss = null)
    {
        jointFaces = newJointFaces;
        // Near-miss diagnostics only matter when nothing actually joints — if a real match
        // exists, jointFaces already shows the authoritative result and the near-miss is noise.
        nearMissHighlight = jointFaces.Count > 0 ? null : newNearMiss;
        showOverlay = jointFaces.Count > 0 || nearMissHighlight.HasValue;
        // debugHullA3D/debugHullB3D (raw pre-clip hull wireframes) are only ever written when a
        // real match is found — clear them here so a stale wireframe from a PREVIOUS Check's
        // successful pair doesn't linger on screen once the current Check finds no match at all.
        if (jointFaces.Count == 0)
        {
            debugHullA3D.Clear();
            debugHullB3D.Clear();
        }
        SceneView.RepaintAll();
        Repaint();
    }

    static float BoundsDistance(Bounds a, Bounds b)
    {
        Vector3 aMin = a.min, aMax = a.max, bMin = b.min, bMax = b.max;
        float dx = Mathf.Max(0f, Mathf.Max(aMin.x - bMax.x, bMin.x - aMax.x));
        float dy = Mathf.Max(0f, Mathf.Max(aMin.y - bMax.y, bMin.y - aMax.y));
        float dz = Mathf.Max(0f, Mathf.Max(aMin.z - bMax.z, bMin.z - aMax.z));
        return new Vector3(dx, dy, dz).magnitude;
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
            return new CompatResult { state = CompatResult.State.Pass, message = $"SP_Mat: Compatible\n{sides}\nWill Auto-Joint" };
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
                // Try any StructurePart entry — but ONLY if every SP entry in this ACL resolves
                // to the SAME JSA, proving the ACL is actually homogeneous (a single baked
                // composite part's own ACL). A shared/room-level ACL covering multiple unrelated
                // parts (e.g. a floor plate and a nav terminal under the same room) is NOT
                // homogeneous, and picking an arbitrary entry from it silently attributes one
                // part's real JSA to a totally unrelated part. Confirmed real-world case: this
                // fallback returned SM_Floor_Plate_Front's JOINT_Panel_Ext for
                // SM_Prop_Terminal_Nav_Lower, whose real JSA (per StructurePartAsset.Data.
                // JointSetupAsset, ground truth from PartInfoLogger's joint_census.csv) is
                // JOINT_PropMount_Exclude_Panel_Ext — see project_jsa_compat_isactive_bug memory.
                string homogeneousJsa = null;
                bool anySpEntry = false, isHomogeneous = true;
                foreach (var cv in acl.componentValues)
                {
                    if (cv.component == null || !(cv.component is BBI.Unity.Game.StructurePart)) continue;
                    var jsa = EnrichedJsaName(cv.address);
                    if (jsa == null) continue;
                    anySpEntry = true;
                    if (homogeneousJsa == null) homogeneousJsa = jsa;
                    else if (homogeneousJsa != jsa) { isHomogeneous = false; break; }
                }
                log += $"\n  P1.5b: any-SP entries in ACL '{acl.gameObject.name}' → " +
                    (anySpEntry ? (isHomogeneous ? homogeneousJsa : "MIXED (skipped, not homogeneous)") : "none");
                if (anySpEntry && isHomogeneous) { Debug.Log(log); return homogeneousJsa; }
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

    // Triangle-pair budget for a single TryFindJointPolygon call (one MeshFilter A x MeshFilter B
    // pair) — the O(trisA x trisB) coplanar scan (same shape as the game's TryFindCoplanarPoints,
    // BBI.Unity.Game.JointHelper) has no natural upper bound for a large/complex mesh pair. Reset
    // fresh for EACH MeshFilter pair (not shared across the whole Check — see below for why that
    // matters); if it runs out mid-pair, that pair's result is partial and hullPairBudgetExceeded
    // is set so the UI can warn, which can cause a FALSE NEGATIVE for that specific pair if the
    // budget runs out before the scan reaches the triangle pair that would have matched.
    // Not a persistent user setting — every manual Check starts fresh at kDefaultHullPairBudget.
    // If exhausted, StopAsyncCheck offers a one-shot confirm dialog to re-run just that Check at
    // worstCaseRequiredPairs + 10% headroom; the bump applies only to that single re-run.
    // Per-pair, not per-Check: a shared/global counter decremented across every MeshFilter pair in
    // the whole selection would make raising it change results non-monotonically, since whichever
    // pair happened to exhaust the shared budget first would depend on scan order, not on the pair
    // actually being examined. Per-pair budgeting makes "did THIS pair need more budget" a
    // well-defined, monotonic question, and lets worstCaseRequiredPairs below report exactly how
    // large a budget would have been needed for the worst-hit pair.
    const int kDefaultHullPairBudget = 2_000_000;
    int hullPairBudgetMax = kDefaultHullPairBudget;
    int hullPairBudget;
    bool hullPairBudgetExceeded;      // true if ANY pair in the Check hit its budget
    long worstCaseRequiredPairs;      // largest trisA/3 * trisB/3 among pairs that hit the budget

    // Vertex/geometry tolerance from the game's JointHelper/MathUtility (BBI.Unity.Game) — see
    // [[project_game_jointing_algorithm]] for the decompiled source this is ported from. The
    // codirectional dot threshold used to be hardcoded here too; it's now the user-settable
    // compatCodirectionalThreshold field above (see comment there for why).
    const float kVertexEpsilon = 1e-5f;

    // Port of BBI.Unity.Game.JointHelper.TryFindJointPolygonsJob.TryFindCoplanarPoints +
    // TryCalculateConvexHull + TryGetConvexHullIntersection — the game's REAL jointing algorithm.
    // Unlike the old GetBakedConvexHull approach (which silently returned the unmodified render
    // mesh — Physics.BakeMesh never writes hull data back to Mesh.vertices/triangles, a Unity
    // limitation with no workaround), this reads MeshFilter.sharedMesh directly, exactly like the
    // game does (see [[reference_joint_mesh_source]]), and finds the actual coplanar overlap via:
    //   1. brute-force triangle-pair scan for near-opposite, near-coplanar face pairs
    //   2. 2D Jarvis March convex hull of each side's matching vertices, in joint-plane space
    //   3. convex polygon clip (Sutherland-Hodgman, via the existing ClipPolygons) of the two hulls
    // Returns false if no coplanar overlap is found. On success, outputs the shared joint plane
    // and the clipped overlap polygon in that plane's 2D space (tan/bitan basis).
    // Near-miss diagnostic data for a pair that ultimately returns false — the single closest
    // triangle pair seen on each of the two independent gating axes (coplanar distance,
    // codirectional angle), purely for user-facing guidance on failed Checks. Populated
    // read-only alongside the existing (unmodified) matching loop; never influences which
    // triangle pairs match or the pass/fail result itself.
    // Distance/Angle: a genuine per-triangle-pair near-miss (both triangles are real mesh
    // geometry, drawn as faces — angle is fundamentally a face-normal comparison, so two real
    // faces is the truthful representation). PointPair: the bounding-box-gate rejection, which
    // isn't a triangle-pair failure at all (every contributing pair already passed both gating
    // tests) — instead it's "these two specific accumulated VERTICES are the real worst-case
    // separation," so it's drawn as a single arrow along that separation axis, not fake faces.
    public enum NearMissAxis { Distance, Angle, PointPair }
    public struct NearMissInfo
    {
        public bool found;
        public NearMissAxis axis;
        public Vector3 triA0, triA1, triA2; // side A's triangle verts (world space) — Distance/Angle only
        public Vector3 triB0, triB1, triB2; // side B's triangle verts (world space) — Distance/Angle only
        public Vector3 pointA, pointB;      // the two real extreme vertices — PointPair only
        public float measuredValue;         // the actual coplanar distance, or codirectional dot
        public float thresholdValue;        // the threshold it failed to meet
    }

    // Per-triangle-pair, exactly ONE axis can be the reason it failed (the codirectional check
    // runs first and `continue`s before the coplanar check ever runs on that pair — see below),
    // so each pair contributes to at most one axis's near-miss tracking, never both. Across the
    // whole scan, only the SINGLE closest-to-passing pair overall is kept (comparing the two
    // axes on a common footing: how many threshold-widths past the limit, i.e. relative
    // overshoot) — never one-per-axis, so the reported near-miss is always one real triangle
    // pair with both its triangles highlightable, not two unrelated statistics.
    bool TryFindJointPolygon(MeshFilter mfA, MeshFilter mfB,
        out Vector3 planeOrigin, out Vector3 faceNorm, out Vector3 tan, out Vector3 bitan,
        List<Vector2> overlapPolygon2D,
        out NearMissInfo nearMiss,
        string pairLabel = null)
    {
        overlapPolygon2D.Clear();
        planeOrigin = faceNorm = tan = bitan = Vector3.zero;
        nearMiss = default;
        float bestMissOvershoot = float.MaxValue; // relative overshoot, common footing across axes

        var meshA = mfA.sharedMesh;
        var meshB = mfB.sharedMesh;
        if (meshA == null || meshB == null) return false;

        var trisA = meshA.triangles;
        var trisB = meshB.triangles;
        hullPairBudget = hullPairBudgetMax; // per-pair, not shared across the whole Check
        var vertsA = meshA.vertices;
        var vertsB = meshB.vertices;
        var mA = mfA.transform.localToWorldMatrix;
        var mB = mfB.transform.localToWorldMatrix;

        var wVertsA = new Vector3[vertsA.Length];
        for (int i = 0; i < vertsA.Length; i++) wVertsA[i] = mA.MultiplyPoint3x4(vertsA[i]);
        var wVertsB = new Vector3[vertsB.Length];
        for (int i = 0; i < vertsB.Length; i++) wVertsB[i] = mB.MultiplyPoint3x4(vertsB[i]);

        // Step 1: TryFindCoplanarPoints — FAITHFUL port of the decompiled algorithm
        // (BBI.Unity.Game.JointHelper.TryFindJointPolygonsJob.TryFindCoplanarPoints,
        // C:\Users\user\.claude\decomp\bbi_full.decompiled.cs ~line 191400). Earlier ports this
        // session omitted three real pieces of this algorithm, which is the actual root cause of
        // both the visual misorientation AND a confirmed Collider-check false positive on a real
        // non-jointing part pair:
        //   1. RESET-ON-CLOSER-MATCH: the game does NOT lock onto the first matching pair forever.
        //      It tracks a running "opposition direction" x = (normA-normB)/2 and the closest
        //      planeDist seen (num). Whenever a NEW pair is found that's closer (num2 < num) AND
        //      still agrees with the current best direction, it CLEARS the accumulated index sets
        //      and re-locks onto that better pair. Without this, one bad early match (e.g. a bevel
        //      edge) permanently contaminates the accumulated set with everything scanned after it.
        //   2. RUNNING-SUM NORMAL: jointTransform.Normal is accumulated as a sum across every
        //      distinct matched vertex (+= for side A, -= for side B), normalized once at the end
        //      — not fixed from one triangle's normal.
        //   3. FINAL BOUNDING-BOX GATE: after accumulation, the decompile checks that side A's and
        //      side B's accumulated point bounding boxes are within coplanarDistanceThreshold of
        //      each other on all 3 axes — if not, the whole match is REJECTED (return false). This
        //      is a real sanity check with no equivalent in earlier ports; its absence is likely
        //      why a spread-out, spurious "match" could still report a plausible clip area.
        var coplanarVertsA = new List<Vector3>();
        var coplanarVertsB = new List<Vector3>();
        // coplanarIndicesA/B equivalent: index membership sets used to dedupe within one A-triangle
        // iteration, matching the decompile's coplanarIndices1/2 (NativeList<int> membership scans).
        var indexSetA = new HashSet<int>();
        var indexSetB = new HashSet<int>();

        Vector3 oppositionDir = Vector3.zero; // decompile's `x`
        float bestPlaneDist = float.MaxValue; // decompile's `num`
        bool hasLocked = false;               // decompile's `flag`
        bool hasTangent = false;               // decompile's `flag2`
        Vector3 normalSum = Vector3.zero;      // decompile's jointTransform.Normal (running sum)
        Vector3 lockedPosition = Vector3.zero; // decompile's jointTransform.Position

        for (int ia = 0; ia < trisA.Length; ia += 3)
        {
            int ia0 = trisA[ia], ia1 = trisA[ia + 1], ia2 = trisA[ia + 2];
            Vector3 wa0 = wVertsA[ia0], wa1 = wVertsA[ia1], wa2 = wVertsA[ia2];
            Vector3 normA = Vector3.Cross(wa1 - wa0, wa2 - wa0);
            if (normA.sqrMagnitude < 1e-10f) continue;
            normA.Normalize();

            bool triAMatchedThisIter = false; // decompile's flag3

            for (int ib = 0; ib < trisB.Length; ib += 3)
            {
                // A single TryFindJointPolygon call has no yield points of its own (it isn't a
                // coroutine), so on a slow MeshFilter pair, Cancel would otherwise do nothing until
                // this whole O(trisA x trisB) scan finishes on its own — StepAsyncCheck can't poll
                // the modal progress bar again until this synchronous call returns. So poll it
                // directly from inside the scan instead, at a coarse cadence (every 4096 triangle
                // pairs — checking every single pair would call into native UI code far too often
                // and slow the scan down). Also drives the progress bar off actual triangle-pair
                // work done (hullPairBudgetMax - hullPairBudget), not just "which MeshFilter pair"
                // — a 1-to-1 selection would otherwise always show a meaningless "1/1".
                if ((hullPairBudgetMax - hullPairBudget) % 4096 == 0)
                {
                    checkProgress = 1f - (float)hullPairBudget / hullPairBudgetMax;
                    if (pairLabel != null)
                        checkProgressLabel = $"{pairLabel} — {hullPairBudgetMax - hullPairBudget:N0}/{hullPairBudgetMax:N0} triangle pairs...";
                    if (EditorUtility.DisplayCancelableProgressBar("Joint Compatibility Check", checkProgressLabel, checkProgress))
                    {
                        checkCanceled = true;
                        nearMiss = default;
                        return false;
                    }
                }

                if (hullPairBudget <= 0)
                {
                    hullPairBudgetExceeded = true;
                    long requiredPairs = (long)(trisA.Length / 3) * (long)(trisB.Length / 3);
                    if (requiredPairs > worstCaseRequiredPairs) worstCaseRequiredPairs = requiredPairs;
                    goto donePairs;
                }
                hullPairBudget--;

                int ib0 = trisB[ib], ib1 = trisB[ib + 1], ib2 = trisB[ib + 2];
                Vector3 wb0 = wVertsB[ib0], wb1 = wVertsB[ib1], wb2 = wVertsB[ib2];
                Vector3 normB = Vector3.Cross(wb1 - wb0, wb2 - wb0);
                if (normB.sqrMagnitude < 1e-10f) continue;
                normB.Normalize();

                float codirDot = Vector3.Dot(normA, normB);
                if (codirDot >= -compatCodirectionalThreshold)
                {
                    // This pair failed ONLY the codirectional angle test. Track it as the best
                    // near-miss overall (across BOTH axes, whole scan) if its relative overshoot
                    // (how many threshold-widths past the limit) is the smallest seen — purely
                    // diagnostic, does not affect matching in any way (read-only).
                    float overshoot = (codirDot - (-compatCodirectionalThreshold)) / Mathf.Max(1e-4f, compatCodirectionalThreshold);
                    if (overshoot < bestMissOvershoot)
                    {
                        bestMissOvershoot = overshoot;
                        nearMiss = new NearMissInfo
                        {
                            found = true,
                            axis = NearMissAxis.Angle,
                            triA0 = wa0, triA1 = wa1, triA2 = wa2,
                            triB0 = wb0, triB1 = wb1, triB2 = wb2,
                            measuredValue = codirDot,
                            thresholdValue = -compatCodirectionalThreshold
                        };
                    }
                    continue;
                }

                float thisDist = Mathf.Abs(Vector3.Dot(wa0 - wb0, normA));
                if (thisDist >= compatCoplanarThreshold)
                {
                    // This pair passed the codirectional test but failed ONLY the coplanar
                    // distance test. Same cross-axis overshoot comparison as above.
                    float overshoot = (thisDist - compatCoplanarThreshold) / Mathf.Max(1e-4f, compatCoplanarThreshold);
                    if (overshoot < bestMissOvershoot)
                    {
                        bestMissOvershoot = overshoot;
                        nearMiss = new NearMissInfo
                        {
                            found = true,
                            axis = NearMissAxis.Distance,
                            triA0 = wa0, triA1 = wa1, triA2 = wa2,
                            triB0 = wb0, triB1 = wb1, triB2 = wb2,
                            measuredValue = thisDist,
                            thresholdValue = compatCoplanarThreshold
                        };
                    }
                    continue;
                }

                Vector3 candidateOpposition = (normA - normB) * 0.5f;
                bool agreesWithBest = Vector3.Dot(oppositionDir, candidateOpposition) < compatCodirectionalThreshold;

                if (!hasLocked || (agreesWithBest && thisDist < bestPlaneDist))
                {
                    // Reset: this is a strictly closer (or first) match — clear everything
                    // accumulated so far and re-lock onto this pair.
                    oppositionDir   = candidateOpposition;
                    bestPlaneDist   = thisDist;
                    normalSum       = Vector3.zero;
                    lockedPosition  = wa0;
                    indexSetA.Clear();
                    indexSetB.Clear();
                    coplanarVertsA.Clear();
                    coplanarVertsB.Clear();
                    hasLocked       = true;
                    hasTangent      = false;
                    agreesWithBest  = false;
                }
                if (agreesWithBest) continue;

                if (!hasTangent && hasLocked)
                {
                    Vector3 edge = wa1 - wa0;
                    if (edge.sqrMagnitude > 1e-5f)
                    {
                        tan = edge.normalized;
                        hasTangent = true;
                    }
                }

                triAMatchedThisIter = true;

                if (indexSetB.Add(ib0)) { coplanarVertsB.Add(wb0); normalSum -= normB; }
                if (indexSetB.Add(ib1)) { coplanarVertsB.Add(wb1); normalSum -= normB; }
                if (indexSetB.Add(ib2)) { coplanarVertsB.Add(wb2); normalSum -= normB; }
            }

            if (!triAMatchedThisIter) continue;

            if (indexSetA.Add(ia0)) { coplanarVertsA.Add(wa0); normalSum += normA; }
            if (indexSetA.Add(ia1)) { coplanarVertsA.Add(wa1); normalSum += normA; }
            if (indexSetA.Add(ia2)) { coplanarVertsA.Add(wa2); normalSum += normA; }
        }
        donePairs:

        if (!hasTangent || coplanarVertsA.Count < 3 || coplanarVertsB.Count < 3)
        {
            DebugLog($"[{mfA.name} x {mfB.name}] NO PLANE FOUND (hasTangent={hasTangent}, vertsA={coplanarVertsA.Count}, vertsB={coplanarVertsB.Count})");
            return false;
        }

        faceNorm    = normalSum.normalized;
        planeOrigin = lockedPosition;
        bitan       = Vector3.Cross(tan, faceNorm).normalized;
        // Re-orthogonalize tan against the final averaged normal (decompile computes BiTangent =
        // cross(Tangent, Normal) directly without re-projecting Tangent — mirror that exactly;
        // Tangent itself came from a single real mesh edge and is never modified further).

        // Final bounding-box sanity gate (decompile ~line 191559): reject the whole match if the
        // two accumulated point clouds' bounding boxes aren't actually within
        // compatCoplanarThreshold of each other on every axis — catches a spread-out, spurious
        // "match" that individual pairwise tests let through.
        Vector3 minA = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 maxA = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (var v in coplanarVertsA)
        {
            minA = Vector3.Min(minA, v);
            maxA = Vector3.Max(maxA, v);
        }
        Vector3 minB = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 maxB = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (var v in coplanarVertsB)
        {
            minB = Vector3.Min(minB, v);
            maxB = Vector3.Max(maxB, v);
        }
        float boxSepX = Mathf.Max(minA.x - maxB.x, minB.x - maxA.x);
        float boxSepY = Mathf.Max(minA.y - maxB.y, minB.y - maxA.y);
        float boxSepZ = Mathf.Max(minA.z - maxB.z, minB.z - maxA.z);
        if (boxSepX > compatCoplanarThreshold || boxSepY > compatCoplanarThreshold || boxSepZ > compatCoplanarThreshold)
        {
            DebugLog($"[{mfA.name} x {mfB.name}] REJECTED by bounding-box gate: A=[{minA:F3}..{maxA:F3}] B=[{minB:F3}..{maxB:F3}]");

            // Diagnostic only (does not affect the reject decision above, already made): every
            // individual triangle pair already passed both gating tests — what fails here is a
            // WHOLE-SET aggregate check, so there's no single "near-miss triangle pair" the way
            // the two per-pair axes have. Instead, find the actual two VERTICES (one per side)
            // that are CLOSEST to each other across the gap on the worst-offending axis — these
            // are precisely the two points minA/maxA/minB/maxB were computed from, so this
            // reconstructs the true closest-approach pair, not an arbitrary/unrelated triangle.
            float worstSep; int worstAxis; // 0=X,1=Y,2=Z
            if (boxSepX >= boxSepY && boxSepX >= boxSepZ) { worstSep = boxSepX; worstAxis = 0; }
            else if (boxSepY >= boxSepZ) { worstSep = boxSepY; worstAxis = 1; }
            else { worstSep = boxSepZ; worstAxis = 2; }

            // On the worst axis, exactly one of {A's near-side vertex, B's near-side vertex} is
            // the pair actually driving the gap — whichever comparison (minA-maxB vs minB-maxA)
            // matches worstSep tells us which side is "on the low end" of the gap.
            bool aIsLowSide = (worstAxis == 0 ? minA.x - maxB.x : worstAxis == 1 ? minA.y - maxB.y : minA.z - maxB.z)
                        >= (worstAxis == 0 ? minB.x - maxA.x : worstAxis == 1 ? minB.y - maxA.y : minB.z - maxA.z);
            // If A is on the low end, A's CLOSEST-to-B point is its min on this axis, and B's
            // closest-to-A point is its max (the two points bracketing the gap from either side).
            Vector3 vertA = FindClosestApproachVertex(coplanarVertsA, worstAxis, findMin: aIsLowSide);
            Vector3 vertB = FindClosestApproachVertex(coplanarVertsB, worstAxis, findMin: !aIsLowSide);

            nearMiss = new NearMissInfo
            {
                found = true,
                axis = NearMissAxis.PointPair,
                pointA = vertA, pointB = vertB,
                measuredValue = worstSep,
                thresholdValue = compatCoplanarThreshold
            };
            return false;
        }

        // Step 2: project each side's coplanar verts into the joint plane's 2D space, then take
        // the 2D convex hull (Jarvis March, matching the game's TryCalculateConvexHull).
        var pts2DA = new List<Vector2>(coplanarVertsA.Count);
        foreach (var v in coplanarVertsA) pts2DA.Add(ToPlane2D(v, planeOrigin, tan, bitan));
        var pts2DB = new List<Vector2>(coplanarVertsB.Count);
        foreach (var v in coplanarVertsB) pts2DB.Add(ToPlane2D(v, planeOrigin, tan, bitan));

        var hullA = Jarvis2DHull(pts2DA);
        var hullB = Jarvis2DHull(pts2DB);
        if (hullA.Count < 3 || hullB.Count < 3)
        {
            DebugLog($"[{mfA.name} x {mfB.name}] HULL TOO SMALL hullA={hullA.Count} hullB={hullB.Count}");
            return false;
        }

        // TEMP DEBUG: reconstruct hullA/hullB to 3D (same formula as the final clipped polygon)
        // for direct visual inspection — do these two hulls actually sit on/near the same real
        // seam, or are they two unrelated surfaces that just happen to flatten onto one plane?
        debugHullA3D.Clear();
        foreach (var p in hullA) debugHullA3D.Add(planeOrigin + p.x * tan + p.y * bitan);
        debugHullB3D.Clear();
        foreach (var p in hullB) debugHullB3D.Add(planeOrigin + p.x * tan + p.y * bitan);

        // Step 3: clip the two convex hulls against each other (Sutherland-Hodgman is
        // mathematically equivalent to the game's segment-intersection-based
        // TryGetConvexHullIntersection for convex-vs-convex polygons).
        float clipArea = ClipPolygons(hullA, hullB, overlapPolygon2D);
        DebugLog($"[{mfA.name} x {mfB.name}] clipArea={clipArea:F4} resultVerts={overlapPolygon2D.Count} " +
            $"planeOrigin={planeOrigin:F3} faceNorm={faceNorm:F4} tan={tan:F4} bitan={bitan:F4} " +
            $"vertsA={coplanarVertsA.Count} vertsB={coplanarVertsB.Count} bestPlaneDist={bestPlaneDist:F5}");
        return clipArea > 1e-9f;
    }

    static Vector2 ToPlane2D(Vector3 v, Vector3 origin, Vector3 tan, Vector3 bitan)
        => new Vector2(Vector3.Dot(v - origin, tan), Vector3.Dot(v - origin, bitan));

    static void AddUnique(List<Vector3> list, Vector3 v)
    {
        for (int i = 0; i < list.Count; i++)
            if ((list[i] - v).sqrMagnitude < kVertexEpsilon) return;
        list.Add(v);
    }

    // Finds the actual vertex in `verts` at the boundary of this point cloud on the given axis —
    // its minimum (findMin: true) or maximum (findMin: false) coordinate. Used by the bounding-box
    // gate's near-miss diagnostic to locate the two REAL vertices bracketing the worst-offending
    // axis's gap, instead of an arbitrary triangle unrelated to the actual measured separation.
    static Vector3 FindClosestApproachVertex(List<Vector3> verts, int axis, bool findMin)
    {
        Vector3 best = verts[0];
        float bestCoord = axis == 0 ? best.x : axis == 1 ? best.y : best.z;
        for (int i = 1; i < verts.Count; i++)
        {
            float c = axis == 0 ? verts[i].x : axis == 1 ? verts[i].y : verts[i].z;
            if (findMin ? c < bestCoord : c > bestCoord) { bestCoord = c; best = verts[i]; }
        }
        return best;
    }

    // 2D convex hull via Jarvis March (gift-wrapping) — port of BBI.Unity.Game.JointHelper.
    // TryFindJointPolygonsJob.TryCalculateConvexHull. At each step, picks the most
    // counter-clockwise point from the current hull edge (ties broken by farthest distance).
    static List<Vector2> Jarvis2DHull(List<Vector2> points)
    {
        var hull = new List<Vector2>();
        int n = points.Count;
        if (n < 3) return hull;

        int start = 0;
        for (int i = 1; i < n; i++)
            if (points[i].x < points[start].x) start = i;

        int current = start;
        do
        {
            hull.Add(points[current]);
            int next = (current + 1) % n;
            for (int i = 0; i < n; i++)
            {
                if (i == current) continue;
                float cross = Cross2D(points[next] - points[current], points[i] - points[current]);
                if (cross < 0f) next = i;
            }
            current = next;
        }
        while (current != start && hull.Count <= n);

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
            .Replace("covers all selected parts","<color=#44cc44>covers all selected parts</color>");
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

        if (snapUseColliderHull)
        {
            // Collider-hull mode is a live-collider proxy, not the game's actual jointing geometry
            // (see [[project_game_jointing_algorithm]] — the game reads the render mesh, not a
            // PhysX hull) — draw a normal-oriented disc + arrow at the picked point instead of a
            // fabricated coplanar patch, so the overlay doesn't imply precision it doesn't have.
            var prevColor = Handles.color;
            float size = HandleUtility.GetHandleSize(f.point) * 0.15f;
            Handles.color = fill;
            Handles.DrawSolidDisc(f.point, wn, size);
            Handles.color = outline;
            Handles.DrawWireDisc(f.point, wn, size);
            Handles.ArrowHandleCap(0, f.point, Quaternion.LookRotation(wn), size * 2f, EventType.Repaint);
            Handles.color = prevColor;
            return;
        }

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
        // Draw joint overlap highlights — the merged coplanar overlap polygon per MeshFilter
        // pair found by TryFindJointPolygon (matches what the game will actually joint).
        if (showOverlay && jointFaces != null)
        {
            var prevColor = Handles.color;
            var fillColor = new Color(0.2f, 1f, 0.4f, 0.35f);
            var lineColor = new Color(0.2f, 1f, 0.4f, 1f);
            foreach (var f in jointFaces)
            {
                if (f.poly == null || f.poly.Length < 3) continue;
                Handles.color = fillColor;
                Handles.DrawAAConvexPolygon(f.poly);
                Handles.color = lineColor;
                for (int i = 0; i < f.poly.Length; i++)
                    Handles.DrawLine(f.poly[i], f.poly[(i + 1) % f.poly.Length]);
            }
            Handles.color = prevColor;
        }

        // Near-miss diagnostic highlight — only ever populated (by ApplyCheckResults) when
        // NOTHING in the selection actually joints, showing the SINGLE closest real candidate
        // pair (both its triangles, side A and side B). Red = distance near-miss (right angle,
        // too far apart). Yellow = angle near-miss (close together, wrong angle). Purely
        // informational — never drawn alongside a real jointFaces result.
        if (showOverlay && nearMissHighlight.HasValue)
        {
            var nm = nearMissHighlight.Value;
            var prevColor = Handles.color;
            Color baseColor = nm.axis == NearMissAxis.Angle ? new Color(1f, 0.9f, 0.1f) : new Color(1f, 0.15f, 0.1f);

            if (nm.axis == NearMissAxis.PointPair)
            {
                // Bounding-box-gate rejection — a real per-triangle-pair angle/distance test
                // never fired here (every contributing pair already passed both), so drawing
                // fake faces would be misleading. The two points' individual positions aren't
                // meaningful on their own (they're just whichever real vertices happened to sit
                // at the extremes of the worst-offending axis) — what IS meaningful is the axis
                // itself: the single direction the two parts need to move together (or apart)
                // along. So draw one arrow, centered on the midpoint, along that direction —
                // not a line connecting two arbitrary vertices.
                Vector3 mid = (nm.pointA + nm.pointB) * 0.5f;
                Vector3 dir = (nm.pointB - nm.pointA);
                if (dir.sqrMagnitude < 1e-8f) dir = Vector3.up;
                dir.Normalize();
                // Purple, not red/green/blue, so it's never confused with the transform gizmo's
                // axis handles when both are visible in the scene view at once.
                Color arrowColor = new Color(0.75f, 0.25f, 1f);
                float arrowSize = HandleUtility.GetHandleSize(mid) * 1.5f;
                Handles.color = arrowColor;
                Handles.ArrowHandleCap(0, mid - dir * arrowSize * 0.5f, Quaternion.LookRotation(dir), arrowSize, EventType.Repaint);
            }
            else
            {
                // Distance/Angle — a genuine per-triangle-pair near-miss; angle in particular is
                // fundamentally a face-normal comparison, so two real faces is the truthful
                // representation (not a synthetic marker).
                Handles.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0.35f);
                Handles.DrawAAConvexPolygon(nm.triA0, nm.triA1, nm.triA2);
                Handles.DrawAAConvexPolygon(nm.triB0, nm.triB1, nm.triB2);
                Handles.color = new Color(baseColor.r, baseColor.g, baseColor.b, 1f);
                Handles.DrawLine(nm.triA0, nm.triA1); Handles.DrawLine(nm.triA1, nm.triA2); Handles.DrawLine(nm.triA2, nm.triA0);
                Handles.DrawLine(nm.triB0, nm.triB1); Handles.DrawLine(nm.triB1, nm.triB2); Handles.DrawLine(nm.triB2, nm.triB0);
            }
            Handles.color = prevColor;
        }

        // TEMP DEBUG: draw the raw pre-clip hullA (orange) / hullB (cyan) wireframes, so we can
        // see directly whether they sit on the same real seam or are two unrelated surfaces that
        // happened to flatten onto the same chosen plane.
        if (showOverlay)
        {
            var prevColor = Handles.color;
            if (debugHullA3D.Count >= 2)
            {
                Handles.color = new Color(1f, 0.5f, 0f, 1f);
                for (int i = 0; i < debugHullA3D.Count; i++)
                    Handles.DrawLine(debugHullA3D[i], debugHullA3D[(i + 1) % debugHullA3D.Count]);
            }
            if (debugHullB3D.Count >= 2)
            {
                Handles.color = new Color(0f, 0.9f, 1f, 1f);
                for (int i = 0; i < debugHullB3D.Count; i++)
                    Handles.DrawLine(debugHullB3D[i], debugHullB3D[(i + 1) % debugHullB3D.Count]);
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

            if (picked != null && snapUseColliderHull)
            {
                // Query PhysX directly against the live MeshCollider(s) — for a convex collider
                // this hits the actual baked convex hull the game's physics/jointing sees, which
                // can bulge outward past concave/pointed render-mesh details. Cast against every
                // collider under picked and keep the closest hit, mirroring the render-mesh path's
                // "closest triangle across all MeshFilters" behavior.
                float bestDist = float.MaxValue;
                foreach (var col in picked.GetComponentsInChildren<Collider>())
                {
                    if (col.isTrigger) continue;
                    if (!col.Raycast(ray, out var rayHit, float.MaxValue)) continue;
                    if (rayHit.distance >= bestDist) continue;
                    bestDist  = rayHit.distance;
                    hitPoint  = rayHit.point;
                    hitNormal = rayHit.normal;
                }
                hit = bestDist < float.MaxValue;
            }
            else if (picked != null)
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
                    GameObject inst;

                    // If the assigned prefab is a pure-addressable wrapper (an AddressableLoader
                    // living somewhere in it, e.g. exported by ImportGamePartWizard's "pure
                    // addressable" placement), don't instantiate the wrapper prefab itself — create
                    // a fresh AddressableLoader node with the SAME assetGUID instead, exactly like
                    // ImportGamePartWizard.DoPlaceLocal's addressable branch. This is what makes the
                    // result genuinely interactable in-game (the game's own Addressables system
                    // resolves assetGUID into the real prefab at runtime) rather than a
                    // FakeStructurePart-style visual stand-in with no real StructurePart behind it.
                    var existingLoader = cutPointPrefab.GetComponentInChildren<BBI.Unity.Game.AddressableLoader>(true);
                    if (existingLoader != null && !string.IsNullOrEmpty(existingLoader.assetGUID))
                    {
                        inst = new GameObject(cutPointPrefab.name);
                        Undo.RegisterCreatedObjectUndo(inst, "Place Cut Point");
                        inst.transform.SetParent(parent, false);
                        var loader = inst.AddComponent<BBI.Unity.Game.AddressableLoader>();
                        loader.assetGUID = existingLoader.assetGUID;
                        loader.childPath = existingLoader.childPath;
                    }
                    else
                    {
                        // A real baked local prefab — instantiate as-is, same as before.
                        inst = (GameObject)PrefabUtility.InstantiatePrefab(cutPointPrefab, parent);
                        Undo.RegisterCreatedObjectUndo(inst, "Place Cut Point");
                    }

                    inst.transform.localScale = Vector3.one;
                    inst.transform.position   = hitPoint;
                    inst.transform.rotation   = CutPointRotation(hitNormal);
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
        // Collider-hull mode is a live-collider proxy with no queryable triangle list — Center
        // of Face averaging isn't possible, so fall back to the raw click point.
        if (!centerMode || snapUseColliderHull) return face.point;
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
        AutoDetectFacesBetween(sel[0], sel[1]);
        statusMessage = $"Auto-detected faces: '{sel[0].name}' → '{sel[1].name}'.";
        statusType    = MessageType.Info;
        Repaint();
    }

    // Core of AutoDetectFaces, factored out so ApplyFaceSnap can re-run it on the same two
    // objects after a snap moves them — the picked points/normals are derived fresh from each
    // object's CURRENT bounds, so this resets the stale indicators without touching any of the
    // snap's own position/rotation/scale math.
    void AutoDetectFacesBetween(GameObject a, GameObject b, bool resetAncestorDepth = true)
    {
        // Mesh mode: pick faces from real mesh geometry — the closest pair of coplanar-triangle
        // clusters (one per object) whose normals roughly oppose each other, i.e. "which two
        // faces would touch first if brought together." Bounding-box picking (below, still used
        // for Collider Hull mode and as a fallback) instead picks whichever face has the
        // smallest box-to-box gap across the object's WHOLE bounds, which for elongated/rotated
        // parts is often a completely different face than the one that's actually closest —
        // confirmed by the same failure mode in Rotate Stops on Flush's early attempts, fixed
        // there by switching to real mesh-triangle-based face picking.
        if (!snapUseColliderHull && TryFindClosestFacingFaces(a, b, out var meshFaceA, out var meshFaceB))
        {
            snapFaceA = meshFaceA;
            snapFaceB = meshFaceB;
            if (resetAncestorDepth) ResetAncestorDepth();
            return;
        }

        Bounds bA = GetBounds(a), bB = GetBounds(b);
        Vector3 dir = GetDirection(bA, bB); // direction from A toward B

        // Face center on A: the face pointing toward B
        Vector3 faceAPoint = bA.center + dir * ReachInDir(bA, dir);
        // Face center on B: the face pointing toward A
        Vector3 faceBPoint = bB.center - dir * ReachInDir(bB, dir);

        snapFaceA = new PickedFace { point = faceAPoint, normal =  dir, source = a, localNormal = a.transform.worldToLocalMatrix.MultiplyVector(dir) };
        snapFaceB = new PickedFace { point = faceBPoint, normal = -dir, source = b, localNormal = b.transform.worldToLocalMatrix.MultiplyVector(-dir) };
        // Only reset the ▲▼ "Moving" ancestor selection when the picked objects themselves
        // change (a genuine new auto-detect). The post-snap refresh call (resetAncestorDepth:
        // false) re-derives stale face points/normals on the SAME pair of objects and must not
        // clobber the user's manual ancestor-depth choice each time Snap is pressed.
        if (resetAncestorDepth)
            ResetAncestorDepth();
    }

    struct MeshFaceCandidate { public Vector3 normal; public Vector3 point; public float area; }

    // Clusters a's/b's render-mesh triangles into distinct coplanar faces (by shared normal,
    // same grouping approach as Rotate Stops on Flush's CollectCandidateFacesOnA), then picks a
    // pair (one face from A, one from B) using a two-stage rule — same reasoning as Rotate Stops
    // on Flush's candidate ranking: pure "closest by distance" let a small, coincidentally-closer
    // sliver face win over the real, larger back face that was only marginally farther away.
    // Stage 1 narrows to face PAIRS within a small tolerance of the closest pair-distance found
    // (i.e. genuinely close, not just "closest of all options" by a hair) and whose normals are
    // at least roughly opposing (rules out two faces that happen to be near each other but point
    // the same way, e.g. two side panels that would never actually come together). Stage 2 picks
    // the largest-combined-area pair among that close bracket, favoring the real dominant face
    // over an incidental sliver.
    static bool TryFindClosestFacingFaces(GameObject a, GameObject b, out PickedFace faceA, out PickedFace faceB)
    {
        var facesA = CollectMeshFaces(a.transform);
        var facesB = CollectMeshFaces(b.transform);

        const float minOpposingDot = 0.3f; // ~72 degrees from exactly opposing; generous since
                                            // the two parts aren't necessarily aligned yet

        float bestDist = float.MaxValue;
        foreach (var ca in facesA)
            foreach (var cb in facesB)
            {
                if (Vector3.Dot(ca.normal, -cb.normal) < minOpposingDot) continue;
                float dist = Vector3.Distance(ca.point, cb.point);
                if (dist < bestDist) bestDist = dist;
            }

        if (bestDist == float.MaxValue) { faceA = default; faceB = default; return false; }

        // Bracket tolerance scales with the closest distance itself (relative, not a fixed
        // meters value) so it behaves sensibly whether the two parts are centimeters or meters
        // apart, then adds a small absolute floor for the near-zero/already-touching case.
        float bracket = Mathf.Max(bestDist * 0.25f, 0.02f);

        MeshFaceCandidate bestA = default, bestB = default;
        float bestArea = -1f;
        bool found = false;

        foreach (var ca in facesA)
        {
            foreach (var cb in facesB)
            {
                if (Vector3.Dot(ca.normal, -cb.normal) < minOpposingDot) continue;
                float dist = Vector3.Distance(ca.point, cb.point);
                if (dist > bestDist + bracket) continue; // not among the closest pairs

                float combinedArea = ca.area + cb.area;
                if (combinedArea > bestArea) { bestArea = combinedArea; bestA = ca; bestB = cb; found = true; }
            }
        }

        if (!found) { faceA = default; faceB = default; return false; }

        faceA = new PickedFace { point = bestA.point, normal = bestA.normal, source = a,
            localNormal = a.transform.worldToLocalMatrix.MultiplyVector(bestA.normal) };
        faceB = new PickedFace { point = bestB.point, normal = bestB.normal, source = b,
            localNormal = b.transform.worldToLocalMatrix.MultiplyVector(bestB.normal) };
        return true;
    }

    static List<MeshFaceCandidate> CollectMeshFaces(Transform root)
    {
        var groups = new List<MeshFaceCandidate>();
        const float normalGroupTolerance = 0.05f; // ~18 degrees

        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            var mesh = mf.sharedMesh;
            var verts = mesh.vertices; var tris = mesh.triangles;
            var m = mf.transform.localToWorldMatrix;

            for (int ti = 0; ti < tris.Length; ti += 3)
            {
                var v0 = m.MultiplyPoint3x4(verts[tris[ti]]);
                var v1 = m.MultiplyPoint3x4(verts[tris[ti + 1]]);
                var v2 = m.MultiplyPoint3x4(verts[tris[ti + 2]]);
                var normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                float area = Vector3.Cross(v1 - v0, v2 - v0).magnitude * 0.5f;
                if (area < 1e-8f) continue;
                var center = (v0 + v1 + v2) / 3f;

                bool merged = false;
                for (int g = 0; g < groups.Count; g++)
                {
                    if (Vector3.Dot(groups[g].normal, normal) < 1f - normalGroupTolerance) continue;
                    var existing = groups[g];
                    // Area-weighted running average keeps 'point' representative of the whole
                    // face rather than drifting toward whichever triangle happened to merge last.
                    float totalArea = existing.area + area;
                    existing.point = (existing.point * existing.area + center * area) / totalArea;
                    existing.area = totalArea;
                    groups[g] = existing;
                    merged = true;
                    break;
                }
                if (!merged)
                    groups.Add(new MeshFaceCandidate { normal = normal, point = center, area = area });
            }
        }

        return groups;
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

        // Face A's stored .point/.normal are never updated after the snap moves/rotates it, so
        // repeated Snap presses kept re-deriving the delta from stale pre-snap data and
        // compounding the move each click. Re-running full auto-detection (confirmed correct
        // by manual testing: pressing "Auto-Detect Faces" between each Snap works as expected)
        // fixes it — rotation is typically free, so keeping the old normal fixed (as an earlier
        // attempt did) goes stale the moment rotation actually changes anything.
        //
        // Physics.SyncTransforms() is required here: GetBounds/AutoDetectFacesBetween read
        // Renderer.bounds/Collider.bounds, and those caches were confirmed (via logging) to lag
        // one call behind the moveRoot.position/rotation write above within the same synchronous
        // ApplyFaceSnap call — without a forced sync, the refresh below reads stale bounds and
        // the fix only "catches up" one click late, producing the inward/outward drift.
        Physics.SyncTransforms();
        if (fA.source != null && fB.source != null)
            AutoDetectFacesBetween(fA.source, fB.source, resetAncestorDepth: false);
        SceneView.RepaintAll();

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
        // Collider.bounds is a real live PhysX AABB — unlike hull triangles, it needs no
        // baked-mesh access, so AutoDetectFaces can honor snapUseColliderHull exactly.
        if (snapUseColliderHull)
        {
            var hullCols = go.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToArray();
            if (hullCols.Length > 0)
            {
                Bounds hb = hullCols[0].bounds;
                for (int i = 1; i < hullCols.Length; i++) hb.Encapsulate(hullCols[i].bounds);
                return hb;
            }
        }

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
