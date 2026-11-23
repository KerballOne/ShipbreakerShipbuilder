using System.Collections.Generic;
using System.IO;
using System.Text;
using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

// Adds the stock cryo-pipe MachinePartActionsComponent to baked parts whose ACL gives them a cryo
// blueprint, so they disarm (DisableAuxiliaryElementalEffects) when cryo is cut off, like stock pipes.
// Only touches GOs owned by the named prefab (not nested prefab instances). Report: .claude/bridge/out/cryo_mpa.txt
public static class AddCryoMachinePartActions
{
    static readonly HashSet<string> CryoBps = new HashSet<string>
    {
        "fa22fc2301d466a409cd1517d4055941", // BP_CryoPipe
        "207cfcb5900a4214d9c498fd35778c85", // BP_CryoPipe_Connection
        "25e77435b23c2cf44baf99a5e10412b5", // BP_CryoPipeWall
        "ea9f381e75da6944aa36eb3cbf7f78d7", // BP_Prop_Cryo_Powered
    };

    static readonly string[] Prefabs =
    {
        "Assets/_CustomShips/Tachi/Tachi.prefab",
        "Assets/_CustomShips/Tachi/Prefabs/Javelin/Interior Vessel.prefab",
    };

    const int Cryo = 4; // MachinePartAsset.MachineElementTypeFlags.Cryo

    [MenuItem("Shipbuilder/Debug/Add Cryo MachinePartActions To Baked Parts")]
    public static void Run()
    {
        var log = new StringBuilder();
        foreach (var path in Prefabs)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            int added = 0;
            try
            {
                var targets = new HashSet<GameObject>();
                foreach (var acl in root.GetComponentsInChildren<AddressableComponentLoader>(true))
                    foreach (var cv in acl.componentValues)
                        if (cv != null && cv.component != null && cv.field == "m_BlueprintAsset" && CryoBps.Contains(cv.address))
                            targets.Add(cv.component.gameObject);

                foreach (var go in targets)
                {
                    string p = GetPath(go.transform);
                    if (PrefabUtility.IsPartOfPrefabInstance(go)) { log.AppendLine($"SKIP nested  {path} :: {p}"); continue; }
                    if (go.GetComponent<MachinePartActionsComponent>() != null) { log.AppendLine($"SKIP has MPA {path} :: {p}"); continue; }
                    if (go.GetComponent<StructurePart>() == null || go.GetComponent<EntityBlueprintComponent>() == null)
                    { log.AppendLine($"SKIP no SP/EBC {path} :: {p}"); continue; }

                    var mpa = go.AddComponent<MachinePartActionsComponent>();
                    var so = new SerializedObject(mpa);
                    var con = so.FindProperty("m_OnElementConnectActions");
                    var dis = so.FindProperty("m_OnElementDisconnectActions");
                    con.arraySize = 1;
                    dis.arraySize = 1;
                    var c0 = con.GetArrayElementAtIndex(0);
                    c0.FindPropertyRelative("m_RequiredElements").intValue = Cryo;
                    c0.FindPropertyRelative("m_ModifyAuxiliaryElementalAbility").boolValue = false;
                    c0.FindPropertyRelative("m_AuxiliaryElementalEffectsEnabled").boolValue = true;
                    var d0 = dis.GetArrayElementAtIndex(0);
                    d0.FindPropertyRelative("m_RequiredElements").intValue = Cryo;
                    d0.FindPropertyRelative("m_ModifyAuxiliaryElementalAbility").boolValue = true;
                    d0.FindPropertyRelative("m_AuxiliaryElementalEffectsEnabled").boolValue = false;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    added++;
                    log.AppendLine($"ADDED        {path} :: {p}");
                }
                if (added > 0) PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            log.AppendLine($"== {path}: added {added}");
        }
        Directory.CreateDirectory(".claude/bridge/out");
        File.WriteAllText(".claude/bridge/out/cryo_mpa.txt", log.ToString());
    }

    static string GetPath(Transform t)
    {
        var s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }
}
