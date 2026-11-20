# Combat turn contract — 3.7.14.18

Native metadata/binary hashes and retained static evidence are the same as the preparation contract. The older server is only a discovery aid; current field constants and native variant getters were checked independently.

| Type | Current fields |
| --- | --- |
| `jsr` cast request | 1 spell identity, 2 destination cell |
| `jst` animation acknowledgement | optional 1 actor, 2 positive sequence, 3 completed boolean |
| `jqh` sequence open | 1 sequence type (ordinary 0), 2 actor |
| `jsy` sequence close | 2 sequence ID, 3 type (0), 4 observing player |
| `jnz` movement | packed cells 1, signed actor 4 (negative for monsters) |
| `jwo` end request | empty ordinary mode |
| `jug` turn ending / `jte` turn confirmed | empty ordinary mode |
| `jtd` readiness | 1 boolean; optional field 2 must remain zero |
| `jvi` round | 1 round, 2 order index |
| `jvj` turn | 2 remaining time in 100 ms units, 3 actor, 6 order index |
| `jsq` action | 22 action ID, 36 source; union 2 cast, 3 death, 13 points, 37 life loss, 39 life gain |

The `jsq.cgxn` native getter tests union discriminator 2 and returns `jsq.jsp.jrw`. Cast payload: common 1, ordinary spell identity 2 (level ID 1, definition 2), cell 5, target 6, critical enum 8 (success 1). Point payload `jrn`: signed delta 2 and target 3. Life loss `jsd`: permanent damage 1, wire element 4, target 5, amount 6. Death `jrz`: target 2. Cast action is 300, AP use 102, MP use 129, death 141. Wire damage/element pairs are neutral 100/0, earth 97/1, fire 99/2, water 96/3 and air 98/4. Catalog effect IDs use a separate mapping and must not be reused as wire action IDs.

`juf` contains shortcuts in repeated field 2 (`ldk`, slot 3, spell union 2 containing definition 1), spells in field 5 (`hkf`, ordinary category 1=0, definition 2, grade 4), and controlled actor 6.

Life gain uses `jse`, target 2 and amount 4, with action 108. In the current binary, `jsq.chab` at `0x180a45f40` checks union discriminator 39; current metadata gives the corresponding `jse` fields. The data-center `ActionId` enum has unsigned 16-bit constants, including `FightLifePointsWinPercent=1109`; catalog effect IDs remain separate from native wire action normalization. Area casts project one cast/AP event followed by all resolved life-loss/gain and death events, then one sequence closure. Codec tests assert multiple targets and the literal gain union/tag values independently.

Additional current `SpellLevelData` getters identify cast-in-line bit 0 (`0x1807b5a20`), cast-in-diagonal bit 1 (`0x1807bf9f0`), free-cell bit 3 (`0x1807bfa40`) and occupied-cell bit 4 (`0x1807bfa60`). `SpellZoneShapeLineBehavior.upo` at `0x1826a6da0` returns maximum minus minimum plus one, corroborating inclusion of both radius endpoints. Point/circle/line shapes and decrease operands are read from the pinned catalogs. Optional installation tests inspect a self-centered circle, a linear area, a percentage heal, a cardinal spell and their cooldowns. This does not replace native visual acceptance of those encounters.

Current `StatId` has int16 underlying constants, independently read from metadata defaults: strength 10, chance 13, agility 14, intelligence 15, initiative 44; earth/fire/water/air/neutral percent resistance 33/34/35/36/37. Shield is 96 and current permanent damage is 102. These differ from labels in some older server mappings. Usable statistics have base 4, context adjustment 6 and used 7; life loss is statistic 97 and is negative. Local neutral multipliers are 100 and erosion is explicitly zero.

Current `SpellLevelData` getters were checked in native code: LOS at `0x1807bfa00` tests bit 2, boostable range at `0x1807bfab0` tests bit 6, and animation at `0x1807bfa80` tests bit 9. Catalog hashes are pinned in `combat-spells.json`. Independent scalar reads establish spell 13338/level 41718 costs 2 AP, ranges 0–8, uses water dice 7–9 (critical 10–12), has 5% critical chance and limits 6 per turn/3 per target. Spell 13331/41697 costs 3 AP, ranges 1–4 without LOS, uses fire dice 11–13 (critical 14–16), has 10% critical chance and limits 3/2. Unarmed 0/10461 costs 3 AP at range 1 with neutral dice 6–7 and 5% critical chance (9–10). Monster 5866/25445 costs 2 AP at range 1 with air dice 6–10 and no critical.

TCP tests cover controlled-book publication, movement cost, cast/damage envelopes, increasing acknowledgements, turn handoff, monster movement with a negative actor followed by an acknowledged attack, automatic damage and forfeit without overwriting exploration position. Runtime tests cover stale acknowledgements across fights, expired turns before timer execution, and disconnect on missing animation completion. Native animation and HUD acceptance remain separate PR evidence.
