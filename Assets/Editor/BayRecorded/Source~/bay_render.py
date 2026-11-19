"""Bay placement render: four pictures of the ship at the pose stored on the Hardpoint, inside the salvage bay.

  top / side / front      straight along the bay's three axes
  down the ship's axis    from ahead of the nose looking aft, bay back board left out

Drawn: the ship, the U of the editor's bay preview (two arms + back board, see-through), and from BayRecorded.prefab
the five jack lights, the Master Jack and the barge salvage plane. See README.md in this folder.

Needs: Unity open on the ship scene with ClaudeBridge compiled, the bay preview loaded (tick Reload on the Bay object's
Salvage Bay Stage - again after every build), Python 3 with Pillow.

usage (from the repository root):
    python "Assets/Editor/BayRecorded/Source~/bay_render.py" [--rootref <RootRef.prefab>] [--ship <scene root name>]
                                                             [--long-axis x|y|z] [--out <png>]
defaults: Assets/_CustomShips/Tachi/Spawn/Tachi_RootRef.prefab, Tachi, y, Docs/Tachi_Bay_Placement.png

The ship root is moved to the Hardpoint pose for the captures and put back to (0,0,0) afterwards; everything hidden
for the captures is switched back on. Nothing is saved."""
import argparse, json, math, os, re, sys, time
from PIL import Image, ImageDraw, ImageFont, ImageChops, ImageFilter

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", ".."))
BRIDGE = os.path.join(REPO, ".claude", "bridge")
ap = argparse.ArgumentParser()
ap.add_argument("--rootref", default="Assets/_CustomShips/Tachi/Spawn/Tachi_RootRef.prefab")
ap.add_argument("--ship", default="Tachi")
ap.add_argument("--long-axis", default="y", choices=["x", "y", "z"], help="the ship's nose direction in its own prefab")
ap.add_argument("--out", default="Docs/Tachi_Bay_Placement.png")
A = ap.parse_args()


def send(cmd, timeout=300):
    """One command to ClaudeBridge (Assets/Editor/ClaudeBridge.cs): a JSON file in .claude/bridge/in, the answer in out."""
    i, o = os.path.join(BRIDGE, "in"), os.path.join(BRIDGE, "out"); os.makedirs(i, exist_ok=True); os.makedirs(o, exist_ok=True)
    name = "%d.json" % int(time.time() * 1000); tmp = os.path.join(BRIDGE, name + ".tmp")
    with open(tmp, "w", encoding="utf-8") as f: json.dump(cmd, f)
    os.replace(tmp, os.path.join(i, name)); out = os.path.join(o, name); end = time.time() + timeout
    while time.time() < end:
        if os.path.exists(out):
            with open(out, encoding="utf-8") as f: res = json.load(f)
            os.remove(out); return res
        time.sleep(0.2)
    return {"ok": False, "error": "no answer after %ss - is Unity open, compiled and not building?" % timeout}


def need(r, what):
    if not r.get("ok"): sys.exit("%s failed: %s" % (what, str(r.get("error"))[:300]))
    return r.get("result")


# ---- the pose on the Hardpoint (the only child transform of the RootRef prefab that is not at the origin) ----
txt = open(os.path.join(REPO, A.rootref), encoding="utf-8").read(); pose = None
for blk in txt.split("--- !u!")[1:]:
    if not blk.startswith("4 "): continue
    q = re.search(r"m_LocalRotation: \{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+), w: ([-\d.e]+)\}", blk)
    p = re.search(r"m_LocalPosition: \{x: ([-\d.e]+), y: ([-\d.e]+), z: ([-\d.e]+)\}", blk)
    if q and p and re.search(r"m_Father: \{fileID: [1-9]", blk): pose = ([float(v) for v in p.groups()], [float(v) for v in q.groups()])
if not pose: sys.exit("no Hardpoint transform found in " + A.rootref)
pos, (qx, qy, qz, qw) = pose
R = [[1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw)],
     [2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw)],
     [2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy)]]
col = lambda k: [R[i][k] for i in range(3)]
print("Hardpoint: position %s, ship forward %s, ship up %s" % ([round(v, 3) for v in pos], [round(v, 3) for v in col(2)], [round(v, 3) for v in col(1)]))

# ---- scene: recorded bay in, the bay preview copy that actually draws, what to hide ----
need(send({"op": "bayshow", "prefab": "Assets/Editor/BayRecorded/BayRecorded.prefab"}), "showing BayRecorded.prefab")
send({"op": "setactive", "paths": ["BayRecorded/BayStructure"], "active": False})       # the editor's bay is drawn instead
top = [k["name"] for k in need(send({"op": "hierarchy", "path": "Bay", "depth": 1}), "reading the Bay object").get("kids", [])]
COMP = "PRF_HexStation_Composite(Clone)"; ncopy = top.count(COMP); live = -1
for i in range(ncopy):                                          # every Reload adds a copy; only one has its meshes loaded
    x = send({"op": "inspect", "path": "Bay/" + COMP + ("#%d" % i if i else "") + "/PRF_HexStation_Bay/ArmLeft/DryDockLeft/SM_HexStation_Bay_DryDockLeft_Roof"}).get("result") or {}
    if max(x.get("bs") or [0]) > 0: live = i
