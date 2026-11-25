# Tachi: Ship Statistics

A custom salvage ship for Hardspace: Shipbreaker, modeled on the MCRN Corvette-class light frigate *Tachi* from *The Expanse*.

Version 0.9.12, updated 25 November 2026. The figures are the first v0.9.12 figures (24 November) less 6 parts on every variant: the six point defense mounts were merged into their trusses and gun bodies. The Rocinante was loaded on this build and the game counted 5,221 parts, as calculated. The hanging suit's mesh was reduced, so the largest mesh is now a stock helmet (14,996 vertices) and the part ceiling rose to 8,950. The first v0.9.12 figures were the v0.9.11 figures below changed by simple arithmetic from the parts log kept while building, not a new census: on every variant 5 slot covers close the gaps in the Engineering ceiling, the four generators became the stock generator with its 3 status lights each (12 lights), 6 PDC ammo posters hang on the ammo crates, and the 8 reactor injector nozzles (6 pipe pieces each) became a 5-part thruster nozzle (8 parts fewer); the Rocinante also gains its railgun ammo poster and 2 generators on the railgun breech (17 parts). The Rocinante was then loaded on this build: the game counted 5,211 parts, 2 fewer than calculated. After that load, 16 reaction mass tanks were hung under the cryo deck plates on every variant (16 parts). A second Rocinante load with the tanks counted 5,227, as calculated. The v0.9.11 figures were the v0.9.10 measurements below changed by simple arithmetic: the cryo ring was rebuilt with longer pipes (12 straight pipes fewer, 2 cut points more, so 10 parts fewer on every variant), and ghost ships carry a 60th Protomolecule node. The Rocinante's count was logged on a load of this build (5,181 parts, normal ship). The paragraph below describes the v0.9.10 measurements. The MCRN Tachi figures, its categories and its loose parts are from a load of the v0.9.10 build on 23 November, with the mod's spawn census. The Gas Hauler's part counts, loose parts and cut points are from a load of the same day as a ghost ship; its normal-ship counts are those less its 59 nodes. The Rocinante was not loaded on this build: its figures are its v0.9.9 counts plus the 94 parts the base ship gained (the point defense housings rebuilt, the rings cut in eight, the fore hull regrouped). The section tables are from the v0.9.8 census and were not re-measured. "Measured" means taken from the game or the editor on that build. "Estimate" means calculated, with the method given.

## At a glance

| | |
|---|---|
| Length (engine bell to antenna tip) | 133.7 m |
| Beam (port to starboard, over the gun mounts) | 54.6 m (Gas variant, over the tank racks: 67.2 m) |
| Height (dorsal to ventral) | 46.3 m (Gas variant: 55.1 m) |
| Parts counted at load | 5,319 (MCRN Tachi) · 5,221 (Rocinante) · 5,744 (Gas variant), all v0.9.12 |
| As a ghost ship | 60 Protomolecule nodes and 252 growth patches more (see Ghost ship content) |
| Cut points | 260 (Gas variant: 343) |
| Loose items at spawn | 183 (props, pickups and one floating corpse, in one of six places) |
| Estimated mass | at least 268 t (Gas variant: about 394 t; see Mass) |
| Download size | 198 MB (zip, all three variants) |
| Load time on the test PC | 45 to 138 s |

---

## 1. Loading and technical

### Load time

Measured on the test PC, from choosing the ship to being able to move:

![Load time on the test PC](Tachi_Load_Time.png)

