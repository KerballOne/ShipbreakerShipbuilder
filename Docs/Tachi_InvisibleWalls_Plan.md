# Tachi: fewer invisible walls (plate or split)

Plan written 2026-11-26. Nothing built yet.

## Background

| | |
|---|---|
| Problem | Every part collides as its convex hull, so concave parts get invisible walls. Pipes and cut points inside those walls can't be cut, scanned or blasted. A player hit this on 25 Nov with the fuel pipes in the aft dorsal PDC tray. |
| Rule | Fewer invisible walls. **Plate:** seal the open side so what you see matches the collider. **Split:** cut the part so each piece's collider follows its shape. |
| Limits | Never plate a face that's outside the ship (23 Nov rule). Splitting is allowed anywhere. Plates use the PDC housing brick texture. |
| Data | `.claude/mesh_work/wallsurvey/table.json` and `reach_rank.txt`; renders `.claude/bridge/shots/wall_g1_*` and `wall_g2_*`. |

## Parts

All paths start with `Tachi/`.

| # | Group | Parts | Path | Do | Priority |
|---|---|---|---|---|---|
| 1 | PDC side-frame trays | `PDC_Aft_{Dorsal,Ventral}_New_Housing_{Top,Bottom}` (4) | `B_Section/B_External/Engineering/Engineering_Objects/` | Plate | 1 |
| 2 | PDC end frames | `PDC_Aft_{Dorsal,Ventral}_New_Housing_Frame_{Fore,Aft}` (4) | same | Split | 1 |
| 3 | PDC nest hulls | `Engineering_25`, `Engineering_27` (2) | `B_Section/B_External/Engineering/` | Split | 1 |
| 4 | Mid hull vents | `Vents_Stbd`, `Vents_Port` (2) | `A_Section/A_Hull/MJC mid stbd/`, `MJC mid port/` | Split | 2 |
| 5 | Wide heat exchangers (U-shaped) | `Heat_Exchanger.005_1 .001_2 .004_1 _1 .006_2 .007_1 .003_2 .002_2` (8) | `B_Section/B_External/Engineering/Heat_Exchanger/` | Split into base + 2 legs | 3 |
| 6 | Narrow heat exchangers | `Heat_Exchanger.003_1 .007_2 .004_2 .005_2 _2 .001_1 .006_1 .002_1` (8) | same | Plate | 3 |
| 7 | Engineering bevelled hull pieces | `Engineering_15 18 8 19 12 10 5 14 23` (9) | `B_Section/B_External/Engineering/` | Plate | 4 |
| 8 | Engineering deep pocket | `Engineering_4` (1) | same | Split | 4 |
| 9 | Engineering floor edges | `Engineering_Floor_seg01 seg02 seg05` (3) | same | Plate | 4 |
| 10 | Filters | `Filter1`, `Filter2` (2) | `.../Engineering/Engineering_Objects/` | Plate | 4 |
| 11 | Fore hull vents | `Vent_Stbd`, `Vent_Port` (2) | `A_Section/A_Hull/MJC fore stbd/`, `MJC fore port/` | Split | 5 |
| 12 | Torpedo collars and tubes | `TubeCollar_Top_1 _2 _3`, `TubeCollar_Bottom_2 _3`, `A_Torpedo_Tube_Top`, `A_Torpedo_Tube_Bottom` (7), plus `MJC mid ventral/TubeCollar_Bottom_1` (1) | `A_Section/A_Objects/` (collars under `Torpedoes/`); `A_Section/A_Hull/` for the last | Plate | 5 |
| 13 | Engine bell | `1_Engine_Bell_seg01 03 04 05 06 08` (6) | `B_Section/B_External/Engine/Engine_Bell/` | ~~Split~~ Dropped 26 Nov (KerballOne: no engine bell or nozzle) | – |
| 14 | No change | 8 `Thermal_Radiator*` (yankable, open once group 5 is split), `Engineering_Ceiling_seg06/08`, `ReactorHousing_00/06` (not reachable) (12) | | Leave | – |

