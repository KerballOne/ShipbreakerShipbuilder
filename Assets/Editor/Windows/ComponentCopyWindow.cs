using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

public class ComponentCopyWindow : EditorWindow
{
    [MenuItem("Shipbuilder/Component Copy Window", priority = 122)]
    public static void Open() => GetWindow<ComponentCopyWindow>("Component Copy").Show();

    // ── shared ───────────────────────────────────────────────────────────────
    int _tab;
    readonly string[] _tabLabels = { "Components", "Material Shader" };

    // ── Tab 0: Component diff ─────────────────────────────────────────────────
    GameObject   _compSrc;
    string       _compSrcName; // display name when GO ref goes null on prefab exit

    // Snapshotted source data — populated on lock, survives prefab context exit
    class LockedSourceData
    {
        // Per component: live ref (valid while prefab open) + YAML text snapshot via EditorUtility
        public class CompSnapshot
        {
            public string typeName;       // display e.g. "StructurePart"
            public string typeFullName;   // AssemblyQualifiedName for GetType lookup
            public Component liveComp;    // non-null while prefab context is open
            public string yamlText;       // EditorUtility.CopySerialized-compatible: written at capture time
        }
        public List<CompSnapshot> components = new List<CompSnapshot>();

        // ACL entries as plain strings
        public class AclSnapshot
        {
            public string typeFullName; // component type
            public string field;        // e.g. "m_StructurePartAsset"
            public string address;      // GUID or path
            public string addrLabel;    // resolved asset name for display
        }
        public List<AclSnapshot> aclEntries = new List<AclSnapshot>();
        public string goName;
    }
    LockedSourceData _lockedSrc;

    GameObject[] _compDsts = new GameObject[0];
    Vector2 _compScroll;

    struct CompEntry
    {
        public Component srcComp;   // may be null if only on src side
        public Component dstComp;   // may be null if missing on dst
        public string label;
        public bool different;
        public bool selected;
        public bool aclDriven;  // target component is runtime-populated by ACL — cannot be meaningfully copied
    }

    bool _dstIsAddressable; // entire target GO is addressable — nothing can be copied
    List<CompEntry> _compEntries = new List<CompEntry>();

    // ACL sub-section
    struct AclEntry
    {
        public AddressableComponentValue srcVal;      // live ref — may be null if source is locked
        public LockedSourceData.AclSnapshot srcSnap;  // snapshot used when source is locked
        public AddressableComponentValue dstVal;      // null = missing in first dst (representative)
        public string label;   // type prefix e.g. "StructurePart"
        public string srcAddr; // resolved asset name from source
        public string dstAddr; // resolved asset name from target(s); "(various)" if targets differ
        public bool selected;
    }
    List<AclEntry> _aclEntries = new List<AclEntry>();
    bool _aclFoldout = true;

    // ── Tab 1: Material shader diff ───────────────────────────────────────────
    GameObject _matSrc, _matDst;
    int _matSrcSlot, _matDstSlot;
    Vector2 _matScroll;

    struct MatProp
    {
        public string name;
        public string srcVal, dstVal;
        public bool selected;
    }
    List<MatProp> _matProps = new List<MatProp>();
    bool _matDiffBuilt;

    // ─────────────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        _tab = GUILayout.Toolbar(_tab, _tabLabels);
        EditorGUILayout.Space(4);

        if (_tab == 0) DrawComponentTab();
        else           DrawMaterialTab();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tab 0 — Components
    // ══════════════════════════════════════════════════════════════════════════

    void OnSelectionChange() => Repaint();

