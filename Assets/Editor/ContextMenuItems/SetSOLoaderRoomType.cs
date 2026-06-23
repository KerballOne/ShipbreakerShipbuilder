using BBI.Unity.Game;
using UnityEditor;
using UnityEngine;

public static class SetSOLoaderRoomType
{
    // DynamicRoomContainerAsset GUIDs from USER_GUIDE Section 4.3
    private static readonly (string label, string guid)[] kRoomTypes = new[]
    {
        ("Airlock",                    "1e2fc202254a9b142821666f0de99c43"),
        ("Bathroom",                   "1618146055ee06241a21a0a070fcb285"),
        ("Bulkhead",                   "27f96a65f1a36ce42879c5c6b295e9cf"),
        ("BulkheadStructure",          "be0601d7017703647a188f1690c9a487"),
        ("Cabin",                      "1890b7b43c4fe394fade0ed5247ce74f"),
        ("CargoBay",                   "944e7dc3b121bc842a1d206109d5ed3f"),
        ("Cockpit",                    "c69f6c1382018f447bd3ab232bf02176"),
        ("Corridor",                   "f960f0730be516340995562ac0b6e597"),
        ("Crawlspace",                 "4360c7aed7fee3e42b466b34f1cf2270"),
        ("CrewQuarters",               "f7ff5f8c1aed42041b653e9eaa54287b"),
        ("CrewStorage",                "1b66cb083eeef6149b41005abdd173ae"),
        ("Default",                    "f743858ced3468449a6fbceca8d0dc44"),
        ("ECU",                        "2ec54053428070f41b789f5af1760d81"),
        ("Engineering",                "47ed34b58fc05a642aaad2a75f79d2a5"),
        ("EngineRoom",                 "c6e8af5db4e6a2f428c077a1ba360950"),
        ("Habitation",                 "76dc4093ec68f644a89b4c100e58fd55"),
        ("Laboratory",                 "978d6afb37c141345b77309092d24f3a"),
        ("MainCompartment",            "d5772889c0d17d041a06c56a8a28f286"),
        ("Operations",                 "30202ba5349db6246948ee8ebbe281f2"),
        ("PassengerStorage",           "501cc60840df1f745860c9496b9913f3"),
        ("Reactor",                    "57308ca44db6e7444939c1c682b40add"),
        ("SalvageBay",                 "92745bbdc73bbe2468d61f192647841c"),
        ("ThrusterRoom",               "35255e7fddb53ec4b84d410aa0947566"),
        ("ThrusterRoom (unpressurised)","c3916206ca44e364eae1bad0e4fa602c"),
        ("Workshop",                   "f1d1b2120f26b4e4ba3386ff70936917"),
    };

    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Airlock")]
    static void SetAirlock(MenuCommand cmd)              => Apply(cmd, 0);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Bathroom")]
    static void SetBathroom(MenuCommand cmd)             => Apply(cmd, 1);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Bulkhead")]
    static void SetBulkhead(MenuCommand cmd)             => Apply(cmd, 2);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/BulkheadStructure")]
    static void SetBulkheadStructure(MenuCommand cmd)    => Apply(cmd, 3);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Cabin")]
    static void SetCabin(MenuCommand cmd)                => Apply(cmd, 4);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/CargoBay")]
    static void SetCargoBay(MenuCommand cmd)             => Apply(cmd, 5);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Cockpit")]
    static void SetCockpit(MenuCommand cmd)              => Apply(cmd, 6);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Corridor")]
    static void SetCorridor(MenuCommand cmd)             => Apply(cmd, 7);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Crawlspace")]
    static void SetCrawlspace(MenuCommand cmd)           => Apply(cmd, 8);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/CrewQuarters")]
    static void SetCrewQuarters(MenuCommand cmd)         => Apply(cmd, 9);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/CrewStorage")]
    static void SetCrewStorage(MenuCommand cmd)          => Apply(cmd, 10);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Default")]
    static void SetDefault(MenuCommand cmd)              => Apply(cmd, 11);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/ECU")]
    static void SetECU(MenuCommand cmd)                  => Apply(cmd, 12);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Engineering")]
    static void SetEngineering(MenuCommand cmd)          => Apply(cmd, 13);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/EngineRoom")]
    static void SetEngineRoom(MenuCommand cmd)           => Apply(cmd, 14);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Habitation")]
    static void SetHabitation(MenuCommand cmd)           => Apply(cmd, 15);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Laboratory")]
    static void SetLaboratory(MenuCommand cmd)           => Apply(cmd, 16);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/MainCompartment")]
    static void SetMainCompartment(MenuCommand cmd)      => Apply(cmd, 17);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Operations")]
    static void SetOperations(MenuCommand cmd)           => Apply(cmd, 18);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/PassengerStorage")]
    static void SetPassengerStorage(MenuCommand cmd)     => Apply(cmd, 19);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Reactor")]
    static void SetReactor(MenuCommand cmd)              => Apply(cmd, 20);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/SalvageBay")]
    static void SetSalvageBay(MenuCommand cmd)           => Apply(cmd, 21);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/ThrusterRoom")]
    static void SetThrusterRoom(MenuCommand cmd)         => Apply(cmd, 22);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/ThrusterRoom (unpressurised)")]
    static void SetThrusterRoomUnpressurised(MenuCommand cmd) => Apply(cmd, 23);
    [MenuItem("CONTEXT/AddressableSOLoader/Set Room Type/Workshop")]
    static void SetWorkshop(MenuCommand cmd)             => Apply(cmd, 24);

    static void Apply(MenuCommand cmd, int index)
    {
        var loader = (AddressableSOLoader)cmd.context;
        Undo.RecordObject(loader, $"Set Room Type {kRoomTypes[index].label}");

        int refIndex = -1;
        for (int i = 0; i < loader.field.Count; i++)
            if (loader.field[i] == "m_DynamicRoomContainerAsset") { refIndex = i; break; }

        if (refIndex < 0)
        {
            Debug.LogWarning("[SetRoomType] No m_DynamicRoomContainerAsset field entry on this AddressableSOLoader.");
            return;
        }

        while (loader.refs.Count <= refIndex) loader.refs.Add("");
        loader.refs[refIndex] = kRoomTypes[index].guid;
        EditorUtility.SetDirty(loader);
    }
}
