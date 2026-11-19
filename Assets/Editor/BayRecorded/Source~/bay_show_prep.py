"""Turn the bay recorded in game (bay_geometry.csv / _meshes.json / _meshes.bin, in this folder) into a few combined
meshes in Unity scene coordinates for the ClaudeBridge op `bayshow`. Groups: jacks, barge, Master Jack, bay structure.
Meshes the game would not release (not readable) are left out. See README.md in this folder.
-> .claude/mesh_work/bay/show/*.bin + show.json
usage (from the repository root): python Assets/Editor/BayRecorded/Source~/bay_show_prep.py
"""
import csv, json, struct, os, collections, shutil
D = os.path.dirname(os.path.abspath(__file__)); S = os.path.abspath(os.path.join(D, "..", "..", "..", "..", ".claude", "mesh_work", "bay", "show")); shutil.rmtree(S, ignore_errors=True); os.makedirs(S)
OFF = (28.6, 3.5, 8.9)                                     # scene = game + OFF (old Hardpoint vs the root position fitted from a census)
rows = list(csv.DictReader(open(os.path.join(D, "bay_geometry.csv"), encoding="utf-8", errors="replace")))
MI = json.load(open(os.path.join(D, "bay_geometry_meshes.json")))["meshes"]; BIN = open(os.path.join(D, "bay_geometry_meshes.bin"), "rb").read()
GROUP = {"PRF_Prop_JaxLight_SingleObject": ("Jacks", [1.0, 0.8, 0.1]), "PRF_Barge_SmallBay (1)": ("Barge", [1.0, 0.45, 0.1]), "PRF_MasterJack_NoSpawner": ("MasterJack", [0.95, 0.75, 0.2]),
         "Work Bays": ("BayStructure", [0.55, 0.57, 0.6]), "FX_Salvagetest_Light_01": ("SmallLights", [0.4, 0.8, 1.0]), "PRF_Prop_JaxLight_SingleObject ": ("Jacks", [1, 0.8, 0.1])}
SKIP = {"FX_Salvagetest_Light_01",      # flat light panels: not solid objects (user: "they aren't real")
        "MJ", "Airlock", "Deck1", "Deck2", "Deck9", "Cryogenics", "Fusion_Reactor", "Floating Corpse", "PRF_Pickup_Data_Drive_S2_Stashed", "Player", "GameCameras", "Audio_TriggerVol",
        "PRF_Playable_Area", "PRF_Base_Planets", "PRF_Base_Planets_HAB", "TutorialLightRig", "PRF_A1_TUT_Scripting_SmallBay_TrackifSeen", "PRF_Polaris_Event", "PRF_Bay_Collision", "PRF_BayAnimatedEmmissiveLighting"}
BOX_T = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3]
G = collections.defaultdict(lambda: {"v": [], "t": [], "n": 0}); seen = set(); stats = collections.Counter()
for r in rows:
    root = r["Root"]
    if root in SKIP: continue
    name, col = GROUP.get(root, ("Other", [0.6, 0.5, 0.7])); i = int(r["MeshIndex"]); m = [float(x) for x in r["LocalToWorld"].split()]
    key = (name, i, r["LocalToWorld"], r["LocalCenter"], r["LocalSize"])
    if key in seen: continue
    seen.add(key)
    if i >= 0:
        e = MI[i]; nv, nt, off = e["verts"], e["tris"], e["offset"]; vs = struct.unpack_from("<%df" % (nv * 3), BIN, off); ts = list(struct.unpack_from("<%di" % (nt * 3), BIN, off + nv * 12)); stats[name + " real meshes"] += 1
    else:
        lc = [float(x) for x in r["LocalCenter"].split()]; ls = [float(x) for x in r["LocalSize"].split()]
        if max(ls) < 1e-6 or r["Kind"] in ("BoxCollider", "SphereCollider", "CapsuleCollider"): continue        # plain colliders are not drawn
        stats[name + " skipped (mesh not readable)"] += 1; continue                                           # their local boxes are unreliable (light cones, far station pieces)
        c = [(lc[0] + sx * ls[0] / 2, lc[1] + sy * ls[1] / 2, lc[2] + sz * ls[2] / 2) for sz in (-1, 1) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        vs = [x for p in c for x in p]; ts = list(BOX_T); nv = 8; stats[name + " boxes (mesh not readable)"] += 1
    g = G[name]; g["col"] = col; base = len(g["v"]) // 3
    for k in range(nv):
        x, y, z = vs[3 * k], vs[3 * k + 1], vs[3 * k + 2]
        g["v"] += [m[0] * x + m[1] * y + m[2] * z + m[3] + OFF[0], m[4] * x + m[5] * y + m[6] * z + m[7] + OFF[1], m[8] * x + m[9] * y + m[10] * z + m[11] + OFF[2]]
    # a mirrored transform flips the winding
    det = m[0] * (m[5] * m[10] - m[6] * m[9]) - m[1] * (m[4] * m[10] - m[6] * m[8]) + m[2] * (m[4] * m[9] - m[5] * m[8])
    if det < 0: ts = [ts[q + j] for q in range(0, len(ts), 3) for j in (0, 2, 1)]
    g["t"] += [base + q for q in ts]
out = []
for name, g in G.items():
    f = os.path.join(S, name + ".bin"); nv = len(g["v"]) // 3; nt = len(g["t"]) // 3
    open(f, "wb").write(struct.pack("<%df" % len(g["v"]), *g["v"]) + struct.pack("<%di" % len(g["t"]), *g["t"]))
    out.append({"name": name, "file": f.replace("\\", "/"), "verts": nv, "tris": nt, "color": g["col"]}); print("%-14s %7d verts %7d tris" % (name, nv, nt))
json.dump({"groups": out}, open(os.path.join(S, "show.json"), "w")); print(dict(stats))
