#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

/// <summary>
/// Bakes an addressable game part into a self-contained prefab: extracts meshes, materials and
/// textures into permanent project assets and recreates per-mesh StructureParts referencing the
/// source's SP_Material (StructurePartAsset). No AddressableLoader / no runtime mesh dependency.
///
/// Reuses the proven extraction approach from AddressableRendering (mesh-by-hash, texture blit to
/// PNG, Fake shader material). Materials use the Fake/_Lynx shader which ModdedShipLoader swaps to
/// the real game shader at load time — same as loader-based parts.
/// </summary>
public static class AddressableBaker
{
    // Caches valid for one bake batch; cleared by callers between batches if desired.
    static readonly Dictionary<string, Mesh> s_MeshCache = new Dictionary<string, Mesh>();

    public class SubMeshInfo
    {
        public string path;            // hierarchy path from source root
        public string nodeName;
        public string sourceSPMatName; // auto-detected SP material asset name from source StructurePart
        public string sourceSPMatGuid; // resolved addressable GUID (may be null if unresolved)
        public string overrideGuid;    // optional user override GUID (null/empty = keep source)
    }

    /// <summary>Headlessly loads an addressable GameObject (optionally a childPath within it).</summary>
    public static Task<GameObject> LoadAddressableAsync(string guid, string childPath = "")
    {
        var tcs = new TaskCompletionSource<GameObject>();
        var locOp = Addressables.LoadResourceLocationsAsync(guid, typeof(GameObject));
        locOp.Completed += locRes =>
        {
            if (locRes.Status != AsyncOperationStatus.Succeeded || locRes.Result == null || locRes.Result.Count == 0)
            {
                tcs.SetResult(null);
                return;
            }
            var loadOp = Addressables.LoadAssetAsync<GameObject>(locRes.Result[0]);
            loadOp.Completed += res =>
            {
                if (res.Status != AsyncOperationStatus.Succeeded || res.Result == null)
                {
                    tcs.SetResult(null);
                    return;
                }
                GameObject result = res.Result;
                if (!string.IsNullOrEmpty(childPath))
                {
                    var found = result.transform.Find(childPath);
                    result = found != null ? found.gameObject : null;
                }
                tcs.SetResult(result);
            };
        };
        return tcs.Task;
    }

    /// <summary>Collects every node that carries a StructurePart, for the override UI.</summary>
    public static List<SubMeshInfo> CollectSubMeshes(GameObject source)
    {
        var list = new List<SubMeshInfo>();
        CollectSubMeshesRecursive(source.transform, "", list);
        return list;
    }

    static void CollectSubMeshesRecursive(Transform t, string path, List<SubMeshInfo> list)
    {
        if (t.TryGetComponent<StructurePart>(out var sp))
        {
            var asset = ReadStructurePartAsset(sp);
            list.Add(new SubMeshInfo
            {
                path            = path,
                nodeName        = t.name,
                sourceSPMatName = asset != null ? asset.name : null,
                sourceSPMatGuid = asset != null ? ResolveAssetGuidByName(asset.name) : null,
            });
        }
        foreach (Transform child in t)
            CollectSubMeshesRecursive(child, path == "" ? child.name : path + "/" + child.name, list);
    }

    /// <summary>
    /// Bakes the source's children under outParent (preserving sub-structure). SP_Material references
    /// can't be stored directly (the game StructurePartAsset lives in the runtime bundle → broken PPtr
    /// on save), so each baked StructurePart records its SP_Mat by addressable GUID into spMatRefs,
    /// which the caller writes onto an AddressableComponentLoader for the mod to resolve at load.
    /// overridesByPath optionally maps a node path → a replacement SP_Mat GUID.
    /// </summary>
    public static void BakeTree(
        GameObject source,
        Transform outParent,
        string assetFolder,
        List<(Component component, string field, string guid)> spMatRefs,
        Dictionary<string, string> overridesByPath = null)
    {
        EnsureFolder(assetFolder);

        bool rootHasContent = source.GetComponent<MeshFilter>() != null
                           || source.GetComponent<MeshCollider>() != null
                           || source.GetComponent<StructurePart>() != null;
        if (rootHasContent)
            BakeOnto(source.transform, outParent, "", assetFolder, spMatRefs, overridesByPath);

        foreach (Transform child in source.transform)
            BakeNode(child, outParent, child.name, assetFolder, spMatRefs, overridesByPath);
    }

