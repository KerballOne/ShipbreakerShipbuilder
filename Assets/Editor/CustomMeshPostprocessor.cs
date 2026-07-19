using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Collections.Generic;

public class CustomMeshPostprocessor : AssetPostprocessor
{
    // Auto-recenter-on-reimport (see AutoRecenterOnReimport.cs). Uses Recenter's default
    // compensatePosition: false, so it only shifts mesh vertices back to origin and never touches
    // transform.position — safe even when multiple GameObjects share one mesh asset.
    const bool AutoRecenterOnReimportEnabled = true;

    // Fires once per import batch (e.g. once per Blender re-export detected by Unity's file
    // watcher). For every reimported FBX under /_CustomShips/, re-runs Recenter Mesh Origin on
    // every GameObject (open scenes + prefab assets) whose MeshFilter references one of that
    // FBX's sub-meshes — so parts stay centered without a manual re-run after each re-export.
    static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
        string[] movedAssets, string[] movedFromAssetPaths)
    {
        if (!AutoRecenterOnReimportEnabled)
            return;

        var fbxPaths = importedAssets
            .Where(p => p.Contains("/_CustomShips/") && p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (fbxPaths.Count == 0)
            return;

        foreach (var fbxPath in fbxPaths)
        {
            Debug.Log($"[CustomMeshPostprocessor] Detected FBX reimport: '{fbxPath}' — scheduling auto-recenter.");
            // Defer: mutating scenes/prefabs via PrefabUtility.SaveAsPrefabAsset while still inside
            // the asset-import pipeline is unsafe. Run after this import batch fully completes.
            var path = fbxPath;
            EditorApplication.delayCall += () => AutoRecenterOnReimport.Run(path);
        }
    }

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

        // Always readable, set here (during preprocess, before the reimport completes) so
        // AutoRecenterOnReimport never needs to trigger a second SaveAndReimport() of its own —
        // that would re-enter OnPostprocessAllAssets and reschedule itself.
        if (!importer.isReadable)
            importer.isReadable = true;
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

        // Mat is named after the texture set (sidecar key matching this Blender material),
        // not the Blender material name itself. This ensures all parts sharing the same
        // texture atlas reuse one material.
        var texFolder  = GetTextureFolder(assetPath);
        var sidecar    = ReadSidecar(assetPath);
        var texSetName = material.name; // fallback
        System.Collections.Generic.Dictionary<string, string> texMap = null;
        if (sidecar != null && sidecar.TryGetValue(material.name, out texMap))
            texSetName = material.name;

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
