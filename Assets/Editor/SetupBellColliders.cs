using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBI.Unity.Game;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

public class CylindricalColliderSegments : EditorWindow
{
    enum Mode { CopySegment, SliceFromGeometry }
    Mode m_Mode = Mode.CopySegment;

    // Copy-segment mode
    GameObject m_Prefab;
    Mesh       m_ColMesh;

    // Shared
    float m_SegmentAngle = 45f;

    // Slice-from-geometry mode
    enum Axis { X, Y, Z }
    Axis  m_Axis = Axis.Z;
    bool  m_AutoOffset = true;         // default offset = segmentAngle/2 (boundaries land on seams)
    float m_StartAngleOffset = 0f;     // manual override when m_AutoOffset is off
    float m_WedgeGap = 0f;             // angular gap (deg) between wedges; 0 = full coverage (default). Only raise if convex-hull overlap blows wedges apart.
    bool  m_PerWedgeStructurePart = true;

    float EffectiveOffset => m_AutoOffset ? m_SegmentAngle * 0.5f : m_StartAngleOffset;

    int WedgeCount => Mathf.Max(1, Mathf.RoundToInt(360f / m_SegmentAngle));

    [MenuItem("Shipbreaker/Shipbuilder Tools/Cylindrical Collider Segments", priority = 10)]
    static void Open()
    {
        var w = GetWindow<CylindricalColliderSegments>("Cylindrical Collider Segments");
        w.minSize = new Vector2(360, 240);
    }

    void OnGUI()
    {
        EditorGUILayout.Space(6);
        m_Mode = (Mode)EditorGUILayout.EnumPopup("Mode", m_Mode);
        EditorGUILayout.Space(4);

        if (m_Mode == Mode.CopySegment)
            DrawCopySegmentGUI();
        else
            DrawSliceGUI();
    }

    // ── Copy-segment mode (original): tile one hand-authored collider mesh around Z ───────────────
    void DrawCopySegmentGUI()
    {
        EditorGUILayout.HelpBox(
            "Tiles one collider-segment mesh around Z by the segment angle and sets the root collider " +
            "to a trigger. Use when you have a single hand-authored wedge collider (e.g. from Blender).",
            MessageType.None);

        m_Prefab       = (GameObject)EditorGUILayout.ObjectField("Base Part Prefab",  m_Prefab,  typeof(GameObject), false);
        m_ColMesh      = (Mesh)      EditorGUILayout.ObjectField("Collider Segment",  m_ColMesh, typeof(Mesh),       false);
        m_SegmentAngle = EditorGUILayout.FloatField("Segment Angle (deg)", m_SegmentAngle);

        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.IntField("Wedge Count", WedgeCount);

        EditorGUILayout.Space(8);

        bool ready = m_Prefab != null && m_ColMesh != null && m_SegmentAngle > 0f;
        using (new EditorGUI.DisabledScope(!ready))
        {
            if (GUILayout.Button("Setup Colliders", GUILayout.Height(32)))
                ApplyCopySegment();
        }

        if (!ready)
            EditorGUILayout.HelpBox(
                m_Prefab  == null    ? "Assign a base part prefab." :
                m_ColMesh == null    ? "Assign a collider segment mesh." :
                m_SegmentAngle <= 0f ? "Segment angle must be > 0." : "",
                MessageType.Info);
    }

    void ApplyCopySegment()
    {
        string prefabPath = AssetDatabase.GetAssetPath(m_Prefab);
        if (string.IsNullOrEmpty(prefabPath)) { Debug.LogError("Prefab is not a project asset."); return; }

        string meshPath = AssetDatabase.GetAssetPath(m_ColMesh);
        if (!string.IsNullOrEmpty(meshPath))
        {
            var importer = AssetImporter.GetAtPath(meshPath) as ModelImporter;
            if (importer != null && !importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); }
        }

        using var scope = new PrefabUtility.EditPrefabContentsScope(prefabPath);
        var root = scope.prefabContentsRoot;

        string prefix = root.name + "_Col_";
        for (int i = root.transform.childCount - 1; i >= 0; i--)
        {
            var child = root.transform.GetChild(i);
            if (child.name.StartsWith(prefix))
                Object.DestroyImmediate(child.gameObject);
        }

