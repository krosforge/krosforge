# World-entry contract, reference build 3.7.14.18

The TCP assertions use literal field tags and expected color bytes, independently of `WorldEntryWire`. Synthetic map/account/character values are test inputs, not extracted content.

Static sources: original `GameAssembly.dll` SHA-256 `af866f65f0d08eebe9008ffda54462e2814d994c81059fffd766f0d685503ab3`; metadata SHA-256 `781890cf890114c5231b5bf04595acbceaf8317f7998fd8053c017de1ae23206`. Metadata version 39, assembly `Ankama.Dofus.Protocol.Game.dll`. Constant tags, native field types and method return types were read without executing the assemblies. Semantic interpretation was compared with the earlier local implementation; native observation is separate evidence in the PR.

| Message/type | Contract used |
| --- | --- |
| `ksb` | Success at 1, containing the character at 1; failure at 2. Character fields use the existing appearance fixture. |
| `kkc`, `kjp` | Context enum at 1, roleplay 0. |
| `jow`, `kjt` | Positive int64 map identity at 1. |
| `jnj` | Repeated `lcs` actors at 3, int64 map at 10, int32 subarea at 12. |
| `lcs` | Int64 actor at 1, `kzo` position at 2, details at 3. |
| `kzo` | Int32 cell at 3, direction enum at 4. |
| Actor details | Roleplay variant at 1, `kzu` appearance at 2. |
| Roleplay variant | Humanoid at 3. Humanoid name at 5, player variant at 1. |
| Player | Int64 account at 2, alignment presence at 6, gender enum at 7. |
| `kzu` | Bone 1, packed skins 3, packed indexed colors 5, packed scales 6. |
| `jnv` | Int64 map at 3, int32 subarea at 5. |
| `irl` | Empty auxiliary message at 3; int64 currency at 4; no item entries. |
| `ith` | Current int64 currency balance at 2. |
| `hme` | XP percentage 3, drop percentage 4, sharing percentage 5. |
| `krg` | `kzv` snapshot at 1. Repeated `lae` stats at 1, next-level XP at 2, currency at 6, level-floor XP at 8, alignment at 10, current XP at 11. |
| `lae` | Stat identity at 2; usable variant `ldw` at 1; simple variant `lgt` at 3. |
| `ldw`, `lgt` | Usable base at 4 and used at 7; simple value at 2. |

The current `Core.DataCenter.Metadata.Stat.StatId` enum has **int16** constants: HitPoints=0, ActionPoints=1, StatsPoints=3, Level=5, Vitality=11, MovementPoints=23, MaxLifePoints=95, Shield=96, HitPointLoss=97, CurPermanentDamage=102. These were read from the current enum default-value records, rather than assuming that historical semantic labels remained correct.

The native character sheet constructs an attribute graph from Strength=10, Vitality=11, Wisdom=12, Chance=13, Agility=14 and Intelligence=15. `UICharacterSheet..ctor` passes the graph's attributes to `EntityStatsGraph`, whose `CreateIcons` dereferences the resolved stat records. The `krg` snapshot includes a usable record for each of the six attributes even when its base value is zero.

Native `esf.zar` at `0x1818a3ce0` reads `hme` runtime offsets 0x24/0x28/0x2c. XP/drop values are narrowed to 16 bits and converted from total percentages to server-bonus percentages by subtracting 100. Server configuration therefore accepts exact hundredths up to 100× and publishes percentage integers up to 10000; zero means a minus-100-percent bonus. Sharing stays at 100. The event follows each exploration map/reference snapshot, so recreating the roleplay context or reconnecting does not restore stale rates. TCP tests verify 250/200/100 for a 2.5× XP, 2× drop world before combat, after victory and after a reset socket. Ordinary bestiary XP and base/prospecting labels still read catalog/prospecting values; publishing this message alone does not establish that those labels display custom server multipliers.

For six RGB inputs 1–6, the expected indexed color stream is `818080088280801083808018848080208580802886808030`. This fixture is independent of the encoder's field lookup and packing code.

The initial map and its cells are read from the separately installed, hash-pinned bundles. The test fixture contains no map geometry or native assets. Initial life/AP/MP are an explicit local configuration; XP thresholds come from the local catalog. None of these assertions alone establishes visual acceptance in the native client.

Inventory and statistics must publish the same cumulative wallet. Native `esg.zht` (`0x1818b22b0`) reads `kzv` runtime offset `0x40` (field 6) and updates the inventory balance. Omitting it resets the client's balance to zero after `irl`/`ith`, including during a combat turn. `fef.bdmz` (`0x181b24a00`) consumes `ith` field 2 and releases inventory hooks. Tests assert the currency in both messages after a reward and after reconnect.

## Bestiary attribute dependencies

In the same pinned build, `BestiaryTabUI.UpdateAggroTexture` at VA `0x1815605b0` requests StatId 29 (`EnergyPoints`) before binding aggression indicators. `BestiaryTabUI.GetFinalDropCoeff` at VA `0x181555a40` requests StatId 48 (`MagicFind`) and subtracts the base 100 before evaluating the prospecting bonus. Both accesses dereference the returned entry; omitting the entries interrupts row binding. The roleplay snapshot therefore supplies energy and prospecting from the configured base attributes. Monster names, grades, families, statistics and drop definitions continue to come from the separately installed catalogs. TCP assertions verify these two numeric entries; native visual acceptance is a separate check.
