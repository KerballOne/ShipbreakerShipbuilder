using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Swaps AddressableLoader GameObjects in-place: patches name + assetGUID, then compensates
// for any difference in the loaded asset's baked child transform so the part stays in place.
public class SwapPrefabInSceneWindow : EditorWindow
{
    GameObject m_Source;
    GameObject m_Target;
    string m_Status = "";
    MessageType m_StatusType = MessageType.None;

    [MenuItem("Shipbuilder/Swap Prefab In Scene", priority = 102)]
    static void Open() => GetWindow<SwapPrefabInSceneWindow>("Swap Prefab In Scene");

    void OnGUI()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Prefab Swap", EditorStyles.boldLabel);
        EditorGUILayout.Space(2);

        var newSource = (GameObject)EditorGUILayout.ObjectField("Source (replace this)", m_Source, typeof(GameObject), true);
        if (newSource != m_Source)
            m_Source = ResolveToPrefabAsset(newSource);

        var newTarget = (GameObject)EditorGUILayout.ObjectField("Target (use this)", m_Target, typeof(GameObject), true);
        if (newTarget != m_Target)
            m_Target = ResolveToPrefabAsset(newTarget);

        EditorGUILayout.Space(6);

        if (m_Source != null && m_Target != null)
        {
            var found = new List<GameObject>();
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                CollectByName(root, m_Source.name, found);

            EditorGUILayout.HelpBox(
                found.Count == 0
                    ? $"No instances of '{m_Source.name}' found in the active scene."
                    : $"Found {found.Count} instance(s) of '{m_Source.name}' in the active scene.",
                found.Count == 0 ? MessageType.Warning : MessageType.Info);
        }

        EditorGUI.BeginDisabledGroup(m_Source == null || m_Target == null || m_Source == m_Target);
        if (GUILayout.Button("Swap All In Scene"))
            RunSwap();
        EditorGUI.EndDisabledGroup();