if live < 0: sys.exit("The bay preview is not drawn (%d copies, none with meshes). Tick Reload on the Bay object's Salvage Bay Stage, wait for the bay to appear, run again." % ncopy)
CP = "Bay/" + COMP + ("#%d" % live if live else ""); U = CP + "/PRF_HexStation_Bay"; hide = []; seen = {}
for n in [k["name"] for k in need(send({"op": "hierarchy", "path": CP, "depth": 1}), "reading the bay preview")["kids"]]:
    i = seen.get(n, 0); seen[n] = i + 1
    if n != "PRF_HexStation_Bay": hide.append(CP + "/" + n + ("#%d" % i if i else ""))
seen = {}
for n in top:
    i = seen.get(n, 0); seen[n] = i + 1
    if not (n == COMP and i == live): hide.append("Bay/" + n + ("#%d" % i if i else ""))
act = lambda paths, on: send({"op": "setactive", "paths": paths, "active": on}).get("ok")
bay = lambda on: send({"op": "move", "path": "Bay", "pos": [0, 0, 0] if on else [0, 0, -9000], "euler": [0, 0, 0], "force": True}).get("ok")

# ---- cameras ----
b = need(send({"op": "hierarchy", "path": A.ship, "depth": 0, "bounds": True}), "reading the ship root")
if max(abs(v) for v in b["pos"]) > 1e-3: sys.exit("The ship root is not at (0,0,0) in the scene; put it back first.")
lc = b["bc"]; SC = [pos[i] + sum(R[i][k] * lc[k] for k in range(3)) for i in range(3)]; ax = col("xyz".index(A.long_axis)); C = [23.2, 15.0, 52.0]   # C = middle of the bay
shots = [("top", "TOP: looking down. Master Jack end at the top, starboard wall on the right.", dict(pos=[C[0], 300, C[2]], lookAt=[C[0], 0, C[2]], up=[0, 0, 1], ortho=88), True),
         ("side", "SIDE: from the starboard wall. Back wall on the left, Master Jack end on the right.", dict(pos=[C[0] + 300, C[1], C[2]], lookAt=C, up=[0, 1, 0], ortho=62), True),
         ("front", "FRONT: from the Master Jack end, looking toward the back wall.", dict(pos=[C[0], C[1], C[2] + 300], lookAt=C, up=[0, 1, 0], ortho=52), True),
         ("shipaxis", "DOWN THE SHIP'S LONG AXIS: from ahead of the nose, looking aft. Bay back board left out.", dict(pos=[SC[i] + ax[i] * 300 for i in range(3)], lookAt=SC, up=[0, 1, 0], ortho=48), False)]
pics = []
try:
    act(hide, False)
    need(send({"op": "move", "path": A.ship, "pos": pos, "forward": col(2), "up": col(1), "force": True}), "posing the ship")
    for name, title, cam, board in shots:
        f = {}; act([U + "/BackBoard"], board)
        for tag, on in (("all", True), ("clear", False)):       # with the bay, and with the bay moved out of sight
            bay(on); f[tag] = need(send(dict({"op": "capture", "name": "bayrender_%s_%s" % (name, tag), "w": 1600, "h": 1000, "near": 0.5, "far": 620}, **cam)), "capture " + name)["file"]
        pics.append((f, title)); print(name, "captured")
finally:
    print("bay back:", bay(True), act([U + "/BackBoard"] + hide, True), "| ship root back to zero:", send({"op": "move", "path": A.ship, "euler": [0, 0, 0], "pos": [0, 0, 0], "force": True}).get("ok"))

# ---- sheet: the bay = whatever differs between the two captures, drawn see-through with its silhouette outlined ----
def font(n, size):
    try: return ImageFont.truetype(n, size)
    except OSError: return ImageFont.load_default()
F, FB, FH = font("arial.ttf", 26), font("arialbd.ttf", 30), font("arialbd.ttf", 46); W, H = 1600, 1000
sheet = Image.new("RGB", (W * 2 + 60, 150 + H * 2 + 40), (245, 245, 245)); d = ImageDraw.Draw(sheet)
d.text((20, 14), "%s at the current Hardpoint pose in the bay" % A.ship, font=FH, fill=(0, 0, 0))
d.text((20, 78), "Yellow X = jack lights.  Yellow block = Master Jack.  Orange = barge salvage plane (the barge frame is not drawn).  Pale blue = the U of the bay, drawn see-through.", font=F, fill=(40, 40, 40))
for i, (f, t) in enumerate(pics):
    a = Image.open(f["all"]).convert("RGB"); c = Image.open(f["clear"]).convert("RGB")
    st = ImageChops.difference(a, c).convert("L").point(lambda v: 255 if v > 8 else 0)
    rim = st.filter(ImageFilter.MaxFilter(9)).filter(ImageFilter.MinFilter(9)).filter(ImageFilter.FIND_EDGES).point(lambda v: 255 if v > 40 else 0).filter(ImageFilter.MaxFilter(3))
    out = Image.composite(Image.blend(c, Image.blend(a, Image.new("RGB", c.size, (150, 195, 240)), 0.45), 0.30), c, st)
    out = Image.composite(Image.new("RGB", c.size, (170, 215, 255)), out, rim)
    q = ImageDraw.Draw(out); q.rectangle([0, 0, W, 54], fill=(20, 20, 22)); q.text((14, 8), t, font=FB, fill=(245, 245, 245))
    sheet.paste(out, (20 + (i % 2) * (W + 20), 140 + (i // 2) * (H + 20)))
sheet.save(os.path.join(REPO, A.out)); print("wrote", A.out, sheet.size)
