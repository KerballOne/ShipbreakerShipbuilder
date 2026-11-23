#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// File-based command bridge so an external agent can drive the editor.
/// Drop a JSON file into .claude/bridge/in/ ; the result appears in .claude/bridge/out/ under the same name.
/// Commands: ping, state, refresh, menu, hierarchy, capture, catalog.
/// </summary>
[InitializeOnLoad]
public static class ClaudeBridge
{
    static readonly string Root    = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".claude", "bridge"));
    static readonly string InDir   = Path.Combine(Root, "in");
    static readonly string OutDir  = Path.Combine(Root, "out");
    static readonly string ShotDir = Path.Combine(Root, "shots");

    static bool   s_Busy;
    static double s_NextPoll;

    static ClaudeBridge()
    {
        Directory.CreateDirectory(InDir);
        Directory.CreateDirectory(OutDir);
        Directory.CreateDirectory(ShotDir);
        EditorApplication.update += Poll;
        Application.logMessageReceived += OnLog;
    }

    // ── console monitoring ───────────────────────────────────────────────────
    // Every error/exception/assert the editor logs is appended to .claude/bridge/console_errors.log and
    // attached to the result of whichever command was running (or the next one) as "consoleErrors".
    static readonly List<string> s_Errors = new List<string>();
    static readonly string ErrorLog = Path.Combine(Root, "console_errors.log");

    static void OnLog(string condition, string stack, LogType type)
    {
        if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
        var first = (stack ?? "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var flat = condition.Replace("\n", " ");
        var line = $"[{type}] {flat} @ {first.Trim()}";
        if (line.Length > 400) line = line.Substring(0, 400);
        lock (s_Errors) s_Errors.Add(line);
        try { File.AppendAllText(ErrorLog, DateTime.Now.ToString("HH:mm:ss ") + line + "\n"); } catch { }
    }

    static JArray DrainErrors()
    {
        lock (s_Errors)
        {
            var arr = new JArray(s_Errors.GroupBy(e => e).Select(g => g.Count() > 1 ? $"x{g.Count()} {g.Key}" : g.Key));
            s_Errors.Clear();
            return arr;
        }
    }

    static void Poll()
    {
        if (s_Busy)
        {
            // Escape hatch: a file named "reset" un-sticks a hung command and recompiles.
            var reset = Path.Combine(InDir, "reset");
            if (!File.Exists(reset)) return;
            File.Delete(reset);
            s_Busy = false;
            AssetDatabase.Refresh();
            return;
        }
        if (EditorApplication.timeSinceStartup < s_NextPoll) return;
        s_NextPoll = EditorApplication.timeSinceStartup + 0.25;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

        string file;
        try { file = Directory.GetFiles(InDir, "*.json").OrderBy(f => f).FirstOrDefault(); }
        catch { return; }
        if (file == null) return;

        string text;
        try { text = File.ReadAllText(file); File.Delete(file); }
        catch { return; } // still being written; retry next poll

        s_Busy = true;
        Run(Path.GetFileName(file), text);
    }

    static void Run(string name, string text)
    {
        var res = new JObject();
        try
        {
            var cmd = JObject.Parse(text);
            var op  = (string)cmd["op"];
            res["op"] = op;
            JToken result;
            switch (op)
            {
                case "ping":      result = "pong"; break;
                case "state":     result = State(); break;
                case "menu":      result = EditorApplication.ExecuteMenuItem((string)cmd["path"]); break;
                case "hierarchy": result = Hierarchy(cmd); break;
                case "capture":   result = Capture(cmd); break;
                case "catalog":   result = Catalog(cmd); break;
                case "group":     result = Group(cmd); break;
                case "place":     result = Place(cmd); break;
                case "move":      result = Move(cmd); break;
                case "delete":    result = Delete(cmd); break;
                case "manifest":  result = Manifest(); break;
                case "wall":      result = Wall(cmd); break;
                case "clone":     result = Clone(cmd); break;
                case "rescale":   result = Rescale(cmd); break;
                case "room":      result = Room(cmd); break;
                case "rooms":     result = Rooms(cmd); break;
                case "partcount": result = PartCount(cmd); break;
                case "setactive": result = SetActiveOp(cmd); break;
                case "mats": result = Mats(cmd); break;
                case "reparent": result = Reparent(cmd); break;
                case "remesh": result = Remesh(cmd); break;
                case "bigmeshes": result = BigMeshes(cmd); break;
                case "meshweld":  result = MeshWeld(cmd); break;
                case "meshdump":  result = MeshDump(cmd); break;
                case "meshload":  result = MeshLoad(cmd); break;
                case "meshparts": result = MeshParts(cmd); break;
                case "meshassign": result = MeshAssign(cmd); break;
                case "worlddump": result = WorldDump(cmd); break;
                case "jointpads": result = JointPads(cmd); break;
                case "setsoloader": result = SetSoLoader(cmd); break;
                case "colormat": result = ColorMat(cmd); break;
                case "prefabdelete": result = PrefabDelete(cmd); break;
                case "aclpurge": result = AclPurge(cmd); break;
                case "aclrepair": result = AclRepair(cmd); break;
                case "addmjc": result = AddMjc(cmd); break;
                case "mjcgroups": result = MjcGroups(cmd); break;
                case "mjcregroup": result = MjcRegroup(cmd); break;
                case "reorderkids": result = ReorderKids(cmd); break;
                case "bayshow": result = BayShow(cmd); break;
                case "restructure": result = Restructure(cmd); break;
                case "aclcopy": result = AclCopy(cmd); break;
                case "aclregister": result = AclRegister(cmd); break;
                case "stripcomponents": result = StripComponents(cmd); break;
                case "splitpart": result = SplitPart(cmd); break;
                case "roomvolume": result = RoomVolume(cmd); break;
                case "bakeparts": result = BakeParts(cmd); break;
                case "disablechild": result = DisableChild(cmd); break;
                case "bake":      result = Bake(cmd); break;
                case "meshuv":    result = MeshUv(cmd); break;
                case "postervariant": result = PosterVariant(cmd); break;
                case "reloadscene":   // discard unsaved scene changes by reopening the scene from disk
                {
                    var sp = SceneManager.GetActiveScene().path;
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(sp, UnityEditor.SceneManagement.OpenSceneMode.Single);
                    result = "reloaded " + sp;
                    break;
                }
                // { op:"openprefab", asset:"Assets/.../Tachi_Rocinante.prefab" } opens a prefab (e.g. a variant) in prefab mode: every
                // other op then works on it, with its root name as the path root. "saveprefab" writes the stage to its asset,
                // "closeprefab" saves and returns to the main scene.
                case "openprefab":
                {
                    var pa = AssetDatabase.LoadAssetAtPath<GameObject>((string)cmd["asset"]);
                    if (pa == null) throw new Exception("Prefab not found: " + cmd["asset"]);
                    AssetDatabase.OpenAsset(pa);
                    var st = PrefabStageUtility.GetCurrentPrefabStage();
                    result = st != null ? st.assetPath + " root=" + st.prefabContentsRoot.name : "no stage";
                    break;
                }
                case "saveprefab":
                case "closeprefab":
                {
                    var st = PrefabStageUtility.GetCurrentPrefabStage();
                    if (st == null) { result = "no prefab stage"; break; }
                    var ap = st.assetPath;
                    PrefabUtility.SaveAsPrefabAsset(st.prefabContentsRoot, ap, out bool okSave);
                    st.ClearDirtiness();
                    if (op == "closeprefab") UnityEditor.SceneManagement.StageUtility.GoToMainStage();
                    result = (okSave ? "saved " : "SAVE FAILED ") + ap;
                    break;
                }
                case "deletemany": result = DeleteMany(cmd); break;
                case "inspect":   result = Inspect(cmd); break;
                case "sodump":    result = SoDump(cmd); break;
                case "prefabdump": result = PrefabDump(cmd); break;
                case "setoi":     result = SetOi(cmd); break;
                case "meshcast":  result = MeshCast(cmd); break;
                case "signmat":   result = SignMat(cmd); break;
                case "placeprefab": result = PlacePrefab(cmd); break;
                case "hardpoint": result = HardPointOp(cmd); break;
                case "hardpointasset": result = HardPointAssetOp(cmd); break;
                case "addrmove":  result = AddrMove(cmd); break;
                case "console":   result = "drained"; break;   // errors ride along as consoleErrors
                case "validate":  result = Validate(); break;
                case "save":      result = UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes(); break;
                case "refresh":
                    res["ok"] = true; res["result"] = "refreshing";
                    WriteResult(name, res);
                    s_Busy = false;
                    AssetDatabase.Refresh();
                    return;
                default: throw new Exception($"Unknown op '{op}'");
            }
            res["ok"] = true;
            res["result"] = result;
        }
        catch (Exception ex)
        {
            res["ok"] = false;
            res["error"] = ex.ToString();
        }
        WriteResult(name, res);
        s_Busy = false;
    }

    static void WriteResult(string name, JObject res)
    {
        var errs = DrainErrors();
        if (errs.Count > 0) res["consoleErrors"] = errs;
        var tmp = Path.Combine(OutDir, name + ".tmp");
        File.WriteAllText(tmp, res.ToString());
        var dst = Path.Combine(OutDir, name);
        if (File.Exists(dst)) File.Delete(dst);
        File.Move(tmp, dst);
    }

    // ── state ────────────────────────────────────────────────────────────────

    static JToken State()
    {
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        var sv = SceneView.lastActiveSceneView;
        return new JObject
        {
            ["activeScene"]  = SceneManager.GetActiveScene().path,
            ["sceneDirty"]   = SceneManager.GetActiveScene().isDirty,
            ["prefabStage"]  = stage != null ? stage.assetPath : null,
            ["roots"]        = new JArray(ActiveRoots().Select(r => r.name)),
            ["selection"]    = new JArray(Selection.gameObjects.Select(PathOf)),
            ["assetsLoaded"] = LoadGameAssets.CheckHandlesValid(),
            ["sceneView"]    = sv == null ? null : new JObject
            {
                ["pivot"] = V(sv.pivot), ["euler"] = V(sv.rotation.eulerAngles), ["size"] = sv.size,
            },
        };
    }

    static IEnumerable<GameObject> ActiveRoots()
    {
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null) return stage.scene.GetRootGameObjects();
        return SceneManager.GetActiveScene().GetRootGameObjects();
    }

    static string PathOf(GameObject go)
    {
        var p = go.name;
        for (var t = go.transform.parent; t != null; t = t.parent) p = t.name + "/" + p;
        return p;
    }

    static Transform Find(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Split('/');
        foreach (var root in ActiveRoots())
        {
            if (root.name != parts[0]) continue;
            var t = root.transform;
            bool ok = true;
            for (int i = 1; i < parts.Length && ok; i++)
            {
                Transform next = null;
                // "Name#2" = the third child called Name (0-based), for same-named siblings
                string want = parts[i]; int skip = 0; int hash = want.LastIndexOf('#');
                if (hash > 0 && int.TryParse(want.Substring(hash + 1), out int idx)) { want = want.Substring(0, hash); skip = idx; }
                foreach (Transform c in t) if (c.name == want && skip-- == 0) { next = c; break; }
                if (next == null) ok = false; else t = next;
            }
            if (ok) return t;
        }
        return null;
    }

    static JArray V(Vector3 v) => new JArray(Math.Round(v.x, 4), Math.Round(v.y, 4), Math.Round(v.z, 4));
    static Vector3 ToV(JToken t, Vector3 def) =>
        t == null || t.Type == JTokenType.Null ? def : new Vector3((float)t[0], (float)t[1], (float)t[2]);

    static bool TryBounds(Transform t, out Bounds b)
    {
        b = default; bool has = false;
        foreach (var r in t.GetComponentsInChildren<Renderer>(false))
        {
            if (!has) { b = r.bounds; has = true; } else b.Encapsulate(r.bounds);
        }
        return has;
    }

    // ── hierarchy ────────────────────────────────────────────────────────────
    // { op:"hierarchy", path:"Tachi/Deck 8", depth:2, bounds:true, filter:"regex on name" }

    static JToken Hierarchy(JObject cmd)
    {
        int depth    = (int?)cmd["depth"] ?? 1;
        bool bounds  = (bool?)cmd["bounds"] ?? true;
        var filter   = (string)cmd["filter"];
        var rx       = string.IsNullOrEmpty(filter) ? null : new System.Text.RegularExpressions.Regex(filter);
        var path     = (string)cmd["path"];

        var arr = new JArray();
        if (string.IsNullOrEmpty(path))
        {
            foreach (var r in ActiveRoots()) arr.Add(Node(r.transform, depth, bounds, rx));
            return arr;
        }
        var t = Find(path);
        if (t == null) throw new Exception($"Not found: {path}");
        return Node(t, depth, bounds, rx);
    }

    static JObject Node(Transform t, int depth, bool bounds, System.Text.RegularExpressions.Regex rx)
    {
        var o = new JObject
        {
            ["name"] = t.name,
            ["pos"]  = V(t.position),
            ["rot"]  = V(t.rotation.eulerAngles),
            ["n"]    = t.childCount,
        };
        if (!t.gameObject.activeInHierarchy) o["inactive"] = true;
        var loader = t.GetComponent<BBI.Unity.Game.AddressableLoader>();
        if (loader != null)
        {
            o["guid"] = loader.assetGUID;
            if (!string.IsNullOrEmpty(loader.childPath)) o["childPath"] = loader.childPath;
        }
        if (bounds && TryBounds(t, out var b)) { o["bc"] = V(b.center); o["bs"] = V(b.size); }
        if (depth > 0 && t.childCount > 0)
        {
            var kids = new JArray();
            foreach (Transform c in t)
            {
                if (c.GetComponent<FakePrefabDisplay>() != null) continue;
                if (rx != null && !rx.IsMatch(c.name) && c.childCount == 0) continue;
                kids.Add(Node(c, depth - 1, bounds, rx));
            }
            o["kids"] = kids;
        }
        return o;
    }

    // ── capture ──────────────────────────────────────────────────────────────
    // { op:"capture", name:"deck8", pos:[x,y,z], lookAt:[x,y,z] | euler:[x,y,z], fov:60, ortho:0, w:1024, h:768,
    //   near:0.05, far:500 }   — omit pos to use the current scene-view camera pose.

    static JToken Capture(JObject cmd)
    {
        int w = (int?)cmd["w"] ?? 1024, h = (int?)cmd["h"] ?? 768;
        var name = (string)cmd["name"] ?? "shot";
        var sv = SceneView.lastActiveSceneView;

        var go = new GameObject("__ClaudeBridgeCam") { hideFlags = HideFlags.HideAndDontSave };
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        Texture2D tex = null;
        // texOverrides:[{mat:"Assets/..mat", prop:"_BaseColorMap", file:"C:/..png", linear:false}] — preview-only:
        // textures are swapped on the in-memory material for this one render and restored afterwards.
        var texRestore = new List<(Material m, string p, Texture t)>();
        var texTemp = new List<Texture2D>();
        var hiddenRoots = new List<GameObject>();
        try
        {
            if (cmd["texOverrides"] is JArray ovs)
                foreach (JObject ov in ovs)
                {
                    var om = AssetDatabase.LoadAssetAtPath<Material>((string)ov["mat"]);
                    var op = (string)ov["prop"];
                    var of = (string)ov["file"];
                    if (om == null || !om.HasProperty(op) || !File.Exists(of))
                        throw new Exception("texOverride not found: " + ov);
                    var ot = new Texture2D(2, 2, TextureFormat.RGBA32, true, (bool?)ov["linear"] ?? false)
                        { hideFlags = HideFlags.HideAndDontSave };
                    ot.LoadImage(File.ReadAllBytes(of));
                    texTemp.Add(ot);
                    texRestore.Add((om, op, om.GetTexture(op)));
                    om.SetTexture(op, ot);
                }
            var cam = go.AddComponent<Camera>();
            if (sv != null && sv.camera != null) cam.CopyFrom(sv.camera);
            cam.enabled = false;
            cam.cameraType = CameraType.SceneView;

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null) cam.scene = stage.scene;

            if (cmd["pos"] != null)
            {
                cam.transform.position = ToV(cmd["pos"], Vector3.zero);
                if (cmd["lookAt"] != null)
                    cam.transform.rotation = Quaternion.LookRotation(
                        ToV(cmd["lookAt"], Vector3.zero) - cam.transform.position, ToV(cmd["up"], Vector3.up));
                else
                    cam.transform.rotation = Quaternion.Euler(ToV(cmd["euler"], Vector3.zero));
            }
            float ortho = (float?)cmd["ortho"] ?? 0f;
            cam.orthographic = ortho > 0f;
            if (ortho > 0f) cam.orthographicSize = ortho;
            cam.fieldOfView   = (float?)cmd["fov"] ?? 60f;
            cam.nearClipPlane = (float?)cmd["near"] ?? 0.05f;
            cam.farClipPlane  = (float?)cmd["far"] ?? 500f;
            cam.aspect        = (float)w / h;
            cam.targetTexture = rt;
            // bg:[r,g,b] — clear to a flat colour instead of the sky (two captures on different colours give an alpha matte).
            if (cmd["bg"] != null)
            {
                var c3 = ToV(cmd["bg"], Vector3.zero);
                var col = new Color(c3.x, c3.y, c3.z, 1f);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = col;
                var hdType = Type.GetType("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime");
                if (hdType != null)
                {
                    var hd = go.GetComponent(hdType) ?? go.AddComponent(hdType);
                    var fMode = hdType.GetField("clearColorMode");
                    fMode.SetValue(hd, Enum.Parse(fMode.FieldType, "Color"));
                    hdType.GetField("backgroundColorHDR").SetValue(hd, col);
                }
            }
            // only:"Tachi" — hide every other scene root for this one render (restored below; scene is not marked dirty).
            if (cmd["only"] != null)
                foreach (var r in SceneManager.GetActiveScene().GetRootGameObjects())
                    if (r.activeSelf && r.name != (string)cmd["only"] && r.GetComponentInChildren<Renderer>() != null && r.GetComponentInChildren<Light>() == null)
                    { r.SetActive(false); hiddenRoots.Add(r); }
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            var file = Path.Combine(ShotDir, name + ".png");
            File.WriteAllBytes(file, tex.EncodeToPNG());
            return new JObject
            {
                ["file"] = file,
                ["pos"]  = V(cam.transform.position),
                ["euler"] = V(cam.transform.rotation.eulerAngles),
            };
        }
        finally
        {
            foreach (var r in hiddenRoots) if (r != null) r.SetActive(true);
            foreach (var r in texRestore) r.m.SetTexture(r.p, r.t);
            foreach (var t in texTemp) UnityEngine.Object.DestroyImmediate(t);
            if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    // ── placement ────────────────────────────────────────────────────────────
    // No Undo records are registered anywhere here (Undo is unstable in this project); every object the
    // bridge creates is written to a ledger, and "delete" only removes ledgered objects.
    //
    // { op:"group", parent:"Tachi/.../CommandDeck", name:"Command Consoles" }
    // { op:"place", guid, name, parent, euler:[x,y,z], center:[x,y,z] (bounds centre; y ignored when floorY set),
    //   floorY: y of the bounds bottom, pos:[x,y,z] (raw transform position instead of center) }
    // { op:"move", path, euler?, center?, floorY?, pos?, delta? }
    // { op:"delete", path }

    static readonly string LedgerFile = Path.Combine(Root, "placed.txt");

    static void Ledger(GameObject go) => File.AppendAllText(LedgerFile, PathOf(go) + "\n");
    static bool InLedger(string path) =>
        File.Exists(LedgerFile) && File.ReadAllLines(LedgerFile).Any(l => l == path || path.StartsWith(l + "/"));

    static void Dirty(GameObject go) => UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);

    static string UniqueName(Transform parent, string name)
    {
        var used = new HashSet<string>();
        foreach (Transform c in parent) used.Add(c.name);
        if (!used.Contains(name)) return name;
        for (int i = 1; ; i++) if (!used.Contains($"{name} ({i})")) return $"{name} ({i})";
    }

    // { op:"hardpoint", parent, name, pos:[x,y,z], euler:[x,y,z], list:"<ModuleListAsset guid>" }
    // A module spawn point: the game spawns one module from the list here as a child module of the ship, so the
    // job board's ship summary sees it (unlike a part an AddressableLoader spawns at runtime).
    static JToken HardPointOp(JObject cmd)
    {
        var parent = Find((string)cmd["parent"]);
        if (parent == null) throw new Exception("Parent not found: " + cmd["parent"]);
        var go = new GameObject(UniqueName(parent, (string)cmd["name"]), typeof(BBI.Unity.Game.HardPoint));
        go.transform.SetParent(parent, false);
        go.transform.position = ToV(cmd["pos"], parent.position);
        go.transform.rotation = Quaternion.Euler(ToV(cmd["euler"], Vector3.zero));
        typeof(BBI.Unity.Game.HardPoint).GetField("m_AssetRef", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .SetValue(go.GetComponent<BBI.Unity.Game.HardPoint>(), new BBI.Unity.Game.AssetReferenceModuleListAsset((string)cmd["list"]));
        EditorUtility.SetDirty(go.GetComponent<BBI.Unity.Game.HardPoint>());
        Ledger(go);
        Dirty(go);
        return new JObject { ["path"] = PathOf(go), ["pos"] = V(go.transform.position) };
    }

    // { op:"hardpointasset", folder:"Assets/...", name, refs:[{guid, weight}], empty:0, property:"<game property GUID>" }
    // Same as Assets / Create / Shipbreaker / Create hardpoint asset: writes the module list (entries, container, list)
    // into `folder`; with `property` the entries are tied to that ship property. Returns the list's GUID.
    static JToken HardPointAssetOp(JObject cmd)
    {
        var folder = ((string)cmd["folder"]).TrimEnd('/');
        AddressableBaker.EnsureFolder(folder);
        var refs = ((JArray)cmd["refs"]).Select(r => new Create_Hardpoint.HardpointAssetReference
            { assetRef = (string)r["guid"], weight = (float?)r["weight"] ?? 1f }).ToArray();
        var guid = Create_Hardpoint.CreateHardpointAsset(folder, (string)cmd["name"], refs, (float?)cmd["empty"] ?? 0f, (string)cmd["property"]);
        return new JObject { ["list"] = guid, ["path"] = AssetDatabase.GUIDToAssetPath(guid) };
    }

    // { op:"addrmove", guid, group:"Tachi" } — moves (or adds) an asset's Addressables entry into that group, keeping its
    // address as the asset path. hardpointasset puts new lists in the DEFAULT group (Common), which a ship build leaves out.
    static JToken AddrMove(JObject cmd)
    {
        var settings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
        var group = settings.FindGroup((string)cmd["group"]);
        if (group == null) throw new Exception("No Addressables group " + cmd["group"]);
        var guid = (string)cmd["guid"];
        var entry = settings.CreateOrMoveEntry(guid, group);
        entry.address = AssetDatabase.GUIDToAssetPath(guid);
        EditorUtility.SetDirty(settings); EditorUtility.SetDirty(group);
        AssetDatabase.SaveAssets();
        return new JObject { ["group"] = entry.parentGroup.Name, ["address"] = entry.address };
    }

    static JToken Group(JObject cmd)
    {
        var parent = Find((string)cmd["parent"]);
        if (parent == null) throw new Exception("Parent not found: " + cmd["parent"]);
        var name = (string)cmd["name"];
        foreach (Transform c in parent) if (c.name == name) return PathOf(c.gameObject);
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = parent.position;
        go.transform.rotation = Quaternion.identity;
        Ledger(go);
        Dirty(go);
        return PathOf(go);
    }

    // Mesh-only stand-in for the game prefab so the part is visible in the editor (same idea as the
    // project's FakePrefabDisplay previews; "Force View Refresh" replaces it with the real preview).
    static void CloneVisual(Transform src, Transform parent, string guid, List<Material> created, Dictionary<Material, Material> map, bool isRoot)
    {
        if (!src.gameObject.activeSelf) return;
        var go = new GameObject(isRoot ? guid : src.name) { hideFlags = HideFlags.DontSave };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = src.localPosition;
        go.transform.localRotation = src.localRotation;
        go.transform.localScale    = src.localScale;
        if (isRoot) go.AddComponent<FakePrefabDisplay>();
        go.AddComponent<SelectAddressableParent>().sourceGUID = guid;

        var mf = src.GetComponent<MeshFilter>();
        var mr = src.GetComponent<MeshRenderer>();
        if (mf != null && mr != null && mr.enabled && mf.sharedMesh != null)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
            var fake = Shader.Find("Fake/_Lynx/Surface/HDRP/Lit");
            var mats = (Material[])mr.sharedMaterials.Clone();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (!map.TryGetValue(m, out var rep))
                {
                    if (fake != null && m.shader != null && m.shader.name == "_Lynx/Surface/HDRP/Lit")
                    {
                        rep = new Material(fake) { hideFlags = HideFlags.DontSave };
                        rep.CopyPropertiesFromMaterial(m);
                    }
                    else rep = m;
                    map[m] = rep;
                }
                mats[i] = rep;
            }
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
        }
        foreach (Transform c in src) CloneVisual(c, go.transform, guid, created, map, false);
    }

    static void ApplyPose(Transform t, JObject cmd)
    {
        if (cmd["euler"] != null) t.rotation = Quaternion.Euler(ToV(cmd["euler"], Vector3.zero));
        if (cmd["localEuler"] != null) t.localRotation = Quaternion.Euler(ToV(cmd["localEuler"], Vector3.zero));
        if (cmd["forward"] != null) t.rotation = Quaternion.LookRotation(ToV(cmd["forward"], Vector3.forward), ToV(cmd["up"], Vector3.up));   // local +Z along forward, +Y towards up
        if (cmd["pos"] != null)   t.position = ToV(cmd["pos"], Vector3.zero);
        if (cmd["delta"] != null) t.position += ToV(cmd["delta"], Vector3.zero);
        if ((cmd["center"] != null || cmd["floorY"] != null) && TryBounds(t, out var b))
        {
            var shift = Vector3.zero;
            if (cmd["center"] != null) shift = ToV(cmd["center"], b.center) - b.center;
            if (cmd["floorY"] != null) shift.y = (float)cmd["floorY"] - b.min.y;
            t.position += shift;
        }
    }

    static JObject Describe(GameObject go)
    {
        var o = new JObject { ["path"] = PathOf(go), ["pos"] = V(go.transform.position), ["euler"] = V(go.transform.rotation.eulerAngles) };
        if (TryBounds(go.transform, out var b)) { o["bc"] = V(b.center); o["bs"] = V(b.size); o["min"] = V(b.min); o["max"] = V(b.max); }
        return o;
    }

    static JToken Place(JObject cmd)
    {
        var guid = (string)cmd["guid"];
        var parent = Find((string)cmd["parent"]);
        if (parent == null) throw new Exception("Parent not found: " + cmd["parent"]);
        if (!LoadGameAssets.CheckHandlesValid()) throw new Exception("Game assets not loaded");
        var src = AddressableBaker.LoadAddressableAsync(guid, "").Result;
        if (src == null) throw new Exception("Could not load addressable " + guid);

        var go = new GameObject(UniqueName(parent, (string)cmd["name"] ?? src.name));
        go.transform.SetParent(parent, false);
        go.transform.position = parent.position;
        go.transform.rotation = Quaternion.identity;
        go.AddComponent<BBI.Unity.Game.AddressableLoader>().assetGUID = guid;
        CloneVisual(src.transform, go.transform, guid, null, new Dictionary<Material, Material>(), true);
        ApplyPose(go.transform, cmd);
        Ledger(go);
        Dirty(go);
        return Describe(go);
    }

    static JToken Move(JObject cmd)
    {
        var t = Find((string)cmd["path"]);
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        if (!InLedger((string)cmd["path"]) && !((bool?)cmd["force"] ?? false)) throw new Exception("Refusing to move an object the bridge did not create (pass force:true if the user asked for it): " + cmd["path"]);
        ApplyPose(t, cmd);
        Dirty(t.gameObject);
        return Describe(t.gameObject);
    }

    static JToken Delete(JObject cmd)
    {
        var path = (string)cmd["path"];
        var t = Find(path);
        if (t == null) throw new Exception("Not found: " + path);
        if (!InLedger(path)) throw new Exception("Refusing to delete an object the bridge did not create: " + path);
        var scene = t.gameObject.scene;
        var instRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject);
        if (instRoot != null && !PrefabUtility.IsAddedGameObjectOverride(t.gameObject))
        {
            // Already saved into the ship prefab: Unity forbids destroying it on the instance, so remove it
            // from the prefab asset itself (the scene instance then updates to match).
            var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instRoot);
            var rel = RelPath(t, instRoot.transform);
            var contents = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var target = contents.transform.Find(rel);
                if (target == null) throw new Exception($"'{rel}' not found inside {assetPath}");
                UnityEngine.Object.DestroyImmediate(target.gameObject);
                PrefabUtility.SaveAsPrefabAsset(contents, assetPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            return $"deleted {path} from {assetPath}";
        }
        UnityEngine.Object.DestroyImmediate(t.gameObject);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        return "deleted " + path;
    }

    // ── inspect / placeprefab ────────────────────────────────────────────────
    // { op:"inspect", path, depth:1 } → prefab source, local scale, components and mesh names per node.
    // sodump {guids:[...], depth:4} — load game/mod ScriptableObjects by addressable GUID and dump their
    // serialized fields (object references as names, AssetReferences as GUIDs). Read-only.
    static JToken SoDump(JObject cmd)
    {
        int depth = (int?)cmd["depth"] ?? 4;
        s_SoExpand = (bool?)cmd["expand"] ?? false;   // expand referenced ScriptableObjects in place
        var outp = new JObject();
        foreach (var g in (JArray)cmd["guids"])
        {
            var guid = (string)g;
            try
            {
                // project assets (by asset GUID) first, so non-addressable local assets can be dumped too
                var localPath = AssetDatabase.GUIDToAssetPath(guid);
                var obj = string.IsNullOrEmpty(localPath) ? null : AssetDatabase.LoadMainAssetAtPath(localPath);
                var op = default(UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<UnityEngine.Object>);
                if (obj == null)
                {
                    op = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<UnityEngine.Object>(guid);
                    obj = op.WaitForCompletion();
                }
                if (obj == null) { outp[guid] = "null (" + op.Status + ")"; continue; }
                if (obj is Texture tx)
                {
                    var tj = new JObject { ["name"] = tx.name, ["type"] = tx.GetType().Name, ["width"] = tx.width, ["height"] = tx.height, ["format"] = (tx as Texture2D)?.format.ToString() };
                    if ((bool?)cmd["png"] ?? false)   // also write a PNG copy (with alpha) into .claude/bridge/shots
                    {
                        var rt = RenderTexture.GetTemporary(tx.width, tx.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                        var prev = RenderTexture.active;
                        Graphics.Blit(tx, rt);
                        RenderTexture.active = rt;
                        var copy = new Texture2D(tx.width, tx.height, TextureFormat.RGBA32, false);
                        copy.ReadPixels(new Rect(0, 0, tx.width, tx.height), 0, 0);
                        copy.Apply();
                        RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(rt);
                        var png = Path.Combine(ShotDir, "tex_" + tx.name + ".png");
                        File.WriteAllBytes(png, copy.EncodeToPNG());
                        UnityEngine.Object.DestroyImmediate(copy);
                        tj["png"] = png;
                    }
                    outp[guid] = tj;
                    continue;
                }
                outp[guid] = new JObject { ["name"] = obj.name, ["type"] = obj.GetType().Name, ["fields"] = SoDumpValue(obj, depth, true) };
            }
            catch (Exception ex) { outp[guid] = "error: " + ex.Message; }
        }
        return outp;
    }

    // { op:"prefabdump", guid:"<addressable or project prefab GUID>", depth:3 } — read-only: every transform of the
    // prefab with its local pose and each component's serialized fields (same value format as sodump).
    static JToken PrefabDump(JObject cmd)
    {
        var guid = (string)cmd["guid"];
        int depth = (int?)cmd["depth"] ?? 3;
        var localPath = AssetDatabase.GUIDToAssetPath(guid);
        var root = string.IsNullOrEmpty(localPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(localPath);
        if (root == null) root = AddressableBaker.LoadAddressableAsync(guid, "").Result;   // same loader the baker uses (handles multi-type keys)
        if (root == null) throw new Exception("Could not load prefab " + guid);
        s_SoExpand = false;
        return PrefabDumpNode(root.transform, depth);
    }

    static JObject PrefabDumpNode(Transform t, int depth)
    {
        var comps = new JArray();
        foreach (var c in t.GetComponents<Component>())
        {
            if (c == null) { comps.Add("<missing script>"); continue; }
            if (c is Transform) continue;
            var cj = new JObject { ["type"] = c.GetType().Name };
            if (c is MonoBehaviour) cj["fields"] = SoDumpValue(c, depth, true);
            else if (c is MeshFilter mf) cj["mesh"] = mf.sharedMesh != null ? mf.sharedMesh.name : null;
            else if (c is Renderer r) cj["materials"] = new JArray(r.sharedMaterials.Select(m => m != null ? m.name + " [" + string.Join(" ", m.shaderKeywords) + "]" : null));
            comps.Add(cj);
        }
        var o = new JObject
        {
            ["name"] = t.name, ["active"] = t.gameObject.activeSelf, ["localPos"] = V(t.localPosition),
            ["localEuler"] = V(t.localEulerAngles), ["localScale"] = V(t.localScale), ["components"] = comps,
        };
        var kids = new JArray();
        foreach (Transform c in t) kids.Add(PrefabDumpNode(c, depth));
        if (kids.Count > 0) o["kids"] = kids;
        return o;
    }

    static bool s_SoExpand;

    static JToken SoDumpValue(object v, int depth, bool root = false)
    {
        if (v == null) return JValue.CreateNull();
        var t = v.GetType();
        if (t.IsPrimitive || v is string || v is decimal) return JToken.FromObject(v);
        if (t.IsEnum) return v.ToString();
        if (v is UnityEngine.Object uo && !root)
        {
            if (uo == null) return JValue.CreateNull();
            if (!(s_SoExpand && uo is ScriptableObject && depth > 0)) return uo.name + " (" + t.Name + ")";
            return new JObject { ["name"] = uo.name, ["fields"] = SoDumpValue(uo, depth - 1, true) };
        }
        if (v is UnityEngine.AddressableAssets.AssetReference ar) return "ref:" + ar.AssetGUID;
        if (depth <= 0) return "…";
        if (v is System.Collections.IDictionary) return "dict";
        if (v is System.Collections.IEnumerable en)
        {
            var arr = new JArray();
            foreach (var e in en) { if (arr.Count >= 200) { arr.Add("…"); break; } arr.Add(SoDumpValue(e, depth - 1)); }
            return arr;
        }
        var o = new JObject();
        for (var bt = t; bt != null && bt != typeof(ScriptableObject) && bt != typeof(UnityEngine.Object) && bt != typeof(object) && bt != typeof(ValueType); bt = bt.BaseType)
            foreach (var f in bt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (f.IsNotSerialized) continue;
                if (!f.IsPublic && f.GetCustomAttribute<SerializeField>() == null) continue;
                try { o[f.Name] = SoDumpValue(f.GetValue(v), depth - 1); } catch (Exception ex) { o[f.Name] = "error: " + ex.Message; }
            }
        return o;
    }

    static JToken Inspect(JObject cmd)
    {
        var t = Find((string)cmd["path"]);
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        inspectFake = (bool?)cmd["fake"] ?? false;   // fake:true also walks the hidden addressable preview children
        try { return InspectNode(t, (int?)cmd["depth"] ?? 1); } finally { inspectFake = false; }
    }

    static bool inspectFake;
    static JObject InspectNode(Transform t, int depth)
    {
        var o = new JObject
        {
            ["name"] = t.name, ["localPos"] = V(t.localPosition), ["localEuler"] = V(t.localEulerAngles), ["localScale"] = V(t.localScale),
            ["lossyScale"] = V(t.lossyScale),
            ["components"] = new JArray(t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name)),
        };
        var src = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
        if (src != null && PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject))
            o["prefab"] = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject);
        var mf = t.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            o["mesh"] = mf.sharedMesh.name; o["meshAsset"] = AssetDatabase.GetAssetPath(mf.sharedMesh);
            o["meshSize"] = V(mf.sharedMesh.bounds.size);
        }
        var mr = t.GetComponent<MeshRenderer>();
        if (mr != null) o["materials"] = new JArray(mr.sharedMaterials.Select(m => m != null ? m.name : null));
        if (TryBounds(t, out var b)) { o["bc"] = V(b.center); o["bs"] = V(b.size); }
        if (depth > 0)
        {
            var kids = new JArray();
            foreach (Transform c in t)
                if (inspectFake || c.GetComponent<FakePrefabDisplay>() == null) kids.Add(InspectNode(c, depth - 1));
            if (kids.Count > 0) o["kids"] = kids;
        }
        return o;
    }

    // { op:"placeprefab", asset:"Assets/.../X.prefab", name, parent, euler, center, floorY, pos }
    // Instantiates a local project prefab (e.g. a baked part) as a linked prefab instance.
    static JToken PlacePrefab(JObject cmd)
    {
        var parent = Find((string)cmd["parent"]);
        if (parent == null) throw new Exception("Parent not found: " + cmd["parent"]);
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>((string)cmd["asset"]);
        if (asset == null) throw new Exception("Prefab not found: " + cmd["asset"]);
        var wanted = UniqueName(parent, (string)cmd["name"] ?? asset.name);   // before instantiating, or the new child counts as a clash
        var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
        go.name = wanted;
        go.transform.position = parent.position;
        go.transform.rotation = Quaternion.identity;
        ApplyPose(go.transform, cmd);
        Ledger(go);
        Dirty(go);
        return Describe(go);
    }

    // ── wall ─────────────────────────────────────────────────────────────────
    // { op:"wall", parent, name, width, height, glass:bool, euler:[0,yaw,0], center:[x,_,z], floorY }
    // Builds one wall panel the way deck 9's airlock walls were built: an instance of the baked board
    // prefab, resized with the project's Lock In Rescale (scale baked into the mesh, localScale back to 1),
    // optionally with the Glass material. At yaw 0 the panel spans X; yaw 90 spans Z.
    const string BoardPrefab = "Assets/_CustomShips/Tachi/Prefabs/Prefabs/PRF_AirLock_Board_Wall_Baked.prefab";
    const string GlassMat = "Assets/_CustomShips/FirstShip/Materials/Glass/Glass.mat";

    static JToken Wall(JObject cmd)
    {
        var parent = Find((string)cmd["parent"]);
        if (parent == null) throw new Exception("Parent not found: " + cmd["parent"]);
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>((string)cmd["asset"] ?? BoardPrefab);
        if (asset == null) throw new Exception("Board prefab not found");
        float width = (float)cmd["width"], height = (float)cmd["height"];

        var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
        go.name = UniqueName(parent, (string)cmd["name"] ?? "Wall");
        go.transform.position = parent.position;
        go.transform.rotation = Quaternion.identity;
        if (!TryBounds(go.transform, out var nb)) throw new Exception("Board prefab has no renderers");
        go.transform.localScale = new Vector3(width / nb.size.x, height / nb.size.y, 1f);
        RescaleLocker.LockRescale(go);

        if (cmd["material"] != null)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>((string)cmd["material"]);
            if (m == null) throw new Exception("Material not found: " + cmd["material"]);
            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                mr.sharedMaterials = Enumerable.Repeat(m, mr.sharedMaterials.Length).ToArray();
        }
        if (cmd["oi"] != null) ApplyOi(go, (string)cmd["oi"]);
        if ((bool?)cmd["glass"] ?? false)
        {
            var glass = AssetDatabase.LoadAssetAtPath<Material>(GlassMat);
            if (glass == null) throw new Exception("Glass material not found");
            foreach (var mr in go.GetComponentsInChildren<MeshRenderer>(true))
                mr.sharedMaterials = Enumerable.Repeat(glass, mr.sharedMaterials.Length).ToArray();
        }
        ApplyPose(go.transform, cmd);
        Ledger(go);
        Dirty(go);
        return Describe(go);
    }

    // { op:"meshuv", asset:"Assets/.../mesh.asset" | guid:"<addressable>" } → UV ranges per channel (diagnostic).
    static JToken MeshUv(JObject cmd)
    {
        var arr = new JArray();
        var meshes = new List<Mesh>();
        if (cmd["asset"] != null) meshes.AddRange(AssetDatabase.LoadAllAssetsAtPath((string)cmd["asset"]).OfType<Mesh>());
        if (cmd["guid"] != null)
        {
            var src = AddressableBaker.LoadAddressableAsync((string)cmd["guid"], "").Result;
            if (src != null) meshes.AddRange(src.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh != null).Select(f => f.sharedMesh));
        }
        foreach (var m in meshes)
        {
            var o = new JObject { ["mesh"] = m.name, ["verts"] = m.vertexCount, ["readable"] = m.isReadable, ["bounds"] = V(m.bounds.size) };
            for (int ch = 0; ch < 4; ch++)
            {
                var uv = new List<Vector2>();
                try { m.GetUVs(ch, uv); } catch (Exception ex) { o["uv" + ch] = "error: " + ex.Message; continue; }
                if (uv.Count == 0) continue;
                o["uv" + ch] = $"n={uv.Count} x[{uv.Min(u => u.x):F3},{uv.Max(u => u.x):F3}] y[{uv.Min(u => u.y):F3},{uv.Max(u => u.y):F3}]";
            }
            // per-face breakdown: which part of the texture each side of the mesh shows
            var uv0 = new List<Vector2>(); m.GetUVs(0, uv0);
            var normals = m.normals; var verts = m.vertices;
            if (uv0.Count == normals.Length && uv0.Count > 0)
            {
                var faces = new JObject();
                foreach (var g in Enumerable.Range(0, uv0.Count).GroupBy(i => $"{Mathf.Round(normals[i].x)},{Mathf.Round(normals[i].y)},{Mathf.Round(normals[i].z)}"))
                {
                    var us = g.Select(i => uv0[i]).ToList(); var ps = g.Select(i => verts[i]).ToList();
                    faces[g.Key] = $"uv x[{us.Min(u => u.x):F4},{us.Max(u => u.x):F4}] y[{us.Min(u => u.y):F4},{us.Max(u => u.y):F4}] " +
                                   $"size {ps.Max(p => p.x) - ps.Min(p => p.x):F3} x {ps.Max(p => p.y) - ps.Min(p => p.y):F3} x {ps.Max(p => p.z) - ps.Min(p => p.z):F3}";
                }
                o["faces"] = faces;
            }
            arr.Add(o);
        }
        return arr;
    }

    // { op:"bake", guid, name, folder:"Assets/_CustomShips/Tachi/Prefabs/Posters" }
    // Bakes a game addressable into a self-contained local prefab the same way the Import Game Part
    // wizard's Bake does (AddressableBaker.BakeTree + ACL for the GUID-resolved part assets).
    static JToken Bake(JObject cmd)
    {
        var guid = (string)cmd["guid"];
        var folder = ((string)cmd["folder"]).TrimEnd('/');
        var src = AddressableBaker.LoadAddressableAsync(guid, "").Result;
        if (src == null) throw new Exception("Could not load addressable " + guid);
        var name = (string)cmd["name"] ?? (src.name + "_Baked");
        var assetFolder = $"{folder}/{name}_Assets";
        var prefabPath = $"{folder}/{name}.prefab";
        AddressableBaker.ClearCaches();
        AddressableBaker.EnsureFolder(folder);
        AddressableBaker.EnsureFolder(assetFolder);
        var root = new GameObject(name);
        try
        {
            var refs = new List<(Component component, string field, string guid)>();
            AddressableBaker.BakeTree(src, root.transform, assetFolder, refs);
            if (refs.Count > 0)
            {
                var acl = root.AddComponent<BBI.Unity.Game.AddressableComponentLoader>();
                acl.componentValues = refs.Select(r => new BBI.Unity.Game.AddressableComponentValue { component = r.component, field = r.field, address = r.guid }).ToList();
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        var mats = new JArray();
        foreach (var mr in prefab.GetComponentsInChildren<MeshRenderer>(true))
            foreach (var m in mr.sharedMaterials)
            {
                if (m == null) continue;
                var tex = new JObject();
                foreach (var tn in m.GetTexturePropertyNames())
                {
                    var t = m.GetTexture(tn);
                    if (t != null) tex[tn] = AssetDatabase.GetAssetPath(t) + $" ({t.width}x{t.height}) st={m.GetTextureScale(tn)}/{m.GetTextureOffset(tn)}";
                }
                mats.Add(new JObject { ["renderer"] = mr.name, ["material"] = AssetDatabase.GetAssetPath(m), ["shader"] = m.shader.name, ["textures"] = tex });
            }
        return new JObject { ["prefab"] = prefabPath, ["materials"] = mats };
    }

    // { op:"postervariant", basePrefab, baseMaterial, texture:"Assets/.../art.png", texProp:"_BaseColorMap", name, folder }
    // Copies a baked poster prefab and gives it a copy of the material with a different artwork texture.
    static JToken PosterVariant(JObject cmd)
    {
        var folder = ((string)cmd["folder"]).TrimEnd('/');
        var name = (string)cmd["name"];
        var baseMat = AssetDatabase.LoadAssetAtPath<Material>((string)cmd["baseMaterial"]);
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>((string)cmd["texture"]);
        if (baseMat == null || tex == null) throw new Exception("Base material or texture not found");
        var matPath = $"{folder}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null) { mat = new Material(baseMat); AssetDatabase.CreateAsset(mat, matPath); }
        foreach (var prop in ((string)cmd["texProp"]).Split(','))
            if (mat.HasProperty(prop)) mat.SetTexture(prop, tex);
        EditorUtility.SetDirty(mat);

        var prefabPath = $"{folder}/{name}.prefab";
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>((string)cmd["basePrefab"]));
        try
        {
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.name = name;
            foreach (var mr in inst.GetComponentsInChildren<MeshRenderer>(true))
                mr.sharedMaterials = mr.sharedMaterials.Select(m => m == baseMat ? mat : m).ToArray();
            PrefabUtility.SaveAsPrefabAsset(inst, prefabPath);
        }
        finally { UnityEngine.Object.DestroyImmediate(inst); }
        AssetDatabase.SaveAssets();
        return new JObject { ["prefab"] = prefabPath, ["material"] = matPath };
    }

    // { op:"meshcast", rays:[{o:[x,y,z], d:[x,y,z]}], max:3, ignore:["name substring", ...] }
    // Casts each ray against the render-mesh triangles of everything in the scene (the same geometry the
    // game joints on) and returns the nearest hit: distance, point, world normal and object path.
    static readonly Dictionary<Mesh, KeyValuePair<Vector3[], int[]>> meshCache = new Dictionary<Mesh, KeyValuePair<Vector3[], int[]>>();

    static JToken MeshCast(JObject cmd)
    {
        if ((bool?)cmd["fresh"] ?? false) meshCache.Clear();   // meshes edited in place (meshload / jointpads) keep their old cached geometry otherwise
        float max = (float?)cmd["max"] ?? 5f;
        var ignore = cmd["ignore"] != null ? cmd["ignore"].Select(t => (string)t).ToList() : new List<string>();
        // inactive:true also tests disabled objects (e.g. hull walls switched off for access); only:[..] restricts by path.
        bool inactive = (bool?)cmd["inactive"] ?? false;
        var only = cmd["only"] != null ? cmd["only"].Select(t => (string)t).ToList() : null;
        var filters = new List<MeshFilter>();
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(inactive))
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled || mf.sharedMesh == null) continue;
                if (only != null) { var pth = PathOf(mf.gameObject); if (!only.Any(i => pth.Contains(i))) continue; }
                filters.Add(mf);
            }
        var unreadable = new HashSet<string>();
        var res = new JArray();
        foreach (var r in cmd["rays"])
        {
            var o = ToV(r["o"], Vector3.zero); var d = ToV(r["d"], Vector3.forward).normalized;
            var ray = new Ray(o, d);
            float best = max; Vector3 bn = Vector3.zero; MeshFilter bf = null;
            foreach (var mf in filters)
            {
                Bounds b;
                if (mf.gameObject.activeInHierarchy) b = mf.GetComponent<MeshRenderer>().bounds;
                else
                {   // a disabled renderer has no valid world bounds: transform the mesh bounds by hand
                    var mb = mf.sharedMesh.bounds; var l2w = mf.transform.localToWorldMatrix;
                    b = new Bounds(l2w.MultiplyPoint3x4(mb.center), Vector3.zero);
                    for (int cx = -1; cx <= 1; cx += 2) for (int cy = -1; cy <= 1; cy += 2) for (int cz = -1; cz <= 1; cz += 2)
                        b.Encapsulate(l2w.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3(cx, cy, cz))));
                }
                b.Expand(0.01f);
                if (!b.IntersectRay(ray, out float bd) || bd > best) continue;
                var path = PathOf(mf.gameObject);
                if (ignore.Any(i => path.Contains(i))) continue;
                if (!meshCache.TryGetValue(mf.sharedMesh, out var data))
                {
                    Vector3[] vs = null; int[] ts = null;
                    try { vs = mf.sharedMesh.vertices; ts = mf.sharedMesh.triangles; } catch { }
                    data = new KeyValuePair<Vector3[], int[]>(vs ?? new Vector3[0], ts ?? new int[0]);
                    meshCache[mf.sharedMesh] = data;
                }
                if (data.Key.Length == 0) { unreadable.Add(mf.sharedMesh.name); continue; }
                var w2l = mf.transform.worldToLocalMatrix;
                var lo = w2l.MultiplyPoint3x4(o); var ld = w2l.MultiplyVector(d);   // not normalised: t stays in world units
                var vsl = data.Key; var tsl = data.Value;
                for (int i = 0; i + 2 < tsl.Length; i += 3)
                {
                    var a = vsl[tsl[i]]; var e1 = vsl[tsl[i + 1]] - a; var e2 = vsl[tsl[i + 2]] - a;
                    var pv = Vector3.Cross(ld, e2); float det = Vector3.Dot(e1, pv);
                    if (det > -1e-9f && det < 1e-9f) continue;
                    float inv = 1f / det; var tv = lo - a;
                    float u = Vector3.Dot(tv, pv) * inv; if (u < 0f || u > 1f) continue;
                    var qv = Vector3.Cross(tv, e1);
                    float v = Vector3.Dot(ld, qv) * inv; if (v < 0f || u + v > 1f) continue;
                    float t = Vector3.Dot(e2, qv) * inv;
                    if (t > 1e-4f && t < best)
                    {
                        best = t; bf = mf;
                        bn = mf.transform.TransformDirection(Vector3.Cross(e1, e2)).normalized;
                    }
                }
            }
            if (bf == null) res.Add(JValue.CreateNull());
            else res.Add(new JObject { ["t"] = Math.Round(best, 4), ["p"] = V(o + d * best), ["n"] = V(bn), ["obj"] = PathOf(bf.gameObject) });
        }
        return new JObject { ["hits"] = res, ["unreadable"] = new JArray(unreadable) };
    }

    // { op:"setoi", path, oi:"Assets/.../OI_X.asset" } — gives every StructurePart under `path` that display name
    // (the ObjectInfoAsset override the project's Set Display Name tool wires). Omit `oi` to just report.
    static int ApplyOi(GameObject go, string oiPath)
    {
        var oi = AssetDatabase.LoadAssetAtPath<BBI.Unity.Game.ObjectInfoAsset>(oiPath);
        if (oi == null) throw new Exception("ObjectInfoAsset not found: " + oiPath);
        int n = 0;
        foreach (var sp in go.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true))
        {
            var so = new SerializedObject(sp);
            var prop = so.FindProperty("m_ObjectInfoAssetOverride");
            if (prop == null) continue;
            prop.objectReferenceValue = oi;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(sp);
            n++;
        }
        return n;
    }

    static JToken SetOi(JObject cmd)
    {
        var t = Find((string)cmd["path"]);
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        int set = 0;
        if (cmd["oi"] != null) { set = ApplyOi(t.gameObject, (string)cmd["oi"]); Dirty(t.gameObject); }
        var arr = new JArray();
        foreach (var sp in t.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true))
        {
            var oi = new SerializedObject(sp).FindProperty("m_ObjectInfoAssetOverride")?.objectReferenceValue;
            arr.Add(new JObject { ["part"] = sp.name, ["oi"] = oi != null ? AssetDatabase.GetAssetPath(oi) : null,
                ["displayName"] = oi != null ? new SerializedObject(oi).FindProperty("m_Data.m_ObjectName")?.stringValue : null });
        }
        return new JObject { ["set"] = set, ["parts"] = arr };
    }

    // { op:"signmat", baseMaterial, texture, texProp:"_MainTex,_BaseColorMap", name, folder } → a copy of the base
    // material showing `texture` (created once, reused after). Used with wall{asset,material} for text signs.
    static JToken SignMat(JObject cmd)
    {
        var folder = ((string)cmd["folder"]).TrimEnd('/');
        AddressableBaker.EnsureFolder(folder);
        var baseMat = AssetDatabase.LoadAssetAtPath<Material>((string)cmd["baseMaterial"]);
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>((string)cmd["texture"]);
        if (baseMat == null || tex == null) throw new Exception("Base material or texture not found: " + cmd["texture"]);
        var matPath = $"{folder}/{(string)cmd["name"]}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null) { mat = new Material(baseMat); AssetDatabase.CreateAsset(mat, matPath); }
        foreach (var prop in ((string)cmd["texProp"] ?? "_MainTex,_BaseColorMap").Split(','))
            if (mat.HasProperty(prop)) mat.SetTexture(prop, tex);
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return matPath;
    }


    // { op:"rescale", path, scale:[sx,sy,sz], euler, center, floorY, pos } — multiplies the object's LOCAL scale and bakes it
    // into the mesh with Lock In Rescale (localScale returns to 1), then applies the pose. For bridge-made objects.
    static JToken Rescale(JObject cmd)
    {
        var t = Find((string)cmd["path"]);
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        var s = ToV(cmd["scale"], Vector3.one);
        t.localScale = Vector3.Scale(t.localScale, s);
        RescaleLocker.LockRescale(t.gameObject);
        ApplyPose(t, cmd);
        Dirty(t.gameObject);
        return Describe(t.gameObject);
    }


    // { op:"disablechild", path, children:["PRF_CargoWall06", ...], enable:false } — adds (or with enable:true removes)
    // entries in an AddressableLoader's disabledChildren list (child paths relative to the loaded prefab root), the
    // same list the loader's inspector tree toggles, then refreshes the editor preview.
    static JToken DisableChild(JObject cmd)
    {
        var t = Find((string)cmd["path"]);
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        var loader = t.GetComponent<BBI.Unity.Game.AddressableLoader>();
        if (loader == null) throw new Exception("No AddressableLoader on " + cmd["path"]);
        bool enable = (bool?)cmd["enable"] ?? false;
        if (loader.disabledChildren == null) loader.disabledChildren = new List<string>();
        foreach (var c in cmd["children"])
        {
            var child = (string)c;
            if (enable) loader.disabledChildren.Remove(child);
            else if (!loader.disabledChildren.Contains(child)) loader.disabledChildren.Add(child);
        }
        EditorUtility.SetDirty(loader);
        Dirty(t.gameObject);
        AddressableRendering.UpdateViewList();
        return new JArray(loader.disabledChildren);
    }


    // { op:"bakeparts", guid, name, folder, sp, bp, smallSp, smallBp, smallMax:0.6, skip:[substr], oi }
    // Bakes a game addressable that has NO salvage data (Hab set dressing: no StructurePart, no colliders) into a local
    // prefab where every mesh is a real part: convex MeshCollider + StructurePart + EntityBlueprintComponent, with the
    // SP_Mat / blueprint resolved by GUID at load through an AddressableComponentLoader on the root. Meshes whose
    // largest dimension is under smallMax get smallSp / smallBp. Nodes matching `skip` are dropped.
    static JToken BakeParts(JObject cmd)
    {
        var guid = (string)cmd["guid"];
        var folder = ((string)cmd["folder"]).TrimEnd('/');
        var src = AddressableBaker.LoadAddressableAsync(guid, "").Result;
        if (src == null) throw new Exception("Could not load addressable " + guid);
        var name = (string)cmd["name"] ?? (src.name + "_Baked");
        string sp = (string)cmd["sp"], bp = (string)cmd["bp"];
        string ssp = (string)cmd["smallSp"] ?? sp, sbp = (string)cmd["smallBp"] ?? bp;
        float smallMax = (float?)cmd["smallMax"] ?? 0.6f;
        var skip = cmd["skip"] != null ? cmd["skip"].Select(t => (string)t).ToList() : new List<string>();
        var assetFolder = $"{folder}/{name}_Assets";
        var prefabPath = $"{folder}/{name}.prefab";
        AddressableBaker.ClearCaches();
        AddressableBaker.EnsureFolder(folder);
        AddressableBaker.EnsureFolder(assetFolder);
        var root = new GameObject(name);
        var report = new JArray();
        try
        {
            var refs = new List<(Component component, string field, string guid)>();
            AddressableBaker.BakeTree(src, root.transform, assetFolder, refs);
            foreach (var t in root.GetComponentsInChildren<Transform>(true).ToList())
                if (t != null && t != root.transform && skip.Any(k => t.name.Contains(k))) UnityEngine.Object.DestroyImmediate(t.gameObject);
            // decor:{ "<name substr>": "<host node name>" } — matching meshes stay render-only and ride along as children
            // of the host part (things sealed inside another part's hull, e.g. the snacks inside a vending machine).
            var decor = cmd["decor"] as JObject;
            var isDecor = new HashSet<GameObject>();
            if (decor != null)
                foreach (var kv in decor)
                {
                    var host = root.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == (string)kv.Value);
                    if (host == null) continue;
                    foreach (var m in root.GetComponentsInChildren<MeshFilter>(true).Where(m => m != host && m.name.Contains(kv.Key)).ToList())
                    {
                        m.transform.SetParent(host.transform, true);
                        foreach (var c in m.GetComponents<Collider>()) UnityEngine.Object.DestroyImmediate(c);
                        isDecor.Add(m.gameObject);
                    }
                }
            var clip = cmd["clipBelow"] as JObject;     // { "<name substr>": height } — collider only from that height (above the prefab base) up
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var go = mf.gameObject;
                if (isDecor.Contains(go)) continue;
                if (mf.sharedMesh == null || go.GetComponent<BBI.Unity.Game.StructurePart>() != null) continue;
                var size = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale);
                bool small = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) < smallMax;
                if (go.GetComponent<Collider>() == null)
                {
                    var mc = go.AddComponent<MeshCollider>();
                    Mesh colMesh = mf.sharedMesh;
                    var ck = clip?.Properties().FirstOrDefault(pr => go.name.Contains(pr.Name));
                    if (ck != null)
                    {
                        float minY = (float)ck.Value;
                        var keep = mf.sharedMesh.vertices.Where(v => root.transform.InverseTransformPoint(mf.transform.TransformPoint(v)).y >= minY).Distinct().ToList();
                        if (keep.Count >= 4)
                        {
                            var tris = new List<int>();
                            for (int i = 0; i + 2 < keep.Count; i++) { tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); }
                            colMesh = new Mesh { name = go.name + "_Collider" };
                            colMesh.SetVertices(keep); colMesh.SetTriangles(tris, 0); colMesh.RecalculateBounds();
                            AssetDatabase.CreateAsset(colMesh, $"{assetFolder}/{go.name}_Collider.asset");
                        }
                    }
                    mc.sharedMesh = colMesh; mc.convex = true;
                }
                var newSp = go.AddComponent<BBI.Unity.Game.StructurePart>();
                var newEbc = go.AddComponent<BBI.Unity.Game.EntityBlueprintComponent>();
                var so = new SerializedObject(newEbc);
                var ai = so.FindProperty("m_AutoInitialize");
                if (ai != null) { ai.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); }
                // liftSmall: small loose items hover this far above what they rest on instead of touching it
                if (small && cmd["liftSmall"] != null)
                {
                    var rend = go.GetComponent<MeshRenderer>();
                    float before = rend != null ? rend.bounds.min.y : go.transform.position.y;
                    if (cmd["tumble"] != null && rend != null)
                    {
                        float deg = (float)cmd["tumble"];
                        var rng = new System.Random((go.name + go.transform.GetSiblingIndex() + go.transform.parent.GetSiblingIndex()).GetHashCode());
                        Func<float> r1 = () => (float)(rng.NextDouble() * 2.0 - 1.0);
                        var pivot = rend.bounds.center;
                        var q = Quaternion.Euler(r1() * deg, r1() * 180f, r1() * deg);
                        go.transform.rotation = q * go.transform.rotation;
                        go.transform.position += pivot - rend.bounds.center;
                    }
                    float after = rend != null ? rend.bounds.min.y : go.transform.position.y;
                    go.transform.position += Vector3.up * ((before - after) + (float)cmd["liftSmall"]);
                }
                refs.Add((newSp, "m_StructurePartAsset", small ? ssp : sp));
                refs.Add((newEbc, "m_BlueprintAsset", small ? sbp : bp));
                report.Add(new JObject { ["node"] = go.name, ["size"] = V(size), ["small"] = small, ["readable"] = mf.sharedMesh.isReadable,
                                         ["localPos"] = V(root.transform.InverseTransformPoint(mf.transform.TransformPoint(mf.sharedMesh.bounds.center))) });
            }
            if (refs.Count > 0)
            {
                var acl = root.AddComponent<BBI.Unity.Game.AddressableComponentLoader>();
                acl.componentValues = refs.Select(r => new BBI.Unity.Game.AddressableComponentValue { component = r.component, field = r.field, address = r.guid }).ToList();
            }
            if (cmd["oi"] != null) ApplyOi(root, (string)cmd["oi"]);
            // parts must be unit scale in game: bake any scaled node into its mesh
            // (uniform scales only: scale each mesh by its accumulated factor, then push the factor down into child offsets)
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                float f = mf.transform.lossyScale.x / root.transform.lossyScale.x;
                if (mf.sharedMesh == null || Mathf.Abs(f - 1f) < 1e-4f) continue;
                var m2 = UnityEngine.Object.Instantiate(mf.sharedMesh);
                m2.name = mf.sharedMesh.name + "_x" + f.ToString("F3");
                m2.vertices = mf.sharedMesh.vertices.Select(v => v * f).ToArray();
                m2.RecalculateBounds();
                AssetDatabase.CreateAsset(m2, AssetDatabase.GenerateUniqueAssetPath($"{assetFolder}/{m2.name}.asset"));
                var mcs = mf.GetComponent<MeshCollider>();
                if (mcs != null && mcs.sharedMesh == mf.sharedMesh) mcs.sharedMesh = m2;
                else if (mcs != null && mcs.sharedMesh != null)
                {
                    var c2 = UnityEngine.Object.Instantiate(mcs.sharedMesh);
                    c2.name = mcs.sharedMesh.name + "_x" + f.ToString("F3");
                    c2.vertices = mcs.sharedMesh.vertices.Select(v => v * f).ToArray(); c2.RecalculateBounds();
                    AssetDatabase.CreateAsset(c2, AssetDatabase.GenerateUniqueAssetPath($"{assetFolder}/{c2.name}.asset"));
                    mcs.sharedMesh = c2;
                }
                mf.sharedMesh = m2;
            }
            foreach (var t in root.GetComponentsInChildren<Transform>(true))        // parents come before their children
            {
                if (t == root.transform || !RescaleLocker.IsNonUnitScale(t)) continue;
                float f = t.localScale.x;
                foreach (Transform c in t) { c.localPosition *= f; c.localScale *= f; }
                t.localScale = Vector3.one;
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return new JObject { ["prefab"] = prefabPath, ["parts"] = report };
    }


    // { op:"room", parent, name, center:[x,y,z], size:[x,y,z], roomAsset:"<DynamicRoomContainerAsset guid>", airlock:false }
    // Adds one box room the way the ship's existing rooms are authored: '[ ] <name>' (ModuleDefinition) > 'Volumes'
    // (RoomContainerDefinition + AddressableSOLoader resolving the room asset by GUID) > 'Volume' (RoomSubVolumeDefinition,
    // Include). World-axis aligned; center/size are world values. Remove by deleting the '[ ] <name>' object.
    static JToken Room(JObject cmd)
    {
        var parent = Find((string)cmd["parent"]);
        if (parent == null) throw new Exception("Parent not found: " + cmd["parent"]);
        var center = ToV(cmd["center"], Vector3.zero); var size = ToV(cmd["size"], Vector3.one);
        var room = new GameObject(UniqueName(parent, "[ ] " + (string)cmd["name"]));
        room.transform.SetParent(parent, false);
        room.transform.position = center; room.transform.rotation = Quaternion.identity;
        room.AddComponent<BBI.Unity.Game.ModuleDefinition>();
        var vols = new GameObject("Volumes"); vols.transform.SetParent(room.transform, false);
        var rcd = vols.AddComponent<BBI.Unity.Game.RoomContainerDefinition>();
        var so = new SerializedObject(rcd);
        so.FindProperty("m_IsAirlock").boolValue = (bool?)cmd["airlock"] ?? false;
        var pt = so.FindProperty("m_PressurizationTime"); if (pt != null) pt.floatValue = 2f;
        var dt = so.FindProperty("m_DecompressionTime"); if (dt != null) dt.floatValue = 2f;
        so.ApplyModifiedPropertiesWithoutUndo();
        var loader = vols.AddComponent<BBI.Unity.Game.AddressableSOLoader>();
        loader.comp = new List<string> { "BBI.Unity.Game.RoomContainerDefinition" };
        loader.field = new List<string> { "m_DynamicRoomContainerAsset" };
        loader.refs = new List<string> { (string)cmd["roomAsset"] };
        var vol = new GameObject("Volume"); vol.transform.SetParent(vols.transform, false);
        var sub = vol.AddComponent<BBI.Unity.Game.RoomSubVolumeDefinition>();
        var so2 = new SerializedObject(sub);
        so2.FindProperty("m_Center").vector3Value = Vector3.zero;
        so2.FindProperty("m_Size").vector3Value = size;
        so2.FindProperty("m_Mode").intValue = 0;
        so2.ApplyModifiedPropertiesWithoutUndo();
        Ledger(room);
        Dirty(room);
        return new JObject { ["path"] = PathOf(room), ["center"] = V(center), ["size"] = V(size) };
    }


    // { op:"rooms" } — every RoomContainerDefinition in the scene with its airlock flag and the world-space box of each
    // sub-volume (corners of the local box transformed to world, reported as an axis-aligned min/max).
    static JToken Rooms(JObject cmd)
    {
        var arr = new JArray();
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (var rcd in root.GetComponentsInChildren<BBI.Unity.Game.RoomContainerDefinition>(true))
            {
                var so = new SerializedObject(rcd);
                var o = new JObject { ["path"] = PathOf(rcd.gameObject), ["active"] = rcd.gameObject.activeInHierarchy,
                                      ["airlock"] = so.FindProperty("m_IsAirlock").boolValue };
                var vols = new JArray();
                foreach (var sv in rcd.GetComponentsInChildren<BBI.Unity.Game.RoomSubVolumeDefinition>(true))
                {
                    var s2 = new SerializedObject(sv);
                    var c = s2.FindProperty("m_Center").vector3Value; var sz = s2.FindProperty("m_Size").vector3Value;
                    var b = new Bounds(sv.transform.TransformPoint(c), Vector3.zero);
                    for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                        b.Encapsulate(sv.transform.TransformPoint(c + Vector3.Scale(sz * 0.5f, new Vector3(x, y, z))));
                    vols.Add(new JObject { ["name"] = sv.name, ["mode"] = s2.FindProperty("m_Mode").intValue, ["min"] = V(b.min), ["max"] = V(b.max) });
                }
                o["volumes"] = vols;
                arr.Add(o);
            }
        return arr;
    }

    // { op:"roomvolume", room:"<path of a '[ ] name' room made by op room>", name, center:[..], size:[..] }
    // Adds another world-aligned Include box to an existing room, so one room can cover an L- or U-shaped space.
    static JToken RoomVolume(JObject cmd)
    {
        var room = Find((string)cmd["room"]);
        if (room == null) throw new Exception("Room not found: " + cmd["room"]);
        var rcd = room.GetComponentInChildren<BBI.Unity.Game.RoomContainerDefinition>(true);
        if (rcd == null) throw new Exception("No RoomContainerDefinition under " + cmd["room"]);
        var vol = new GameObject(UniqueName(rcd.transform, (string)cmd["name"] ?? "Volume"));
        vol.transform.SetParent(rcd.transform, false);
        vol.transform.position = ToV(cmd["center"], Vector3.zero); vol.transform.rotation = Quaternion.identity;
        var sub = vol.AddComponent<BBI.Unity.Game.RoomSubVolumeDefinition>();
        var so = new SerializedObject(sub);
        so.FindProperty("m_Center").vector3Value = Vector3.zero;
        so.FindProperty("m_Size").vector3Value = ToV(cmd["size"], Vector3.one);
        so.FindProperty("m_Mode").intValue = 0;
        so.ApplyModifiedPropertiesWithoutUndo();
        Dirty(vol);
        return PathOf(vol);
    }

    // { op:"splitpart", source:"Tachi/.../PDC_tubes_2", model:"Assets/.../Fore_Section_Split.fbx", prefix:"PDC_tubes_2_p", disableSource:true }
    // Replaces one custom part by several pieces of it: for every mesh in `model` whose name starts with `prefix`, the
    // source object is duplicated beside itself (same transform, StructurePart, blueprint, collider settings), given
    // that mesh on its MeshFilter and MeshCollider and the materials the model import assigned to it, and its
    // GUID-resolved asset references are registered on the same AddressableComponentLoader as the source's.
    // The source is then switched off (not deleted).
    static JToken SplitPart(JObject cmd)
    {
        var src = Find((string)cmd["source"]);
        if (src == null) throw new Exception("Source not found: " + cmd["source"]);
        var model = (string)cmd["model"]; var prefix = (string)cmd["prefix"];
        var meshes = AssetDatabase.LoadAllAssetsAtPath(model).OfType<Mesh>().Where(m => m.name.StartsWith(prefix)).OrderBy(m => m.name).ToList();
        if (meshes.Count == 0) throw new Exception("No meshes starting with '" + prefix + "' in " + model);
        var modelRoot = AssetDatabase.LoadAssetAtPath<GameObject>(model);
        BBI.Unity.Game.AddressableComponentLoader acl = null;
        for (var t = src; t != null && acl == null; t = t.parent) acl = t.GetComponent<BBI.Unity.Game.AddressableComponentLoader>();
        var srcAll = src.GetComponentsInChildren<Component>(true);
        var srcEntries = acl != null ? acl.componentValues.Where(cv => cv != null && cv.component != null && Array.IndexOf(srcAll, cv.component) >= 0).ToList() : null;
        var srcR = src.GetComponent<MeshRenderer>();
        var made = new JArray();
        Bounds? union = null;
        foreach (var mesh in meshes)
        {
            var go = UnityEngine.Object.Instantiate(src.gameObject, src.parent);
            go.name = mesh.name;
            go.transform.localPosition = src.localPosition; go.transform.localRotation = src.localRotation; go.transform.localScale = src.localScale;
            go.SetActive(true);
            var mf = go.GetComponent<MeshFilter>(); if (mf != null) mf.sharedMesh = mesh;
            var mc = go.GetComponent<MeshCollider>(); if (mc != null) mc.sharedMesh = mesh;
            var mr = go.GetComponent<MeshRenderer>();
            string modelPose = "";
            if (modelRoot != null)
            {
                var node = modelRoot.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(f => f.sharedMesh == mesh);
                if (node != null)
                {
                    var nr = node.GetComponent<MeshRenderer>();
                    if (nr != null && mr != null) mr.sharedMaterials = nr.sharedMaterials;
                    modelPose = "model node pos " + V(node.transform.localPosition) + " euler " + V(node.transform.localEulerAngles);
                }
            }
            int reg = 0;
            if (srcEntries != null)
            {
                var dstAll = go.GetComponentsInChildren<Component>(true);
                foreach (var cv in srcEntries)
                {
                    int idx = Array.IndexOf(srcAll, cv.component);
                    if (idx < 0 || idx >= dstAll.Length || dstAll[idx] == null || dstAll[idx].GetType() != cv.component.GetType()) continue;
                    acl.componentValues.Add(new BBI.Unity.Game.AddressableComponentValue { component = dstAll[idx], field = cv.field, address = cv.address });
                    reg++;
                }
            }
            Ledger(go); Dirty(go);
            if (mr != null) { if (union == null) union = mr.bounds; else { var b = union.Value; b.Encapsulate(mr.bounds); union = b; } }
            made.Add(new JObject { ["name"] = go.name, ["verts"] = mesh.vertexCount, ["subMeshes"] = mesh.subMeshCount,
                ["materials"] = mr != null ? new JArray(mr.sharedMaterials.Select(m => m != null ? m.name : "null")) : null,
                ["aclEntries"] = reg, ["convex"] = mc != null ? (JToken)mc.convex : null, ["modelPose"] = modelPose });
        }
        if (acl != null) EditorUtility.SetDirty(acl);
        var o = new JObject { ["made"] = made, ["srcVerts"] = src.GetComponent<MeshFilter>() != null ? src.GetComponent<MeshFilter>().sharedMesh.vertexCount : 0,
            ["srcLocalPos"] = V(src.localPosition), ["srcLocalEuler"] = V(src.localEulerAngles), ["aclOn"] = acl != null ? PathOf(acl.gameObject) : null,
            ["srcAclEntries"] = srcEntries != null ? srcEntries.Count : 0 };
        if (srcR != null) { o["srcBoundsMin"] = V(srcR.bounds.min); o["srcBoundsMax"] = V(srcR.bounds.max); }
        if (union != null) { o["newBoundsMin"] = V(union.Value.min); o["newBoundsMax"] = V(union.Value.max); }
        if ((bool?)cmd["disableSource"] ?? true) { src.gameObject.SetActive(false); Dirty(src.gameObject); }
        return o;
    }

    // { op:"meshweld", asset:"Assets/.../x.asset", key:"full"|"pnu"|"pu"|"p", dry:true }
    // Merges vertices of a mesh asset IN PLACE (same asset, so every reference keeps working). key = what must match for
    // two vertices to merge: full = every channel, pnu = position+normal+uv0, pu = position+uv0 (normals averaged),
    // p = position only (normals averaged, first uv kept). Always reports the counts each key would give.
    static JToken MeshWeld(JObject cmd)
    {
        var path = (string)cmd["asset"];
        var m = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault();
        if (m == null) throw new Exception("No mesh in " + path);
        string keyMode = (string)cmd["key"] ?? "full"; bool dry = (bool?)cmd["dry"] ?? true;
        int n = m.vertexCount;
        var pos = m.vertices; var nrm = m.normals; var tan = m.tangents; var col = m.colors32;
        var uvs = new List<Vector4>[8]; var dims = new int[8];
        for (int ch = 0; ch < 8; ch++)
        {
            uvs[ch] = new List<Vector4>(); m.GetUVs(ch, uvs[ch]);
            dims[ch] = uvs[ch].Count == n ? m.GetVertexAttributeDimension((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch)) : 0;
        }
        Func<Vector3, float, string> q3 = (v, s) => Mathf.RoundToInt(v.x * s) + "," + Mathf.RoundToInt(v.y * s) + "," + Mathf.RoundToInt(v.z * s);
        Func<int, string, string> keyOf = (i, mode) =>
        {
            var k = q3(pos[i], 100000f);
            if (mode == "p") return k;
            if (dims[0] > 0) k += "|" + q3(uvs[0][i], 100000f);
            if (mode == "pu") return k;
            if (nrm.Length == n) k += "|" + q3(nrm[i], 500f);
            if (mode == "pnu") return k;
            if (tan.Length == n) k += "|" + q3(tan[i], 500f) + "," + tan[i].w;
            if (col.Length == n) k += "|" + col[i].r + "," + col[i].g + "," + col[i].b + "," + col[i].a;
            for (int ch = 1; ch < 8; ch++) if (dims[ch] > 0) k += "|" + q3(uvs[ch][i], 100000f) + "," + Mathf.RoundToInt(uvs[ch][i].w * 100000f);
            return k;
        };
        long tris = 0; for (int s = 0; s < m.subMeshCount; s++) tris += m.GetIndexCount(s) / 3;
        var o = new JObject { ["mesh"] = m.name, ["verts"] = n, ["tris"] = tris, ["subMeshes"] = m.subMeshCount, ["readable"] = m.isReadable,
            ["normals"] = nrm.Length == n, ["tangents"] = tan.Length == n, ["colors"] = col.Length == n, ["uvDims"] = new JArray(dims),
            ["boneWeights"] = m.boneWeights.Length, ["blendShapes"] = m.blendShapeCount, ["indexFormat"] = m.indexFormat.ToString() };
        var used = new HashSet<int>();
        for (int s = 0; s < m.subMeshCount; s++) foreach (var i in m.GetIndices(s)) used.Add(i);
        o["usedVerts"] = used.Count;
        foreach (var mode in new[] { "full", "pnu", "pu", "p" })
            o["unique_" + mode] = used.Select(i => keyOf(i, mode)).Distinct().Count();
        if (dry) return o;

        var map = new Dictionary<string, int>(); var remap = new int[n]; var first = new List<int>();
        var nSum = new List<Vector3>(); var tSum = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            if (!used.Contains(i)) { remap[i] = -1; continue; }
            var k = keyOf(i, keyMode); int id;
            if (!map.TryGetValue(k, out id)) { id = first.Count; map[k] = id; first.Add(i); nSum.Add(Vector3.zero); tSum.Add(Vector3.zero); }
            remap[i] = id;
            if (nrm.Length == n) nSum[id] += nrm[i];
            if (tan.Length == n) tSum[id] += new Vector3(tan[i].x, tan[i].y, tan[i].z);
        }
        int c = first.Count;
        var subs = new List<int[]>(); var topo = new List<MeshTopology>();
        for (int s = 0; s < m.subMeshCount; s++) { subs.Add(m.GetIndices(s).Select(i => remap[i]).ToArray()); topo.Add(m.GetTopology(s)); }
        int dropped = 0;
        for (int s = 0; s < subs.Count; s++)
        {
            if (topo[s] != MeshTopology.Triangles) continue;
            var keep = new List<int>(); var a = subs[s];
            for (int t = 0; t + 2 < a.Length; t += 3)
            {
                if (a[t] == a[t + 1] || a[t + 1] == a[t + 2] || a[t] == a[t + 2]) { dropped++; continue; }
                keep.Add(a[t]); keep.Add(a[t + 1]); keep.Add(a[t + 2]);
            }
            subs[s] = keep.ToArray();
        }
        var bounds = m.bounds; var name = m.name;
        var nPos = first.Select(i => pos[i]).ToArray();
        Vector3[] nNrm = nrm.Length == n ? Enumerable.Range(0, c).Select(j => nSum[j].sqrMagnitude > 1e-12f ? nSum[j].normalized : nrm[first[j]]).ToArray() : null;
        Vector4[] nTan = tan.Length == n ? Enumerable.Range(0, c).Select(j => { var t = tSum[j].sqrMagnitude > 1e-12f ? tSum[j].normalized : (Vector3)tan[first[j]]; return new Vector4(t.x, t.y, t.z, tan[first[j]].w); }).ToArray() : null;
        Color32[] nCol = col.Length == n ? first.Select(i => col[i]).ToArray() : null;
        m.Clear();
        m.indexFormat = c > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
        m.vertices = nPos;
        if (nNrm != null) m.normals = nNrm;
        if (nTan != null) m.tangents = nTan;
        if (nCol != null) m.colors32 = nCol;
        for (int ch = 0; ch < 8; ch++)
        {
            if (dims[ch] == 0) continue;
            var src = uvs[ch];
            if (dims[ch] == 2) m.SetUVs(ch, first.Select(i => (Vector2)src[i]).ToList());
            else if (dims[ch] == 3) m.SetUVs(ch, first.Select(i => (Vector3)src[i]).ToList());
            else m.SetUVs(ch, first.Select(i => src[i]).ToList());
        }
        m.subMeshCount = subs.Count;
        for (int s = 0; s < subs.Count; s++) m.SetIndices(subs[s], topo[s], s, false);
        m.bounds = bounds; m.name = name;
        EditorUtility.SetDirty(m); AssetDatabase.SaveAssets();
        o["key"] = keyMode; o["newVerts"] = m.vertexCount; o["droppedDegenerateTris"] = dropped;
        long nt = 0; for (int s = 0; s < m.subMeshCount; s++) nt += m.GetIndexCount(s) / 3; o["newTris"] = nt;
        return o;
    }

    // { op:"meshdump", asset:"Assets/.../x.asset", out:"C:/.../x.json" } — writes positions, normals, uv0, uv1 and triangles
    // of a single-submesh mesh asset as flat JSON arrays (Unity coordinates).
    static JToken MeshDump(JObject cmd)
    {
        var path = (string)cmd["asset"];
        var m = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault();
        if (m == null) throw new Exception("No mesh in " + path);
        if (m.subMeshCount != 1 || m.GetTopology(0) != MeshTopology.Triangles) throw new Exception("meshdump handles one triangle submesh; this has " + m.subMeshCount);
        var uv0 = new List<Vector2>(); var uv1 = new List<Vector2>(); m.GetUVs(0, uv0); m.GetUVs(1, uv1);
        var o = new JObject
        {
            ["name"] = m.name,
            ["pos"] = new JArray(m.vertices.SelectMany(v => new[] { v.x, v.y, v.z })),
            ["nrm"] = new JArray(m.normals.SelectMany(v => new[] { v.x, v.y, v.z })),
            ["uv0"] = new JArray(uv0.SelectMany(v => new[] { v.x, v.y })),
            ["uv1"] = new JArray(uv1.SelectMany(v => new[] { v.x, v.y })),
            ["tri"] = new JArray(m.triangles),
        };
        File.WriteAllText((string)cmd["out"], o.ToString(Newtonsoft.Json.Formatting.None));
        return new JObject { ["verts"] = m.vertexCount, ["tris"] = m.triangles.Length / 3, ["out"] = (string)cmd["out"] };
    }

    // { op:"meshload", asset:"Assets/.../x.asset", in:"C:/.../x.json" } — replaces the geometry of the mesh asset IN PLACE
    // (same asset and name, so every reference keeps working) from the JSON layout meshdump writes; tangents are recalculated.
    static JToken MeshLoad(JObject cmd)
    {
        var path = (string)cmd["asset"];
        var m = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().FirstOrDefault();
        if (m == null) throw new Exception("No mesh in " + path);
        var j = JObject.Parse(File.ReadAllText((string)cmd["in"]));
        Func<string, float[]> F = k => j[k] == null ? new float[0] : j[k].Select(t => (float)t).ToArray();
        var p = F("pos"); var nr = F("nrm"); var a0 = F("uv0"); var a1 = F("uv1"); var tri = j["tri"].Select(t => (int)t).ToArray();
        int n = p.Length / 3;
        if (n == 0 || n > 65535) throw new Exception("vertex count " + n + " not usable");
        int before = m.vertexCount; var name = m.name; bool hadTan = m.tangents.Length == before;
        m.Clear();
        m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
        m.vertices = Enumerable.Range(0, n).Select(i => new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2])).ToArray();
        if (nr.Length == n * 3) m.normals = Enumerable.Range(0, n).Select(i => new Vector3(nr[i * 3], nr[i * 3 + 1], nr[i * 3 + 2])).ToArray();
        if (a0.Length == n * 2) m.SetUVs(0, Enumerable.Range(0, n).Select(i => new Vector2(a0[i * 2], a0[i * 2 + 1])).ToList());
        if (a1.Length == n * 2) m.SetUVs(1, Enumerable.Range(0, n).Select(i => new Vector2(a1[i * 2], a1[i * 2 + 1])).ToList());
        m.subMeshCount = 1;
        m.SetTriangles(tri, 0, true);
        if (hadTan && a0.Length == n * 2) m.RecalculateTangents();
        m.RecalculateBounds();
        m.name = name;
        EditorUtility.SetDirty(m); AssetDatabase.SaveAssets();
        return new JObject { ["before"] = before, ["verts"] = m.vertexCount, ["tris"] = tri.Length / 3, ["bounds"] = V(m.bounds.size) };
    }

    // { op:"meshparts", source:"Tachi/.../SM_Bodysuit_Mesh_Bodysuit", folder:"Assets/.../CorpseParts", dir:"C:/.../corpse_parts",
    //   items:[{ name, localPos:[x,y,z] }], disable:[paths] }
    // For every item: builds (or overwrites) the mesh asset <folder>/<name>.asset from <dir>/<name>.json (meshdump layout),
    // duplicates `source` beside itself with that mesh on its MeshFilter and MeshCollider, at localPos with no rotation,
    // and registers the copy's GUID-resolved references on the same AddressableComponentLoader as the source's (if any).
    // An existing sibling with the item's name is replaced. The paths in `disable` are then switched off.
    static JToken MeshParts(JObject cmd)
    {
        var src = Find((string)cmd["source"]);
        if (src == null) throw new Exception("Source not found: " + cmd["source"]);
        var folder = (string)cmd["folder"]; var dir = (string)cmd["dir"];
        if (!AssetDatabase.IsValidFolder(folder))
        {
            var parent = Path.GetDirectoryName(folder).Replace("\\", "/");
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
        BBI.Unity.Game.AddressableComponentLoader acl = null;
        for (var t = src; t != null && acl == null; t = t.parent) acl = t.GetComponent<BBI.Unity.Game.AddressableComponentLoader>();
        var srcAll = src.GetComponentsInChildren<Component>(true);
        var srcEntries = acl != null ? acl.componentValues.Where(cv => cv != null && cv.component != null && Array.IndexOf(srcAll, cv.component) >= 0).ToList() : null;
        var made = new JArray();
        foreach (JObject it in cmd["items"])
        {
            var name = (string)it["name"];
            var j = JObject.Parse(File.ReadAllText(Path.Combine(dir, name + ".json")));
            Func<string, float[]> F = k => j[k] == null ? new float[0] : j[k].Select(t => (float)t).ToArray();
            var p = F("pos"); var nr = F("nrm"); var a0 = F("uv0"); var a1 = F("uv1"); var tri = j["tri"].Select(t => (int)t).ToArray();
            int n = p.Length / 3;
            if (n == 0 || n > 65535) throw new Exception(name + ": vertex count " + n + " not usable");
            var assetPath = folder + "/" + name + ".asset";
            var m = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            bool isNew = m == null;
            if (isNew) m = new Mesh(); else m.Clear();
            m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
            m.vertices = Enumerable.Range(0, n).Select(i => new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2])).ToArray();
            if (nr.Length == n * 3) m.normals = Enumerable.Range(0, n).Select(i => new Vector3(nr[i * 3], nr[i * 3 + 1], nr[i * 3 + 2])).ToArray();
            if (a0.Length == n * 2) m.SetUVs(0, Enumerable.Range(0, n).Select(i => new Vector2(a0[i * 2], a0[i * 2 + 1])).ToList());
            if (a1.Length == n * 2) m.SetUVs(1, Enumerable.Range(0, n).Select(i => new Vector2(a1[i * 2], a1[i * 2 + 1])).ToList());
            m.subMeshCount = 1;
            m.SetTriangles(tri, 0, true);
            if (a0.Length == n * 2) m.RecalculateTangents();
            m.RecalculateBounds();
            m.name = name;
            if (isNew) AssetDatabase.CreateAsset(m, assetPath); else EditorUtility.SetDirty(m);

            var old = src.parent.Find(name);
            if (old != null && ((bool?)cmd["reuse"] ?? true))
            {
                // already placed (and possibly saved into the ship prefab): the mesh asset was rewritten in place, just move it
                var lp0 = it["localPos"];
                old.localPosition = new Vector3((float)lp0[0], (float)lp0[1], (float)lp0[2]);
                Dirty(old.gameObject);
                made.Add(new JObject { ["name"] = name, ["verts"] = m.vertexCount, ["reused"] = true });
                continue;
            }
            if (old != null)
            {
                if (acl != null) { var oldAll = old.GetComponentsInChildren<Component>(true); acl.componentValues.RemoveAll(cv => cv != null && cv.component != null && Array.IndexOf(oldAll, cv.component) >= 0); }
                UnityEngine.Object.DestroyImmediate(old.gameObject);
            }
            var go = UnityEngine.Object.Instantiate(src.gameObject, src.parent);
            go.name = name;
            var lp = it["localPos"];
            go.transform.localPosition = new Vector3((float)lp[0], (float)lp[1], (float)lp[2]);
            go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
            go.SetActive(true);
            var mf = go.GetComponent<MeshFilter>(); if (mf != null) mf.sharedMesh = m;
            var mc = go.GetComponent<MeshCollider>(); if (mc != null) mc.sharedMesh = m;
            int reg = 0;
            if (srcEntries != null)
            {
                var dstAll = go.GetComponentsInChildren<Component>(true);
                foreach (var cv in srcEntries)
                {
                    int idx = Array.IndexOf(srcAll, cv.component);
                    if (idx < 0 || idx >= dstAll.Length || dstAll[idx] == null || dstAll[idx].GetType() != cv.component.GetType()) continue;
                    acl.componentValues.Add(new BBI.Unity.Game.AddressableComponentValue { component = dstAll[idx], field = cv.field, address = cv.address });
                    reg++;
                }
            }
            Ledger(go); Dirty(go);
            var mr = go.GetComponent<MeshRenderer>();
            made.Add(new JObject { ["name"] = name, ["verts"] = m.vertexCount, ["aclEntries"] = reg, ["convex"] = mc != null ? (JToken)mc.convex : null,
                ["materials"] = mr != null ? new JArray(mr.sharedMaterials.Select(x => x != null ? x.name : "null")) : null,
                ["worldMin"] = mr != null ? V(mr.bounds.min) : null, ["worldMax"] = mr != null ? V(mr.bounds.max) : null });
        }
        if (acl != null) EditorUtility.SetDirty(acl);
        AssetDatabase.SaveAssets();
        var off = new JArray();
        if (cmd["disable"] != null)
            foreach (var d in cmd["disable"]) { var t = Find((string)d); if (t != null) { t.gameObject.SetActive(false); Dirty(t.gameObject); off.Add((string)d); } }
        return new JObject { ["made"] = made, ["disabled"] = off, ["acl"] = acl != null ? PathOf(acl.gameObject) : null, ["srcAclEntries"] = srcEntries != null ? srcEntries.Count : 0 };
    }

    // { op:"meshassign", paths:[...], folder:"Assets/.../X", dir:"C:/.../json dir", name:"Suit_Hanging" }
    // Builds (or overwrites in place) the mesh asset <folder>/<name>.asset from <dir>/<name>.json (meshdump layout) and puts
    // it on the MeshFilter and MeshCollider of every listed object.
    static JToken MeshAssign(JObject cmd)
    {
        var folder = (string)cmd["folder"]; var name = (string)cmd["name"];
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace("\\", "/"), Path.GetFileName(folder));
        var j = JObject.Parse(File.ReadAllText(Path.Combine((string)cmd["dir"], name + ".json")));
        Func<string, float[]> F = k => j[k] == null ? new float[0] : j[k].Select(t => (float)t).ToArray();
        var p = F("pos"); var nr = F("nrm"); var a0 = F("uv0"); var a1 = F("uv1"); var tri = j["tri"].Select(t => (int)t).ToArray();
        int n = p.Length / 3;
        if (n == 0 || n > 65535) throw new Exception(name + ": vertex count " + n + " not usable");
        var assetPath = folder + "/" + name + ".asset";
        var m = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        bool isNew = m == null;
        if (isNew) m = new Mesh(); else m.Clear();
        m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt16;
        m.vertices = Enumerable.Range(0, n).Select(i => new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2])).ToArray();
        if (nr.Length == n * 3) m.normals = Enumerable.Range(0, n).Select(i => new Vector3(nr[i * 3], nr[i * 3 + 1], nr[i * 3 + 2])).ToArray();
        if (a0.Length == n * 2) m.SetUVs(0, Enumerable.Range(0, n).Select(i => new Vector2(a0[i * 2], a0[i * 2 + 1])).ToList());
        if (a1.Length == n * 2) m.SetUVs(1, Enumerable.Range(0, n).Select(i => new Vector2(a1[i * 2], a1[i * 2 + 1])).ToList());
        var subs = j["subs"] != null ? j["subs"].Select(t => (int)t).ToArray() : new[] { tri.Length / 3 };
        m.subMeshCount = subs.Length;
        for (int si = 0, off = 0; si < subs.Length; off += subs[si] * 3, si++) m.SetTriangles(tri.Skip(off).Take(subs[si] * 3).ToArray(), si, true);
        Material[] newMats = cmd["materials"] != null ? cmd["materials"].Select(t => AssetDatabase.LoadAssetAtPath<Material>((string)t)).ToArray() : null;
        if (newMats != null && newMats.Any(x => x == null)) throw new Exception("material not found in " + cmd["materials"]);
        if (a0.Length == n * 2) m.RecalculateTangents();
        m.RecalculateBounds();
        m.name = name;
        if (isNew) AssetDatabase.CreateAsset(m, assetPath); else EditorUtility.SetDirty(m);
        var done = new JArray();
        if (cmd["paths"] != null)
            foreach (var q in cmd["paths"])
            {
                var t = Find((string)q);
                if (t == null) throw new Exception("Not found: " + q);
                var mf = t.GetComponent<MeshFilter>(); if (mf != null) { mf.sharedMesh = m; EditorUtility.SetDirty(mf); }
                var mc = t.GetComponent<MeshCollider>(); if (mc != null) { mc.sharedMesh = m; EditorUtility.SetDirty(mc); }
                Dirty(t.gameObject);
                var mr = t.GetComponent<MeshRenderer>();
                if (mr != null && newMats != null) { mr.sharedMaterials = newMats; EditorUtility.SetDirty(mr); }
                done.Add(new JObject { ["path"] = (string)q, ["min"] = mr != null ? V(mr.bounds.min) : null, ["max"] = mr != null ? V(mr.bounds.max) : null,
                    ["components"] = new JArray(t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name)) });
            }
        AssetDatabase.SaveAssets();
        return new JObject { ["asset"] = assetPath, ["verts"] = m.vertexCount, ["size"] = V(m.bounds.size), ["assigned"] = done };
    }

    // { op:"worlddump", paths:[...], dir:"C:/.../out" } — writes <dir>/<object name>.json for each object: its render-mesh
    // vertices in WORLD space (float32, exactly what the jointing code sees) and triangle indices of all submeshes.
    static JToken WorldDump(JObject cmd)
    {
        var dir = (string)cmd["dir"]; Directory.CreateDirectory(dir);
        var done = new JArray();
        foreach (var q in cmd["paths"])
        {
            var t = Find((string)q);
            if (t == null) { done.Add(new JObject { ["path"] = (string)q, ["error"] = "not found" }); continue; }
            var mf = t.GetComponent<MeshFilter>();
            if ((mf == null || mf.sharedMesh == null) && ((bool?)cmd["preview"] ?? false))
            {
                // addressable part: dump every mesh of its editor preview (children, incl. FakePrefabDisplay) as <name>__<k>.json
                int pk = 0;
                foreach (var cf in t.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (cf.sharedMesh == null) continue;
                    var cm = cf.transform.localToWorldMatrix; var cv = cf.sharedMesh.vertices; var cw = new float[cv.Length * 3];
                    for (int i = 0; i < cv.Length; i++) { var p = cm.MultiplyPoint3x4(cv[i]); cw[i * 3] = p.x; cw[i * 3 + 1] = p.y; cw[i * 3 + 2] = p.z; }
                    var co = new JObject { ["name"] = t.name + "__" + pk, ["child"] = cf.name, ["mesh"] = cf.sharedMesh.name, ["pos"] = new JArray(cw.Select(f => (double)f)), ["tri"] = new JArray(cf.sharedMesh.triangles),
                        ["hasCollider"] = cf.GetComponent<Collider>() != null, ["rendered"] = cf.GetComponent<MeshRenderer>() != null && cf.GetComponent<MeshRenderer>().enabled, ["activeSelf"] = cf.gameObject.activeSelf };
                    File.WriteAllText(Path.Combine(dir, t.name + "__" + pk + ".json"), co.ToString(Newtonsoft.Json.Formatting.None));
                    done.Add(new JObject { ["name"] = t.name + "__" + pk, ["child"] = cf.name, ["mesh"] = cf.sharedMesh.name, ["verts"] = cv.Length, ["tris"] = cf.sharedMesh.triangles.Length / 3 }); pk++;
                }
                if (pk == 0) done.Add(new JObject { ["path"] = (string)q, ["error"] = "no preview mesh" });
                continue;
            }
            if (mf == null || mf.sharedMesh == null) { done.Add(new JObject { ["path"] = (string)q, ["error"] = "no mesh" }); continue; }
            var m = t.localToWorldMatrix; var v = mf.sharedMesh.vertices;
            var w = new float[v.Length * 3];
            for (int i = 0; i < v.Length; i++) { var p = m.MultiplyPoint3x4(v[i]); w[i * 3] = p.x; w[i * 3 + 1] = p.y; w[i * 3 + 2] = p.z; }
            var o = new JObject { ["name"] = t.name, ["path"] = (string)q, ["pos"] = new JArray(w.Select(f => (double)f)), ["tri"] = new JArray(mf.sharedMesh.triangles),
                ["localPos"] = V(t.localPosition), ["worldPos"] = V(t.position), ["lossyScale"] = V(t.lossyScale) };
            if ((bool?)cmd["full"] ?? false)
            {
                var sm = mf.sharedMesh; var uvl = new List<Vector2>(); sm.GetUVs(0, uvl);
                o["localVerts"] = new JArray(sm.vertices.SelectMany(q => new[] { (double)q.x, q.y, q.z }));
                o["nrm"] = new JArray(sm.normals.SelectMany(q => new[] { (double)q.x, q.y, q.z }));
                o["uv0"] = new JArray(uvl.SelectMany(q => new[] { (double)q.x, q.y }));
                o["euler"] = V(t.eulerAngles); o["subs"] = new JArray(Enumerable.Range(0, sm.subMeshCount).Select(k => (int)sm.GetIndexCount(k) / 3));
                var mr0 = t.GetComponent<MeshRenderer>(); if (mr0 != null) o["materials"] = new JArray(mr0.sharedMaterials.Select(x => x != null ? AssetDatabase.GetAssetPath(x) : null));
            }
            File.WriteAllText(Path.Combine(dir, t.name + ".json"), o.ToString(Newtonsoft.Json.Formatting.None));
            done.Add(new JObject { ["name"] = t.name, ["verts"] = v.Length, ["tris"] = mf.sharedMesh.triangles.Length / 3, ["active"] = t.gameObject.activeInHierarchy });
        }
        return done;
    }

    // { op:"jointpads", model:"Assets/.../PDC_Parts.fbx", folder:"Assets/.../JointPads",
    //   pads:[{ a:path, b:path, center:[world], normal:[world], tangent:[world], w:0.16, h:0.16, nu:8, nv:8 }] }
    // The game joints two parts where it finds facing coplanar triangles, and with detailed meshes it can lock onto the
    // wrong pair. A joint pad is a small flat grid of triangles added to BOTH meshes at the same world position (facing
    // +normal in a, -normal in b), hidden inside solid material, appended LAST in the index buffer so the search ends on it.
    // The new mesh of each object = its mesh from `model` (by name) + all its pads, saved as <folder>/<mesh name>.asset.
    static JToken JointPads(JObject cmd)
    {
        var folder = (string)cmd["folder"]; var model = (string)cmd["model"];
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace("\\", "/"), Path.GetFileName(folder));
        var modelMeshes = AssetDatabase.LoadAllAssetsAtPath(model).OfType<Mesh>().ToList();
        var per = new Dictionary<Transform, List<KeyValuePair<JObject, float>>>();
        foreach (JObject pd in cmd["pads"])
            foreach (var side in new[] { "a", "b" })
            {
                if (pd[side] == null) continue;
                var t = Find((string)pd[side]);
                if (t == null) throw new Exception("Not found: " + pd[side]);
                if (!per.ContainsKey(t)) per[t] = new List<KeyValuePair<JObject, float>>();
                per[t].Add(new KeyValuePair<JObject, float>(pd, side == "a" ? 1f : -1f));
            }
        var res = new JArray();
        foreach (var kv in per)
        {
            var t = kv.Key; var mf = t.GetComponent<MeshFilter>();
            var src = modelMeshes.FirstOrDefault(x => x.name == mf.sharedMesh.name);
            bool own = src == null;                       // not a model mesh (a generated .asset): add the pads to it in place
            if (own) src = mf.sharedMesh;
            int n0 = src.vertexCount;
            var pos = new List<Vector3>(src.vertices); var nrm = new List<Vector3>(src.normals); var tan = new List<Vector4>(src.tangents); var col = new List<Color32>(src.colors32);
            bool hasN = nrm.Count == n0, hasT = tan.Count == n0, hasC = col.Count == n0;
            var uvs = new List<Vector4>[4]; var dims = new int[4];
            for (int ch = 0; ch < 4; ch++)
            {
                uvs[ch] = new List<Vector4>(); src.GetUVs(ch, uvs[ch]);
                dims[ch] = uvs[ch].Count == n0 ? src.GetVertexAttributeDimension((UnityEngine.Rendering.VertexAttribute)((int)UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch)) : 0;
            }
            var subs = new List<List<int>>();
            for (int s = 0; s < src.subMeshCount; s++) subs.Add(new List<int>(src.GetTriangles(s)));
            int padTris = 0;
            foreach (var pk in kv.Value)
            {
                var pd = pk.Key; float sign = pk.Value;
                Vector3 c = ToV(pd["center"], Vector3.zero), n = ToV(pd["normal"], Vector3.up).normalized, tu = ToV(pd["tangent"], Vector3.right);
                tu = (tu - n * Vector3.Dot(tu, n)).normalized; var tv = Vector3.Cross(n, tu).normalized;
                float w = (float?)pd["w"] ?? 0.16f, h = (float?)pd["h"] ?? 0.16f; int nu = (int?)pd["nu"] ?? 8, nv = (int?)pd["nv"] ?? 8;
                int b0 = pos.Count; var ln = t.InverseTransformDirection(n * sign).normalized;
                for (int j = 0; j <= nv; j++)
                    for (int i = 0; i <= nu; i++)
                    {
                        var wp = c + tu * (w * ((float)i / nu - 0.5f)) + tv * (h * ((float)j / nv - 0.5f));
                        pos.Add(t.InverseTransformPoint(wp));
                        if (hasN) nrm.Add(ln);
                        if (hasT) tan.Add(new Vector4(1, 0, 0, 1));
                        if (hasC) col.Add(col[0]);
                        for (int ch = 0; ch < 4; ch++) if (dims[ch] > 0) uvs[ch].Add(uvs[ch][0]);
                    }
                var last = subs[subs.Count - 1];
                for (int j = 0; j < nv; j++)
                    for (int i = 0; i < nu; i++)
                    {
                        int v00 = b0 + j * (nu + 1) + i, v10 = v00 + 1, v01 = v00 + nu + 1, v11 = v01 + 1;
                        foreach (var tri in new[] { new[] { v00, v10, v11 }, new[] { v00, v11, v01 } })
                        {
                            var g = Vector3.Cross(pos[tri[1]] - pos[tri[0]], pos[tri[2]] - pos[tri[0]]);
                            if (Vector3.Dot(g, ln) < 0) { int x = tri[1]; tri[1] = tri[2]; tri[2] = x; }
                            last.AddRange(tri); padTris++;
                        }
                    }
            }
            var assetPath = own && AssetDatabase.GetAssetPath(src).EndsWith(".asset") ? AssetDatabase.GetAssetPath(src) : folder + "/" + src.name + ".asset";
            var srcName = src.name;
            var m = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
            bool isNew = m == null;
            if (isNew) m = new Mesh(); else m.Clear();
            m.indexFormat = pos.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(pos);
            if (hasN) m.SetNormals(nrm);
            if (hasT) m.SetTangents(tan);
            if (hasC) m.SetColors(col);
            for (int ch = 0; ch < 4; ch++)
            {
                if (dims[ch] == 0) continue;
                if (dims[ch] == 2) m.SetUVs(ch, uvs[ch].Select(u => (Vector2)u).ToList());
                else if (dims[ch] == 3) m.SetUVs(ch, uvs[ch].Select(u => (Vector3)u).ToList());
                else m.SetUVs(ch, uvs[ch]);
            }
            m.subMeshCount = subs.Count;
            for (int s = 0; s < subs.Count; s++) m.SetTriangles(subs[s], s, false);
            m.RecalculateBounds();
            m.name = srcName;
            if (isNew) AssetDatabase.CreateAsset(m, assetPath); else EditorUtility.SetDirty(m);
            mf.sharedMesh = m; EditorUtility.SetDirty(mf);
            var mc = t.GetComponent<MeshCollider>(); if (mc != null) { mc.sharedMesh = m; EditorUtility.SetDirty(mc); }
            Dirty(t.gameObject);
            res.Add(new JObject { ["object"] = t.name, ["asset"] = assetPath, ["verts"] = m.vertexCount, ["was"] = n0, ["padTris"] = padTris, ["subMeshes"] = subs.Count });
        }
        AssetDatabase.SaveAssets();
        return res;
    }

    // { op:"setsoloader", paths:[...], sp:"<StructurePartAsset guid>", bp:"<EntityBlueprintAsset guid>" }
    // Re-points the AddressableSOLoader of each object (the old per-object loader the corpse parts use) at another
    // StructurePartAsset / EntityBlueprintAsset. Omit sp/bp to just report.
    static JToken SetSoLoader(JObject cmd)
    {
        var res = new JArray();
        foreach (var q in cmd["paths"])
        {
            var t = Find((string)q);
            if (t == null) throw new Exception("Not found: " + q);
            var l = t.GetComponent<BBI.Unity.Game.AddressableSOLoader>();
            if (l == null) { res.Add(new JObject { ["path"] = (string)q, ["error"] = "no AddressableSOLoader" }); continue; }
            for (int i = 0; i < l.field.Count && i < l.refs.Count; i++)
            {
                if (l.field[i] == "m_StructurePartAsset" && cmd["sp"] != null) l.refs[i] = (string)cmd["sp"];
                if (l.field[i] == "m_BlueprintAsset" && cmd["bp"] != null) l.refs[i] = (string)cmd["bp"];
            }
            EditorUtility.SetDirty(l); Dirty(t.gameObject);
            res.Add(new JObject { ["path"] = (string)q, ["field"] = new JArray(l.field), ["refs"] = new JArray(l.refs) });
        }
        return res;
    }

    // { op:"colormat", baseMaterial:"Assets/.../x.mat", folder, name, color:[r,g,b,a], metallic, smoothness, emission:[r,g,b] }
    // A copy of the base material with every texture removed and a flat colour (created once, updated after).
    static JToken ColorMat(JObject cmd)
    {
        var folder = (string)cmd["folder"]; var name = (string)cmd["name"];
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(Path.GetDirectoryName(folder).Replace("\\", "/"), Path.GetFileName(folder));
        var path = folder + "/" + name + ".mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            if (!AssetDatabase.CopyAsset((string)cmd["baseMaterial"], path)) throw new Exception("could not copy " + cmd["baseMaterial"]);
            m = AssetDatabase.LoadAssetAtPath<Material>(path);
        }
        foreach (var tp in m.GetTexturePropertyNames()) m.SetTexture(tp, null);
        foreach (var kw in new[] { "_NORMALMAP", "_NORMALMAP_TANGENT_SPACE", "_MASKMAP", "_EMISSIVE_COLOR_MAP", "_DETAIL_MAP", "_HEIGHTMAP" }) m.DisableKeyword(kw);
        var c = cmd["color"]; var col = new Color((float)c[0], (float)c[1], (float)c[2], c.Count() > 3 ? (float)c[3] : 1f);
        foreach (var pn in new[] { "_BaseColor", "_Color" }) if (m.HasProperty(pn)) m.SetColor(pn, col);
        if (cmd["metallic"] != null && m.HasProperty("_Metallic")) m.SetFloat("_Metallic", (float)cmd["metallic"]);
        if (cmd["smoothness"] != null) foreach (var pn in new[] { "_Smoothness", "_Glossiness" }) if (m.HasProperty(pn)) m.SetFloat(pn, (float)cmd["smoothness"]);
        var e = cmd["emission"]; var ec = e != null ? new Color((float)e[0], (float)e[1], (float)e[2], 1f) : Color.black;
        foreach (var pn in new[] { "_EmissiveColor", "_EmissiveColorLDR", "_EmissionColor" }) if (m.HasProperty(pn)) m.SetColor(pn, ec);
        if (m.HasProperty("_EmissiveIntensity")) m.SetFloat("_EmissiveIntensity", e != null ? 1f : 0f);
        EditorUtility.SetDirty(m); AssetDatabase.SaveAssets();
        return new JObject { ["path"] = path, ["shader"] = m.shader != null ? m.shader.name : null };
    }

    // { op:"prefabdelete", asset:"Assets/.../Fore_Section.prefab", names:["PDC_tubes_2", ...], dry:true }
    // Removes child objects from a prefab ASSET (needed for children of a nested prefab, which cannot be deleted on the
    // instance). Their entries on any AddressableComponentLoader inside that prefab are removed first. With dry:true it
    // only reports what it would remove.
    static JToken PrefabDelete(JObject cmd)
    {
        var asset = (string)cmd["asset"]; bool dry = (bool?)cmd["dry"] ?? false;
        var names = new HashSet<string>(cmd["names"].Select(t => (string)t));
        var root = PrefabUtility.LoadPrefabContents(asset);
        var res = new JArray();
        try
        {
            var targets = root.GetComponentsInChildren<Transform>(true).Where(t => t != root.transform && names.Contains(t.name)).ToList();
            var comps = new HashSet<Component>(targets.SelectMany(t => t.GetComponentsInChildren<Component>(true)));
            int aclRemoved = 0;
            foreach (var acl in root.GetComponentsInChildren<BBI.Unity.Game.AddressableComponentLoader>(true))
            {
                int n = acl.componentValues.Count(cv => cv != null && cv.component != null && comps.Contains(cv.component));
                aclRemoved += n;
                if (!dry && n > 0) acl.componentValues.RemoveAll(cv => cv != null && cv.component != null && comps.Contains(cv.component));
            }
            foreach (var t in targets)
            {
                var mf = t.GetComponent<MeshFilter>();
                res.Add(new JObject { ["name"] = t.name, ["active"] = t.gameObject.activeSelf, ["children"] = t.childCount,
                    ["verts"] = mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0 });
            }
            if (!dry)
            {
                foreach (var t in targets) UnityEngine.Object.DestroyImmediate(t.gameObject);
                PrefabUtility.SaveAsPrefabAsset(root, asset);
            }
            return new JObject { ["asset"] = asset, ["dry"] = dry, ["objects"] = res, ["aclEntriesRemoved"] = aclRemoved,
                ["notFound"] = new JArray(names.Where(n => !targets.Any(t => t.name == n))) };
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    // { op:"aclrepair", path:"Tachi", dry:true, addresses:{ "StructurePart":"<guid>", "EntityBlueprintComponent":"<guid>" } }
    // Finds AddressableComponentLoader entries that have a component but a blank field or address (the game fails the
    // whole ship load on a blank address), and components listed twice for one field with different addresses.
    // alignDupes:true gives the earlier of two conflicting entries the later one's address.
    // Repair: the field is set from the component's type; a blank address is filled from `addresses` by type name.
    static JToken AclRepair(JObject cmd)
    {
        var t = Find((string)cmd["path"] ?? "Tachi"); bool dry = (bool?)cmd["dry"] ?? true;
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        var addr = cmd["addresses"] as JObject;
        var blank = new JArray(); var dupes = new JArray(); int fixedN = 0;
        foreach (var acl in t.GetComponentsInChildren<BBI.Unity.Game.AddressableComponentLoader>(true))
        {
            bool changed = false;
            var seen = new Dictionary<string, int>(); bool align = (bool?)cmd["alignDupes"] ?? false;
            for (int i = 0; i < acl.componentValues.Count; i++)
            {
                var cv = acl.componentValues[i];
                if (cv == null || cv.component == null) continue;
                string type = cv.component.GetType().Name;
                if (string.IsNullOrEmpty(cv.field) || string.IsNullOrEmpty(cv.address))
                {
                    var row = new JObject { ["acl"] = PathOf(acl.gameObject), ["index"] = i, ["object"] = PathOf(cv.component.gameObject), ["type"] = type,
                        ["field"] = cv.field ?? "", ["address"] = cv.address ?? "" };
                    string f = type == "StructurePart" ? "m_StructurePartAsset" : type == "EntityBlueprintComponent" ? "m_BlueprintAsset" : null;
                    string a = string.IsNullOrEmpty(cv.address) ? (addr != null ? (string)addr[type] : null) : cv.address;
                    row["newField"] = f; row["newAddress"] = a;
                    if (!dry && f != null && !string.IsNullOrEmpty(a)) { cv.field = f; cv.address = a; changed = true; fixedN++; row["fixed"] = true; }
                    blank.Add(row);
                }
                string key = cv.component.GetInstanceID() + "|" + cv.field;
                if (seen.TryGetValue(key, out var pi)) { var prev = acl.componentValues[pi]; if (prev.address != cv.address) { dupes.Add(new JObject { ["acl"] = PathOf(acl.gameObject), ["index"] = i, ["object"] = PathOf(cv.component.gameObject), ["type"] = type, ["field"] = cv.field, ["address"] = cv.address, ["other"] = prev.address, ["otherIndex"] = pi }); if (!dry && align) { prev.address = cv.address; changed = true; fixedN++; } } }
                else seen[key] = i;
            }
            if (changed) { EditorUtility.SetDirty(acl); PrefabUtility.RecordPrefabInstancePropertyModifications(acl); Dirty(acl.gameObject); }
        }
        return new JObject { ["dry"] = dry, ["blank"] = blank, ["dupes"] = dupes, ["fixed"] = fixedN };
    }

    // { op:"stripcomponents", path:"Tachi/.../Floating Corpse", types:["AddressableSOLoader","StructurePart","EntityBlueprintComponent"], dry:true }
    // Removes the named component types from ONE object (not its children), e.g. to turn a mesh-less part root into a
    // plain container. Entries for those components on any AddressableComponentLoader above it are removed first.
    static JToken StripComponents(JObject cmd)
    {
        var t = Find((string)cmd["path"]); bool dry = (bool?)cmd["dry"] ?? false;
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        var types = cmd["types"].Select(x => (string)x).ToList();
        var comps = types.SelectMany(n => t.GetComponents<Component>().Where(c => c != null && c.GetType().Name == n)).ToList();
        int acl = 0;
        foreach (var l in t.GetComponentsInParent<BBI.Unity.Game.AddressableComponentLoader>(true))
        {
            int n = l.componentValues.Count(cv => cv != null && cv.component != null && comps.Contains(cv.component));
            acl += n;
            if (!dry && n > 0) { l.componentValues.RemoveAll(cv => cv != null && cv.component != null && comps.Contains(cv.component)); EditorUtility.SetDirty(l); Dirty(l.gameObject); }
        }
        var removed = new JArray(comps.Select(c => c.GetType().Name));
        if (!dry)
        {
            // Two passes: a component another one requires cannot go until the dependant is gone.
            for (int pass = 0; pass < 3; pass++)
                foreach (var c in comps.Where(c => c != null).ToList())
                    try { UnityEngine.Object.DestroyImmediate(c, true); } catch { }
            Dirty(t.gameObject);
        }
        return new JObject { ["path"] = PathOf(t.gameObject), ["dry"] = dry, ["removed"] = removed, ["aclEntriesRemoved"] = acl,
            ["left"] = new JArray(t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name)) };
    }

    // { op:"addmjc", path:"Tachi/.../Group" } - adds a MandatoryJointContainer to the object: every StructurePart below it
    // is joined to the others regardless of joint type (needed for copied pieces of one prop, which share JOINT_Prop
    // and so cannot joint to each other).
    // { op:"mjcgroups", prefab:"Assets/.../Tachi.prefab", unpack:["FwdSectionExt/Fore_Section"], under:"FwdSectionExt/Fore_Section",
    //   groups:{ "<group name>": ["FwdSectionExt/Fore_Section/Part", "FwdSectionExt/TubeFrames/Part#1", ...] },
    //   removeMjc:["FwdSectionExt"], dry:true }
    // Edits the prefab ASSET (paths relative to its root): unpacks the listed nested prefab instances one level, creates one
    // empty GameObject per group under `under` with a MandatoryJointContainer, moves the parts into it keeping their world
    // transforms, and removes the MJC from the listed objects. All paths are resolved before anything is changed.
    static JToken MjcGroups(JObject cmd)
    {
        var ap = (string)cmd["prefab"]; bool dry = (bool?)cmd["dry"] ?? false;
        var root = PrefabUtility.LoadPrefabContents(ap); var res = new JObject(); var log = new JArray();
        try
        {
            Func<string, Transform> rel = pth =>
            {
                var t = root.transform;
                foreach (var seg in pth.Split('/'))
                {
                    string want = seg; int skip = 0; int hash = want.LastIndexOf('#');
                    if (hash > 0 && int.TryParse(want.Substring(hash + 1), out int idx)) { want = want.Substring(0, hash); skip = idx; }
                    Transform next = null; foreach (Transform c in t) if (c.name == want && skip-- == 0) { next = c; break; }
                    if (next == null) return null; t = next;
                }
                return t;
            };
            foreach (var u in cmd["unpack"] ?? new JArray())
            {
                var t = rel((string)u); if (t == null) throw new Exception("unpack: not found " + u);
                bool isRoot = PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject);
                if (isRoot && !dry) PrefabUtility.UnpackPrefabInstance(t.gameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                log.Add((string)u + (isRoot ? (dry ? ": would unpack" : ": unpacked") : ": not a prefab instance root"));
            }
            var under = rel((string)cmd["under"]); if (under == null) throw new Exception("under: not found " + cmd["under"]);
            var plan = new List<KeyValuePair<string, List<Transform>>>(); var missing = new JArray(); var seen = new HashSet<Transform>();
            foreach (var g in (JObject)cmd["groups"])
            {
                var lst = new List<Transform>();
                foreach (var p in g.Value)
                {
                    var t = rel((string)p);
                    if (t == null) { missing.Add((string)p); continue; }
                    if (!seen.Add(t)) throw new Exception("part listed twice: " + p);
                    lst.Add(t);
                }
                plan.Add(new KeyValuePair<string, List<Transform>>(g.Key, lst));
            }
            if (missing.Count > 0) { res["missing"] = missing; throw new Exception("parts not found: " + missing.Count + " (first: " + missing[0] + ")"); }
            var made = new JArray();
            foreach (var kv in plan)
            {
                made.Add(new JObject { ["group"] = kv.Key, ["parts"] = kv.Value.Count });
                if (dry) continue;
                var go = new GameObject("MJC " + kv.Key); go.transform.SetParent(under, false);
                go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
                go.AddComponent<BBI.Unity.Game.MandatoryJointContainer>();
                foreach (var t in kv.Value) t.SetParent(go.transform, true);
            }
            foreach (var r in cmd["removeMjc"] ?? new JArray())
            {
                var t = rel((string)r); if (t == null) throw new Exception("removeMjc: not found " + r);
                var c = t.GetComponent<BBI.Unity.Game.MandatoryJointContainer>(); bool had = c != null;
                if (had && !dry) UnityEngine.Object.DestroyImmediate(c, true);
                log.Add((string)r + (had ? (dry ? ": would remove MJC" : ": MJC removed") : ": had no MJC"));
            }
            if (!dry) PrefabUtility.SaveAsPrefabAsset(root, ap);
            res["log"] = log; res["groups"] = made; res["saved"] = !dry;
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        return res;
    }

    // { op:"mjcregroup", prefab:"Assets/.../Tachi.prefab", search:"FwdSectionExt", under:"FwdSectionExt/Fore_Section",
    //   assign:{ "<part name>": "<group>" | "" }, dry:true }
    // Edits the prefab ASSET: finds each part by name under `search` (names must be unique there), moves it into the
    // "MJC <group>" parent under `under` (created with a MandatoryJointContainer if missing), or straight under `under`
    // for "" (no MJC). World transforms are kept. "MJC *" parents left empty are deleted.
    static JToken MjcRegroup(JObject cmd)
    {
        var ap = (string)cmd["prefab"]; bool dry = (bool?)cmd["dry"] ?? false;
        var root = PrefabUtility.LoadPrefabContents(ap); var res = new JObject();
        try
        {
            Func<string, Transform> rel = pth => { var t = root.transform; foreach (var seg in pth.Split('/')) { Transform n = null; foreach (Transform c in t) if (c.name == seg) { n = c; break; } if (n == null) return null; t = n; } return t; };
            var unpacked = new JArray();
            foreach (var u in cmd["unpack"] ?? new JArray())                 // nested prefab instances to unpack one level first
            {
                var ut = rel((string)u); if (ut == null) throw new Exception("unpack: not found " + u);
                bool isRoot = PrefabUtility.IsAnyPrefabInstanceRoot(ut.gameObject);
                if (isRoot && !dry) PrefabUtility.UnpackPrefabInstance(ut.gameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
                unpacked.Add((string)u + (isRoot ? (dry ? ": would unpack" : ": unpacked") : ": not a prefab instance root"));
            }
            res["unpacked"] = unpacked;
            var search = rel((string)cmd["search"]); var under = rel((string)cmd["under"]);
            if (search == null || under == null) throw new Exception("search/under not found");
            var byName = new Dictionary<string, List<Transform>>();
            foreach (var sp in search.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true))
            { if (!byName.TryGetValue(sp.name, out var l)) byName[sp.name] = l = new List<Transform>(); if (!l.Contains(sp.transform)) l.Add(sp.transform); }
            var moves = new List<KeyValuePair<Transform, string>>(); var bad = new JArray();
            foreach (var kv in (JObject)cmd["assign"])
            {
                if (!byName.TryGetValue(kv.Key, out var l)) { bad.Add("missing: " + kv.Key); continue; }
                if (l.Count > 1) { bad.Add("not unique: " + kv.Key); continue; }
                moves.Add(new KeyValuePair<Transform, string>(l[0], (string)kv.Value));
            }
            if (bad.Count > 0) { res["errors"] = bad; throw new Exception(bad.Count + " parts not resolved (first: " + bad[0] + ")"); }
            var changed = new JArray(); var created = new JArray();
            foreach (var kv in moves)
            {
                Transform dest = under;
                if (!string.IsNullOrEmpty(kv.Value))
                {
                    dest = null; foreach (Transform c in under) if (c.name == "MJC " + kv.Value) { dest = c; break; }
                    if (dest == null)
                    {
                        created.Add(kv.Value);
                        if (dry) { changed.Add(kv.Key.name + " -> MJC " + kv.Value + " (new)"); continue; }
                        var go = new GameObject("MJC " + kv.Value); go.transform.SetParent(under, false); go.AddComponent<BBI.Unity.Game.MandatoryJointContainer>(); dest = go.transform;
                    }
                }
                if (kv.Key.parent == dest) continue;
                changed.Add(kv.Key.name + ": " + kv.Key.parent.name + " -> " + dest.name);
                if (!dry) kv.Key.SetParent(dest, true);
            }
            var removed = new JArray();
            foreach (Transform c in under.Cast<Transform>().ToList())
                if (c.name.StartsWith("MJC ") && c.childCount == 0) { removed.Add(c.name); if (!dry) UnityEngine.Object.DestroyImmediate(c.gameObject); }
            if (!dry) PrefabUtility.SaveAsPrefabAsset(root, ap);
            res["moved"] = changed; res["created"] = created; res["removedEmpty"] = removed; res["saved"] = !dry;
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        return res;
    }

    // { op:"restructure", prefab:"Assets/.../Tachi.prefab", dry:true,
    //   delete:["A/B", ...], create:[{parent:"", name:"B_Section", index:2}], rename:[{path:"A/B", name:"New"}],
    //   renamePrefix:[{under:"A", from:"Fore_Section.", to:"A_"}], move:[{path:"X", parent:"B_Section", index:0}] }
    // Edits the prefab ASSET. All paths are resolved against the names as they are BEFORE the call; the parent of a move
    // may be a path that exists before the call or the name of an object made by `create` (looked up under the root).
    // Moves keep world transforms. Reports, for every StructurePart: whether the depth-first order (= joint test order),
    // world transform, nearest MandatoryJointContainer and active state are unchanged, and that nested prefab instances
    // are still instances of the same asset. With dry:true everything is done in memory and not saved.
    static JToken Restructure(JObject cmd)
    {
        var ap = (string)cmd["prefab"]; bool dry = (bool?)cmd["dry"] ?? false;
        var root = PrefabUtility.LoadPrefabContents(ap); var res = new JObject();
        try
        {
            Func<string, Transform> rel = pth => { if (string.IsNullOrEmpty(pth)) return root.transform; var t = root.transform; foreach (var seg in pth.Split('/')) { Transform n = null; foreach (Transform c in t) if (c.name == seg) { n = c; break; } if (n == null) return null; t = n; } return t; };
            Func<Transform, string> pathOf = t => { if (t == root.transform) return ""; var s = t.name; for (var p = t.parent; p != null && p != root.transform; p = p.parent) s = p.name + "/" + s; return s; };
            Func<Transform, BBI.Unity.Game.MandatoryJointContainer> mjcOf = t => { for (var p = t; p != null; p = p.parent) { var m = p.GetComponent<BBI.Unity.Game.MandatoryJointContainer>(); if (m != null) return m; } return null; };
            Func<Transform, string> tag = c => c.name + (PrefabUtility.IsAnyPrefabInstanceRoot(c.gameObject) ? " [prefab instance]" : "") + (c.gameObject.activeSelf ? "" : " [off]");
            // ---- before
            var sps = root.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true).Select(s => s.transform).Distinct().ToList();
            var j0 = sps.ToDictionary(t => t, t => mjcOf(t)); var a0 = sps.ToDictionary(t => t, t => t.gameObject.activeInHierarchy);
            var allT = root.GetComponentsInChildren<Transform>(true); var w0 = allT.ToDictionary(t => t, t => t.localToWorldMatrix);
            var inst0 = allT.Where(t => t != root.transform && PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)).ToDictionary(t => t, t => PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject));
            res["before"] = new JObject { ["objects"] = allT.Length, ["structureParts"] = sps.Count, ["mjc"] = root.GetComponentsInChildren<BBI.Unity.Game.MandatoryJointContainer>(true).Length,
                ["nestedPrefabInstances"] = inst0.Count, ["top"] = new JArray(root.transform.Cast<Transform>().Select(tag)) };
            // ---- resolve everything against the old names
            var bad = new JArray();
            var dels = new List<Transform>(); foreach (var d in cmd["delete"] ?? new JArray()) { var t = rel((string)d); if (t == null) bad.Add("delete: not found " + d); else dels.Add(t); }
            var rens = new List<KeyValuePair<Transform, string>>(); foreach (var r in cmd["rename"] ?? new JArray()) { var t = rel((string)r["path"]); if (t == null) bad.Add("rename: not found " + r["path"]); else rens.Add(new KeyValuePair<Transform, string>(t, (string)r["name"])); }
            var pres = new List<Tuple<Transform, string, string>>(); foreach (var r in cmd["renamePrefix"] ?? new JArray()) { var t = rel((string)r["under"]); if (t == null) bad.Add("renamePrefix: not found " + r["under"]); else pres.Add(Tuple.Create(t, (string)r["from"], (string)r["to"])); }
            var movs = new List<Tuple<Transform, string, Transform, int>>(); foreach (var r in cmd["move"] ?? new JArray()) { var t = rel((string)r["path"]); if (t == null) { bad.Add("move: not found " + r["path"]); continue; } movs.Add(Tuple.Create(t, (string)r["parent"], rel((string)r["parent"]), (int?)r["index"] ?? -1)); }
            if (bad.Count > 0) { res["errors"] = bad; return res; }
            // ---- moveLoader:[{from, to}]: hand a loader's entries to another object's loader BEFORE its object is deleted
            // (a loader on an emptied folder can still hold the registrations of parts that were moved out of it)
            var ml = new JArray();
            foreach (var r in cmd["moveLoader"] ?? new JArray())
            {
                var st = rel((string)r["from"]); var dt = rel((string)r["to"]);
                if (st == null || dt == null) { bad.Add("moveLoader: not found " + r["from"] + " / " + r["to"]); continue; }
                var src = st.GetComponent<BBI.Unity.Game.AddressableComponentLoader>(); if (src == null) { ml.Add((string)r["from"] + ": no loader"); continue; }
                var dst = dt.GetComponent<BBI.Unity.Game.AddressableComponentLoader>(); bool isNew = dst == null; if (isNew) dst = dt.gameObject.AddComponent<BBI.Unity.Game.AddressableComponentLoader>();
                int n = src.componentValues.Count; int live = src.componentValues.Count(cv => cv != null && cv.component != null);
                dst.componentValues.AddRange(src.componentValues.Where(cv => cv != null && cv.component != null)); src.componentValues = new List<BBI.Unity.Game.AddressableComponentValue>();
                EditorUtility.SetDirty(dst); ml.Add((string)r["from"] + " -> " + (string)r["to"] + ": " + live + " of " + n + " entries moved" + (isNew ? " (new loader)" : " (appended, now " + dst.componentValues.Count + ")"));
            }
            res["movedLoaders"] = ml;
            if (bad.Count > 0) { res["errors"] = bad; return res; }
            // any loader on an object about to be deleted that still registers a component living elsewhere blocks the delete
            var orphan = new JArray();
            foreach (var d in dels) foreach (var l in d.GetComponentsInChildren<BBI.Unity.Game.AddressableComponentLoader>(true)) foreach (var cv in l.componentValues)
                if (cv != null && cv.component != null && !dels.Any(x => cv.component.transform.IsChildOf(x))) orphan.Add(pathOf(d) + " still registers " + pathOf(cv.component.transform));
            if (orphan.Count > 0) { res["errors"] = new JArray("delete would drop " + orphan.Count + " loader registrations (use moveLoader)"); res["wouldOrphan"] = new JArray(orphan.Take(10)); return res; }
            // ---- delete (leaf objects only; report what they carried and anything that still points at them)
            var delInfo = new JArray(); var delSet = new HashSet<GameObject>(dels.Select(t => t.gameObject)); var refs = new JArray();
            if (dels.Count > 0)
                foreach (var c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || delSet.Contains(c.gameObject) || c is Transform) continue;
                    var so = new SerializedObject(c); var it = so.GetIterator();
                    while (it.Next(true))
                    {
                        if (it.propertyType != SerializedPropertyType.ObjectReference || it.objectReferenceValue == null) continue;
                        var v = it.objectReferenceValue; var g = v as GameObject ?? (v as Component)?.gameObject;
                        if (g != null && delSet.Contains(g)) refs.Add(pathOf(c.transform) + " [" + c.GetType().Name + "." + it.propertyPath + "] -> " + g.name);
                    }
                }
            foreach (var t in dels)
            {
                var o = new JObject { ["path"] = pathOf(t), ["children"] = t.childCount, ["active"] = t.gameObject.activeSelf, ["components"] = new JArray(t.GetComponents<Component>().Where(c => !(c is Transform)).Select(c => c == null ? "(missing script)" : c.GetType().Name)) };
                bool ok = t.childCount == 0 && refs.Count == 0; o["deleted"] = ok; delInfo.Add(o);
                if (ok) UnityEngine.Object.DestroyImmediate(t.gameObject);
            }
            res["delete"] = delInfo; res["referencesToDeleted"] = refs;
            // ---- create
            var made = new Dictionary<string, Transform>(); var createdArr = new JArray();
            foreach (var r in cmd["create"] ?? new JArray())
            {
                var par = rel((string)r["parent"]); if (par == null) { bad.Add("create: parent not found " + r["parent"]); continue; }
                var go = new GameObject((string)r["name"]); go.transform.SetParent(par, false); if (r["index"] != null) go.transform.SetSiblingIndex((int)r["index"]);
                made[(string)r["name"]] = go.transform; createdArr.Add((string)r["name"]);
            }
            res["created"] = createdArr;
            // ---- move (world transform kept)
            var moved = new JArray();
            foreach (var mv in movs)
            {
                Transform mt; var dest = mv.Item3 ?? (made.TryGetValue(mv.Item2, out mt) ? mt : null);
                if (dest == null) { bad.Add("move: parent not found " + mv.Item2); continue; }
                moved.Add(mv.Item1.name + " -> " + dest.name); mv.Item1.SetParent(dest, true); if (mv.Item4 >= 0) mv.Item1.SetSiblingIndex(mv.Item4); else mv.Item1.SetAsLastSibling();
            }
            res["moved"] = moved;
            // ---- setTransform:[{path | made-name, pos:[x,y,z], euler:[x,y,z]}]: local pose of one object (its children follow)
            var posed = new JArray();
            foreach (var r in cmd["setTransform"] ?? new JArray())
            {
                Transform mt2; var tt = rel((string)r["path"]) ?? (made.TryGetValue((string)r["path"], out mt2) ? mt2 : null);
                if (tt == null) { bad.Add("setTransform: not found " + r["path"]); continue; }
                if (r["pos"] != null) tt.localPosition = ToV(r["pos"], Vector3.zero);
                if (r["euler"] != null) tt.localRotation = Quaternion.Euler(ToV(r["euler"], Vector3.zero));
                posed.Add(tt.name + " pos " + tt.localPosition.ToString("F3") + " euler " + tt.localEulerAngles.ToString("F3"));
            }
            res["posed"] = posed;
            // ---- rename
            int np = 0;
            foreach (var pr in pres)
                foreach (var t in pr.Item1.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith(pr.Item2)) { t.name = pr.Item3 + t.name.Substring(pr.Item2.Length); np++; }
            var renamed = new JArray(); foreach (var kv in rens) { renamed.Add(kv.Key.name + " -> " + kv.Value); kv.Key.name = kv.Value; }
            res["renamed"] = renamed; res["renamedByPrefix"] = np;
            if (bad.Count > 0) { res["errors"] = bad; return res; }
            // ---- after: what must not have changed
            var sps1 = root.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true).Select(s => s.transform).Distinct().ToList();
            bool sameOrder = sps1.SequenceEqual(sps); int firstDiff = -1; if (!sameOrder) for (int i = 0; i < Math.Min(sps.Count, sps1.Count); i++) if (sps[i] != sps1[i]) { firstDiff = i; break; }
            float maxMove = 0f; foreach (var kv in w0) { if (kv.Key == null) continue; var a = kv.Value; var b = kv.Key.localToWorldMatrix; for (int i = 0; i < 16; i++) maxMove = Mathf.Max(maxMove, Mathf.Abs(a[i] - b[i])); }
            int mjcChanged = sps.Count(t => t != null && mjcOf(t) != j0[t]); int activeChanged = sps.Count(t => t != null && t.gameObject.activeInHierarchy != a0[t]);
            int instBroken = inst0.Count(kv => kv.Key == null || !PrefabUtility.IsAnyPrefabInstanceRoot(kv.Key.gameObject) || PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(kv.Key.gameObject) != kv.Value);
            var dup = new JArray(); foreach (var t in root.GetComponentsInChildren<Transform>(true)) { var names = new HashSet<string>(); foreach (Transform c in t) if (c.GetComponent<BBI.Unity.Game.StructurePart>() != null && !names.Add(c.name)) dup.Add(pathOf(c)); }
            var after = new JObject { ["objects"] = root.GetComponentsInChildren<Transform>(true).Length, ["structureParts"] = sps1.Count, ["mjc"] = root.GetComponentsInChildren<BBI.Unity.Game.MandatoryJointContainer>(true).Length,
                ["partOrderUnchanged"] = sameOrder, ["firstOrderDifference"] = firstDiff >= 0 ? (pathOf(sps[firstDiff]) + " | now " + pathOf(sps1[firstDiff])) : null,
                ["maxWorldTransformChange"] = maxMove, ["partsWithDifferentMjc"] = mjcChanged, ["partsWithDifferentActiveState"] = activeChanged, ["nestedPrefabInstancesBroken"] = instBroken,
                ["sameNamedSiblingParts"] = dup.Count, ["top"] = new JArray(root.transform.Cast<Transform>().Select(tag)) };
            foreach (var kv in made) after[kv.Key] = new JArray(kv.Value.Cast<Transform>().Select(c => tag(c) + " (" + c.childCount + " children)"));
            res["after"] = after;
            if (!dry) PrefabUtility.SaveAsPrefabAsset(root, ap);
            res["saved"] = !dry;
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        return res;
    }

    // { op:"bayshow", json:"C:/.../show.json" }            - shows the bay recorded in game (PartInfoLogger bay_geometry.*,
    //                                                        prepared by .claude/bay_show_prep.py) as plain meshes under a
    //                                                        scene object "BayRecorded" (DontSave: never written to the scene).
    // { op:"bayshow", json:..., save:"Assets/Editor/BayRecorded" } - also writes it as real assets: one mesh and one material
    //                                                        per group plus BayRecorded.prefab, so it can be brought back later.
    // { op:"bayshow", prefab:"Assets/Editor/BayRecorded/BayRecorded.prefab" } - shows the saved prefab again.
    // { op:"bayshow", clear:true }                          - removes it from the scene.
    static JToken BayShow(JObject cmd)
    {
        var old = GameObject.Find("BayRecorded"); if (old != null) UnityEngine.Object.DestroyImmediate(old);
        if ((bool?)cmd["clear"] ?? false) return new JObject { ["cleared"] = old != null };
        if (cmd["prefab"] != null)
        {
            var pf = AssetDatabase.LoadAssetAtPath<GameObject>((string)cmd["prefab"]); if (pf == null) throw new Exception("Prefab not found: " + cmd["prefab"]);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(pf); inst.name = "BayRecorded";
            foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.DontSave;
            SceneView.RepaintAll();
            return new JObject { ["shown"] = (string)cmd["prefab"], ["children"] = new JArray(inst.transform.Cast<Transform>().Select(c => c.name)) };
        }
        var j = JObject.Parse(File.ReadAllText((string)cmd["json"]));
        string save = cmd["save"] != null ? ((string)cmd["save"]).TrimEnd('/') : null;
        if (save != null) AddressableBaker.EnsureFolder(save);
        var flags = save != null ? HideFlags.None : HideFlags.DontSave;
        var root = new GameObject("BayRecorded"); root.hideFlags = flags;
        var shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Standard");
        var res = new JArray();
        foreach (var g in j["groups"])
        {
            int nv = (int)g["verts"], nt = (int)g["tris"]; var bytes = File.ReadAllBytes((string)g["file"]); string gn = (string)g["name"];
            var fl = new float[nv * 3]; var tr = new int[nt * 3];
            Buffer.BlockCopy(bytes, 0, fl, 0, nv * 12); Buffer.BlockCopy(bytes, nv * 12, tr, 0, nt * 12);
            var vs = new Vector3[nv]; for (int i = 0; i < nv; i++) vs[i] = new Vector3(fl[i * 3], fl[i * 3 + 1], fl[i * 3 + 2]);
            var m = new Mesh { name = gn, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, hideFlags = flags };
            m.vertices = vs; m.triangles = tr; m.RecalculateNormals(); m.RecalculateBounds();
            var c = g["color"]; var col = new Color((float)c[0], (float)c[1], (float)c[2], 1f);
            var mat = new Material(shader) { hideFlags = flags, name = "BayRecorded_" + gn };
            mat.color = col; if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col);
            if (save != null)
            {
                AssetDatabase.DeleteAsset(save + "/" + gn + ".asset"); AssetDatabase.CreateAsset(m, save + "/" + gn + ".asset");
                AssetDatabase.DeleteAsset(save + "/BayRecorded_" + gn + ".mat"); AssetDatabase.CreateAsset(mat, save + "/BayRecorded_" + gn + ".mat");
            }
            var go = new GameObject(gn); go.hideFlags = flags; go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = m; go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            res.Add(new JObject { ["name"] = gn, ["verts"] = nv, ["tris"] = nt, ["center"] = V(m.bounds.center), ["size"] = V(m.bounds.size) });
        }
        var outp = new JObject { ["groups"] = res };
        if (save != null)
        {
            string pp = save + "/BayRecorded.prefab"; PrefabUtility.SaveAsPrefabAsset(root, pp); AssetDatabase.SaveAssets();
            UnityEngine.Object.DestroyImmediate(root);                     // the saved copy is shown as a DontSave instance instead
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(pp)); inst.name = "BayRecorded";
            foreach (var t in inst.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.DontSave;
            outp["prefab"] = pp;
        }
        SceneView.RepaintAll();
        return outp;
    }

    // { op:"reorderkids", prefab:"Assets/.../Tachi.prefab", parents:{ "FwdSectionExt/Fore_Section/MJC x": ["childA","childB",...] }, dry:true }
    // Sets the sibling order of the named children (prefab ASSET). The game tests a joint pair as (A,B) with A = the part
    // earlier in the hierarchy, so order decides seams that only pass one way. Children not listed keep their relative
    // order after the listed ones. Nothing moves in space.
    static JToken ReorderKids(JObject cmd)
    {
        var ap = (string)cmd["prefab"]; bool dry = (bool?)cmd["dry"] ?? false;
        var root = PrefabUtility.LoadPrefabContents(ap); var res = new JArray();
        try
        {
            foreach (var kv in (JObject)cmd["parents"])
            {
                var t = root.transform; bool ok = true;
                foreach (var seg in kv.Key.Split('/')) { Transform n = null; foreach (Transform c in t) if (c.name == seg) { n = c; break; } if (n == null) { ok = false; break; } t = n; }
                if (!ok) throw new Exception("parent not found: " + kv.Key);
                var want = kv.Value.Select(x => (string)x).ToList(); var kids = t.Cast<Transform>().ToList(); var missing = want.Where(n => !kids.Any(k => k.name == n)).ToList();
                if (missing.Count > 0) throw new Exception(kv.Key + ": children not found: " + string.Join(", ", missing.Take(5)));
                int changed = 0;
                for (int i = 0; i < want.Count; i++)
                {
                    var k = kids.First(x => x.name == want[i]);
                    if (k.GetSiblingIndex() != i) { changed++; if (!dry) k.SetSiblingIndex(i); }
                }
                res.Add(new JObject { ["parent"] = kv.Key, ["children"] = kids.Count, ["listed"] = want.Count, ["moved"] = changed });
            }
            if (!dry) PrefabUtility.SaveAsPrefabAsset(root, ap);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        return new JObject { ["parents"] = res, ["saved"] = !dry };
    }

    static JToken AddMjc(JObject cmd)
    {
        var t = Find((string)cmd["path"]);
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        var c = t.GetComponent<BBI.Unity.Game.MandatoryJointContainer>();
        bool added = c == null;
        if (added) { t.gameObject.AddComponent<BBI.Unity.Game.MandatoryJointContainer>(); Dirty(t.gameObject); }
        return new JObject { ["path"] = PathOf(t.gameObject), ["added"] = added, ["parts"] = t.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true).Length };
    }

    // { op:"aclregister", path:"Tachi_Gas/.../Part", from:"Tachi_Gas/.../SiblingPart" }
    // Registers an EXISTING object's components in the loader that holds `from`'s entries, with the same addresses
    // (component by component, same order and type). For objects that were cloned in the base prefab while a variant
    // overrides that loader's list, so the variant never received their entries. Entries already there are left alone.
    static JToken AclRegister(JObject cmd)
    {
        var dst = Find((string)cmd["path"]); if (dst == null) throw new Exception("Not found: " + cmd["path"]);
        var src = Find((string)cmd["from"]); if (src == null) throw new Exception("Not found: " + cmd["from"]);
        var srcAll = src.GetComponents<Component>(); var dstAll = dst.GetComponents<Component>(); int added = 0, had = 0;
        for (var t = src; t != null; t = t.parent)
        {
            var acl = t.GetComponent<BBI.Unity.Game.AddressableComponentLoader>(); if (acl == null || acl.componentValues == null) continue;
            foreach (var cv in acl.componentValues.ToList())
            {
                if (cv == null || cv.component == null) continue;
                int idx = Array.IndexOf(srcAll, cv.component);
                if (idx < 0 || idx >= dstAll.Length || dstAll[idx] == null || dstAll[idx].GetType() != cv.component.GetType()) continue;
                if (acl.componentValues.Any(x => x != null && x.component == dstAll[idx] && x.field == cv.field)) { had++; continue; }
                acl.componentValues.Add(new BBI.Unity.Game.AddressableComponentValue { component = dstAll[idx], field = cv.field, address = cv.address }); added++;
            }
            EditorUtility.SetDirty(acl);
        }
        return new JObject { ["path"] = (string)cmd["path"], ["added"] = added, ["had"] = had };
    }

    // { op:"aclcopy", path:"Tachi/.../Part", from:"Tachi/.../OtherPart", field:"m_StructurePartAsset", dry:true }
    // Gives `path` the same addressable asset as `from` for one loader field (default: the StructurePartAsset, which
    // carries the joint type). Both must be registered in an AddressableComponentLoader. Omit `from` to just report.
    static JToken AclCopy(JObject cmd)
    {
        string field = (string)cmd["field"] ?? "m_StructurePartAsset"; bool dry = (bool?)cmd["dry"] ?? false;
        Func<string, List<KeyValuePair<BBI.Unity.Game.AddressableComponentLoader, int>>> find = pth =>
        {
            var t = Find(pth); if (t == null) throw new Exception("Not found: " + pth);
            var res = new List<KeyValuePair<BBI.Unity.Game.AddressableComponentLoader, int>>();
            foreach (var acl in t.GetComponentsInParent<BBI.Unity.Game.AddressableComponentLoader>(true))
                for (int i = 0; i < acl.componentValues.Count; i++)
                {
                    var cv = acl.componentValues[i];
                    if (cv != null && cv.component != null && cv.component.transform == t && cv.field == field) res.Add(new KeyValuePair<BBI.Unity.Game.AddressableComponentLoader, int>(acl, i));
                }
            return res;
        };
        var dst = find((string)cmd["path"]);
        var o = new JObject { ["path"] = (string)cmd["path"], ["field"] = field, ["entries"] = dst.Count,
            ["was"] = new JArray(dst.Select(kv => kv.Key.componentValues[kv.Value].address)) };
        if (cmd["from"] == null && cmd["address"] == null) return o;
        if (dst.Count == 0) throw new Exception("No loader entry for " + field + " on " + cmd["path"]);
        string addr;
        if (cmd["address"] != null) addr = (string)cmd["address"];   // { ..., address:"<guid>" } sets it directly
        else
        {
            var src = find((string)cmd["from"]);
            if (src.Count == 0) throw new Exception("No loader entry for " + field + " on " + cmd["from"]);
            addr = src[0].Key.componentValues[src[0].Value].address;
        }
        o["now"] = addr;
        if (!dry) foreach (var kv in dst) { kv.Key.componentValues[kv.Value].address = addr; EditorUtility.SetDirty(kv.Key); PrefabUtility.RecordPrefabInstancePropertyModifications(kv.Key); Dirty(kv.Key.gameObject); }
        return o;
    }

    // { op:"aclpurge", path:"Tachi", dry:true } — drops entries whose component no longer exists from every
    // AddressableComponentLoader under `path` (the ship validator treats such entries as errors).
    static JToken AclPurge(JObject cmd)
    {
        bool dry = (bool?)cmd["dry"] ?? false;
        if (cmd["asset"] != null)
        {
            // { op:"aclpurge", asset:"Assets/.../X.prefab", dry:true } — same, inside a prefab ASSET (e.g. a nested prefab
            // whose children were deleted). Run aclrepair afterwards: the outer prefab's index-based overrides can shift.
            var ap = (string)cmd["asset"]; var root = PrefabUtility.LoadPrefabContents(ap); var ra = new JArray();
            try
            {
                foreach (var acl in root.GetComponentsInChildren<BBI.Unity.Game.AddressableComponentLoader>(true))
                {
                    int n = acl.componentValues.Count(cv => cv == null || cv.component == null);
                    ra.Add(new JObject { ["acl"] = acl.gameObject.name, ["entries"] = acl.componentValues.Count, ["nullEntries"] = n });
                    if (n > 0 && !dry) acl.componentValues.RemoveAll(cv => cv == null || cv.component == null);
                }
                if (!dry) PrefabUtility.SaveAsPrefabAsset(root, ap);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            return ra;
        }
        var t = Find((string)cmd["path"] ?? "Tachi");
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        var res = new JArray();
        foreach (var acl in t.GetComponentsInChildren<BBI.Unity.Game.AddressableComponentLoader>(true))
        {
            int n = acl.componentValues.Count(cv => cv == null || cv.component == null);
            if (n == 0) continue;
            if (!dry) { acl.componentValues.RemoveAll(cv => cv == null || cv.component == null); EditorUtility.SetDirty(acl); Dirty(acl.gameObject); }
            res.Add(new JObject { ["acl"] = PathOf(acl.gameObject), ["nullEntries"] = n });
        }
        return res;
    }

    // { op:"bigmeshes", path:"Tachi", min:16000 } — every mesh with at least `min` vertices under the ship, ACTIVE OR NOT:
    // scene MeshFilters and the meshes inside each AddressableLoader's prefab, with whether the object is active in
    // the hierarchy and whether it carries a StructurePart (only active StructureParts count for the jointing limit).
    static JToken BigMeshes(JObject cmd)
    {
        var root = Find((string)cmd["path"] ?? "Tachi");
        if (root == null) throw new Exception("Not found: " + cmd["path"]);
        int min = (int?)cmd["min"] ?? 16000;
        var rows = new JArray();
        var cache = new Dictionary<string, List<KeyValuePair<string, int[]>>>();
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (IsUnderPreview(t) && t.GetComponent<BBI.Unity.Game.AddressableLoader>() == null) continue;
            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount >= min)
            {
                var sp = t.GetComponent<BBI.Unity.Game.StructurePart>();
                rows.Add(new JObject { ["verts"] = mf.sharedMesh.vertexCount, ["mesh"] = mf.sharedMesh.name, ["path"] = PathOf(t.gameObject),
                    ["active"] = t.gameObject.activeInHierarchy, ["structurePart"] = sp != null && sp.enabled, ["kind"] = "scene" });
            }
            var loader = t.GetComponent<BBI.Unity.Game.AddressableLoader>();
            if (loader == null || string.IsNullOrEmpty(loader.assetGUID)) continue;
            var key = loader.assetGUID + "|" + (loader.childPath ?? "");
            List<KeyValuePair<string, int[]>> list;
            if (!cache.TryGetValue(key, out list))
            {
                list = new List<KeyValuePair<string, int[]>>();
                GameObject src = null;
                for (int attempt = 0; attempt < 4 && src == null; attempt++) src = AddressableBaker.LoadAddressableAsync(loader.assetGUID, loader.childPath ?? "").Result;
                if (src != null)
                    foreach (var f in src.GetComponentsInChildren<MeshFilter>(true))
                        if (f.sharedMesh != null && f.sharedMesh.vertexCount >= min)
                        {
                            var sp = f.GetComponent<BBI.Unity.Game.StructurePart>();
                            list.Add(new KeyValuePair<string, int[]>(src.name + "/" + RelPath(f.transform, src.transform) + " [" + f.sharedMesh.name + "]", new[] { f.sharedMesh.vertexCount, sp != null && sp.enabled ? 1 : 0 }));
                        }
                cache[key] = list;
            }
            foreach (var kv in list)
                rows.Add(new JObject { ["verts"] = kv.Value[0], ["mesh"] = kv.Key, ["path"] = PathOf(t.gameObject), ["active"] = t.gameObject.activeInHierarchy && loader.enabled,
                    ["structurePart"] = kv.Value[1] == 1, ["kind"] = "addressable" });
        }
        return new JArray(rows.OrderByDescending(r => (int)r["verts"]));
    }

    // { op:"remesh", model:"Assets/.../PDC_Mounts.fbx", items:[{path, mesh, rename}], dry:true }
    // Points an existing part's MeshFilter + MeshCollider at the mesh called `mesh` in `model` (materials taken from
    // the model's node), and renames the object. Reports old/new vertex counts and world bounds so a swap that is
    // meant to be like-for-like can be checked first with dry:true.
    static JToken Remesh(JObject cmd)
    {
        var model = (string)cmd["model"]; bool dry = (bool?)cmd["dry"] ?? false;
        var meshes = AssetDatabase.LoadAllAssetsAtPath(model).OfType<Mesh>().GroupBy(m => m.name).ToDictionary(g => g.Key, g => g.First());
        var modelRoot = AssetDatabase.LoadAssetAtPath<GameObject>(model);
        var nodes = modelRoot != null ? modelRoot.GetComponentsInChildren<MeshFilter>(true) : new MeshFilter[0];
        var outp = new JArray();
        foreach (var it in cmd["items"])
        {
            var path = (string)it["path"]; var meshName = (string)it["mesh"]; var rename = (string)it["rename"];
            var t = Find(path);
            var o = new JObject { ["path"] = path, ["mesh"] = meshName };
            outp.Add(o);
            if (t == null) { o["error"] = "object not found"; continue; }
            Mesh mesh; if (!meshes.TryGetValue(meshName, out mesh)) { o["error"] = "mesh not in model"; continue; }
            var mf = t.GetComponent<MeshFilter>(); var mc = t.GetComponent<MeshCollider>(); var mr = t.GetComponent<MeshRenderer>();
            if (mf == null) { o["error"] = "no MeshFilter"; continue; }
            var node = nodes.FirstOrDefault(f => f.sharedMesh == mesh);
            o["oldVerts"] = mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0; o["newVerts"] = mesh.vertexCount;
            o["oldMesh"] = mf.sharedMesh != null ? mf.sharedMesh.name : null;
            if (mf.sharedMesh != null) { o["oldLocalMin"] = V(mf.sharedMesh.bounds.min); o["oldLocalMax"] = V(mf.sharedMesh.bounds.max); }
            o["newLocalMin"] = V(mesh.bounds.min); o["newLocalMax"] = V(mesh.bounds.max);
            if (node != null)
            {
                o["poseMatches"] = (node.transform.localPosition - t.localPosition).magnitude < 0.002f && Quaternion.Angle(node.transform.localRotation, t.localRotation) < 0.05f;
                o["nodeMaterials"] = new JArray(node.GetComponent<MeshRenderer>().sharedMaterials.Select(m => m != null ? m.name : "null"));
            }
            if (mr != null) o["oldMaterials"] = new JArray(mr.sharedMaterials.Select(m => m != null ? m.name : "null"));
            o["active"] = t.gameObject.activeInHierarchy;
            if (dry) continue;
            mf.sharedMesh = mesh; if (mc != null) mc.sharedMesh = mesh;
            if (node != null && mr != null) mr.sharedMaterials = node.GetComponent<MeshRenderer>().sharedMaterials;
            if (!string.IsNullOrEmpty(rename)) t.gameObject.name = rename;
            Dirty(t.gameObject);
            o["newPath"] = PathOf(t.gameObject);
        }
        return outp;
    }

    // { op:"reparent", parent:"Tachi/.../Fore_Section", group:"PDC_Fore_Port", paths:[...] }
    // Moves existing objects under an (identity-transform) group object inside `parent`, creating the group if needed.
    // World position / rotation / scale of every moved object stay exactly as they were; nothing else is touched.
    static JToken Reparent(JObject cmd)
    {
        var parent = Find((string)cmd["parent"]);
        if (parent == null) throw new Exception("Parent not found: " + cmd["parent"]);
        var gname = (string)cmd["group"];
        var grp = parent.Find(gname);
        if (grp == null)
        {
            var go = new GameObject(gname);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
            grp = go.transform; Dirty(go);
        }
        var moved = new JArray(); float worst = 0f;
        foreach (var p in cmd["paths"])
        {
            var t = Find((string)p);
            if (t == null) throw new Exception("Not found: " + p);
            var wp = t.position; var wr = t.rotation;
            t.SetParent(grp, true);
            worst = Mathf.Max(worst, (t.position - wp).magnitude, Quaternion.Angle(t.rotation, wr) * 0.01f);
            Dirty(t.gameObject); moved.Add(PathOf(t.gameObject));
        }
        return new JObject { ["group"] = PathOf(grp.gameObject), ["moved"] = moved.Count, ["worstShift"] = worst };
    }

    // { op:"mats", paths:[...], set:{ "<material asset path to replace>": "<material asset path to use>" } }
    // Lists each object's renderer materials with their asset paths; with `set`, swaps materials by asset path.
    static JToken Mats(JObject cmd)
    {
        var outp = new JArray();
        var set = cmd["set"] as JObject;
        foreach (var p in cmd["paths"])
        {
            var t = Find((string)p);
            if (t == null) { outp.Add(new JObject { ["path"] = (string)p, ["error"] = "not found" }); continue; }
            var mr = t.GetComponent<MeshRenderer>();
            if (mr == null) { outp.Add(new JObject { ["path"] = (string)p, ["error"] = "no renderer" }); continue; }
            var ms = mr.sharedMaterials; bool changed = false;
            if (set != null)
                for (int i = 0; i < ms.Length; i++)
                {
                    var ap = ms[i] != null ? AssetDatabase.GetAssetPath(ms[i]) : "";
                    var to = (string)set[ap];
                    if (to == null) continue;
                    var nm = AssetDatabase.LoadAssetAtPath<Material>(to);
                    if (nm != null) { ms[i] = nm; changed = true; }
                }
            if (changed) { mr.sharedMaterials = ms; Dirty(t.gameObject); }
            outp.Add(new JObject { ["path"] = (string)p, ["changed"] = changed,
                ["materials"] = new JArray(ms.Select(m => m != null ? AssetDatabase.GetAssetPath(m) + (AssetDatabase.IsSubAsset(m) ? " [embedded:" + m.name + "]" : "") : "null")) });
        }
        return outp;
    }

    // { op:"setactive", paths:[...], active:false }  — enable / disable scene objects (inactive parts are not jointed).
    static JToken SetActiveOp(JObject cmd)
    {
        bool on = (bool?)cmd["active"] ?? false; var done = new JArray();
        foreach (var p in cmd["paths"])
        {
            var t = Find((string)p);
            if (t == null) throw new Exception("Not found: " + p);
            t.gameObject.SetActive(on); Dirty(t.gameObject); done.Add(PathOf(t.gameObject));
        }
        return done;
    }

    // { op:"partcount", path:"Tachi" }
    // Estimates what the game's jointing step will see: every active StructurePart under the ship, i.e. the ones on
    // scene objects plus the ones inside each active AddressableLoader's prefab (minus its disabledChildren), and the
    // largest mesh vertex count among them. The game crashes on load when parts * largestVerts exceeds 2^27 (NativeList rounds capacity up to a power of two)
    // (JointHelper.JointingWorkerData allocates NativeList<float2>(largestVertexCount * numParts)).
    static JToken PartCount(JObject cmd)
    {
        var root = Find((string)cmd["path"] ?? "Tachi");
        if (root == null) throw new Exception("Not found: " + cmd["path"]);
        int sceneParts = 0, addrParts = 0, loaders = 0, failed = 0;
        var perGuid = new Dictionary<string, int[]>();           // key -> [partsPerInstance, instances]
        var names = new Dictionary<string, string>();
        var meshes = new List<KeyValuePair<int, string>>();
        var byGroup = new Dictionary<string, int>();
        Func<Transform, string> groupOf = t =>
        {
            var chain = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent) chain.Add(c.name);
            chain.Reverse();
            return string.Join("/", chain.Take(3));
        };
        Action<string, int> addGroup = (g, n) => { int v; byGroup.TryGetValue(g, out v); byGroup[g] = v + n; };
        foreach (var t in root.GetComponentsInChildren<Transform>(false))
        {
            if (t.name == "FakePrefabDisplay" || (t.parent != null && t.GetComponentInParent<BBI.Unity.Game.AddressableLoader>() != null
                && t.GetComponent<BBI.Unity.Game.AddressableLoader>() == null && IsUnderPreview(t))) continue;
            var sp = t.GetComponent<BBI.Unity.Game.StructurePart>();
            if (sp != null && sp.enabled)
            {
                sceneParts++; addGroup(groupOf(t), 1);
                var mf = t.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) meshes.Add(new KeyValuePair<int, string>(mf.sharedMesh.vertexCount, PathOf(t.gameObject)));
            }
            var loader = t.GetComponent<BBI.Unity.Game.AddressableLoader>();
            if (loader == null || !loader.enabled || string.IsNullOrEmpty(loader.assetGUID)) continue;
            loaders++;
            var dis = loader.disabledChildren ?? new List<string>();
            var key = loader.assetGUID + "|" + (loader.childPath ?? "") + "|" + string.Join(",", dis.OrderBy(x => x));
            int[] rec;
            if (!perGuid.TryGetValue(key, out rec))
            {
                GameObject src = null;
                for (int attempt = 0; attempt < 4 && src == null; attempt++)
                    src = AddressableBaker.LoadAddressableAsync(loader.assetGUID, loader.childPath ?? "").Result;
                int n = 0;
                if (src == null) { failed++; n = -1; }
                else
                {
                    names[key] = src.name;
                    foreach (var p in src.GetComponentsInChildren<BBI.Unity.Game.StructurePart>(true))
                    {
                        if (!p.enabled || !ActiveInPrefab(p.transform, src.transform)) continue;
                        var rel = RelPath(p.transform, src.transform);
                        if (dis.Any(d => rel == d || rel.StartsWith(d + "/"))) continue;
                        n++;
                        var mf = p.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null) meshes.Add(new KeyValuePair<int, string>(mf.sharedMesh.vertexCount, "[addressable " + src.name + "/" + rel + "] first seen at " + PathOf(t.gameObject)));
                    }
                }
                rec = new[] { n, 0 }; perGuid[key] = rec;
            }
            rec[1]++;
            if (rec[0] > 0) { addrParts += rec[0]; addGroup(groupOf(t), rec[0]); }
        }
        var top = new JArray(perGuid.Where(k => k.Value[0] > 0).OrderByDescending(k => k.Value[0] * k.Value[1]).Take(25)
            .Select(k => new JObject { ["name"] = names.ContainsKey(k.Key) ? names[k.Key] : k.Key, ["partsEach"] = k.Value[0], ["instances"] = k.Value[1], ["total"] = k.Value[0] * k.Value[1] }));
        int meshMin = (int?)cmd["meshMin"] ?? 0;
        var big = new JArray(meshes.Where(m => m.Key >= meshMin).OrderByDescending(m => m.Key).Take(meshMin > 0 ? 400 : 12)
            .Select(m => new JObject { ["verts"] = m.Key, ["where"] = m.Value }));
        var groups = new JArray(byGroup.OrderByDescending(k => k.Value).Take(40).Select(k => new JObject { ["group"] = k.Key, ["parts"] = k.Value }));
        int largest = meshes.Count > 0 ? meshes.Max(m => m.Key) : 0;
        int total = sceneParts + addrParts;
        return new JObject
        {
            ["total"] = total, ["sceneParts"] = sceneParts, ["addressableParts"] = addrParts, ["loaders"] = loaders, ["failedLoads"] = failed,
            ["largestVerts"] = largest, ["product"] = (long)total * largest, ["limit"] = 134217728L,
            ["maxParts"] = largest > 0 ? 134217728L / largest : 0,
            ["topAddressables"] = top, ["largestMeshes"] = big, ["groups"] = groups
        };
    }

    static bool IsUnderPreview(Transform t)
    {
        for (var c = t; c != null; c = c.parent)
        {
            if (c.name == "FakePrefabDisplay") return true;
            if (c.GetComponent<BBI.Unity.Game.AddressableLoader>() != null) return false;
        }
        return false;
    }

    // { op:"clone", source:"Tachi/.../PDC_Barrel.003", parent, name, euler:[x,y,z], center, floorY }
    // Copies one baked part that lives in the ship (e.g. a single PDC component) as a plain, unlinked
    // object. Its StructurePart/blueprint asset references are GUID-resolved at load through an
    // AddressableComponentLoader, so the source's ACL entries are duplicated onto an ACL on `parent`.
    static JToken Clone(JObject cmd)
    {
        var src = Find((string)cmd["source"]);
        if (src == null) throw new Exception("Source not found: " + cmd["source"]);
        var parent = Find((string)cmd["parent"]);
        if (parent == null) throw new Exception("Parent not found: " + cmd["parent"]);

        var go = UnityEngine.Object.Instantiate(src.gameObject, parent);
        go.name = UniqueName(parent, (string)cmd["name"] ?? src.name);
        go.SetActive(true);
        go.transform.position = parent.position;
        go.transform.rotation = Quaternion.identity;

        // Every loader above the source may hold its entries (a part can carry an empty loader of its own while the
        // real entries sit on a group further up), so all of them are searched; snapshots, because `parent` may be one.
        var srcEntries = new List<BBI.Unity.Game.AddressableComponentValue>();
        for (var t = src; t != null; t = t.parent) { var a = t.GetComponent<BBI.Unity.Game.AddressableComponentLoader>(); if (a != null && a.componentValues != null) srcEntries.AddRange(a.componentValues); }
        int registered = 0;
        if (srcEntries.Count > 0)
        {
            var acl = parent.GetComponent<BBI.Unity.Game.AddressableComponentLoader>();
            if (acl == null) acl = parent.gameObject.AddComponent<BBI.Unity.Game.AddressableComponentLoader>();
            var srcAll = src.GetComponentsInChildren<Component>(true);
            var dstAll = go.GetComponentsInChildren<Component>(true);
            foreach (var cv in srcEntries)
            {
                if (cv == null || cv.component == null) continue;
                int idx = Array.IndexOf(srcAll, cv.component);
                if (idx < 0 || idx >= dstAll.Length || dstAll[idx] == null || dstAll[idx].GetType() != cv.component.GetType()) continue;
                acl.componentValues.Add(new BBI.Unity.Game.AddressableComponentValue { component = dstAll[idx], field = cv.field, address = cv.address });
                registered++;
            }
            EditorUtility.SetDirty(acl);
        }
        ApplyPose(go.transform, cmd);
        Ledger(go);
        Dirty(go);
        var d = Describe(go);
        d["aclEntries"] = registered;
        return d;
    }

    // { op:"deletemany", paths:[...] } — like delete, but one prefab load/save for all saved objects.
    static JToken DeleteMany(JObject cmd)
    {
        var byAsset = new Dictionary<string, List<string>>();
        int direct = 0; var notFound = new JArray();
        foreach (var p in cmd["paths"])
        {
            var path = (string)p;
            var t = Find(path);
            if (t == null) { notFound.Add(path); continue; }
            if (!InLedger(path) && !((bool?)cmd["force"] ?? false)) throw new Exception("Refusing to delete an object the bridge did not create (pass force:true if the user asked for it): " + path);
            var instRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject);
            // Saved into the outer ship prefab (this also covers nested prefab instances such as wall panels).
            if (instRoot != null && instRoot != t.gameObject && !PrefabUtility.IsAddedGameObjectOverride(t.gameObject))
            {
                var assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instRoot);
                if (!byAsset.TryGetValue(assetPath, out var l)) byAsset[assetPath] = l = new List<string>();
                l.Add(RelPath(t, instRoot.transform));
            }
            else
            {
                var scene = t.gameObject.scene;
                UnityEngine.Object.DestroyImmediate(t.gameObject);
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                direct++;
            }
        }
        // Objects may be defined in a nested prefab asset (e.g. Interior Vessel.prefab inside Tachi.prefab);
        // Unity refuses to destroy those from the outer asset, so they are queued for the asset that owns them.
        int fromPrefab = 0;
        var queue = new Queue<KeyValuePair<string, List<string>>>(byAsset);
        int guard = 0;
        while (queue.Count > 0 && guard++ < 8)
        {
            var kv = queue.Dequeue();
            var deferred = new Dictionary<string, List<string>>();
            var contents = PrefabUtility.LoadPrefabContents(kv.Key);
            try
            {
                foreach (var rel in kv.Value)
                {
                    var target = contents.transform.Find(rel);
                    if (target == null) { notFound.Add(rel); continue; }
                    var parentGo = target.parent != null ? target.parent.gameObject : null;
                    var nested = parentGo != null ? PrefabUtility.GetNearestPrefabInstanceRoot(parentGo) : null;
                    if (nested != null && !PrefabUtility.IsAddedGameObjectOverride(target.gameObject))
                    {
                        var nestedAsset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(nested);
                        if (!deferred.TryGetValue(nestedAsset, out var l)) deferred[nestedAsset] = l = new List<string>();
                        l.Add(RelPath(target, nested.transform));
                        continue;
                    }
                    UnityEngine.Object.DestroyImmediate(target.gameObject);
                    fromPrefab++;
                }
                PrefabUtility.SaveAsPrefabAsset(contents, kv.Key);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            foreach (var d in deferred) queue.Enqueue(d);
        }
        return new JObject { ["deletedInScene"] = direct, ["deletedFromPrefab"] = fromPrefab, ["notFound"] = notFound };
    }

    // ── validate ─────────────────────────────────────────────────────────────
    // { op:"validate" } runs the project's pre-build ShipValidator and returns its issues.
    static JToken Validate()
    {
        var r = ShipValidator.Validate();
        var arr = new JArray();
        foreach (var i in r.Issues)
            arr.Add(new JObject { ["error"] = i.IsError, ["kind"] = i.Kind.ToString(), ["message"] = i.Message });
        return new JObject { ["hasErrors"] = r.HasErrors, ["count"] = arr.Count, ["issues"] = arr };
    }

    // ── manifest ─────────────────────────────────────────────────────────────
    // { op:"manifest" } writes <repo>/placement_manifest.json: for every bridge-placed AddressableLoader,
    // the renderer meshes and colliders its game prefab contains. PartInfoLogger's PlacementCheck reads it
    // in-game and reports which parts actually spawned with those renderers/colliders.

    static bool ActiveInPrefab(Transform t, Transform root)
    {
        for (var c = t; c != null; c = c.parent)
        {
            if (!c.gameObject.activeSelf) return false;
            if (c == root) break;
        }
        return true;
    }

    static string RelPath(Transform t, Transform root)
    {
        if (t == root) return "";
        var p = t.name;
        for (var c = t.parent; c != null && c != root; c = c.parent) p = c.name + "/" + p;
        return p;
    }

    static JToken Manifest()
    {
        if (!File.Exists(LedgerFile)) throw new Exception("Nothing placed yet");
        if (!LoadGameAssets.CheckHandlesValid()) throw new Exception("Game assets not loaded");
        var entries = new JArray();
        var expectedByGuid = new Dictionary<string, JObject>();
        string shipName = null;
        int missing = 0;
        foreach (var path in File.ReadAllLines(LedgerFile).Distinct())
        {
            var t = Find(path);
            if (t == null) { missing++; continue; }   // deleted since
            var loader = t.GetComponent<BBI.Unity.Game.AddressableLoader>();
            if (loader == null) continue;              // group container
            var guid = loader.assetGUID;
            if (!expectedByGuid.TryGetValue(guid, out var exp))
            {
                // The first lookup after a domain reload sometimes returns no location; retry.
                GameObject src = null;
                for (int attempt = 0; attempt < 4 && src == null; attempt++)
                    src = AddressableBaker.LoadAddressableAsync(guid, loader.childPath ?? "").Result;
                var rs = new JArray(); var cs = new JArray();
                if (src != null)
                {
                    foreach (var mr in src.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        if (!mr.enabled || mf == null || mf.sharedMesh == null || !ActiveInPrefab(mr.transform, src.transform)) continue;
                        rs.Add(new JObject { ["p"] = RelPath(mr.transform, src.transform), ["m"] = mf.sharedMesh.name, ["v"] = mf.sharedMesh.vertexCount });
                    }
                    foreach (var col in src.GetComponentsInChildren<Collider>(true))
                    {
                        if (!col.enabled || !ActiveInPrefab(col.transform, src.transform)) continue;
                        var mc = col as MeshCollider;
                        cs.Add(new JObject
                        {
                            ["p"] = RelPath(col.transform, src.transform), ["t"] = col.GetType().Name,
                            ["m"] = mc != null && mc.sharedMesh != null ? mc.sharedMesh.name : "",
                            ["convex"] = mc != null && mc.convex, ["trigger"] = col.isTrigger,
                        });
                    }
                }
                exp = new JObject { ["asset"] = src != null ? src.name : null, ["renderers"] = rs, ["colliders"] = cs };
                expectedByGuid[guid] = exp;
            }
            var root = t.root;
            shipName = root.name;
            entries.Add(new JObject
            {
                ["key"] = path, ["name"] = t.name,
                ["group"] = t.parent != null ? t.parent.name : "",
                ["deck"] = t.parent != null && t.parent.parent != null ? t.parent.parent.name : "",
                ["guid"] = guid, ["shipLocalPos"] = V(root.InverseTransformPoint(t.position)),
                ["asset"] = exp["asset"], ["renderers"] = exp["renderers"], ["colliders"] = exp["colliders"],
            });
        }
        var file = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "placement_manifest.json"));
        File.WriteAllText(file, new JObject { ["ship"] = shipName, ["entries"] = entries }.ToString(Newtonsoft.Json.Formatting.None));
        return new JObject { ["file"] = file, ["entries"] = entries.Count, ["ledgerPathsNotInScene"] = missing, ["distinctParts"] = expectedByGuid.Count,
            // A part whose prefab failed to load has no expectations and would pass vacuously in-game — rerun until 0.
            ["unresolvedParts"] = expectedByGuid.Count(kv => ((JArray)kv.Value["renderers"]).Count == 0 && ((JArray)kv.Value["colliders"]).Count == 0) };
    }

    // ── catalog ──────────────────────────────────────────────────────────────
    // { op:"catalog", guids:["..."], size:256, dir:"catalog", force:false }
    // Renders each addressable to <dir>/<guid>.png. Fully synchronous (WaitForCompletion inside
    // AddressableBaker) — awaiting Addressables tasks from here never completes in an idle editor.

    static JToken Catalog(JObject cmd)
    {
        int size  = (int?)cmd["size"] ?? 256;
        bool force = (bool?)cmd["force"] ?? false;
        var dir   = Path.Combine(Root, (string)cmd["dir"] ?? "catalog");
        Directory.CreateDirectory(dir);
        if (!LoadGameAssets.CheckHandlesValid()) throw new Exception("Game assets not loaded");

        var done = new JArray(); var failed = new JObject();
        foreach (var g in cmd["guids"])
        {
            var guid = (string)g;
            var png  = Path.Combine(dir, guid + ".png");
            if (!force && File.Exists(png)) { done.Add(guid); continue; }
            try
            {
                var src = AddressableBaker.LoadAddressableAsync(guid, "").Result;
                if (src == null) { failed[guid] = "load failed"; continue; }
                if (RenderPrefab(src, png, size) == null) { failed[guid] = "no renderers"; continue; }
                done.Add(guid);
            }
            catch (Exception ex)
            {
                failed[guid] = ex.InnerException?.Message ?? ex.Message;
            }
        }
        return new JObject { ["dir"] = dir, ["done"] = done.Count, ["failed"] = failed };
    }

    // Game materials use a shader the editor can't draw; swap to the project's stand-in (as CustomStage does).
    static void SwapToFakeShader(GameObject inst, List<Material> created)
    {
        var fake = Shader.Find("Fake/_Lynx/Surface/HDRP/Lit");
        if (fake == null) return;
        var map = new Dictionary<Material, Material>();
        foreach (var r in inst.GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = (Material[])r.sharedMaterials.Clone();
            for (int i = 0; i < mats.Length; i++)
            {
                var m = mats[i];
                if (m == null) continue;
                if (!map.TryGetValue(m, out var rep))
                {
                    if (m.shader != null && m.shader.name == "_Lynx/Surface/HDRP/Lit")
                    {
                        rep = new Material(fake);
                        rep.CopyPropertiesFromMaterial(m);
                        created.Add(rep);
                    }
                    else rep = m;
                    map[m] = rep;
                }
                mats[i] = rep;
            }
            r.sharedMaterials = mats;
        }
    }

    static string RenderPrefab(GameObject prefab, string png, int size)
    {
        var pru = new PreviewRenderUtility();
        Texture2D tex = null;
        var created = new List<Material>();
        try
        {
            var inst = UnityEngine.Object.Instantiate(prefab, Vector3.zero, Quaternion.identity);
            inst.hideFlags = HideFlags.HideAndDontSave;
            SwapToFakeShader(inst, created);
            pru.AddSingleGO(inst);
            if (!TryBounds(inst.transform, out var b)) return null;

            var cam = pru.camera;
            cam.fieldOfView = 30f;
            float radius = Mathf.Max(b.extents.magnitude, 0.01f);
            float dist   = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
            var dir = new Vector3(-1f, 0.8f, -1f).normalized;
            cam.transform.position = b.center + dir * dist;
            cam.transform.LookAt(b.center);
            cam.nearClipPlane = Mathf.Max(0.01f, dist - radius * 1.5f);
            cam.farClipPlane  = dist + radius * 1.5f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.22f, 0.24f, 0.28f, 1f);

            pru.lights[0].intensity = 1.3f;
            pru.lights[0].transform.rotation = Quaternion.Euler(40f, 30f, 0f);
            pru.lights[1].intensity = 0.7f;
            pru.lights[1].transform.rotation = Quaternion.Euler(-20f, 200f, 0f);
            pru.ambientColor = new Color(0.35f, 0.35f, 0.35f, 1f);

            pru.BeginStaticPreview(new Rect(0, 0, size, size));
            pru.Render(true, true);
            tex = pru.EndStaticPreview();
            File.WriteAllBytes(png, tex.EncodeToPNG());
            File.WriteAllText(png + ".txt", $"{b.size.x:F2}x{b.size.y:F2}x{b.size.z:F2} c=({b.center.x:F2},{b.center.y:F2},{b.center.z:F2})");
            return png;
        }
        finally
        {
            if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
            pru.Cleanup();
            foreach (var m in created) UnityEngine.Object.DestroyImmediate(m);
        }
    }
}

#endif
