using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

public class ComponentCopyWindow : EditorWindow
{
    [MenuItem("Shipbuilder/Components", priority = 122)]
    public static void Open() => GetWindow<ComponentCopyWindow>("Components").Show();

    // ── shared ───────────────────────────────────────────────────────────────
    int _tab;
    readonly string[] _tabLabels = { "Copy", "Material Shader", "SP/BP" };

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
    // Snapshotted source material — survives prefab context exit
    class LockedMatSource
    {
        public string goName;
        public int slot;
        public string matPath;
        public string shaderGuid;
        public Dictionary<string, string> props;
    }

    GameObject _matSrc;
    string _matSrcName;
    LockedMatSource _lockedMatSrc;
    int _matSrcSlot;

    GameObject[] _matDstGos = new GameObject[0];
    int _matDstSlot;
    Vector2 _matScroll;

    struct MatProp
    {
        public string name;
        public string srcVal, dstVal;
        public bool selected;
    }
    List<MatProp> _matProps = new List<MatProp>();
    bool _matDiffBuilt;

    // ── Tab 2: Assign by Name ─────────────────────────────────────────────────
    string  m_AssignSpGuid, m_AssignSpName, m_AssignSpSearch;
    bool    m_AssignSpOpen;
    Vector2 m_AssignSpScroll;

    string  m_AssignBpGuid, m_AssignBpName, m_AssignBpSearch;
    bool    m_AssignBpOpen;
    Vector2 m_AssignBpScroll;

    bool m_AssignSpPreviewOpen = true;
    bool m_AssignBpPreviewOpen = true;
    readonly Dictionary<string, Vector2> m_PreviewScrolls = new Dictionary<string, Vector2>();
    readonly Dictionary<string, string> m_PreviewFilters = new Dictionary<string, string>();

    static List<(string name, string guid)> s_AssignSpEntries;
    static List<(string name, string guid)> s_AssignBpEntries;

    GameObject[] _assignLastSel = new GameObject[0];

    // ─────────────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        _tab = GUILayout.Toolbar(_tab, _tabLabels);
        EditorGUILayout.Space(4);

        if (_tab == 0)      DrawComponentTab();
        else if (_tab == 1) DrawMaterialTab();
        else                DrawAssignByNameTab();
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

        // ── Source GO picker + snapshot ──
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.BeginHorizontal();
        if (_matSrc == null && _lockedMatSrc != null)
        {
            GUI.enabled = false;
            EditorGUILayout.TextField("Source", _matSrcName ?? "(snapshot)");
            GUI.enabled = true;
            if (GUILayout.Button("Clear", GUILayout.Width(50)))
                { _lockedMatSrc = null; _matSrcName = null; _matDiffBuilt = false; }
        }
        else
        {
            var prev = _matSrc;
            _matSrc = (GameObject)EditorGUILayout.ObjectField("Source", _matSrc, typeof(GameObject), true);
            if (_matSrc != prev)
            {
                _matSrcName   = _matSrc != null ? _matSrc.name : null;
                _lockedMatSrc = _matSrc != null ? CaptureMatSource(_matSrc, _matSrcSlot) : null;
                _matDiffBuilt = false;
            }
        }
        EditorGUILayout.EndHorizontal();

        if (_matSrc != null || _lockedMatSrc != null)
        {
            int slots = _matSrc != null ? MatSlotCount(_matSrc) : 1;
            if (slots > 1)
            {
                int prev = _matSrcSlot;
                _matSrcSlot = EditorGUILayout.IntSlider("Source slot", _matSrcSlot, 0, slots - 1);
                if (_matSrcSlot != prev && _matSrc != null)
                {
                    _lockedMatSrc = CaptureMatSource(_matSrc, _matSrcSlot);
                    _matDiffBuilt = false;
                }
            }
            else _matSrcSlot = 0;

            // Show resolved material name
            if (_lockedMatSrc != null)
            {
                var srcMat = AssetDatabase.LoadAssetAtPath<Material>(_lockedMatSrc.matPath);
                GUI.enabled = false;
                EditorGUILayout.ObjectField("  Material", srcMat, typeof(Material), false);
                GUI.enabled = true;
            }
        }
        bool srcChanged = EditorGUI.EndChangeCheck();

        if (GUILayout.Button("Set Source from Selection", EditorStyles.miniButton))
        {
            var picked = Selection.activeGameObject;
            if (picked != null)
            {
                _matSrc       = picked;
                _matSrcName   = picked.name;
                _lockedMatSrc = CaptureMatSource(picked, _matSrcSlot);
                _matDiffBuilt = false;
            }
        }
        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        // ── Targets from Hierarchy selection ──
        var selGos = Selection.gameObjects
            .Where(go => go != _matSrc)
            .ToArray();
        if (!selGos.SequenceEqual(_matDstGos)) { _matDstGos = selGos; _matDiffBuilt = false; }

