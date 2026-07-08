using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class FixBrokenGuidReferences
{
    // Matches PPtr references with a nonzero fileID but an all-zero guid -
    // corrupted orphaned pointers that trigger Unity's "Could not extract GUID
    // in text file ... at line N" scanner error. A proper null reference is
    // {fileID: 0} alone, so we collapse the broken form down to that.
    static readonly Regex BrokenRefPattern = new Regex(
        @"\{fileID: -?\d+, guid: 00000000000000000000000000000000, type: 0\}",
        RegexOptions.Compiled);

    const string Replacement = "{fileID: 0}";

    [MenuItem("Shipbuilder/Actions/Fix Broken GUID References In Scene", priority = 81)]
    static void FixCurrentScene()
    {
        var scene = EditorSceneManager.GetActiveScene();
        if (string.IsNullOrEmpty(scene.path))
        {
            EditorUtility.DisplayDialog("Fix Broken GUID References", "The active scene has no saved path. Save the scene first.", "OK");
            return;
        }

        if (scene.isDirty)
        {
            bool proceed = EditorUtility.DisplayDialog(
                "Fix Broken GUID References",
                "The active scene has unsaved changes. Save before running this fix, or the fix will be overwritten next save.",
                "Save and Continue",
                "Cancel");
            if (!proceed)
                return;
            EditorSceneManager.SaveScene(scene);
        }

        string fullPath = Path.GetFullPath(scene.path);
        string content = File.ReadAllText(fullPath);

        var matches = BrokenRefPattern.Matches(content);
        if (matches.Count == 0)
        {
            EditorUtility.DisplayDialog("Fix Broken GUID References", "No broken references found in this scene.", "OK");
            return;
        }

        string preview = BuildPreview(content, matches, previewCount: 5);

        bool confirmed = EditorUtility.DisplayDialog(
            "Fix Broken GUID References",
            $"Found {matches.Count} broken reference(s) in {scene.path}.\n\n" +
            $"Preview (first {System.Math.Min(5, matches.Count)}):\n{preview}\n" +
            "Each will be replaced with a clean null reference ({fileID: 0}).\n" +
            "A backup will be saved alongside the scene file first.\n\n" +
            "The scene must be reloaded afterward for the fix to take effect.",
            "Confirm",
            "Cancel");
        if (!confirmed)
            return;

        string backupPath = fullPath + ".guidfix.bak";
        File.Copy(fullPath, backupPath, true);

        string fixedContent = BrokenRefPattern.Replace(content, Replacement);
        File.WriteAllText(fullPath, fixedContent);

        Debug.Log($"[FixBrokenGuidReferences] Fixed {matches.Count} broken reference(s) in {scene.path}. Backup saved to {backupPath}.");

        bool reload = EditorUtility.DisplayDialog(
            "Fix Broken GUID References",
            $"Fixed {matches.Count} reference(s). Reload the scene now to apply the change?",
            "Reload Now",
            "Later");
        if (reload)
        {
            EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
        }
    }

    static string BuildPreview(string content, MatchCollection matches, int previewCount)
    {
        var sb = new StringBuilder();
        foreach (Match match in matches.Cast<Match>().Take(previewCount))
        {
            int line = content.Take(match.Index).Count(c => c == '\n') + 1;
            sb.AppendLine($"  Line {line}: {match.Value}");
        }
        return sb.ToString();
    }
}
