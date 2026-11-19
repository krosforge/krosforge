# Combat preparation contract — 3.7.14.18

Field numbers and native types were independently read from the installed metadata and binary, before implementing the encoder. The older server was a semantic discovery aid, not evidence that fields from another build remained compatible.

- Metadata SHA-256: `781890cf890114c5231b5bf04595acbceaf8317f7998fd8053c017de1ae23206`.
- Native assembly SHA-256: `af866f65f0d08eebe9008ffda54462e2814d994c81059fffd766f0d685503ab3`.
- Static metadata evidence: `ev_556f8155ff8ea4e94f819eda09079d60181aaa72d78e8760f2b491c986053e58`.

| Type | Fields used |
| --- | --- |
| `hms` attack request | 2: signed int64 group identity |
| `jxb` placement request | 1: cell; 2: signed int64 player identity |
| `jxj` readiness request | 1: ready boolean; optional field 2 only accepted as false |
| `kjx` forfeit request | empty |
| `kjy`, `kjp` context reset | empty destruction, then field 1 context (1 combat, 0 exploration) |
| `jxi` fight join | 1: fight identity; 6: fight type enum |
| `jxa` combat map preview request | empty |
| `jvk` combat map preview | 1: exploration map; 2: `lfp`; 4: battle map |
| `jnj` map information | 10: map; 12: subarea; 11: combat variant `ldg` |
| `jww` preparation | 2: observed true flag; 4: placement input enabled; 5: fight type |
| `jwj` fighter shown | 1: actor `lcs` |
| `jxk` placement zones | 1: `lfp`; 2: player's team |
| `lfp` | 1: packed red cells; 2: packed blue cells |
| `jws` placement change | 2: repeated `kzo` positions (actor 2, cell 3, direction 4) |
| `juf` controlled character | 6: selected actor; spell fields reserved for the turn/action layer |
| `jxr` combat started | empty |
| `jvu` turn order | repeated field 1 → field 2 → actor 1, ordinary variant 3 |

Actor `lcs` uses identity 1, position 2 and details 3. Details `lcq` use look 2, exploration oneof 1 (`lbx`) and combat oneof 4 (`lco`). The separate legacy field 3 is also typed `lco`; it must not replace the combat oneof. Native getters `lcq.cqzq` and `lcq.cqzs` independently identify the exploration and combat variants. `lco` contains common information 7, status 8, player oneof 5 and monster oneof 6. Common information uses visibility 1 and repeated statistics 4. Status uses alive 1 and team 2. Monster identity is field 3 containing grade 1, level 2 and definition 3. Playable identity contains level 2, name 4 and character variant 1 (breed 2, gender 4, level 6).

The exploration group uses `lbx.5 -> lbs.6 -> lgk`: leader `kzi` at field 4, additional monsters at repeated field 3. Each `kzi` carries grade 2, look 3, level 4 and monster definition 5. These tags were read from the same-build type layouts independently of the encoder. A companion's concrete fight identity, grade, appearance and placement are checked separately from the exploration group preview. Synthetic tests also verify that killing the leader alone cannot close a multi-monster fight or award its XP/items.

The `ldg` map variant has integer fields 1, 2 and 3. The current projection retains values 1 in fields 1 and 3 from the earlier interoperability behavior; their gameplay meanings remain unknown. Likewise preparation field 2 is an observed compatibility flag, not an inferred capability. These literals are confined to the native adapter.

Independent native data checks use the monster bundle hash `02a44dcd50e87333adf5218c5545901c819e1de52798139f67d8d6599dd8db00` and the geometry hash in the surface contract. Monster 3270, grade 1, has level 1, life 15, AP 2, MP 3 and look bone 3253. The reference client's preview at `[0,-3]` matches installed arena `153883910` (subarea `450`), with 65 walkable cells and 10 blocked cells shown in the preview. The preview also shows eight placement cells per team. These cells are configured separately because the installed arena has no native placement flags. The default arena `153888768`, spawn cell 363, initial fighter positions and encounter membership are local rules.

TCP coverage checks signed negative identities, concrete participant variants, distinct exploration and arena identities, identical preview and preparation zones, context ordering, and restoration of the exploration location after forfeit.

## Neutral calendar presentation

The current `lov` message carries the calendar identity in field 3. The native monster tooltip reads `fka.bfaa()` through `fbm.bcki`; without the calendar event it throws before displaying the group details. The installed calendar bundle SHA-256 is `2791d7f01a597706eab8783c74b9d78970ea8d3e77be2a161877d238a5b48713`. An independent read found record 10 to be the lowest ID with no bonus entries. Choosing an empty bonus list is local policy; record identity is read from the installation. The calendar event precedes the map actors.

## Controlled team initialization

After participants, placement zones and the action bar, `jxh` supplies fight ID 1 (int32) and team 2 (`lln`). Team 6 is the controlled participant's team; leader 8 is its actor ID. Ordinary-team union 4 (`lkq`) contains member entries 2 (`ljc`), each with signed actor 3 and player union 5 (`llk`), name 2 and level 3. This profile has one controlled member; it does not synthesize other party members.

These tags were rechecked in build 3.7.14.18 metadata. Native `fxp.biag` at `0x181e22360` invokes `fxj.bhyi` at `0x181e18ec0`; the latter reads team/leader at `lln` offsets 0x24/0x30 and publishes `bpg`. `EndTurnWidgetUI.OnFightLeaderInfoChanged` at `0x182c61b40` refreshes the ready button. `lln.cueq` at `0x180c5fcc0` requires ordinary-team union 4, so an absent team body is not a valid substitute. Omitting this update left the first preparation button without text. TCP coverage parses the fight, leader, team and member independently; native acceptance verifies the label after a fresh client/runtime start.
