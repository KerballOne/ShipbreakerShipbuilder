using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

public class DumpScriptGuids
{
    [MenuItem("Shipbuilder/Actions/Dump Script GUIDs", priority = 80)]
    public static void Dump()
    {
        var sb = new StringBuilder();
        var scripts = MonoImporter.GetAllRuntimeMonoScripts();

        foreach (var script in scripts.OrderBy(s => s.GetClass()?.FullName ?? s.name))
        {
            var cls = script.GetClass();
            var path = AssetDatabase.GetAssetPath(script);
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out string guid, out long fileId))
                continue;

            sb.AppendLine($"{(cls != null ? cls.FullName : script.name)}\t{guid}\t{fileId}\t{path}");
        }

        var outPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "script_guid_dump.txt"));
        File.WriteAllText(outPath, sb.ToString());
        EditorUtility.RevealInFinder(outPath);
    }
}
