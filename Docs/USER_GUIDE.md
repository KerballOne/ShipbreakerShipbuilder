# Shipbuilder User Guide

This guide covers every tool in the ShipbreakerShipbuilder mod — menus, context menus, editor windows, and the non-obvious workflows and caveats that come up when building custom ships.

**Start here first:** Read the [README](../README.md) for project setup, first-ship walkthrough, and the bay-positioning workflow. Come back here once you are inside the editor and building.

---

## Table of Contents

1. [Blender Tools](#1-blender-tools)
   - [Export to Unity FBX](#11-export-to-unity-fbx)
   - [Radial Split (Fixed Angle)](#12-radial-split-fixed-angle)
   - [Radial Split (Seam Detect)](#13-radial-split-seam-detect)
   - [Convex Hull Preview](#14-convex-hull-preview)
   - [Interactive Bisect](#15-interactive-bisect)
   - [Hollow Mesh](#16-hollow-mesh)
   - [Group to Collection](#17-group-to-collection)
   - [Fix Normals](#18-fix-normals)
   - [Material & Texture Pipeline](#19-material--texture-pipeline)
   - [Cut Point Seat](#110-cut-point-seat)
   - [Part Splitting](#111-part-splitting)
   - [Rebuilding a Layered Part from Its Original Model](#112-rebuilding-a-layered-part-from-its-original-model)
2. [The Shipbuilder Menu](#2-the-shipbuilder-menu)
   - [Build & Run](#21-build--run)
   - [Actions Submenu](#22-actions-submenu)
   - [Part Creation Wizards](#23-part-creation-wizards)
   - [Editing Tools](#24-editing-tools)
   - [Geometry & Layout Tools](#25-geometry--layout-tools)
   - [Visualization](#26-visualization)
   - [Utility](#27-utility)
3. [Context Menus](#3-context-menus)
   - [Transform Component](#31-transform-component-right-click-the-gear-icon)
   - [GameObject Hierarchy](#32-gameobject-hierarchy-right-click)
   - [Assets Panel](#33-assets-panel)
4. [Working with Game Parts](#4-working-with-game-parts)
   - [Bake vs Import Addressable](#41-bake-vs-import-addressable)
   - [Repositioning and Rescaling](#42-repositioning-and-rescaling)
   - [Duplicating a Part](#43-duplicating-a-part)
   - [Deleting a Part](#44-deleting-a-part)
   - [Pre-Build Checklist](#45-pre-build-checklist)
5. [Rooms](#5-rooms)
   - [Components Overview](#51-components-overview)
   - [DummyPlugRoom Workflow](#52-adding-a-room-dummyplugroom-workflow)
   - [Room Type GUIDs](#53-room-type-guids)
   - [Pressurisation State](#54-pressurisation-state)
   - [Custom Room Data Assets](#55-custom-room-data-assets)
6. [Joints](#6-joints)
   - [How Auto-Jointing Works](#61-how-auto-jointing-works)
   - [MandatoryJointContainer](#62-mandatoryjointcontainer)
   - [InvisibleJoint Pattern](#63-invisiblejoint-pattern)
7. [Texturing Custom Models](#7-texturing-custom-models)
8. [SP_Mat & BP_Mat GUID Reference](#8-sp_mat--bp_mat-guid-reference)
9. [Caveats & Gotchas](#9-caveats--gotchas)
10. [Expected Unity Exceptions](#10-expected-unity-exceptions)
11. [Career Job Board (ModdedShipLoader)](#11-career-job-board-moddedshiploader)
12. [Ship Variants (Liveries and Variant-Only Parts)](#12-ship-variants-liveries-and-variant-only-parts)
13. [Pipes, Cut Points and Convex Colliders](#13-pipes-cut-points-and-convex-colliders)
14. [Hardpoints and Module Lists](#14-hardpoints-and-module-lists)

---

## 1. Blender Tools

The Blender scripts live in `Blender/blender_scripts/startup/` and are registered as a Blender Script Directory — Blender auto-registers them on startup. Reload all scripts without restarting with **Ctrl+Shift+R**.

---

### 1.1 Export to Unity FBX

**Trigger:** N-panel sidebar → **ShipBreaker** tab → **Export to Unity FBX**. (The Outliner right-click option has been removed — it was unreliable about which object it targeted.)

**Sidebar label** shows the active object when objects are selected, or the active collection name when nothing is selected — use this to confirm what will be exported before clicking.

**Export source — priority order:**
1. **Selected objects** (and their mesh children recursively) — click an object in the Outliner or viewport to select it; child meshes are automatically included
2. **Active layer collection** — if nothing is selected, uses whichever collection is highlighted blue in the Outliner

**What it does:**
- Exports all mesh objects to an FBX with Unity-correct settings: `axis_forward=Y`, `axis_up=Z`, `apply_scale_options=FBX_SCALE_ALL`, `bake_space_transform=True`
- Copies all image textures to `../Textures/` (sibling of the `Models/` folder — shared flat folder, not per-part subfolders)
- Writes a sidecar `<name>.textures.json` next to the FBX — Unity uses this to name materials and wire textures automatically

**Prerequisite — apply scale before exporting:**
1. Make meshes single-user: `Object → Relations → Make Single User → Object & Data`
2. Apply scale: select all objects, `Ctrl+A → Scale`

If scale is not applied, the FBX exporter bakes the transform scale into the export and the geometry arrives in Unity at the wrong size.

**Prerequisite — recenter each object's origin before exporting (fixes cut/explosion FX spawning away from the visible mesh):**
1. Select the object(s), `Object → Set Origin → Origin to Geometry`

Do this for any part whose Blender-authored origin sits far from its own geometry — root cause of cut/explosion FX spawning at the wrong location in-game, since the game reads the GameObject's `transform.position` (which tracks the origin) for FX spawn points, not the render bounds. Do **not** follow this with `Object → Apply → All Transforms` — that re-bakes the object's Location back toward zero and moves the origin back to roughly where it was before, undoing the fix. (Apply All Transforms is still the correct, normal step whenever you've manually moved/rotated/scaled a part and want that baked into its mesh data — that's unrelated to this specific origin-centering step.)

This requires `CustomPartWizard.cs`'s FBX-submesh bake to read each sub-mesh's own node transform from the FBX (not just its vertex data) — confirmed 2026-07-19. Earlier CPW versions placed every segment at identity transform under a shared root, which only produced correctly-assembled parts by coincidence, when every sub-object in the FBX happened to share one Blender origin. Once each part gets its own individually recentered origin via Origin to Geometry, each sub-mesh's vertex data is expressed relative to a *different* local origin — reading only vertex data and ignoring the node's own transform then misassembles the parts (they collapse toward one shared location instead of their correct relative positions). CPW now reads and applies each node's `localPosition`/`localRotation`/`localScale`, so this reassembles correctly.

**Remembers the last export directory** across Blender restarts (saved to `export_collection_prefs.json` next to the script).

---

### 1.2 Radial Split (Fixed Angle)

**ShipBreaker sidebar → Radial Split (Fixed Angle)** (or Object menu → Radial Split (Fixed Angle)).

Splits the selected mesh into N equal radial pie-slice segments around a chosen axis (X, Y, or Z). Each segment becomes a separate mesh object. The original mesh is hidden (not deleted). All segments are grouped into a collection named after the original object.

**Why separate objects:** Unity imports each object in an FBX as a separate `Mesh` asset. Submeshes within a single object become material slots, not separate meshes — the CPW segmented collider workflow requires separate meshes.

**Settings dialog:**
- **Segments** — number of pie slices (default 8)
- **Axis** — X, Y, or Z (default Y)
- **Angle Offset** — rotate all cuts by this many degrees (default 0)

**Typical workflow for a cylindrical shell:**
1. Model the full ring/cylinder
2. Radial Split (Fixed Angle) → 8 segments at 45° around the long axis
3. Keep the original (hidden) in the same collection — the CPW uses it as the visual/trigger mesh
4. Export the collection — original + all segments end up in one FBX

---

### 1.3 Radial Split (Seam Detect)

**ShipBreaker sidebar → Radial Split (Seam Detect)** (or Object menu → Radial Split (Seam Detect)).

Interactive modal tool that auto-detects where large faces meet at concave angles and proposes split planes at those boundaries. Unlike Fixed Angle which requires a known segment count, Seam Detect reads the mesh geometry to find the split planes itself. Works for any polygon count — octagons, pentagons, irregular shapes.

**How it works:**
1. At invoke, the tool analyses the mesh: coplanar faces (within 2°) are merged into groups, then adjacent group pairs are evaluated for area and angle
2. Groups where both faces exceed **Min Face** area, meet at an angle greater than the **Angle** threshold, and have similar areas (ratio < 2:1) are detected as seam boundaries
3. Yellow preview planes show where the cuts will be made
4. On confirm, the mesh is duplicated, cut at all seam planes, and each angular sector is separated into its own named object in a collection

**Controls:**
| Input | Action |
|---|---|
| Drag up/down | Increase/decrease Min Face area threshold (50 m² range per full drag) |
| Scroll wheel | Fine-adjust Min Face ±0.5 m² |
| Q / E | Decrease / increase Angle threshold ±1° |
| R | Cycle axis (X → Y → Z) |
| F | Toggle fill on cut faces |
| Enter / Numpad Enter | Confirm and apply |
| RMB / Esc | Cancel |

**HUD:** `Radial Split (Seam Detect)  |  Axis: Z  |  Min Face: 10.00 m²  |  Angle: 15.0°  |  Splits: 8  |  Fill: OFF`

**Splits count** = number of detected seam boundaries = number of output segments.

**Typical workflow for a polygon-shaped ring:**
1. Model the full ring
2. Run Radial Split (Seam Detect) with Axis = Z (or whichever axis runs through the ring)
3. Adjust Min Face up until only the corner seams between wall panels remain; adjust Angle as needed
4. Press Enter — each wall panel becomes its own `_seg01..N` object in a collection
5. Export the collection (original hidden + all segments)

**Pentagon / asymmetric shapes:** The tool uses sector-labeling rather than fixed angles, so it handles non-symmetric shapes correctly — a pentagon produces 5 segments, an irregular hexagon produces 6, etc.

---

### 1.4 Convex Hull Preview

**ShipBreaker sidebar → Convex Hull Preview**

Visualization-only tool that previews the convex hull mesh Unity would generate for a MeshCollider. Select any number of mesh objects and click **Preview Convex Hull(s)** — each object's hull is computed (via an isolated bmesh convex-hull op, no edit-mode/undo needed) and drawn as a colored wireframe overlay. When two or more hulls actually intersect (checked via an exact boolean intersection, not just a bounding-box test), the true overlap volume is highlighted in red, flagging real collider interpenetration. Click **Clear Preview** to remove the overlay.

---

### 1.5 Interactive Bisect

**ShipBreaker sidebar → Interactive Bisect**

Modal operator for cutting a mesh with a single plane interactively. Drag to position the cut, confirm with Enter.

---

### 1.6 Hollow Mesh

**ShipBreaker sidebar → Hollow Mesh**

Adds wall thickness to a flat or single-sided mesh by extruding faces inward. Useful for converting a thin shell mesh into a solid-walled part suitable for convex colliders.

---

### 1.7 Group to Collection

**ShipBreaker sidebar → Group to Collection**

Creates a new collection containing the selected objects, nested under their current collection. Equivalent to grouping objects in the Outliner without changing their scene positions.

---

### 1.8 Fix Normals

**ShipBreaker sidebar → Fix Normals panel**

Tools for detecting and correcting face normal problems on imported or modelled meshes. Located in the **Fix Normals** sub-panel in the ShipBreaker N-panel tab.

#### Check Mesh

Analyses all selected objects and reports per-object: total face count, non-manifold edge count, and thin face count. After running, thin faces (faces on open/single-thickness surfaces) are selected across all objects and Edit Mode opens in Face Select so you can see them highlighted in orange.

**What "thin faces" means:** A face whose edges are only shared by one face (non-manifold boundary edges). These are surfaces with no thickness — flipping their normal just moves the problem to the other side. They need Solidify before normals can be fixed.

#### Solidify

Adds thickness to all selected mesh objects using Blender's Solidify modifier, then applies it. Opens a dialog to set the **Thickness** in metres before applying.

- Grows inward (`offset = -1`) so the outer surface stays in place
- Use this when Check Mesh reports NEEDS SOLIDIFY
- **Thickness affects in-game mass** — the game uses mesh volume for mass calculation, so adjust thickness intentionally
- **Running it again does not stack shells.** Solidify marks the shell it adds (vertex group `SB_Shell`). A second run removes that shell first and builds a new one, so you can change the thickness by simply running it again. Meshes that are already closed are skipped.
- **Remove Shell** takes the added shell off again and leaves the original faces.
- **Even Thickness** is off by default: on meshes with sharp angles it can throw vertices far away. If the result grows well beyond the chosen thickness, the tool restores the mesh and reports it.

**Workflow for game-exported ring meshes (e.g. reactor housing segments):**
1. Select all segments
2. **Check Mesh** — confirm they need solidify
3. **Solidify** — set thickness to suit gameplay/mass requirements
4. After solidify, run **Mesh → Normals → Recalculate Outside** (Alt+N in Edit Mode) — all faces should turn blue
5. Export to Unity FBX

#### Fix Inner Normals

Experimental raycast-based tool for flipping inward-facing normals on ring-segment meshes. For game-exported meshes, use the **Solidify → Recalculate** workflow above instead — it is more reliable. Fix Inner Normals is intended for meshes that are already solid (have wall thickness) but have inconsistent normals.

**Settings:**
- **Ring Axis** — the axis the ring runs along (Z/Y/X)
- **Show Debug Rays** — draws 3 yellow ray lines in the viewport instead of flipping, for diagnosing raycast behaviour

#### Clear Debug Rays

Clears the yellow debug ray lines from the viewport.

---

### 1.9 Material & Texture Pipeline

When you export from Blender, the script automatically copies textures and writes a sidecar JSON. On the Unity side, `CustomMeshPostprocessor` reads the sidecar and creates a correctly wired material automatically — no manual material setup needed.

**Texture naming convention (required):**

Textures must follow the `<TextureSetName>_<Suffix>.png` pattern. The suffix determines the HDRP slot:

| Suffix | HDRP Slot | Import setting |
|---|---|---|
| `_BaseColor` | `_BaseColorMap` | Default, sRGB |
| `_Normal` | `_NormalMap` | Normal Map type, sRGB off |
| `_MaskMap` | `_MaskMap` | Default, sRGB off |
| `_Metallic` / `_Roughness` / `_AO` | (copied but not auto-wired) | Default, sRGB off |

**Material naming:** The material is named after the **texture set**, not the Blender material name or part name. For example, if `Aft_Section_BaseColor.png` is found, the created material is `Aft_Section.mat`. Multiple parts sharing the same texture atlas automatically reuse the same material — you do not need to create or assign it manually.

**Sidecar JSON format:**
```json
{
  "Aft_Section": {
    "BaseColor": "Aft_Section_BaseColor.png",
    "Normal": "Aft_Section_Normal.png",
    "MaskMap": "Aft_Section_MaskMap.png"
  }
}
```

**CPW auto-population:** When you drag an FBX into the CPW **Model** field, the wizard reads the sidecar and auto-populates the Material, Mesh, BaseColor, Normal, and MaskMap fields. No manual picker needed.

**Folder layout:**
```
Assets/_CustomShips/<Ship>/
  Models/          — FBX files + sidecar .textures.json files
  Materials/       — auto-created .mat files (named after texture set)
  Textures/        — PNG textures (flat, shared across all parts)
```

### 1.10 Cut Point Seat

**Trigger:** N-panel sidebar → **ShipBreaker** tab → **Cut Point Seat** (script: `Blender/blender_scripts/startup/cutpoint_seat.py`).

**What it is for.** A stock cut point is a plain block. It only joints to a part when one of its flat faces lies flat against a flat face of that part. Where two hull parts meet at a ridge, a lap or an odd angle there is usually no such face. This tool builds one: a small flat **seat** (attachment pad) on one or both parts, shaped so the block sits cleanly across the two parts. You then place the real stock cut point in Unity where the tool shows it.

The tool only shapes geometry. It does not check jointing or collisions; do that in Unity and in game (section 5).

#### Before you start

- Both parts must be separate mesh objects, and they must touch or nearly touch (within the **Seam tolerance**, 2 cm by default).
- Apply scale on both objects (`Ctrl+A → Scale`), the same as for export (1.1).
- Modifiers are ignored. Apply any modifier that changes the shape first.
- Save the file. **Build Seat** edits the meshes (it is one undo step, `Ctrl+Z`).

#### Step by step

1. **Select the two parts.** Click one, Shift-click the other. The last one clicked (lighter outline) is the **active** part and is called **A** in the panel; the other is **B**. The top of the panel shows which is which.
2. **Put the 3D cursor on the seam** near where the cut point should go: `Shift + right-click` on the spot. It does not have to be exact; the tool snaps to the nearest point where the parts touch.
3. **Look at the side the seat should be on.** Orbit the viewport so you are looking at the faces that should carry the cut point (for a hull, usually the inside). The seat is built on the side facing you.
4. **Pick the cut point size** under **Cut point** (see the table below), or choose **Custom size** and type length, width and thickness.
5. **Pick the seat type and where the pads go** (see "Choosing the options").
6. Press **Preview**. Orange lines show the cut point, blue lines the pad on A, red lines the pad on B. The status bar at the bottom of the window reports the pad heights. Nothing is changed yet.
7. Adjust and press **Preview** again until it looks right. The **X** button clears the preview.
8. Press **Build Seat**. The pads are added to the meshes and a wire box the size of the cut point appears in a collection called **Cut Point Seats**.

#### Cut point sizes

| Preset | Stock part to place in Unity | Length × width × thickness (m) |
|---|---|---|
| Generic 1xBxB | `PRF_Cutpoint_Generic_1xBxB` | 1.0 × 0.5 × 0.5 |
| ShipKit Ax1CxB | `PRF_ShipKit_Cutpoint_Ax1CxB_Grade0` | 1.75 × 0.5 × 0.25 |
| Javelin 2xAxC | `PRF_Javelin_Cockpit_Cutpoint_2xAxC` | 0.375 × 0.25 × 0.125 |
| Bayonet Straight | `PRF_Shipkit_Exterior_Bayonet_2x4x26_Straight_Z_CutpointGrade2` | 1.65 × 0.375 × 1.03 |
| Ring Bracket AxAxB | `PRF_ShipKit_Internal_ChassisRing_Cutpoint_Bracket_AxAxB` | 0.5 × 0.25 × 0.25 |

**Length** always runs along the seam. **Thickness** is how far the block stands off the seat. Stock cut points must be used at their real size (never scaled), so match these numbers; to turn a block on its side, swap width and thickness. The ring bracket is a triangular corner piece, not a full block: use it with an Orthogonal seat.

#### Choosing the options

**Seat type**

| Choose | When | Result |
|---|---|---|
| **Co-planar** | The two parts form a ridge, a shallow valley or lie roughly in one plane | One flat plane across both parts. The block lies flat on it, half on each part. |
| **Orthogonal** | The two parts meet at about a right angle (a wall standing on a floor) | Two planes at 90°. The block's bottom sits on one part and one long side butts against the other. **Bottom rests on** chooses which part is the floor. **Side pad height** sets how much of the block's side is backed (1 = all of it; lower gives a smaller pad). |

**Pads on**

| Choose | When | Result |
|---|---|---|
| **Both parts** | Neither part has a usable flat face at the spot | The seat is lifted clear of both parts and each part gets a pad. With **Find the lowest seat angle** on, the tool turns the seat about the seam and keeps the angle with the lowest pads. Turn it off to set **Seat angle** by hand. |
| **Active part only** | Part B already has a good flat face there and must not be changed | B is left untouched; its own face is used as its seat. Only A gets a pad. |
| **Other part only** | The same, the other way round | A is left untouched; only B gets a pad. |

**Position**

| Option | Effect |
|---|---|
| Where | **Nearest the 3D cursor** (normal use) or **Middle of the seam**. |
| Search along seam | The tool also tries spots up to this distance either way along the seam and keeps the one with the lowest pads. The report says "moved … m along the seam" when it did. Set to 0 to force the exact spot. |
| Slide along seam / Slide across seam | Nudge the result by hand. |
| Side | **Side I am looking at** builds on the faces toward the viewport. Tick **Flip to the far side** for the opposite. **Whichever is lowest** ignores the view. |

**Pad size**

| Option | Effect |
|---|---|
| Clear of | **Whole part** lifts the seat above every point of the part. Use this by default: the game gives each part a convex collider, and a cut point sitting in a hollow of a part ends up inside that collider. **Nearby surface only** gives lower pads and is safe only on parts with no hollows. |
| Minimum pad height | How far the seat is lifted above the highest point (2 cm default). |
| Pad margin | How far the pad extends past the block's footprint. |
| Pad length | Pad length as a fraction of the block's length. |
| Maximum pad height | Seats needing a taller pad are refused. Raise it if you accept a tall pad. |
| Gap under cut point | Clearance between pad and block (1 mm default). |

**Advanced**

| Option | Effect |
|---|---|
| Skirt detail | Spacing of the points where the pad's walls follow the part's surface. Smaller follows a curved surface more closely and adds faces. |
| Seam tolerance | How close the parts must be to count as touching. |
| Pad faces first in the mesh | Puts the new faces at the start of the face list. Leave on. |
| Add cut point placeholder box | Adds the wire box. Leave on unless you only want the pads. |
| Show contact points | Draws the points where the tool found the parts touching. Use it when the seat lands somewhere unexpected. |

#### Typical recipes

- **Small cut point across a ridge between two hull panels:** Javelin 2xAxC, Co-planar, Pads on Both parts.
- **Block in the corner where a wall panel stands on a deck plate you must not change:** Generic 1xBxB, Orthogonal, select the wall last so it is A, **Bottom rests on: Other part**, **Pads on: Active part only**.
- **Pad comes out very tall:** try the other seat type, a smaller preset, a larger **Search along seam**, or look from the other side. On a deep inside ridge the height comes from the shape of the parts and will not go down with a smaller block.

#### When it refuses

The reason is shown in the status bar and written to `blender_debug.log`.

| Message | Meaning and fix |
|---|---|
| Select exactly two mesh objects | Select two meshes; the active one is A. |
| The two parts do not come within … of each other | They do not touch. Check you picked neighbours, or raise **Seam tolerance**. |
| The pad's footprint runs off … | The pad would hang over the edge of that part. Move the cursor along the seam, raise **Search along seam**, lower **Pad margin**, or use a smaller block. |
| Pad on … would be … m tall | Taller than **Maximum pad height**. Try another spot or seat type, or raise the limit. |
| … has no flat face near that spot / under its half | With pads on one part only, the untouched part needs its own flat face there. Use **Both parts**, or another spot. |
| Seat would be on the far side | Nothing fits on the side you are looking at. Orbit to the other side or tick **Flip**. |

#### Taking the result to Unity

1. **Export the changed parts** with **Export to Unity FBX** (1.1) as usual. Do **not** export the **Cut Point Seats** collection; the boxes are markers only.
2. In Unity, open **Shipbuilder / Import Game Part Wizard** (3.1), find the stock part from the table above and choose **Import Addressable**. Leave its scale at 1.
3. **Move it into the seat.** Select the placeholder box in Blender and read its Location in the N-panel **Item** tab. Convert to a Unity position with: Unity X = −Blender X, Unity Y = Blender Z, Unity Z = −Blender Y. Rotate the cut point so its long side runs along the seam and its faces lie flat on the pads; the box's edges show the orientation. The part should end up touching both pads with no visible gap and without sinking into them.
4. **Check the joints** with the tools in section 5, then confirm in game that the cut point holds both parts and that they separate when it is cut.

Keep in mind that a cut point only joints parts whose joint types allow it (section 5.1), and that the pad takes the material and one colour sample of the face it stands on, so it blends in but has no detail of its own.

---

### 1.11 Part Splitting

**ShipBreaker sidebar → Part Splitting panel**

Every part of a custom ship collides as its **convex hull**, not as its visible shape. A part that other parts pass through or sit inside (a ring, a frame, a collar, a tunnel) therefore has an invisible collider that fills its opening. Whatever sits in that opening is embedded in the collider: it can be pushed away when a joint is cut, and the cutter can hit the invisible collider first. The fix is to cut such a part into pieces that are each close to convex. These three tools do the cutting and the checking.

#### Cut Along Face

Cuts objects along the plane of a face you pick on another object, fills every cut in the same step, and keeps both sides as separate objects.

1. In Object Mode select the parts to cut, then select the **reference** part last (the part whose face gives the plane).
2. Press Tab. All selected objects enter Edit Mode together.
3. Select one face on the reference part. (Instead of a face you can select exactly three vertices; the plane goes through them.)
4. Click **Cut Others Along This Face**.

The reference part is never cut. Each cut part keeps the side behind the face under its own name; the side in front becomes a new object named `<name>_2` (next free number). The panel then lists every resulting piece as OK or FIX.

Options in the redo panel (bottom left of the viewport, or F9):

| Option | Meaning |
|---|---|
| **Keep** | Both sides (default), only the side behind the face, or only the side in front |
| **Offset** | Moves the cut plane along the face normal, in metres |
| **Fill the cut** | Closes every cut loop. A loop inside another loop (a hollow cross-section) is filled as a ring, so a thick-walled tube gets a ring-shaped cap and not a lid |
| **Separate loose pieces** | If a side falls into several pieces, each becomes its own object |

**Pick Cut Plane (use later)** stores the plane of the selected face instead of cutting. Back in Object Mode, **Cut Selected Along Stored Plane** then cuts whatever is selected along it. Use this when the reference part and the parts to cut are awkward to select together, or to reuse one plane for several rounds.

**Why cut along another part's face:** parts that nest inside each other should be cut along the *same* planes, taken from the part they nest with. Then the seams of both line up and every piece stays on its own side of the plane. Cutting each part along its own, slightly different planes leaves pieces that reach across each other's seams.

#### Check Parts

Reports for every selected object: open edges, non-manifold edges, loose pieces, volume and triangle count. A part is good when it is **closed and one piece**. Afterwards only the objects that need attention stay selected. Vertices closer than 0.2 mm count as one, so UV seams and split normals are not reported as open edges.

#### Collider Check

For every pair of selected objects, reports how much of one lies inside the convex hull of the other: the share of it and the deepest point, in metres. Parts that only touch are not reported (the default tolerance is 5 mm; change it in the redo panel). Use **Convex Hull Preview** (1.4) to see the hull of a single part.

A result of *no overlaps* means no selected part is embedded in another one's collider. Small overlaps where two pieces of the same seam meet are normal.

The full text of the last report of any of the three tools is in the Text Editor under `SB_Part_Report`.

---

### 1.12 Rebuilding a Layered Part from Its Original Model

Use this when a part of the ship has become heavy and messy inside: stacked shells from repeated Solidify, interleaved sheets, open rims after slicing. Going back to the original model and repeating the steps in the right order is quicker and cleaner than repairing the result. The order matters more than any single step.

**Before you start:** find the earliest version of the model you have. The original objects are usually simple: a few open sheets and a few closed boxes.

1. **Take the original objects** of the part into a new collection. If a piece you need is only a region of a larger object, select its faces in Edit Mode and use **Mesh → Separate → Selection**.
2. **Solidify once, open sheets only.** Run **Fix Normals → Solidify** (1.8) on the open sheets with a plain thickness. Leave closed boxes alone. Solidify marks the shell it adds, so running it again replaces that shell and does not stack a second one; **Remove Shell** takes it off again.
3. **Separate the originals from each other while each is still whole.** Where two solids share volume, give one a Boolean modifier (Difference, Exact solver) with the other as the object, and apply it. If the result is not one closed piece, undo and subtract the other way round.
4. **Shorten with one square cut** where the part should end (for example at an end plate): **Cut Along Face** with **Keep** set to one side. Use the part's own direction for the plane, not the ship's axes; a part that is turned a few degrees gets a skewed cut otherwise.
5. **Cut what belongs to different pieces apart**, for example a flange from the tube behind it: **Cut Along Face** with both sides kept.
6. **Split every part that something passes through**, along the planes of the part it nests with:
   - a tube or tunnel into its walls, at the corners;
   - a flat plate around an opening into strips, along the faces of the frame that surrounds it;
   - what is left around the opening into quarters, along the same seams as the tube behind it.
7. **Check Parts** on everything. Every piece must be closed and one piece.
8. **Collider Check** on the pieces together with whatever sits inside them. Cut again where something is still deep inside a collider, or join small details into the piece they rest on.
9. **Replace heavy detail that reads as a simple shape** with the simple shape (for example a stack of rings by a half cylinder), and join it into the piece it rests on. Give it its own material slot if it needs its own material.
10. **Set each origin to its geometry** (**Object → Set Origin → Origin to Geometry**) and export with **Export to Unity FBX** (1.1).
11. **In Unity**, place the new parts, register them like any other part, switch the old pieces off and compare from several sides. Delete the old pieces only when the new ones look right, then run the build validation (it reports list entries left behind by deleted objects; clean them before building).

Rules that keep the result clean:

- **Solidify before any cut.** Solidifying pieces after cutting makes neighbouring pieces grow into each other.
- **Fill each cut in the same step as the cut.** Open rims are far harder to close later.
- **One Solidify pass.** If the wall is too thin, remove the shell and solidify again with a larger thickness.
- **Judge overlap by the collider, not by the visible mesh.** Two parts can look clear of each other and still sit inside one another's convex hull.

---

## 2. The Shipbuilder Menu

The main `Shipbuilder` menu in the Unity menu bar is the primary interface for all build and tool operations.

![Shipbuilder menu](ShipbuilderMenu.png)

---

### 1.1 Build & Run

#### `Shipbuilder / ⛭ Build`

Runs the pre-build validator ([ShipValidator](#ship-validator)) and then builds the addressable content bundles. The validator will block the build if any errors are found (e.g. null ACL entries). Warnings are shown but do not block the build — but you should fix them before testing in-game.

**When to use:** After any change to ship prefabs, materials, or addressables.

**Caveats:**
- The first build after a clean is slow (rebuilds everything from scratch). Subsequent builds are incremental.
- If the build hangs at the loading screen in-game, a null ACL entry is the most likely cause. The validator catches these — check for errors before building.

#### `Shipbuilder / ▶ Build and run`

Same as Build, but automatically launches the game with the built content when the build completes.

#### Toolbar `↺` Button

A quick-access button in the Unity toolbar (approximately 450px from the left) that triggers **Force View Refresh** — identical to `Shipbuilder / Actions / Force View Refresh` (`Ctrl+Alt+R`). Use this after placing or moving addressable parts in the scene.

---

#### Ship Validator

The validator runs automatically before every build. It scans all addressable prefabs and reports:

| Severity | Condition | Effect |
|---|---|---|
| **Error** | `AddressableComponentLoader` has null/missing component entries | Blocks the build |
| **Warning** | GO with `StructurePart` in subtree has non-unit `localScale` | Non-blocking — use **Auto-Fix & Build** in the warning dialog to lock in rescale and save automatically, or cancel and fix manually |

**Common error scenario:** You bake a panel, delete the baked GO, add an addressable replacement — the original ACL entries become null but remain in the list. Delete the dead entries using the `−` button in the Inspector, or use the **Component Copy Window** to diff and clean.

---

### 1.2 Actions Submenu

#### `Shipbuilder / Actions / Force View Refresh` — `Ctrl+Alt+R`

Refreshes the scene view rendering of addressable assets. Unity does not automatically update the visual representation of addressable GOs when you reposition or re-parent them.

**Two-pass pattern:** After placing a new addressable part, run Force View Refresh **twice**:
1. First pass builds the EditorCache prefab for the asset
2. Second pass instantiates from cache and populates the rendered children in the scene

If you run it once and the part looks empty or missing, run it again.

#### `Shipbuilder / Actions / Cancel Refresh` — `Ctrl+Alt+C`

Halts an ongoing view refresh. Use this if you accidentally trigger a refresh while many assets are loading and want to stop it mid-process.

#### `Shipbuilder / Actions / Clear Editor Cache`

Wipes all cached game assets from the `Assets/EditorCache` folder, forcing them to be re-downloaded and re-cached on next refresh. 

**When to use:** If an addressable part's visual in the editor looks wrong after you updated the game or changed a material — the old cached version is being used. Clear the cache and run Force View Refresh.

#### `Shipbuilder / Actions / Clear View`

Removes all currently rendered addressable assets from the scene view without clearing the cache. Faster than Clear Editor Cache when you just want a clean view.

#### `Shipbuilder / Actions / Reload Assets`

Reloads the game's addressable catalogs and your custom catalogs. Run this after:
- Updating the game via Steam
- Manually editing addressable group settings
- Seeing "catalog not found" or missing-asset errors

#### `Shipbuilder / Actions / Part Placeholders`

Keeps every loader-filled `StructurePart` in your ship prefabs pointed at a stand-in `SPA_Placeholder` asset, so the game's job board never sees an empty part (see section 11 for why). You normally never run anything: it works by itself.

| Item | What it does |
|---|---|
| **Apply Automatically** (ticked by default) | Whenever a prefab under `Assets/_CustomShips` is written (Import Game Part Wizard, Custom Part Wizard, Bake Addressable→Prefab, Make Interactable, a scene save that updates the ship prefab), any `StructurePart` that an `AddressableComponentLoader` entry in the same prefab fills, and that is still empty, gets the placeholder. Untick to switch this off |
| **Apply To All Ship Prefabs** | Does the same for every prefab under `Assets/_CustomShips` in one pass and reports the count. Use it once on a ship built before this existed, or after a fresh checkout |

- A part nothing fills (no loader entry in the same prefab) is left empty on purpose: nothing would ever replace the placeholder on it.
- The placeholder asset is the `SPA_Placeholder.asset` inside the ship's own folder if it has one (for example `<ShipName>/Data`), otherwise the shared `Assets/_CustomShips/_Common/SPA_Placeholder.asset`.
- Imports of more than 25 prefabs at once (a fresh checkout, a Library rebuild) are skipped, so opening the project never rewrites prefabs.
- To take the placeholders off a ship again: `python .claude/spa_placeholder.py remove <Ship>` with automatic mode unticked.

#### `Shipbuilder / Actions / Rebuild Addressables`

Triggers the full Unity Addressables build pipeline. Run this when you change addressable group assignments or after adding new assets to an addressable group.

#### `Shipbuilder / Actions / Update game catalog`

Updates the project's reference to the game's addressable catalog. Run once during initial setup, and again after any game update. Requires a Unity restart to take effect.

#### `Shipbuilder / Actions / Update known assets`

Refreshes `known_assets.json` — the mapping file the Import Game Part Wizard uses to search for parts by name. Run once after initial setup or after a game update that adds new assets.

#### `Shipbuilder / Actions / Reload Build Settings`

Reloads `shipbreaker_settings.json` (game path, author name). Use this if you edit the settings file while Unity is open and want the changes to take effect without restarting.

#### `Shipbuilder / Actions / Dump Selected Components`

Prints the addressable component hierarchy for the currently selected GO to the Unity console. Useful when reverse-engineering how a game asset is structured or diagnosing why a part's components are not registering.

#### `Shipbuilder / Actions / Dump Addressable Components (live)`

Dumps all live-loaded addressable components currently present in the scene to the console. Use this when you want a snapshot of everything currently loaded for debug purposes.

---

### 1.3 Part Creation Wizards

#### `Shipbuilder / Import Game Part Wizard`

Opens a searchable window for importing game parts into your ship. Search by part name, browse results, and choose how to import the selected part.

**Two import modes — choose carefully:**

| Mode | What it does | Use when |
|---|---|---|
| **Bake** | Downloads the addressable, extracts mesh/collider/components into a self-contained `.prefab` in your project | You need to rescale it; it's a structural part (panel, floor, hull); you want to modify it freely |
| **Import Addressable** | Places a lightweight `AddressableLoader` GO that streams the part at runtime | The part has animations or runtime behaviour (doors, airlocks, reactors, powered subsystems); you are not rescaling it |

**Do not bake:**
- Animated doors and airlocks — baking strips the AddressableSOLoader and the animations stop working
- Parts that load child content via their own `AddressableSOLoader` at runtime

**Real example — a test ship's engine nozzle:** The torch drive nozzle was built as a custom FBX mesh and imported via the wizard using the Bake path, then scaled to fit. Lock In Rescale was required before building because the game reads joint/mass data from raw mesh vertices, not transform scale.

**Real example — hull panels:** Mackerel wall panels are baked so they can be repositioned and rescaled to fit custom hull dimensions. The bake process auto-recenters the geometry on the prefab's root origin, so panels appear correctly at (0,0,0) in prefab edit mode.

**After baking:** The output is a `_Baked.prefab` in `<ship>/Prefabs/<sub>/`. It has an `AddressableComponentLoader` on the root (distinct from the `AddressableLoader` on a loader-mode GO). Each sub-mesh has its own `StructurePart` and `EntityBlueprintComponent` for per-mesh salvage physics.

**Caveat — colliders:** Baked parts use convex colliders. Concave colliders do not register with the game's physics/salvage system — the player passes through the part and it cannot be cut or targeted.

---

#### `Shipbuilder / Create Custom Part Wizard`

Opens a wizard for creating new custom salvageable parts from your own meshes (FBX files you have already imported into the project).

**Workflow:**
1. Select a template from the dropdown (see table below)
2. Enter the part name and choose an output folder
3. Click Create — the wizard copies the template prefab, swaps in your mesh and material, and wires up all required components
4. The output prefab appears in `<ShipRoot>/Prefabs/CUSTOM/<PartName>.prefab`

**Available templates:**

| Template | Material | Density | Destination |
|---|---|---|---|
| Nanocarbon Panel | Nanocarbon | 50 kg/m³ | Processor |
| Steel Chassis | Steel | 200 kg/m³ | Furnace |
| Glass | Glass | 50 kg/m³ | Furnace |
| Aluminum Chassis | Aluminum | 50 kg/m³ | Furnace |
| ExoStructure Chassis | Titanium (display) | 200 kg/m³ | Furnace |
| Reactor Core | Reactor | 200 kg/m³ | Barge (explosive when cut) |
| Thruster Nozzle | Class X | 50 kg/m³ | Barge (cut level 5) |
| Quasar Thruster | Class X Engine | 50 kg/m³ | Barge |

**Mass:** Mass is calculated automatically as `SP_Mat density × mesh volume`. There is no mass override field — to change mass, either change the template (different density) or adjust the mesh geometry.

**Label format:** The in-game part label cannot contain `/` — Unity treats that as a submenu separator. Use `kg|m3` or similar instead of `kg/m3`.

**Read/Write:** The wizard automatically enables Read/Write on your FBX at import. This is required — the game reads raw vertex data from the mesh to calculate mass. If you see a part with zero mass in-game, check that Read/Write is enabled on the FBX import settings.

**Opening child:** By default the wizard removes the `Opening` child GO (script ID `1728645113`). This child marks an atmosphere/pressure boundary — keep it only if you are making an airlock or section connector.

#### Using a segmented model (convex collider children)

When a mesh is too complex or too hollow for a single convex collider (e.g. a ring, torus, or cylindrical shell), export it from Blender as a model with the original mesh plus radially-split segment objects, and use the **Model** field instead of (or alongside) **Mesh**.

**Blender preparation:**
1. Use **Radial Split (Fixed Angle)** (for uniform N-segment rings) or **Radial Split (Seam Detect)** (for polygon-shaped rings where the splits should align with face boundaries) to split the mesh into segments. Keep the original unsplit object in the same collection — do not hide or delete it.
2. Select the entire collection in the Outliner, then run **Object menu → Export Collection to Unity FBX**. This exports all mesh objects with Unity-correct axis and scale settings.
3. Import the resulting FBX into `Assets/_CustomShips/<ShipName>/Models/`.

**FBX requirements:**
- The original unsplit mesh must be present in the FBX (not just the segments). CPW uses it for the root MeshFilter, MeshRenderer, and trigger MeshCollider.
- Segment objects must be named with a `_seg` suffix (e.g. `Engine_Mount_seg01`). CPW identifies segments by this pattern.
- Apply scale in Blender before export (`Ctrl+A → Scale` on all objects, after making all meshes single-user via `Object → Relations → Make Single User → Object & Data`) so export scale = 1.

**In CPW:**
1. Drag the FBX root GameObject from the Project window onto the **Model** field. The helpbox shows how many segment meshes were found and how many convex collider children will be created.
2. Leave **Mesh** and **Copy Mesh From** blank — CPW automatically uses the non-segment mesh from the FBX as the root visual mesh.
3. Fill in Part Name, SP Material template, and other fields as normal.
4. Click **Create Part Prefab**.

**Result structure** (matches the Engine Bell pattern):
- **Root GO** — MeshFilter (original mesh) + MeshRenderer + MeshCollider (`isTrigger = true`, full hull for scanner/interaction) + StructurePart + EntityBlueprintComponent + AddressableSOLoader
- **`PartName_Col_00` … `_Col_N`** — one child per segment, each with a convex MeshCollider (`isTrigger = false`) for physics and cutting

---

### 1.4 Editing Tools

#### `Shipbuilder / Component Copy Window`

A two-tab editor window for diffing and copying data between GameObjects or materials.

**Tab 0 — Component diff/copy:**
- Set a Source GO and a Target GO
- The window shows all components on each, highlighted where they differ
- Check the components you want to copy and click Copy
- ACL (`AddressableComponentLoader`) entries are handled specially — the tool merges entries rather than overwriting, so existing registrations on the target are preserved

**When to use:** After baking a new part variant that is similar to an existing one — copy the `StructurePart`, `EntityBlueprintComponent`, and ACL entries from the working part rather than setting them up from scratch.

**Tab 1 — Material shader diff/copy:**
- Set a **Source GO** (snapshot is taken immediately; source survives prefab context exit)
- Set one or more **Target GOs** — the tool resolves and displays the actual material asset(s) used by each GO
- Shows which shader properties differ between the source snapshot and the first target material
- All properties default to **unchecked** — select only what you want to copy
- **Shader guard:** if the source and any target use different shaders the copy is blocked and the mismatch is shown. Do not copy properties across different shaders — keyword semantics differ and the result will mangle the target material (e.g. adding `_PIXEL_DISPLACEMENT` to an HDRP Lit mat breaks its depth appearance)
- Click **Apply** to write selected properties to all unique target materials at once

**When to use:** After copying SP/BP components from a working game part to a custom baked part, the custom mesh's material is not updated — it retains its Blender-origin values. If the part glows in-game, use this tab to compare the glowing material against a non-glowing game part that uses the same Lynx shader and copy the differing float values and keywords. See [Persistent glow after SP/BP copy](#persistent-glow-after-spbp-component-copy) in Caveats.

**Addressable detection:** The tool identifies a GO as an addressable loader (not a baked part) by the presence of an `AddressableLoader` component — not by the absence of `StructurePart`. Keep this in mind if you see unexpected classification.

**SP/BP summary (always visible, Tab 0):** Below the SP/BP name field, a compact summary shows key gameplay fields pulled from `sp_bp_fields.json` (the reflected dump PartInfoLogger writes every game session) without needing to expand "Preview captured fields":
- SP: Cut grade/stage, Mass density, Joint setup, Room sealing, Vaporize on cut, Yank lock on start, Breakable by grapple
- BP: Fuel control, Cryo control, Salvage destination (color-coded Furnace/Processor/Barge), Salvage value per awarded currency (min-max, flagged `(mass-based)` if scaled by part mass), and **Electrical control** (Global/Local — highlighted orange when Global is present, since that's what triggers the [power-state glow bug](#persistent-glow-after-spbp-component-copy))

This summary requires PartInfoLogger to have run at least once with the current DLL (rebuild + relaunch the game) so `sp_bp_fields.json` is up to date; the window re-reads the file automatically whenever its write time changes.

---

#### `Shipbuilder / Bake Addressable In Place`

Loads the addressable asset referenced by a selected scene GO, bakes it into a standalone prefab (same process as Import Game Part Wizard → Bake), and replaces the original loader GO in the scene with the baked prefab — preserving world position and rotation.

**Also available as:** `GameObject / Shipbuilder / Bake Addressable In Place` (right-click in the hierarchy)

**When to use:** You placed a part using Import Addressable but later decided you need to rescale it or modify its components. Rather than re-importing, bake it in place.

**Caveat:** After baking, you still need to **Register Components in Parent ACL** (see [context menus](#22-gameobject-hierarchy-right-click)) if the baked GO is inside a prefab that has an `AddressableComponentLoader`.

---

#### `Shipbuilder / Swap Prefab In Scene`

Replaces one prefab instance in the scene with another, compensating for transform differences between the two prefabs (pivot offsets, default scale). The new prefab inherits the original's world position and rotation.

**When to use:** You want to swap one hull panel variant for another across a section, or replace a placeholder mesh with the final baked part.

---

#### `Shipbuilder / Lock In Rescale — Selected`

Bakes the current non-unit transform scale into the mesh geometry and resets the GO's `localScale` to `(1, 1, 1)`. Works on the selected GO and all descendants.

**Also available as:** Inspector button on `AddressableComponentLoader` (shown when non-unit scale is detected), right-click on Transform component, and right-click in the hierarchy.

**Why this is required:** The game reads joint anchor points and part mass directly from mesh vertex positions in world space. If a GO has a non-unit scale, the transform applies visually in the editor but the game's systems see the un-scaled mesh — joints and mass will be wrong. Locking in the rescale writes the scale into the vertex data so the game sees the correct geometry.

**How it works internally:**
1. Captures all child world positions
2. Resets the parent `localScale` to `(1, 1, 1)`
3. Restores child world positions (which causes their `localPosition` to recompute to the correct scaled values automatically)
4. Writes the scale into each `MeshFilter.sharedMesh` via vertex transformation

**Real example — a test ship's hull panels:** A Mackerel wall panel was baked and then scaled to 1.5× to fit the test ship's wider hull section. After scaling, Lock In Rescale was applied. The panel's joint and mass now correctly reflect the larger geometry.

#### `Shipbuilder / Lock In Rescale — All Rescaled`

Same as above but operates on every GO in the scene that has a `StructurePart` component and a non-unit `localScale`. Use this as a bulk fix when the validator reports multiple scale warnings. The **Auto-Fix & Build** button in the warning dialog runs this automatically and saves assets before proceeding to the build step.

---

### 1.5 Geometry & Layout Tools

#### `Shipbuilder / Joint Assist`

The most complex tool in the mod. Automates the placement of joint markers between parts so the game knows which parts are physically connected at spawn.

**Full workflow:**

1. **Pick a face on Part A:** Click a mesh face in the scene view — the tool highlights it and reads the face normal.
2. **Pick a face on Part B:** Click the mating face on the second part.
3. **Snap:** The tool calculates the transform needed to bring Part B flush against Part A (zero gap, face normals anti-parallel). Click Snap to apply.
4. **Check compatibility:** The tool shows a compatibility summary — whether the two parts' `JointSetupAsset` types are able to joint at runtime.
5. **Place joints:** Click Auto-Place InvisibleJoint to drop a joint marker that bridges the two flush faces. The tool deduplicates overlapping joints automatically.

**InvisibleJoint requirements:**
- Must be a solid collider (`Is Trigger: false`)
- Scale must be `(1, 1, 1)` — larger scales physically push parts apart before joints can form
- Parts must be flush (zero gap); the IJ straddles the contact plane at ~5 cm depth

**When auto-jointing is enough:** If both parts use compatible `SP_Mat` types (e.g. both are `SP_Mat_Panel_Ext_Nanocarbon`) their `JointSetupAsset` pairing allows them to auto-joint at spawn without an explicit InvisibleJoint — the proximity is enough. Use Joint Assist's compatibility check to confirm before placing unnecessary markers.

**How the JSA compatibility check works:** Each `StructurePartAsset` references a `JointSetupAsset` (JSA) by name (e.g. `JOINT_Chassis`, `JOINT_Panel_Ext`). The check resolves the JSA for each selected part via four paths in priority order:

1. `AddressableSOLoader.refs[0]` on the GO — used for custom baked parts whose SP_Mat GUID is stored directly
2. The ancestor `AddressableComponentLoader` (ACL) — walks up the hierarchy to find the ACL, then looks up the SP_Mat address stored for this GO's `StructurePart` entry. The SP_Mat GUID is resolved via `known_assets.json` (GUID → filename), then the filename is matched to a JSA name via longest-prefix match against the known JSA table (e.g. `SP_Mat_Chassis_CutPoint_Grade0_VaporizeJointEverything` → `JOINT_Chassis_CutPoint`)
3. `AddressableLoader.assetGUID` — for game addressable parts, looks up the prefab GUID in the enriched data
4. Prefab name / asset GUID fallback — matches GO or prefab name against `known_assets_enriched.json` part names

The JSA compatibility table (`jsa_compat.json`) is populated by **PartInfoLogger** at runtime: on gameplay start, it reflects the complete `JointabilityAsset.m_AllPairingAssets` table (351 pairs, 55 JSA names as of this writing). SP_Mat filenames have decoration suffixes that do not appear in JSA names — the longest-prefix match strips these correctly.

#### Joint Compatibility Check (JCC)

A separate panel in the same window that predicts whether two selected parts will actually auto-joint in-game, without needing to build and test. Select two (or two groups of) `StructurePart`/`FakeStructurePart` GameObjects and click **Check**.

The panel reports three independent results, all of which must pass for the parts to auto-joint:

1. **SP_Mat** — whether the two parts' `JointSetupAsset` types are compatible (see the JSA compatibility check above).
2. **MJC** — whether the two parts share a `MandatoryJointContainer` ancestor (bypasses the JSA/Mesh requirement entirely if so).
3. **Mesh** — the actual result of the game's real jointing algorithm (`BBI.Unity.Game.JointHelper.TryFindJointPolygonsJob`), reimplemented faithfully: a brute-force search for coplanar, near-opposite triangle pairs between the two parts' render meshes, then a 2D convex-hull clip of the matched region. This is the same geometry test the game runs at spawn — a **Pass** here means the parts will physically joint if placed this way.

**Thresholds** (top of the panel):
- **Distance Threshold (m)** — how far apart two faces' planes can be and still count as coplanar. Game defaults: `0.1` (general/runtime jointing) or a stricter `0.05` (ship-spawn/generation).
- **Angle Threshold (°)** — how far off from exactly opposite two faces' normals can be. Game defaults: `~36.9°` (runtime) or a stricter `~25.8°` (ship-spawn).

Set both to match whichever in-game scenario you're testing against.

**Reading a Fail result:** if the Mesh row reports Fail, the message includes a **Closest** near-miss line — the single closest candidate triangle pair found anywhere in the whole selection, and by how much it missed:
- Yellow text = an angle near-miss (the two faces were close enough together, but not opposite enough).
- Red text = a distance near-miss (the two faces were angled correctly, but too far apart) — this also covers the rarer case where every individual triangle pair passed both tests but the two parts' full matched regions don't line up as a whole.

The scene view highlights this same closest pair: green highlights are a real coplanar match (the parts will joint); a colored triangle pair (yellow/red) or a purple arrow marks the near-miss and, for the arrow case, the actual direction one part needs to move to close the gap.

**Triangle-pair budget:** the coplanar search is brute-force (every triangle in A against every triangle in B), so it's capped at 2,000,000 triangle-pair comparisons per MeshFilter pair to keep the Editor responsive on large/complex parts. If a pair hits this cap, its result may be a false negative — a dialog will offer to re-run the Check with a larger budget (sized to exactly what that pair needs, plus 10% headroom). The progress bar shows real triangle-pair throughput while a scan is in progress, and **Cancel** takes effect within a fraction of a second even mid-scan on a single large pair.

---

#### `Shipbuilder / Radial Duplicate`

Duplicates selected GameObjects radially around a chosen axis (X, Y, or Z) or a reference axis defined by another GO. Enter the count and angle, optionally rotate room volumes proportionally.

**When to use:** Creating cylindrical structures like a test ship's octagonal hull rings — place one segment, then radially duplicate 8× at 45° around the ship's long axis.

**Caveat — double children after duplication:** If both a parent and one of its children are selected when you run Radial Duplicate, the child will be duplicated twice per copy (once as part of the parent's Instantiate, once on its own). The tool filters to topmost-only roots automatically, but this can still happen if you manually multi-select a parent and a descendant. If you see double GUID-named children after duplicating, use **Remove Duplicate Non-Addressable Children** (below) to clean them up.

---

#### `Shipbuilder / Remove Duplicate Non-Addressable Children`

Scans the selected object's subtree for children whose names are GUIDs (32 hex characters) and removes any duplicate that lacks an `AddressableLoader` component — keeping the addressable original (the `+` icon in the hierarchy) and removing the plain instantiated copy.

**When to use:** After Radial Duplicate produces double GUID-named children under addressable parts (e.g. cryo pipe PRF_ objects with two identical GUID children instead of one).

**How it works:**
1. Dry-runs the subtree and shows a confirmation dialog listing every object to be removed.
2. On confirm, unpacks any prefab instances that contain the duplicates (required by Unity to allow child deletion), deletes the duplicates, then re-instantiates the unpacked prefab roots from their source prefab asset to restore the prefab link.

**Caveats and recovery:**

- **Prefab instance overrides are lost on re-link.** The re-link step replaces each unpacked prefab root with a clean `InstantiatePrefab` call. Any instance-level overrides (e.g. modified component values on the prefab root) that existed before the operation will not be carried over. Overrides on objects *inside* the prefab that were not themselves prefab roots are unaffected.

- **If re-linking fails and prefab links are broken**, the affected objects will appear as plain GameObjects in the hierarchy (no blue prefab icon, no Prefab submenu on right-click). To recover:
  1. **Check undo first** (`Ctrl+Z`) — the operation is wrapped in a single undo group and may roll back cleanly if no other edits were made after.
  2. **If undo is not viable**, drag the affected root GameObject from the hierarchy onto its existing prefab asset in the Project window. Unity will ask whether to replace the existing prefab — confirm. This re-establishes the prefab link and overwrites the asset with the current scene state (which is correct, since the duplicates are already gone).
  3. **To identify which prefab asset to target**, look at the object's name — it typically matches a `.prefab` file under `Assets/_CustomShips/<ShipName>/Prefabs/`. If the object was a nested prefab inside the ship prefab (e.g. a cryo pipe layout), find the corresponding prefab by name in the Project panel and drag onto it.

- **Manual deduplication (if the tool cannot be used):** In the hierarchy, expand the affected PRF_ object. For each GUID-named child that appears twice, identify the one *without* the `+` icon (the plain cube) and delete it manually. The `+` icon indicates `AddressableLoader` is present; that is the one to keep. Do not delete the `+` variant — it will be restored by the next asset refresh and the plain duplicate will reappear.

---

#### `Shipbuilder / Segment Meshes`

Auto-generates radial collider wedge segments on a prefab for cylindrical geometry. Enter the segment angle (e.g. 45° for 8 segments) and the tool creates child collider GOs rotated appropriately.

**Why this exists:** Convex colliders on a full cylinder fill the hollow interior. By splitting the cylinder into wedge-shaped segments and making each one convex, the correct hollow shape is preserved in the physics system.

**Real example — a test ship's nozzle:** The nozzle ring was exported as a single mesh but its collider was split into 8× 45° wedge children using Segment Meshes. Each wedge's convex collider covers the outer wall without filling the interior.

---

#### `Shipbuilder / Mesh Curve Deformer`

Replaces a straight cylinder mesh with a parametric biarc tube — two circular arcs joined tangentially — that departs flush from one face and arrives at another. Useful for curved pipe runs, conduit bends, or any curved connector between two flat faces.

**Inputs:** Source face transform, destination face transform, segment count, radius. The tool solves the biarc geometry (tangent-joined arcs from the face normals) and outputs a new mesh.

**Segment count:** Increase this when the curve is tight. The tool splits the curve into segments so each one remains convex when converted to a convex MeshCollider — too few segments on a tight curve produces a concave shape that fails the convex collider requirement.

---

#### `Shipbuilder / PB Vertex Follower`

Locks ProBuilder mesh vertices to the surface of a target part, then moves those vertices whenever the target moves. Supports multiple meshes simultaneously with per-mesh subdivision control.

**When to use:** When you have a custom trim or gasket mesh that needs to conform to the surface of an adjacent baked game part. Instead of manually adjusting vertices every time you reposition the part, the follower tracks the target automatically.

**Corner rule:** A vertex is classified as a corner only when it touches exactly two face edges (logical AND, not OR). Subdivision is applied to corner vertices differently.

---

### 1.6 Visualization

#### `Shipbuilder / Render Overlays`

Opens a configurable overlay window that draws debug gizmos in the scene view. Each overlay's color is configurable in the window (defaults shown are just a starting point):

| Overlay | What it shows |
|---|---|
| Room SubVolumes | Include volumes (pressurized space) |
| Room SubVolumes (Exclude) | Exclude volumes (carve-outs) |
| Room Openings | Flow axis direction on each opening (shown as arrows) |
| Joints | Joint marker positions, color-coded by type |
| Joint collision | Collider bounds of each joint (shown as an outline) |

**When to use:** When diagnosing pressurisation issues, room overlap problems, or unexpected de-pressurisation on ship load. The overlays make the invisible room volumes visible without having to click into each prefab.

**Caveat — room overlaps:** If you see instant de-pressurisation when loading your ship, check the Overlap opening volumes (shown in the overlay). An extra or misplaced Overlap volume that intersects a SubVolume Include region causes the room to register as breached immediately. See [Room Caveats](#rooms-caveats) for the atmosphere regulator probe issue.

#### `Shipbuilder / Visibility Tool`

Hides or shows parts in the Scene view in bulk, so you can work inside a crowded ship: around one room, one deck, or everything matching a name. It uses Unity's own scene visibility (the eye and hand icons in the Hierarchy), so it **never** switches GameObjects off. The scene and prefab are not marked changed, and the build is the same.

A *part* here is any saved object that draws something: it has a Renderer, or it is an addressable part whose preview mesh draws. A part's preview mesh is always hidden and shown together with the part.

**Scope (top of the window)**

| Setting | What it does |
|---|---|
| Affect | **Visibility** hides and shows parts (eye icon). **Picking** locks and unlocks them so clicks in the Scene view pass through (hand icon). Every button below follows this setting. |
| Limit to | Only act on parts under this object. Leave it empty to act on the whole open scene, or on the prefab you are editing in Prefab Mode. |
| Include non-rendering | Also count objects that draw nothing (rooms, collider-only objects, empty groups). |
| Show all / Hide all / Invert | Act on every part in scope. |
| Back | Undoes the last action of this window (up to 30 steps). Unity's Undo does not track scene visibility; use this instead. |
| Re-hide previews | Hides the preview meshes of hidden parts again. This also runs by itself after every preview refresh, so you rarely need it. |

**Selection**: hide or show the selected parts, everything *except* the selection, or **Isolate selected** (show the selection, hide the rest). With **Include children** on, selecting a group counts every part under it. Clicking a preview mesh in the Scene view counts as selecting its part.

**By distance**: acts on parts within a radius.

| Setting | What it does |
|---|---|
| Centre | **Selection** (follows what you select), **Scene View Pivot** (the point the Scene view orbits), or **Fixed Point** (type it in, or click **From selection**). |
| From selection surface | With Centre = Selection: on, the radius is measured out from the selection's outer box, so a large selection keeps its neighbours; off, from its centre. |
| Measure to part pivot | Off: a part is inside when any of its box is within the radius. On: only its pivot point counts. |
| Radius (m) | Slider; the box to its right sets the slider's maximum. With **Draw radius** on, you can also drag the radius in the Scene view. |
| Keep selected on | Distance actions never hide the selected parts. |
| Live isolate | Re-runs **Isolate within radius** whenever the radius, centre or selection changes. Select a part and everything near it shows, the rest hides. |

Buttons: **Isolate within radius**, and hide or show the parts **inside** or **outside** the radius.

**By name or component**: type comma-separated terms in **Contains** (not case-sensitive). A part matches if any term appears in its name (or, for an addressable part, its preview's name), or with **Match = Component** in one of its component type names. Put `!` before a term to exclude parts that match it, e.g. `pipe, !bracket`. Then **Hide**, **Show**, **Isolate**, or **Select** the matches.

**When to use:** Reaching parts buried inside the hull, checking one room's furniture without the walls in the way, or locking big hull panels so clicks reach the small parts behind them (Affect = Picking, select the panels, **Lock selected**).

**Caveats:**
- If the window warns that scene visibility is switched off in the Scene view toolbar, hidden parts still draw. Click **Switch it on**.
- The hidden state is saved per user in `Library/` and is never committed or built. If a part seems to be missing from the Scene view, check the Hierarchy eye icon before anything else, or click **Show all**.
- Hidden parts still exist for every other tool, the build and the game. Hiding is only a view.

---

### 1.7 Utility

#### `Shipbuilder / Set Display Name on Selected…`

Batch-renames the display name field on the `StructurePart` component of all currently selected baked prefab GOs. Opens a dialog to enter the new name.

**When to use:** After baking several variants of the same panel and wanting to give each a distinct in-game label.

#### `Shipbuilder / Organize Ship Folder…`

Scans the selected ship folder and moves loose asset files into type-named subfolders: `Models/`, `Textures/`, `Data/`. Organized by file extension. Run this when a ship folder has grown messy with files in the root.

#### `Shipbuilder / Show Game Inspector`

Opens the Game Inspector Window — a search panel where you can paste a part GUID (from `known_assets.json`) and load the corresponding game prefab into the editor for inspection without placing it in the ship scene.

**Workflow for inspecting a game part:**
1. Open `known_assets.json` (in the project root), find the prefab entry, copy its GUID
2. Open the Game Inspector (`Shipbuilder / Show Game Inspector`)
3. Paste the GUID into the `Asset GUID` field and click `Load GameObject`
4. Inspect the loaded hierarchy, component values, and mesh structure

**Example:** To inspect the Mackerel airlock: search `known_assets.json` for `PRF_Mackerel_Airlock`, copy `fd038d23f35b59747a22dec2f214b11f`, paste into Game Inspector.

---

## 2. Context Menus

### 2.1 Transform Component (right-click the gear icon)

#### `Lock In Rescale — Selected`

Same as `Shipbuilder / Lock In Rescale — Selected`. Right-clicking the Transform component gear and choosing this bakes scale into mesh geometry for the selected GO and its descendants.

#### `Copy World Placement`

Copies the selected GO's **world** position and rotation to a temporary clipboard (not the system clipboard — internal only).

**Primary use:** Positioning a ship in the bay. Select your ship prefab root, use the Unity move tool to position it in the bay scene, then right-click the Transform → `Copy World Placement` to capture that position before resetting the prefab root back to `(0,0,0)`.

#### `Paste World Placement`

Pastes the previously copied world position and rotation onto the selected GO.

**Bay positioning workflow:**
1. In `SampleScene`, position your ship prefab where you want it to appear in the bay
2. Right-click the ship prefab's Transform → **Copy World Placement**
3. Double-click to open the ship's `RootRef` prefab (inside the `Spawn/` folder)
4. Select the `Hardpoint` child GO
5. Right-click its Transform → **Paste World Placement**
6. Exit the prefab editor (click the back arrow)
7. Reset your ship prefab's position to `(0, 0, 0)` in the scene

**Seeing the jack lights and the barge while you place the ship:** the editor's bay preview does not include the five floating jack lights or the barge. `Assets/Editor/BayRecorded/BayRecorded.prefab` is a record of them taken from the running game; drag it into the scene while positioning. The same folder has a script that renders a four-view picture of the ship at the Hardpoint pose. Step-by-step instructions are in `Assets/Editor/BayRecorded/Source~/README.md`.

---

### 2.2 GameObject Hierarchy (right-click)

#### `GameObject / Shipbuilder / Bake Addressable In Place`

Right-click a GO in the hierarchy that has an `AddressableLoader` component → bakes the referenced addressable into a standalone prefab and replaces this GO in-scene. See [Bake Addressable In Place](#shipbuilder--bake-addressable-in-place) above.

#### `GameObject / Shipbuilder / Duplicate AddressableLoader`

Right-click a GO that has an `AddressableLoader` → creates a duplicate loader GO in the scene pointing to the same source GUID. Useful when you want two instances of the same addressable part (e.g. two identical door frames) without going back through the Import Wizard.

#### `GameObject / Shipbuilder / Bake ProBuilder Children`

Right-click a GO that contains ProBuilder mesh children → bakes all ProBuilder meshes to standard `MeshFilter` / `MeshRenderer` with convex `MeshCollider` components attached. Run this before building to ensure ProBuilder geometry is correctly represented in the game's physics system.

#### `GameObject / Shipbuilder / Register Components in Parent ACL`

The most frequently needed context menu item. After duplicating or adding a GO under a prefab that has an `AddressableComponentLoader`, the new GO's `StructurePart` and `EntityBlueprintComponent` are not registered — the game will not load their data at runtime.

**Workflow after duplicating a part:**
1. Duplicate the GO (`Ctrl+D` or right-click → Duplicate) and reposition it
2. Right-click the new GO → **Register Components in Parent ACL**
3. The tool walks up the hierarchy to find the nearest `AddressableComponentLoader`, then scans all `StructurePart` and `EntityBlueprintComponent` under your GO
4. For each found component, it matches by name (stripping copy suffixes like ` - 1` or ` (1)`) to find the correct asset address from:
   - The parent ACL itself (fastest)
   - Other ACL instances open in the scene
   - All prefab assets on disk
5. If a single match is found it auto-confirms; if multiple, a picker window appears
6. Entries are appended to the ACL — existing entries are not disturbed
7. Save the prefab (`Ctrl+S`)

**Caveat:** The parent ACL must already have at least one entry of the same component type as a reference. If you are adding a completely new component type with no existing ACL entry, the tool will tell you rather than insert a broken entry — you will need to set up that entry manually.

**Why copying does not copy addressable wiring:** Unity's Ctrl+D duplicates the GO and all components, but the asset addresses live in the parent's ACL (`componentValues` array), not on the component itself. The duplicated GO has no entry in any ACL until you register it.

#### `GameObject / Shipbuilder / Lock In Rescale — Selected`

Same as above — bakes transform scale into mesh geometry for the selected GO and descendants.

#### `GameObject / Shipbuilder / Recenter Mesh to Origin`

**Manual fallback only — the real fix is a combination of a one-time Blender habit and a CPW code fix, both already in place.** See the "recenter each object's origin before exporting" prerequisite in [§1.1](#11-export-to-unity-fbx) (Set Origin to Geometry — do **not** follow with Apply All Transforms). Parts exported that way, baked through Custom Part Wizard, arrive in Unity already correctly assembled — this tool should rarely be needed. Use it by hand only on a part someone forgot to fix in Blender before export.

Right-click a **leaf** part GO (mesh + collider + `StructurePart`/`EntityBlueprintComponent` all on the same node, no children — e.g. a plain mesh assigned SP/BP via Component Copy Window rather than baked through the Import Game Part Wizard) → moves the **mesh** to the GameObject's existing origin, in place. The GameObject's `transform.position` is never touched.

**Symptom this fixes:** cut/explosion FX spawning far away from the visible mesh instead of at the cut location. Root cause: the mesh's Blender-authored object origin sits far from its own geometry (e.g. down near an unrelated part of the ship), so while the renderer draws the mesh where its vertices are, the GameObject's actual `transform.position` — and the game's FX spawn point, which reads that transform — sit wherever the bad origin is. Parts baked through the Import Game Part Wizard never hit this because that pipeline always recenters automatically (`RecenterChildren`); parts added via Component Copy Window / "Assign StructurePart/Blueprint by name" onto a raw imported mesh do not.

**How it works:**
1. Computes the mesh's bounding-box center as the offset.
2. Mutates the mesh asset's vertices **in place** (no clone) — shifts every vertex by `-offset` so the geometry is now centered on the mesh's own local origin.
3. `transform.position` is left untouched, since the GameObject is presumed already correctly placed — this run only pulls the mesh back to where the origin already is.

**Caveat — mutates a shared asset, not a clone:** because this edits the mesh asset directly rather than cloning it, if the same mesh sub-asset is referenced by more than one GameObject (e.g. repeated tower/leg instances baked from one FBX), only the *first* `Recenter()` call actually shifts vertices — later calls on sibling GOs see it already centered and no-op. That's fine since position is never touched here, but it means running this on one instance affects every GameObject sharing that mesh.

**Works on a parent GO too:** select a parent object (e.g. a whole assembly) instead of a single leaf part, and the command expands to every descendant `MeshFilter`, recentering each one as if it had been selected individually. Scope is always exactly the selected object(s) and their descendants — it never touches anything else in the scene.

**Not durable across reimport:** editing an FBX sub-mesh's vertices in memory doesn't survive Unity's next reimport of that FBX — sub-meshes are rebuilt from the file each time. Re-run this manually after a re-export if a part still needs it. There is **no automatic version of this anymore** — an on-reimport auto-trigger was built, tested, and then deliberately deleted (not just disabled) once the real root cause was found and fixed in `CustomPartWizard.cs` (see below); automating this in Unity had already proven fragile and wasn't needed once the underlying bug was actually fixed.

**Related but different:** `GameObject / Shipbuilder / Recenter Pivot to Mesh (Keep World Position)` does the opposite job for a **parent** GO — it moves the **origin** to the mesh by shifting the parent's **children's** local positions and compensating the parent's world position so nothing visually moves. It requires the target to have child Renderers and cannot fix a leaf part with no children (like the case above).

#### Why parts used to misassemble after Origin to Geometry (fixed in CPW)

Custom Part Wizard's FBX-submesh bake mode used to place every segment GameObject at **identity local transform**, reading only each sub-mesh's raw vertex data and ignoring the source FBX node's own transform entirely. That happened to work as long as every part in a ship's FBX shared one common Blender-scene origin — but the moment a part got its own individually-recentered origin (via Origin to Geometry, above), its vertex data became relative to a *different* local origin than its siblings, and CPW's identity-transform assumption broke: parts would collapse toward each other in Unity instead of assembling at their correct relative positions.

Fixed: CPW now also reads each sub-mesh's own `localPosition`/`localRotation`/`localScale` from the imported FBX hierarchy and applies it to the corresponding segment GameObject. This is what makes the Blender-side Origin to Geometry habit actually work end to end — do both, not just one or the other.

---

### 2.3 Assets Panel

#### `Assets / Copy GUID`

Right-click any asset in the Project window → copies its GUID to the system clipboard. For assets in `EditorCache`, copies the original game GUID rather than the local Unity asset GUID.

**When to use:** When you need a GUID to paste into an `AddressableSOLoader` `Refs` field, an `AddressableLoader` GUID field, or a component picker.

#### `Assets / Shipbreaker / Set Display Name…`

Right-click a baked prefab asset in the Project window → opens a dialog to set the display name on its root `StructurePart` component. Same as the hierarchy menu item but operates on the asset file directly.

#### `Assets / Create / Shipbreaker / Create room asset`

Creates a complete room prefab setup: a `RoomContainerDefinition` + `RoomSubVolumeDefinition` prefab, plus the `ModuleEntry` and `ModuleList` scriptable objects it needs. The result is a ready-to-place room prefab.

**Note:** For most ships, using the `DummyPlugRoom` workflow (see [Section 4.2](#42-adding-a-room-dummyplugroom-workflow)) is easier than creating room assets from scratch.

#### `Assets / Create / Shipbreaker / Create hardpoint asset`

Creates the module list a `HardPoint` component points at: one weighted entry per prefab, an optional "spawn nothing" entry, the container and the list, and adds the list to the default Addressables group. With a game property GUID in **Only on ships with this property**, the entries spawn only on ships that have that property (for example ghost ships). Used for ship spawn points and for anything that should appear only sometimes or only on some ships. Step-by-step use is in [Section 14](#14-hardpoints-and-module-lists).

#### `Assets / Create / Shipbreaker / Create level asset`

Creates a level asset with the appropriate scriptable object structure for defining a custom ship level.

---

## 3. Working with Game Parts

### 3.1 Bake vs Import Addressable

Open the wizard via `Shipbuilder / Import Game Part Wizard`. Search for the part by name, then choose how to import it:

**Bake (recommended for structural parts)**

Baking downloads the addressable asset and converts it into a self-contained prefab in your project. The geometry, colliders, StructurePart, and EntityBlueprintComponent are all embedded — no runtime address lookup required.

Use baking when:
- You need to **rescale** the part
- The part is purely structural (wall panels, floor tiles, hull pieces)
- You want a fully independent prefab you can modify freely

Do not bake when:
- The part has **animations or complex runtime behaviour** (doors, airlocks, reactors)
- The part loads child content via its own `AddressableSOLoader` at runtime

**Import Addressable**

Places a lightweight loader GO in your hierarchy that tells the game to stream the part at runtime.

Use this when:
- The part needs to remain a live addressable (animated doors, functional subsystems, powered parts)
- You are not rescaling it
- You want the game to manage its lifecycle exactly as it would on a stock ship

---

### 3.2 Repositioning and Rescaling

After placing a baked part, reposition and rotate freely using standard Unity transform tools.

If you **rescale** a baked part, the game's joint and mass systems will not see the scaled geometry — they read directly from mesh bounds and vertex data. Lock in the scale before building:

1. Select the part in the hierarchy
2. The **Addressable Component Loader** Inspector will show a **Lock In Rescale** button when non-unit scale is detected
3. Alternatively: right-click the Transform component → **Lock In Rescale**, or right-click the GO in the hierarchy → **Shipbuilder / Lock In Rescale — Selected**
4. The scale writes into the mesh geometry and the transform resets to `(1, 1, 1)`

The pre-build validator will warn if any part with a `StructurePart` component still has non-unit scale.

---

### 3.3 Duplicating a Part

When you duplicate a part GO (`Ctrl+D`), the new GO's components are not wired into any `AddressableComponentLoader`. The game will not load their asset data and the part will not appear in-game correctly.

After duplicating:
1. Reposition as needed
2. Right-click the new GO → **GameObject / Shipbuilder / Register Components in Parent ACL**
3. Save the prefab

> If you are adding parts to a **nested prefab instance** (indicated by the `>` arrow badge in the hierarchy), prefer opening that nested prefab in isolation and making the addition there. Otherwise your new GOs are prefab overrides and may not behave correctly at runtime.

---

### 3.4 Deleting a Part

When you delete a part GO, any `AddressableComponentLoader` on its parent that referenced components on that GO will have **null/missing entries**. These cause a crash at runtime.

After deleting:
1. Select the parent GO that holds the `AddressableComponentLoader`
2. In the Inspector, expand each entry — entries showing **Missing Component** are dead references
3. Remove each missing entry using the `−` button
4. Save the prefab

The pre-build validator catches remaining null ACL entries as an **error** and blocks the build.

---

### 3.5 Pre-Build Checklist

Before running `Shipbuilder / ⛭ Build`, confirm:

- [ ] All rescaled parts have had **Lock In Rescale** applied (transform reads `(1,1,1)`) — or use **Auto-Fix & Build** in the warning dialog to handle this automatically
- [ ] All deleted parts have had their **ACL entries removed** from the parent loader
- [ ] All duplicated or new parts have been **registered in the parent ACL**
- [ ] The validator reports no errors and no scale warnings

---

## 4. Rooms

### 4.1 Components Overview

#### RoomContainerDefinition

Defines how the room behaves. Set the room type via the `Dynamic Room Container Asset` field using the GUIDs in [Section 4.3](#43-room-type-guids).

#### RoomSubVolumeDefinition (shown as boxes in the overlay)

Defines the pressurised volume.
- At least one `Include` mode volume is required
- `Exclude` mode volumes carve out space from Include volumes
- The `Center` field is a world-space offset applied after the parent Transform — edit it in the Inspector

#### RoomOpeningDefinition (shown as boxes in the overlay)

Defines how volumes connect. The `Type` field:

| Type | Name | Behaviour |
|---|---|---|
| 0 | Block | Wall — no air flow |
| 1 | Portal | Door opening. Game hatch prefabs already include their own portal, so you do not need to add one manually. |
| 2 | Overlap | **Required** wherever two different rooms' SubVolume boxes intersect in 3D space |

The overlay's arrows show the **flow axis** — which direction air flows when the opening is breached.

**Overlap caveat:** Extra or misplaced Overlap volumes cause instant room de-pressurisation when loading. The room volume debug overlay (`Shipbuilder / Render Overlays`) makes overlaps visible.

---

### 4.2 Adding a Room: DummyPlugRoom Workflow

Rooms must live inside an **addressable prefab with a ModuleDefinition** component. A plain GameObject in the ship hierarchy will not have its `AddressableSOLoader` fired at runtime and the room type will not load. The `DummyPlugRoom` prefab (`Assets/_CustomShips/FirstShip/Components/DummyPlug/DummyPlugRoom.prefab`) is a ready-made container.

**Each room needs its own separate prefab.** Do not reuse the same DummyPlugRoom for multiple rooms — all instances share the same prefab data, so editing one edits all.

**To add a new room:**
1. In the Project window, duplicate `DummyPlugRoom.prefab` (`Ctrl+D`) and give it a descriptive name (e.g. `AftSectionRoom.prefab`)
2. Ensure the new prefab is in the Addressables group for your ship (open the Addressables Groups window, drag it in, or enable the Addressable checkbox in the Inspector)
3. Place the new prefab instance in your ship hierarchy where the room should be
4. To edit room volumes with the ship visible: click the **arrow** on the prefab instance to enter **Prefab Mode In Context** — the ship mesh stays visible. Do not use isolation mode for room volume editing.
5. To add a new SubVolume child: **copy** the existing `Volume` object while in the outer scene context first, then enter prefab mode in context and **paste**. Dragging across prefab boundaries is not supported.
6. Edit `Center` and `Size` on each component in the Inspector. Run `Shipbuilder / Actions / Force View Refresh` to update the gizmos.
7. Save the prefab (`Ctrl+S` while in prefab mode, or click the back arrow)

**Do not unpack the room prefab instance** into the ship hierarchy — the `AddressableSOLoader` will no longer fire at runtime.

#### Room probe and atmosphere regulator caveat

The game's `RoomProbeInitializeSystem` assigns each `RoomProbe` to a room at runtime. The probe must land cleanly inside a `RoomSubVolumeDefinition` Include volume — not inside an Exclude volume, and not inside a `RoomOpeningDefinition` Overlap volume straddling a boundary.

**Real issue discovered while building a test ship:** Every baked `PRF_Panel_4xBx4` has a `RoomOpeningDefinition` with `Type = Overlap` and a default Size of `(3, 1.25, 3)`. When a wall panel is positioned adjacent to an atmosphere regulator, that 3×3 overlap volume can cover the regulator's probe position — preventing the probe from being claimed by the room, which breaks the interaction button entirely.

**Fix:** Shrink the Overlap volume on the offending panel (a Size of approximately `(1, 0.2, 1)` is enough to fulfil the overlap requirement without swallowing the probe).

---

### 4.3 Room Type

The room type controls pressurisation state and how the game categorises a room for salvage scoring. It is set on the `AddressableSOLoader` component of the `Room` child object.

**To set the room type:** right-click the `Addressable SO Loader (Script)` component header → `Set Room Type` → choose from the submenu. The GUID is written into `Refs → Element 0` automatically. No manual GUID entry required.

Pressurisation behaviour by type:

| Pressurisation | Room Types |
|---|---|
| Pressurised | Cabin, Cockpit, CrewQuarters, Habitation |
| Always unpressurised | ThrusterRoom (unpressurised variant) |
| Mixed (random each run) | All others |

---

### 4.3.1 Light Fixture Configuration

Addressable light fixtures (e.g. `PRF_Light_LowSodium_*`, `PRF_Prop_LightBank_*`) are colored at runtime by MSL based on properties set on their `AddressableLoader` stub GO. Without these, lights default to purple.

**To configure lights:** open `Shipbuilder / Addressable Light Config`. Select one or more fixture stub GOs in the Hierarchy — the window lists all `AddressableLoader` components found. Use the batch controls to set all at once, or configure each fixture individually.

#### Room Type

Sets the color palette the fixture draws from. Each fixture prefab has a built-in `DynamicLightColorAsset` (e.g. Industrial = warm orange, Commercial = blue, Science = cool blue) — the room type selects which entry in that palette to use. The window shows a color preview swatch for each fixture based on the room type you select.

- **Random** (default when left empty) — MSL falls back to Cockpit palette color
- Any named room type — fixture uses that palette entry; the window previews the resulting color

The `AddressableLoader` inspector also has a **Set Room Type** dropdown for quick single-component edits.

#### Light Level Chances

Controls the probability of each fixture's state on ship spawn. These are randomised per-fixture using the ship seed, so results are consistent for a given ship but vary between ships.

| Field | Default | Effect |
|---|---|---|
| `Damaged Chance` | 0.2 (20%) | Fixture spawns flickering |
| `Broken Chance` | 0.1 (10%) | Fixture spawns off |
| Normal (implied) | remainder | Fixture spawns on, steady |

Set both to 0 to force fixtures always on. The sliders clamp automatically so the total never exceeds 100%.

---

### 4.4 Pressurisation State

Each `DynamicRoomContainerAsset` holds a weighted list of `RoomDataAsset` entries. Each entry carries a `PressurizationProbability`. There is no way to select a specific pressurisation variant by swapping a GUID — the asset controls the weighted random selection at runtime.

The only built-in way to guarantee a room starts unpressurised is `ThrusterRoomAlwaysUnpressurized` (`c3916206ca44e364eae1bad0e4fa602c`).

Note: pressurisation logic overrides all probabilities to 0 when loading from a save file (`wasLoadedFromFile = true`).

---

### 4.5 Custom Room Data Assets

If you want to control pressurisation probability, dust levels, or room name for a room — rather than accepting the game's built-in weighted random — you can create your own `DynamicRoomContainerAsset` and point your room's `AddressableSOLoader` at it instead of one of the GUIDs in [Section 4.3](#43-room-type-guids).

#### Asset types

Three ScriptableObject types are involved:

| Asset | Purpose |
|---|---|
| `RoomTypeAsset` | Declares the room type (e.g. Corridor, Cockpit). References one of the game's built-in room type assets. |
| `RoomDataAsset` | One concrete room variant: sets the `RoomTypeAsset`, `PressurizationProbability`, `DustLevelMin`, `DustLevelMax`, and localization IDs for the room name. |
| `DynamicRoomContainerAsset` | The top-level asset your `AddressableSOLoader` points to. Holds a weighted list of `RoomDataAsset` entries (with per-entry restrictions) and a fallback Default Room Data. |

#### Creating the assets

All three types can be created via `Assets > Create > Scriptable Objects`:

- `Assets > Create > Scriptable Objects > RoomTypeAsset`
- `Assets > Create > Scriptable Objects > RoomDataAsset`
- `Assets > Create > Scriptable Objects > DynamicRoomContainerAsset`

Create them in a logical folder (e.g. `Assets/_CustomShips/<ShipName>/RoomData/`).

#### Workflow

1. **Create a `RoomDataAsset`** — set the fields:
   - `Room Type`: reference one of the game's `RoomTypeAsset`s (load via Game Inspector using a GUID from [Section 4.3](#43-room-type-guids), inspect which `RoomTypeAsset` it references internally, then use that)
   - `Pressurization Probability`: `1.0` = always pressurised, `0.0` = always unpressurised, `0.5` = 50% chance
   - `Dust Level Min` / `Dust Level Max`: floats controlling random dust particle amount in the room
   - `Room Name` / `Short Room Name`: localization database IDs (e.g. `"Crawlspace"`, `"CRWL"`) — these must match strings in the game's localization database; you cannot invent new ones without a localization mod
2. **Create a `DynamicRoomContainerAsset`** — add your `RoomDataAsset` to the `Weighed Room Datas` list with `Weight = 1` and no `Restrictions`. Set it as the `Default Room Data` too.
3. **Make the asset addressable**: in the Addressables Groups window, add your `DynamicRoomContainerAsset` to your ship's addressable group. Copy its address (or its GUID via `Assets / Copy GUID`).
4. **Wire it up**: in your room prefab's `AddressableSOLoader`, set `Refs → Element 0` to the GUID of your `DynamicRoomContainerAsset`.

#### DynamicRoomContainerAsset fields

| Field | Type | Description |
|---|---|---|
| Weighed Room Datas | List\<WeightedRoomData\> | Each entry has a `Value` (RoomDataAsset), a `Weight` (relative probability when multiple entries are eligible), and `Restrictions` (Ship Properties required for this entry to be chosen — if any listed property is absent from the ship, the entry is skipped) |
| Default Room Data | RoomDataAsset | Fallback used if no weighted entry is eligible |

#### RoomDataAsset fields

| Field | Type | Description |
|---|---|---|
| Room Name | localized string | Localization DB ID for the full room name (e.g. `"Crawlspace"`) |
| Short Room Name | localized string | Localization DB ID for the scan-mode label (e.g. `"CRWL"`) |
| Room Type | RoomTypeAsset | Reference to the Room Type asset |
| Pressurization Probability | float | Chance (0–1) the room spawns pressurised |
| Dust Level Min | float | Minimum random dust particle amount |
| Dust Level Max | float | Maximum random dust particle amount |

---

## 5. Joints

### 5.1 How Auto-Jointing Works

The game joints parts together at ship spawn using two mechanisms. Understanding which applies to your parts determines whether you need to do anything extra.

The engine's decision per part-pair:

```
AreMandatoryJoints  OR  AreCompoundObjects  OR  jointingInfo.CanJoint(jsa1, jsa2)
```

**JointSetupAsset (JSA) pairing:** Each `StructurePartAsset` has a JSA. A `JointabilityInfo` table defines which JSA pairs are compatible. Parts in physical contact at spawn that have compatible JSAs auto-joint without any extra setup.

- Ship-level parts (panels, hull modules, reactor sets) have JSAs compatible with the ship's structural JSA → they auto-joint with the hull without any work
- Subsystem-internal parts (small components inside a reactor assembly) have JSAs only compatible with each other → they need `MandatoryJointContainer` to joint correctly when placed standalone

---

### 5.2 MandatoryJointContainer

A `MandatoryJointContainer` MonoBehaviour forces all descendant `StructurePart` components to share a mandatory connection, regardless of their `SP_Mat` type or JSA compatibility. The connection is still cuttable by the player.

**When to add one:** When you have a set of custom parts that need to be joined to each other but their SP_Mats are not JSA-compatible with the ship structure. Attach `MandatoryJointContainer` to their shared parent GO.

**Requirement:** The container must have a physical collider (not a trigger) that overlaps with the child parts' colliders. Trigger colliders are not detected.

---

### 5.3 InvisibleJoint Pattern

For parts that cannot be guaranteed to be in physical contact at spawn time (e.g. async-loaded ShipKit parts whose geometry arrives after Awake), place an explicit InvisibleJoint GO to bridge them.

**Requirements:**
- `Is Trigger: false` (solid collider)
- `localScale: (1, 1, 1)` — larger scales physically push parts apart before joint formation
- Parts must be flush (zero gap); the IJ straddles the contact plane at approximately 5 cm depth each side
- Joint Assist's Auto-Place accounts for mesh pivot offsets via `TransformVector(bounds.center)` automatically

---

## 6. Texturing Custom Models

To give a custom mesh the game's triplanar exterior texture (the worn hull surface that reads the ship's surface in three axis-aligned planes):

1. Create a new material using the shader `Fake/_Lynx/Surface/HDRP/Lit` (found at `Assets/_CustomShips/_Common/Shaders/FakeLynxHDSurface.shader`)
2. Set `_SurfaceType: 0` and `_TransmissionEnable: 0` in the material
3. Apply `Assets/_CustomShips/FirstShip/Materials/FirstShipWalls.png` (or your own equivalent) to the `BaseColorMap` slot

The top half of the texture receives the triplanar exterior treatment. The bottom half renders as a standard UV-mapped texture.

An example of a fully set-up custom mesh material can be found at `Assets/_CustomShips/FirstShip/Components/Shell/ShellConnector.prefab`.

**After any material change:** Run `Shipbuilder / Actions / Rebuild Addressables` to ensure the updated material is included in the next build.

### Making part of a mesh glow

Use this to give some faces of a custom part a steady glow of their own, such as coils, indicator strips or end caps, while the rest of the part keeps its normal texture. The glowing faces get their own material slot, and that slot gets a flat-colour material with an emissive colour.

**1. Give the glowing faces their own material slot (Blender)**

1. Select the part and enter Edit Mode.
2. Select the faces that should glow. For a separate piece of the mesh, hover over it and press `L`.
3. In the Material tab, press `+` to add a slot, create or pick a material in it (the name is only a label), and press **Assign**.
4. Export with [Export to Unity FBX](#11-export-to-unity-fbx) as usual.

In Unity the part's `MeshRenderer` now lists one more material. The part is still one part with one collider; nothing about its joints or mass changes.

**2. Make the glow material (Unity)**

1. Pick a flat-colour material your ship already uses with the `HDRP/Lit` shader, select it in the Project panel and press `Ctrl+D`. Rename the copy, for example `<ShipName>_Glow_Blue`.
2. In the Inspector, under **Surface Inputs**, clear the Base Map, Mask Map and Normal Map slots, and set the base colour to a dark shade of the glow colour.
3. Under **Emission Inputs**, clear the Emissive Map slot, leave **Use Emission Intensity** off, and set the **Emissive Color** (it is an HDR colour, so the numbers can go above 1).

The brightness is the size of the emissive colour's numbers:

| Look | Example Emissive Color (R, G, B) |
|---|---|
| Faint blue | 0.08, 0.32, 0.9 |
| Clear blue | 0.2, 1.1, 2.0 |
| Bright blue | 0.4, 1.6, 6.0 |

Keep the same proportions between R, G and B to change brightness without changing the hue.

**3. Assign it**

1. Select the part in the scene.
2. On its `MeshRenderer`, drag the glow material onto the new slot in **Materials**.
3. Save the scene, then run `Shipbuilder / Actions / Rebuild Addressables` before the next build.

**Notes**

- The glow is the surface only. It does not light the surfaces around it; add a light for that.
- Use one glow material for every part that should glow the same way, so a later change of colour or brightness is a single edit.
- A part whose blueprint makes it a powered machine part has its emissive colour set by the game every frame, and the numbers in the material are ignored. See [Power-state-dependent glow](#power-state-dependent-glow-different-from-the-above--machinepartemissivesystem) in Caveats.
- If the whole part glows white or orange after this, the cause is a different one. See [Persistent glow after SP/BP copy](#persistent-glow-after-spbp-component-copy) in Caveats.

---

## 7. SP_Mat & BP_Mat GUID Reference

These GUIDs are for the `StructurePartAsset` material (SP_Mat) and `EntityBlueprintComponent` material (BP_Mat) fields used in the Create Custom Part Wizard and manual prefab setup.

Source: `aa/catalog.json` binary decode (authoritative as of 2026-06-18).

### SP_Mat GUIDs

| Name | GUID | Density | Destination |
|---|---|---|---|
| Panel Ext Nanocarbon | `5173116158d71b74fa747dd2dec0d1bb` | 50 kg/m³ | Processor |
| Chassis Int Steel | `29193db511f0cb74aae62b98493e4b48` | 200 kg/m³ | Furnace |
| Chassis Aluminum | `e44706eda25d2a54baf79cb7df747e14` | 50 kg/m³ | Furnace |
| Chassis ExoStructure (displays "Titanium") | `8e3a736ff6a439f4a99610a7f86b1cd3` | 200 kg/m³ | Furnace |
| Glass | `49cd95d6fbf115a4c9733cce40a11b20` | 50 kg/m³ | Furnace |
| Reactor Core | `02984cb5befc22b44adac78c1c85aee6` | 200 kg/m³ | Barge (explosive when cut) |
| Thruster Class X | `d18f4147ea6439b41b2d74bd38757bd4` | 50 kg/m³ | Barge (cut level 5) |
| Thruster Class X Engine | `645afb95379f0734d84692c71a811d17` | 50 kg/m³ | Barge |

> **Note:** There is no `SP_Mat_Chassis_Titanium`. The "Titanium" display name in-game comes from `SP_Mat_Chassis_ExoStructure`.

### BP_Mat GUIDs (EntityBlueprintComponent)

| Name | GUID |
|---|---|
| Barge Blueprint | `10334f5f29e4d554ea18c1c8b9071bdc` |
| Furnace Blueprint | `0298f8cb1a0e06942b40e606b523b400` |

---

## 8. Caveats & Gotchas

These are issues discovered during real ship building (primarily a test ship mod) that are non-obvious and not directly surfaced by any tool.

**Convex colliders fill hollow meshes**
A convex `MeshCollider` on a ring or cylinder fills the interior with an implicit convex hull — the player cannot enter the inside. Use the **Segment Meshes** tool to split the mesh into wedge-shaped children, each of which is individually convex. The root collider stays enabled (required for the game to register the part on load) but the wedge children handle actual physics.

**Some parts have a fixed mass**
A part's mass is its density times the volume of its **MeshCollider** mesh, unless the StructurePartAsset's rigidbody asset is set to use a fixed mass. Pickup types use fixed masses (10 kg for most), whatever their size. To make something heavier, pick a StructurePartAsset whose rigidbody asset uses density and whose other settings (joint, yank, cut level, vitality) match; changing the mesh does nothing on a fixed-mass type. A StructurePartAsset cannot be partly copied, so a custom one with only a different mass is not possible.

**Make Interactable and grants that use a vitality type**
Patch kit, oxygen and med kit pickups grant through a component that refers to a vitality type from the game's data. That reference cannot be stored in a prefab; Make Interactable now records it in the AddressableComponentLoader, like the interactable asset. Parts made with older versions of the tool saved a broken reference, and their grant does nothing: run the tool on them again.

**Read/Write must be enabled on FBX imports**
The game reads raw vertex data from the meshes to calculate part mass (the MeshCollider's mesh) and joints (the MeshFilter's mesh). If Read/Write is disabled on the FBX, the mesh data is not accessible at runtime and the part's mass will be wrong (often zero). The Custom Part Wizard enables this automatically; check manually for FBX files imported outside the wizard.

**Non-unit scale is invisible to the game's physics systems**
Joint anchor points and mass are calculated from mesh vertex positions. Transform scale is applied visually but does not affect the raw mesh data the game reads. Always run **Lock In Rescale** before building any rescaled part.

**Persistent glow after SP/BP component copy**
After copying `StructurePart` and `EntityBlueprintComponent` from a working game part to a custom baked part (via Component Copy Window → Component tab), the custom mesh's material is not updated — it keeps the Blender-origin material, which lacks the Lynx shader keywords and float values that suppress the built-in cut/scorch glow effect. The part will glow bright white/orange in-game as if permanently on fire.

Fix: open **Component Copy Window → Material Shader tab**, set the glowing part as Target and a known non-glowing Lynx-shader part as Source, then diff and copy the following:
- Keywords: add `_EMISSIVE_COLOR_MAP` and `_SKIN_PLANAR_MAPPING` to `m_ShaderKeywords`
- `_CutIntensity`: set to `0.603` (default 1.0 triggers full glow)
- `_CutWidth`: set to `0.8` (default 0.1 makes the glow cover the whole face)
- `_EmissiveColorMap`: assign a real emissive texture from a working game part

Rebuild after saving the material. The glow will be gone on next load.

**Power-state-dependent glow (different from the above) — `MachinePartEmissiveSystem`**
If a part glows bright white/orange only while ship battery power is connected, and stops glowing when power is disconnected, this is NOT the persistent shader-keyword bug above — the `.mat` file is not the cause and editing it will not fix it.

Root cause: the game's `MachinePartEmissiveSystem` (in `BBI.Unity.Game.dll`) runs every frame and writes `_EmissiveColor`/`_RoomIntensity` directly via `MaterialPropertyBlock` — bypassing the material asset entirely — on any `MeshRenderer` belonging to an entity that has `MachinePartComponent` plus an electricity/fuel/cryo/pressure receiver buffer (e.g. a BP with `GlobalElectricReceiver_MachinePartAsset` or `LocalElectricReceiver_MachinePartAsset` in its `m_ComponentDataAssets`). This is the same MPB-override pattern the BayLights mod uses for fixture emissive colors.

The system explicitly excludes any entity that also has a `DynamicLight`, `ReactorBaseEmissiveController`, or `ReactorComponent`.

**Fix: add a `DynamicLight` component to the part's GameObject.** This is a Unity-asset-only change (no BepInEx mod or runtime code needed) and excludes the part from the emissive system entirely. Confirmed working.

The **Component Copy Window → Component tab** shows an **Electrical control** row (Global/Local) in the BP summary whenever a copied BP carries an electric receiver — it's highlighted orange when "Global" is present, since that's the specific case that triggers this glow. Use this to spot at-risk parts before they show the bug in-game, without needing to know which BP was used.

**ACL null entries crash ship load**
An `AddressableComponentLoader` with null/missing entries causes `ShipPreviewUtils.CalculateSpaceTruckPartCount` to receive null `moduleSummaries`, which chains to a crash that freezes the loading screen. The validator catches these as errors and blocks the build. After deleting any part GO, clean its ACL entries immediately.

**Colliders must be convex**
Concave (non-convex) MeshColliders do not register with the game's salvage and physics systems. A part with concave colliders renders visually but the player passes through it, it cannot be cut, and it shows no mass or targeting reticle. Always use convex colliders or the wedge pattern for hollow geometry.

**EntityBlueprintComponent is required**
A part without an `EntityBlueprintComponent` has no mass, no in-game label, and no salvage behaviour. If a baked part appears in-game but is hollow, intangible, and unidentifiable, the missing EBC is the likely cause.

**Negative scales break the game**
The game does not handle negative transform scales. Avoid negative scales entirely — use mesh editing (Blender) to flip geometry instead.

**Two-pass Force View Refresh for addressable children**
After placing a new addressable part in the scene, run `Force View Refresh` twice: the first pass builds the EditorCache entry, the second pass instantiates the rendered children. A single pass often leaves the part visually empty in the scene view.

**Copying a GO does not copy addressable wiring**
`Ctrl+D` duplicates the GO and its components, but asset addresses live in the parent `AddressableComponentLoader`'s `componentValues` array, not on the component itself. Always follow a duplicate with **Register Components in Parent ACL**.

**Do not unpack room prefab instances**
If you unpack a room prefab instance (right-click → Unpack Prefab) into the ship hierarchy, the `AddressableSOLoader` on the Room object will not fire at runtime. The room type and pressurisation state will not load. Keep room prefab instances packed.

**Room Overlap volumes can swallow probes**
Baked wall panels (e.g. `PRF_Panel_4xBx4`) include a `RoomOpeningDefinition` with `Type = Overlap` and a default Size of `(3, 1.25, 3)`. If a panel is placed adjacent to an atmosphere regulator, this large overlap volume can cover the regulator's `RoomProbe` position, breaking the interaction button. Shrink the offending panel's Overlap volume size (approximately `(1, 0.2, 1)` works) to fix this.

**A joint that forms in only one search order can disappear after an unrelated edit**
The game searches each pair of parts in one order only, and the result can differ between the two orders. A part that hangs on a single such joint stays attached until some other change (a part added, removed or merged elsewhere) alters the order, and then it falls off at spawn with no other symptom. In the spawn census it shows 0 joints. Do not nudge it: run the joint search for the pair in both orders, in the editor frame and in the game's world frame, and change the geometry until all four pass. Two fixes that worked: bring the raised underside of a fin down flat onto the floor of the slot it stands in; sink a part whose rim only shares vertices with the rim of its hole 1 to 2 cm into the skin so the rim rests on a real face.

**A StructurePart on an AddressableLoader object is an error every load**
An object that only loads a game prefab must carry the `AddressableLoader` and nothing else. If it also has a `StructurePart` (left over from a copy), the game logs `Failed to joint part "<name>", it has no MeshFilter or shared mesh` on every load and the census lists a part with no mesh. Remove the component; the loaded prefab brings its own parts.

**Salvage value comes from the blueprint, not the material**
The credits a part awards are set by the `EntityBlueprintAsset` (its salvageable component): either a flat value or a value scaled by mass. Two blueprints with identical behaviour can differ only in that value, so a part that salvages for a token amount usually has a generic blueprint. Compare the candidates' awarded-currency fields before choosing one.

**Set dressing is not a pickup**
Bottles, snack packs, galley items and soft pouches are plain props: they float free and never show a collect prompt, even where the mesh is the same as a real pickup's. For something the player can collect, place the game's pickup prefab. A pickup's prompt can also depend on progression events recorded in the player's profile, so some pickups spawn but cannot be collected in every game mode.

**Items on an open shelf can be inside the shelf's collider**
A cupboard or shelf unit baked as one part has a convex collider that covers its open front. Anything stored on its shelves is inside that collider and cannot be aimed at. Hang or stand such items in front of the unit, or split the unit into convex pieces.

**Addressable scaling requires baking first**
Addressable loader GOs (`AddressableLoader` component) cannot be rescaled — the game ignores transform scale on loaded addressables. If you need a game part at a different scale, bake it first via the Import Game Part Wizard, then rescale the baked prefab and apply Lock In Rescale.

---

## 9. Expected Unity Exceptions

These exceptions appear in the Unity console during normal editor use and do not indicate a problem:

| Exception | Marked | Notes |
|---|---|---|
| `Broken text PPtr. GUID 000...` | **[suppressed]** | Appears when something refers directly to in-game content that needs to be properly referenced or cloned |
| `Could not extract GUID in text file` | **[suppressed]** | Same as above |
| `Burst error BC1054: Unable to resolve type` | — | Unknown, harmless |
| `System.Reflection.ReflectionTypeLoadException` followed by `System.NullReferenceException` | — | Unknown, harmless |
| `Could not Move to directory Library/com.unity.addressables/aa/Windows, directory already exists.` | **[suppressed]** | Unknown, harmless |
| `Cannot instantiate objects with a parent which is persistent. New object will be created without a parent.` | — | Due to the way game assets are cached, harmless |
| `ThemesSettings.get_Database / ThemeManager` NullReferenceException | **[suppressed]** | Triggered by Doozy UI components on game assets loaded into the editor, harmless |

**[suppressed]** = filtered by the project's LogHandler and will not appear in the console.

## 10. Scripted Placement (ClaudeBridge) and In-Game Placement Check

### 10.1 ClaudeBridge

`Assets/Editor/ClaudeBridge.cs` lets an external script drive the open editor. It polls `.claude/bridge/in/` for JSON command files and writes each result to `.claude/bridge/out/` under the same file name. It runs while Unity is unfocused.

| Command | What it does |
|---|---|
| `state`, `hierarchy` | Report the open scene, selection, and object names/positions/bounds |
| `capture` | Render a camera at a given pose to `.claude/bridge/shots/<name>.png` |
| `catalog` | Render game parts to thumbnails in `.claude/bridge/catalog/` |
| `group`, `place`, `move`, `delete` | Create a container, place an `AddressableLoader` part by its bounds (`center` + `floorY`), reposition, or remove |
| `wall` | Place a baked board (or any baked prefab via `asset`) resized with Lock In Rescale; `glass`, or `material` for a text sign, and `oi` for a display name |
| `stripcomponents`, `setsoloader` | `{path, types:[..], dry}` removes named component types from one object (e.g. a stray StructurePart on a loader object); `{paths:[..], sp, bp}` re-points per-object loaders at another StructurePartAsset or blueprint (omit `sp`/`bp` to list) |
| `clone`, `setoi` | `clone` copies the part's loader entries from every loader above the source, so do not clone into the object that holds them. Copy one baked part already in the ship (e.g. a spare gun barrel); `setoi` gives its StructureParts the same display-name asset as the originals (omit `oi` to list) |
| `bakeparts` | `{guid, name, folder, sp, bp, smallSp, smallBp, smallMax, decor, clipBelow, skip}` — bakes a game addressable that has no salvage data (Hab set dressing has no colliders or StructureParts, so it is not solid or interactable in game) into a local prefab where every mesh is a part: convex collider + StructurePart + EntityBlueprintComponent, resolved by GUID through an AddressableComponentLoader. `decor:{nameSubstr: hostNode}` keeps meshes render-only as children of a host part; `clipBelow:{nameSubstr: height}` trims a collider so its convex hull does not swallow open space; uniform node scales are baked into the meshes. Run a texture de-dup over the output folder afterwards |
| `disablechild` | `{path, children:[..], enable}` — edits an AddressableLoader's `disabledChildren` list (child paths relative to the loaded prefab), same as the loader inspector's tree |
| `meshcast` | `{rays:[{o,d}], max, ignore, only, inactive}` — ray vs render-mesh triangles; `inactive:true` also tests disabled objects (e.g. hull tiles switched off for access), `only` restricts by path |
| `room` | `{parent, name, center, size, roomAsset, airlock}` — adds one world-aligned box room authored like the ship's existing rooms (`[ ] name` ModuleDefinition > `Volumes` RoomContainerDefinition + AddressableSOLoader > `Volume` RoomSubVolumeDefinition). Use it to give each closed cabin its own room, so every door has a room on both sides; delete the object to revert |
| `rescale` | `{path, scale:[sx,sy,sz], euler, center}` — multiplies a bridge-made object's local scale and bakes it into the mesh with Lock In Rescale (used with `clone` to cut a filler section from an existing plate). `clone` resets rotation, so pass the source's world euler. Save before running `clean_orphan_meshes.py` |
| `bake`, `postervariant`, `signmat`, `placeprefab` | Bake a game part to a local prefab, make a poster prefab or sign material from a texture, place a local prefab |
| `inspect` | `{path, depth, fake}` — components, mesh and bounds per node; `fake:true` also walks the hidden preview children of addressable parts (`python .claude/inspect_fake.py <path>`; `python .claude/preview_missing.py <path>` lists loaders whose preview is not loaded, which is what makes the Joint Compatibility Check say "No StructurePart MeshFilters found") |
| `worlddump` | `{paths, dir, preview}` — writes each object's render mesh in world space for the jointing emulator (`.claude/joint_emul.py`, run in Blender); `preview:true` dumps the preview meshes of addressable parts. The emulator pairs up neighbouring parts and reports which pairs find a joint plane |
| `sodump` | `{guids:[..], depth, expand, png}` — read-only dump of game or mod ScriptableObjects by addressable GUID (object references as names, AssetReferences as GUIDs); `expand` follows referenced ScriptableObjects, `png` also writes a texture to `.claude/bridge/shots/` |
| `meshassign`, `splitpart` | `{paths, folder, dir, name, materials}` builds (or overwrites in place, keeping its GUID) `<folder>/<name>.asset` from `<dir>/<name>.json` and puts it on the MeshFilter and MeshCollider of each object; omit `paths` to only write the asset. `splitpart` copies a part (SP, blueprint, collider and loader entries kept) for each extra piece of a split; pass `assets` for the pieces' meshes |
| `collidermesh` | `{paths, asset}` puts a mesh on the MeshCollider only; the MeshFilter (rendering, jointing) is left alone |
| `makeinteractable` | `{path, sourceGuid, fitBox}` runs Make Interactable's Direct Node Mapping on one object from a game pickup addressable, and with `fitBox` sizes the Interaction box to the object's mesh. The first addressable load after a recompile can come back empty: send the command again |
| `addgroup`, `addrtree` | `addgroup` wraps parts in a StructureGroup object (with its own SP, blueprint and loader entries) so the group's SP sets their cut level, as stock ships do for fuel junction pipes; `addrtree` dumps a stock prefab's tree with each node's SP, blueprint and group SP names |
| `prefabnudge`, `prefabloaders` | Move, re-address, rename or delete objects inside a nested prefab asset (by world delta or world pose) without opening it |
| `manifest` | Write `placement_manifest.json` (see 10.2) |
| `save`, `refresh`, `menu` | Save open scenes, recompile/reimport, run a menu item |

- No Undo records are registered. Every created object is listed in `.claude/bridge/placed.txt`, and `move`/`delete` refuse to touch anything not in that list.
- Saving the scene applies the additions to the ship prefab (via `SavePrefabOverride`).
- If a command hangs, create an empty file `.claude/bridge/in/reset`.

### 10.2 In-game placement check (PartInfoLogger)

1. In the editor, run the bridge `manifest` command. It writes `placement_manifest.json` to the repo root: one entry per bridge-placed part, listing the renderer meshes and colliders its game prefab contains. Rerun it if `unresolvedParts` is not 0.
2. Build the ship and load it in-game with PartInfoLogger installed (`[PlacementCheck] Enabled = true`, the default).
3. About 5 seconds after gameplay starts, the mod writes `placement_check.csv` next to its `OutputPath` and logs a one-line summary tagged `[PlacementCheck]`.

| Status | Meaning |
|---|---|
| `OK` | Loader found, content loaded, all expected renderer meshes visible, all expected colliders present and enabled |
| `MISSING_STUB` | No `AddressableLoader` with that name and GUID exists in the loaded ship |
| `NOT_LOADED` | Loader exists but nothing was instantiated beneath it |
| `RENDER_MISSING` / `HIDDEN` | An expected mesh is absent / present but disabled or inactive |
| `BAD_MATERIAL` | A renderer has a null material or the error shader |
| `COLLIDER_MISSING` / `COLLIDER_DISABLED` | An expected collider is absent / disabled |

Extra renderers or colliders at runtime (for example hardpoint spawns) are not flagged. The check is skipped when the manifest is absent or none of its entries match the loaded ship.

## 11. Career Job Board (ModdedShipLoader)

A custom ship is offered in Career only when its `ModuleConstructionAsset` passes three checks in ModdedShipLoader (`showModShipsInCareer` must be on in the loader's `settings.json`).

| Requirement | Where it is set |
|---|---|
| `Ship Archetype` is assigned | A `ShipArchetypeAsset` in the ship's Data folder. `m_ShipModelName` is the model name shown on the job board. Keep the label `Archetype_Jaavelin` (the game's own spelling) to keep Javelin hull skins |
| `Catalogue Image Ref` is assigned | A 1024x512 texture with a transparent background and `Alpha Is Transparency` on, added to the ship's Addressables group. This is not the free-play thumbnail (`LevelThumbnailImageRef` on the LevelAsset), which is opaque |
| The ship has a hazard level | Taken from the lowest `Difficulty` entry of the ship's `PropertyData`. With `PropertyData` left empty this comes from the `AssetBasis` ship; every normal stock ship lists hazard 6, 7, 8, 9 |

Fields left empty on the construction asset are filled from the `AssetBasis` game ship at load time (archetype, skin list, property data, manufacturer, audio size).

The loader adds the ship to every ship class whose hazard level is at or above the ship's lowest hazard level. Completing rank N grants work permit N, so class N is on the job board from rank N+1.

| Ship classes | Hazard level | On the job board from rank |
|---|---|---|
| 11-12 | 6 | 12 |
| 13-14 | 7 | 14 |
| 15-17 | 8 | 16 |
| 18-20 | 9 | 20 |

The job board draws its ships at random from the class pool, so a custom ship is not offered every day. Mass and value read "Unknown (Modded)".

**Ship name.** The name shown on the job board and when the ship loads is generated by the `CompanyInfoAsset` in `Manufacturer Ref`; left empty, the basis ship's manufacturer picks a random one. To fix the name, give the ship its own `CompanyInfoAsset` (in the Addressables group) whose only `CompanyShipNameAsset` has a single entry in `Possible Primary Names` and no prefixes, adjectives or suffixes. For example `Data/<ShipName>_CompanyInfoAsset.asset` and `Data/<ShipName>_CompanyShipNameAsset.asset`; edit the one line under `m_PossiblePrimaryNames` to rename the ship.

**Catalogue image.** With the ship scene open in Unity, render the ship alone on black and again on white (bridge `capture` with `bg` and `only`); the difference between the two gives the transparency, and the pair becomes a transparent 1024x512 image with the orange outline. `python .claude/career_dump.py` re-reads the class, rank and hazard tables from the game through the bridge.

**Job-board summary errors (open issue).** The game builds a summary of every ship on the job board (and of the ship being loaded) before ModdedShipLoader has filled in the parts' `StructurePartAsset`s. Every part still empty at that moment logs an error per core-system type, and each error is sent to the game's crash reporter; a ship with thousands of baked parts turns that into tens of thousands of errors and a very slow load whenever it is on the board.

The fix is a placeholder `StructurePartAsset` on the parts. The editor applies it by itself whenever a ship prefab is written (`Shipbuilder / Actions / Part Placeholders`, section 2). `python .claude/spa_placeholder.py report <ShipName>` lists the parts that have it and `remove` reverts (back up first). On a ship with several thousand baked parts it cut the summary errors from about 41,000 to 32, with no effect on Free Play (a new ship, or one restored by FreePlayPlus).

**Continuing a modded ship after a game restart.** ModdedShipLoader filled part references only after a prefab was instantiated, so a ship spawned without an earlier load in the same session (Career "continue" after a restart) came up broken: parts lost their `StructurePart` and fell off, or, with the placeholder applied, kept the placeholder's setup (no scanner info, no highlight, not grappleable or cuttable). This happens with or without the placeholder and with or without FreePlayPlus; ending a shift and continuing within one game session works. The patched loader (fork branch `fix/fill-refs-before-instantiate`, not yet upstream) fills the prefab before instantiating it, and ships then continue cleanly.

**Job-board image and milestones for a modded ship.**

| Symptom | Cause | Fix |
|---|---|---|
| In-progress ship shows the previous card's image | A ship read from the save has a null space-truck parts list when the ship has none; the job board throws before it sets the image | Loader patch (null list becomes empty) |
| No salvage milestones, or the previous card's milestones | The ship's basis has no `ShipRewardsAsset`. `Javelin_Science_Survey` has none; every other regular stock ship type has one (`python .claude/mca_rewards_dump.py` lists them) | Loader patch stops the stale display; real milestones need a basis that has rewards, for example `Javelin_Industrial_Cargo_Lrg` (9 tiers, the most any stock type has; hazard levels 6-9; role Heavy Cargo, theme Industrial) |
| Hazard and core-system icons missing | They are counted from the parts in the ship prefab when the card is created; a card created before the prefab was ever filled in that session counts none | Loader patch: a mod prefab's load now waits for its references (untested) |

**What the job board can see.** Hazards, core systems and "Detected on ship" are read from the ship's module prefabs, never from parts an `AddressableLoader` spawns at runtime.

| Shown | Needs |
|---|---|
| Explosion (triangle) | a baked part whose blueprint has a Fire-type explosive (reactor core, fuel tank, thruster) |
| Cryo (snowflake) | a Coolant-type explosive (cryo pipe, cryo canister) |
| Electric (bolt) | an Electricity-type explosive, for example a baked power generator (`BP_Subsystem_PowerGenerator`); one placed as an addressable does not count |
| Fuel (flame) | a machine part that sources fuel both globally and locally; none of the game's 18 machine-part assets has both, so it never lights on any ship |
| Space-truck parts | the pickup spawned as a child module from a `HardPoint` (bridge op `hardpoint {parent,name,pos,euler,list}`), not placed as an addressable. `python .claude/make_truck_part_lists.py` writes one `ModuleListAsset` per part type into `Data/TruckParts` and adds them to the Addressables group. Every stock basis with rewards caps a ship at 8 space-truck modules or fewer; to carry more, give the construction asset its own `m_Restrictions` list with one blank entry, which replaces the basis list and is skipped by the game. Place the hardpoints about 0.12 m above the deck: a pickup spawned within joint distance of the floor joints to it and has to be tugged off instead of floating free |

PartInfoLogger's `PlaceholderCheck` (off by default; set `Enabled = true` under `[PlaceholderCheck]` in its config; writes `placeholder_check.txt` next to its output file a few seconds after gameplay starts) lists parts with no asset, parts still on `SPA_Placeholder`, and parts whose real asset has scanner info or a highlight that the part itself lacks. On a healthy ship every count is zero; mesh-less wrapper objects (for example the roots of ceiling terminals) can show under "no highlight" and are harmless.

## 12. Ship Variants (Liveries and Variant-Only Parts)

A ship can spawn as one of several versions, picked at random by weight each time it is generated. Each version is a Unity **prefab variant** of the ship prefab, so every later change to the ship reaches all versions; a variant only stores what differs (other hull materials, extra parts).

| Piece | Where | What it does |
|---|---|---|
| `<ShipName>_<Variant>.prefab` | next to the ship prefab | Prefab variant of `<ShipName>.prefab`. Holds the material overrides and any parts that exist only in this version |
| `<Material>_<Variant>.mat` + textures | `Materials/`, `Textures/<Variant>/` | Copies of the hull materials pointing at the repainted textures |
| `Spawn_ModuleEntryDefinition_<Variant>.asset` | `Spawn/` | A second entry in `Spawn_ModuleEntryContainer`, with `Module Def Ref` set to the variant prefab |
| Addressables entry | the ship's Addressables group | The variant prefab must be in the group, or the entry cannot load it |

**Making a variant by hand.**

1. In the Project window, right-click `<ShipName>.prefab` and choose `Create / Prefab Variant`. Name it `<ShipName>_<Variant>`.
2. Open the variant and make it differ: assign other materials, add parts, or untick objects it should not have. Leave everything shared alone; edit that in the base ship.
3. Add the variant prefab to the ship's Addressables group.
4. In `Spawn/`, duplicate `Spawn_ModuleEntryDefinition.asset`, name the copy `Spawn_ModuleEntryDefinition_<Variant>`, and set its `Module Def Ref` to the variant prefab.
5. Select `Spawn_ModuleEntryContainer.asset` and add the new entry to its `Data` list.
6. Set the `Weight` of each entry (see below), run the Ship Validator, build, and load the ship several times.

A variant inherits everything in the base ship, including its hardpoints ([Section 14](#14-hardpoints-and-module-lists)), so content spawned from hardpoints appears in every variant without further work. The spawn list itself is an ordinary module list: the ship is the prefab its entries point at.

**Setting the chance.** Each `Spawn_ModuleEntryDefinition*.asset` has a `Weight`. The chance of a version is its weight divided by the sum of all weights (1 and 1 = 50 % each; 3 and 1 = 75 % / 25 %). Weight 0 means never; set the other entry to 0 to force one version while testing. The roll is made from the ship's seed.

**Variant-only parts.** Open the variant prefab (double-click it, or drag it into a scene) and add the parts there, under the same group as their neighbours so they joint like any other part. Parts added in the variant never appear on the base ship. Edit everything shared in the base ship as before.

**New hull parts.** A variant overrides materials per renderer, so a hull part added to the ship later keeps the base material in the variant until it gets its own override: select it in the variant and assign the variant material, or re-run the script that created the variant.

**A variant as its own ship.** A version picked by weight shares the ship's name and pictures, so the job board cannot show which one will spawn. To give a variant its own name and pictures, list it as a separate ship that spawns the variant prefab:

| Piece | What it does |
|---|---|
| `Data/<ShipName>_<Variant>_LevelAsset.asset` | Free Play entry: display name and thumbnail. Addressables labels `GameMode_Free` and `Release` |
| `Data/<ShipName>_<Variant>_ModuleConstructionAsset.asset` | The ship itself: root prefab, catalogue picture, manufacturer. Label `ModdedLevel`; its `m_AssetGUID` must be its own GUID |
| `Data/<ShipName>_<Variant>_CompanyInfoAsset.asset` + `..._CompanyShipNameAsset.asset` | The names the job board can give it |
| `Spawn/<ShipName>_<Variant>_RootRef.prefab` | Its own HardPoint (and so its own bay pose), with a one-entry module list that spawns `<ShipName>_<Variant>.prefab` |

The ship loader registers every construction asset labelled `ModdedLevel` in the bundle, so the extra ships need no second bundle and add only these small files. Take the variant out of the base ship's `Spawn_ModuleEntryContainer` so the base ship always spawns as itself. The archetype asset can be shared.

**A variant with its own bay pose.** The bay pose belongs to the HardPoint that spawns the ship, so every entry in one module list shares it. To give one variant a different pose, point its entry at a small mount prefab instead of at the variant itself:

| Piece | What it does |
|---|---|
| `Spawn/<ShipName>_<Variant>_Mount.prefab` | Holds a second HardPoint. The variant's entry in the main container points here |
| `Spawn_<Variant>Inner_ModuleListAsset` / `...EntryContainer` / `...EntryDefinition` | A one-entry list on that HardPoint which spawns `<ShipName>_<Variant>.prefab` |

The mount is spawned at the base ship's pose, so the second HardPoint's local transform is the base pose inverted, times the pose wanted for the variant. Both the mount prefab and the inner list must be in the ship's Addressables group.

**A variant that is too big for the bay.** Two ways to handle it:
- Pose it clear of the bay's fixtures. A ship that is too wide to sit between the light jacks can be posed level above them; nothing in the bay is in the way higher up. Check the pose against the recorded bay geometry before a test load.
- Ship the variant's entry with `Weight` 0 and let a mod that makes room for it give it a weight. Players without the mod then never get it.

**Mass of scaled-up parts.** The game sets a part's mass to its material's density times the volume of its collider mesh, and it adds that volume up as signed triangle volumes. Scaling a part by a factor multiplies its mass by the factor cubed, so a stock part scaled 2.35 times weighs 13 times as much and can be too heavy to tether. To cut the mass without changing the size, add an inward-facing copy of the mesh, scaled down about its center of volume, to the same mesh: with a copy scaled by `s` the mass becomes `1 - s^3` of what it was (0.90 leaves 26 %). The copy is only visible from inside the part.

**Interior differences between variants.** A variant can also differ inside, without any new objects:
- **Leave something out.** Open the variant prefab, select the object and untick it in the Inspector. The variant stores that as an override; the base ship keeps the object. Switch off whatever sits on or hangs from it too (bottles on a table, bedding on a bunk), or it floats. Switched-off objects are not spawned, but they are still in the download.
- **Change a picture.** Posters and signs that share one mesh can show a different picture per variant: assign another material to the poster in the variant. To have the base ship differ and the variants keep the original, change the picture on the base ship and assign the original material again in each variant.
- When writing such overrides into a variant file by script, put them in the variant's own instance of the ship (the one whose source is `<ShipName>.prefab`); a variant that adds parts holds other prefab instances as well.

**Things to know.**
- A build stores each variant as a full copy of the ship prefab's objects (meshes and unchanged textures are shared), plus the variant's own textures.
- Changing weights between starting a Career ship and continuing it can change which version is rebuilt; leave the weights alone while a shift on that ship is in progress.
- The catalogue image and free-play thumbnail are per ship, not per variant.

---

## 13. Pipes, Cut Points and Convex Colliders

Every custom hull part collides as the **convex hull** of its mesh, because the game needs a convex collider on anything that can become a free body. A part that is folded or wrapped round a pocket therefore has collider faces across the open side of that pocket: the player sees open space but cannot fly, grapple or cut into it, and the scanner reports the part there. Unity shows this shape as the green wireframe of the part's `MeshCollider`.

Anything placed in such a pocket is inside the part's collider. A pipe cut point there cannot be reached, and a pipe that ends there looks reachable when it is not.

**Rules for pipes that run from an interior wall to a hull part**

1. Never move the point where a line leaves the interior wall; it has a counterpart pipe on the other side.
2. Test every pipe end and cut point against the convex hull of each hull part nearby, not against the visible mesh.
3. Put the cut point on the last straight stretch of pipe that is clear of the part's collider, as close to the part as it fits. Follow it with one short piece of pipe that runs on into the part.
4. A cut point right against a junction box is redundant: the box can be cut.
5. A long main that passes through several parts runs straight through them, with an inline cut point on each open stretch near where it enters a part.
6. Do not leave pipe that no longer comes from a wall. A piece whose upstream connection was removed is jointed to nothing and floats away at spawn.

**Closing a pocket so the collider is visible**

Add thin plates to the existing part along the faces of its own convex hull, so the part reads as a closed hollow box. Because every plate lies on or inside the hull, the collider, the part's outline and its existing triangles do not change. Leave open only the hull faces on the outside of the ship. Where a hull face partly coincides with existing mesh, set the plate about 1 cm inside it so the two surfaces do not flicker.

Keep the plates thin and closed (a slab, not a single sheet): the game works out a part's mass from the volume of its mesh.

**Things to know.**
- New plates lie in the same planes as neighbouring parts' faces and can change which joints the game finds. Compare the joint search before and after, then confirm with a spawn census.
- Pipes are not held by hull panels: the pipe joint type cannot joint to an exterior panel. A hull part is released by cutting its hull joints; its pipes are released at their cut points.
- A pipe piece of 6 cm joints like any other, but only if both its neighbours are still there.

**Fewer invisible walls: plate or split**

Where a convex hull spans open space the player can reach, the player meets an invisible wall and anything inside cannot be cut, scanned or blasted. Fix each such part one of two ways:

| Method | When | How |
|---|---|---|
| Plate | The open side faces a room and the pocket is not needed | Add thin slabs on the hull faces that face a room (see above). Never on faces that open to space outside the ship |
| Split | The part's shape is what makes the hull large (a frame with legs, a wall with a roof, a block with rods) | Cut the mesh in Blender with a capped cut, assign the first piece to the original object and copy the part for the rest, so each piece's hull follows its visible shape. Leave the cut faces 0.5 to 1 mm apart so the pieces joint to each other |

Things learned doing this on a whole ship:
- Decide "faces a room" with a flood fill of the free space around the ship, not by the face's direction. A slanted face can point inward and still open to space.
- A slicer that caps cut faces must only re-map UVs on the faces it creates. Faces next to the cut keep their own UVs, or the texture streaks.
- Give plates a plain tile from the hull atlas, tiled at the hull's texel density, not the UVs of a nearby face (that copies stripes and decals).
- Check every changed part with textured renders of the original and the new mesh from two sides before calling it done.
- A cap that closes the end of an open, U-shaped piece shows. Clip it to the area where the neighbouring piece sits right behind it.
- New objects made by a split exist only in the base prefab's list of objects. A variant that swaps materials or switches parts off per object does not cover them: set its materials and switch them off in the variant too.

**Parts that glow when a line reaches them**

Some hull parts (thermal vents, filters) are built from materials that accept fuel or coolant. If a pipe, a junction box or the game's own pipe cut point joints to one of them, the part glows bright white in game. The game's pipe cut point does not prevent this: its type joints with everything.

1. Check which fuel and coolant objects find a joint with each such part (joint search, both orders), and confirm in the spawn census which ones actually jointed.
2. Keep live pipe and junction boxes from jointing to the part. Do not close such a part with inside plates if that brings a line into contact with it.
3. For the pieces that must sit against the part, use baked copies whose type does not joint with it: bake the game's cut point, bend or straight pipe to a local prefab and give it the plain chassis cut point StructurePartAsset and the cut point blueprint. They look like pipe, carry nothing, and still joint to pipe and junction boxes.
4. Put the cut point where it was, then the baked bend or pipe after it, so the line still releases at the cut point.

---

## 14. Hardpoints and Module Lists

A hardpoint is a spawn point. When the game builds a ship it visits every hardpoint in it, rolls once on that hardpoint's list, and spawns the prefab the roll picked at the hardpoint's position and rotation. The roll comes from the ship's seed, so the same seed gives the same result.

Use a hardpoint when something should be there only sometimes, only on some kinds of ship, in one of several places, or as one of several versions. Use an ordinary placed part when it should always be there.

Everything in this section can be done by hand in the Unity editor with the menu items named below.

### 14.1 The pieces

| Piece | What it is | Where it lives |
|---|---|---|
| `HardPoint` component | The spawn point. Its GameObject's position and rotation become the spawned prefab's position and rotation | On an empty GameObject inside the ship prefab, or inside any prefab the ship spawns |
| `ModuleListAsset` | The list a hardpoint rolls on. The hardpoint's `Asset Ref` points at it | An asset in the ship's Addressables group |
| `ModuleEntryContainer` | Holds the list's entries | An asset next to the list |
| `ModuleEntryDefinition` | One entry: "spawn this prefab", with a `Weight` and optional `Properties` | An asset next to the list |
| `ModuleEntryEmpty` | One entry: "spawn nothing", with a `Weight` | An asset next to the list |
| The prefab to spawn | Anything: one part, a group of parts, another hardpoint. Its root must carry a `ModuleDefinition` component | In the ship's Addressables group |
| Property reference (optional) | A stand-in for one of the game's ship properties, used to tie entries to a kind of ship | An asset next to the list |

A spawned prefab becomes a child module of the ship. Its parts are real parts: they joint, scan, salvage and count toward the ship's part total and value like placed parts.

### 14.2 How the game picks an entry

1. **Filter by properties.** An entry with `Properties` is offered only to a ship that has every one of them. An entry with no properties is offered to every ship.
2. **Priority.** If the ship has a property with priority above 0, and at least one offered entry carries it, entries that do not carry it are dropped. The property references this toolkit creates have priority 1.
3. **Weights.** One of the remaining entries is picked at random, with chance equal to its `Weight` divided by the sum of the remaining weights.

Examples for one hardpoint:

| Entries | Result |
|---|---|
| Prefab A, weight 1 | Always A |
| Prefab A, weight 1; Empty, weight 3 | A on one ship in four |
| Prefab A, weight 2; Prefab B, weight 1 | A on two ships in three, B on the rest |
| Prefab A tied to property P; Empty | A on every ship that has P, nothing on ships without P |
| Prefab A and Prefab B, both tied to P, weight 1 each; Empty | A or B, half each, on ships with P; nothing on the rest |

### 14.3 Making a hardpoint by hand

1. **Make the prefab to spawn.**
   - Build it like any other part or group of parts (a baked game part, a custom part from the wizard, or several under one root).
   - Select its root, **Add Component**, and add `Module Definition`. Without it the game refuses to spawn the prefab.
   - Add the prefab to your ship's Addressables group (Addressables Groups window, drag it in).
   - Decide which way it faces. A wall-mounted prefab is easiest to place when one fixed axis points out of the wall, for example the root's +Z, and the mounting face sits at the root's origin.
2. **Copy the prefab's GUID.** Right-click it in the Project window and choose `Assets / Copy GUID`.
3. **Create the list.**
   - Select the folder the assets should go in.
   - Choose `Assets / Create / Shipbreaker / Create hardpoint asset`.
   - **Hardpoint name:** the prefix for the new files.
   - **Asset references:** set the size to the number of prefabs, paste one GUID per row, and give each a weight.
   - **Spawn empty weight:** above 0 adds a "spawn nothing" entry with that weight.
   - **Only on ships with this property:** leave empty for now. See [14.4](#144-content-for-one-kind-of-ship-only).
   - Press **Create**. The window writes one `<Name>_ModuleEntryDefinition` asset per prefab (numbered when there are several), the Empty entry if asked for, the container and `<Name>_ModuleListAsset`.
4. **Move the list into your ship's Addressables group.** The window puts it in the default group. Open the Addressables Groups window and drag `<Name>_ModuleListAsset` into the group your ship uses.
5. **Place the hardpoint.**
   - In the ship, create an empty GameObject where the prefab should appear.
   - **Add Component** and add `Hard Point`.
   - Drag `<Name>_ModuleListAsset` onto its **Asset Ref** field.
   - Leave the other fields at their defaults.
   - Move and rotate the GameObject. The spawned prefab's root takes exactly this pose.
6. **Save, run the Ship Validator, build, and load the ship.** Load it more than once if the list has more than one outcome.

To change the odds later, select an entry asset and edit its `Weight` in the Inspector. To add an outcome later, duplicate an entry asset, change its `Module Def Ref`, and add it to the `Data` list of the container asset.

One list can serve any number of hardpoints. Each hardpoint rolls on its own.

### 14.4 Content for one kind of ship only

Some game features mark the ship with a property. A ghost ship, for example, carries the game's ghost ship property. An entry tied to that property spawns only on ghost ships.

1. Find the property's GUID. Search `known_assets.json` in the project root for the property's name, for example `GhostShip_ModulePropertyAsset`, and copy the 32-character GUID at the start of that line.
2. Create the list as in [14.3](#143-making-a-hardpoint-by-hand), and paste that GUID into **Only on ships with this property**.
3. The window then does three things:
   - It creates a property reference asset, `ShipProperty_<first 8 characters>_Reference`, or reuses the one the project already has for that GUID.
   - It puts that reference in the `Properties` list of every prefab entry.
   - It adds an Empty entry, so ships without the property have something to pick and spawn nothing.
4. Place hardpoints as usual. They cost nothing on ships without the property.

**Why a reference asset.** The game compares properties by asset GUID, and the game's own property asset is not in your project. The reference asset is a local asset whose own GUID is the game's GUID, which is what makes the comparison succeed. Do not type a GUID into a property asset's `Asset GUID` field by hand: Unity overwrites that field with the asset's own GUID, and the entries then silently stop matching.

**Always test both ways.** Load the ship once with the property and once without. A broken tie shows up as "nothing spawns on either".

The ship must be able to get the property in the first place. For the ghost ship property that means ghost ships are available for your ship, through its job board setup or through a mod that turns ghost mode on.

### 14.5 Example: AI nodes

A ghost ship is haunted while it has AI nodes. Each node is a part carrying the game's AI node blueprint. The haunting weakens as nodes are destroyed and stops when none are left, so the number of nodes sets how long the job is.

To add nodes, with your own look if you like:

1. Bake the game's AI node (`PRF_Props_AI_Node_Regular`, or one of the two large versions) to a local prefab with `Shipbuilder / Bake Addressable In Place`. Keep its structure part and blueprint.
2. Optional: swap the mesh and material for your own. Keep the mounting face flat, at the root's origin, with the node growing along one axis.
3. Add `Module Definition` to the root and put the prefab in the ship's Addressables group.
4. Create the list with the ghost ship property GUID ([14.4](#144-content-for-one-kind-of-ship-only)). Several node prefabs in one list give a random mix.
5. Place hardpoints on flat surfaces with clear space in front, rotated so the node's growing axis points out of the surface.

Things specific to nodes:

- **Glow.** The node's blueprint makes it a powered machine part, and the game then sets the material's emissive colour itself, every frame, while the node has power. Emissive numbers in the material are ignored. Brightness is set by how bright the emissive map is: paint the map darker to dim the node.
- **Light.** The stock node carries a spot light that shines away from the wall. Change its type to Point for an even glow, and expect to lower its intensity a great deal: it sits a few centimetres from the mesh.
- **Name.** The scanner name comes from the structure part's `Object Info Asset Override`. Set it with `Shipbuilder / Set Display Name on Selected…`.
- **Clearance.** Check each spot against the deepest and widest node prefab in the list, not the one the editor shows.

### 14.6 One of several places, or two independent rolls

A hardpoint can spawn a prefab that itself contains a hardpoint. The game follows these to any depth. Two uses:

**Exactly one object, in one of several places.** Placing a hardpoint at each place would roll each one separately and could give none or several. Instead:

1. Make the object a prefab with `Module Definition`, as in [14.3](#143-making-a-hardpoint-by-hand).
2. For each place, make a small "mount" prefab: an empty root with `Module Definition`, and one child GameObject with a `Hard Point`. Position that child, inside the mount prefab, at the place in ship coordinates. Point its `Asset Ref` at a list that spawns the object.
3. Make a second list whose entries are the mount prefabs, one per place.
4. Put a single hardpoint at the ship's root, with no offset or rotation, pointing at that second list.

The outer roll picks the place. Exactly one mount spawns, so exactly one object appears.

**Two things that vary independently.** In the same setup, give the inner list several entries, for example the same object with different names or colours. The outer roll picks the place and the inner roll picks the version, so every combination can occur.

A prefab can also carry a hardpoint for an optional extra, for example something that should appear on the object only on one kind of ship: put a hardpoint with a property-tied list inside the object's prefab.

### 14.7 Decals through hardpoints

The game can paint a texture onto the parts around a point when the ship spawns. It is the same system the game uses for hull markings and damage. The paint stays on each part after cutting. Because it is spawned from a hardpoint, it can be random or tied to one kind of ship like anything else.

**The prefab.**

| Object | Components | Settings |
|---|---|---|
| Root | `Module Definition` | |
| Child | `Baked Decal Projector` and `Entity Blueprint Component` | `Decal Texture`: your texture. `Size`: the box, in metres. The blueprint component stays empty with Auto Initialize ticked |

- The projector paints along its own downward axis (local -Y) onto every part its box touches. Rotate the child so that its +Y points out of the surface the hardpoint will sit on, and move it a little way out along that axis.
- `Size` X and Z are the width and height of the painted area. Y is the depth of the box; make it deep enough to reach the surface.
- The texture's alpha is the coverage: transparent where the surface should show. Set the texture's wrap mode to Clamp and keep its outer edge fully transparent, because parts larger than the box sample the edge.
- Do not put a projector directly in the ship prefab. Its blueprint component fails the Ship Validator. Always spawn it from a hardpoint.

**Which parts take it.**

- Only materials with the shader keyword `_BAKED_DECALS_ON` are painted. Many stock materials have it. To see a material's keywords, switch the Inspector to Debug mode and read `Shader Keywords`.
- You cannot edit a stock game material. You can edit the materials of your own and baked parts.
- When adding the keyword to your own material, the whole keyword set must be one the game's shader was built with, or the decal is baked but never drawn. Copy the complete keyword set from a stock material that shows decals and has the same maps as yours. `_BAKED_DECALS_ON _MASKMAP _NORMALMAP _NORMALMAP_TANGENT_SPACE` is one such set.
- That set turns the material's normal map on. If the surface relief becomes too strong, lower the material's `Normal Scale`.
- Check which other parts share a material before changing it. A hull material is sometimes also used on interior parts.

**Limits.**

- A part keeps at most two decals. A third projector touching the same part is ignored for that part. Large single parts therefore need larger decals, not more of them.
- A part is skipped when its pivot lies in front of the projector along the projection direction. Keep the projector in front of the parts it should paint.
- A decal colours the surface only. It cannot glow.
- Each part that receives a decal gets its own copy of its mesh and material at load, so very many painted parts cost some load time and frame rate.
- One decal is one texture stretched over the box. A 2 m box from a 1024 pixel texture is sharp up close; a 10 m box from the same texture is soft.

### 14.8 Checking and troubleshooting

**In the editor.** `Shipbuilder / Actions / Force View Refresh` draws a preview at each hardpoint. It always draws the first entry of the list, ignores properties and weights, does not follow a hardpoint inside a spawned prefab, and does not draw decals.

**In the game.** The ship's part count rises by the parts spawned. Run the Ship Validator before each build, and keep an eye on the part budget ([Caveats & Gotchas](#8-caveats--gotchas)) when hardpoints add many parts.

| What you see | Likely cause |
|---|---|
| Nothing spawns at any hardpoint using the list | The list or the prefab is not in the ship's Addressables group; or the prefab's root has no `Module Definition` |
| Nothing spawns on the kind of ship it is tied to, and nothing on other ships either | The property reference no longer carries the game's GUID. Delete it and create the list again with the window |
| It spawns on every ship although it is tied to a property | The entry's `Properties` list is empty, or the list has an untied prefab entry |
| It spawns only sometimes | The list has an Empty entry or other entries sharing the weight. That may be what you asked for |
| Several appear where one was wanted | Several hardpoints each rolled. Use the nested setup in [14.6](#146-one-of-several-places-or-two-independent-rolls) |
| It appears in the wrong pose | The hardpoint's rotation is the prefab root's rotation. Check which axis of the prefab points out of the surface |
| A decal is missing on one part but present on its neighbours | That part's material lacks the keyword set, or the part already has two decals |
| A decal is missing everywhere | The projector is not spawned from a hardpoint, or its child lacks the blueprint component, or it points away from the surface |
