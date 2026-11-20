using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Gives every loader-filled StructurePart in a custom-ship prefab a placeholder StructurePartAsset.
///
/// Why: the game builds each job-board ship's summary before ModdedShipLoader has filled in the parts'
/// StructurePartAssets, and logs an error (sent to its crash reporter) for every part that is still empty.
/// A placeholder in the prefab means no part is ever empty; the loader overwrites it with the real asset.
/// See Docs/USER_GUIDE.md section 11.
///
/// Only StructureParts that a loader entry in the SAME prefab file fills (field m_StructurePartAsset with a
/// non-empty address) are touched; an empty part nothing would ever fill is left alone. The prefab file is
/// patched as text, so nothing else in it changes.
///
/// Runs by itself whenever a tool or a scene save writes a prefab under Assets/_CustomShips (wizards, bakers,
/// the ship prefab itself), so no tool has to call it. Large import batches (a fresh checkout, a Library
/// rebuild) are skipped so opening the project never rewrites prefabs; use the menu item for a full sweep.
/// </summary>
public class SpaPlaceholder : AssetPostprocessor
{
    const string Root = "Assets/_CustomShips/";
    const string AssetName = "SPA_Placeholder";
    const string CommonAsset = Root + "_Common/" + AssetName + ".asset";
    const string AutoKey = "Shipbuilder.SpaPlaceholder.Auto";
    const string MenuAuto = "Shipbuilder/Actions/Part Placeholders/Apply Automatically";
    const string MenuSweep = "Shipbuilder/Actions/Part Placeholders/Apply To All Ship Prefabs";
    const int MaxAutoBatch = 25;

    const string Empty = "m_StructurePartAsset: {fileID: 0}";
    static readonly Regex Doc = new Regex(@"^--- !u!(\d+) &(-?\d+)", RegexOptions.Multiline);
    static readonly Regex Entry = new Regex(@"- component: \{fileID: (-?\d+)\}\r?\n\s+field: m_StructurePartAsset\r?\n\s+address: (\S[^\r\n]*)");
    // Old-style AddressableSOLoader on the part's own GameObject (onChild empty): comp / field / refs are parallel lists.
    static readonly Regex SoLoader = new Regex(@"onChild: \[\]\r?\n\s+comp:\r?\n((?:\s+- .*\r?\n)+)\s+field:\r?\n((?:\s+- .*\r?\n)+)\s+refs:\r?\n((?:\s+- .*\r?\n)+)");
    static readonly Regex GoRef = new Regex(@"m_GameObject: \{fileID: (-?\d+)\}");

    // A StructurePartAsset with default data; the script reference is the game DLL's StructurePartAsset.
    const string AssetTemplate =
        "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &11400000\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n" +
        "  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n" +
        "  m_GameObject: {fileID: 0}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n" +
        "  m_Script: {fileID: -225375714, guid: ce18f73e52ea6c74eaa70e7784553b2c, type: 3}\n  m_Name: " + AssetName + "\n" +
        "  m_EditorClassIdentifier: \n  Data:\n    m_DisplaysInfo: 1\n    m_ApplySurfaceDamage: 1\n" +
        "    m_IRigidbodyAsset: {fileID: 11400000, guid: f237edde892f434585755b7baef867de, type: 2}\n";
    const string MetaTemplate =
        "fileFormatVersion: 2\nguid: {0}\nNativeFormatImporter:\n  externalObjects: {{}}\n  mainObjectFileID: 11400000\n" +
        "  userData: \n  assetBundleName: \n  assetBundleVariant: \n";

    static readonly List<string> pending = new List<string>();

    static bool Auto
    {
        get => EditorPrefs.GetBool(AutoKey, true);
        set => EditorPrefs.SetBool(AutoKey, value);
    }

    [MenuItem(MenuAuto, priority = 40)]
    static void ToggleAuto() => Auto = !Auto;

    [MenuItem(MenuAuto, true)]
    static bool ToggleAutoValidate()
    {
        Menu.SetChecked(MenuAuto, Auto);
        return true;
    }

