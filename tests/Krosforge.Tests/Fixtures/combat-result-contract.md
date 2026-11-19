# Combat result contract — 3.7.14.18

This contract uses the current binary and metadata hashes documented in the preparation contract. The old server supplied discovery names only; field numbers, unions and the native consumer were inspected again against build 3.7.14.18.

| Type | Current fields |
| --- | --- |
| `jwp` result | repeated team 2 (`ljn`), repeated fighter 4 (`lis`), duration in milliseconds 8 |
| `ljn` team | name 1 (`lfs`, UTF-8 value 1), outcome 2 (`kyk`) |
| `lis` fighter result | outcome 1, identity 2 (`liq`), prize 3 (`lia`) |
| `liq` identity | alive 2, signed actor 5, player union 3 (`lim`) |
| `lim` player | level 2; native entity information supplies breed and appearance |
| `lia` prize | repeated groups 1, currency 2 (int64) |
| `irl` inventory | empty ordinary inventory 3, current currency balance 4 |
| `ith` balance | current currency balance 2; releases inventory hooks |
| `krg` statistics | snapshot 1 (`kzv`), including cumulative currency 6 |
| `jwh` statistics | participant map 1, identified player summary 2 (`jum`) |
| `jum` summary | shield 1, healing done 2, healing received 3, damage dealt 4, damage taken 5, blocked damage 6, enemies defeated 7 (int32) |
| participant map entry | player actor ID 1, details 2 (`jud`) |
| `jud` details | dealt 1, healing received 2, shield applied 3, MP 5, shield received 6, taken 7, identity 8, AP 9, defeats 11, healing done 12 |
| `jvd` identity | actor ID 1, player union 5, team enum 7 |

The result outcome enum uses victory 2 and defeat 0. Monsters retain negative signed actor IDs. A player prize contains committed currency and item deltas; inventory contains cumulative balances and stacks. The player identity also carries the committed experience state and the experience delta.

Native `fzi.bipi` at `0x181e71960` reads `lis` identity and prize, maps the outcome enum and dispatches player identity union 3 to `fzi.bipe`. `fzk.bipo` at `0x181e73730` reads `lia` currency from runtime offset `0x20` and places it into the result-prize model `fzg` at offset `0x20`. These runtime offsets corroborate the corresponding current metadata field constants; they are not protocol tags.

Tests parse result envelopes independently by these field numbers and check the negative monster identity, player outcome, prize delta and wallet balance. TCP tests cover a complete result followed by return to exploration, duplicate final acknowledgements and a reset socket after the terminal animation but before acknowledgement. Database tests cover concurrent duplicate payouts, rollback before commit, conflicting retries, ownership, deletion and recovery of interrupted fights. Passing these tests does not establish native visual acceptance; that evidence belongs in the PR.

Current `dt.hvw` at `0x1814c9c70` maps `jum` offsets 0x24/0x28/0x30 to the damage-dealt, damage-taken and enemy-count model fields. `FightResultCharacterTab.UpdateFightStatistics` at `0x182c6bb00` reads those fields into the corresponding named UI bindings. The statistics follow `jwp`. Effective life loss is capped to remaining life and hostile kills exclude allies. Healing counts restored life, including both entries for a self-heal. An accepted cast charges AP even when it hits an empty cell. Movement charges MP for traversed cells. Rejected actions and acknowledgements leave the recorded totals unchanged.

The detailed tab resolves the player's portrait through its result identity. All nine category messages are present for the playable actor. The categories use int32 totals and fixed32 IEEE 754 averages. Damage dealt carries direct damage at field 2, total at 10, per AP at 1 and per turn at 6. Damage taken carries total at 6 and per turn at 7. AP and MP use spent per turn at field 2. Hostile defeats use ordinary count at 2 and total at 3. Healing done uses total at 3, per turn at 2 and per AP at 4. Healing received uses total at 4 and per turn at 3. Averages divide by the confirmed AP spent or turns started, with zero for an empty denominator. Shield categories have no activity with the current combat rules. TCP tests check the identity, all category messages and fixed32 averages.

## Experience and inventory rewards

The player result `lim` contains repeated `lik` at field 1, with the XP union `lih` at field 1. In `lih`, fields 3, 4, 6 and 8 carry current XP, level floor, next-level floor and combat delta. Fields 5 and 9 enable the XP and delta presentation. Native `fzi.bipe` (`0x181e71040`) maps the corresponding `lih` runtime offsets 0x20, 0x28, 0x38 and 0x48 into result-model offsets 0x48, 0x50, 0x58 and 0x60. `FightResultCharacterTab.DisplayCharacterXp` (`0x182c68af0`) uses model offsets 0x50/0x58 as the gauge limits, 0x48 as its value and 0x60 for the gain label. Tests independently decode those tags and check the XP again after a TCP reset before final acknowledgement.

Result item groups use `lia.1 -> lie.3 -> lib` with quantity at 1 and template at 3. Templates are combined for the prize display; persistent inventory stacks retain distinct identities and effects. `irl.2` contains inventory entries (`lmy`), whose common item is at 1 and position is at 4. The common item (`lef`) carries quantity 3, template 4, UID 5 and repeated effects 7. Effect action is `lkp.10`, with integer value at 5 or dice at 9 (minimum 1, maximum 2, constant 4). The current `CharacterInventoryPositionEnum` has an Int16 literal 63 for an unequipped item. Native catalogs and the metadata layouts were inspected separately from the encoder. These protocol checks remain distinct from native visual acceptance.
