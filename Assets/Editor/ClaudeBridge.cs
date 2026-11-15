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
                case "reloadscene":   // discard unsaved scene changes by reopening the scene from disk
                {
                    var sp = SceneManager.GetActiveScene().path;
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(sp, UnityEditor.SceneManagement.OpenSceneMode.Single);
                    result = "reloaded " + sp;
                    break;
                }
                case "deletemany": result = DeleteMany(cmd); break;
                case "inspect":   result = Inspect(cmd); break;
                case "placeprefab": result = PlacePrefab(cmd); break;
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
                foreach (Transform c in t) if (c.name == parts[i]) { next = c; break; }
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
        try
        {
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
        if (!InLedger((string)cmd["path"])) throw new Exception("Refusing to move an object the bridge did not create: " + cmd["path"]);
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
    static JToken Inspect(JObject cmd)
    {
        var t = Find((string)cmd["path"]);
        if (t == null) throw new Exception("Not found: " + cmd["path"]);
        return InspectNode(t, (int?)cmd["depth"] ?? 1);
    }

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
                if (c.GetComponent<FakePrefabDisplay>() == null) kids.Add(InspectNode(c, depth - 1));
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
        var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, parent);
        go.name = UniqueName(parent, (string)cmd["name"] ?? asset.name);
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
            if (!InLedger(path)) throw new Exception("Refusing to delete an object the bridge did not create: " + path);
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
        int fromPrefab = 0;
        foreach (var kv in byAsset)
        {
            var contents = PrefabUtility.LoadPrefabContents(kv.Key);
            try
            {
                foreach (var rel in kv.Value)
                {
                    var target = contents.transform.Find(rel);
                    if (target == null) { notFound.Add(rel); continue; }
                    UnityEngine.Object.DestroyImmediate(target.gameObject);
                    fromPrefab++;
                }
                PrefabUtility.SaveAsPrefabAsset(contents, kv.Key);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
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