| Run | Total | Loading assets | Jointing to playable |
|---|---|---|---|
| 17 Nov | 44.7 s | 28.0 s | 16.7 s |
| 18 Nov, after a fresh build | 137.7 s | 119.8 s | 17.9 s |
| 18 Nov, v0.9.5 | 126.8 s | 97.1 s | 29.7 s |
| 19 Nov, v0.9.6 | 45.2 s | 28.2 s | 17.0 s |
| 19 Nov, v0.9.6 | 53.6 s | 37.0 s | 16.6 s |
| 20 Nov, after a fresh build | 61.9 s | 44.3 s | 17.7 s |
| 20 Nov | 49.0 s | 30.6 s | 18.4 s |
| 20 Nov, Gas variant | 59.3 s | 42.4 s | 17.0 s |
| 20 Nov, Gas variant, raised pose | 49.8 s | 33.2 s | 16.6 s |
| 20 Nov, v0.9.7 | 55.7 s | 38.0 s | 17.8 s |
| 21 Nov, v0.9.8, MCRN Tachi | 53.8 s | 37.3 s | 16.4 s |
| 21 Nov, v0.9.8, Rocinante as a ghost ship | 68.1 s | 50.2 s | 17.9 s |
| 22 Nov, v0.9.9, Gas variant | 71.3 s | 36.9 s | 34.4 s |
| 23 Nov, v0.9.10, MCRN Tachi | 47.2 s | 37.0 s | 10.2 s |
| 23 Nov, v0.9.10, Gas variant as a ghost ship | 62.2 s | 49.5 s | 12.7 s |
| 24 Nov, v0.9.12, Rocinante | 50.7 s | 40.6 s | 10.1 s |
| 24 Nov, v0.9.12, Rocinante with the tanks | 50.0 s | 39.9 s | 10.1 s |

The v0.9.5 run had the mod's part-data capture switched on, which adds load time, so it overstates a normal load.

Test PC: AMD Ryzen 7 5800X (8 cores), 64 GB RAM, NVMe SSD, RTX 4070 Ti.

The last stage takes about 10 to 35 s. The asset-loading stage varied more than threefold between runs on the same machine, so expect a wide spread; the two longest runs each followed a fresh build of the ship.

**Expected load time on other hardware.** These are estimates scaled from the shortest and longest runs above, not measurements. No other machine has been timed.

| PC | CPU class | RAM | Storage | Expected load |
|---|---|---|---|---|
| High end | Ryzen 7 5800X, Core i7-11700K or newer | 32 GB or more | NVMe SSD | 45 s to 2.5 min (measured on 64 GB) |
| Mid range | Ryzen 5 3600, Core i5-10400 | 16 GB | SATA SSD | 1 to 3.5 min |
| Older | Ryzen 5 2600, Core i5-8400 | 16 GB | Hard disk | 2 to 6 min |
| Minimum | any | 8 GB | any | untested; may fail to load |

If the game sits on the loading screen for more than ten minutes, treat it as a failed load.

### Parts

![Part budget](Tachi_Part_Budget.png)

| | |
|---|---|
| Live parts at spawn (every piece the game simulates) | 5,321 (v0.9.12; 5,296 on v0.9.11) |
| Parts counted by the game's joint pass | 5,319 (v0.9.12; 5,294 on v0.9.11) |
| Objects in the ship that never become parts | 260 (see below) |
| Largest mesh | `SM_Bodysuit_Mesh_Helmet` (a stock suit helmet), 14,996 vertices |
| Part ceiling at that mesh size | 8,950 |
| Headroom | 3,631 parts |

**The part ceiling.** While the ship loads, the game multiplies the number of parts by the vertex count of the largest single mesh. If that product passes 134,217,728 the game crashes on load. With the current largest mesh that allows 8,950 parts. Adding parts, or adding one mesh larger than 14,996 vertices, both use up the headroom. The rebuilt point defense housings took the ship's own largest mesh (18,291 vertices) below the stock suit's, and in v0.9.12 the hanging suit's mesh was reduced from 17,136 to 4,813 vertices.

**Objects that never become parts.** 260 objects in the ship are switched off by the game's own prefabs and are not simulated:

| Object | Count |
|---|---|
| Ladder rungs | 248 |
| Room cage bars | 4 |
| Other (railings, spare suit meshes, a spare part, a data drive, the old airlock entry top) | 8 |

### Parts by category

![Parts by category](Tachi_Parts_By_Category.png)

| Category | Parts | Share |
|---|---|---|
| Furniture and props | 1,730 | 32.5 % |
| Decks, walls, stairs and doors | 1,015 | 19.1 % |
| Machinery, power and weapons | 840 | 15.8 % |
| Pipes and cables | 802 | 15.1 % |
| Cut points | 260 | 4.9 % |
| Lights | 258 | 4.8 % |
| Hull panels | 212 | 4.0 % |
| Structural frame | 204 | 3.8 % |

