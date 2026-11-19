# BayRecorded: placing a ship in the salvage bay

This folder holds a record of the salvage bay taken from the running game, and two scripts. Use them to place a ship
in the bay so it clears the jack lights, the Master Jack and the barge, and to produce a picture of the result.

What is here:

| File | What it is |
|---|---|
| `../BayRecorded.prefab` (+ meshes and materials) | The recorded bay as a Unity prefab: `Jacks` (the five floating jack lights), `MasterJack`, `Barge` (the barge salvage plane) and `BayStructure`. Editor only; it is never part of a ship. |
| `bay_geometry.csv`, `bay_geometry_meshes.json`, `bay_geometry_meshes.bin` | The raw record written by the PartInfoLogger mod. |
| `bay_show_prep.py` | Turns the raw record into the meshes of the prefab. Only needed when you re-record. |
| `bay_render.py` | Makes the four-view placement picture. |

The folder name ends in `~`, so Unity ignores it.

## What the record can and cannot tell you

- The **jack lights**, the **Master Jack** and the **barge salvage plane** are real geometry at their real positions.
  The jack lights are the reason for this record: they are not in the editor's bay preview at all.
- The **barge frame** is missing. The game does not allow its mesh to be read, so only the flat salvage plane is
  recorded. The real barge has a tall frame around that plane; leave room for it.
- Most of the **bay structure** is missing for the same reason. Use the editor's own bay preview for the walls
  (the `Bay` object in the ship scene); the render script does this for you.
- The jack lights float. The record is where they were when the ship loaded.
- The pictures are for judging the placement by eye. They are not a clearance measurement. Always finish with a
  game load.

## 1. Place the ship

You need the ship scene open (for the Tachi: `Assets/Tachi3.unity`).

1. Select the `Bay` object and tick **Reload** on its Salvage Bay Stage component. Wait for the bay to appear around
   the ship. You must do this again after every build, because a build empties the preview.
2. Drag `Assets/Editor/BayRecorded/BayRecorded.prefab` into the scene Hierarchy, as a root object, and leave it at
   position (0, 0, 0). The yellow shapes are the jack lights and the Master Jack; the orange plate is the barge
   salvage plane. Switch its `BayStructure` child off if it gets in the way of the editor's bay.
3. Select the ship root (`Tachi`) and move and rotate it until it sits where you want it: clear of the five jack
   lights, clear of the Master Jack, above the barge plane with room for the barge frame.
4. Copy the placement to the Hardpoint. This is the documented bay positioning workflow
   (`Docs/USER_GUIDE.md`, section 2.1):
   1. Right-click the ship root's Transform, **Copy World Placement**.
   2. Open the ship's `RootRef` prefab in its `Spawn/` folder (`Tachi_RootRef.prefab`).
   3. Select the `Hardpoint` child, right-click its Transform, **Paste World Placement**. Leave the prefab editor.
   4. Set the ship root in the scene back to position (0, 0, 0), rotation (0, 0, 0).
5. Delete `BayRecorded` from the scene before you save it, so it does not end up in the scene file.

The pose belongs on the Hardpoint only. Do not add a pivot object to the ship prefab and do not leave the ship root
moved: the ship prefab must stay at the origin.

## 2. Make the placement picture

`bay_render.py` reads the pose from the Hardpoint, so run it after step 4 above.

Requirements:

- Unity open on the ship scene, not building, with `Assets/Editor/ClaudeBridge.cs` compiled.
- The bay preview loaded (step 1 above). The script stops with a message if it is not.
- The ship root at (0, 0, 0).
- Python 3 with Pillow (`pip install pillow`).

Run it from the repository root:

```
python "Assets/Editor/BayRecorded/Source~/bay_render.py"
```

It writes `Docs/Tachi_Bay_Placement.png` with four views:

- **Top, side, front**: straight along the bay's three axes.
- **Down the ship's long axis**: from ahead of the nose looking aft, with the bay's back board left out.

In every view the ship is solid, the U of the bay (two arms and the back board) is see-through with a pale blue
outline, the jack lights and Master Jack are yellow, and the barge salvage plane is orange.

The script brings `BayRecorded` into the scene itself, moves the ship root to the Hardpoint pose for the captures,
then puts the root back to zero and switches everything it hid back on. It saves nothing in Unity. It takes under a
minute.

For another ship:

```
python "Assets/Editor/BayRecorded/Source~/bay_render.py" --rootref Assets/_CustomShips/MyShip/Spawn/MyShip_RootRef.prefab --ship MyShip --long-axis z --out Docs/MyShip_Bay_Placement.png
```

`--ship` is the name of the ship's root object in the scene. `--long-axis` is the direction the nose points in the
ship's own prefab (`x`, `y` or `z`; the Tachi is built nose-up, so `y`).

## 3. Check in the game

1. Build the ship. Wait for "Build Complete"; the bundle in the game's `ModdedShipLoader/Ships` folder gets a new
   time. A game started before that still loads the old placement.
2. Load the ship and look at it from outside: the jack lights, the Master Jack and above all the barge frame.
3. If something touches, go back to section 1.

Changing the bay pose can change which parts joint at spawn, so check the ship for loose parts after a new placement.

## Re-recording the bay

Only needed if the bay itself is different (another bay, or a game update).

1. Install the PartInfoLogger mod. In its config file (`BepInEx/config`, section `[BayGeometry]`) set
   `Enabled = true`.
2. Load any ship in that bay. A few seconds after gameplay starts the mod writes `bay_geometry.csv`,
   `bay_geometry_meshes.json` and `bay_geometry_meshes.bin` next to its other output (the folder of its `OutputPath`
   setting).
3. Copy those three files into this folder, replacing the old ones.
4. Run `python "Assets/Editor/BayRecorded/Source~/bay_show_prep.py"`. It prints how many meshes it could use and
   writes `.claude/mesh_work/bay/show/show.json`.
5. With Unity open, send ClaudeBridge this command (the bridge is described in `Docs/USER_GUIDE.md`, section 10.1):

   ```
   {"op": "bayshow", "json": "<repository>/.claude/mesh_work/bay/show/show.json", "save": "Assets/Editor/BayRecorded"}
   ```

   It rebuilds `BayRecorded.prefab` and its meshes.

Coordinates: the record is in game coordinates. The prep script adds (28.6, 3.5, 8.9) to turn them into the ship
scene's coordinates, so the prefab lines up with the editor's bay preview. That offset is set at the top of
`bay_show_prep.py` and belongs to this bay.