        // Show GO count and resolve unique target materials
        string targetGoLabel = _matDstGos.Length == 0 ? "—"
            : _matDstGos.Length == 1 ? _matDstGos[0].name
            : $"{_matDstGos.Length} objects selected";
        EditorGUILayout.LabelField("Target", targetGoLabel);

        if (_matDstGos.Length > 0)
        {
            int slots = MatSlotCount(_matDstGos[0]);
            if (slots > 1)
            {
                int prev = _matDstSlot;
                _matDstSlot = EditorGUILayout.IntSlider("Target slot", _matDstSlot, 0, slots - 1);
                if (_matDstSlot != prev) _matDiffBuilt = false;
            }
            else _matDstSlot = 0;

            // Resolve and display unique target materials
            var uniqueDstMats = _matDstGos
                .Select(go => GetMat(go, _matDstSlot))
                .Where(m => m != null)
                .Distinct()
                .ToArray();
            if (uniqueDstMats.Length == 1)
            {
                GUI.enabled = false;
                EditorGUILayout.ObjectField("  Material", uniqueDstMats[0], typeof(Material), false);
                GUI.enabled = true;
            }
            else if (uniqueDstMats.Length > 1)
                EditorGUILayout.LabelField("  Material", $"{uniqueDstMats.Length} unique materials");
        }

        EditorGUILayout.Space(2);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        if ((_matSrc == null && _lockedMatSrc == null) || _matDstGos.Length == 0)
        {
            EditorGUILayout.HelpBox("Set Source and select one or more Target GameObjects in the Hierarchy.", MessageType.Info);
            return;
        }

        // ── Shader guard: check all unique target materials ──
        string srcShaderGuid = _lockedMatSrc?.shaderGuid;
        if (!string.IsNullOrEmpty(srcShaderGuid))
        {
            var mismatch = _matDstGos
                .Select(go => GetMat(go, _matDstSlot))
                .Where(m => m != null)
                .Distinct()
                .Where(m => MatShaderGuidFromPath(AssetDatabase.GetAssetPath(m)) != srcShaderGuid)
                .ToArray();
            if (mismatch.Length > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Shader mismatch on {mismatch.Length} target material(s) (e.g. '{mismatch[0].name}') — applying across different shaders will corrupt the material.",
                    MessageType.Error);
                return;
            }
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
            bool propSel = EditorGUILayout.ToggleLeft(label, p.selected);
            if (propSel != p.selected) { p.selected = propSel; _matProps[i] = p; }
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.HelpBox("Textures are always kept from the Target.", MessageType.None);

        EditorGUILayout.Space(6);
        bool anySelected = _matProps.Any(p => p.selected);
        GUI.enabled = anySelected;
        var uniqueMats = _matDstGos.Select(go => GetMat(go, _matDstSlot)).Where(m => m != null).Distinct().ToArray();
        string applyLabel = uniqueMats.Length == 1
            ? $"Apply Selected to '{uniqueMats[0].name}'"
            : $"Apply Selected to {uniqueMats.Length} Materials";
        if (GUILayout.Button(applyLabel))
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

    LockedMatSource CaptureMatSource(GameObject go, int slot)
    {
        var mat = GetMat(go, slot);
        if (mat == null) return null;
        string path = AssetDatabase.GetAssetPath(mat);
        if (string.IsNullOrEmpty(path)) return null;
        var shaderMatch = Regex.Match(File.ReadAllText(path), @"m_Shader:.*?guid:\s*(\w+)");
        return new LockedMatSource
        {
            goName     = go.name,
            slot       = slot,
            matPath    = path,
            shaderGuid = shaderMatch.Success ? shaderMatch.Groups[1].Value : null,
            props      = ParseMatProps(path),
        };
    }