The category table is from the v0.9.10 census of the MCRN Tachi. Against v0.9.9 the rebuilt point defense housings add 91 machinery parts, and the regrouped fore hull adds 4 cut points. In v0.9.11 the rebuilt cryo ring has 12 straight pipes fewer and 2 cut points more. In v0.9.12 the posters and the 16 reaction mass tanks add 22 furniture parts, the generator status lights 12 lights, the ceiling slot covers 5 hull panels, and the new injector nozzles count 8 pipe parts fewer. The six point defense mounts were also merged into their trusses and gun bodies (6 machinery parts fewer). The section tables are from the v0.9.8 census of the Rocinante, with the MCRN Tachi's 106 extra furniture parts added to the interior, where they all are. Four pipe pieces at the fore thermal vents carry the cut point type so that they do not joint to the vents; they are counted here as pipe.

Categories are assigned from each part's joint type and name. The rules were rewritten for v0.9.5, so these shares are not comparable with earlier versions of this document.

### Parts by section

![What each section is made of](Tachi_Parts_By_Section.png)

| Section | Parts | Largest categories |
|---|---|---|
| Interior | 4,220 | Furniture and props 38 %, decks and walls 21 %, pipes and cables 17 % |
| Aft interior | 464 | Pipes and cables 23 %, decks and walls 23 %, machinery 22 % |
| Forward hull | 271 | Hull panels 56 %, machinery 28 %, structural frame 7 % |
| Aft hull and engine | 257 | Structural frame 34 %, machinery 23 %, hull panels 22 % |

The floating corpse and its suit (14 parts) spawn in one of six places. In this census they were in the interior; when they spawn in the engine bay they count in the aft interior instead.

### Loose parts

At spawn the main structure is one body of 5,138 parts (v0.9.12, calculated: every part added since the census is jointed to the ship). Two fittings are loose in this build: a railing and a thruster terminal.

| | |
|---|---|
| Main structure | 5,138 parts in one body |
| Loose parts | 183, in 159 separate bodies |
| Loose single items | 150 |
| Loose bodies of more than one part | 9 |

Apart from the railing and the terminal, all 183 loose parts are items meant to float or be picked up:

![Loose parts by type](Tachi_Loose_Parts.png)

| Type | Parts | Share |
|---|---|---|
| Galley items (bowls, food, coffee maker, vending parts) | 38 | 20.8 % |
| Bottles | 32 | 17.5 % |
| Supply pickups (oxygen, thruster fuel, med and patch kits) | 20 | 10.9 % |
| Other (ten spare-part pickups, wall chestplates, a repair kit, a toy, two salvage bay objects, a railing, a thruster terminal) | 19 | 10.4 % |
| Snack packs | 19 | 10.4 % |
| Suit pieces and helmets | 18 | 9.8 % |
| Floating corpse and its suit | 15 | 8.2 % |
| Data and credit drives | 15 | 8.2 % |
| Crates and pouches | 5 | 2.7 % |
| Desk items | 2 | 1.1 % |

The loose bodies of more than one part:

| Body | Parts |
|---|---|
| Floating corpse | 13 |
| Bowl, spoon and food set | 5 |
| Coffee maker, pot and vending part | 3 |
| Six pairs of small items | 2 each |

### Pipes, cut points and hull colliders

Every hull part collides as the convex shape wrapped round it, not as its visible mesh. On the folded hull panels of the engineering section that left invisible collider faces across the pockets where the fuel and coolant pipes run, and put pipe cut points out of reach. The 20 November rework changed three things:

| | |
|---|---|
| Hull parts given inside plates | 12 (11 engineering wall panels and the sealed hatch; the two mid thermal vents were returned to their original shape, see below) |
| Plate area added | 50 to 210 m² per part; each part's outline and collider are unchanged |
| Pipe cut points at spawn | 93, every one jointed and none fully inside a collider |
| Inline cut points on the cryo ring | 14, the ventral pair mirroring the dorsal pair; one of them reaches 1.4 cm into a hull part's collider |
| Pipe pieces loose at spawn | 0 |

- **Inside plates.** Each of the 14 parts is closed along its own collider on every face except those on the outside of the ship, so what blocks the player is now visible.
- **Cut points.** Each fuel and coolant line keeps its start on the interior wall. Its cut point sits on the last stretch of pipe that is clear of the part's collider, as close to the part as it fits, and one short piece of pipe runs on into the part.
- **Coolant ring.** The ring runs straight through the hull panels, with inline cut points on each open stretch. The stretch under the ventral panel has no open pipe and no cut point of its own.

