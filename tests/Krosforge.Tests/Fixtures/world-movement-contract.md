# Movement and presence wire contract — 3.7.14.18

Field constants and types were read independently from the locally installed metadata, using the same source hashes recorded in [world-entry-contract.md](world-entry-contract.md). Previous server code was a discovery aid; current field definitions and native acceptance are separate checks. No binary or extracted map resource is included here.

| Type | Direction | Fields used |
| --- | --- | --- |
| `jok` | request | 1: repeated int32 compressed keys; 2: int64 map; 3: bool mode, supported false |
| `jnz` | event | 1: repeated int32 expanded cells; 4: int64 actor |
| `jny` | request | 1: enum; 2: int32; supported default values only, no movement ID |
| `jpj` | request | 1: int32 cancellation cell |
| `jpk` | event | empty payload, movement released |
| `joc` | request | 1: bool mode, supported false; 2: int32 destination map |
| `joz` | event | 1: int32 cell; 2: int64 actor; optional enum omitted |
| `jns` | event | 1: repeated `lcs` actors; field 2 exists but is not used |
| `kjz` | event | 1: int64 departing actor |

Actor structure and map snapshots use the entry fixture. Compressed keys use a 12-bit cell and three direction bits; the runtime expands and validates geometry rather than trusting the requested final facing. The direction hint must remain within the supported key range.

Independent request bytes used in TCP tests:

- `0a0200021064`: turning cells 0 → 2, map 100.
- `080008021064`: equivalent unpacked repeated keys.
- `10c801`: destination map 200.
- `1064`: destination map 100.

For the synthetic straight path, the response must contain expanded bytes `00 01 02` under field 1 and the moving actor under field 4. TCP assertions also verify snapshot identities, departure/arrival ordering, absence of events from another map, and restoration after a socket reset during a committed transition.