    void DrawComponentTab()
    {
        EditorGUILayout.LabelField("Source → Target component diff", EditorStyles.boldLabel);

        // Source: picker — snapshot taken immediately on change so data survives prefab context exit
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.BeginHorizontal();
        if (_compSrc == null && _lockedSrc != null)
        {
            // GO ref lost on prefab exit — show snapshotted name, read-only
            GUI.enabled = false;
            EditorGUILayout.TextField("Source", _compSrcName ?? "(snapshot)");
            GUI.enabled = true;
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
                { _lockedSrc = null; _compSrcName = null; }
        }
        else
        {
            var prev = _compSrc;
            _compSrc = (GameObject)EditorGUILayout.ObjectField("Source", _compSrc, typeof(GameObject), true);
            if (_compSrc != prev)
            {
                _lockedSrc   = _compSrc != null ? CaptureSource(_compSrc) : null;
                _compSrcName = _compSrc != null ? _compSrc.name : null;
            }
        }
        EditorGUILayout.EndHorizontal();

        // "Set from Selection" — picks the selected GO from inside a prefab context
        if (GUILayout.Button("Set Source from Selection", EditorStyles.miniButton))
        {
            var picked = Selection.activeGameObject;
            if (picked != null)
            {
                _compSrc     = picked;
                _compSrcName = picked.name;
                _lockedSrc   = CaptureSource(picked);
            }
        }
        bool changed = EditorGUI.EndChangeCheck();

        // When source is a snapshot (_compSrc null), all selections are targets; otherwise exclude the source GO
        var sel = Selection.gameObjects
            .Where(go => _compSrc == null || go != _compSrc)
            .ToArray();
        if (!sel.SequenceEqual(_compDsts)) { _compDsts = sel; changed = true; }

        string targetLabel = _compDsts.Length == 0 ? "—"
            : _compDsts.Length == 1 ? _compDsts[0].name
            : $"{_compDsts.Length} objects selected";
        EditorGUILayout.LabelField("Target", targetLabel);

        if (changed) RebuildCompDiff();

        if ((_compSrc == null && _lockedSrc == null) || _compDsts.Length == 0)
        {
            EditorGUILayout.HelpBox("Set Source and select one or more Target GameObjects in the Hierarchy.", MessageType.Info);
            return;
        }

        if (_dstIsAddressable)
        {
            EditorGUILayout.HelpBox("Target is addressable — its components are runtime-populated and cannot be meaningfully copied.", MessageType.Warning);
            return;
        }

        EditorGUILayout.Space(4);

        // Select all / none (skips ACL-driven rows)
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("All", GUILayout.Width(50)))  SetAllComp(true);
        if (GUILayout.Button("None", GUILayout.Width(50))) SetAllComp(false);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        _compScroll = EditorGUILayout.BeginScrollView(_compScroll);

        // ── Regular components ──
        bool anyComp = false;
        for (int i = 0; i < _compEntries.Count; i++)
        {
            var e = _compEntries[i];
            if (!e.different) continue;
            anyComp = true;
            GUI.enabled = !e.aclDriven;
            EditorGUILayout.BeginHorizontal();
            string rowLabel = e.aclDriven ? $"{e.label}  [runtime ACL]" : e.label;
            bool compSel = EditorGUILayout.ToggleLeft(rowLabel, e.selected);
            if (!e.aclDriven && compSel != e.selected) { e.selected = compSel; _compEntries[i] = e; }
            EditorGUILayout.EndHorizontal();
            GUI.enabled = true;
        }
        if (!anyComp)
            EditorGUILayout.LabelField("  No differing components found.", EditorStyles.miniLabel);