        var rootCol = root.GetComponent<MeshCollider>();
        if (rootCol != null) { rootCol.enabled = true; rootCol.isTrigger = true; }

        int wedgeCount = WedgeCount;
        for (int i = 0; i < wedgeCount; i++)
        {
            var child = new GameObject($"{prefix}{i:D2}");
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = Vector3.zero;
            child.transform.localScale    = Vector3.one;
            child.transform.localRotation = Quaternion.Euler(0f, 0f, i * m_SegmentAngle);

            var mc = child.AddComponent<MeshCollider>();
            mc.sharedMesh = m_ColMesh;
            mc.convex     = true;
        }

        Debug.Log($"Cylindrical Collider Segments: added {wedgeCount} segments to '{m_Prefab.name}', root MeshCollider set to trigger.");
    }

    // ── Slice-from-geometry mode (new): re-segment the LIVE scaled assembly into angular wedges ────
    void DrawSliceGUI()
    {
        EditorGUILayout.HelpBox(
            "Slices the selected scene object's live geometry into angular wedges around an axis. Reads " +
            "world-space verts, so non-uniform scale/shear is captured exactly as shown. Each wedge becomes " +
            "a full part (render mesh + convex collider + StructurePart), preserving the hollow interior " +
            "with no shear. Scale the object first, then slice (the scale is baked into the wedge meshes).",
            MessageType.None);

        var sel = Selection.activeGameObject;
        EditorGUILayout.LabelField("Source (scene selection)", sel != null ? sel.name : "— none —");

        m_Axis                  = (Axis)EditorGUILayout.EnumPopup("Radial Axis", m_Axis);
        m_SegmentAngle          = EditorGUILayout.FloatField("Segment Angle (deg)", m_SegmentAngle);

        m_AutoOffset = EditorGUILayout.Toggle(
            new GUIContent("Auto Offset (½ segment)", "Default: start boundaries at segmentAngle/2 so they land on natural seams (e.g. octagon corners). Turn off to set a manual offset."),
            m_AutoOffset);
        if (m_AutoOffset)
        {
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.FloatField("Start Angle Offset (deg)", m_SegmentAngle * 0.5f);
        }
        else
        {
            m_StartAngleOffset = EditorGUILayout.FloatField(
                new GUIContent("Start Angle Offset (deg)", "Rotates where wedge boundaries begin so they land on natural seams instead of cutting mid-panel."),
                m_StartAngleOffset);
        }

        m_WedgeGap = EditorGUILayout.FloatField(
            new GUIContent("Wedge Gap (deg)", "Angular gap left between wedges so their convex colliders don't overlap at the seams. Overlapping convex hulls interpenetrate and the physics solver blows the wedges apart on spawn. Increase if the part explodes; 0 = no gap (full coverage, may overlap)."),
            m_WedgeGap);
        m_WedgeGap = Mathf.Clamp(m_WedgeGap, 0f, Mathf.Max(0f, m_SegmentAngle - 1f));

        m_PerWedgeStructurePart = EditorGUILayout.Toggle(
            new GUIContent("Per-wedge StructurePart", "Each wedge is independently salvageable. Off = collision-only wedges."),
            m_PerWedgeStructurePart);

        using (new EditorGUI.DisabledScope(true))
            EditorGUILayout.IntField("Wedge Count", WedgeCount);

        EditorGUILayout.Space(8);

        int meshCount = sel != null ? sel.GetComponentsInChildren<MeshFilter>().Count(m => m.sharedMesh != null) : 0;
        bool ready = sel != null && meshCount > 0 && m_SegmentAngle > 0f;
        using (new EditorGUI.DisabledScope(!ready))
        {
            if (GUILayout.Button($"Slice Into {WedgeCount} Wedges", GUILayout.Height(32)))
                ApplySlice(sel);
        }

        if (!ready)
            EditorGUILayout.HelpBox(
                sel == null          ? "Select the assembly GameObject in the scene." :
                meshCount == 0       ? "Selection has no meshes to slice." :
                m_SegmentAngle <= 0f ? "Segment angle must be > 0." : "",
                MessageType.Info);
    }

    struct SourceTri
    {
        public Vector3 a, b, c;        // world-space positions
        public Vector3 na, nb, nc;     // world-space normals
        public Vector2 ua, ub, uc;     // UVs (passed through unchanged)
        public Material material;
        public StructurePart sourcePart;
    }

    void ApplySlice(GameObject sel)
    {
        // The source may be a BAKED part whose StructureParts have m_StructurePartAsset NULLED (resolved
        // at runtime via the part's own AddressableComponentLoader). Read the SP_Mat / blueprint GUIDs
        // from that existing ACL so we can re-wire the wedges. Falls back to the (possibly populated)
        // StructurePart fields for non-baked sources.
        string fallbackSpGuid = null, fallbackBpGuid = null;
        var srcAcl = sel.GetComponent<AddressableComponentLoader>();
        if (srcAcl != null && srcAcl.componentValues != null)
        {
            foreach (var cv in srcAcl.componentValues)
            {
                if (cv.field == "m_StructurePartAsset" && fallbackSpGuid == null) fallbackSpGuid = cv.address;
                if (cv.field == "m_BlueprintAsset"      && fallbackBpGuid == null) fallbackBpGuid = cv.address;
            }
        }

        // 1) Gather all source triangles in WORLD space (captures the live non-uniform scale/shear).
        var tris = new List<SourceTri>();
        foreach (var mf in sel.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = mf.sharedMesh;
            if (mesh == null || !mesh.isReadable) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            var sp = mf.GetComponentInParent<StructurePart>();
            var l2w = mf.transform.localToWorldMatrix;
            var n2w = mf.transform.localToWorldMatrix.inverse.transpose;

            var verts   = mesh.vertices;
            var normals = mesh.normals != null && mesh.normals.Length == verts.Length ? mesh.normals : null;
            var uvs     = mesh.uv != null && mesh.uv.Length == verts.Length ? mesh.uv : null;
            var mats    = mr != null ? mr.sharedMaterials : null;

            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                var idx = mesh.GetTriangles(sm);
                var mat = mats != null && mats.Length > 0 ? mats[Mathf.Min(sm, mats.Length - 1)] : null;
                for (int i = 0; i < idx.Length; i += 3)
                {
                    int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
                    tris.Add(new SourceTri
                    {
                        a = l2w.MultiplyPoint3x4(verts[i0]),
                        b = l2w.MultiplyPoint3x4(verts[i1]),
                        c = l2w.MultiplyPoint3x4(verts[i2]),
                        na = normals != null ? n2w.MultiplyVector(normals[i0]).normalized : Vector3.up,
                        nb = normals != null ? n2w.MultiplyVector(normals[i1]).normalized : Vector3.up,
                        nc = normals != null ? n2w.MultiplyVector(normals[i2]).normalized : Vector3.up,
                        ua = uvs != null ? uvs[i0] : Vector2.zero,
                        ub = uvs != null ? uvs[i1] : Vector2.zero,
                        uc = uvs != null ? uvs[i2] : Vector2.zero,
                        material = mat,
                        sourcePart = sp,
                    });
                }
            }
        }

        if (tris.Count == 0) { Debug.LogError("[Slice] No readable triangles in selection."); return; }

        // 2) Axis + center from the world bounds of the gathered geometry.
        Vector3 axis = m_Axis == Axis.X ? Vector3.right : m_Axis == Axis.Y ? Vector3.up : Vector3.forward;
        Bounds wb = new Bounds(tris[0].a, Vector3.zero);
        foreach (var t in tris) { wb.Encapsulate(t.a); wb.Encapsulate(t.b); wb.Encapsulate(t.c); }
        Vector3 center = wb.center;

        // Reference directions perpendicular to the axis for angle measurement.
        Vector3 refU = Vector3.Cross(axis, Mathf.Abs(Vector3.Dot(axis, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
        Vector3 refV = Vector3.Cross(axis, refU).normalized;

        int wedgeCount = WedgeCount;
        float wedgeRad = m_SegmentAngle * Mathf.Deg2Rad;
        float offsetRad = EffectiveOffset * Mathf.Deg2Rad;
        float halfGapRad = m_WedgeGap * Mathf.Deg2Rad * 0.5f;

        // 3) Assign each triangle to a wedge by its centroid angle (rotated by the start offset so wedge
        //    boundaries land on natural seams). Each wedge keeps a FULL render list (all its triangles)
        //    and a COLLIDER list that excludes triangles within halfGap of a boundary — so the render
        //    mesh stays solid (no visible holes) while the convex collider is inset from the seams and
        //    doesn't interpenetrate its neighbor (which blows the part apart on spawn).
        var wedgeRender = new List<SourceTri>[wedgeCount];
        var wedgeCollide = new List<SourceTri>[wedgeCount];
        for (int i = 0; i < wedgeCount; i++) { wedgeRender[i] = new List<SourceTri>(); wedgeCollide[i] = new List<SourceTri>(); }

        foreach (var t in tris)
        {
            Vector3 centroid = (t.a + t.b + t.c) / 3f;
            Vector3 rel = centroid - center;
            float angle = Mathf.Atan2(Vector3.Dot(rel, refV), Vector3.Dot(rel, refU)) - offsetRad;
            angle %= 2f * Mathf.PI;
            if (angle < 0) angle += 2f * Mathf.PI;
            int w = Mathf.Clamp((int)(angle / wedgeRad), 0, wedgeCount - 1);
            wedgeRender[w].Add(t);

            float within = angle - w * wedgeRad;
            if (halfGapRad <= 0f || (within >= halfGapRad && within <= wedgeRad - halfGapRad))
                wedgeCollide[w].Add(t);
        }

        // 4) Build the output. Map world verts into a scale-1 frame at sel's world pos/rotation.
        //    World verts already carry the full non-uniform stretch (gathered via localToWorldMatrix above),
        //    so mapping through a scale-1 inverse bakes that stretch directly into the wedge vertex positions.
        //    The wedge meshes are then saved at (1,1,1) scale — the game reads raw verts and rejects
        //    non-unit localScale, so the stretch must live in geometry. The slicer neutralizes sel's own
        //    lossyScale after building so the prefab root ends up at (1,1,1) and doesn't re-apply the stretch.
        Matrix4x4 worldToLocal = Matrix4x4.TRS(sel.transform.position, sel.transform.rotation, Vector3.one).inverse;
        Matrix4x4 normalW2L = worldToLocal.inverse.transpose;


        string saveFolder = ResolveSliceFolder(sel);
        string prefabFolder = saveFolder.Replace("/Meshes/SlicedWedges", ""); // same dir as source prefab
        EnsureFolder(saveFolder);

        // Purge any previously sliced mesh assets for this part so AssetDatabase.CreateAsset never
        // silently keeps a stale file. Old meshes with the same name would otherwise survive and the
        // new slice would reference them — meaning broken/unscaled geometry from an earlier run would
        // persist in-game even after a fresh slice.
        string selBaseName = sel.name;
        foreach (var assetPath in AssetDatabase.FindAssets("t:Mesh", new[] { saveFolder })
                     .Select(AssetDatabase.GUIDToAssetPath)
                     .Where(p => Path.GetFileName(p).StartsWith(selBaseName + "_Wedge_")))
            AssetDatabase.DeleteAsset(assetPath);

        Undo.SetCurrentGroupName("Slice Colliders From Geometry");
        int group = Undo.GetCurrentGroup();

        // Capture a representative source StructurePart (+ its EntityBlueprintComponent / MJC) and the
        // resolved SP_Mat / blueprint GUIDs BEFORE we delete the source nodes. Every wedge of one part
        // shares the same material/blueprint, so one representative is enough. We CopySerialized from
        // these snapshots so the wedges get correct salvage data even though the originals are gone.
        StructurePart capturedSp = tris.Select(t => t.sourcePart).FirstOrDefault(p => p != null);
        // Snapshot everything as plain C# values NOW before any deletion — Unity component refs become
        // fake-null after DestroyImmediate even though the C# object still exists.
        bool hasCapturedSp = capturedSp != null;
        string capturedSpGuid = hasCapturedSp ? (ResolveSpMatGuid(capturedSp) ?? fallbackSpGuid) : fallbackSpGuid;
        EntityBlueprintComponent capturedEbc = null;
        MandatoryJointContainer capturedMjc = null;
        string capturedBpGuid = fallbackBpGuid;
        bool hasCapturedEbc = false;
        bool hasCapturedMjc = false;
        if (hasCapturedSp)
        {
            capturedSp.TryGetComponent(out capturedEbc);
            capturedSp.TryGetComponent(out capturedMjc);
            hasCapturedEbc = capturedEbc != null;
            hasCapturedMjc = capturedMjc != null;
            if (hasCapturedEbc)
                capturedBpGuid = ResolveBlueprintGuid(capturedEbc) ?? fallbackBpGuid;
        }

        // Unpack ONLY sel's own prefab instance — never the ship root. If sel is nested inside a
        // larger prefab (e.g. Rocinante), climbing to GetNearestPrefabInstanceRoot would unpack the
        // entire ship and silently break the scene's prefab link. Instead, only unpack if sel itself
        // is the nearest instance root (i.e. it's a standalone prefab instance, not a nested child).
        if (PrefabUtility.IsPartOfPrefabInstance(sel))
        {
            var nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(sel);
            if (nearestRoot == sel)
                PrefabUtility.UnpackPrefabInstance(sel, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            // If nearestRoot != sel, sel is nested inside a parent prefab. We can't unpack just sel
            // without affecting the parent, so we leave the connection intact. SaveAsPrefabAsset
            // on plain scene objects still works after the deletions below because Unity allows
            // saving a sub-tree as a new prefab even when it was originally part of a larger instance.
        }

        // Remove ALL existing children of sel that aren't wedges — both previous wedge runs AND the
        // original source sub-parts (which may be nested prefabs with MeshFilters deep in their hierarchy).
        // Deleting top-level non-wedge children handles both cases cleanly.
        string prefix = "Wedge_";
        for (int i = sel.transform.childCount - 1; i >= 0; i--)
        {
            var child = sel.transform.GetChild(i);
            if (!child.name.StartsWith(prefix))
                Undo.DestroyObjectImmediate(child.gameObject);
        }
        // If sel itself carried a mesh, strip its renderer/collider too — the wedges replace it.
        if (sel.TryGetComponent<MeshFilter>(out var selMf))   Undo.DestroyObjectImmediate(selMf);
        if (sel.TryGetComponent<MeshRenderer>(out var selMr))  Undo.DestroyObjectImmediate(selMr);
        if (sel.TryGetComponent<MeshCollider>(out var selMc))  Undo.DestroyObjectImmediate(selMc);

        // Store (wedgeName, componentType, field, guid) — no live Component refs, since source nodes
        // are deleted and the scene objects will be destroyed after SaveAsPrefabAsset.
        var spMatRefs = new List<(string wedgeName, System.Type compType, string field, string guid)>();
        int built = 0;

        for (int w = 0; w < wedgeCount; w++)
        {
            var renderList = wedgeRender[w];
            if (renderList.Count == 0) continue;
            var collideList = wedgeCollide[w].Count > 0 ? wedgeCollide[w] : renderList; // fall back if gap emptied it

            // Full render mesh (no gap) so the wall looks solid; the joint system also reads this mesh.
            Mesh renderMesh = BuildWedgeMesh(renderList, worldToLocal, normalW2L, $"{sel.name}_Wedge_{w:D2}", saveFolder);
            // Gapped collider mesh, inset from the seams so neighbor convex hulls don't interpenetrate.
            Mesh collideMesh = collideList == renderList
                ? renderMesh
                : BuildWedgeMesh(collideList, worldToLocal, normalW2L, $"{sel.name}_Wedge_{w:D2}_col", saveFolder);

            // Dominant material among this wedge's triangles. (StructurePart data comes from the captured
            // representative snapshot — the live source nodes were deleted above.)
            Material mat = renderList.GroupBy(t => t.material).OrderByDescending(g => g.Count()).First().Key;

            var node = new GameObject($"{prefix}{w:D2}");
            Undo.RegisterCreatedObjectUndo(node, "Slice Colliders From Geometry");
            node.transform.SetParent(sel.transform, false);
            node.transform.localPosition = Vector3.zero;
            node.transform.localRotation = Quaternion.identity;
            node.transform.localScale    = Vector3.one;

            node.AddComponent<MeshFilter>().sharedMesh = renderMesh;
            node.AddComponent<MeshRenderer>().sharedMaterial = mat;
            var mc = node.AddComponent<MeshCollider>();
            mc.sharedMesh = collideMesh;
            mc.convex = true;

            if (m_PerWedgeStructurePart && hasCapturedSp)
            {
                var newSp = node.AddComponent<StructurePart>();
                // CopySerialized skipped — capturedSp is destroyed by this point (Unity fake-null).
                // The fields we care about (m_StructurePartAsset, m_BlueprintAsset) are resolved at
                // runtime via the ACL GUIDs below; other SP fields use safe component defaults.
                NullObjectField(newSp, "m_StructurePartAsset");
                NullObjectField(newSp, "m_ObjectInfoAssetOverride");
                if (!string.IsNullOrEmpty(capturedSpGuid))
                    spMatRefs.Add((node.name, typeof(StructurePart), "m_StructurePartAsset", capturedSpGuid));

                // EntityBlueprintComponent — required for the wedge to register as a salvageable entity.
                var newEbc = node.AddComponent<EntityBlueprintComponent>();
                // CopySerialized skipped — capturedEbc is destroyed (Unity fake-null) by this point.
                NullObjectField(newEbc, "m_BlueprintAsset");
                if (!string.IsNullOrEmpty(capturedBpGuid))
                    spMatRefs.Add((node.name, typeof(EntityBlueprintComponent), "m_BlueprintAsset", capturedBpGuid));

                // NOTE: the MandatoryJointContainer is intentionally NOT added per-wedge here. The game's
                // JointHelper.AreMandatoryJoints requires BOTH parts to resolve to the SAME MJC instance,
                // so a per-wedge copy would give each wedge a distinct instance and they would never joint
                // (the fly-apart bug). A single shared MJC is added to the parent (sel) after this loop.
            }

            built++;
        }

        // ACL wiring is deferred until after SaveAsPrefabAsset — the prefab asset gets its own copies
        // of all components, and the ACL must reference those (not the scene instances which are destroyed
        // afterward). We store the (field, guid) pairs now and apply them to the saved prefab below.

        // Single shared MandatoryJointContainer on the parent: JointHelper.AreMandatoryJoints requires
        // BOTH parts to resolve to the SAME MJC instance (GetComponentInParent ==), so ONE MJC on the
        // shared parent makes every wedge joint to every other. A per-wedge copy gives each wedge a
        // distinct instance and they never joint — the fly-apart bug. Always add it when slicing per-wedge
        // parts so the hollow ring holds together standalone (the GetComponentInParent==null guard avoids
        // stacking a second MJC if an ancestor already provides one).
        if (m_PerWedgeStructurePart && built > 0 && sel.GetComponentInParent<MandatoryJointContainer>() == null)
        {
            // Always add a fresh MJC — capturedMjc is destroyed (Unity fake-null) by this point.
            Undo.AddComponent<MandatoryJointContainer>(sel);
        }

        // Neutralize sel's lossyScale: the stretch is now baked into the wedge mesh vertices, so the
        // prefab root must be (1,1,1). If the stretch was on sel itself, set localScale to (1,1,1).
        // If it came from an ancestor, set sel.localScale to 1/parentLossyScale so sel.lossyScale = 1.
        // Either way the wedges (children at localScale 1) render at the baked size with no double-apply.
        Undo.RecordObject(sel.transform, "Slice Colliders From Geometry");
        Vector3 parentLossy = sel.transform.parent != null ? sel.transform.parent.lossyScale : Vector3.one;
        sel.transform.localScale = new Vector3(
            Mathf.Approximately(parentLossy.x, 0f) ? 1f : 1f / parentLossy.x,
            Mathf.Approximately(parentLossy.y, 0f) ? 1f : 1f / parentLossy.y,
            Mathf.Approximately(parentLossy.z, 0f) ? 1f : 1f / parentLossy.z);

        Undo.CollapseUndoOperations(group);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Save the sliced assembly as a NEW prefab so it builds into the ship (loose scene objects are
        // never built/loaded in-game). Distinct '_Sliced' name so it never overwrites the per-mesh or
        // single-mesh bakes of the same part.
        string baseName = StripSuffixes(sel.name);
        string slicedPath = $"{prefabFolder}/{baseName}_Sliced.prefab";
        if (System.IO.File.Exists(System.IO.Path.GetFullPath(slicedPath)))
            AssetDatabase.DeleteAsset(slicedPath);

        // Defensive guard — same rule as above: only unpack if sel itself is the nearest root.
        if (PrefabUtility.IsPartOfPrefabInstance(sel))
        {
            var nearestRoot = PrefabUtility.GetNearestPrefabInstanceRoot(sel);
            if (nearestRoot == sel)
                PrefabUtility.UnpackPrefabInstance(sel, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        }

        // Capture the root scale before sel may be destroyed below — used for the "needs bake" notice.
        Vector3 rootScale = sel.transform.localScale;

        var savedPrefab = PrefabUtility.SaveAsPrefabAsset(sel, slicedPath, out bool saveOk);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // Wire the ACL in the saved prefab asset using its own component instances (not the destroyed
        // scene ones). EditPrefabContentsScope gives us the prefab's real GameObjects so the component
        // references are valid and persist correctly on save.
        if (savedPrefab != null && saveOk && spMatRefs.Count > 0)
        {
            using (var scope = new PrefabUtility.EditPrefabContentsScope(slicedPath))
            {
                var root = scope.prefabContentsRoot;
                var acl = root.GetComponent<AddressableComponentLoader>();
                if (acl == null) acl = root.AddComponent<AddressableComponentLoader>();

                // Rebuild component value list using the prefab's own wedge components.
                // spMatRefs stores (wedgeName, compType, field, guid) — no live Component refs.
                var newValues = new List<AddressableComponentValue>();
                foreach (var (wedgeName, compType, field, guid) in spMatRefs)
                {
                    var node = root.transform.Find(wedgeName);
                    if (node == null) continue;
                    var comp = node.GetComponent(compType);
                    if (comp == null) continue;
                    newValues.Add(new AddressableComponentValue { component = comp, field = field, address = guid });
                }
                acl.componentValues = newValues;
                EditorUtility.SetDirty(acl);
            }
            AssetDatabase.SaveAssets();
        }

        // Register in the correct Addressables group (same ship group as the source prefab) so the
        // bundle build picks it up. Mirrors the CustomPartWizard's GuessAddressableGroup pattern.
        if (savedPrefab != null && saveOk)
        {
            var addrSettings = AddressableAssetSettingsDefaultObject.Settings;
            if (addrSettings != null)
            {
                string addrGroupName = ResolveAddressableGroup(slicedPath, addrSettings);
                var addrGroup = addrSettings.FindGroup(addrGroupName) ?? addrSettings.DefaultGroup;
                var addrGuid = AssetDatabase.AssetPathToGUID(slicedPath);
                var addrEntry = addrSettings.CreateOrMoveEntry(addrGuid, addrGroup);
                addrEntry.address = Path.GetFileNameWithoutExtension(slicedPath);
                addrSettings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, addrEntry, true);
                AssetDatabase.SaveAssets();
            }
        }

        // Replace the scene object with an instance of the new sliced prefab so the scene immediately
        // shows the sliced version (same parent/position/rotation), ready to build — no manual step.
        if (savedPrefab != null && saveOk)
        {
            var parent  = sel.transform.parent;
            var lPos    = sel.transform.localPosition;
            var lRot    = sel.transform.localRotation;
            var lScale  = sel.transform.localScale;
            int sibIdx  = sel.transform.GetSiblingIndex();

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(savedPrefab);
            Undo.RegisterCreatedObjectUndo(inst, "Slice Colliders From Geometry");
            inst.transform.SetParent(parent, false);
            inst.transform.localPosition = lPos;
            inst.transform.localRotation = lRot;
            inst.transform.localScale    = lScale;
            inst.transform.SetSiblingIndex(sibIdx);

            Undo.DestroyObjectImmediate(sel);
            Selection.activeGameObject = inst;
        }

        Debug.Log($"[Slice] Built {built} wedge(s) around {m_Axis}; saved prefab '{slicedPath}' (ok={saveOk}). Scale {rootScale} baked into wedge geometry; root reset to (1,1,1).");

        EditorGUIUtility.PingObject(savedPrefab);
    }

    static string StripSuffixes(string name)
    {
        // Drop Unity's " (N)" instance suffix and a trailing _Baked/_BakedSingle so the sliced prefab
        // name stays clean (e.g. "PRF_..._Baked (1)" → "PRF_...").
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"_Baked(Single)?$", "");
        return name;
    }

    // Builds a wedge mesh from world-space triangles, mapped into the target local frame, and saves it.
    static Mesh BuildWedgeMesh(List<SourceTri> list, Matrix4x4 worldToLocal, Matrix4x4 normalW2L, string name, string saveFolder)
    {
        var verts = new List<Vector3>(list.Count * 3);
        var norms = new List<Vector3>(list.Count * 3);
        var uvs = new List<Vector2>(list.Count * 3);
        var indices = new List<int>(list.Count * 3);
        foreach (var t in list)
        {
            int baseIdx = verts.Count;
            verts.Add(worldToLocal.MultiplyPoint3x4(t.a));
            verts.Add(worldToLocal.MultiplyPoint3x4(t.b));
            verts.Add(worldToLocal.MultiplyPoint3x4(t.c));
            norms.Add(normalW2L.MultiplyVector(t.na).normalized);
            norms.Add(normalW2L.MultiplyVector(t.nb).normalized);
            norms.Add(normalW2L.MultiplyVector(t.nc).normalized);
            uvs.Add(t.ua); uvs.Add(t.ub); uvs.Add(t.uc);
            indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        }

        var mesh = new Mesh { name = name };
        mesh.indexFormat = verts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        mesh.UploadMeshData(false); // CPU-readable for jointing
        AssetDatabase.CreateAsset(mesh, $"{saveFolder}/{name}.asset");
        return mesh;
    }

    // ── Helpers (mirror AddressableBaker's GUID resolution) ───────────────────────────────────────

    static string ResolveSpMatGuid(StructurePart sp)
    {
        var so = new SerializedObject(sp);
        var prop = so.FindProperty("m_StructurePartAsset");
        var asset = prop != null ? prop.objectReferenceValue : null;
        return asset != null ? AddressableBaker.ResolveAssetGuidByName(asset.name) : null;
    }

    static string ResolveBlueprintGuid(Component ebc)
    {
        var so = new SerializedObject(ebc);
        var prop = so.FindProperty("m_BlueprintAsset");
        var asset = prop != null ? prop.objectReferenceValue : null;
        return asset != null ? AddressableBaker.ResolveAssetGuidByName(asset.name) : null;
    }

    static void NullObjectField(Component c, string fieldName)
    {
        var so = new SerializedObject(c);
        var prop = so.FindProperty(fieldName);
        if (prop != null && prop.propertyType == SerializedPropertyType.ObjectReference)
        {
            prop.objectReferenceValue = null;
            so.ApplyModifiedProperties();
        }
    }

    static string ResolveAddressableGroup(string assetPath, AddressableAssetSettings settings)
    {
        const string marker = "_CustomShips/";
        int idx = assetPath.IndexOf(marker, System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        string after = assetPath.Substring(idx + marker.Length);
        int slash = after.IndexOf('/');
        string shipName = slash >= 0 ? after.Substring(0, slash) : after;
        return !string.IsNullOrEmpty(shipName) && settings.FindGroup(shipName) != null ? shipName : null;
    }

    static string ResolveSliceFolder(GameObject sel)
    {
        var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(sel);
        string baseFolder = !string.IsNullOrEmpty(prefabPath)
            ? Path.GetDirectoryName(prefabPath).Replace('\\', '/')
            : "Assets/_CustomShips";
        return $"{baseFolder}/Meshes/SlicedWedges";
    }

    static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        string name = Path.GetFileName(folder);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }
}
