using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Select 2+ GameObjects with MeshFilters (or a parent containing several) →
/// "Combine Meshes". Merges all their meshes into a single new Mesh asset, with vertices
/// expressed relative to the first selected object's world transform, and puts it on a new
/// GameObject that adopts that same world transform — so the combined object sits exactly
/// where the originals were. Source objects are left untouched (disabled, not deleted) so the
/// merge can be inspected/undone by hand. The new GameObject is named after the mesh asset
/// filename the user chooses in the save dialog, so scene name and asset name always match.
///
/// One submesh per unique source material is preserved (mergeSubMeshes: false), so the combined
/// object keeps multi-material rendering via multiple MeshRenderer materials in the same slot
/// order as submeshes.
/// </summary>
public static class CombineMeshesContextMenu
{
    const string GOMenuPath = "GameObject/Shipbuilder/Combine Meshes";

    static bool Validate() =>
        Selection.gameObjects
            .SelectMany(go => go.GetComponentsInChildren<MeshFilter>(true))
            .Count(mf => mf.sharedMesh != null) > 1;

    [MenuItem(GOMenuPath, true)]
    static bool ValidateGO() => Validate();

    // Unity invokes a GameObject/ menu item once PER SELECTED OBJECT, not once for the whole
    // selection — calling Execute() directly here would pop one save dialog per object. Defer to
    // a single delayed call instead, so N invocations this frame collapse into one batch + one dialog.
    [MenuItem(GOMenuPath, false)]
    static void ExecuteGO() => ScheduleExecute();

    static bool s_ExecuteScheduled;

    static void ScheduleExecute()
    {
        if (s_ExecuteScheduled) return;
        s_ExecuteScheduled = true;
        EditorApplication.delayCall += () =>
        {
            s_ExecuteScheduled = false;
            Execute();
        };
    }

    static void Execute()
    {
        var meshFilters = Selection.gameObjects
            .SelectMany(go => go.GetComponentsInChildren<MeshFilter>(true))
            .Where(mf => mf.sharedMesh != null)
            .Distinct()
            .ToList();

        if (meshFilters.Count < 2)
        {
            EditorUtility.DisplayDialog("Combine Meshes", "Select at least 2 objects with meshes.", "OK");
            return;
        }

        // Express every source vertex relative to the first source's world transform, not raw
        // world space — the new GameObject then adopts that same transform, landing exactly
        // where the first source object was instead of at the parent's origin.
        var originTransform = meshFilters[0].transform;
        var worldToOrigin = originTransform.worldToLocalMatrix;

        // Collect unique materials across all sources, in first-seen order — this becomes the
        // combined object's material list, and each source triangle's submesh index maps into it.
        var materials = new List<Material>();
        var combineByMaterial = new Dictionary<Material, List<CombineInstance>>();

        foreach (var mf in meshFilters)
        {
            var renderer = mf.GetComponent<MeshRenderer>();
            var sharedMats = renderer != null ? renderer.sharedMaterials : new Material[0];
            var mesh = mf.sharedMesh;

            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var mat = sub < sharedMats.Length ? sharedMats[sub] : null;
                if (!combineByMaterial.TryGetValue(mat, out var list))
                {
                    list = new List<CombineInstance>();
                    combineByMaterial[mat] = list;
                    materials.Add(mat);
                }

                list.Add(new CombineInstance
                {
                    mesh = mesh,
                    subMeshIndex = sub,
                    transform = worldToOrigin * mf.transform.localToWorldMatrix
                });
            }
        }

        // First pass: combine each material's triangles into one submesh (mergeSubMeshes: true
        // within the group). Second pass: combine those per-material meshes together without
        // merging submeshes, so the final mesh has exactly one submesh per material.
        var perMaterialMeshes = new List<CombineInstance>();
        foreach (var mat in materials)
        {
            var combine = combineByMaterial[mat].ToArray();
            var subMesh = new Mesh();
            subMesh.CombineMeshes(combine, true, true);
            perMaterialMeshes.Add(new CombineInstance { mesh = subMesh, transform = Matrix4x4.identity });
        }

        var finalMesh = new Mesh();
        finalMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        finalMesh.CombineMeshes(perMaterialMeshes.ToArray(), false, false);
        finalMesh.RecalculateBounds();

        var savePath = EditorUtility.SaveFilePanelInProject(
            "Save Combined Mesh", originTransform.name + "_Combined", "asset",
            "Choose where to save the combined mesh asset.");
        if (string.IsNullOrEmpty(savePath))
            return;

        AssetDatabase.CreateAsset(finalMesh, savePath);
        AssetDatabase.SaveAssets();

        var combinedName = Path.GetFileNameWithoutExtension(savePath);
        var combinedGo = new GameObject(combinedName);
        Undo.RegisterCreatedObjectUndo(combinedGo, "Combine Meshes");
        combinedGo.transform.SetParent(originTransform.parent, false);
        combinedGo.transform.SetPositionAndRotation(originTransform.position, originTransform.rotation);
        combinedGo.transform.localScale = originTransform.lossyScale;

        var newMf = combinedGo.AddComponent<MeshFilter>();
        var newRenderer = combinedGo.AddComponent<MeshRenderer>();
        newRenderer.sharedMaterials = materials.ToArray();
        newMf.sharedMesh = finalMesh;

        foreach (var mf in meshFilters)
        {
            Undo.RecordObject(mf.gameObject, "Combine Meshes");
            mf.gameObject.SetActive(false);
        }

        Selection.activeGameObject = combinedGo;
        Debug.Log($"[CombineMeshes] Combined {meshFilters.Count} mesh(es) into '{savePath}' " +
            $"({materials.Count} material(s)). Source objects disabled, not deleted.");
    }
}
