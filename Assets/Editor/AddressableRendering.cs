using BBI.Unity.Game;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.SceneManagement;

public class AddressableRendering : MonoBehaviour
{
    public static List<RenderableMapping> rooms = new List<RenderableMapping>();
    public static List<RenderableMapping> roomOverlaps = new List<RenderableMapping>();
    public static List<JointGizmoData> jointData = new List<JointGizmoData>();
    public static List<JointGizmoData> bakedJointData = new List<JointGizmoData>();

    static bool isUpdating = false;
    static int currentRecursiveDepth = 0;

    static List<GameObject> fakes = new List<GameObject>();

    static Dictionary<string, string> prefabToHardpoint = new Dictionary<string, string>();

    static Dictionary<string, Mesh> meshCache = new Dictionary<string, Mesh>();

    public class JointGizmoData
    {
        public enum JointType { Root, Standard, CutPoint }
        public Matrix4x4 worldMatrix;
        public Bounds bounds;
        public JointType type;
    }

    public static void ClearView()
    {
        isUpdating = false;

        foreach (var fakePrefab in fakes)
            if (fakePrefab != null)
                DestroyImmediate(fakePrefab.gameObject);
        fakes.Clear();

        // Catch orphaned fakes parented to AddressableLoader wrappers
        foreach (var root in GetActiveRootObjects())
        {
            foreach (var loader in root.GetComponentsInChildren<BBI.Unity.Game.AddressableLoader>())
            {
                for (int i = loader.transform.childCount - 1; i >= 0; i--)
                {
                    var child = loader.transform.GetChild(i);
                    if (child.GetComponent<SelectAddressableParent>() != null ||
                        child.GetComponent<FakePrefabDisplay>() != null)
                        DestroyImmediate(child.gameObject);
                }
            }

            // Destroy leaked _temp wrappers left at scene root by interrupted TryCacheAsset calls
            if (root.name == "_temp")
                DestroyImmediate(root);
        }

        rooms.Clear();
        roomOverlaps.Clear();
        jointData.Clear();
        bakedJointData.Clear();
    }

    public static void ForceResetUpdateFlag() => isUpdating = false;

    static GameObject[] GetActiveRootObjects()
    {
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        return stage != null
            ? stage.scene.GetRootGameObjects()
            : SceneManager.GetActiveScene().GetRootGameObjects();
    }

    public async static void UpdateViewList()
    {
        if (isUpdating) return;
        isUpdating = true;

        ClearView();

        try
        {
            bool needToRefreshCache = false;
            List<AddressableLoader> addressablesToLoad = new List<AddressableLoader>();
            List<HardPoint> hardPoints = new List<HardPoint>();

            var rootObjects = GetActiveRootObjects();

            foreach (var rootGameObject in rootObjects)
            {
                currentRecursiveDepth = 0;
                if (rootGameObject.TryGetComponent<BBI.Unity.Game.ModuleDefinition>(out var moduleDefinition))
                {
                    foreach (var addressable in rootGameObject.GetComponentsInChildren<BBI.Unity.Game.AddressableLoader>())
                    {
                        addressablesToLoad.Add(addressable);

                        if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID($"Assets/EditorCache/{addressable.assetGUID ?? addressable.refs[0]}.prefab")))
                        {
                            needToRefreshCache = true;
                        }
                    }

                    foreach (var hardpoint in rootGameObject.GetComponentsInChildren<HardPoint>())
                    {
                        hardPoints.Add(hardpoint);

                        if (hardpoint.AssetRef != null && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID($"Assets/EditorCache/{hardpoint.AssetRef.AssetGUID}.prefab")))
                        {
                            needToRefreshCache = true;
                        }
                    }

                    foreach (var room in rootGameObject.GetComponentsInChildren<RoomSubVolumeDefinition>())
                    {
                        rooms.Add(RenderableMapping.RoomMapping(room.transform, false));
                    }

                    foreach (var roomOverlap in rootGameObject.GetComponentsInChildren<RoomOpeningDefinition>())
                    {
                        roomOverlaps.Add(RenderableMapping.RoomMapping(roomOverlap.transform, false));
                    }

                    CollectBakedStructureParts(rootGameObject.transform);

                    if (!needToRefreshCache || LoadGameAssets.CheckHandlesValid())
                    {
                        foreach (var addressable in addressablesToLoad)
                        {
                            try { await LoadAddress(addressable.assetGUID ?? addressable.refs[0], addressable.transform, false, addressable.childPath, addressable.disabledChildren); }
                            catch (System.Exception ex) { Debug.LogError($"[AddressableRendering] Skipped {addressable.assetGUID}: {ex.Message}"); }
                        }

                        foreach (var hardpoint in hardPoints)
                        {
                            try
                            {
                                var assetGUID = await LoadHardpoint(hardpoint);
                                if (!string.IsNullOrEmpty(assetGUID))
                                    await LoadAddress(assetGUID, hardpoint.transform, true);
                            }
                            catch (System.Exception ex) { Debug.LogError($"[AddressableRendering] Skipped hardpoint: {ex.Message}"); }
                        }
                    }
                    else
                    {
                        Debug.Log("Please load the catalogs");
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
        }
        finally
        {
            isUpdating = false;
        }
    }

