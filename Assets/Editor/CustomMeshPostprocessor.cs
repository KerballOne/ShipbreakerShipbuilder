using UnityEngine;
using UnityEditor;
using System.IO;

public class CustomMeshPostprocessor : AssetPostprocessor
{
    void OnPreprocessModel()
    {
        if (!assetPath.Contains("/_CustomShips/"))
            return;

        var importer = (ModelImporter)assetImporter;
        // Name materials after the Blender material, not the mesh/texture name
        if (importer.materialName != ModelImporterMaterialName.BasedOnMaterialName)
        {
            importer.materialName = ModelImporterMaterialName.BasedOnMaterialName;
        }
    }

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

        var matFolder = GetMaterialFolder(assetPath);
        if (!AssetDatabase.IsValidFolder(matFolder))
        {
            var parent = matFolder.Substring(0, matFolder.LastIndexOf('/'));
            var leaf   = matFolder.Substring(matFolder.LastIndexOf('/') + 1);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        // Mat is named after the texture set (sidecar key), not the Blender material name.
        // This ensures all parts sharing the same texture atlas reuse one material.
        var texFolder  = GetTextureFolder(assetPath);
        var sidecar    = ReadSidecar(assetPath);
        var texSetName = material.name; // fallback
        System.Collections.Generic.Dictionary<string, string> texMap = null;
        if (sidecar != null)
            foreach (var kv in sidecar) { texSetName = kv.Key; texMap = kv.Value; break; }

        var matPath  = $"{matFolder}/{texSetName}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (existing != null)
            return existing;

        var newMat = new Material(shader) { name = texSetName };
        newMat.SetFloat("_SurfaceType", 0);
        newMat.SetFloat("_TransmissionEnable", 0);
        newMat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;

        WireTextures(newMat, texFolder, assetPath);

        AssetDatabase.CreateAsset(newMat, matPath);
        return newMat;
    }

    // Returns Assets/.../Materials/ — sibling of whichever folder the FBX lives in.
    public static string GetMaterialFolder(string fbxAssetPath) => GetSiblingFolder(fbxAssetPath, "Materials");

    // Returns Assets/.../Textures/ — sibling of whichever folder the FBX lives in.
    public static string GetTextureFolder(string fbxAssetPath) => GetSiblingFolder(fbxAssetPath, "Textures");

    static string GetSiblingFolder(string fbxAssetPath, string folderName)
    {
        // Use forward-slash string ops to avoid Path.GetDirectoryName cross-platform issues with Unity paths.
        var normalized = fbxAssetPath.Replace('\\', '/');
        var fbxDir     = normalized.Substring(0, normalized.LastIndexOf('/'));       // strip filename
        var parentDir  = fbxDir.Substring(0, fbxDir.LastIndexOf('/'));              // strip Models/
        return parentDir + "/" + folderName;
    }

    void OnPreprocessTexture()
    {
        if (!assetPath.Contains("/_CustomShips/"))
            return;

        var importer = (TextureImporter)assetImporter;
        var name     = Path.GetFileNameWithoutExtension(assetPath);

        if (name.EndsWith("_Normal"))
        {
            importer.textureType    = TextureImporterType.NormalMap;
            importer.sRGBTexture    = false;
        }
        else if (name.EndsWith("_Metallic") || name.EndsWith("_Roughness") ||
                 name.EndsWith("_MaskMap")  || name.EndsWith("_AO"))
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
        }
        // BaseColor and everything else stays Default + sRGB (Unity's default)
    }

    // suffix → HDRP material property
    static readonly System.Collections.Generic.Dictionary<string, string> SuffixToProperty =
        new System.Collections.Generic.Dictionary<string, string>
        {
            { "BaseColor", "_BaseColorMap" },
            { "Normal",    "_NormalMap"    },
            { "MaskMap",   "_MaskMap"      },
        };

    static void WireTextures(Material mat, string texFolder, string fbxAssetPath)
    {
        var sidecar = ReadSidecar(fbxAssetPath);
        System.Collections.Generic.Dictionary<string, string> texMap = null;
        if (sidecar != null) sidecar.TryGetValue(mat.name, out texMap);

        foreach (var kv in SuffixToProperty)
        {
            string fname = null;
            texMap?.TryGetValue(kv.Key, out fname);
            if (fname != null)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{texFolder}/{fname}");
                if (tex != null) mat.SetTexture(kv.Value, tex);
            }
        }
    }

    public static System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>
        ReadSidecar(string fbxAssetPath)
    {
        var normalized = fbxAssetPath.Replace('\\', '/');
        var stem       = normalized.Substring(normalized.LastIndexOf('/') + 1);
        stem           = stem.Substring(0, stem.LastIndexOf('.'));
        var dir        = normalized.Substring(0, normalized.LastIndexOf('/'));
        var sidecarPath = $"{dir}/{stem}.textures.json";

        if (!File.Exists(Path.GetFullPath(sidecarPath)))
            return null;

        var json = File.ReadAllText(Path.GetFullPath(sidecarPath));
        return Newtonsoft.Json.JsonConvert.DeserializeObject<
            System.Collections.Generic.Dictionary<string,
            System.Collections.Generic.Dictionary<string, string>>>(json);
    }
}