    static void BakeNode(
        Transform inT,
        Transform outParent,
        string path,
        string assetFolder,
        List<(Component, string, string)> spMatRefs,
        Dictionary<string, string> overridesByPath)
    {
        var node = new GameObject(inT.name);
        node.transform.SetParent(outParent, false);
        node.transform.localPosition = inT.localPosition;
        node.transform.localRotation = inT.localRotation;
        node.transform.localScale    = inT.localScale;

        BakeOnto(inT, node.transform, path, assetFolder, spMatRefs, overridesByPath);

        foreach (Transform child in inT)
            BakeNode(child, node.transform, path == "" ? child.name : path + "/" + child.name, assetFolder, spMatRefs, overridesByPath);
    }

    /// <summary>Bakes the components of inT onto an already-created outNode (no children).</summary>
    static void BakeOnto(
        Transform inT,
        Transform outNode,
        string path,
        string assetFolder,
        List<(Component, string, string)> spMatRefs,
        Dictionary<string, string> overridesByPath)
    {
        var node = outNode.gameObject;

        // Mesh + renderer.
        if (inT.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
        {
            var bakedMesh = CloneMeshByHash(mf.sharedMesh, assetFolder);
            node.AddComponent<MeshFilter>().sharedMesh = bakedMesh;

            if (inT.TryGetComponent<MeshRenderer>(out var mr))
            {
                var newMr = node.AddComponent<MeshRenderer>();
                newMr.sharedMaterials = mr.sharedMaterials.Select(m => m != null ? CloneMaterial(m, assetFolder) : null).ToArray();
            }

            // Collider: reuse the baked mesh if the source collider shared the render mesh.
            // Force Convex=true: a concave MeshCollider does NOT register with the physics/salvage
            // system (the part renders hollow — player passes through, no scan/targeting/salvage).
            if (inT.TryGetComponent<MeshCollider>(out var srcMc) && srcMc.sharedMesh != null)
            {
                var newMc = node.AddComponent<MeshCollider>();
                newMc.sharedMesh = srcMc.sharedMesh == mf.sharedMesh ? bakedMesh : CloneMeshByHash(srcMc.sharedMesh, assetFolder);
                newMc.convex = true;
            }
        }
        else if (inT.TryGetComponent<MeshCollider>(out var soloMc) && soloMc.sharedMesh != null)
        {
            var newMc = node.AddComponent<MeshCollider>();
            newMc.sharedMesh = CloneMeshByHash(soloMc.sharedMesh, assetFolder);
            newMc.convex = true;
        }

        // Room volumes copy cleanly.
        if (inT.TryGetComponent<RoomSubVolumeDefinition>(out var rsv))
            EditorUtility.CopySerialized(rsv, node.AddComponent<RoomSubVolumeDefinition>());
        if (inT.TryGetComponent<RoomOpeningDefinition>(out var rop))
            EditorUtility.CopySerialized(rop, node.AddComponent<RoomOpeningDefinition>());

        // StructurePart: recreate so this sub-mesh is independently salvageable. The source's
        // m_StructurePartAsset / m_ObjectInfoAssetOverride point into the runtime bundle and CANNOT be
        // stored as direct references (broken PPtr on save). So we copy the component, NULL those refs,
        // and record the SP_Mat GUID for the AddressableComponentLoader to resolve at load.
        //
        // A functioning salvageable part ALSO requires an EntityBlueprintComponent wired to a blueprint
        // asset (m_BlueprintAsset) — without it the part has no mass/label/salvage destination and no
        // physics registration. We recreate that too, resolving its blueprint GUID the same way.
        if (inT.TryGetComponent<StructurePart>(out var srcSp))
        {
            var newSp = node.AddComponent<StructurePart>();
            EditorUtility.CopySerialized(srcSp, newSp);

            // SP_Mat GUID: override (if any) else resolve the source asset's GUID by name.
            string spMatGuid = null;
            if (overridesByPath != null && overridesByPath.TryGetValue(path, out var ov) && !string.IsNullOrEmpty(ov))
                spMatGuid = ov;
            else
            {
                var srcAsset = ReadStructurePartAsset(srcSp);
                if (srcAsset != null) spMatGuid = ResolveAssetGuidByName(srcAsset.name);
            }

            NullObjectField(newSp, "m_StructurePartAsset");
            NullObjectField(newSp, "m_ObjectInfoAssetOverride");

            if (!string.IsNullOrEmpty(spMatGuid))
                spMatRefs.Add((newSp, "m_StructurePartAsset", spMatGuid));
            else if (ReadStructurePartAsset(srcSp) != null)
                Debug.LogWarning($"[AddressableBaker] Could not resolve SP_Mat GUID for '{inT.name}' (asset '{ReadStructurePartAsset(srcSp)?.name}') — part will have no material.");

            // EntityBlueprintComponent — required for the part to be a salvageable entity.
            if (inT.TryGetComponent<EntityBlueprintComponent>(out var srcEbc))
            {
                var newEbc = node.AddComponent<EntityBlueprintComponent>();
                EditorUtility.CopySerialized(srcEbc, newEbc);

                var blueprintAsset = ReadObjectField(srcEbc, "m_BlueprintAsset");
                string blueprintGuid = blueprintAsset != null ? ResolveAssetGuidByName(blueprintAsset.name) : null;

                NullObjectField(newEbc, "m_BlueprintAsset");

                if (!string.IsNullOrEmpty(blueprintGuid))
                    spMatRefs.Add((newEbc, "m_BlueprintAsset", blueprintGuid));
                else if (blueprintAsset != null)
                    Debug.LogWarning($"[AddressableBaker] Could not resolve blueprint GUID for '{inT.name}' (asset '{blueprintAsset.name}') — part may not register as salvageable.");
            }
        }

        // Preserve MandatoryJointContainer marker so island/joint splitting still works at runtime.
        if (inT.TryGetComponent<MandatoryJointContainer>(out var mjc))
            EditorUtility.CopySerialized(mjc, node.AddComponent<MandatoryJointContainer>());
    }

    // ── SP material reflection (m_StructurePartAsset is private SerializeField) ─────────────────

    static StructurePartAsset ReadStructurePartAsset(StructurePart sp)
    {
        var so = new SerializedObject(sp);
        var prop = so.FindProperty("m_StructurePartAsset");
        return prop != null ? prop.objectReferenceValue as StructurePartAsset : null;
    }

    static Object ReadObjectField(Component c, string fieldName)
    {
        var so = new SerializedObject(c);
        var prop = so.FindProperty(fieldName);
        return prop != null && prop.propertyType == SerializedPropertyType.ObjectReference ? prop.objectReferenceValue : null;
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

    /// <summary>Finds the addressable GUID for an asset by matching its name against known_assets
    /// (LoadGameAssets.knownAssetMap: guid → asset path). Returns null if not found.</summary>
    public static string ResolveAssetGuidByName(string assetName)
    {
        if (string.IsNullOrEmpty(assetName) || LoadGameAssets.knownAssetMap == null) return null;
        // assetName may have a "(Clone)" suffix from runtime instantiation; strip it.
        string clean = assetName.Replace("(Clone)", "").Trim();
        string needle = "/" + clean + ".asset";
        foreach (var kv in LoadGameAssets.knownAssetMap)
            if (kv.Value.EndsWith(needle, System.StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return null;
    }

    // ── Mesh cloning by content hash (mirrors AddressableRendering.CloneMeshTree) ───────────────

    static Mesh CloneMeshByHash(Mesh source, string folder)
    {
        var dataArray = Mesh.AcquireReadOnlyMeshData(source);
        var hash = "";
        for (int i = 0; i < dataArray.Length; i++)
        {
            if (dataArray[i].vertexCount == 0) continue;
            var data = dataArray[i];
            hash += Hash128.Compute(ref data).ToString();
        }
        dataArray.Dispose();

        if (s_MeshCache.TryGetValue(hash, out var cached) && cached != null)
            return cached;

        string assetPath = $"{folder}/{hash}.asset";
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (existing == null)
        {
            var copy = Object.Instantiate(source);
            // The joint/mass system reads vertices/bounds at runtime — keep CPU-readable.
            copy.UploadMeshData(false);
            AssetDatabase.CreateAsset(copy, assetPath);
            existing = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        }
        s_MeshCache[hash] = existing;
        return existing;
    }

    // ── Material + texture cloning (mirrors AddressableRendering.CloneMaterial/DuplicateTexture) ─

    static Material CloneMaterial(Material material, string folder)
    {
        string matPath = $"{folder}/{material.ComputeCRC()}.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (existing != null) return existing;

        var validTextures = new Dictionary<string, string>();
        foreach (var texName in material.GetTexturePropertyNames())
        {
            if (material.GetTexture(texName) is Texture2D orgTexture && orgTexture != null)
            {
                var dup = DuplicateTexture(orgTexture);
                string pngPath = $"{folder}/{dup.imageContentsHash}.png";
                if (!File.Exists(Path.GetFullPath(pngPath)))
                    File.WriteAllBytes(Path.GetFullPath(pngPath), dup.EncodeToPNG());
                validTextures[texName] = dup.imageContentsHash.ToString();
            }
        }
        AssetDatabase.Refresh();

        var textureCache = new Dictionary<string, Texture2D>();
        foreach (var kv in validTextures)
            if (!textureCache.ContainsKey(kv.Key))
                textureCache[kv.Key] = AssetDatabase.LoadAssetAtPath<Texture2D>($"{folder}/{kv.Value}.png");

        Material tempMaterial;
        switch (material.shader.name)
        {
            case "_Lynx/Surface/HDRP/Lit":
                tempMaterial = new Material(Shader.Find("Fake/_Lynx/Surface/HDRP/Lit"));
                tempMaterial.CopyPropertiesFromMaterial(material);
                break;
            default:
                tempMaterial = new Material(Shader.Find("HDRP/Lit"));
                Debug.LogWarning($"[AddressableBaker] Unknown shader {material.shader.name}");
                break;
        }
        foreach (var kv in validTextures)
            tempMaterial.SetTexture(kv.Key, textureCache[kv.Key]);

        tempMaterial.enableInstancing = true;
        AssetDatabase.CreateAsset(tempMaterial, matPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return AssetDatabase.LoadAssetAtPath<Material>(matPath);
    }

    static Texture2D DuplicateTexture(Texture2D source)
    {
        var renderTex = RenderTexture.GetTemporary(source.width, source.height, 0,
            RenderTextureFormat.Default, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, renderTex);
        var previous = RenderTexture.active;
        RenderTexture.active = renderTex;
        var readable = new Texture2D(source.width, source.height);
        readable.ReadPixels(new Rect(0, 0, renderTex.width, renderTex.height), 0, 0);
        readable.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(renderTex);
        return readable;
    }

    // ── Folder helpers ──────────────────────────────────────────────────────────────────────────

    public static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        string name = Path.GetFileName(folder);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }

    public static void ClearCaches() => s_MeshCache.Clear();
}
#endif