| Totals | Plate 34 | Split 25 | Leave 12 | **71** |
|---|---|---|---|---|

## Method

| | Plate | Split |
|---|---|---|
| Tool | `.claude/pocket_plate.py`: adds 2 cm slabs on the collider faces on the inboard side. The outline, collider and existing triangles stay unchanged. | Blender bisect and cap (`.claude/mesh_work/pdc_mount_split/split_blender.py`). The convex-hull split tool for curved parts (bell, vents). |
| Changes needed | Accept any part from the survey (it only knows Engineering parts and the old vent names now). Take the UVs from a PDC housing plate so the brick texture lines up. Add the outside-face test from `.claude/plate_fix/undo_find.py`. | Choose cut planes so each piece is roughly convex. Cap the cut faces 1 mm apart so the pieces joint to each other. |
| Into Unity | `Models/ConvexFix/<part>.asset`, then bridge `meshassign`, then `save` immediately. | Bridge `splitpart` (pieces keep the part's structure setting, blueprint and collider, and get registered), then delete the disabled original with `deletemany`. |
| Part count | No change. | Plus (pieces − 1) per part, logged in `.claude/parts_ledger.md`. |
| Pipes inside | The 20 Nov rule: move the cut point upstream to the wall and leave a short half-buried pipe piece. Use caps where pipes touch a filter or vent. Ask before cutting the fuel risers in Engineering 15 and 18. | They become reachable. |
| Special | – | Vents: the pieces keep the vent's joint type, and the whole vent must still come off with one yank. |

## Order

| Step | Work | Check |
|---|---|---|
| 1 | Backup (`.claude/backup_ship.py`) | – |
| 2 | Pilot plate on `PDC_Aft_Dorsal_New_Housing_Top` and pilot split on `PDC_Aft_Dorsal_New_Housing_Frame_Fore`, then render both (`.claude/wallsurvey/f2_prep.py`, `g_render.py`) | Look in Unity before anything else is done |
| 3 | The rest of priority 1 (both PDC nests) | Renders |
| 4 | Priority 2 (mid hull vents) | Yank test in game |
| 5 | Priorities 3–6, one group at a time | Renders for each group |

## Verification

| Check | Tool | Pass |
|---|---|---|
| Invisible walls | `.claude/wallsurvey/` survey re-run (a_points, b_classify, d_reach, c_rank) | Changed parts drop off `reach_rank.txt` |
| Hidden pipes | `.claude/convex_audit.py` | Fewer than the 69 found on 26 Nov, for these parts |
| Joints | `.claude/relcheck/joint_new.py`, Joint Assist, overlap audit | Pieces joint; no new loose parts |
| Prefab | Bridge `validate` and `aclrepair` (dry run) | 0 issues |
| Part budget | Parts ledger and the `[JointBudget]` log line | Headroom stays positive |
| In game | F10 census; cut or blast the trapped fuel pipes; yank each vent | All pass |
| Bundle | Size after each batch | No big jump |

## Status, 26 November

| # | Group | Result |
|---|---|---|
| 1-3 | PDC nests, Engineering_25/27 | Done: 4 side frames plated, 4 end frames split (bridge + 2 legs), E25/27 split into wall + roof |
| 4-5 | Vents | Left as they are (user) |
| 6-7 | Heat exchangers | All 16 split into block + rods (rods capped); room-facing walls on the blocks remain (~127 m2) |
| 8 | Bevel hulls | Engineering_8 and _23 plated; the rest open partly to outside, left |
| 9 | Engineering_4 | Not done |
| 10-11 | Floor edges, filters | seg02/05 and both filters plated; seg01 had nothing reachable |
| 12 | Torpedo collars/tubes | 6 plated; TubeCollar_Top_1 and _Bottom_2 had nothing reachable |
| 13 | Engine bell | Dropped (user) |

Survey (audit 8): room-facing invisible area 352 -> 240 m2.
