# Shipbuilder User Guide

This guide covers every tool in the ShipbreakerShipbuilder mod — menus, context menus, editor windows, and the non-obvious workflows and caveats that come up when building custom ships.

**Start here first:** Read the [README](../README.md) for project setup, first-ship walkthrough, and the bay-positioning workflow. Come back here once you are inside the editor and building.

---

## Table of Contents

1. [Blender Tools](#1-blender-tools)
   - [Export to Unity FBX](#11-export-to-unity-fbx)
   - [Radial Split](#12-radial-split)
   - [Concavity](#13-concavity)
   - [Interactive Bisect](#14-interactive-bisect)
   - [Hollow Mesh](#15-hollow-mesh)
   - [Group to Collection](#16-group-to-collection)
   - [Material & Texture Pipeline](#17-material--texture-pipeline)
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

---

## 1. Blender Tools

The Blender scripts live in `Blender/blender_scripts/startup/` and are registered as a Blender Script Directory — Blender auto-registers them on startup. Reload all scripts without restarting with **Ctrl+Shift+R**.

---

### 1.1 Export to Unity FBX

**Two ways to trigger it:**
- **N-panel sidebar:** Press **N** in the 3D viewport → **Export** tab. Shows what will be exported before you click.
- **Outliner right-click:** Right-click any object or collection row in the Outliner → **Export to Unity FBX**.

**Export source — priority order:**
1. **Selected objects** (and their mesh children recursively) — click an object in the Outliner or viewport to select it; child meshes are automatically included
2. **Active layer collection** — if nothing is selected, uses whichever collection is highlighted blue in the Outliner

The N-panel label shows `Name (N meshes)` so you can confirm what will be exported before clicking.

**What it does:**
- Exports all mesh objects to an FBX with Unity-correct settings: `axis_forward=Y`, `axis_up=Z`, `apply_scale_options=FBX_SCALE_ALL`, `bake_space_transform=True`
- Copies all image textures to `../Textures/` (sibling of the `Models/` folder — shared flat folder, not per-part subfolders)
- Writes a sidecar `<name>.textures.json` next to the FBX — Unity uses this to name materials and wire textures automatically

**Prerequisite — apply scale before exporting:**
1. Make meshes single-user: `Object → Relations → Make Single User → Object & Data`
2. Apply scale: select all objects, `Ctrl+A → Scale`

If scale is not applied, the FBX exporter bakes the transform scale into the export and the geometry arrives in Unity at the wrong size.

**Remembers the last export directory** for the session (persists across script reloads via `bpy.app.driver_namespace`, cleared on Blender restart).

---

### 1.2 Radial Split

**ShipBreaker sidebar → Radial Split** (or Object menu → Radial Split).

Splits the selected mesh into N equal radial pie-slice segments around a chosen axis (X, Y, or Z). Each segment becomes a separate mesh object. The original mesh is hidden (not deleted). All segments are grouped into a collection named after the original object.

**Why separate objects:** Unity imports each object in an FBX as a separate `Mesh` asset. Submeshes within a single object become material slots, not separate meshes — the CPW segmented collider workflow requires separate meshes.

**Settings dialog:**
- **Segments** — number of pie slices (default 8)
- **Axis** — X, Y, or Z (default Y)
- **Angle Offset** — rotate all cuts by this many degrees (default 0)

**Typical workflow for a cylindrical shell:**
1. Model the full ring/cylinder
2. Radial Split → 8 segments at 45° around the long axis
3. Keep the original (hidden) in the same collection — the CPW uses it as the visual/trigger mesh
4. Export the collection — original + all segments end up in one FBX

---

### 1.3 Concavity

**ShipBreaker sidebar → Concavity** (also accessible via Radial Split → Auto-Detect Seams)

Automatically detects where large faces meet at concave angles and splits the mesh at those boundaries. Unlike Radial Split which requires a known segment count, Concavity reads the mesh geometry to find the split planes itself. Works for any polygon count — octagons, pentagons, irregular shapes.

> **Note:** Concavity detection is also available inside the Radial Split dialog as "Auto-Detect Seams", which launches the same interactive mode directly.

**How it works:**
1. At invoke, the tool analyses the mesh: coplanar faces (within 2°) are merged into "super-faces", then adjacent super-face pairs are evaluated for area and angle
2. Pairs where both faces exceed **Min Face** area, meet at an angle greater than the **Angle** threshold, and have similar areas (ratio < 2:1) are detected as seam boundaries
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

**HUD:** `Concavity  |  Axis: Z  |  Min Face: 10.00 m²  |  Angle: 15.0°  |  Splits: 8  |  Fill: OFF`

**Splits count** = number of detected seam boundaries = number of output segments.

**Typical workflow for a ring-shaped part:**
1. Model the full ring
2. Run Concavity with Axis = Z (or whichever axis runs through the ring)
3. Adjust Min Face up until only the corner seams between wall panels remain; adjust Angle as needed
4. Press Enter — each wall panel becomes its own `_seg01..N` object in a collection
5. Export the collection (original hidden + all segments)

**Pentagon / asymmetric shapes:** The tool uses sector-labeling rather than fixed angles, so it handles non-symmetric shapes correctly — a pentagon produces 5 segments, an irregular hexagon produces 6, etc.

---

### 1.4 Interactive Bisect

**ShipBreaker sidebar → Interactive Bisect**

Modal operator for cutting a mesh with a single plane interactively. Drag to position the cut, confirm with Enter.

---

### 1.5 Hollow Mesh

**ShipBreaker sidebar → Hollow Mesh**

Adds wall thickness to a flat or single-sided mesh by extruding faces inward. Useful for converting a thin shell mesh into a solid-walled part suitable for convex colliders.

---

### 1.6 Group to Collection

**ShipBreaker sidebar → Group to Collection**

Creates a new collection containing the selected objects, nested under their current collection. Equivalent to grouping objects in the Outliner without changing their scene positions.

---

### 1.7 Material & Texture Pipeline

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

**Real example — Rocinante nozzle:** The Epstein drive nozzle was built as a custom FBX mesh and imported via the wizard using the Bake path, then scaled to fit. Lock In Rescale was required before building because the game reads joint/mass data from raw mesh vertices, not transform scale.

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
1. Use **Radial Split** (for uniform N-segment rings) or **Concavity** (for polygon-shaped rings where the splits should align with face boundaries) to split the mesh into segments. Keep the original unsplit object in the same collection — do not hide or delete it.
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
- Set a Source material and a Target material
- Shows which shader properties differ
- Check properties to copy and click Copy
- Useful when you have a working material and want to apply specific property values (e.g. surface type, transmission) to a new material without re-doing everything

**Addressable detection:** The tool identifies a GO as an addressable loader (not a baked part) by the presence of an `AddressableLoader` component — not by the absence of `StructurePart`. Keep this in mind if you see unexpected classification.

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

**Real example — Rocinante hull panels:** A Mackerel wall panel was baked and then scaled to 1.5× to fit the Rocinante's wider hull section. After scaling, Lock In Rescale was applied. The panel's joint and mass now correctly reflect the larger geometry.

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

**FSP/SP caveat:** The tool handles both `StructurePart` (SP) components on baked parts and `FakeStructurePart` (FSP) components on async-loaded addressable parts. Both paths are supported. Parts with FSPs are flagged in the compatibility panel with an `(addressable)` label.

**InvisibleJoint requirements:**
- Must be a solid collider (`Is Trigger: false`)
- Scale must be `(1, 1, 1)` — larger scales physically push parts apart before joints can form
- Parts must be flush (zero gap); the IJ straddles the contact plane at ~5 cm depth

**When auto-jointing is enough:** If both parts use compatible `SP_Mat` types (e.g. both are `SP_Mat_Panel_Ext_Nanocarbon`) their `JointSetupAsset` pairing allows them to auto-joint at spawn without an explicit InvisibleJoint — the proximity is enough. Use Joint Assist's compatibility check to confirm before placing unnecessary markers.

---

#### `Shipbuilder / Radial Duplicate`

Duplicates selected GameObjects radially around a chosen axis (X, Y, or Z) or a reference axis defined by another GO. Enter the count and angle, optionally rotate room volumes proportionally.

**When to use:** Creating cylindrical structures like the Rocinante's octagonal hull rings — place one segment, then radially duplicate 8× at 45° around the ship's long axis.

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

**Real example — Rocinante nozzle:** The nozzle ring was exported as a single mesh but its collider was split into 8× 45° wedge children using Segment Meshes. Each wedge's convex collider covers the outer wall without filling the interior.

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

Opens a configurable overlay window that draws debug gizmos in the scene view:

| Overlay | Color | What it shows |
|---|---|---|
| Room SubVolumes | Green | Include volumes (pressurized space) |
| Room SubVolumes (Exclude) | Red | Exclude volumes (carve-outs) |
| Room Openings | Red arrows | Flow axis direction on each opening |
| Joints | Color-coded by type | Joint marker positions |
| Joint collision | Outline | Collider bounds of each joint |

**When to use:** When diagnosing pressurisation issues, room overlap problems, or unexpected de-pressurisation on ship load. The overlays make the invisible room volumes visible without having to click into each prefab.

**Caveat — room overlaps:** If you see instant de-pressurisation when loading your ship, check the Overlap opening volumes (shown in the overlay). An extra or misplaced Overlap volume that intersects a SubVolume Include region causes the room to register as breached immediately. See [Room Caveats](#rooms-caveats) for the atmosphere regulator probe issue.

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

Creates a hardpoint asset with weighted `ModuleEntry` asset references and an optional empty-spawn weight. Used for ship spawn points and module attachment points.

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

#### RoomSubVolumeDefinition (green boxes in overlay)

Defines the pressurised volume.
- At least one `Include` mode volume is required
- `Exclude` mode volumes carve out space from Include volumes
- The `Center` field is a world-space offset applied after the parent Transform — edit it in the Inspector

#### RoomOpeningDefinition (red boxes in overlay)

Defines how volumes connect. The `Type` field:

| Type | Name | Behaviour |
|---|---|---|
| 0 | Block | Wall — no air flow |
| 1 | Portal | Door opening. Game hatch prefabs already include their own portal, so you do not need to add one manually. |
| 2 | Overlap | **Required** wherever two different rooms' SubVolume boxes intersect in 3D space |

The red arrows show the **flow axis** — which direction air flows when the opening is breached.

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

**Real issue discovered during the Rocinante build:** Every baked `PRF_Panel_4xBx4` has a `RoomOpeningDefinition` with `Type = Overlap` and a default Size of `(3, 1.25, 3)`. When a wall panel is positioned adjacent to an atmosphere regulator, that 3×3 overlap volume can cover the regulator's probe position — preventing the probe from being claimed by the room, which breaks the interaction button entirely.

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

These are issues discovered during real ship building (primarily the Rocinante mod) that are non-obvious and not directly surfaced by any tool.

**Convex colliders fill hollow meshes**
A convex `MeshCollider` on a ring or cylinder fills the interior with an implicit convex hull — the player cannot enter the inside. Use the **Segment Meshes** tool to split the mesh into wedge-shaped children, each of which is individually convex. The root collider stays enabled (required for the game to register the part on load) but the wedge children handle actual physics.

**Read/Write must be enabled on FBX imports**
The game reads raw vertex data from `MeshFilter.sharedMesh` to calculate part mass. If Read/Write is disabled on the FBX, the mesh data is not accessible at runtime and the part's mass will be wrong (often zero). The Custom Part Wizard enables this automatically; check manually for FBX files imported outside the wizard.

**Non-unit scale is invisible to the game's physics systems**
Joint anchor points and mass are calculated from mesh vertex positions. Transform scale is applied visually but does not affect the raw mesh data the game reads. Always run **Lock In Rescale** before building any rescaled part.

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
