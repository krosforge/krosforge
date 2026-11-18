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

## Route revision regression

An observed native sequence on surface map `153880324` requested turning cells `287,288,274,277,333,391`, then `287,288,274,277,333,377` 1,024 ms later without an intervening `jpj`. The previous runtime rejected this second request and corrected to cell 305, followed by repeated corrections as the client's subsequent paths used different origins. A regression replays the request timings with the embedded production policy. No raw session log or account data is committed.

The synthetic TCP fixture separately sends `0a0200031064` (0 → 3 on map 100), then `0a0200021064` after 1,500 ms with 1,000 ms segments. Both peers must receive `01 02` under field 1 of `jnz`, without `joz` or a replay from 0. The revised route retains the original deadline at 2,000 ms. A map change at that deadline completes it even without a timer callback, and reconnection restores the committed destination. These assertions establish runtime and wire behavior; they do not establish how the native renderer interpolates a revised route.

## Running profile selection

Timing is checked through the native selection path, not only a speed factory. In build 3.7.14.18, `fce.bcqf` (RVA `0x1a6fd80`) selects the animation profile through `xs.rkb` (RVA `0x25ff450`). Its unmounted running branch selects mode 5. `xs.rjy` registers mode 5 with `xz.rlg` speeds (factory RVA `0x2605e10`):

| Direction on screen | IEEE-754 inverse milliseconds | Segment duration |
| --- | --- | --- |
| Diagonal | `0x3bf0f0f1` | 136 ms |
| Horizontal | `0x3ba0a0a1` | 204 ms |
| Vertical | `0x3c088889` | 120 ms |

Mode 2 uses the distinct `xz.rle` profile (170/255/150 ms); using it for these runs accumulates an incorrect delay. The native regression uses the **embedded production policy** and an observed cancellation at cell 500 after 3,239 ms on the path `87 → 159 → 271 → 416 → 500`, followed by `500 → 416 → 315 → 287`, with a second account present and moving. The former policy rejects cell 500 and corrects to 416. A second regression replays the recorded rapid short continuations against the same embedded policy.