        // ── ACL entries — only shown when target has a real ACL of its own (baked) ──
        bool hasSrcAcl = (_compSrc != null && FindAncestorAcl(_compSrc.transform) != null)
                      || (_lockedSrc != null && _lockedSrc.aclEntries.Count > 0);
        var dstAcl = FindAncestorAcl(_compDsts[0].transform);
        if (hasSrcAcl && dstAcl != null && _aclEntries.Count > 0)
        {
            EditorGUILayout.Space(4);
            _aclFoldout = EditorGUILayout.Foldout(_aclFoldout, $"ACL entries ({_aclEntries.Count} differ)", true);
            if (_aclFoldout)
            {
                // Header row
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(18); // checkbox width
                EditorGUILayout.LabelField("", GUILayout.Width(110));
                EditorGUILayout.LabelField("Target (current)", EditorStyles.miniLabel, GUILayout.MinWidth(80));
                EditorGUILayout.LabelField("Source (will apply)", EditorStyles.miniLabel, GUILayout.MinWidth(80));
                EditorGUILayout.EndHorizontal();

                for (int i = 0; i < _aclEntries.Count; i++)
                {
                    var e = _aclEntries[i];
                    EditorGUILayout.BeginHorizontal();
                    bool aclSel = EditorGUILayout.Toggle(e.selected, GUILayout.Width(16));
                    if (aclSel != e.selected) { e.selected = aclSel; _aclEntries[i] = e; }
                    EditorGUILayout.LabelField(e.label, GUILayout.Width(110));
                    EditorGUILayout.LabelField(e.dstAddr, EditorStyles.miniLabel, GUILayout.MinWidth(80));
                    EditorGUILayout.LabelField(e.srcAddr, EditorStyles.miniLabel, GUILayout.MinWidth(80));
                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(6);
        bool anySelected = _compEntries.Any(e => e.selected && !e.aclDriven) || _aclEntries.Any(e => e.selected);
        GUI.enabled = anySelected;
        if (GUILayout.Button("Apply Selected to Target"))
            ApplyCompCopy();
        GUI.enabled = true;
    }

    void RebuildCompDiff()
    {
        _compEntries.Clear();
        _aclEntries.Clear();
        _dstIsAddressable = false;
        bool usingSnapshot = _compSrc == null && _lockedSrc != null;
        if ((_compSrc == null && _lockedSrc == null) || _compDsts.Length == 0) return;

        var _compDst = _compDsts[0]; // representative target for component diff

        _dstIsAddressable = _compDst.GetComponentInParent<AddressableLoader>(true) != null
                         || _compDst.GetComponentInParent<SelectAddressableParent>(true) != null;

        // Build set of types that are ACL-driven on the dst GO
        var aclDrivenTypes = new HashSet<System.Type>();
        var dstAclEarly = FindAncestorAcl(_compDst.transform);
        if (dstAclEarly != null)
        {
            string dstName = StripCopySuffix(_compDst.name);
            foreach (var cv in dstAclEarly.componentValues)
            {
                if (cv.component != null && StripCopySuffix(cv.component.gameObject.name) == dstName)
                    aclDrivenTypes.Add(cv.component.GetType());
            }
        }

        var dstComps = _compDst.GetComponents<Component>();
        var dstByType = new Dictionary<System.Type, List<Component>>();
        foreach (var c in dstComps)
        {
            if (c == null) continue;
            var t = c.GetType();
            if (!dstByType.ContainsKey(t)) dstByType[t] = new List<Component>();
            dstByType[t].Add(c);
        }
        var usedDst = new HashSet<Component>();

        if (usingSnapshot)
        {
            // Source is a snapshot — build comp entries from snapshot types only
            foreach (var snap in _lockedSrc.components)
            {
                var type = System.Type.GetType(snap.typeFullName);
                if (type == null) continue;
                Component dst = null;
                if (dstByType.TryGetValue(type, out var candidates))
                {
                    dst = candidates.FirstOrDefault(c => !usedDst.Contains(c));
                    if (dst != null) usedDst.Add(dst);
                }
                bool driven = aclDrivenTypes.Contains(type);
                _compEntries.Add(new CompEntry
                {
                    srcComp = null, dstComp = dst,
                    label = dst == null ? $"[MISSING in target] {snap.typeName}" : snap.typeName,
                    different = true, // always show when using snapshot — can't deep-compare
                    selected = false,
                    aclDriven = driven,
                });
            }
        }
        else
        {
            var srcComps = _compSrc.GetComponents<Component>();
            foreach (var src in srcComps)
            {
                if (src == null || src is Transform) continue;
                var type = src.GetType();
                Component dst = null;
                if (dstByType.TryGetValue(type, out var candidates))
                {
                    dst = candidates.FirstOrDefault(c => !usedDst.Contains(c));
                    if (dst != null) usedDst.Add(dst);
                }
                bool driven = aclDrivenTypes.Contains(type);
                bool diff = dst == null || !ComponentsEqual(src, dst);
                _compEntries.Add(new CompEntry
                {
                    srcComp = src, dstComp = dst,
                    label = dst == null ? $"[MISSING in target] {AbbrevType(type)}" : AbbrevType(type),
                    different = diff, selected = false, aclDriven = driven,
                });
            }
        }

        // Components only on dst (extra)
        foreach (var dst in dstComps)
        {
            if (dst == null || dst is Transform || usedDst.Contains(dst)) continue;
            _compEntries.Add(new CompEntry
            {
                srcComp = null, dstComp = dst,
                label = $"[ONLY in target] {AbbrevType(dst.GetType())}",
                different = true, selected = false,
            });
        }

        // ACL diff
        var dstAcl = FindAncestorAcl(_compDst.transform);
        if (dstAcl == null) return;

        if (usingSnapshot)
        {
            // Build ACL diff from snapshot
            foreach (var snap in _lockedSrc.aclEntries)
            {
                var type = System.Type.GetType(snap.typeFullName);

                AddressableComponentValue FindDstVal(GameObject dstGo, AddressableComponentLoader acl)
                {
                    string nm = StripCopySuffix(dstGo.name);
                    return acl.componentValues.FirstOrDefault(cv =>
                        cv.component != null &&
                        (cv.component.gameObject == dstGo || StripCopySuffix(cv.component.gameObject.name) == nm) &&
                        cv.field == snap.field &&
                        cv.component.GetType() == type);
                }

                var dstAddrs = new HashSet<string>();
                AddressableComponentValue repDv = null;
                foreach (var dstGo in _compDsts)
                {
                    var acl = FindAncestorAcl(dstGo.transform);
                    if (acl == null) continue;
                    var dv = FindDstVal(dstGo, acl);
                    dstAddrs.Add(dv == null ? "<missing>" : AddressToAssetName(dv.address));
                    if (dstGo == _compDst) repDv = dv;
                }

                bool allSame = dstAddrs.Count == 1 && dstAddrs.Contains(snap.addrLabel);
                if (allSame) continue;

                string typeLabel = type != null ? AbbrevType(type) : snap.typeFullName;
                string fieldLabel = AbbrevField(snap.field);
                string prefix = fieldLabel == typeLabel ? typeLabel : $"{typeLabel}.{fieldLabel}";
                _aclEntries.Add(new AclEntry
                {
                    srcVal = null, srcSnap = snap, dstVal = repDv,
                    label = prefix, srcAddr = snap.addrLabel,
                    dstAddr = dstAddrs.Count == 1 ? dstAddrs.First() : "(various)",
                    selected = false,
                });
            }
            return;
        }

        var srcAcl = FindAncestorAcl(_compSrc.transform);
        if (srcAcl == null) return;

        var srcEntries = srcAcl.componentValues
            .Where(cv => cv.component != null && cv.component.gameObject == _compSrc)
            .ToList();
        if (srcEntries.Count == 0)
        {
            string srcName = StripCopySuffix(_compSrc.name);
            srcEntries = srcAcl.componentValues
                .Where(cv => cv.component != null && StripCopySuffix(cv.component.gameObject.name) == srcName)
                .ToList();
        }

        foreach (var sv in srcEntries)
        {
            AddressableComponentValue FindDstVal(GameObject dstGo, AddressableComponentLoader acl)
            {
                var dv = acl.componentValues.FirstOrDefault(cv =>
                    cv.component != null &&
                    cv.component.gameObject == dstGo &&
                    cv.field == sv.field &&
                    cv.component.GetType() == sv.component.GetType());
                if (dv == null)
                {
                    string nm = StripCopySuffix(dstGo.name);
                    dv = acl.componentValues.FirstOrDefault(cv =>
                        cv.component != null &&
                        StripCopySuffix(cv.component.gameObject.name) == nm &&
                        cv.field == sv.field &&
                        cv.component.GetType() == sv.component.GetType());
                }
                return dv;
            }

            var dstAddrs = new HashSet<string>();
            AddressableComponentValue repDv = null;
            foreach (var dstGo in _compDsts)
            {
                var acl = FindAncestorAcl(dstGo.transform);
                if (acl == null) continue;
                var dv = FindDstVal(dstGo, acl);
                string addr = dv == null ? "<missing>" : AddressToAssetName(dv.address);
                dstAddrs.Add(addr);
                if (dstGo == _compDst) repDv = dv;
            }

            bool allSame = dstAddrs.Count == 1 && dstAddrs.Contains(AddressToAssetName(sv.address));
            if (allSame) continue;

            string srcAddr = AddressToAssetName(sv.address);
            string dstAddr = dstAddrs.Count == 1 ? dstAddrs.First() : "(various)";
            string typeLabel = AbbrevType(sv.component.GetType());
            string fieldLabel = AbbrevField(sv.field);
            string prefix = fieldLabel == typeLabel ? typeLabel : $"{typeLabel}.{fieldLabel}";
            _aclEntries.Add(new AclEntry { srcVal = sv, srcSnap = null, dstVal = repDv, label = prefix, srcAddr = srcAddr, dstAddr = dstAddr, selected = false });
        }
    }

    bool ComponentsEqual(Component a, Component b)
    {
        // Shallow serialized field comparison via SerializedObject
        var soA = new SerializedObject(a);
        var soB = new SerializedObject(b);
        var it = soA.GetIterator();
        it.Next(true);
        while (it.NextVisible(false))
        {
            var pB = soB.FindProperty(it.propertyPath);
            if (pB == null) return false;
            if (!SerializedProperty.DataEquals(it, pB)) return false;
        }
        return true;
    }

    void SetAllComp(bool val)
    {
        for (int i = 0; i < _compEntries.Count; i++) { var e = _compEntries[i]; if (!e.aclDriven) e.selected = val; _compEntries[i] = e; }
        for (int i = 0; i < _aclEntries.Count; i++)  { var e = _aclEntries[i]; e.selected = val; _aclEntries[i] = e; }
    }

    void ApplyCompCopy()
    {
        Undo.SetCurrentGroupName("Component Copy");
        int group = Undo.GetCurrentGroup();

        foreach (var dstGo in _compDsts)
        {
            // Copy selected components
            foreach (var entry in _compEntries.Where(e => e.selected && !e.aclDriven))
            {
                if (entry.srcComp != null)
                {
                    // Live source
                    var dstComp = dstGo.GetComponents<Component>()
                        .FirstOrDefault(c => c != null && c.GetType() == entry.srcComp.GetType());
                    if (dstComp == null)
                    {
                        var newComp = dstGo.AddComponent(entry.srcComp.GetType());
                        Undo.RegisterCreatedObjectUndo(newComp, "Component Copy");
                        CopySerializedFields(entry.srcComp, newComp);
                    }
                    else
                    {
                        Undo.RecordObject(dstComp, "Component Copy");
                        CopySerializedFields(entry.srcComp, dstComp);
                    }
                }
                else if (_lockedSrc != null)
                {
                    // Snapshot source — find matching snapshot by label
                    var snap = _lockedSrc.components.FirstOrDefault(s => s.typeName == entry.label || entry.label.EndsWith(s.typeName));
                    if (snap == null) continue;
                    var type = System.Type.GetType(snap.typeFullName);
                    if (type == null) continue;
                    var dstComp = dstGo.GetComponents<Component>()
                        .FirstOrDefault(c => c != null && c.GetType() == type);
                    if (dstComp == null)
                    {
                        dstComp = dstGo.AddComponent(type);
                        Undo.RegisterCreatedObjectUndo(dstComp, "Component Copy");
                    }
                    else
                    {
                        Undo.RecordObject(dstComp, "Component Copy");
                    }
                    if (snap.liveComp != null)
                        CopySerializedFields(snap.liveComp, dstComp);
                    else
                        EditorJsonUtility.FromJsonOverwrite(snap.yamlText, dstComp);
                }
            }

            // Copy ACL entries
            var dstAcl = FindAncestorAcl(dstGo.transform);
            if (dstAcl != null && _aclEntries.Any(e => e.selected))
            {
                Undo.RecordObject(dstAcl, "Component Copy — ACL");
                string dstGoName = StripCopySuffix(dstGo.name);

                foreach (var entry in _aclEntries.Where(e => e.selected))
                {
                    // Resolve address and type from live ref or snapshot
                    string field, address;
                    System.Type compType;
                    if (entry.srcVal != null)
                    {
                        field = entry.srcVal.field;
                        address = entry.srcVal.address;
                        compType = entry.srcVal.component.GetType();
                    }
                    else if (entry.srcSnap != null)
                    {
                        field = entry.srcSnap.field;
                        address = entry.srcSnap.address;
                        compType = System.Type.GetType(entry.srcSnap.typeFullName);
                        if (compType == null) continue;
                    }
                    else continue;

                    var dstComp = dstGo.GetComponents<Component>()
                        .FirstOrDefault(c => c != null && c.GetType() == compType);
                    if (dstComp == null) continue;

                    dstAcl.componentValues.RemoveAll(cv =>
                        cv.component != null &&
                        StripCopySuffix(cv.component.gameObject.name) == dstGoName &&
                        cv.field == field &&
                        cv.component.GetType() == compType);

                    dstAcl.componentValues.Add(new AddressableComponentValue
                    {
                        component = dstComp,
                        field     = field,
                        address   = address,
                    });
                }
                EditorUtility.SetDirty(dstAcl);
            }

            EditorUtility.SetDirty(dstGo);
        }

        Undo.CollapseUndoOperations(group);
        RebuildCompDiff();
        string names = _compDsts.Length == 1 ? $"'{_compDsts[0].name}'" : $"{_compDsts.Length} objects";
        Debug.Log($"[ComponentCopy] Applied to {names}.");
    }

    void CopySerializedFields(Component src, Component dst)
    {
        var soSrc = new SerializedObject(src);
        var soDst = new SerializedObject(dst);
        var it = soSrc.GetIterator();
        it.Next(true);
        while (it.NextVisible(false))
        {
            var pDst = soDst.FindProperty(it.propertyPath);
            if (pDst != null) soDst.CopyFromSerializedProperty(it);
        }
        soDst.ApplyModifiedProperties();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tab 1 — Material Shader
    // ══════════════════════════════════════════════════════════════════════════

    void DrawMaterialTab()
    {
        EditorGUILayout.LabelField("Material shader properties diff", EditorStyles.boldLabel);

        EditorGUI.BeginChangeCheck();
        _matSrc = (GameObject)EditorGUILayout.ObjectField("Source", _matSrc, typeof(GameObject), true);
        if (_matSrc != null)
        {
            int slots = MatSlotCount(_matSrc);
            if (slots > 1) _matSrcSlot = EditorGUILayout.IntSlider("Source slot", _matSrcSlot, 0, slots - 1);
            else _matSrcSlot = 0;
        }
        _matDst = (GameObject)EditorGUILayout.ObjectField("Target", _matDst, typeof(GameObject), true);
        if (_matDst != null)
        {
            int slots = MatSlotCount(_matDst);
            if (slots > 1) _matDstSlot = EditorGUILayout.IntSlider("Target slot", _matDstSlot, 0, slots - 1);
            else _matDstSlot = 0;
        }
        if (EditorGUI.EndChangeCheck()) _matDiffBuilt = false;

        if (_matSrc == null || _matDst == null)
        {
            EditorGUILayout.HelpBox("Assign both Source and Target GameObjects.", MessageType.Info);
            return;
        }

        if (!_matDiffBuilt)
        {
            if (GUILayout.Button("Build Diff")) BuildMatDiff();
            return;
        }

        if (_matProps.Count == 0)
        {
            EditorGUILayout.HelpBox("No non-texture property differences found.", MessageType.Info);
            return;
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Button("All",  GUILayout.Width(50))) SetAllMat(true);
        if (GUILayout.Button("None", GUILayout.Width(50))) SetAllMat(false);
        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField("Property", "Source  →  Target (kept)", EditorStyles.miniLabel);
        _matScroll = EditorGUILayout.BeginScrollView(_matScroll);
        for (int i = 0; i < _matProps.Count; i++)
        {
            var p = _matProps[i];
            string label = $"{p.name}:  {Truncate(p.srcVal, 30)}  →  {Truncate(p.dstVal, 30)}";
            EditorGUILayout.BeginHorizontal();
            bool sel = EditorGUILayout.ToggleLeft(label, p.selected);
            if (sel != p.selected) { p.selected = sel; _matProps[i] = p; }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.HelpBox("Textures are always kept from the Target.", MessageType.None);

        EditorGUILayout.Space(6);
        bool anySelected = _matProps.Any(p => p.selected);
        GUI.enabled = anySelected;
        if (GUILayout.Button("Apply Selected to Target Material"))
            ApplyMatCopy();
        GUI.enabled = true;
    }

    int MatSlotCount(GameObject go)
    {
        var mr = go.GetComponent<MeshRenderer>();
        return mr != null ? mr.sharedMaterials.Length : 0;
    }

    Material GetMat(GameObject go, int slot)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null || mr.sharedMaterials.Length <= slot) return null;
        return mr.sharedMaterials[slot];
    }

    string MatPath(GameObject go, int slot)
    {
        var mat = GetMat(go, slot);
        return mat != null ? AssetDatabase.GetAssetPath(mat) : null;
    }

    void BuildMatDiff()
    {
        _matProps.Clear();
        _matDiffBuilt = false;

        string srcPath = MatPath(_matSrc, _matSrcSlot);
        string dstPath = MatPath(_matDst, _matDstSlot);
        if (string.IsNullOrEmpty(srcPath) || string.IsNullOrEmpty(dstPath))
        {
            EditorUtility.DisplayDialog("Material Diff", "Could not find material asset paths for the selected slots.", "OK");
            return;
        }

        var srcProps = ParseMatProps(srcPath);
        var dstProps = ParseMatProps(dstPath);

        // Keywords diff
        string srcKw = DictGet(srcProps, "__keywords__");
        string dstKw = DictGet(dstProps, "__keywords__");
        if (srcKw != dstKw)
            _matProps.Add(new MatProp { name = "__ShaderKeywords__", srcVal = srcKw, dstVal = dstKw, selected = true });

        // Non-texture property diffs
        foreach (var key in srcProps.Keys.Union(dstProps.Keys).OrderBy(k => k))
        {
            if (key.StartsWith("__") || key.StartsWith("TEX_")) continue;
            string sv = DictGet(srcProps, key, "<missing>");
            string dv = DictGet(dstProps, key, "<missing>");
            if (sv != dv)
                _matProps.Add(new MatProp { name = key, srcVal = sv, dstVal = dv, selected = true });
        }

        _matDiffBuilt = true;
    }

    void SetAllMat(bool val)
    {
        for (int i = 0; i < _matProps.Count; i++) { var p = _matProps[i]; p.selected = val; _matProps[i] = p; }
    }

    void ApplyMatCopy()
    {
        string dstPath = MatPath(_matDst, _matDstSlot);
        string srcPath = MatPath(_matSrc, _matSrcSlot);
        if (string.IsNullOrEmpty(dstPath) || string.IsNullOrEmpty(srcPath)) return;

        string text = File.ReadAllText(dstPath);
        string srcText = File.ReadAllText(srcPath);

        foreach (var prop in _matProps.Where(p => p.selected))
        {
            if (prop.name == "__ShaderKeywords__")
            {
                // Replace everything between m_ShaderKeywords: and m_LightmapFlags
                text = Regex.Replace(text,
                    @"m_ShaderKeywords:.*?(?=\n\s*m_LightmapFlags)",
                    $"m_ShaderKeywords: {prop.srcVal}",
                    RegexOptions.Singleline);
            }
            else
            {
                // Float/color: "    - PropName: value\n" 
                string escaped = Regex.Escape(prop.name);
                // Replace existing
                var replaced = Regex.Replace(text,
                    $@"(- {escaped}: )(.+)",
                    $"${{1}}{prop.srcVal}");
                if (replaced == text && prop.dstVal == "<missing>")
                {
                    // Property missing in dst — insert after last float/color entry before m_Colors or end
                    // Find insertion point: just before "    m_Colors:" or "  m_BuildTextureStacks"
                    string insertAfter = prop.name.StartsWith("_") && IsColorProp(srcText, prop.name)
                        ? "    m_Colors:"
                        : "    m_Floats:";
                    // Insert at end of the relevant block
                    replaced = InsertPropBeforeSection(text, prop.name, prop.srcVal,
                        IsColorProp(srcText, prop.name));
                }
                text = replaced;
            }
        }

        File.WriteAllText(dstPath, text);
        AssetDatabase.ImportAsset(dstPath);
        BuildMatDiff();
        Debug.Log($"[ComponentCopy] Material properties applied to '{dstPath}'.");
    }

    bool IsColorProp(string matText, string propName)
    {
        // Check if the property appears in m_Colors section of the source
        int colorsIdx = matText.IndexOf("    m_Colors:");
        if (colorsIdx < 0) return false;
        int propIdx = matText.IndexOf($"- {propName}:", colorsIdx);
        return propIdx >= 0;
    }

    string InsertPropBeforeSection(string text, string propName, string value, bool isColor)
    {
        string sectionEnd = isColor ? "  m_BuildTextureStacks" : "    m_Colors:";
        int idx = text.IndexOf(sectionEnd);
        if (idx < 0) return text;
        string entry = isColor
            ? $"    - {propName}: {value}\n"
            : $"    - {propName}: {value}\n";
        return text.Insert(idx, entry);
    }

    // Parses floats, colors, textures, keywords from a .mat YAML file
    Dictionary<string, string> ParseMatProps(string path)
    {
        var props = new Dictionary<string, string>();
        string text = File.ReadAllText(path);

        foreach (Match m in Regex.Matches(text, @"- (_\w+): (.+)"))
            props[m.Groups[1].Value] = m.Groups[2].Value.Trim();

        foreach (Match m in Regex.Matches(text, @"- (_\w+):\n\s+m_Texture: \{fileID: (\d+), guid: (\w*)"))
            props[$"TEX_{m.Groups[1].Value}"] = $"fileID={m.Groups[2].Value} guid={m.Groups[3].Value}";

        // m_ShaderKeywords may wrap to a continuation line; capture everything up to m_LightmapFlags
        var kw = Regex.Match(text, @"m_ShaderKeywords:(.*?)(?=\n\s*m_LightmapFlags)", RegexOptions.Singleline);
        props["__keywords__"] = kw.Success ? kw.Groups[1].Value.Trim() : "";

        return props;
    }

    // ── Locked source snapshot ────────────────────────────────────────────────

    LockedSourceData CaptureSource(GameObject go)
    {
        var data = new LockedSourceData { goName = go.name };

        // Snapshot components — store live ref + YAML text via EditorJsonUtility
        foreach (var comp in go.GetComponents<Component>())
        {
            if (comp == null || comp is Transform) continue;
            data.components.Add(new LockedSourceData.CompSnapshot
            {
                typeName     = AbbrevType(comp.GetType()),
                typeFullName = comp.GetType().AssemblyQualifiedName,
                liveComp     = comp,
                yamlText     = EditorJsonUtility.ToJson(comp, prettyPrint: false),
            });
        }

        // Snapshot ACL entries for this GO
        var acl = FindAncestorAcl(go.transform);
        if (acl != null)
        {
            string goName = StripCopySuffix(go.name);
            foreach (var cv in acl.componentValues)
            {
                if (cv.component == null) continue;
                if (cv.component.gameObject != go && StripCopySuffix(cv.component.gameObject.name) != goName) continue;
                data.aclEntries.Add(new LockedSourceData.AclSnapshot
                {
                    typeFullName = cv.component.GetType().AssemblyQualifiedName,
                    field        = cv.field,
                    address      = cv.address,
                    addrLabel    = AddressToAssetName(cv.address),
                });
            }
        }

        return data;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static AddressableComponentLoader FindAncestorAcl(Transform t)
    {
        var p = t.parent;
        while (p != null)
        {
            var acl = p.GetComponent<AddressableComponentLoader>();
            if (acl != null) return acl;
            p = p.parent;
        }
        return null;
    }

    static string StripCopySuffix(string name)
    {
        name = name.Trim();
        var m = Regex.Match(name, @"^(.*?)\s+-\s+\d+$");
        if (m.Success) return m.Groups[1].Value.Trim();
        m = Regex.Match(name, @"^(.*?)\s+\(\d+\)$");
        if (m.Success) return m.Groups[1].Value.Trim();
        return name;
    }

    static string AddressToAssetName(string address)
    {
        if (string.IsNullOrEmpty(address)) return address ?? "";
        // If it looks like a GUID (32 hex chars), resolve via Addressables resource locations
        if (address.Length == 32 && System.Text.RegularExpressions.Regex.IsMatch(address, @"^[0-9a-fA-F]+$"))
        {
            try
            {
                var op = UnityEngine.AddressableAssets.Addressables.LoadResourceLocationsAsync(address);
                var locs = op.WaitForCompletion();
                if (locs != null && locs.Count > 0 && locs[0].PrimaryKey != null)
                    address = locs[0].PrimaryKey;
            }
            catch { /* catalogs not loaded — fall back to raw GUID */ }
        }
        return Path.GetFileNameWithoutExtension(address);
    }

    static string AbbrevType(System.Type t)
    {
        var name = t.Name;
        if (name == "StructurePart")             return "StructurePart";
        if (name == "EntityBlueprintComponent")  return "Blueprint";
        return name;
    }

    static string AbbrevField(string field)
    {
        // Strip Unity's m_ prefix and known redundant suffixes
        if (field.StartsWith("m_")) field = field.Substring(2);
        if (field.EndsWith("Asset")) field = field.Substring(0, field.Length - 5);
        return field;
    }

    static string Truncate(string s, int max) =>
        s != null && s.Length > max ? s.Substring(0, max) + "…" : s ?? "";

    static string DictGet(Dictionary<string, string> d, string key, string def = "")
    {
        string v;
        return d.TryGetValue(key, out v) ? v : def;
    }
}
