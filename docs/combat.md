# Local combat encounters

The runtime reads monster appearance, grade, level and basic attributes from the player's hash-pinned installation. `Native/combat-encounters.json` supplies encounter identities, source map, spawn, approach distance and initial placement choices. These are explicit local encounter rules, not recovered official spawn scripts. No extracted models, names, dialogue, monster records or client binaries are distributed.

The runtime also selects the lowest installed calendar record with an empty bonus list. It initializes the native monster tooltip with a neutral local calendar; it does not reproduce the official seasonal schedule.

Combat terrain comes from the installed map geometry. The low nibble of `linkedZone` identifies the combat zone; exploration continues to use the high nibble. Native red and blue cell flags define the two placement sets. Initial positions must belong to their respective teams and cannot overlap. The catalog rejects unknown creature grades, unsupported appearance formats and invalid placement instead of silently substituting content.

## Encounter and preparation lifecycle

An authenticated selected character can attack a visible group on its current map. If necessary, it approaches along a validated exploration path. Admission waits for the movement and any carried partial-segment time to finish. A new movement, cancellation, map change or disconnect cancels the pending attack. An attack requested during another walk resumes its approach after that walk ends.

The same coordinator that owns exploration claims the group, removes the exploring actor from map presence and creates an isolated fight context. The stored exploration position remains the return location. Other connections cannot claim the same group while it is reserved. This ownership rule is independent of native acceptance with multiple accounts, which is deferred.

Loading the admitted combat map publishes fight information, participants, placement zones and the controlled team with its leader. The team update initializes the native ready button on the first encounter. Placement changes require the selected player's identity, a free cell in that player's zone and an open preparation phase. An invalid placement receives the authoritative positions. Readiness seals preparation and announces combat start once; duplicate requests cannot start it again. This layer establishes preparation and context ownership; turn execution and durable results are separate layers.

Forfeit destroys the fight context and restores exploration at the previously confirmed location. Disconnect also releases the group. No combat-placement cell is written as the exploration location, and an old connection's cleanup cannot release a replacement's encounter. After a runtime restart the character returns to its durable exploration position.

## Validation

Domain tests cover placement eligibility, readiness, closure and approach connectivity. Runtime tests cover approach completion, remaining travel time, cancellation, forfeit and connection replacement. TCP tests exercise the native context sequence, signed group/fighter identities, placement zones, concrete monster identity, readiness and return to exploration. Optional installation tests check the selected monster and placement zones against independently read native data.

See the [combat preparation wire contract](../tests/Krosforge.Tests/Fixtures/combat-preparation-contract.md). Native visual validation is recorded in the PR and does not follow from passing protocol tests. The initial acceptance scope is one account, Portuguese interface, Windows, client build `3.7.14.18`.