Four cut points still reach a few centimeters into a collider (up to 13 cm) where there was less than 25 cm of open pipe.

**Thermal vents.** The four thermal vents are built from a material that accepts fuel, and glowed white in game wherever a fuel pipe, junction box or the game's pipe cut point jointed to them. The cut points beside all four vents are now baked cut points of the plain chassis type, and each fore riser ends in a bend and a pipe of the same type running into its vent. Those pieces look like fuel pipe but carry nothing and do not joint to the vents.

### Ghost ship content

When any of the three ships is taken as a ghost ship it carries the following. None of it is on a normal ship.

| | |
|---|---|
| Protomolecule nodes | 60: 2 to 5 on every deck, 8 on the two crawlspace generators, 8 on the reactor (one a large crystal), 5 on the cryogenics coolant tanks |
| Node types | 3 crystal shapes (cluster, spire, ridge), picked at random per spot |
| Growth patches | 252: 110 on the hull, 138 on deck walls, floors and ceilings, 4 in Engineering |
| Growth patterns | 4 (colony, streak, ring, lobes) in 4 sizes, 3 to 10 m across |
| Growth on the corpse | 1 patch across its front |

The nodes are the ship's AI nodes: the haunting weakens as they are destroyed and stops when none are left. They scan as "Protomolecule". Sixteen of them sit on a growth patch.

| Area | Growth patches | Nodes |
|---|---|---|
| Hull | 110 | |
| Flight deck | 10 | 2 |
| Command deck | 12 | 3 |
| Deck 9 | 14 | 4 |
| Deck 8 | 12 | 3 |
| Deck 7 | 12 | 3 |
| Deck 6 | 12 | 4 |
| Deck 5 | 11 | 3 |
| Deck 4 | 12 | 3 |
| Deck 3 | 12 | 2 |
| Deck 2 | 5 | 4 |
| Deck 1 | 12 | 3 |
| Deck 0 | 14 | 5 |
| Engineering, reactor and cryogenics | 4 | 21 |

Growth is painted onto the parts when the ship spawns and stays on each part after cutting. It adds no parts. The nodes add 60 parts. Growth is thin on Deck 2 and in Engineering because few surfaces there can take it.

### Variants

The ship comes in three versions, each listed as its own ship in Free Play and on the Career job board, with its own name and picture: MCRN Tachi, Rocinante and Beratnas Gas Hauler. The second and third are prefab variants of the first: the same ship with its own hull textures and, for the Gas Hauler, its own parts and bay pose. All three are in one bundle and share meshes and unchanged textures. All three are in the v0.9.7 release; the game picks one each time the ship is offered, by the weights below.

| | MCRN Tachi (base) | Rocinante | Beratnas Gas Hauler |
|---|---|---|---|
| What differs | | Colors as an independent warship, keel railgun with its ammo crate and 2 generators on the breech, Belter posters, less furniture | Colors, 24 external tanks on trusses, point defense cannons removed, own bay pose, Belter posters, less furniture |
| How it is picked | Own ship entry | Own ship entry | Own ship entry |
| Live parts at spawn | 5,321 (v0.9.12, see Parts) | 5,223 (logged) | 5,746 (5,806 as a ghost ship) |
| Parts counted by the joint pass | 5,319 | 5,221 as a normal ship (logged), 5,281 as a ghost ship | 5,744 as a normal ship, 5,804 as a ghost ship |
| Headroom under the part ceiling | 3,631 | 3,729 (3,669 as a ghost ship) | 3,206 (3,146 as a ghost ship) |
| Main structure | 5,138 | 5,045 | 5,616 as a ghost ship |
| Loose parts | 183 in 159 bodies | about 180 in 156 bodies | 190 in 160 bodies, as a ghost ship |
| Cut points | 260 | 260 | 343 |
| Size (length x beam x height) | 133.7 x 54.6 x 46.3 m | same | 133.7 x 67.2 x 55.1 m |
| Estimated mass | at least 268 t | same | about 394 t (see Mass) |
| Wall posters at 30 spots | MCRN | Belter | Belter |
| Crew bunks (berths) | 28 | 12 | 12 |
| Medbay beds | 5 | 5 | 5 |
| Galley tables | center, long and 3 corner tables | center and long table | center and long table |
| Deck carpets | red on the command and weapons decks, blue in the medbay and crew decks, green in the galley, gray on the airlock deck | same | same |
| Floating corpse | one, in one of 6 places, as one of 7 factions, each with its own suit colors and visor | same | same |
| As a ghost ship | 60 Protomolecule nodes, 252 growth patches | same | same |

