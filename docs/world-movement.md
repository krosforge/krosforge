# Local movement and map presence

The runtime validates every cell between requested turning points against geometry read from the reference installation. The source must match the confirmed location; the path must remain within the configured length, walkable cells and the same altitude/zone region. Repeated cells, malformed segments, unsupported modes and unknown links are rejected. Facing is derived from the last traversed segment. These are explicit local exploration rules, not recovered official server scripts.

`Native/world-entry.json` declares the grid, path budget, step duration and two directed transitions. Each transition is checked against the installed map's neighbour identity, boundary column and exit flags. Arrival cells are validated against the destination geometry. The arrival choices and movement timing are reconstructed local policy; the neighbour, cell and exit data come from the hash-pinned installation. Other maps and transitions remain unsupported.

## Confirmation and persistence

A movement has a distinct runtime identity and a monotonic start time. An animation acknowledgement carries no movement identifier in this client protocol, so it cannot advance time or save a location. The runtime commits the endpoint after the admitted duration. Before validating another movement, cancellation or map transition, the coordinator commits any elapsed movement under the same lock, even if its timer continuation has not run yet. Cancellation can commit only a cell on the elapsed prefix. A disconnect before completion discards the pending path and restores the last confirmed location on reentry.

SQLite updates compare the expected character owner and complete previous location/revision in a short transaction. A duplicate or stale operation cannot overwrite a newer revision. The in-memory confirmed location changes only after commit. Persistence failure ends the affected session; an old timer or cleanup cannot modify its replacement. No frame-by-frame database writes are needed.

A map transition requires a confirmed exit cell and no pending movement. The destination is committed before the old map receives the departure and the client receives the map load. It becomes visible in the destination only when that map is requested. Failure before commit retains the source; loss of connection after commit restores the destination.

## Ordering and isolation

One local coordinator serializes presence and location changes for the supported maps. It enqueues output without waiting for sockets. Each connection has one bounded writer, preserving frame boundaries and event order; an overflowing or stalled connection closes independently.

A map snapshot and its subsequent presence events are ordered under the same coordinator. Accounts see arrivals, movement and departures only in their current map. An account joining during a movement receives the actor's current visual cell and the remaining path; this does not commit an intermediate location. Repeated map requests do not duplicate arrival announcements.

Automated coverage includes path rejection, monotonic completion, cancellation, stale timers, concurrent SQLite writes, failed transitions, slow output queues and two-account TCP transitions/reconnection. Native acceptance is recorded separately in the PR. See the [independent protocol fixture](../tests/Krosforge.Tests/Fixtures/world-movement-contract.md).
