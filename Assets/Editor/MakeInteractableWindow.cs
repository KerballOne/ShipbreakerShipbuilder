#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using BBI.Unity.Game;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// Manual, inspectable fixup tool for baked (non-addressable) prefabs that need real interaction
/// (pickup / interact prompt / grant / ITEM COLLECTED popup) support — see
/// project_baked_pickup_interaction_fix and project_baked_interactable_procedure memories.
/// AddressableBaker only clones a fixed component allowlist (Mesh/StructurePart/
/// EntityBlueprintComponent/MandatoryJointContainer/Light) and never touches an "Interaction" marker
/// child's InteractableObject/TriggerableSalvage/NarrativeItemComponent/grant component (Triggerable
/// ThrusterCharge or TriggerableVitalityChange) — those only exist on the live source addressable,
/// which the baked prefab has no stored reference back to. This window takes BOTH a baked prefab root
/// and the original source addressable (found by name search, since it can't be loaded as a normal
/// asset reference) and copies the missing interaction wiring across, resolving the
/// InteractableObjectAsset GUID the same way AddressableBaker already resolves SP_Mat/Blueprint GUIDs.
/// It also explicitly rebuilds TriggerableOnInteractProgressComplete's UnityEvent to call the target's
/// own TriggerableSalvage.Trigger() — a raw component copy does NOT safely carry this over, since
/// persistent UnityEvent call targets are object references into the SOURCE hierarchy and can silently
/// resolve to the wrong object on copy (this broke the ITEM COLLECTED popup once already; see
/// project_baked_thrusterfuel_backpack_resolved memory). Covers all 5 known pickup types: Helmet (data
/// drive), ThrusterFuel, O2 tank, Patch Kit, Med Kit.
///
/// This is the manual first pass — once proven reliable on real prefabs, this logic should be folded
/// into AddressableBaker.BakeOnto itself so future bakes get it automatically. Do that only after this
/// window has been validated, per user instruction (manual tool first, auto-bake integration second).
/// </summary>
public class MakeInteractableWindow : EditorWindow
{
    [MenuItem("Shipbuilder/Make Interactable", priority = 143)]
    static void Open() => GetWindow<MakeInteractableWindow>("Make Interactable");

    enum SourceType { Addressable, Prefab }

    GameObject m_BakedRoot;
    SourceType m_SourceType = SourceType.Addressable;

    // Addressable source
    string m_SourceGuid;
    string m_SourceName;

    // Prefab source (e.g. another already-fixed baked prefab, or its instance in the scene)
    GameObject m_SourcePrefab;

    // Direct node mapping: bypass hierarchy-path matching entirely and apply source's Interaction/SP
    // node straight onto one hand-picked target node, regardless of name or path. For grafting wiring
    // from an unrelated source (e.g. a SparePart pickup) onto an arbitrary descendant of a large,
    // differently-shaped prefab (e.g. one specific glove/boot inside a multi-part suit prefab) — normal
    // path matching can't work here since the two hierarchies don't mirror each other at all.
    bool m_DirectMapping;
    GameObject m_TargetNode;

    string m_StatusMessage = "";
    MessageType m_StatusType = MessageType.None;

