using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes non-unit transform scale into mesh geometry for a part and its descendants, then resets
/// localScale to (1,1,1) and scales child localPositions so multi-mesh layouts stay correct. Required
/// because the game reads raw mesh vertices/bounds for joints + mass and rejects non-unit scale.
/// Uniform scale is always safe; non-uniform scale skews descendant meshes that are rotated relative
/// to the scaled transform (caller should warn). Invoked from the AddressableComponentLoader inspector
/// button and the Transform component context menu.
/// </summary>
public static class TransformScaleBaker
{
    const string FallbackSaveFolder = "Assets/_CustomShips/Meshes/BakedScale";

    /// <summary>When true, BakeScale logs detailed per-node transforms, matrices and position deltas.</summary>
    public static bool VerboseLogging = false;

    /// <summary>True if the transform's localScale differs from unit (1,1,1) on any axis.</summary>
    public static bool IsNonUnitScale(Transform t)
    {
        return Vector3.Distance(t.localScale, Vector3.one) > 1e-5f;
    }

    public static bool IsUniformScale(Transform t)
    {
        var s = t.localScale;
        return Mathf.Abs(s.x - s.y) < 1e-5f && Mathf.Abs(s.y - s.z) < 1e-5f;
    }

    /// <summary>
    /// Non-uniform scale skews any descendant mesh that is ROTATED relative to this transform, because
    /// the bake scales vertices in the mesh's local axes while Unity applies parent scale in the
    /// parent's axes — these only agree when scale is uniform OR the child isn't rotated. Returns true
    /// for the risky non-uniform + rotated-mesh-descendant case so callers can warn (not block).
    /// </summary>
    public static bool HasNonUniformScaleWithRotatedChildren(Transform t)
    {
        if (IsUniformScale(t)) return false;
        return HasRotatedMeshDescendant(t);
    }

    static bool HasRotatedMeshDescendant(Transform t)
    {
        foreach (Transform child in t)
        {
            bool rotated = Quaternion.Angle(child.localRotation, Quaternion.identity) > 1e-3f;
            bool hasMesh = child.GetComponent<MeshFilter>() != null || child.GetComponent<MeshCollider>() != null;
            if (rotated && hasMesh) return true;
            if (HasRotatedMeshDescendant(child)) return true;
        }
        return false;
    }

    /// <summary>Counts descendant transforms with a mesh and non-unit lossyScale (what will be baked).</summary>
    public static int CountAffected(Transform t)
    {
        int count = 0;
        if (Vector3.Distance(t.lossyScale, Vector3.one) > 1e-5f &&
            (t.GetComponent<MeshFilter>() != null || t.GetComponent<MeshCollider>() != null))
            count++;
        foreach (Transform child in t)
            count += CountAffected(child);
        return count;
    }