    async static System.Threading.Tasks.Task<string> LoadHardpoint(HardPoint hardPoint)
    {
        if (hardPoint.AssetRef == null) return string.Empty;
        if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(hardPoint.AssetRef.AssetGUID)))
        {
            var moduleEntry = AssetDatabase.LoadAssetAtPath<ModuleListAsset>(AssetDatabase.GUIDToAssetPath(hardPoint.AssetRef.AssetGUID)).Data.ModuleEntryContainer.Data.FirstOrDefault();
            if (moduleEntry == null) return "";
            if (moduleEntry.GetType() == typeof(ModuleEntryDefinition))
            {
                prefabToHardpoint[((ModuleEntryDefinition)moduleEntry).ModuleDefRef.AssetGUID] = hardPoint.AssetRef.AssetGUID;
                return ((ModuleEntryDefinition)moduleEntry).ModuleDefRef.AssetGUID;
            }
        }
        else if (System.IO.File.Exists($"{Application.dataPath}/EditorCache/{hardPoint.AssetRef.AssetGUID}.prefab"))
        {
            return hardPoint.AssetRef.AssetGUID;
        }
        else
        {
            var guid = await LoadHardpointGuidFromModuleListAsset(hardPoint.AssetRef.AssetGUID);
            if (!string.IsNullOrEmpty(guid))
                prefabToHardpoint[guid] = hardPoint.AssetRef.AssetGUID;
            return guid;
        }

        throw new System.Exception("LoadHardpoint");
    }

    async static System.Threading.Tasks.Task<string> LoadHardpointGuidFromModuleListAsset(string moduleListAssetGuid)
    {
        if(string.IsNullOrEmpty(moduleListAssetGuid))
        {
            Debug.LogError($"Missing HardPoint GUID");
            return "";
        }

        UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<ModuleListAsset> res;
        try
        {
            res = Addressables.LoadAssetAsync<ModuleListAsset>(moduleListAssetGuid);
        }
        catch(System.Exception ex)
        {
            Debug.LogError($"Failed to load GUID {moduleListAssetGuid}");
            Debug.LogError(ex);
            return "";
        }
        await res.Task;

        if (res.IsValid())
        {
            var moduleEntry = res.Result.Data.ModuleEntryContainer.Data.FirstOrDefault();
            if (moduleEntry == null) return "";
            if (moduleEntry.GetType() == typeof(ModuleEntryDefinition))
            {
                return ((ModuleEntryDefinition)moduleEntry).ModuleDefRef.AssetGUID;
            }
            else if (moduleEntry.GetType() == typeof(ModuleEntryList))
            {
                return await LoadHardpointGuidFromModuleListAsset(((ModuleEntryList)moduleEntry).ModuleListRef.AssetGUID);
            }
            else if (moduleEntry.GetType() == typeof(ModuleEntryEmpty))
            {
                return "";
            }
        }

        Debug.LogWarning($"[AddressableRendering] Could not resolve hardpoint GUID {moduleListAssetGuid} (handle valid: {res.IsValid()})");
        return "";
    }

    async static System.Threading.Tasks.Task<GameObject> LoadAddress(string addressRef, Transform parent, bool isHardpoint, string assetPath = "", List<string> disabledChildren = null)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(addressRef));

        bool treatAsHardpointPosition = isHardpoint;

        // Only treat it as a hardpoint if it's loading from the game files, for some reason?
        if (prefab)
        {
            treatAsHardpointPosition = false;
        }
        else
        {
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/EditorCache/{addressRef + assetPath.Replace("/", "_")}.prefab");
        }

        if (!prefab)
        {
            var locations = Addressables.LoadResourceLocationsAsync(addressRef, typeof(GameObject));
            await locations.Task;

            if (locations.Status != UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded || locations.Result == null || locations.Result.Count == 0)
            {
                Debug.LogError($"[AddressableRendering] No GameObject location for {addressRef}");
                return null;
            }

            var res = Addressables.LoadAssetAsync<GameObject>(locations.Result[0]);
            await res.Task;

            if (res.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && res.Result != null)
            {
                GameObject result;

                Vector3 cachedPosition;

                if (string.IsNullOrEmpty(assetPath))
                {
                    result = res.Result;
                    cachedPosition = result.transform.localPosition;
                }
                else
                {
                    result = res.Result.transform.Find(assetPath)?.gameObject;

                    if (result == null)
                    {
                        throw new System.Exception($"Can't find {assetPath} in {addressRef}.");
                    }

                    cachedPosition = result.transform.localPosition;
                    result.transform.localPosition = Vector3.zero;
                }

                if (result.TryGetComponent<BBI.Unity.Game.AddressableLoader>(out var loader))
                {
                    if(currentRecursiveDepth++ < GameRenderWindow.maxLoopDepth)
                    {
                        await LoadAddress(loader.assetGUID ?? loader.refs[0], res.Result.transform, false, loader.childPath, loader.disabledChildren);
                    }
                    currentRecursiveDepth--;
                }

                await TryCacheAsset(addressRef, result, treatAsHardpointPosition, addressRef + assetPath.Replace("/", "_"));

                // Collect StructurePart data from the loaded game asset (result.localPosition is 0 here,
                // correctly treating the result as anchored at the parent wrapper's world position).
                if (parent != null && !EditorUtility.IsPersistent(parent.gameObject))
                    CollectStructurePartData(result.transform, parent.localToWorldMatrix, true);

                result.transform.localPosition = cachedPosition;

                foreach (var hardpoint in result.GetComponentsInChildren<HardPoint>())
                {
                    var assetGUID = await LoadHardpoint(hardpoint);
                    if (!string.IsNullOrEmpty(assetGUID))
                    {
                        if(currentRecursiveDepth++ < GameRenderWindow.maxLoopDepth)
                        {
                            await LoadAddress(assetGUID, hardpoint.transform, treatAsHardpointPosition);
                        }
                        currentRecursiveDepth--;
                    }
                }

                return res.Result;
            }
            else
            {
                Debug.LogError($"[AddressableRendering] Failed to load {addressRef}: status={res.Status} exception={res.OperationException?.Message}");
                return null;
            }
        }
        else if (parent != null && !EditorUtility.IsPersistent(parent.gameObject))
        {
            var temp = Instantiate(prefab, parent);
            temp.name = !string.IsNullOrEmpty(assetPath) ? assetPath : addressRef;
            temp.hideFlags = HideFlags.DontSave;

            if (treatAsHardpointPosition)
                temp.transform.GetChild(0).localPosition = Vector3.zero;

            foreach (var room in temp.GetComponentsInChildren<RoomSubVolumeDefinition>())
            {
                rooms.Add(RenderableMapping.RoomMapping(room.transform, treatAsHardpointPosition));
            }

            foreach (var roomOverlap in temp.GetComponentsInChildren<RoomOpeningDefinition>())
            {
                roomOverlaps.Add(RenderableMapping.RoomMapping(roomOverlap.transform, treatAsHardpointPosition));
            }

            // Collect joint data from FakeStructurePart components baked into the EditorCache prefab.
            foreach (var fsp in temp.GetComponentsInChildren<FakeStructurePart>(true))
            {
                jointData.Add(new JointGizmoData
                {
                    worldMatrix = fsp.transform.localToWorldMatrix,
                    bounds = fsp.localColliderBounds,
                    type = (JointGizmoData.JointType)fsp.type
                });
            }

            if (disabledChildren != null)
            {
                foreach (var disabledChild in disabledChildren)
                {
                    GameObject foundChild = null;
                    IEnumerable<Transform> children = temp.transform.GetChild(0).Cast<Transform>();
                    var cList = children.ToList();
                    foreach (var childPathPart in disabledChild.Split('/'))
                    {
                        foundChild = children.Where(c => c.name.StartsWith(childPathPart)).FirstOrDefault()?.gameObject;
                        if(foundChild == null)
                        {
                            break;
                        }
                        children = foundChild.transform.Cast<Transform>();
                    }
                    if (foundChild != null)
                        GameObject.DestroyImmediate(foundChild);
                }
            }

            temp.AddComponent<FakePrefabDisplay>();
            fakes.Add(temp);

            if (isHardpoint)
            {
                foreach (var hardpoint in temp.GetComponentsInChildren<FakeHardpoint>())
                {
                    if (hardpoint == null) continue;
                    if (currentRecursiveDepth++ < GameRenderWindow.maxLoopDepth)
                        await LoadAddress(hardpoint.AssetGUID, hardpoint.transform, true);
                    currentRecursiveDepth--;
                }
            }
        }

        return prefab;
    }

    // Collects baked StructureParts (e.g. InvisibleJoints) directly in the scene hierarchy,
    // skipping subtrees rooted at AddressableLoader since those are covered by EditorCache FakeStructureParts.
    static void CollectBakedStructureParts(Transform t)
    {
        if (t.TryGetComponent<BBI.Unity.Game.AddressableLoader>(out _)) return;

        if (t.TryGetComponent<InvisibleJointMarker>(out _) && t.TryGetComponent<StructurePart>(out _))
        {
            Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 0.05f);
            if (t.TryGetComponent<MeshCollider>(out var mc) && mc.sharedMesh != null)
                bounds = mc.sharedMesh.bounds;
            else if (t.TryGetComponent<BoxCollider>(out var bc))
                bounds = new Bounds(bc.center, bc.size);

            bakedJointData.Add(new JointGizmoData
            {
                worldMatrix = t.localToWorldMatrix,
                bounds = bounds,
                type = JointGizmoData.JointType.Standard
            });
        }

        foreach (Transform child in t)
            CollectBakedStructureParts(child);
    }

    // Traverses the game prefab hierarchy (not in scene) and collects StructurePart data into jointData.
    // parentWorld is the local-to-world matrix of the ancestor scene transform (AddressableLoader wrapper).
    static void CollectStructurePartData(Transform t, Matrix4x4 parentWorld, bool isRoot)
    {
        var world = parentWorld * Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);

        if (t.TryGetComponent<StructurePart>(out _))
        {
            Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 0.1f);
            if (t.TryGetComponent<MeshCollider>(out var mc) && mc.sharedMesh != null)
                bounds = mc.sharedMesh.bounds;
            else if (t.TryGetComponent<BoxCollider>(out var bc))
                bounds = new Bounds(bc.center, bc.size);

            var jtype = isRoot ? JointGizmoData.JointType.Root
                      : t.name.IndexOf("cutpoint", System.StringComparison.OrdinalIgnoreCase) >= 0
                        ? JointGizmoData.JointType.CutPoint
                        : JointGizmoData.JointType.Standard;

            jointData.Add(new JointGizmoData { worldMatrix = world, bounds = bounds, type = jtype });
        }

        foreach (Transform child in t)
            CollectStructurePartData(child, world, false);
    }

    static int count = 0;
    async static System.Threading.Tasks.Task TryCacheAsset(string address, GameObject obj, bool isHardpoint, string cachePath)
    {
        count = 0;
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(address)) ?? AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/EditorCache/{cachePath}.prefab");

        if (!prefab)
        {
            // Use a temporary wrapper so CloneMeshTree's root node becomes the prefab root
            var tempWrapper = new GameObject("_temp");
            await CloneMeshTree(address, obj.transform, tempWrapper.transform, cachePath, true);

            if (tempWrapper.transform.childCount > 0)
            {
                var prefabRoot = tempWrapper.transform.GetChild(0).gameObject;
                prefabRoot.transform.SetParent(null);
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, $"Assets/EditorCache/{cachePath}.prefab");
                DestroyImmediate(prefabRoot);
            }
            DestroyImmediate(tempWrapper);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/EditorCache/{cachePath}.prefab");
        }

        if (isHardpoint)
        {
            var hardpointPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/EditorCache/{prefabToHardpoint[address]}.prefab");

            if (!hardpointPrefab)
            {
                var tempGO = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                PrefabUtility.SaveAsPrefabAsset(tempGO, $"Assets/EditorCache/{prefabToHardpoint[address]}.prefab");
                DestroyImmediate(tempGO);
            }
        }

        foreach (var hardpoint in prefab.GetComponentsInChildren<FakeHardpoint>())
        {
            if (hardpoint == null) continue;
            await LoadAddress(hardpoint.AssetGUID, hardpoint.transform, isHardpoint);
        }
    }

    private async static System.Threading.Tasks.Task CloneMeshTree(string address, Transform inTransform, Transform outParent, string cachePath, bool isRoot = false)
    {
        var newPrefabChild = new GameObject(inTransform.name);
        newPrefabChild.transform.parent = outParent;
        newPrefabChild.transform.localPosition = inTransform.localPosition;
        newPrefabChild.transform.localRotation = inTransform.localRotation;
        newPrefabChild.transform.localScale = inTransform.localScale;
        newPrefabChild.AddComponent<SelectAddressableParent>();
        foreach (Transform child in inTransform)
        {
            await CloneMeshTree(address, child, newPrefabChild.transform, cachePath);
        }

        var meshPath = $"Assets/EditorCache/";

        if (inTransform.TryGetComponent<MeshRenderer>(out var meshRenderer) && inTransform.TryGetComponent<MeshFilter>(out var meshFilter) && meshFilter.sharedMesh != null)
        {
            // TODO: Is this stable enough?
            var dataArray = Mesh.AcquireReadOnlyMeshData(meshFilter.sharedMesh);
//
            var meshHashText = "";
            for(int i = 0; i < dataArray.Length; i++)
            {
                if(dataArray[i].vertexCount == 0) continue;

                var data = dataArray[i];
                meshHashText += Hash128.Compute(ref data).ToString();
            }
            dataArray.Dispose();

            if(!System.IO.File.Exists($"{Application.dataPath}/EditorCache/{meshHashText}.asset"))
            {
                AssetDatabase.CreateAsset(Instantiate(meshFilter.sharedMesh), $"{meshPath}{meshHashText}.asset");
            }

            if(meshCache.ContainsKey(meshHashText))
            {
                newPrefabChild.AddComponent<MeshFilter>().sharedMesh = meshCache[meshHashText];
            }
            else
            {
                var newMesh = AssetDatabase.LoadAssetAtPath<Mesh>($"{meshPath}{meshHashText}.asset");
                newPrefabChild.AddComponent<MeshFilter>().sharedMesh = newMesh;
                meshCache.Add(meshHashText, newMesh);
            }

            MeshRenderer newRenderer = newPrefabChild.AddComponent<MeshRenderer>();
            newRenderer.sharedMaterials = meshRenderer.sharedMaterials.Select((mat, matIndex) => CloneMaterial(mat)).ToArray();

            count++;
        }

        if (inTransform.TryGetComponent<RoomSubVolumeDefinition>(out var roomVolume))
        {
            var newRoomVolume = newPrefabChild.AddComponent<RoomSubVolumeDefinition>();
            EditorUtility.CopySerialized(roomVolume, newRoomVolume);
        }

        if (inTransform.TryGetComponent<RoomOpeningDefinition>(out var roomOpening))
        {
            var newRoomOpening = newPrefabChild.AddComponent<RoomOpeningDefinition>();
            EditorUtility.CopySerialized(roomOpening, newRoomOpening);
        }

        if (inTransform.TryGetComponent<HardPoint>(out var hardPoint) && hardPoint.gameObject.activeSelf)
        {
            var assetGUID = await LoadHardpoint(hardPoint);
            if (!string.IsNullOrEmpty(assetGUID))
            {
                var newHardpoint = newPrefabChild.AddComponent<FakeHardpoint>();
                newHardpoint.AssetGUID = assetGUID;
            }
        }

        // Bake StructurePart collider data into the EditorCache prefab for joint visualization.
        if (inTransform.TryGetComponent<StructurePart>(out _))
        {
            Bounds colliderBounds = new Bounds(Vector3.zero, Vector3.one * 0.1f);
            if (inTransform.TryGetComponent<MeshCollider>(out var mc) && mc.sharedMesh != null)
                colliderBounds = mc.sharedMesh.bounds;
            else if (inTransform.TryGetComponent<BoxCollider>(out var bc))
                colliderBounds = new Bounds(bc.center, bc.size);

            var fsp = newPrefabChild.AddComponent<FakeStructurePart>();
            fsp.localColliderBounds = colliderBounds;
            fsp.type = inTransform.name.IndexOf("cutpoint", System.StringComparison.OrdinalIgnoreCase) >= 0
                       ? FakeStructurePart.JointType.CutPoint
                       : FakeStructurePart.JointType.Standard;
        }

        // Bake Root marker for independent jointing units (MandatoryJointContainer), regardless
        // of whether this node also has a StructurePart. Island detection uses this to split parts.
        if (!isRoot && inTransform.TryGetComponent<BBI.Unity.Game.MandatoryJointContainer>(out _))
        {
            var existing = newPrefabChild.GetComponent<FakeStructurePart>();
            if (existing != null)
                existing.type = FakeStructurePart.JointType.Root;
            else
            {
                var fsp = newPrefabChild.AddComponent<FakeStructurePart>();
                fsp.localColliderBounds = new Bounds(Vector3.zero, Vector3.one * 0.1f);
                fsp.type = FakeStructurePart.JointType.Root;
            }
        }
    }

    static Texture2D DuplicateTexture(Texture2D source)
    {
        RenderTexture renderTex = RenderTexture.GetTemporary(
                    source.width,
                    source.height,
                    0,
                    RenderTextureFormat.Default,
                    RenderTextureReadWrite.sRGB);

        Graphics.Blit(source, renderTex);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = renderTex;
        Texture2D readableText = new Texture2D(source.width, source.height);
        readableText.ReadPixels(new Rect(0, 0, renderTex.width, renderTex.height), 0, 0);
        readableText.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(renderTex);
        return readableText;
    }

    static Material CloneMaterial(Material material)
    {
        var matPath = $"EditorCache/{material.ComputeCRC()}.mat";

        if (!System.IO.File.Exists($"{Application.dataPath}/{matPath}"))
        {
            Dictionary<string, Texture2D> textureCache = new Dictionary<string, Texture2D>();
            Dictionary<string, string> validTextures = new Dictionary<string, string>();

            foreach (var textureName in material.GetTexturePropertyNames())
            {
                if(material.GetTexture(textureName) is Texture2D)
                {
                    var orgTexture = (Texture2D)material.GetTexture(textureName);
                    if (orgTexture != null)
                    {
                        Texture2D newTexture = DuplicateTexture(orgTexture);
                        if (!System.IO.File.Exists($"{Application.dataPath}/EditorCache/{newTexture.imageContentsHash.ToString()}.png"))
                            System.IO.File.WriteAllBytes($"{Application.dataPath}/EditorCache/{newTexture.imageContentsHash.ToString()}.png", newTexture.EncodeToPNG());

                        validTextures.Add(textureName, newTexture.imageContentsHash.ToString());
                    }
                }
            }

            AssetDatabase.Refresh();

            foreach (var textureName in validTextures)
            {
                if (!textureCache.ContainsKey(textureName.Key))
                {
                    textureCache.Add(textureName.Key, AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/EditorCache/{textureName.Value}.png"));
                    EditorUtility.SetDirty(textureCache[textureName.Key]);
                }
            }

            Material tempMaterial;
            switch (material.shader.name)
            {
                case "_Lynx/Surface/HDRP/Lit":
                    tempMaterial = new Material(Shader.Find("Fake/_Lynx/Surface/HDRP/Lit"));
                    tempMaterial.CopyPropertiesFromMaterial(material);
                    break;
                default:
                    tempMaterial = new Material(Shader.Find("HDRP/Lit")); // Unknown material, use the default
                    Debug.LogWarning($"Unknown shader {material.shader.name}");
                    break;
            }

            foreach (var textureName in validTextures)
            {
                tempMaterial.SetTexture(textureName.Key, textureCache[textureName.Key]);
            }

            tempMaterial.enableInstancing = true;
            AssetDatabase.CreateAsset(tempMaterial, $"Assets/{matPath}");
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        return AssetDatabase.LoadAssetAtPath<Material>($"Assets/{matPath}");
    }

    public class RenderableMapping
    {
        public Mesh mesh;
        public Material[] mats;
        public Transform parent;
        public Vector3 offset;
        public Quaternion rotation;
        public Vector3 scale;

        private RenderableMapping() { }

        public static RenderableMapping AddressableMapping(Mesh _mesh, Material[] _mats, Transform _parent, Transform _offsetParent, Transform _offset, bool _hardpoint)
        {
            return new RenderableMapping()
            {
                mesh = _mesh,
                mats = _mats,
                parent = _parent,
                offset = _hardpoint ? _offset.position - _offsetParent.GetChild(0).position : _offset.position,
                rotation = _offset.rotation,
                scale = _offset.lossyScale
            };
        }

        public static RenderableMapping AddressableHardpointMapping(Mesh _mesh, Material[] _mats, Transform _parent, Transform _offsetParent, Transform _offset)
        {
            return new RenderableMapping()
            {
                mesh = _mesh,
                mats = _mats,
                parent = _parent,
                offset = _offsetParent.position + _offset.position,
                rotation = _offsetParent.rotation * _offset.rotation,
                scale = Vector3.Scale(_offsetParent.lossyScale, _offset.lossyScale)
            };
        }

        public static RenderableMapping RoomMapping(Transform _parent, bool _hardpoint)
        {
            return new RenderableMapping()
            {
                parent = _parent
            };
        }
    }
}