    void OnGUI()
    {
        EditorGUILayout.LabelField("Make Interactable", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Copies pickup/interact wiring (InteractableObject, TriggerableSalvage, NarrativeItemComponent, " +
            "TriggerableThrusterCharge/TriggerableVitalityChange grant components) onto a baked prefab's " +
            "matching child GameObjects, from either the original live addressable or another already-fixed " +
            "prefab, and rewires TriggerableOnInteractProgressComplete to call the target's own " +
            "TriggerableSalvage.Trigger() (needed for the ITEM COLLECTED popup). AddressableBaker never " +
            "clones any of this — it only exists on the real, un-baked asset. Covers Helmet/ThrusterFuel/" +
            "O2/PatchKit/MedKit pickups; see project_baked_interactable_procedure memory.",
            MessageType.Info);

        EditorGUILayout.Space();
        m_BakedRoot = (GameObject)EditorGUILayout.ObjectField(
            new GUIContent("Baked Prefab Root", "The baked prefab (or its root GameObject) to fix up."),
            m_BakedRoot, typeof(GameObject), true);

        EditorGUILayout.Space();
        m_SourceType = (SourceType)EditorGUILayout.EnumPopup("Source Type", m_SourceType);

        if (m_SourceType == SourceType.Addressable)
        {
            EditorGUILayout.LabelField("Source Addressable", EditorStyles.boldLabel);
            var buttonLabel = !string.IsNullOrEmpty(m_SourceName) ? $"{m_SourceName}  ({m_SourceGuid})" : "(none selected)";
            var pickerRect = GUILayoutUtility.GetRect(new GUIContent(buttonLabel), EditorStyles.popup, GUILayout.Height(20));
            if (EditorGUI.DropdownButton(pickerRect, new GUIContent(buttonLabel), FocusType.Keyboard))
            {
                var dropdown = new AddressablePickerDropdown(new AdvancedDropdownState(), guid =>
                {
                    m_SourceGuid = guid;
                    m_SourceName = System.IO.Path.GetFileNameWithoutExtension(LoadGameAssets.knownAssetMap[guid]);
                    Repaint();
                });
                dropdown.Show(pickerRect);
            }
        }
        else
        {
            EditorGUILayout.LabelField("Source Prefab", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Pick another prefab/GameObject that already has working interaction wiring (e.g. a " +
                "sibling baked part you already fixed with this tool, or its instance in the scene). " +
                "Node matching is by exact hierarchy path — if the two prefabs have different internal " +
                "names, matching child paths won't be found; check the result notes after Apply.",
                MessageType.None);
            m_SourcePrefab = (GameObject)EditorGUILayout.ObjectField(
                "Source Root", m_SourcePrefab, typeof(GameObject), true);
        }

        EditorGUILayout.Space();
        m_DirectMapping = EditorGUILayout.ToggleLeft(
            new GUIContent("Direct Node Mapping", "Skip hierarchy-path matching. Apply the source's " +
                "Interaction/StructurePart wiring straight onto ONE hand-picked target node, regardless " +
                "of name — for grafting wiring from an unrelated source (e.g. a SparePart pickup) onto " +
                "an arbitrary descendant of a differently-shaped prefab (e.g. one glove/boot inside a " +
                "multi-part suit)."),
            m_DirectMapping);
        if (m_DirectMapping)
        {
            m_TargetNode = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Target Node", "The specific child GameObject on the baked prefab to receive the wiring."),
                m_TargetNode, typeof(GameObject), true);
            EditorGUILayout.HelpBox(
                "The source's FIRST InteractableObject node (its \"Interaction\" child, or the source root " +
                "itself if it carries InteractableObject directly) will be applied onto Target Node, plus " +
                "the source's SP-root supporting components (Collider/Animator/TrackIfSeen/etc.) applied " +
                "onto Baked Prefab Root above. If the target needs its own trigger collider for the " +
                "interact prompt, add an \"Interaction\" child under Target Node first (or one will be " +
                "created for you).",
                MessageType.None);
        }

        EditorGUILayout.Space();
        bool sourceReady = m_SourceType == SourceType.Addressable
            ? !string.IsNullOrEmpty(m_SourceGuid)
            : m_SourcePrefab != null;
        bool targetReady = !m_DirectMapping || m_TargetNode != null;
        GUI.enabled = m_BakedRoot != null && sourceReady && targetReady;
        if (GUILayout.Button("Apply", GUILayout.Height(30)))
            Apply();
        GUI.enabled = true;

        if (!string.IsNullOrEmpty(m_StatusMessage))
            EditorGUILayout.HelpBox(m_StatusMessage, m_StatusType);
    }

    async void Apply()
    {
        m_StatusMessage = "";
        try
        {
            GameObject source;
            string sourceLabel;
            if (m_SourceType == SourceType.Addressable)
            {
                source = await AddressableBaker.LoadAddressableAsync(m_SourceGuid);
                sourceLabel = m_SourceName;
                if (source == null)
                {
                    SetStatus($"Could not load source addressable '{m_SourceGuid}'.", MessageType.Error);
                    return;
                }
            }
            else
            {
                source = m_SourcePrefab;
                sourceLabel = source.name;

                // The dragged-in prefab may be a thin AddressableLoader stub (e.g. imported via
                // ImportGamePartWizard) rather than the real baked/authored hierarchy — it carries only
                // an assetGUID reference, no actual InteractableObject/StructurePart content of its own.
                // Detect that and transparently load the real live addressable instead of failing.
                if (source.GetComponentsInChildren<InteractableObject>(true).Length == 0
                    && source.GetComponentInChildren<AddressableLoader>(true) is AddressableLoader stub
                    && !string.IsNullOrEmpty(stub.assetGUID))
                {
                    var resolved = await AddressableBaker.LoadAddressableAsync(stub.assetGUID, stub.childPath);
                    if (resolved != null)
                    {
                        source = resolved;
                        sourceLabel = $"{m_SourcePrefab.name} → (resolved via AddressableLoader) {stub.assetGUID}";
                    }
                }
            }

            var interactables = source.GetComponentsInChildren<InteractableObject>(true);
            if (interactables.Length == 0)
            {
                SetStatus($"'{sourceLabel}' has no InteractableObject anywhere in its hierarchy — nothing to copy.", MessageType.Warning);
                return;
            }

            var acl = m_BakedRoot.GetComponent<AddressableComponentLoader>();
            if (acl == null) acl = m_BakedRoot.AddComponent<AddressableComponentLoader>();

            int applied = 0;
            int attempted = 0;
            var notes = new List<string>();

            if (m_DirectMapping)
            {
                // No path matching at all — the source and target hierarchies are unrelated (e.g. a
                // SparePart pickup's wiring being grafted onto one glove inside a multi-part suit
                // prefab). Map source's InteractableObject node onto the hand-picked Target Node, and
                // source's StructurePart node (supporting components) onto Baked Prefab Root, since
                // that's the only sensible anchor for "the mesh this wiring should now be attached to."
                var srcIoNode = interactables[0].transform;
                var srcSpNode = source.GetComponentsInChildren<StructurePart>(true).FirstOrDefault()?.transform;

                if (interactables.Length > 1)
                    notes.Add($"ℹ Source has {interactables.Length} InteractableObject nodes — using the first one found ('{srcIoNode.name}'). " +
                        $"Others: {string.Join(", ", interactables.Skip(1).Select(io => io.name))}.");
                var srcIoAssetPreview = ReadObjectField(interactables[0], "m_Asset");
                notes.Add($"ℹ Reading from source node '{srcIoNode.name}': m_Asset = {(srcIoAssetPreview != null ? srcIoAssetPreview.name : "null")}.");

                // The target needs its own trigger-collider child for the interact raycast to hit —
                // reuse one named "Interaction" if present, else create one at the target's origin.
                var targetInteractionNode = m_TargetNode.transform.Find("Interaction");
                if (targetInteractionNode == null)
                {
                    var newInteractionGo = new GameObject("Interaction");
                    newInteractionGo.transform.SetParent(m_TargetNode.transform, false);
                    targetInteractionNode = newInteractionGo.transform;
                    notes.Add($"ℹ Created a new 'Interaction' child under '{m_TargetNode.name}' (none existed).");
                }

                attempted++;
                ApplyInteractionNode(srcIoNode.gameObject, targetInteractionNode.gameObject, acl, notes);
                applied++;

                if (srcSpNode != null)
                {
                    attempted++;
                    ApplyInteractionNode(srcSpNode.gameObject, m_TargetNode, acl, notes);
                    applied++;
                }
            }
            else
            {
                // Nodes to process: every InteractableObject node (the "Interaction" child) PLUS every
                // StructurePart node (the SP root and any sub-mesh) — the SP root also carries supporting
                // interaction components (SphereCollider, Animator, TrackIfSeen, a second "decoy"
                // TriggerableSalvage) that don't live under InteractableObject at all. See
                // project_baked_pickup_interaction_fix memory: matching every component exactly, not just
                // the ones we've specifically identified as load-bearing, is what actually reproduces a
                // working part — AddressableBaker's original fixed allowlist is exactly the mistake this
                // tool exists to route around.
                var nodesToProcess = source.GetComponentsInChildren<InteractableObject>(true).Select(c => c.transform)
                    .Concat(source.GetComponentsInChildren<StructurePart>(true).Select(c => c.transform))
                    .Distinct()
                    .ToList();

                foreach (var srcT in nodesToProcess)
                {
                    attempted++;
                    string relPath = GetRelativePath(source.transform, srcT);
                    var targetT = FindByRelativePath(m_BakedRoot.transform, relPath);
                    if (targetT == null)
                    {
                        notes.Add($"⚠ No matching child at path '{relPath}' on baked prefab — skipped '{srcT.name}'.");
                        continue;
                    }

                    ApplyInteractionNode(srcT.gameObject, targetT.gameObject, acl, notes);
                    applied++;
                }
            }

            EditorUtility.SetDirty(m_BakedRoot);
            SetStatus($"Applied interaction wiring for {applied}/{attempted} node(s).\n" + string.Join("\n", notes),
                applied > 0 ? MessageType.Info : MessageType.Warning);
        }
        catch (System.Exception ex)
        {
            SetStatus($"Failed: {ex}", MessageType.Error);
            Debug.LogError($"[MakeInteractable] {ex}");
        }
    }

    void SetStatus(string msg, MessageType type)
    {
        m_StatusMessage = msg;
        m_StatusType = type;
        Repaint();
    }

    static string GetRelativePath(Transform root, Transform t)
    {
        var segments = new List<string>();
        while (t != null && t != root)
        {
            segments.Insert(0, t.name);
            t = t.parent;
        }
        return string.Join("/", segments);
    }

    static Transform FindByRelativePath(Transform root, string relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return root;
        return root.Find(relPath);
    }

    /// <summary>Copies InteractableObject / TriggerableSalvage / NarrativeItemComponent /
    /// EntityBlueprintComponent from srcNode onto targetNode, resolving the InteractableObjectAsset GUID
    /// via AddressableBaker.ResolveAssetGuidByName (same pattern as SP_Mat/Blueprint). Also copies the
    /// node's layer and any Collider (Box/Sphere/Capsule — MeshCollider is already handled by
    /// AddressableBaker's own bake pass and shouldn't be duplicated here).</summary>
    static void ApplyInteractionNode(GameObject srcNode, GameObject targetNode, AddressableComponentLoader acl, List<string> notes)
    {
        targetNode.layer = srcNode.layer;

        // Collider — only add if the target doesn't already have a collider of some kind (avoid
        // duplicating one the baker or a prior manual pass already created).
        if (targetNode.GetComponent<Collider>() == null)
        {
            if (srcNode.TryGetComponent<BoxCollider>(out var srcBox))
            {
                var newBox = targetNode.AddComponent<BoxCollider>();
                newBox.isTrigger = srcBox.isTrigger;
                newBox.size = srcBox.size;
                newBox.center = srcBox.center;
            }
            else if (srcNode.TryGetComponent<SphereCollider>(out var srcSphere))
            {
                var newSphere = targetNode.AddComponent<SphereCollider>();
                newSphere.isTrigger = srcSphere.isTrigger;
                newSphere.radius = srcSphere.radius;
                newSphere.center = srcSphere.center;
            }
            else if (srcNode.TryGetComponent<CapsuleCollider>(out var srcCapsule))
            {
                var newCapsule = targetNode.AddComponent<CapsuleCollider>();
                newCapsule.isTrigger = srcCapsule.isTrigger;
                newCapsule.radius = srcCapsule.radius;
                newCapsule.height = srcCapsule.height;
                newCapsule.center = srcCapsule.center;
                newCapsule.direction = srcCapsule.direction;
            }
        }

        // InteractableObject — the actual gate InteractionController checks. m_Asset can't be stored
        // directly (lives in the runtime bundle, broken PPtr on save) — resolve by name and record for
        // the AddressableComponentLoader to fill in at load, same as SP_Mat/Blueprint. Always
        // re-resolved and overwrites any existing ACL entry (see UpsertAclEntry) — re-running Apply
        // against a corrected/different source is how a wrong address (e.g. from AclRegisterContextMenu's
        // name-matching landing on an unrelated same-named part) gets fixed, not left alone.
        if (srcNode.TryGetComponent<InteractableObject>(out var srcIo))
        {
            var newIo = targetNode.GetComponent<InteractableObject>() ?? targetNode.AddComponent<InteractableObject>();
            UnityEditor.EditorUtility.CopySerialized(srcIo, newIo);

            var srcAsset = ReadObjectField(srcIo, "m_Asset");
            NullObjectField(newIo, "m_Asset");

            if (srcAsset != null)
            {
                var guid = AddressableBaker.ResolveAssetGuidByName(srcAsset.name);
                if (!string.IsNullOrEmpty(guid))
                {
                    UpsertAclEntry(acl, newIo, "m_Asset", guid);
                }
                else
                {
                    notes.Add($"⚠ Could not resolve GUID for InteractableObjectAsset '{srcAsset.name}' on '{srcNode.name}' — " +
                        "search for it manually via Import Game Part Wizard and add the address by hand.");
                }
            }

            // HandTarget / DisableProxyObject point at sibling/parent nodes within the SAME hierarchy —
            // remap them onto the equivalent target-side nodes rather than leaving stale source refs.
            RemapSelfReference(newIo, "m_HandTarget", srcNode, targetNode, source: srcIo.transform);
            RemapSelfReference(newIo, "m_DisableProxyObject", srcNode, targetNode, source: null);
        }

        // TriggerableSalvage — the actual "pickup complete" action. Preserve TriggerOn exactly (the
        // real gate — must NOT default to OnStart, see project_baked_pickup_interaction_fix memory).
        // MUST end up enabled — CopySerialized normally carries this over correctly from a working
        // source, but the baked backpack was found with this false after an earlier bake/copy pass, so
        // force it explicitly rather than trusting the copy alone (see
        // project_baked_thrusterfuel_backpack_resolved memory).
        TriggerableSalvage newSalvage = null;
        if (srcNode.TryGetComponent<TriggerableSalvage>(out var srcTs))
        {
            newSalvage = targetNode.GetComponent<TriggerableSalvage>() ?? targetNode.AddComponent<TriggerableSalvage>();
            UnityEditor.EditorUtility.CopySerialized(srcTs, newSalvage);
            newSalvage.enabled = true;
        }

        // Grant component — TriggerableThrusterCharge (fuel) or TriggerableVitalityChange (O2/PatchKit/
        // MedKit). Lives on the Interaction node in every real source prefab. A straight CopySerialized
        // is safe here since these components don't reference other GameObjects (no persistent UnityEvent
        // targets to rebind), unlike TriggerableOnInteractProgressComplete below.
        if (srcNode.TryGetComponent<TriggerableThrusterCharge>(out var srcCharge) && targetNode.GetComponent<TriggerableThrusterCharge>() == null)
            UnityEditor.EditorUtility.CopySerialized(srcCharge, targetNode.AddComponent<TriggerableThrusterCharge>());

        if (srcNode.TryGetComponent<TriggerableVitalityChange>(out var srcVitality) && targetNode.GetComponent<TriggerableVitalityChange>() == null)
            UnityEditor.EditorUtility.CopySerialized(srcVitality, targetNode.AddComponent<TriggerableVitalityChange>());

        if (srcNode.TryGetComponent<NarrativeItemComponent>(out var srcNic))
        {
            var newNic = targetNode.GetComponent<NarrativeItemComponent>() ?? targetNode.AddComponent<NarrativeItemComponent>();
            UnityEditor.EditorUtility.CopySerialized(srcNic, newNic);
        }

        // EntityBlueprintComponent on the Interaction node itself (separate from the SP root's) — some
        // interaction nodes resolve their own blueprint independently of the part's main SP/BP pair.
        // Re-processed even if already present (see TriggerablePATSender comment above for why).
        if (srcNode.TryGetComponent<EntityBlueprintComponent>(out var srcEbc))
        {
            var newEbc = targetNode.GetComponent<EntityBlueprintComponent>();
            if (newEbc == null)
            {
                newEbc = targetNode.AddComponent<EntityBlueprintComponent>();
                UnityEditor.EditorUtility.CopySerialized(srcEbc, newEbc);
            }

            var srcBp = ReadObjectField(srcEbc, "m_BlueprintAsset");
            NullObjectField(newEbc, "m_BlueprintAsset");

            if (srcBp != null)
            {
                var guid = AddressableBaker.ResolveAssetGuidByName(srcBp.name);
                if (!string.IsNullOrEmpty(guid))
                    UpsertAclEntry(acl, newEbc, "m_BlueprintAsset", guid);
                else
                    notes.Add($"⚠ Could not resolve GUID for blueprint '{srcBp.name}' on '{srcNode.name}'.");
            }
        }

        // TriggerableOnInteractProgressComplete — the component whose OnTriggered UnityEvent actually
        // calls TriggerableSalvage.Trigger() to fire the pickup-collected popup (see
        // project_baked_thrusterfuel_backpack_resolved memory). A straight CopySerialized does NOT
        // safely carry this over: persistent UnityEvent call targets are object references into the
        // SOURCE hierarchy, and Unity's serialization can silently resolve them to the wrong object
        // type (observed: ended up bound to an unrelated TextAsset with an empty method name) rather
        // than erroring. So the event is rebuilt explicitly here instead of trusted from the copy —
        // cleared, then a fresh persistent call added pointing at THIS target's own TriggerableSalvage
        // (found via GetComponentInParent, since Interaction is always a child of the SP root that
        // carries it), calling Trigger().
        if (srcNode.TryGetComponent<TriggerableOnInteractProgressComplete>(out var srcTip))
        {
            var newTip = targetNode.GetComponent<TriggerableOnInteractProgressComplete>() ?? targetNode.AddComponent<TriggerableOnInteractProgressComplete>();
            UnityEditor.EditorUtility.CopySerialized(srcTip, newTip);

            var targetSalvage = newSalvage != null ? newSalvage : targetNode.GetComponentInParent<TriggerableSalvage>();
            if (targetSalvage != null)
            {
                var so = new SerializedObject(newTip);
                var eventProp = so.FindProperty("m_TriggerUnityEvent.m_PersistentCalls.m_Calls");
                if (eventProp != null)
                {
                    eventProp.ClearArray();
                    eventProp.InsertArrayElementAtIndex(0);
                    var call = eventProp.GetArrayElementAtIndex(0);
                    call.FindPropertyRelative("m_Target").objectReferenceValue = targetSalvage;
                    call.FindPropertyRelative("m_TargetAssemblyTypeName").stringValue = "TriggerableComponent, BBI.Unity.Game";
                    call.FindPropertyRelative("m_MethodName").stringValue = "Trigger";
                    call.FindPropertyRelative("m_Mode").enumValueIndex = 1; // PersistentListenerMode.Void
                    call.FindPropertyRelative("m_CallState").enumValueIndex = 2; // UnityEventCallState.RuntimeOnly
                    so.ApplyModifiedProperties();
                    notes.Add($"ℹ Wired '{targetNode.name}'.TriggerableOnInteractProgressComplete → TriggerableSalvage('{targetSalvage.gameObject.name}').Trigger().");
                }
            }
            else
            {
                notes.Add($"⚠ No TriggerableSalvage found on '{targetNode.name}' or its parents — could not wire TriggerableOnInteractProgressComplete's trigger event. Add TriggerableSalvage to the SP root first.");
            }
        }

        // TriggerablePATSender — progression tracking, not required for the pickup action itself. Its
        // PAT reference has the same broken-PPtr problem as SP_Mat/Blueprint/InteractableObjectAsset —
        // PlayerActionTrackerAsset is a plain ScriptableObject in known_assets.json, resolve it the
        // same way via the AddressableComponentLoader. Re-processed even if the component already
        // exists on the target (e.g. from a prior Apply run that left m_PAT unresolved) — otherwise a
        // second Apply silently no-ops here with no warning, since there's nothing left to add.
        if (srcNode.TryGetComponent<TriggerablePATSender>(out var srcPat))
        {
            var newPat = targetNode.GetComponent<TriggerablePATSender>();
            bool isNewComponent = newPat == null;
            if (isNewComponent)
            {
                newPat = targetNode.AddComponent<TriggerablePATSender>();
                UnityEditor.EditorUtility.CopySerialized(srcPat, newPat);
            }

            var srcPatAsset = ReadObjectField(srcPat, "m_PAT");
            NullObjectField(newPat, "m_PAT");

            if (srcPatAsset != null)
            {
                var guid = AddressableBaker.ResolveAssetGuidByName(srcPatAsset.name);
                if (!string.IsNullOrEmpty(guid))
                    UpsertAclEntry(acl, newPat, "m_PAT", guid);
                else
                    notes.Add($"⚠ Could not resolve GUID for PAT asset '{srcPatAsset.name}' on '{srcNode.name}'.");
            }
        }

        // TriggerableOnAwake — a separate "fire an action immediately" trigger, distinct from
        // TriggerableSalvage. Commonly present on the SP root alongside the real interaction wiring;
        // harmless red herring per project_baked_pickup_interaction_fix memory, but included for exact
        // component parity with the working source.
        if (srcNode.TryGetComponent<TriggerableOnAwake>(out var srcToa) && targetNode.GetComponent<TriggerableOnAwake>() == null)
            UnityEditor.EditorUtility.CopySerialized(srcToa, targetNode.AddComponent<TriggerableOnAwake>());

        // Animator — drives cosmetic FX (e.g. helmet light blink) via runtimeAnimatorController. Not a
        // ScriptableObject (it's a RuntimeAnimatorController/.controller asset) but still a plain named
        // addressable asset in known_assets.json, resolved the same way. Re-processed even if already
        // present (see TriggerablePATSender comment above for why).
        if (srcNode.TryGetComponent<Animator>(out var srcAnim))
        {
            var newAnim = targetNode.GetComponent<Animator>();
            if (newAnim == null)
            {
                newAnim = targetNode.AddComponent<Animator>();
                UnityEditor.EditorUtility.CopySerialized(srcAnim, newAnim);
            }

            var srcController = srcAnim.runtimeAnimatorController;
            NullObjectField(newAnim, "m_Controller");

            if (srcController != null)
            {
                var guid = AddressableBaker.ResolveAssetGuidByName(srcController.name);
                if (!string.IsNullOrEmpty(guid))
                    UpsertAclEntry(acl, newAnim, "m_Controller", guid);
                else
                    notes.Add($"⚠ Could not resolve GUID for AnimatorController '{srcController.name}' on '{srcNode.name}' (cosmetic only, not required for pickup).");
            }
        }

        // TrackIfSeen — progression/PAT tracking ("has the player seen this object"), not interaction-
        // gating, but resolved the same way as TriggerablePATSender's PAT for consistency. Re-processed
        // even if already present (see TriggerablePATSender comment above for why).
        if (srcNode.TryGetComponent<TrackIfSeen>(out var srcTis))
        {
            var newTis = targetNode.GetComponent<TrackIfSeen>();
            if (newTis == null)
            {
                newTis = targetNode.AddComponent<TrackIfSeen>();
                UnityEditor.EditorUtility.CopySerialized(srcTis, newTis);
            }

            var srcSeenAction = ReadObjectField(srcTis, "m_ActionOfSeeingObject");
            NullObjectField(newTis, "m_ActionOfSeeingObject");

            if (srcSeenAction != null)
            {
                var guid = AddressableBaker.ResolveAssetGuidByName(srcSeenAction.name);
                if (!string.IsNullOrEmpty(guid))
                    UpsertAclEntry(acl, newTis, "m_ActionOfSeeingObject", guid);
                else
                    notes.Add($"⚠ Could not resolve GUID for PAT asset '{srcSeenAction.name}' on '{srcNode.name}'.");
            }
        }
    }

    /// <summary>Adds an AddressableComponentValue entry for (component, field), replacing any existing
    /// entry for the same pair rather than skipping. Re-running Apply against a corrected or different
    /// source is the intended way to fix a wrong address (e.g. one that AclRegisterContextMenu's
    /// name-matching pulled from an unrelated same-named part) — silently leaving stale data in place
    /// would make that impossible without manually editing the ACL list by hand.</summary>
    static void UpsertAclEntry(AddressableComponentLoader acl, Component component, string field, string address)
    {
        acl.componentValues.RemoveAll(cv => cv.component == component && cv.field == field);
        acl.componentValues.Add(new AddressableComponentValue { component = component, field = field, address = address });
    }

    /// <summary>Best-effort remap for a Transform/GameObject-typed field that referenced a node within
    /// the SOURCE hierarchy (e.g. HandTarget usually points at the Interaction node itself, DisableProxyObject
    /// at the SP root/parent) — points the equivalent field on the TARGET at the matching node instead of
    /// leaving a dangling source-hierarchy reference.</summary>
    static void RemapSelfReference(Component comp, string fieldName, GameObject srcNode, GameObject targetNode, Transform source)
    {
        var so = new SerializedObject(comp);
        var prop = so.FindProperty(fieldName);
        if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference) return;

        var value = prop.objectReferenceValue;
        Object newValue = null;

        if (value is Transform t && t == srcNode.transform)
            newValue = targetNode.transform;
        else if (value is GameObject g && g == srcNode.transform.parent?.gameObject)
            newValue = targetNode.transform.parent?.gameObject;

        prop.objectReferenceValue = newValue;
        so.ApplyModifiedProperties();
    }

