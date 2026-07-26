using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Build;
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class BuildContent
{
    public const int MANIFEST_VERSION = 1;

    [MenuItem("Shipbuilder/⛭ Build", priority = 1)]
    public static bool RunBuild()
    {
        // Don't go through with lengthy build process if build settings are not in order
        if (!Settings.VerifyBuildSettings())
        {
            return false;
        }

        var validation = ShipValidator.Validate();
        if (validation.HasErrors)
        {
            BuildValidationWindow.Show(validation.Issues);
            return false;
        }
        if (validation.Warnings.Count > 0)
        {
            var msg = string.Join("\n\n", validation.Warnings);

            // Check if any warnings are scale violations — offer auto-fix if so
            bool hasScaleWarnings = validation.Warnings.Any(w => w.Contains("non-unit scale"));
            int choice;
            if (hasScaleWarnings)
            {
                choice = EditorUtility.DisplayDialogComplex(
                    "Build Warnings",
                    $"Validation produced {validation.Warnings.Count} warning(s):\n\n{msg}\n\n" +
                    "Auto-Fix will run Lock In Rescale on all violating objects before building.",
                    "Proceed Anyway",
                    "Cancel",
                    "Auto-Fix & Build"
                );
            }
            else
            {
                choice = EditorUtility.DisplayDialogComplex(
                    "Build Warnings",
                    $"Validation produced {validation.Warnings.Count} warning(s):\n\n{msg}\n\nProceed with build anyway?",
                    "Proceed",
                    "Cancel",
                    ""
                );
            }

            // 0 = left (Proceed), 1 = Cancel, 2 = right (Auto-Fix)
            if (choice == 1)
            {
                Debug.Log("Build cancelled by user after warnings.");
                return false;
            }
            if (choice == 2)
            {
                var rescaled = ShipValidator.FindScaleViolations();
                Debug.Log($"[Build] Locking in rescale on {rescaled.Count} object(s) before build.");
                RescaleLockerContextMenu.LockAllSilent(rescaled);
                AssetDatabase.SaveAssets();
                UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
                // Defer the build until after the save has settled.
                EditorApplication.delayCall += ContinueBuildAfterAutoFix;
                return true;
            }
        }

        foreach(var typeAsset in Resources.FindObjectsOfTypeAll<BBI.Unity.Game.TypeAsset>())
        {
            if(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(typeAsset, out string guid, out long id))
            {
                typeAsset.SetAssetGUIDInEditMode(guid);
                EditorUtility.SetDirty(typeAsset);
            }
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        AddressableAssetSettingsDefaultObject.Settings.activeProfileId = AddressableAssetSettingsDefaultObject.Settings.profileSettings.GetProfileId("Default");

        // Clear stale bundles so the CRC in the catalog always matches the deployed file
        var builtShipContentPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BuiltShipContent"));
        if (Directory.Exists(builtShipContentPath))
        {
            Directory.Delete(builtShipContentPath, true);
        }

        Debug.Log("Starting to build player content...");
        AddressableAssetSettings.BuildPlayerContent(out AddressablesPlayerBuildResult result);
        bool success = string.IsNullOrEmpty(result.Error);

        if (success)
        {
            Debug.Log("Player content is built.");
            Debug.Log("Starting to move ship bundles...");
            
            var manifest = GenerateManifest();

            // Move root-level bundles (Unity Addressables flat output: each group builds to root as {name}_assets_all_{hash}.bundle)
            var mainCatalogPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "com.unity.addressables", "aa", "Windows", "catalog.json"));
            var rootBundles = Directory.GetFiles(builtShipContentPath, "*_assets_all_*.bundle", SearchOption.TopDirectoryOnly);

            // Determine which ship names this build will produce, then delete stale deploy folders for this author
            var modPath = Path.Combine("BepInEx", "plugins", "ModdedShipLoader", "Ships");
            var shipsBaseDir = Path.Combine(Settings.buildSettings.ShipbreakerPath, modPath);
            var builtShipNames = new HashSet<string>(rootBundles.Select(b => {
                var name = Path.GetFileNameWithoutExtension(b).Split(new string[] { "_assets_all_" }, StringSplitOptions.None)[0];
                return (char.ToUpper(name[0]) + name.Substring(1)) + "." + Settings.buildSettings.Author;
            }), StringComparer.OrdinalIgnoreCase);
            foreach (var staleDir in Directory.GetDirectories(shipsBaseDir, "*." + Settings.buildSettings.Author))
            {
                if (!builtShipNames.Contains(Path.GetFileName(staleDir)))
                {
                    Debug.Log($"Removing stale ship folder: {staleDir}");
                    Directory.Delete(staleDir, true);
                }
            }
            if (rootBundles.Length == 0)
            {
                Debug.LogWarning("No ship bundles found in " + builtShipContentPath);
            }
            foreach (var rootBundle in rootBundles)
            {
                var bundleFileName = Path.GetFileNameWithoutExtension(rootBundle);
                var shipNameRaw = bundleFileName.Split(new string[] { "_assets_all_" }, StringSplitOptions.None)[0];
                var shipName = char.ToUpper(shipNameRaw[0]) + shipNameRaw.Substring(1);
                Debug.Log($"Processing root bundle for ship: {shipName} ({rootBundle})");
                MoveShipBundle(shipName, mainCatalogPath, rootBundle, manifest);
                SplitBundleForRepo(shipName);
            }

            // Move each custom bundle
            foreach(var shipDirectory in Directory.GetDirectories(builtShipContentPath))
            {
                Debug.Log("Current shipDirectory is: " + shipDirectory);
                var shipName = Path.GetFileName(shipDirectory);
                var bundlePath = Path.Combine(shipDirectory, shipName + "_assets_all.bundle");
                Debug.Log("Current bundlePath is: " + bundlePath);

                MoveShipBundle(shipName, Path.Combine(shipDirectory, shipName + ".json"), bundlePath, manifest);
                SplitBundleForRepo(shipName);
            }
            Debug.Log("Moving ship bundles completed");

            // Domain reload reinitializes Addressables from scratch, which is necessary
            // after a build — manual ReloadAssets leaves the AssetBundleProvider in a
            // broken state that prevents preview loading until the Editor restarts.
            // [InitializeOnLoad] on LoadGameAssets will call ReloadAssets() automatically.
            EditorApplication.delayCall += () => {
                AddressableRendering.UpdateViewList();
                EditorUtility.RequestScriptReload();
            };
            Debug.Log("Build Complete");
            return true;
        }
        else
        {
            Debug.LogError("Build Failed!");
            Debug.LogError("Addressables build error encountered: " + result.Error);
            Debug.LogError("If you are stuck, contact Piepieonline on the Shipbreaker discord (#modding-discussion)");
            return false;
        }
    }

    private static Manifest GenerateManifest()
    {
        var baseVersionList = new List<string>();
        string[] typesToSearch = File.ReadAllLines(Path.Combine(Settings.buildSettings.ShipbreakerPath, "BepInEx", "patchers", "ModdedShipLoaderPatcher", "TypesToModify.txt"));

        foreach(var typeString in typesToSearch)
        {
            var type = Type.GetType($"BBI.Unity.Game.{typeString}, BBI.Unity.Game, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", true);
            foreach(var assetGUID in AssetDatabase.FindAssets($"t:{typeString}", null))
            {
                var asset = AssetDatabase.LoadAssetAtPath(AssetDatabase.GUIDToAssetPath(assetGUID), type);
                var AssetCloneRef = ((string)type.GetField("AssetCloneRef").GetValue(asset));
                var AssetBasis = ((string)type.GetField("AssetBasis").GetValue(asset));
                if((AssetBasis != null && AssetBasis != "") || (AssetCloneRef != null && AssetCloneRef != ""))
                {
                    baseVersionList.Add($"[{assetGUID}]");
                }
            }
        }

        return new Manifest() { baseOverrides = baseVersionList.ToArray() };
    }

    private static void MoveShipBundle(string shipName, string catalogPath, string bundlePath, Manifest manifest)
    {
        var modPath = Path.Combine("BepInEx", "plugins", "ModdedShipLoader", "Ships");
        var shipPath = $"{shipName}.{Settings.buildSettings.Author}";

        if(!Directory.Exists(Path.Combine(Settings.buildSettings.ShipbreakerPath, modPath, shipPath)))
        {
            Debug.Log($"{shipName} - Creating build directory");
            Directory.CreateDirectory(Path.Combine(Settings.buildSettings.ShipbreakerPath, modPath, shipPath));
        }

        string targetPath = Path.Combine(Settings.buildSettings.ShipbreakerPath, modPath, shipPath, shipName + "_assets_all.bundle");
        string pathToGameFolder = Settings.buildSettings.ShipbreakerPath;

        if(SystemInfo.operatingSystem.ToLower().Contains("linux"))
        {
            targetPath = Path.Combine(Settings.buildSettings.ShipbreakerPath, modPath, shipPath, shipName + "_assets_all.bundle");
            pathToGameFolder = Settings.buildSettings.WindowsShipbreakerPathOnLinux;
        }
        
        Debug.Log($"{shipName} - Moving bundle from {bundlePath} to {targetPath}"); 

        File.Copy(
            bundlePath,
            targetPath,
            true
        );
        
        Debug.Log($"{shipName} - Moving and modifying catalog");
        var catalog = JObject.Parse(File.ReadAllText(catalogPath));
        var internalIds = (JArray)catalog.SelectToken("$.m_InternalIds");

        for(int i = 0; i < internalIds.Count(); i++)
        {
            if(internalIds[i].ToString().Contains("common_assets_all.bundle"))
            {
                // make sure all paths inside the bundles are Windows format, as the Windows-based game will use them (replacing potential Linux path separators / with Windows \\)
                internalIds[i].Replace(Path.Combine(pathToGameFolder, modPath, $"Common.{Settings.buildSettings.Author}", "common_assets_all.bundle").Replace("/","\\")); 
            }
            else if(internalIds[i].ToString().Contains(".bundle"))
            {
                internalIds[i].Replace(Path.Combine(pathToGameFolder, modPath, shipPath, shipName + "_assets_all.bundle").Replace("/","\\")); 
            }
        }

        File.WriteAllText(Path.Combine(Settings.buildSettings.ShipbreakerPath, modPath, shipPath, "catalog.json"), catalog.ToString());

        // File.Delete(catalogPath);

        Debug.Log($"{shipName} - Writing manifest");
        File.WriteAllText(Path.Combine(Settings.buildSettings.ShipbreakerPath, modPath, shipPath, "manifest.json"), JsonConvert.SerializeObject(manifest));
    }

    // GitHub blocks files over 100MB, so bundles are committed as a 7z archive under BundleParts/
    // instead of the raw .bundle (which is gitignored). The archive includes catalog.json and
    // manifest.json alongside the bundle so the ship folder can be reconstructed by extracting
    // with 7-Zip (or any tool that understands .7z / split volumes, e.g. WinZip, PeaZip).
    const long SPLIT_THRESHOLD_BYTES = 99L * 1024 * 1024;
    const long VOLUME_SIZE_BYTES = 99L * 1024 * 1024;

    private static readonly string[] SevenZipCandidates =
    {
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe",
    };

    private static void SplitBundleForRepo(string shipName)
    {
        var modPath = Path.Combine("BepInEx", "plugins", "ModdedShipLoader", "Ships");
        var shipFolder = Path.Combine(Settings.buildSettings.ShipbreakerPath, modPath, $"{shipName}.{Settings.buildSettings.Author}");
        var bundlePath = Path.Combine(shipFolder, shipName + "_assets_all.bundle");
        var catalogPath = Path.Combine(shipFolder, "catalog.json");
        var manifestPath = Path.Combine(shipFolder, "manifest.json");

        if (!File.Exists(bundlePath))
        {
            Debug.LogError($"{shipName} - Expected deployed bundle at {bundlePath}, skipping repo archive.");
            return;
        }

        var sevenZip = FindSevenZip();
        if (sevenZip == null)
        {
            Debug.LogError($"{shipName} - Could not find 7z.exe (checked PATH and standard install locations). Skipping repo archive.");
            return;
        }

        var partsDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BundleParts", shipName));
        if (Directory.Exists(partsDir))
        {
            Directory.Delete(partsDir, true);
        }
        Directory.CreateDirectory(partsDir);

        var bundleSize = new FileInfo(bundlePath).Length;

        if (bundleSize <= SPLIT_THRESHOLD_BYTES)
        {
            // Under threshold: no archive needed, just copy the loose files.
            File.Copy(bundlePath, Path.Combine(partsDir, Path.GetFileName(bundlePath)), true);
            File.Copy(catalogPath, Path.Combine(partsDir, Path.GetFileName(catalogPath)), true);
            File.Copy(manifestPath, Path.Combine(partsDir, Path.GetFileName(manifestPath)), true);
            Debug.Log($"{shipName} - Bundle is {bundleSize / (1024 * 1024)} MB, copied loose (no archive needed) to {partsDir}");
            return;
        }

        // Over threshold: split into 7z volumes (7z -v produces {archive}.7z.001, .002, ...)
        // -mx=0: store only, no compression — bundle contents are already compressed and
        // large ships otherwise take a long time to archive for little size benefit.
        var archivePath = Path.Combine(partsDir, $"{shipName}.7z");
        var inputFiles = $"\"{bundlePath}\" \"{catalogPath}\" \"{manifestPath}\"";
        var args = $"a -mx=0 -v{VOLUME_SIZE_BYTES}b \"{archivePath}\" {inputFiles}";

        if (!RunSevenZip(sevenZip, args, out string error))
        {
            Debug.LogError($"{shipName} - 7z archive creation failed: {error}");
            return;
        }

        var volumeCount = Directory.GetFiles(partsDir, $"{shipName}.7z.*").Length;
        Debug.Log($"{shipName} - Bundle was {bundleSize / (1024 * 1024)} MB, split into {volumeCount} volume(s) under {partsDir}");
    }

    private static string FindSevenZip()
    {
        foreach (var candidate in SevenZipCandidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Fall back to PATH
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("where", "7z.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    return output.Split('\n')[0].Trim();
            }
        }
        catch { }

        return null;
    }

    private static bool RunSevenZip(string sevenZipPath, string args, out string error)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(sevenZipPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var proc = System.Diagnostics.Process.Start(psi))
        {
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                error = string.IsNullOrEmpty(stderr) ? stdout : stderr;
                return false;
            }

            error = null;
            return true;
        }
    }

    static void ContinueBuildAfterAutoFix()
    {
        // Re-entry after auto-fix: assets are saved and the import has settled.
        // RunBuild will re-validate; if scale warnings are gone it proceeds to build.
        RunBuild();
    }

    [MenuItem("Shipbuilder/▶ Build and run", priority = 2)]
    static void BuildAndRun()
    {
        if(RunBuild())
            System.Diagnostics.Process.Start(Path.Combine(Settings.buildSettings.ShipbreakerPath, "Shipbreaker.exe"));
    }

    [MenuItem("Shipbuilder/Actions/Update game catalog", priority = 70)]
    static void UpdateGameCatalog()
    {
        var catalog = File.ReadAllText(Path.Combine(Settings.buildSettings.ShipbreakerPath, "Shipbreaker_Data", "StreamingAssets", "aa", "catalog.json"));

        string sep = "\\\\";
        if(SystemInfo.operatingSystem.ToLower().Contains("linux"))
        {
            sep = "/";
        }

        // make sure all paths inside the modded catalog are platform correct, as the Unity Editor will use them which may be running on Linux
        catalog = catalog.Replace(@"{UnityEngine.AddressableAssets.Addressables.RuntimePath}\\StandaloneWindows64\\", (Path.Combine(Settings.buildSettings.ShipbreakerPath, "Shipbreaker_Data", "StreamingAssets", "aa", "StandaloneWindows64") + "\\").Replace("\\", sep));

        var path = System.IO.Path.GetFullPath(Path.Combine(Application.dataPath, "..", "modded_catalog.json"));

        // Give the game catalog a unique locator ID so it doesn't collide with the custom
        // asset catalog (both default to "AddressablesMainContentCatalog"); whichever loads
        // second would otherwise silently replace the first, making one set of assets unloadable.
        catalog = catalog.Replace("\"m_LocatorId\":\"AddressablesMainContentCatalog\"",
                                  "\"m_LocatorId\":\"GameContentCatalog\"");

        File.WriteAllText(path, catalog);
        Debug.Log($"Game catalog recreated and written to {path}");
    }

    [MenuItem("Shipbuilder/Actions/Update known assets", priority = 71)]
    static void UpdateKnownAssets()
    {
        List<string> output = new List<string>() { "{" };
        var knownLocations = new Dictionary<string, bool>();
        foreach (var loc in UnityEngine.AddressableAssets.Addressables.ResourceLocators)
        {
            foreach (var key in loc.Keys)
            {
                if (!loc.Locate(key, typeof(object), out var resourceLocations))
                    continue;

                if (key.ToString().Length == 32)
                {
                    output.Add($"\"{key}\": \"{resourceLocations.First().InternalId}\",");
                }
            }
        }

        output[output.Count - 1] = output[output.Count - 1].TrimEnd(',');
        output.Add("}");

        var path = System.IO.Path.GetFullPath(Path.Combine(Application.dataPath, "..", "known_assets.json"));

        // regardless of platform, make sure the Known Assets file is saved with Windows line terminators, as the GameInspectorWindow regex search depends on them
        using (var writer = new StreamWriter(path)) 
        {
            writer.NewLine = "\r\n";
            foreach (var line in output) 
            {
                writer.WriteLine(line);
            }
        }
        //File.WriteAllLines(path, output);
        Debug.Log($"Known asset list recreated and written to {path}");
    }

    [MenuItem("Shipbuilder/Actions/Reload Build Settings", priority = 72)]
    static void ReloadBuildSettings()
    {
        Settings.ReloadBuildSettings();
    }

    public class Manifest
    {
        public readonly int version = MANIFEST_VERSION;
        public string[] baseOverrides;
    }
}