    /// <summary>
    /// Bakes scale for one root object and its descendants. The scaled root's localScale S is applied
    /// in the ROOT's local space; to bake it into rotated sub-meshes without skew, each mesh vertex is
    /// transformed by R⁻¹·diag(S)·R where R is the mesh node's rotation relative to the scaled root.
    /// Child positions are likewise scaled in the root's frame. After baking, all localScales are 1.
    /// </summary>
    public static int BakeScale(GameObject root)
    {
        Undo.SetCurrentGroupName("Bake Transform Scale");
        int group = Undo.GetCurrentGroup();

        var restoreLog = new System.Text.StringBuilder();
        restoreLog.AppendLine("[BakeTransformScale] Restore map (GameObject path → original mesh asset path):");

        string saveFolder = ResolveSaveFolder(root);
        EnsureFolderExists(saveFolder);

        Vector3 S = root.transform.localScale;
        Quaternion rootRot = root.transform.rotation;

        if (VerboseLogging)
            Debug.Log($"[BakeScale] ===== '{root.name}'  S={S}  rootWorldRot={rootRot.eulerAngles}  rootWorldPos={root.transform.position} =====");

        // Snapshot the content's world-bounds center NOW (original meshes, original scale), before any
        // baking, so we can re-anchor at the end and keep the assembly visually in place.
        Bounds? preBounds = ComputeWorldBounds(root.transform);

        // Pass 1: bake meshes. R is each node's rotation relative to the scaled root.
        int bakedCount = BakeMeshesInHierarchy(root.transform, rootRot, S, saveFolder, restoreLog);

        // Pass 2: reposition every descendant so its position relative to the root is scaled by S in
        // the root's axes. Capture ORIGINAL world positions first; then reset ALL scales to 1 (so
        // setting world positions yields clean localPositions with no later scale-induced drift); then
        // apply the scaled world positions.
        var originalWorldPos = new Dictionary<Transform, Vector3>();
        CollectWorldPositions(root.transform, originalWorldPos);

        Vector3 rootPos = root.transform.position;
        Quaternion rootRotPos = root.transform.rotation;

        ResetScalesToOne(root.transform);

        foreach (var kv in originalWorldPos)
        {
            if (kv.Key == root.transform) continue;
            Vector3 relInRoot = Quaternion.Inverse(rootRotPos) * (kv.Value - rootPos);
            Vector3 scaledWorld = rootPos + rootRotPos * Vector3.Scale(relInRoot, S);
            Undo.RecordObject(kv.Key, "Bake Transform Scale");
            kv.Key.position = scaledWorld;

            if (VerboseLogging)
                Debug.Log(
                    $"[BakeScale][POS] '{kv.Key.name}'\n" +
                    $"  worldOld={kv.Value}  relInRoot={relInRoot}  scaledRelInRoot={Vector3.Scale(relInRoot, S)}\n" +
                    $"  worldNew={scaledWorld}  localNew={kv.Key.localPosition}");
        }

        // Re-anchor: shift the root so the post-scale bounds center matches the pre-scale center.
        if (preBounds.HasValue)
        {
            Bounds postBounds = ComputeWorldBounds(root.transform) ?? preBounds.Value;
            Vector3 delta = preBounds.Value.center - postBounds.center;
            if (delta.sqrMagnitude > 1e-8f)
            {
                Undo.RecordObject(root.transform, "Bake Transform Scale");
                root.transform.position += delta;
                if (VerboseLogging)
                    Debug.Log($"[BakeScale][ANCHOR] preCenter={preBounds.Value.center} postCenter={postBounds.center} delta={delta} → root moved to {root.transform.position}");
            }
        }

        Debug.Log(restoreLog.ToString());

        Undo.CollapseUndoOperations(group);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[BakeTransformScale] Baked {bakedCount} mesh(es) on '{root.name}'.");
        return bakedCount;
    }

    static string ResolveSaveFolder(GameObject root)
    {
        var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
        if (!string.IsNullOrEmpty(prefabPath))
        {
            string shipFolder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            return $"{shipFolder}/Meshes/BakedScale";
        }
        return FallbackSaveFolder;
    }

    static void EnsureFolderExists(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        string name = Path.GetFileName(folder);
        EnsureFolderExists(parent);
        AssetDatabase.CreateFolder(parent, name);
    }

    // Pass 1: bake each mesh through M = R⁻¹·diag(S)·R, where R is the node's rotation relative to the
    // scaled root. For uniform S this reduces to plain vertex scaling; for non-uniform S it elongates
    // rotated meshes along the root's axes without skew.
    static int BakeMeshesInHierarchy(Transform t, Quaternion rootRot, Vector3 S, string saveFolder, System.Text.StringBuilder restoreLog)
    {
        int count = 0;
        foreach (Transform child in t)
            count += BakeMeshesInHierarchy(child, rootRot, S, saveFolder, restoreLog);

        if (Vector3.Distance(S, Vector3.one) < 1e-5f)
            return count;

        var mf = t.GetComponent<MeshFilter>();
        var mc = t.GetComponent<MeshCollider>();
        if (mf == null && mc == null)
            return count;

        // Rotation of this node relative to the scaled root.
        Quaternion R = Quaternion.Inverse(rootRot) * t.rotation;
        // Vertex bake matrix: into root space (R), apply non-uniform scale (diag S), back to local (R⁻¹).
        Matrix4x4 M = Matrix4x4.Rotate(Quaternion.Inverse(R)) * Matrix4x4.Scale(S) * Matrix4x4.Rotate(R);
        string scaleTag = $"{S.x:F3}x{S.y:F3}x{S.z:F3}_r{R.eulerAngles.x:F0}_{R.eulerAngles.y:F0}_{R.eulerAngles.z:F0}";

        string goPath = GetHierarchyPath(t);
        Mesh preBakeMFMesh = mf != null ? mf.sharedMesh : null;

        if (VerboseLogging)
        {
            var srcMesh = mf != null ? mf.sharedMesh : (mc != null ? mc.sharedMesh : null);
            string boundsStr = srcMesh != null ? $"size={srcMesh.bounds.size}" : "no-mesh";
            Debug.Log(
                $"[BakeScale][MESH] '{t.name}'\n" +
                $"  worldRot={t.rotation.eulerAngles}  R(rel-root)={R.eulerAngles}\n" +
                $"  S={S}  srcBounds {boundsStr}\n" +
                $"  M row0={M.GetRow(0)} row1={M.GetRow(1)} row2={M.GetRow(2)}");
        }

        if (mf != null && mf.sharedMesh != null)
        {
            string originalPath = AssetDatabase.GetAssetPath(mf.sharedMesh);
            restoreLog.AppendLine($"  MeshFilter  {goPath}  ←  {originalPath}");
            var bakedMesh = BakeMesh(mf.sharedMesh, M, mf.sharedMesh.name, scaleTag, saveFolder);
            Undo.RecordObject(mf, "Bake Transform Scale");
            mf.sharedMesh = bakedMesh;
            count++;
        }

        if (mc != null && mc.sharedMesh != null)
        {
            Mesh colliderSource = mc.sharedMesh;
            Mesh bakedCollider;
            if (mf != null && colliderSource == preBakeMFMesh)
            {
                bakedCollider = mf.sharedMesh; // reuse just-baked mesh
            }
            else
            {
                string originalPath = AssetDatabase.GetAssetPath(colliderSource);
                restoreLog.AppendLine($"  MeshCollider {goPath}  ←  {originalPath}");
                bakedCollider = BakeMesh(colliderSource, M, colliderSource.name + "_col", scaleTag, saveFolder);
                count++;
            }
            Undo.RecordObject(mc, "Bake Transform Scale");
            mc.sharedMesh = bakedCollider;
        }

        return count;
    }