    // ── Reflection helpers (mirrors AddressableBaker.ReadObjectField/NullObjectField) ──────────────

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
}

/// <summary>Searchable dropdown (built-in Unity widget, same one used for "Add Component" etc. — has
/// its own search field automatically, no custom filtering needed) for picking an addressable prefab
/// by name out of LoadGameAssets.knownAssetMap. Flat list, not grouped by folder — this project has
/// thousands of entries and grouping by path would mostly just add extra clicks for no benefit when
/// the search field already narrows it down instantly.</summary>
class AddressablePickerDropdown : AdvancedDropdown
{
    readonly System.Action<string> m_OnSelected;
    readonly List<KeyValuePair<string, string>> m_Items;

    public AddressablePickerDropdown(AdvancedDropdownState state, System.Action<string> onSelected) : base(state)
    {
        m_OnSelected = onSelected;
        m_Items = (LoadGameAssets.knownAssetMap ?? new Dictionary<string, string>())
            .Where(kv => kv.Value.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => System.IO.Path.GetFileNameWithoutExtension(kv.Value))
            .ToList();
        minimumSize = new Vector2(400, 400);
    }

    protected override AdvancedDropdownItem BuildRoot()
    {
        var root = new AdvancedDropdownItem("Addressable Prefabs");
        for (int i = 0; i < m_Items.Count; i++)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(m_Items[i].Value);
            root.AddChild(new AdvancedDropdownItem(name) { id = i });
        }
        return root;
    }

    protected override void ItemSelected(AdvancedDropdownItem item)
    {
        if (item.id >= 0 && item.id < m_Items.Count)
            m_OnSelected(m_Items[item.id].Key);
    }
}
#endif