    [MenuItem(MenuSweep, priority = 41)]
    static void Sweep()
    {
        int parts = 0, files = 0;
        var paths = AssetDatabase.FindAssets("t:Prefab", new[] { Root.TrimEnd('/') }).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();
        try
        {
            AssetDatabase.StartAssetEditing();
            foreach (var p in paths)
            {
                int n = ApplyToFile(p, false);
                if (n > 0) { parts += n; files++; }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }
        EditorUtility.DisplayDialog("Part Placeholders",
            parts == 0 ? $"Nothing to do: no loader-filled part is empty in the {paths.Count} prefabs under {Root}."
                       : $"{parts} part(s) given the placeholder in {files} prefab file(s).", "OK");
    }

    static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
    {
        if (!Auto) return;
        var prefabs = imported.Where(p => p.StartsWith(Root) && p.EndsWith(".prefab")).ToList();
        if (prefabs.Count == 0 || prefabs.Count > MaxAutoBatch) return;
        bool first = pending.Count == 0;
        foreach (var p in prefabs)
            if (!pending.Contains(p)) pending.Add(p);
        // update, not delayCall: delayCall does not run while the editor window is in the background
        if (first) EditorApplication.update += ProcessPending;
    }

    static void ProcessPending()
    {
        EditorApplication.update -= ProcessPending;
        var todo = pending.ToList();
        pending.Clear();
        foreach (var p in todo)
        {
            try { ApplyToFile(p, true); }
            catch (IOException) { /* file busy or gone: the next save of it comes through here again */ }
        }
    }

    /// <summary>Patches one prefab file. Returns how many parts were given the placeholder.</summary>
    public static int ApplyToFile(string assetPath, bool reimport)
    {
        var full = Path.GetFullPath(assetPath);
        if (!File.Exists(full)) return 0;
        var text = File.ReadAllText(full);
        if (!text.Contains(Empty)) return 0;
        // First pass with a dummy GUID: only make (or look for) the placeholder asset when a part really needs it.
        if (Fill(text, new string('0', 32), out _) == 0) return 0;
        var guid = PlaceholderGuid(assetPath);
        int n = Fill(text, guid, out var patched);
        File.WriteAllText(full, patched, new UTF8Encoding(false));
        if (reimport) AssetDatabase.ImportAsset(assetPath);
        return n;
    }

    static int Fill(string text, string guid, out string patched)
    {
        string filled = "m_StructurePartAsset: {fileID: 11400000, guid: " + guid + ", type: 2}";
        var covered = new HashSet<string>(Entry.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value));
        var marks = Doc.Matches(text).Cast<Match>().Select(m => (start: m.Index, fid: m.Groups[2].Value)).ToList();

        // GameObjects whose own StructurePart an old-style loader fills
        var soGos = new HashSet<string>();
        for (int i = 0; i < marks.Count; i++)
        {
            int end = i + 1 < marks.Count ? marks[i + 1].start : text.Length;
            var doc = text.Substring(marks[i].start, end - marks[i].start);
            var m = SoLoader.Match(doc);
            if (!m.Success) continue;
            var lists = Enumerable.Range(1, 3).Select(g => m.Groups[g].Value.Trim().Split('\n').Select(Item).ToList()).ToList();
            int count = lists.Min(l => l.Count);
            for (int k = 0; k < count; k++)
                if (lists[0][k].EndsWith("StructurePart") && lists[1][k] == "m_StructurePartAsset" && lists[2][k].Length > 0)
                {
                    var g = GoRef.Match(doc);
                    if (g.Success) soGos.Add(g.Groups[1].Value);
                }
        }

        var sb = new StringBuilder(text.Length + 64);
        int last = 0, n = 0;
        for (int i = 0; i < marks.Count; i++)
        {
            int start = marks[i].start, end = i + 1 < marks.Count ? marks[i + 1].start : text.Length;
            if (text.IndexOf(Empty, start, end - start, System.StringComparison.Ordinal) < 0) continue;
            var doc = text.Substring(start, end - start);
            var go = GoRef.Match(doc);
            if (!covered.Contains(marks[i].fid) && !(go.Success && soGos.Contains(go.Groups[1].Value))) continue;
            sb.Append(text, last, start - last).Append(doc.Replace(Empty, filled));
            last = end;
            n++;
        }
        sb.Append(text, last, text.Length - last);
        patched = sb.ToString();
        return n;
    }

    // "    - value\r" -> "value"
    static string Item(string line)
    {
        line = line.Trim();
        return line.Length >= 2 ? line.Substring(2).Trim() : "";
    }

    /// <summary>GUID of the placeholder asset for a prefab: the one inside the prefab's own ship folder if it has
    /// one, otherwise the shared one in _Common, which is created on first use.</summary>
    static string PlaceholderGuid(string prefabPath)
    {
        var rest = prefabPath.Substring(Root.Length);
        int slash = rest.IndexOf('/');
        if (slash > 0)
        {
            var top = Root + rest.Substring(0, slash);
            foreach (var g in AssetDatabase.FindAssets(AssetName, new[] { top }))
                if (Path.GetFileName(AssetDatabase.GUIDToAssetPath(g)) == AssetName + ".asset") return g;
        }
        var commonFull = Path.GetFullPath(CommonAsset);
        if (File.Exists(commonFull) && File.Exists(commonFull + ".meta"))
        {
            var m = Regex.Match(File.ReadAllText(commonFull + ".meta"), @"^guid: ([0-9a-f]{32})", RegexOptions.Multiline);
            if (m.Success) return m.Groups[1].Value;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(commonFull));
        var guid = GUID.Generate().ToString();
        File.WriteAllText(commonFull, AssetTemplate, new UTF8Encoding(false));
        File.WriteAllText(commonFull + ".meta", string.Format(MetaTemplate, guid), new UTF8Encoding(false));
        AssetDatabase.ImportAsset(CommonAsset);
        return guid;
    }
}