Figures are the v0.9.10 measurements (MCRN Tachi and Gas Hauler, loads of 23 November) less the 10 parts the cryo ring lost in v0.9.11, plus the v0.9.12 changes (15 parts on every variant, 17 more on the Rocinante); ghost counts include the 60th node. The Rocinante's v0.9.11 joint-pass count and main structure were logged on a load of that build.

**Interior differences.** The three ships share one interior, with these differences:

- **Posters.** Thirty wall posters are MCRN posters in the MCRN Tachi (MCRN Colors, MCRN Logo, MCRN Corvette, Mars Needs You) and Belter posters in the other two (OPA, three Remember the Cant designs, Beltalowda, Tycho, Ceres Water). The poster objects are the same; only the picture differs.
- **Bunks.** The six crew cabins on Decks 3 and 4 hold 14 two-tier bunks in the MCRN Tachi (28 berths) and one two-tier bunk each in the other two (6 bunks, 12 berths). The 5 medbay beds are in all three and are not counted as crew bunks.
- **Ammo posters.** All three carry a point defense ammo poster on one crate of each of the six ammo stacks (crawlspace and weapons storage). The Rocinante adds a railgun slug poster on the railgun's loader crate in Weapons Control.
- **Galley.** The other two ships have no corner tables: three tables, six chairs and the three bottles on them are switched off. The center table and the long table are in all three.

Switched-off objects are still in the download, so the size is the same. These differences were checked in the editor, not yet in game, and the part counts above were measured before them.

**Gas variant tank racks**

| | |
|---|---|
| Tanks | 24: 16 large and 8 small |
| Truss parts | 78 (24 beams, 35 feet, 19 links), aluminium structure |
| Hull cut points holding the racks | 35 |
| Tank cradle cut points | 48 |
| Joints between truss and hull other than through cut points | 0 |
| Large tank body mass | 7,500 kg (28,689 kg before hollowing) |
| Small tank body mass | 5,500 kg (7,879 kg before hollowing) |

The racks are held to the ship only by cut points: in the census every foot joints to its beam and to a cut point, and every hull cut point joints to a hull part.

The tank masses are calculated with the game's own formula (material density times the signed volume of the part's mesh) and reproduce the figures the game reported before the change. Each tank body has an inward-facing inner shell that subtracts volume, so the outside size is unchanged. The new masses have not yet been read back in game.

**Gas variant bay pose.** The Gas variant is too wide to sit between the light jacks, so it spawns level, nose toward the Master Jack, raised above them: the ship's axis is 49.5 m above the bay's center line. The engine bell and reactor housing are 5 m above the tallest light jack. One tank hangs below that level and clears the nearest light jack by 7.5 m. No mod is needed to make room for it.

Earlier versions of this document listed the engine nozzle, the reactor and a coolant pipe network as separate bodies. That was a counting error in the analysis, not a fault in the ship; the census now records exact joints.

---

## 2. In-game statistics

### Dimensions

| | |
|---|---|
| Length | 133.7 m |
| Beam | 54.6 m |
| Height | 46.3 m |
| Decks | 12 (Deck 0 to Deck 9, Command, Flight) |
| Hull envelope volume (estimate) | about 68,000 m³ |

The volume is an upper estimate: the outline of the hull parts was taken every meter along the ship and the slices added up. It counts the space inside the hull's outline, not the solid material, and it excludes the gun mounts and thrusters.

### Mass

Estimated total: **at least 268 t**.

The game works out a part's mass from its material density and the volume of its mesh. This figure adds that up for every custom mesh, and uses the mass the game reported for each stock part. It covers 2,592 of 3,461 objects (75 %). The rest could not be resolved, and stock assemblies made of several pieces are counted by their main piece only, so the true figure is higher.