        if (!string.IsNullOrEmpty(m_Status))
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(m_Status, m_StatusType);
        }
    }

    void RunSwap()
    {
        m_Status = "";
        m_StatusType = MessageType.None;

        var sourceLoader = m_Source.GetComponent<BBI.Unity.Game.AddressableLoader>();
        var targetLoader = m_Target.GetComponent<BBI.Unity.Game.AddressableLoader>();
        if (targetLoader == null)
        {
            m_Status = $"Target '{m_Target.name}' has no AddressableLoader component.";
            m_StatusType = MessageType.Error;
            return;
        }

        string sourceGUID      = sourceLoader != null ? sourceLoader.assetGUID : "";
        string targetGUID      = targetLoader.assetGUID;
        string targetChildPath = targetLoader.childPath;
        string targetName      = m_Target.name;
        string sourceName      = m_Source.name;

        // Compute the transform compensation needed:
        // When AddressableLoader loads an asset, the root GO of that asset becomes a child
        // of the wrapper with whatever baked local transform it has. If source and target
        // assets have different baked transforms, we offset the wrapper to compensate.
        bool srcOk = GetLoadedChildTransform(sourceGUID, out Vector3 srcPos, out Quaternion srcRot);
        bool dstOk = GetLoadedChildTransform(targetGUID, out Vector3 dstPos, out Quaternion dstRot);
        bool hasCompensation = srcOk && dstOk;

        var scene = SceneManager.GetActiveScene();
        var allMatches = new List<GameObject>();
        foreach (var root in scene.GetRootGameObjects())
            CollectByName(root, sourceName, allMatches);

        if (allMatches.Count == 0)
        {
            m_Status = $"No instances of '{sourceName}' found in the active scene.";
            m_StatusType = MessageType.Warning;
            return;
        }

        Undo.SetCurrentGroupName("Swap Prefab In Scene");
        int undoGroup = Undo.GetCurrentGroup();

        foreach (var go in allMatches)
        {
            Undo.RecordObject(go, "Swap Prefab In Scene");
            Undo.RecordObject(go.transform, "Swap Prefab In Scene");

            go.name = targetName;

            var loader = go.GetComponent<BBI.Unity.Game.AddressableLoader>();
            if (loader != null)
            {
                var so = new SerializedObject(loader);
                so.Update();
                so.FindProperty("assetGUID").stringValue = targetGUID;
                so.FindProperty("childPath").stringValue = targetChildPath;
                so.ApplyModifiedProperties();
            }

            // Shift wrapper to cancel out the change in the loaded child's baked offset.
            // The loaded child sits at dstPos/dstRot relative to the wrapper. We want it
            // to appear where srcPos/srcRot was, so we counter-rotate and counter-translate
            // the wrapper in its parent's local space.
            if (hasCompensation && (srcPos != dstPos || srcRot != dstRot))
            {
                // World-space position of where the source child was placed:
                // wrapperWorld * srcLocal = childWorld
                // We want: newWrapperWorld * dstLocal = childWorld
                // => newWrapperWorld = childWorld * inverse(dstLocal)
                // In local space of wrapper's parent:
                Matrix4x4 parentWorld = go.transform.parent != null
                    ? go.transform.parent.localToWorldMatrix
                    : Matrix4x4.identity;

                Matrix4x4 wrapperLocal = Matrix4x4.TRS(
                    go.transform.localPosition, go.transform.localRotation, go.transform.localScale);
                Matrix4x4 wrapperWorld = parentWorld * wrapperLocal;

                // Where the source child ended up in world space
                Matrix4x4 srcChildWorld = wrapperWorld * Matrix4x4.TRS(srcPos, srcRot, Vector3.one);

                // New wrapper world = srcChildWorld * inverse(dstLocal)
                Matrix4x4 dstLocalInv = Matrix4x4.TRS(dstPos, dstRot, Vector3.one).inverse;
                Matrix4x4 newWrapperWorld = srcChildWorld * dstLocalInv;

                // Convert back to local space of parent
                Matrix4x4 newWrapperLocal = parentWorld.inverse * newWrapperWorld;

                go.transform.localPosition = newWrapperLocal.GetColumn(3);
                go.transform.localRotation = newWrapperLocal.rotation;
                // preserve scale
            }
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(scene);
        m_Status = $"Swapped {allMatches.Count} instance(s){(hasCompensation ? " with transform compensation" : " (no cache — check positions manually)")}.";
        m_StatusType = MessageType.Info;
        Repaint();
    }

    // Loads the editor-cached prefab for a game asset GUID and returns the local transform
    // of its root GO (named after the GUID) — this is the baked world-captured offset that
    // AddressableLoader will apply when it instantiates the asset as a child of the wrapper.
    static bool GetLoadedChildTransform(string guid, out Vector3 localPos, out Quaternion localRot)
    {
        localPos = Vector3.zero;
        localRot = Quaternion.identity;
        if (string.IsNullOrEmpty(guid)) return false;

        string cachePath = $"Assets/EditorCache/{guid}.prefab";
        var cached = AssetDatabase.LoadAssetAtPath<GameObject>(cachePath);
        if (cached == null) return false;

        localPos = cached.transform.localPosition;
        localRot = cached.transform.localRotation;
        return true;
    }

    static void CollectByName(GameObject go, string sourceName, List<GameObject> results)
    {
        if (NameMatches(go.name, sourceName))
        {
            results.Add(go);
            return;
        }
        foreach (Transform child in go.transform)
            CollectByName(child.gameObject, sourceName, results);
    }

    static bool NameMatches(string name, string sourceName) =>
        name == sourceName ||
        (name.StartsWith(sourceName) && name.Length > sourceName.Length &&
         name[sourceName.Length] == ' ' && name[sourceName.Length + 1] == '(');

    static GameObject ResolveToPrefabAsset(GameObject go)
    {
        if (go == null) return null;
        if (!go.scene.IsValid()) return go;
        var asset = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
        if (asset != null) return asset;
        var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
        if (root != null)
        {
            asset = PrefabUtility.GetCorrespondingObjectFromSource(root);
            if (asset != null) return asset;
        }
        return go;
    }
}
