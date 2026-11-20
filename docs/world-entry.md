# Local world entry

Selecting an owned character acquires an exclusive runtime selection until its game connection returns or closes. A selection has a distinct connection identity; releasing an old selection cannot remove its replacement. A different profile, deleted character, or concurrent selection cannot acquire the character. Socket failure after a committed location does not erase it.

The runtime sends selection acceptance, a roleplay context, the map load, an explicitly empty inventory and initial attributes. The client then requests information for that exact map. The response contains the visible characters' persisted appearances, account identities, cells and directions. A different map identifier is rejected. [Movement and map presence](world-movement.md) extend this entry contract; combat and inventory gameplay are separate features.

`Native/world-entry.json` is the versioned local configuration. Its initial location and life/AP/MP policy are reconstructed local choices, not a claim of official server behavior. Surface map geometry, world coordinates, subarea and experience thresholds are read from the hash-pinned reference installation. The managed reader uses the embedded schemas and retains only the requested map geometry; assets are neither extracted to disk nor distributed. Saved locations outside the supported map or walkable cells cause an explicit failure instead of a silent reset.

## Database migration and recovery

Schema 2 adds `character_locations` with a cascading character foreign key, map, cell, direction and revision. Existing profiles, characters, appearances and settings remain unchanged. A character without a location acquires its configured initial position on first valid entry, once. An existing location is validated and restored.

Before migrating an older supported schema, an immediate SQLite transaction excludes competing writers. A separate read connection backs up the last committed database, including WAL contents, into `Krosforge-data/backups/schema-<old>-before-<new>-<id>.db`. The backup is checked for integrity and schema version, flushed and promoted before schema changes commit. Backup failure aborts migration. An interrupted transaction rolls back and can be retried. Schema 3 adds combat receipts and per-character currency. Schema 4 adds experience, item stacks and combat awards. Schema 5 adds per-character point distributions. Fresh databases start at schema 5. Future schema versions are rejected before journal settings are changed.

## Character progression

Each level gained grants five characteristic points and five maximum life. The runtime derives the total budget from durable XP and stores the costs assigned to the six attributes. An ordinary `krc` request replaces the entire distribution, so retrying it yields the same state. The runtime verifies the selected character and current level in the write transaction and sends the authoritative statistics to the client. Omitted costs free those points for reuse.

The `hkh` base book contains available class and common spells at their installed grades. Combat loads its own executable spell set with `juf`. After a victory raises the level, the runtime returns the character to the exploration scene, then sends `kqp`, the updated statistics and the refreshed spell book. A reconnect restores the level and points from the committed award.

An older executable cannot open schema 2. To restore a pre-migration installation, close every native window and the runtime, preserve the entire current data directory, and restore the selected schema-1 backup into a separate data directory as `krosforge.db`. Do not combine the restored database with WAL/SHM files from the newer database. That restored copy represents the backup time and omits later changes. Downgrading also requires a client copy compatible with the older executable; see [native-client compatibility](native-client.md#character-navigation-compatibility). This is an explicit recovery procedure, not automatic schema downgrade.

Automated checks cover migration, backup failure, rollback, concurrent initial locations, ownership, deletion, stale selection cleanup and TCP disconnection/reentry. [The wire fixture](../tests/Krosforge.Tests/Fixtures/world-entry-contract.md) records independently recovered field tags. Native acceptance and its remaining limitations belong in the PR and release notes.
