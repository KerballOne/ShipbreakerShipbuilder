using UnityEngine;
using UnityEditor;
using System.IO;

public class CustomMeshPostprocessor : AssetPostprocessor
{
    Material OnAssignMaterialModel(Material material, Renderer renderer)
    {
        if (!assetPath.Contains("/_CustomShips/"))
            return null;

        var shader = Shader.Find("Fake/_Lynx/Surface/HDRP/Lit");
        if (shader == null)
        {
            Debug.LogWarning("[CustomMeshPostprocessor] Shader 'Fake/_Lynx/Surface/HDRP/Lit' not found");
            return null;
        }

        var dir = Path.GetDirectoryName(assetPath);
        var matPath = Path.Combine(dir, material.name + ".mat").Replace('\\', '/');

        var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (existing != null)
            return existing;

        var newMat = new Material(shader) { name = material.name };
        // Opaque surface type; prevents the material defaulting to Transparent
        newMat.SetFloat("_SurfaceType", 0);
        newMat.SetFloat("_TransmissionEnable", 0);
        newMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;
        AssetDatabase.CreateAsset(newMat, matPath);
        return newMat;
    }
}