    static string GetHierarchyPath(Transform t)
    {
        var parts = new List<string>();
        while (t != null) { parts.Insert(0, t.name); t = t.parent; }
        return string.Join("/", parts);
    }

    static void CollectWorldPositions(Transform t, Dictionary<Transform, Vector3> map)
    {
        map[t] = t.position;
        foreach (Transform child in t)
            CollectWorldPositions(child, map);
    }

    /// <summary>Combined world-space renderer bounds of the subtree, or null if it has no renderers.</summary>
    static Bounds? ComputeWorldBounds(Transform t)
    {
        Bounds? result = null;
        foreach (var r in t.GetComponentsInChildren<Renderer>(true))
        {
            if (result.HasValue) { var b = result.Value; b.Encapsulate(r.bounds); result = b; }
            else result = r.bounds;
        }
        return result;
    }

    // Pass 2b: reset localScale on the root and every descendant to (1,1,1). Mesh geometry already
    // carries the scale; positions were fixed in pass 2a.
    static void ResetScalesToOne(Transform t)
    {
        if (Vector3.Distance(t.localScale, Vector3.one) > 1e-5f)
        {
            Undo.RecordObject(t, "Bake Transform Scale");
            t.localScale = Vector3.one;
        }
        foreach (Transform child in t)
            ResetScalesToOne(child);
    }

    static Mesh BakeMesh(Mesh source, Matrix4x4 M, string baseName, string scaleTag, string saveFolder)
    {
        // Reuse existing baked asset if it already exists for this source + transform combination.
        string safeName = $"{baseName}_{scaleTag}".Replace('.', 'd').Replace('-', 'm');
        string assetPath = $"{saveFolder}/{safeName}.asset";

        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existing != null) return existing;

        var baked = Object.Instantiate(source);
        baked.name = safeName;

        var verts = source.vertices;
        for (int i = 0; i < verts.Length; i++)
            verts[i] = M.MultiplyPoint3x4(verts[i]);
        baked.vertices = verts;

        // Normals transform by the inverse-transpose of the linear part, then renormalize.
        var normals = source.normals;
        if (normals != null && normals.Length > 0)
        {
            Matrix4x4 nrm = M.inverse.transpose;
            for (int i = 0; i < normals.Length; i++)
                normals[i] = nrm.MultiplyVector(normals[i]).normalized;
            baked.normals = normals;
        }

        baked.RecalculateBounds();
        baked.RecalculateTangents();

        if (VerboseLogging)
            Debug.Log($"[BakeScale][BAKED] '{baseName}'  srcBounds={source.bounds.size}  →  bakedBounds={baked.bounds.size}  ({verts.Length} verts)");

        // The joint system reads mesh.vertices/bounds at runtime, which requires the mesh to be
        // CPU-readable. Asset meshes default to non-readable; force it on so jointing works.
        baked.UploadMeshData(false);

        AssetDatabase.CreateAsset(baked, assetPath);
        return baked;
    }
}
