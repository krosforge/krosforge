# Local combat encounters

The runtime reads monster appearance, grade, level and basic attributes from the player's hash-pinned installation. `Native/combat-encounters.json` supplies encounter identities, source map, spawn, approach distance and initial placement choices. These are explicit local encounter rules, not recovered official spawn scripts. No extracted models, names, dialogue, monster records or client binaries are distributed.

The runtime also selects the lowest installed calendar record with an empty bonus list. It initializes the native monster tooltip with a neutral local calendar; it does not reproduce the official seasonal schedule.

Combat terrain comes from the installed map geometry. The low nibble of `linkedZone` identifies the combat zone; exploration continues to use the high nibble. Native red and blue cell flags define the two placement sets. Initial positions must belong to their respective teams and cannot overlap. The catalog rejects unknown creature grades, unsupported appearance formats and invalid placement instead of silently substituting content.

## Encounter and preparation lifecycle

An authenticated selected character can attack a visible group on its current map. If necessary, it approaches along a validated exploration path. Admission waits for the movement and any carried partial-segment time to finish. A new movement, cancellation, map change or disconnect cancels the pending attack. An attack requested during another walk resumes its approach after that walk ends.

The same coordinator that owns exploration claims the group, removes the exploring actor from map presence and creates an isolated fight context. The stored exploration position remains the return location. Other connections cannot claim the same group while it is reserved. This ownership rule is independent of native acceptance with multiple accounts, which is deferred.

Loading the admitted combat map publishes fight information, participants and placement zones. Placement changes require the selected player's identity, a free cell in that player's zone and an open preparation phase. An invalid placement receives the authoritative positions. Readiness seals preparation and announces combat start once; duplicate requests cannot start it again. Readiness also builds the turn order from initiative, with a stable local tie break that favors controlled actors.

Forfeit destroys the fight context and restores exploration at the previously confirmed location. Disconnect also releases the group. No combat-placement cell is written as the exploration location, and an old connection's cleanup cannot release a replacement's encounter. After a runtime restart the character returns to its durable exploration position.

## Validation

Domain tests cover placement eligibility, readiness, closure and approach connectivity. Runtime tests cover approach completion, remaining travel time, cancellation, forfeit and connection replacement. TCP tests exercise the native context sequence, signed group/fighter identities, placement zones, concrete monster identity, readiness and return to exploration. Optional installation tests check the selected monster and placement zones against independently read native data.

See the [combat preparation wire contract](../tests/Krosforge.Tests/Fixtures/combat-preparation-contract.md). Native visual validation is recorded in the PR and does not follow from passing protocol tests. The initial acceptance scope is one account, Portuguese interface, Windows, client build `3.7.14.18`.

## Turn execution

The coordinator owns initiative, turn serials, action/movement points and the animation sequence. Combat movement follows cardinal neighbors in the native isometric grid, respects terrain connectivity and occupied cells, and spends one MP per cell. Exploration locations remain unchanged throughout combat.

The configured spell books admit the shared unarmed action and two level-one ranged class actions. The monster book is read from the installed creature record. Spell identities, grades, costs, ranges, damage dice, critical chance and cast limits come from hash-pinned installed catalogs. Unsupported effects, conditions, areas and cooldowns fail catalog loading; they are not silently discarded. Other class books currently expose the shared action only. Equipment, buffs, summons, tackle and erosion are outside this ruleset.

Casts check the active actor, AP, range, line of sight and per-turn/per-target limits before consuming resources. Admitted masks exclude the caster and may include allies. An empty or ineligible target cell still consumes AP, without fabricated damage. Damage applies the admitted elemental attributes, resistance and critical dice, capped at the target's remaining life. The automatic opponent approaches a living enemy, uses an affordable supported spell and ends its turn when it cannot act.

A movement or cast opens a native sequence addressed to its actor and closes it to the observing player. Only the matching acknowledgement unlocks the next action; sequence IDs do not reset between fights in the same coordinator. Ending a turn during animation waits for that acknowledgement. Turn deadlines are processed before incoming actions, even if the clock continuation has not run. Missing animation acknowledgements close that connection after the configured deadline rather than guessing the client's rendered position. Turn readiness also has a bounded fallback. Timing units are explicit in `combat-spells.json`.

A defeated side seals further actions. Durable outcomes, rewards and automatic return to exploration are owned by the completion layer; forfeit remains available during this stage.