    string MatShaderGuidFromPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var m = Regex.Match(File.ReadAllText(path), @"m_Shader:.*?guid:\s*(\w+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    void BuildMatDiff()
    {
        _matProps.Clear();
        _matDiffBuilt = false;

        if (_lockedMatSrc == null || _matDstGos.Length == 0) return;

        var srcProps = _lockedMatSrc.props;
        var repMat = GetMat(_matDstGos[0], _matDstSlot);
        string dstPath = repMat != null ? AssetDatabase.GetAssetPath(repMat) : null;
        if (string.IsNullOrEmpty(dstPath)) { EditorUtility.DisplayDialog("Material Diff", "Could not find target material.", "OK"); return; }
        var dstProps = ParseMatProps(dstPath);

        // Keywords diff — show source vs representative first target
        string srcKw = DictGet(srcProps, "__keywords__");
        string dstKw = DictGet(dstProps, "__keywords__");
        if (srcKw != dstKw)
            _matProps.Add(new MatProp { name = "__ShaderKeywords__", srcVal = srcKw, dstVal = dstKw, selected = false });

        // Non-texture property diffs — default unchecked
        foreach (var key in srcProps.Keys.Union(dstProps.Keys).OrderBy(k => k))
        {
            if (key.StartsWith("__") || key.StartsWith("TEX_")) continue;
            string sv = DictGet(srcProps, key, "<missing>");
            string dv = DictGet(dstProps, key, "<missing>");
            if (sv != dv)
                _matProps.Add(new MatProp { name = key, srcVal = sv, dstVal = dv, selected = false });
        }

        _matDiffBuilt = true;
    }

    void SetAllMat(bool val)
    {
        for (int i = 0; i < _matProps.Count; i++) { var p = _matProps[i]; p.selected = val; _matProps[i] = p; }
    }

    void ApplyMatCopy()
    {
        if (_lockedMatSrc == null) return;
        string srcText = File.Exists(_lockedMatSrc.matPath) ? File.ReadAllText(_lockedMatSrc.matPath) : "";
        var selectedProps = _matProps.Where(p => p.selected).ToList();

        // Collect unique target material paths
        var uniquePaths = _matDstGos
            .Select(go => GetMat(go, _matDstSlot))
            .Where(m => m != null)
            .Distinct()
            .Select(m => AssetDatabase.GetAssetPath(m))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
        int applied = 0;

        foreach (var dstPath in uniquePaths)
        {

            string text = File.ReadAllText(dstPath);

            foreach (var prop in selectedProps)
            {
                if (prop.name == "__ShaderKeywords__")
                {
                    text = Regex.Replace(text,
                        @"m_ShaderKeywords:.*?(?=\n\s*m_LightmapFlags)",
                        $"m_ShaderKeywords: {prop.srcVal}",
                        RegexOptions.Singleline);
                }
                else
                {
                    string escaped = Regex.Escape(prop.name);
                    var replaced = Regex.Replace(text, $@"(- {escaped}: )(.+)", $"${{1}}{prop.srcVal}");
                    if (replaced == text && prop.dstVal == "<missing>")
                        replaced = InsertPropBeforeSection(text, prop.name, prop.srcVal, IsColorProp(srcText, prop.name));
                    text = replaced;
                }
            }

            File.WriteAllText(dstPath, text);
            AssetDatabase.ImportAsset(dstPath);
            applied++;
        }

        BuildMatDiff();
        string names = uniquePaths.Count == 1 ? $"'{Path.GetFileNameWithoutExtension(uniquePaths[0])}'" : $"{applied}/{uniquePaths.Count} materials";
        Debug.Log($"[ComponentCopy] Material properties applied to {names}.");
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

    // ══════════════════════════════════════════════════════════════════════════
    // Tab 2 — Assign by Name
    // ══════════════════════════════════════════════════════════════════════════

    void DrawAssignByNameTab()
    {
        EditorGUILayout.LabelField("Assign StructurePart / Blueprint by name", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Picks a known SP_Mat / BP_Mat by name and writes it into the target's ACL. " +
            "No source GameObject required — use this when nothing existing already carries the component you want.",
            MessageType.Info);

        var targets = Selection.gameObjects;
        string targetLabel = targets.Length == 0 ? "—"
            : targets.Length == 1 ? targets[0].name
            : $"{targets.Length} objects selected";
        EditorGUILayout.LabelField("Target", targetLabel);

        if (targets.Length == 0)
        {
            EditorGUILayout.HelpBox("Select one or more target GameObjects in the Hierarchy.", MessageType.Info);
            return;
        }

        if (!targets.SequenceEqual(_assignLastSel))
        {
            _assignLastSel = targets;
            AutoPopulateAssignFromSelection(targets);
        }

        var missingAcl = targets.Where(t => FindAncestorAcl(t.transform) == null).ToArray();
        if (missingAcl.Length > 0)
        {
            EditorGUILayout.HelpBox(
                $"{missingAcl.Length} target(s) have no ancestor AddressableComponentLoader (e.g. '{missingAcl[0].name}') — cannot assign.",
                MessageType.Error);
            return;
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("StructurePart (SP_Mat)", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox(
            "Governs physical/cutting properties: cut grade/stage (CuttingTargetable), mass density and " +
            "rigidbody behavior, joint behavior and room/atmosphere sealing (JointSetup), and " +
            "vaporize/yank-on-cut (BreakableJoint).",
            MessageType.None);
        DrawRefOverride(ref m_AssignSpGuid, ref m_AssignSpName, ref m_AssignSpSearch,
            ref m_AssignSpOpen, ref m_AssignSpScroll, GetAssignSpEntries);
        DrawSpBpSummary(m_AssignSpName, isBlueprint: false);
        DrawAssetPreview(m_AssignSpName, ref m_AssignSpPreviewOpen);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Blueprint (BP_Mat)", EditorStyles.miniBoldLabel);
        EditorGUILayout.HelpBox(
            "Governs ECS entity setup: fuel/coolant network membership, salvage destination " +
            "(Furnace/Barge/Processor), pressure explosion logic, vitality/health, and scanner HUD entry.",
            MessageType.None);
        DrawRefOverride(ref m_AssignBpGuid, ref m_AssignBpName, ref m_AssignBpSearch,
            ref m_AssignBpOpen, ref m_AssignBpScroll, GetAssignBpEntries);
        DrawSpBpSummary(m_AssignBpName, isBlueprint: true);
        DrawAssetPreview(m_AssignBpName, ref m_AssignBpPreviewOpen);

        EditorGUILayout.Space(6);
        bool anyChosen = !string.IsNullOrEmpty(m_AssignSpGuid) || !string.IsNullOrEmpty(m_AssignBpGuid);
        GUI.enabled = anyChosen;
        string applyLabel = targets.Length == 1
            ? $"Assign to '{targets[0].name}'"
            : $"Assign to {targets.Length} objects";
        if (GUILayout.Button(applyLabel))
            ApplyAssignByName(targets);
        GUI.enabled = true;

        EditorGUILayout.Space(6);
        var lineRect = EditorGUILayout.GetControlRect(false, 1f);
        EditorGUI.DrawRect(lineRect, new Color(0.35f, 0.35f, 0.35f, 1f));
        EditorGUILayout.Space(6);

        var parts = new List<StructurePart>();
        foreach (var go in targets)
            foreach (var sp in go.GetComponentsInChildren<StructurePart>(true))
                if (!parts.Contains(sp)) parts.Add(sp);

        GUI.enabled = parts.Count > 0;
        if (GUILayout.Button("Set Display Name…"))
        {
            string existing = "";
            foreach (var sp in parts)
            {
                var so = new SerializedObject(sp);
                if (so.FindProperty("m_ObjectInfoAssetOverride")?.objectReferenceValue is ObjectInfoAsset oi)
                {
                    existing = new SerializedObject(oi).FindProperty("m_Data.m_ObjectName")?.stringValue ?? "";
                    if (!string.IsNullOrEmpty(existing)) break;
                }
            }
            var dataFolder = SetBakedPartDisplayName.FindShipDataFolder(parts);
            SetDisplayNameWizard.OpenForSceneParts(parts, existing, dataFolder);
        }
        GUI.enabled = true;
    }

    // Pre-fills the SP_Mat / BP_Mat pickers from whatever is already assigned on the
    // selected target(s), so the tab shows current state instead of always starting blank.
    // Only populates when every target agrees on the same asset — if they differ, Apply
    // would otherwise silently overwrite all of them to a single value, so leave blank instead.
    void AutoPopulateAssignFromSelection(GameObject[] targets)
    {
        m_AssignSpGuid = ""; m_AssignSpName = ""; m_AssignSpOpen = false; m_AssignSpSearch = "";
        m_AssignBpGuid = ""; m_AssignBpName = ""; m_AssignBpOpen = false; m_AssignBpSearch = "";

        string spGuid = null, bpGuid = null;
        bool spAgree = true, bpAgree = true;

        foreach (var go in targets)
        {
            var acl = FindAncestorAcl(go.transform);
            if (acl == null) continue;
            string goName = StripCopySuffix(go.name);

            var sp = go.GetComponent<StructurePart>();
            var spCv = sp != null ? acl.componentValues.FirstOrDefault(cv =>
                cv.component != null &&
                StripCopySuffix(cv.component.gameObject.name) == goName &&
                cv.field == "m_StructurePartAsset" &&
                cv.component.GetType() == typeof(StructurePart)) : null;
            string thisSp = spCv?.address;
            if (spGuid == null && thisSp != null) spGuid = thisSp;
            else if (thisSp != null && thisSp != spGuid) spAgree = false;

            var bp = go.GetComponent<EntityBlueprintComponent>();
            var bpCv = bp != null ? acl.componentValues.FirstOrDefault(cv =>
                cv.component != null &&
                StripCopySuffix(cv.component.gameObject.name) == goName &&
                cv.field == "m_BlueprintAsset" &&
                cv.component.GetType() == typeof(EntityBlueprintComponent)) : null;
            string thisBp = bpCv?.address;
            if (bpGuid == null && thisBp != null) bpGuid = thisBp;
            else if (thisBp != null && thisBp != bpGuid) bpAgree = false;
        }

        if (spAgree && !string.IsNullOrEmpty(spGuid))
        {
            var entry = GetAssignSpEntries().FirstOrDefault(e => e.guid == spGuid);
            m_AssignSpGuid = spGuid;
            m_AssignSpName = entry.name ?? AddressToAssetName(spGuid);
        }

        if (bpAgree && !string.IsNullOrEmpty(bpGuid))
        {
            var entry = GetAssignBpEntries().FirstOrDefault(e => e.guid == bpGuid);
            m_AssignBpGuid = bpGuid;
            m_AssignBpName = entry.name ?? AddressToAssetName(bpGuid);
        }
    }

    void ApplyAssignByName(GameObject[] targets)
    {
        Undo.SetCurrentGroupName("Assign SP/BP by Name");
        int group = Undo.GetCurrentGroup();

        int applied = 0;
        foreach (var go in targets)
        {
            var acl = FindAncestorAcl(go.transform);
            if (acl == null) continue;

            if (!string.IsNullOrEmpty(m_AssignSpGuid))
            {
                var sp = go.GetComponent<StructurePart>();
                if (sp == null) { sp = go.AddComponent<StructurePart>(); Undo.RegisterCreatedObjectUndo(sp, "Assign SP/BP by Name"); }
                AssignAclValue(acl, go, sp, "m_StructurePartAsset", m_AssignSpGuid);
                applied++;
            }

            if (!string.IsNullOrEmpty(m_AssignBpGuid))
            {
                var bp = go.GetComponent<EntityBlueprintComponent>();
                if (bp == null) { bp = go.AddComponent<EntityBlueprintComponent>(); Undo.RegisterCreatedObjectUndo(bp, "Assign SP/BP by Name"); }
                AssignAclValue(acl, go, bp, "m_BlueprintAsset", m_AssignBpGuid);
                applied++;
            }

            EditorUtility.SetDirty(acl);
            EditorUtility.SetDirty(go);
        }

        Undo.CollapseUndoOperations(group);
        Debug.Log($"[ComponentCopy] Assigned by name to {targets.Length} object(s), {applied} ACL entr{(applied == 1 ? "y" : "ies")} written.");
    }

    static void AssignAclValue(AddressableComponentLoader acl, GameObject go, Component comp, string field, string guid)
    {
        Undo.RecordObject(acl, "Assign SP/BP by Name");
        string goName = StripCopySuffix(go.name);
        acl.componentValues.RemoveAll(cv =>
            cv.component != null &&
            StripCopySuffix(cv.component.gameObject.name) == goName &&
            cv.field == field &&
            cv.component.GetType() == comp.GetType());
        acl.componentValues.Add(new AddressableComponentValue { component = comp, field = field, address = guid });
    }

    static List<(string name, string guid)> GetAssignSpEntries()
    {
        if (s_AssignSpEntries != null) return s_AssignSpEntries;
        s_AssignSpEntries = new List<(string, string)>();
        if (LoadGameAssets.knownAssetMap == null) return s_AssignSpEntries;
        foreach (var kv in LoadGameAssets.knownAssetMap)
        {
            if (!kv.Value.Contains("/StructurePartAsset/") || !kv.Value.EndsWith(".asset")) continue;
            s_AssignSpEntries.Add((Path.GetFileNameWithoutExtension(kv.Value), kv.Key));
        }
        s_AssignSpEntries.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
        return s_AssignSpEntries;
    }

    static List<(string name, string guid)> GetAssignBpEntries()
    {
        if (s_AssignBpEntries != null) return s_AssignBpEntries;
        s_AssignBpEntries = new List<(string, string)>();
        if (LoadGameAssets.knownAssetMap == null) return s_AssignBpEntries;
        foreach (var kv in LoadGameAssets.knownAssetMap)
        {
            if (!kv.Value.EndsWith(".asset")) continue;
            string name = Path.GetFileNameWithoutExtension(kv.Value);
            if (!name.StartsWith("BP_")) continue;
            s_AssignBpEntries.Add((name, kv.Key));
        }
        s_AssignBpEntries.Sort((a, b) => string.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase));
        return s_AssignBpEntries;
    }

    // ── Shared ref-override UI (searchable name dropdown) ───────────────────────

    void DrawRefOverride(
        ref string guid, ref string displayName,
        ref string search, ref bool open, ref Vector2 scroll,
        System.Func<List<(string name, string guid)>> getEntries)
    {
        var entries = getEntries();

        EditorGUILayout.BeginHorizontal();

        string btnLabel = string.IsNullOrEmpty(displayName) ? "(none)" : displayName;
        if (GUILayout.Button(btnLabel, EditorStyles.popup))
        {
            open = !open;
            if (open)
            {
                // The search TextField's IMGUI control state (keyboard focus / recycled editor
                // text) can persist across the field disappearing and reappearing, showing stale
                // text even though `search` itself was already cleared — force focus away so the
                // field re-reads the current `search` value fresh on reopen.
                GUIUtility.keyboardControl = 0;
                EditorGUIUtility.editingTextField = false;
            }
        }

        if (!string.IsNullOrEmpty(guid))
        {
            if (GUILayout.Button("✕", GUILayout.Width(24)))
            {
                guid = ""; displayName = ""; open = false; search = "";
            }
        }
        EditorGUILayout.EndHorizontal();

        if (open)
        {
            search = EditorGUILayout.TextField(
                new GUIContent("Search", "Space-separated terms, all must match. Prefix a term with - to exclude it, e.g. \"panel -aft\"."),
                search);

            var filtered = new List<(string name, string guid)>();
            foreach (var e in entries)
                if (MatchesSearch(e.name, search))
                    filtered.Add(e);

            float rowH  = EditorGUIUtility.singleLineHeight + 2;
            float maxH  = 200f;
            float listH = Mathf.Min(maxH, filtered.Count * rowH + 4);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(listH));

            var capturedTable = LoadSpBpFields();

            foreach (var (eName, eGuid) in filtered)
            {
                bool selected  = eGuid == guid;
                bool captured  = capturedTable != null && capturedTable.TryGetValue(eName, out var f) && f != null && f.Count > 0;
                var  style     = new GUIStyle(EditorStyles.label);
                if (selected) style.fontStyle = FontStyle.Bold;
                if (captured) style.normal.textColor = style.onNormal.textColor = new Color(0.4f, 0.85f, 0.4f);

                string label = captured ? $"{eName}  ✓" : eName;
                if (GUILayout.Button(label, style, GUILayout.Height(rowH)))
                {
                    // Search term is intentionally preserved across a pick — reopening the
                    // dropdown should show the same filtered list again, not reset to blank.
                    guid = eGuid; displayName = eName; open = false;
                }
            }

            GUILayout.EndScrollView();
        }

        if (!string.IsNullOrEmpty(guid))
            EditorGUILayout.HelpBox($"{displayName}\n{guid}", MessageType.None);
    }

    // SP_Mat/BP_Mat assets only exist inside the shipped game's asset bundles — there is no
    // loose .asset file in this project to inspect. Instead, PartInfoLogger (the runtime mod)
    // reflects over the live asset's fields in-game and writes sp_bp_fields.json next to
    // known_assets_enriched.json. This reads that dump by asset name, keyed off the display
    // name already resolved by GetAssignSpEntries/GetAssignBpEntries.
    static Dictionary<string, Dictionary<string, object>> s_SpBpFields;
    static System.DateTime s_SpBpFieldsMTime;

    // Re-reads sp_bp_fields.json whenever its on-disk write time changes, instead of caching
    // forever — PartInfoLogger rewrites this file every gameplay session, and a stale in-memory
    // cache from an earlier (possibly file-not-found) load would otherwise hide new data until
    // the Unity Editor process restarts.
    static Dictionary<string, Dictionary<string, object>> LoadSpBpFields()
    {
        var path = Path.Combine(Application.dataPath, "..", "sp_bp_fields.json");
        if (!File.Exists(path)) { s_SpBpFields = null; return null; }

        var mtime = File.GetLastWriteTimeUtc(path);
        if (s_SpBpFields != null && mtime == s_SpBpFieldsMTime) return s_SpBpFields;

        try
        {
            s_SpBpFields = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(File.ReadAllText(path));
            s_SpBpFieldsMTime = mtime;
        }
        catch { s_SpBpFields = null; }
        return s_SpBpFields;
    }

    // Always-visible compact summary of the fields called out in the SP/BP HelpBox descriptions
    // above — pulled from the same sp_bp_fields.json PartInfoLogger dump DrawAssetPreview uses,
    // but surfaced without needing to expand "Preview captured fields". Field key names confirmed
    // against actual captured data (SP: CuttingTargetable/IRigidbodyAsset/JointSetup/BreakableJoint;
    // BP: MachinePartAsset fuel/cryo control, SalvageableComponentAsset options — room sealing and
    // rigidbody type only ever appear on SP assets, never BP, so they're SP-only rows here).
    void DrawSpBpSummary(string displayName, bool isBlueprint)
    {
        if (string.IsNullOrEmpty(displayName)) return;
        var table = LoadSpBpFields();
        if (table == null || !table.TryGetValue(displayName, out var fields) || fields == null) return;

        var rows = new List<(string label, string value)>();

        if (!isBlueprint)
        {
            AddRow(rows, fields, "Cut grade/stage", "Data.m_CuttingTargetableAsset.Data.m_PowerRating");
            AddRow(rows, fields, "Mass density", "Data.m_IRigidbodyAsset.Data.m_DensityOrMass");
            AddRow(rows, fields, "Joint setup", "Data.m_JointSetupAsset@ref", "Data.m_JointSetupAsset.name");
            AddRow(rows, fields, "Room sealing", "Data.m_JointSetupAsset.m_CanSealRoom", "Data.m_JointSetupAsset.CanSealRoom");
            AddRow(rows, fields, "Vaporize on cut", "Data.m_ShatterableComponentAsset.Data.m_VaporizationOverrideAsset@ref");
            AddRow(rows, fields, "Yank lock on start", "Data.m_BreakableJointAsset.Data.m_YankLockOnStart");
            AddFlagContainsRow(rows, fields, "Breakable by grapple", "Data.m_BreakableJointAsset.Data.m_BreakableBy", "Grapple");
        }
        else
        {
            AddRow(rows, fields, "Fuel control", FindKeyContaining(fields, "m_FuelControl"));
            AddRow(rows, fields, "Cryo control", FindKeyContaining(fields, "m_CryoControl"));
            AddRow(rows, fields, "Salvage destination", FindKeyContaining(fields, "m_PossibleSalvageableOptions"));
        }

        if (rows.Count == 0) return;

        var valStyleTrue     = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.4f, 0.85f, 0.4f) } };
        var valStyleFalse    = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.9f, 0.4f, 0.4f) } };
        var valStyleFurnace  = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.9f, 0.4f, 0.4f) } };
        var valStyleProcessor= new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.4f, 0.85f, 0.9f) } };
        var valStyleBarge    = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.4f, 0.85f, 0.4f) } };

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        foreach (var (label, value) in rows)
        {
            GUIStyle valStyle = value == "True" ? valStyleTrue
                : value == "False" ? valStyleFalse
                : label == "Salvage destination" && value == "Furnace" ? valStyleFurnace
                : label == "Salvage destination" && value == "Processor" ? valStyleProcessor
                : label == "Salvage destination" && value == "Barge" ? valStyleBarge
                : EditorStyles.boldLabel;
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(130));
            EditorGUILayout.LabelField(value, valStyle);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndVertical();
    }

    // Tries each candidate key in order, using the first one present in fields.
    static void AddRow(List<(string, string)> rows, Dictionary<string, object> fields, string label, params string[] candidateKeys)
    {
        foreach (var key in candidateKeys)
        {
            if (key == null) continue;
            if (fields.TryGetValue(key, out var val) && val != null)
            {
                rows.Add((label, val.ToString()));
                return;
            }
        }
    }

    // Some captured field paths carry array indices that vary per asset (e.g.
    // "m_ComponentDataAssets[6].Data.m_FuelControl") — match by suffix instead of exact key.
    static string FindKeyContaining(Dictionary<string, object> fields, string suffix)
    {
        foreach (var key in fields.Keys)
            if (key.EndsWith(suffix, System.StringComparison.Ordinal))
                return key;
        return null;
    }

    // m_BreakableBy is a flags enum captured as a comma-separated string (e.g. "Tether, Grapple,
    // GrappleThrow"), not a bool — show it as a true/false row for a single flag of interest.
    static void AddFlagContainsRow(List<(string, string)> rows, Dictionary<string, object> fields, string label, string key, string flagName)
    {
        if (!fields.TryGetValue(key, out var val) || val == null) return;
        bool has = val.ToString().Split(',').Any(f => f.Trim().Equals(flagName, System.StringComparison.OrdinalIgnoreCase));
        rows.Add((label, has ? "True" : "False"));
    }

    void DrawAssetPreview(string displayName, ref bool open)
    {
        if (string.IsNullOrEmpty(displayName)) return;

        var table = LoadSpBpFields();
        if (table == null)
        {
            EditorGUILayout.HelpBox(
                "sp_bp_fields.json not found in project root — run the game once with PartInfoLogger installed to generate it.",
                MessageType.Info);
            return;
        }

        if (!table.TryGetValue(displayName, out var fields) || fields == null || fields.Count == 0)
        {
            EditorGUILayout.HelpBox($"No captured fields for '{displayName}' yet — this asset hasn't been seen in-game by PartInfoLogger.", MessageType.Info);
            return;
        }

        open = EditorGUILayout.Foldout(open, $"Preview captured fields ({fields.Count})", true);
        if (!open) return;

        var scrollKey = displayName;
        if (!m_PreviewScrolls.TryGetValue(scrollKey, out var scroll)) scroll = Vector2.zero;
        m_PreviewFilters.TryGetValue(scrollKey, out var filter);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        var newFilter = EditorGUILayout.TextField("Filter", filter ?? "");
        if (newFilter != filter) m_PreviewFilters[scrollKey] = filter = newFilter;

        IEnumerable<KeyValuePair<string, object>> shown = fields;
        if (!string.IsNullOrEmpty(filter))
            shown = fields.Where(kv => kv.Key.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0
                                     || (kv.Value?.ToString().IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
        var shownList = shown.OrderBy(kv => kv.Key, System.StringComparer.OrdinalIgnoreCase).ToList();

        // Key names can be long (e.g. "Data.m_AudioMaterialAsset.Data.m_AudioMaterialAsset...") and a
        // fixed-width side-by-side column truncates them with no way to read the rest. Stack key above
        // value instead so the full key is always visible (word-wrapped). Height is a fraction of the
        // window's current height (not a fixed px value) so it scales consistently as the window is
        // resized, and stays bounded when both SP and BP previews are open at once.
        var keyStyle = new GUIStyle(EditorStyles.wordWrappedLabel) { fontSize = 9, fontStyle = FontStyle.Normal };
        var valStyleTrue  = new GUIStyle(EditorStyles.wordWrappedLabel) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.4f, 0.85f, 0.4f) } };
        var valStyleFalse = new GUIStyle(EditorStyles.wordWrappedLabel) { fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.9f, 0.4f, 0.4f) } };
        var valStyleOther = new GUIStyle(EditorStyles.wordWrappedLabel) { fontStyle = FontStyle.Bold };

        float previewHeight = Mathf.Max(120f, position.height * 0.22f);
        scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(previewHeight));
        foreach (var kv in shownList)
        {
            EditorGUILayout.LabelField(kv.Key, keyStyle);
            string valStr = kv.Value?.ToString() ?? "null";
            var valStyle = valStr == "True" ? valStyleTrue : valStr == "False" ? valStyleFalse : valStyleOther;
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(12);
            EditorGUILayout.LabelField(valStr, valStyle);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }
        if (shownList.Count == 0)
            EditorGUILayout.LabelField("  No fields match filter.", EditorStyles.miniLabel);
        EditorGUILayout.EndScrollView();

        EditorGUILayout.EndVertical();

        m_PreviewScrolls[scrollKey] = scroll;
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

    // Space-separated search terms, all must match (AND); a term prefixed with "-" must NOT
    // match instead (e.g. "panel -aft" finds names containing "panel" but not "aft").
    // Underscores/spaces are stripped from both the name and each term before comparing, so
    // "fuelxl" matches "SP_Element_Fuel_XL" without needing exact underscore placement.
    static bool MatchesSearch(string name, string search)
    {
        if (string.IsNullOrEmpty(search)) return true;
        string lname = NormalizeForSearch(name);
        foreach (var raw in search.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
        {
            bool exclude = raw.Length > 1 && raw[0] == '-';
            string term = NormalizeForSearch(exclude ? raw.Substring(1) : raw);
            if (term.Length == 0) continue;
            bool contains = lname.Contains(term);
            if (exclude && contains) return false;
            if (!exclude && !contains) return false;
        }
        return true;
    }

    static string NormalizeForSearch(string s) =>
        s.ToLowerInvariant().Replace("_", "").Replace(" ", "");

    static string Truncate(string s, int max) =>
        s != null && s.Length > max ? s.Substring(0, max) + "…" : s ?? "";

    static string DictGet(Dictionary<string, string> d, string key, string def = "")
    {
        string v;
        return d.TryGetValue(key, out v) ? v : def;
    }
}
