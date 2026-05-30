using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class BakeTransformScaleWindow : EditorWindow
{
    const string FallbackSaveFolder = "Assets/_CustomShips/Meshes/BakedScale";

    [MenuItem("Shipbreaker/Shipbuilder Tools/Bake Transform Scale", priority = 20)]
    static void Open() => GetWindow<BakeTransformScaleWindow>("Bake Transform Scale");

    int _cachedAffectedCount = 0;
    GameObject[] _cachedSelection = null;

    void OnEnable() => Selection.selectionChanged += OnSelectionChanged;
    void OnDisable() => Selection.selectionChanged -= OnSelectionChanged;

    void OnSelectionChanged()
    {
        _cachedSelection = null; // invalidate cache
        Repaint();
    }

    void OnGUI()
    {
        var sel = Selection.gameObjects;

        // Recompute only when selection changes, not every repaint.
        if (_cachedSelection == null || !System.Linq.Enumerable.SequenceEqual(_cachedSelection, sel))
        {
            _cachedSelection = sel;
            _cachedAffectedCount = 0;
            foreach (var go in sel)
                _cachedAffectedCount += CountAffectedByLossyScale(go.transform);
        }

        EditorGUILayout.HelpBox(
            "Bakes non-unit localScale into mesh geometry for all selected objects and their descendants. " +
            "Resets localScale to (1,1,1) after baking. Required for StructurePart joints to function correctly at non-unit scale.",
            MessageType.Info);

        EditorGUILayout.Space();

        if (sel.Length == 0)
            EditorGUILayout.HelpBox("Select one or more objects in the hierarchy first.", MessageType.Warning);
        else
            EditorGUILayout.LabelField($"Transforms with non-unit scale found: {_cachedAffectedCount}");

        using (new EditorGUI.DisabledScope(sel.Length == 0 || _cachedAffectedCount == 0))
        {
            if (GUILayout.Button("Bake Scale"))
                Execute(sel);
        }

        if (sel.Length > 0 && _cachedAffectedCount == 0)
            EditorGUILayout.HelpBox("No non-unit scale found in selection. Nothing to bake.", MessageType.None);
    }

    static string ResolveSaveFolder(GameObject root)
    {
        // Walk up to find the nearest prefab instance root and use its asset folder.
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

    static void Execute(GameObject[] roots)
    {
        Undo.SetCurrentGroupName("Bake Transform Scale");
        int group = Undo.GetCurrentGroup();

        var restoreLog = new System.Text.StringBuilder();
        restoreLog.AppendLine("[BakeTransformScale] Restore map (GameObject path → original mesh asset path):");

        int bakedCount = 0;
        foreach (var root in roots)
        {
            string saveFolder = ResolveSaveFolder(root);
            EnsureFolderExists(saveFolder);
            // Pass 1: bake meshes using lossyScale (accumulated world scale from root down).
            bakedCount += BakeMeshesInHierarchy(root.transform, saveFolder, restoreLog);
            // Pass 2: reset all localScales to (1,1,1) now that meshes are baked.
            ResetScalesInHierarchy(root.transform);
        }

        Debug.Log(restoreLog.ToString());

        Undo.CollapseUndoOperations(group);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[BakeTransformScale] Baked {bakedCount} mesh(es).");
        EditorUtility.DisplayDialog("Bake Complete",
            $"Baked scale into {bakedCount} mesh(es).\n" +
            $"LocalScale reset to (1,1,1) on all affected transforms.", "OK");
    }

    static int CountAffectedByLossyScale(Transform t)
    {
        int count = 0;
        if (Vector3.Distance(t.lossyScale, Vector3.one) > 1e-5f &&
            (t.GetComponent<MeshFilter>() != null || t.GetComponent<MeshCollider>() != null))
            count++;
        foreach (Transform child in t)
            count += CountAffectedByLossyScale(child);
        return count;
    }

    // Pass 1: bake each mesh using the transform's lossyScale (world-accumulated scale).
    static int BakeMeshesInHierarchy(Transform t, string saveFolder, System.Text.StringBuilder restoreLog)
    {
        int count = 0;
        foreach (Transform child in t)
            count += BakeMeshesInHierarchy(child, saveFolder, restoreLog);

        Vector3 bakeScale = t.lossyScale;
        if (Vector3.Distance(bakeScale, Vector3.one) < 1e-5f)
            return count;

        var mf = t.GetComponent<MeshFilter>();
        var mc = t.GetComponent<MeshCollider>();
        if (mf == null && mc == null)
            return count;

        string goPath = GetHierarchyPath(t);
        Mesh preBakeMFMesh = mf != null ? mf.sharedMesh : null;

        if (mf != null && mf.sharedMesh != null)
        {
            string originalPath = AssetDatabase.GetAssetPath(mf.sharedMesh);
            restoreLog.AppendLine($"  MeshFilter  {goPath}  ←  {originalPath}");
            var bakedMesh = BakeMesh(mf.sharedMesh, bakeScale, mf.sharedMesh.name, saveFolder);
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
                bakedCollider = BakeMesh(colliderSource, bakeScale, colliderSource.name + "_col", saveFolder);
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

    // Pass 2: reset localScale on every transform in the hierarchy.
    static void ResetScalesInHierarchy(Transform t)
    {
        foreach (Transform child in t)
            ResetScalesInHierarchy(child);

        if (Vector3.Distance(t.localScale, Vector3.one) > 1e-5f)
        {
            Undo.RecordObject(t, "Bake Transform Scale");
            t.localScale = Vector3.one;
        }
    }

    static Mesh BakeMesh(Mesh source, Vector3 scale, string baseName, string saveFolder)
    {
        // Reuse existing baked asset if it already exists for this source+scale combination.
        string safeName = $"{baseName}_s{scale.x:F3}x{scale.y:F3}x{scale.z:F3}".Replace('.', 'd');
        string assetPath = $"{saveFolder}/{safeName}.asset";

        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existing != null) return existing;

        var baked = Object.Instantiate(source);
        baked.name = safeName;

        var verts = source.vertices;
        for (int i = 0; i < verts.Length; i++)
            verts[i] = Vector3.Scale(verts[i], scale);
        baked.vertices = verts;

        // Under non-uniform scale, normals must be transformed by the inverse scale and renormalized.
        var normals = source.normals;
        if (normals != null && normals.Length > 0)
        {
            Vector3 invScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
            for (int i = 0; i < normals.Length; i++)
                normals[i] = Vector3.Scale(normals[i], invScale).normalized;
            baked.normals = normals;
        }

        baked.RecalculateBounds();
        baked.RecalculateTangents();

        // The joint system reads mesh.vertices/bounds at runtime, which requires the mesh to be
        // CPU-readable. Asset meshes default to non-readable; force it on so jointing works.
        baked.UploadMeshData(false);

        AssetDatabase.CreateAsset(baked, assetPath);
        return baked;
    }
}