**Gas variant mass.** About 394 t, an estimate built on the base figure: 268 t, less up to 38 t for the point defense cannon parts the variant removes, plus 164 t for the 24 tank bodies (16 at 7,500 kg and 8 at 5,500 kg). The trusses, tank end caps and cradle cut points are not included, so the true figure is somewhat higher.

### Cut points and joint groups

| | |
|---|---|
| Cut points | 260 |
| Forced joint groups (MJC) | 94 (counted on an earlier build) |
| Reactor mount | 4 slanted beams, each tied to a reactor panel and the cryo room floor by a cut point |
| Forward hull groups | 9, tied by 15 internal cut points; the largest ventral panel is a group of its own |
| Forward section to Engineering | 4 internal cut points |
| Torpedo salvage value | 1,218,750 credits each, 12 aboard (flat value from the blueprint) |

### By section

| Section | What it is | Live parts | Size, length × beam × height (m) | Est. mass (t) | Cut points |
|---|---|---|---|---|---|
| A_Section | Forward outer hull, gun mounts, torpedo tubes, RCS | 271 | 81.1 × 54.6 × 31.3 | 113.0 | 15 |
| Interior Vessel | Decks, tunnel walls, crawlspace, furnishings | 4,220 | 90.9 × 26.2 × 28.0 | 49.1 | 193 |
| B_External | Engineering hull, support ring, engine | 257 | 54.4 × 28.0 × 46.3 | 70.9 | 32 |
| B_Internal | Aft airlock, cryogenics, fusion reactor | 464 | 28.8 × 23.1 × 21.0 | 34.6 | 14 |

Sizes and masses were measured on an earlier 18 November build and not re-measured since; the changes since then (a reactor mount of 12 parts, two roof generators, spare-part pickups, the inside plates) do not move them noticeably. The live-part and cut-point columns are from the v0.9.8 census and were not re-measured for v0.9.10, which adds parts to the point defense housings and the forward hull. Section masses carry the same caveat as the total. The interior figure is the least complete, because it is mostly stock assemblies.

---

## 3. The ship in *The Expanse*

### What is modeled on this ship

| | |
|---|---|
| Point defense cannons | 6: fore port, fore starboard, mid port, mid starboard, aft dorsal, aft ventral |
| Torpedo tubes | 2 (dorsal and ventral) |
| Torpedoes aboard | 12: five per tube plus two on ceiling racks |
| Railgun (Rocinante only) | 1 keel railgun, loaded from Weapons Control; its loader crate holds 20 slugs |
| Crew bunks | 28 berths in 14 two-tier bunks (12 in 6 bunks in the Rocinante and the Gas Hauler) |
| Medbay beds | 5 |
| Acceleration couches | 32 |
| Main drive | 1, with a 16-segment nozzle and 8 fuel injectors |
| Reactor | 1 fusion reactor |
| Reaction mass tanks | 16, hung under the cryo deck around the reactor |
| RCS thruster housings | 7 |
| Heat exchangers and radiators | 8 of each |
| Airlocks | 2 (forward and aft) |
| Layout | Tower: decks stacked along the thrust axis, engine at the bottom |

**Scale.** The show's ship is usually quoted at 46 m long. This model is 133.7 m, about 2.9 times that, sized so the decks are walkable in Shipbreaker.

### Corvette-class light frigate, as described in the lore

Sourcing note: the Expanse Wiki could not be read directly when this was compiled. These figures come from a lore site that reproduces the wiki's text and from search summaries, so they are second-hand. They do not separate the TV series from the books. Items marked "unconfirmed" rest on a single secondary mention.

**Class and role**

| | |
|---|---|
| Class | Corvette-class light frigate |
| Operator | Martian Congressional Republic Navy (MCRN) |
| Roles | Fast attack, missile defense, law enforcement, reconnaissance, border patrol |
| Carried by | Donnager-class battleships; the *Tachi* sat in the hangar of the MCRN *Donnager* |
| Standing | Second-smallest MCRN warship |
| Length | 46 m (unconfirmed) |
| Beam, height, mass | not stated |

**Crew**

| | |
|---|---|
| Berths | 30 officers and crew |
| Typical complement | "well over a dozen" naval personnel, plus at least six marines on many missions |
| Minimum crew | not stated |

