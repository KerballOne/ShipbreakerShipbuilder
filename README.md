# Hardspace: Shipbreaker - Shipbuilder
Piepieonline's custom Shipbuilder for Hardspace: Shipbreaker

## Note
A wiki has been created - https://shipbreakermodding.miraheze.org/wiki/Main_Page

It will be a much better source of information moving forward

## Steps to get setup
* Install Unity 2020.3.35f (https://unity3d.com/unity/whats-new/2020.3.35)
* Install ModdedShipLoader (https://www.nexusmods.com/hardspaceshipbreaker/mods/8?tab=description) as per the instructions on that page
* Change the settings of "ModdedShipLoader", and set "enableDeveloperShips" to true 
* Delete the folder "FirstShip.Piepieonline" in "BepInEx\plugins\ModdedShipLoader\Ships"
* Install the ShipbreakerShipbuilder project wherever you want
* Copy (only those with waiting '.meta' files listed) game dlls from 'Shipbreaker_Data\Managed' into the Dll folder
* Update "shipbreaker_settings.json"
  * Add the correct game path (note that you need to use double backslash in the path, eg: "D:\\\\SteamGames\\\\steamapps\\\\common\\\\Hardspace Shipbreaker")
  * Add your username as the author, eg: "Piepieonline" 
* Open the ShipbreakerShipbuilder project in unity
* Accept the popup to upgrade APIs
* Run `Shipbreaker/Update game catalog`
![Shipbreaker menu](Docs/ShipbreakerMenu.png?raw=true)
* Restart unity
* Run `Shipbreaker/Update known assets`
* Restart unity
* Run `Shipbreaker/Build and run`, and ensure that the freeplay menu has update - the Wombat should appear first, and the developer ships should appear at the end

## First steps
* Open "Scenes/SampleScene"
* Navigate to "_CustomShips/ExampleBox"
* Drag "ExampleBox.prefab" into the scene hierarchy (This will take a while on first attempt, as it is caching all used game assets - takes about a minute on my midrange machine)
 * If this doesn't look right, when the Editor has finished loading, `Shipbreaker/Force View Refresh`
* Move and rotate the atmospheric regulator into the box (Hint: Hold ctrl to snap to the grid)
* Save and build - `Shipbreaker/Build`
* Run the game, open the freeplay menu, and find "Example Box" at the end of the list
* Close the game
* In the hierarchy, disable "East", "CutPointER" and "CutPointEB"
* Enable "AirlockHardpoint", then run `Shipbreaker/Force View Refresh` (twice)
* Move the airlock such that the inner wall sits inline with the open space, but not touching any walls
![Airlock position](Docs/AirlockPlacement.jpg?raw=true)
* Clone "CutPointEB", enable it, and move it so that it just touches the floor and the airlock inner wall (It'll need to be rotated 180 degrees)
* Build
* Open the ship in-game
* Congratulations, you've created your first ship!

## First ship from scratch
* Open "Scenes/SampleScene" (important: the "Bay" object used for positioning is only in this scene)
* Create a new folder in "_CustomShips", call it "ScratchShip"
* Add an image to use as the thumbnail to the "ScratchShip" folder
* Right click an empty space inside your "ScratchShip" folder, click `Create/Shipbreaker/Create custom level` (The shipbreaker menu is at the bottom of the create menu)
* Fill in the details, and click Create
  * The "Module Construction Asset" option will influence the exterior (triplaner) texture that gets applied
* The new prefab named "ScratchShip" is now selected in the project window. Drag it into the scene hierarchy
* Create an empty child, name it "Floor", give it a rotation of 0/-90/0
* To "Floor", add an `Addressable Loader` component, and set it's GUID to "15e523455b134fe40b33c5d6a4346fe8"
* Save your scene
* Follow the "Positioning a ship in the bay" steps below to set the spawn location before building - the default position will place the ship off to the side
* Build and run - note the first build after any clean will take longer as it rebuilds everything from scratch

## Positioning a ship in the bay
* Make sure you are in "Scenes/SampleScene" - the Bay object is only present there
* With your prefab in the scene, at position 0/0/0
* Click the "Bay" object in the hierarchy, click the checkbox next to "Reload"
* Close or import TMPPro assets. It doesn't matter either way.
* Move your prefab to where you want it to sit in the bay
* Right click your prefab's transform component, click `Copy World Placement` (at the bottom)
* Double click to open your prefab's RootRef prefab (Inside the Spawn folder), click the Hardpoint, right click the transform component and `Paste World Placement`
* Close the RootRef prefab editor
* Reset your prefab's position to 0/0/0 (the floor will appear to shift - this is expected, the spawn position is stored in the RootRef Hardpoint, not the prefab root)

## Custom ship notes
* The game doesn't work with negative scales (Scaling is weird, tends to break things in general)

## Joints
* Joints are how the game knows to connect multiple separate assets
* Mandatory Joint Containers: These work by connecting all child objects. Useful for attaching trim to a wall, for example (can be cut in game, but won't come apart otherwise)
* StructurePartAsset - Joint setup asset: These work by attaching at runtime, whatever is connected to them - used by things like cutpoints?

## Rooms

### Components
* **RoomContainerDefinition**: Defines how the room behaves. Set the room type via the `Dynamic Room Container Asset` field using the GUIDs below.
* **RoomSubVolumeDefinition** (green boxes): Defines the pressurised volume. At least one `Include` mode volume required. `Exclude` mode volumes carve out space from include volumes. The `Center` field is a world-space offset applied after the parent Transform — edit it in the Inspector.
* **RoomOpeningDefinition** (red boxes): Defines how volumes connect.
  * Type 0 = Block (wall, no air flow)
  * Type 1 = Portal (door opening — game hatch prefabs already include their own portal, so you don't need to add one manually)
  * Type 2 = Overlap (required wherever two different rooms' SubVolume boxes intersect in 3D space)
  * Flow axis (red arrows) defines which direction air flows when breached
* Room overlaps need to be carefully managed — extra or misplaced overlaps will cause instant breaching when loading

### Adding a room to your ship (DummyPlugRoom workflow)

Rooms must live inside an **addressable prefab with a ModuleDefinition** component — a plain GameObject in the ship hierarchy will not have its `AddressableSOLoader` fired at runtime and the room type will not load. The `DummyPlugRoom` prefab (`Assets/_CustomShips/FirstShip/Components/DummyPlug/DummyPlugRoom.prefab`) is a ready-made container for this purpose.

**Each room needs its own separate prefab asset.** You cannot reuse the same DummyPlugRoom prefab for multiple rooms — all instances share the same prefab data, so editing one in context edits all of them. For each room, duplicate the DummyPlugRoom prefab file in the Project window (right-click → Duplicate, or Ctrl+D) and rename it before placing it in the ship.

**To add a new room:**
1. In the Project window, duplicate `DummyPlugRoom.prefab` and give it a descriptive name (e.g. `AftSectionRoom.prefab`).
2. Make sure the new prefab is in the Addressables group for your ship (open the Addressables Groups window, drag the prefab in, or use the Addressable checkbox in the Inspector).
3. Place the new prefab instance in your ship hierarchy where the room should be.
4. To edit room volumes with the ship visible for context: click the **arrow** on the prefab instance to enter **Prefab Mode In Context** (not isolation — the ship mesh stays visible).
5. If you need to add a new SubVolume child: **copy** (`Ctrl+C` or right-click → Copy) the existing `Volume` object while in the outer scene context first, then enter prefab mode in context and **paste**. Dragging across prefab boundaries is not supported by Unity.
6. Edit `Center` and `Size` on each `RoomSubVolumeDefinition` in the Inspector. Run `Shipbreaker/Force View Refresh` to update the green/red box gizmos.
7. Save the prefab (Ctrl+S while in prefab mode, or click the back arrow to exit and save).

**Do not unpack the room prefab instance** into the ship hierarchy — the Room's `AddressableSOLoader` will no longer fire at runtime.

### Room type GUIDs

Set the GUID in the `AddressableSOLoader` component on the `Room` object (the `Refs → Element 0` field) to change the room type:

The GUID goes in the `AddressableSOLoader` on the `Room` object (`Refs → Element 0`). Each asset internally picks from a weighted list of `RoomDataAsset` entries — the default pressurisation state shown below is the typical in-game behaviour for that room type on a standard ship.

| Room Type | GUID | Default pressurisation |
|---|---|---|
| Airlock | `1e2fc202254a9b142821666f0de99c43` | Mixed |
| Bathroom | `1618146055ee06241a21a0a070fcb285` | Mixed |
| Bulkhead | `27f96a65f1a36ce42879c5c6b295e9cf` | Mixed |
| BulkheadStructure | `be0601d7017703647a188f1690c9a487` | Mixed |
| Cabin | `1890b7b43c4fe394fade0ed5247ce74f` | Pressurised |
| CargoBay | `944e7dc3b121bc842a1d206109d5ed3f` | Mixed |
| Cockpit | `c69f6c1382018f447bd3ab232bf02176` | Pressurised |
| Corridor | `f960f0730be516340995562ac0b6e597` | Mixed |
| Crawlspace | `4360c7aed7fee3e42b466b34f1cf2270` | Mixed |
| CrewQuarters | `f7ff5f8c1aed42041b653e9eaa54287b` | Pressurised |
| CrewStorage | `1b66cb083eeef6149b41005abdd173ae` | Mixed |
| Default | `f743858ced3468449a6fbceca8d0dc44` | Mixed |
| ECU | `2ec54053428070f41b789f5af1760d81` | Mixed |
| Engineering | `47ed34b58fc05a642aaad2a75f79d2a5` | Mixed |
| EngineRoom | `c6e8af5db4e6a2f428c077a1ba360950` | Mixed |
| Habitation | `76dc4093ec68f644a89b4c100e58fd55` | Pressurised |
| Laboratory | `978d6afb37c141345b77309092d24f3a` | Mixed |
| MainCompartment | `d5772889c0d17d041a06c56a8a28f286` | Mixed |
| Operations | `30202ba5349db6246948ee8ebbe281f2` | Mixed |
| PassengerStorage | `501cc60840df1f745860c9496b9913f3` | Mixed |
| Reactor | `57308ca44db6e7444939c1c682b40add` | Mixed |
| SalvageBay | `92745bbdc73bbe2468d61f192647841c` | Mixed |
| ThrusterRoom | `35255e7fddb53ec4b84d410aa0947566` | Mixed |
| ThrusterRoom (always unpressurised) | `c3916206ca44e364eae1bad0e4fa602c` | **Always unpressurised** |
| Workshop | `f1d1b2120f26b4e4ba3386ff70936917` | Mixed |

*Mixed = random chance each run. Exact probabilities are defined inside each asset and not directly editable from the prefab.*

### Pressurisation state

Each `DynamicRoomContainerAsset` internally holds a weighted list of `RoomDataAsset` entries. Each `RoomDataAsset` carries a `PressurizationProbability` — this is what determines whether a room starts pressurised. There is no way to select a specific pressurisation variant by swapping a GUID in the `AddressableSOLoader`; the asset itself controls the weighted random selection at runtime.

The GUIDs in the table above are the default asset for each room type, which typically uses `MixedPressurization` (random chance). The only built-in way to guarantee a room starts unpressurised is `ThrusterRoomAlwaysUnpressurized` (`c3916206ca44e364eae1bad0e4fa602c`).

Note: the game's pressurisation logic overrides all probabilities to 0 when loading from a save file (`wasLoadedFromFile = true`).
## Texturing custom models
We can add custom models to the game, and set them so that they get the triplaner texture applied
An example of the process can be found in "Assets/_CustomShips/FirstShip/Components/Shell/ShellConnector.prefab"
1. Create a new material, with the "Fake/_Lynx/Surface/HDRP/Lit" shader (Found in "Assets/_CustomShips/_Common/Shaders/FakeLynxHDSurface.shader")
2. Apply the "Assets/_CustomShips/FirstShip/Materials/FirstShipWalls.png" texture to the BaseColorMap

The top half of this texture will get the triplaner texture applied (as the outside), and the rest will appear as normal

## Inspecting game content
* Find the '.prefab' in known_assets.json, and copy it's GUID
  * For example, the Mackerel airlock:
  * Find the line: "fd038d23f35b59747a22dec2f214b11f": "Assets/Content/Prefabs/Ship Kit/Nodes/Mackerel/Core Segments/PRF_Mackerel_Airlock.prefab",
  * Copy fd038d23f35b59747a22dec2f214b11f (no quotation marks)
* Paste it into 'Asset GUID' of the 'GameInspectorWindow' (If this is missing, open with `Shipbreaker/Show Game Inspector`)
* Click 'Load GameObject'

## Working with game parts

Game parts are assets from the base game that you can place, reposition, rescale, or combine in your ship. There are two ways to bring them in, and understanding which to use is important.

### Importing a game part: Bake vs Import Addressable

Open the wizard via `Shipbreaker/Import Game Part Wizard`. Search for the part by name, then choose how to import it:

**Bake (recommended for structural parts)**
Baking downloads the addressable asset and converts it into a self-contained prefab in your project. The geometry, colliders, StructurePart, and EntityBlueprintComponent are all embedded — no runtime address lookup required.

Use baking when:
- You need to **rescale** the part (the game reads joint anchor points and mass from mesh geometry; non-unit scale is invisible to those systems without baking)
- The part is purely structural (wall panels, floor tiles, hull pieces)
- You want a fully independent prefab you can modify freely

Do not bake when:
- The part has **animations or complex runtime behaviour** (doors, airlocks, reactors) — baking strips the addressable loader and those systems will stop working
- The part loads child content via its own `AddressableSOLoader` at runtime

**Import Addressable**
Places a lightweight loader GO in your hierarchy that tells the game to stream the part at runtime, identical to how the base game places it.

Use this when:
- The part needs to remain a live addressable (animated doors, functional subsystems, powered parts)
- You are not rescaling it
- You want the game to manage its lifecycle exactly as it would on a stock ship

### Repositioning and rescaling

After placing a baked part, you can freely reposition and rotate it using the standard Unity transform tools.

If you **rescale** a baked part (non-unit scale on the transform), the game's joint and mass systems will not see the scaled geometry — they read directly from mesh bounds and vertex data. You must lock in the scale before building:

1. Select the part in the hierarchy
2. In the Inspector, the **Addressable Component Loader** will show a **Lock In Rescale** button when a non-unit scale is detected
3. Alternatively, right-click the Transform component → **Lock In Rescale**
4. This writes the scale into the mesh geometry and resets the transform to (1,1,1)

The pre-build validator (`Shipbreaker/Build`) will warn you if any part with a `StructurePart` component still has a non-unit scale — fix these before building.

### Deleting a part

When you delete a part GO from the hierarchy, any `AddressableComponentLoader` on its parent that referenced components on that GO will have **null/missing entries**. These broken entries will cause a crash at runtime.

After deleting a part:
1. Select the parent GO that holds the `AddressableComponentLoader`
2. In the Inspector, expand each entry in the ACL list — entries showing **Missing Component** are dead references
3. Remove each missing entry using the `−` button
4. Save the prefab

The pre-build validator will catch any remaining null ACL entries as an **error** and block the build until they are cleaned up.

### Copying a part or adding a new one

When you duplicate an existing part GO (Ctrl+D or right-click → Duplicate), the new GO's child components (`StructurePart`, `EntityBlueprintComponent`) are not registered in any parent `AddressableComponentLoader` — so the game will not load their asset data at runtime and the part will not appear in-game.

After duplicating or adding a new part:
1. Reposition it as needed
2. Right-click the new GO in the hierarchy → **Register Components in Parent ACL**
3. The tool walks up to find the nearest ancestor with an `AddressableComponentLoader`, finds all `StructurePart` and `EntityBlueprintComponent` components under your GO, and appends the correct entries — reusing addresses already present in that ACL for the same component types
4. Save the prefab

> **Note:** The parent must already have ACL entries of the same component types (i.e. at least one existing panel of the same kind), so the tool can infer the correct asset addresses. If the ACL has no matching entries to copy from, the tool will tell you rather than add broken entries.

If you are adding parts to a **nested prefab instance** (a GO with the `>` arrow badge in the hierarchy), prefer opening that nested prefab in isolation and making the addition there — otherwise your new GOs are prefab overrides and may not behave correctly at runtime. Use the **Overrides** dropdown in the Inspector to apply overrides back to the base prefab on disk when you are satisfied with the result.

### Lock In Rescale checklist

Before building, confirm:
- [ ] All rescaled parts have had **Lock In Rescale** applied (transform reads (1,1,1))
- [ ] All deleted parts have had their **ACL entries removed** from the parent loader
- [ ] All duplicated or new parts have been **registered in the parent ACL**
- [ ] The validator (`Shipbreaker/Build`) reports no errors and no scale warnings

## Other important gotchas
* If something doesn't load correctly, make sure that everything that needs to be marked as addressable, is!
* I am caching all (game based) addressables at the moment. This means that the view of an addressable prefab won't update unless you remove it's prefab from "Assets/EditorCache"
* When using asset references, it must be the assets GUID, not the addressable name/path
* Saving the scene will save all overrides to the top level prefabs that are loaded - this is normally very helpful, but it is something to be aware of.

## Other interesting information
The BBI devs did us a massive favour, and released a bunch of documentation on their tools: https://drive.google.com/file/d/12gYTLHTgoeJLBlpVMsv4WfJC9cMHxcd5/view
While extremely interesting, it's direct application is limited.
Read it to get a better understanding of how the systems fit together (especially pressurisation), but don't expect processes detailed by them to work for us.

## Contact
Find me on the [Shipbreaker discord - #modding-discussion](https://discord.gg/shipbreakergame)

## Expected Unity Exceptions
There are a bunch of exceptions that will not impact the process, as follows. Those marked **[suppressed]** are filtered by the project's LogHandler and will not appear in the console.
* "Broken text PPtr. GUID 000..." **[suppressed]**: Appears when something is referring directly to in-game content that needs to be properly referenced or cloned.
* "Could not extract GUID in text file" **[suppressed]**: As above
* "Burst error BC1054: Unable to resolve type": Unknown, harmless
* "System.Reflection.ReflectionTypeLoadException" followed by "System.NullReferenceException": Unknown, harmless
* "Could not Move to directory Library/com.unity.addressables/aa/Windows, directory arlready exists." **[suppressed]**: Unknown, harmless
* "Cannot instantiate objects with a parent which is persistent. New object will be created without a parent.": Due to the way I am caching game assets, harmless
* "ThemesSettings.get_Database / ThemeManager" NullReferenceException **[suppressed]**: Triggered by Doozy UI components on game assets loaded into the editor, harmless
