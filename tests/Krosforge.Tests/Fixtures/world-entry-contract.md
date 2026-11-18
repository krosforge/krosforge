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
| `krg` | `kzv` snapshot at 1. Repeated `lae` stats at 1, next-level XP at 2, level-floor XP at 8, alignment at 10, current XP at 11. |
| `lae` | Stat identity at 2; usable variant `ldw` at 1; simple variant `lgt` at 3. |
| `ldw`, `lgt` | Usable base at 4 and used at 7; simple value at 2. |

The current `Core.DataCenter.Metadata.Stat.StatId` enum has **int16** constants: HitPoints=0, ActionPoints=1, StatsPoints=3, Level=5, Vitality=11, MovementPoints=23, MaxLifePoints=95, Shield=96, HitPointLoss=97, CurPermanentDamage=102. These were read from the current enum default-value records, rather than assuming that historical semantic labels remained correct.

For six RGB inputs 1–6, the expected indexed color stream is `818080088280801083808018848080208580802886808030`. This fixture is independent of the encoder's field lookup and packing code.

The initial map and its cells are read from the separately installed, hash-pinned bundles. The test fixture contains no map geometry or native assets. Initial life/AP/MP are an explicit local configuration; XP thresholds come from the local catalog. None of these assertions alone establishes visual acceptance in the native client.