**Armament as built (*Tachi*)**

| | |
|---|---|
| Point defense | 6 × Nariman Dynamics 40 mm point defense cannons |
| Torpedoes | 2 launchers, 10 torpedoes each (20 total) |
| Drones | 4 reconnaissance drones |

Added later, as the *Rocinante*: a light keel-mounted railgun. One summary gives it 1 kg tungsten rounds at about 9,980 m/s (unconfirmed).

**Propulsion**

| | |
|---|---|
| Main drive | Single Epstein drive; reactor and drive cone at the base of the ship |
| Class acceleration, g limits | not stated |

The lore gives performance figures for the Epstein drive in general, not for this class:

| Epstein drive, general figures | |
|---|---|
| Fusion fuel | Deuterium and helium-3, 1:2 |
| Thrust | 6,370,000 N |
| Specific impulse | 1,927,000 s |
| Exhaust velocity | 18,900,000 m/s (6.8 % of light speed) |
| Mass flow | 2.2 kg/s |
| Thrust power | 60.2 TW |
| Total output | 96.8 TW |

**Decks, bottom to top:** storage and engineering, machine shop, galley and crew cabins, medical bay, storage, crew airlock, command deck, flight deck.

---

## How the figures were obtained

| Figure | Source |
|---|---|
| Load times, joint-pass part count, largest mesh, part ceiling | Logged by the PartInfoLogger mod during loading |
| Live parts, categories, loose parts, cut points per section | The mod's spawn census: base ship from the build of 20 November, 21:52 (the release build), Gas variant from the load of 20 November, 19:59; exact joints between parts |
| Dimensions, per-section sizes | Part bounds in the Unity editor |
| Volume | Calculated from part bounds, as described above |
| Mass | Calculated with the game's own formula from mesh volume and material density, plus game-reported masses for stock parts |
| v0.9.12 figures, 25 November | The 24 November figures less the 6 point defense mounts (merged into trusses and gun bodies); Rocinante checked on a load of 25 November: 5,221 counted, as calculated. Largest mesh and part ceiling from the same load's log |
| v0.9.12 figures, 24 November | The v0.9.11 figures changed by the parts logged while building (`.claude/parts_ledger.md`): 5 ceiling slot covers, 12 generator status lights, 6 ammo posters, 8 injector nozzles of 5 parts replacing 8 of 6 pipe pieces; Rocinante also 1 railgun poster and 2 generators of 8 parts. Rocinante checked on a load of 24 November: 5,211 counted, 2 under the calculation. Then 16 reaction mass tanks on every variant; a second Rocinante load counted 5,227 |
| v0.9.11 figures | The v0.9.10 figures changed by the parts added and removed since: the cryo ring (12 straight pipes fewer, 2 cut points more) and the 60th ghost-ship node. Rocinante: joint-pass count and main structure from the mod's spawn census on a load of 23 November, 11:16 |
| v0.9.10 figures | The mod's spawn census on loads of 23 November: MCRN Tachi as a normal ship, Gas variant as a ghost ship (its normal-ship counts are those less 59 nodes). Rocinante: its v0.9.9 count plus the 94 parts the base ship gained |
| v0.9.9 part counts | The v0.9.8 counts, changed by the objects added, removed and switched off in the ship's files since: none on the MCRN Tachi, 7 fewer on the Rocinante. Checked against the part counts the PartInfoLogger mod logged on two loads of 22 November: Rocinante 5,097, Gas variant 5,687. Loose parts by type: 20 floating data drives became 7 credit drives, 7 oxygen cans and 6 thruster fuel cans |
| v0.9.8 part counts and load times | Logged by the PartInfoLogger mod on loads of 21 November: MCRN Tachi as a normal ship, Rocinante as a ghost ship. The Rocinante normal-ship count is the ghost count less its 59 nodes |
| Ghost ship content | Counted from the spawn points in the ship prefab |
| v0.9.8 categories, loose parts, main structure | The mod's spawn census of the Rocinante as a normal ship, 21 November, with exact joints between parts. MCRN Tachi figures add the 106 furniture parts (3 of them loose bottles) that the Rocinante leaves out |
| Lore | Second-hand, as noted in section 3 |
