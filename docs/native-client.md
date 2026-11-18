# Local native-client sessions

The runtime owns preparation jobs, native process handles, shared loopback endpoints, and separate profile registrations. The launcher uses `launch`, `launch-status`, and `cancel-launch` requests so that copying game files does not occupy a control-pipe request for minutes.

## Isolation and ownership

The selected installation is read-only. A complete copy is prepared under `Krosforge-data/clients/shared` using a temporary directory and an atomic rename. The copy's Unity company/product identity is changed once for this installation. Its `StreamingAssets/version` file is pointed at the shared configuration endpoint once per runtime lifetime, before the first native process starts. It is not rewritten when additional profiles start. Every launch validates the executable, native assembly, metadata, and modified player settings before starting the owned executable.

Native instances share HTTP, Thrift, and connection ports allocated on IPv4 loopback. Each process receives its own slot and random secret. A Thrift connection binds to that registration, and the single-use authentication ticket selects the profile on the shared connection endpoint. Disposing one registration revokes its tickets and cancels its connections without stopping other profiles. The original client, existing server processes, and external interfaces are not used. Closing or deleting an active profile is rejected; close its native window first. Updates and runtime shutdown are also rejected while preparation or native sessions are active. A canceled preparation can be retried.

The database and prepared copy are inside `Krosforge-data`. Unity can additionally create local cache/settings directories under the isolated company/product identity. These native caches are not included in launcher-folder backups. Native graphics/window preferences and Unity caches are shared within one prepared installation. Account authentication, language arguments, and log files remain per session. This does not claim that all native preferences are isolated by profile. New prepared installations receive a random Unity identity; copying a prepared installation also copies that identity.

## Preparation and migration

A preparation semaphore serializes initial copy, validation, migration, and configuration. Waiting launches can be canceled independently. Once prepared, all profiles execute the same file. Profile deletion removes only profile records, logs, and any legacy profile directory; it never removes `clients/shared`.

Before the first native process starts, valid legacy GUID directories can be promoted to `shared` by an atomic directory rename. Their marker is updated atomically; an interrupted promotion can be retried. After the shared directory is validated, redundant validated copies receive a durable cleanup journal, are renamed to `.retired-<guid>`, and are removed. Retirement directories with matching journals are cleaned on retry; similarly named directories without journals are preserved. Unrecognized or invalid legacy directories remain untouched. The original installation and external native caches are not removed.

## Connection contract

The local adapter provides:

- HTTP configuration and version discovery, pointing the client at loopback services.
- Strict bounded Thrift launcher calls, authenticated with a random per-session secret.
- Short-lived, single-use connection tickets bound to one profile registration.
- Length-delimited protobuf negotiation and authentication replies carrying the profile identity and server list.

Wire codecs were adapted from the original Krosforge protocol implementation. Technical field numbers and server identifiers belong to this compatibility adapter; game binaries, names, artwork, or extracted content are not distributed. The installation hashes in `ClientCompatibility` restrict native execution to build `3.7.14.18`.

The `server-selection` launch status means that the authenticated server-list response was written to the native connection. Automated protocol tests establish this transport behavior; observing the native screen is a separate validation step. Selecting the local realm transfers the authenticated identity to the game endpoint.

Unauthenticated launcher and connection requests have a 30-second timeout. Once authenticated, connections remain open while the user waits at server selection; closing the profile revokes them. Cancellation and the transition to starting a native process are serialized: an accepted preparation cancellation prevents that process from starting.

## Diagnostics

Native logs are written under `Krosforge-data/logs/<profile-id>`. The native client may include launcher arguments and session secrets in these files. Do not attach raw logs to issues or commits. Loopback authentication tests use synthetic accounts and no client assets.

## Local server presentation

The runtime advertises one classic, multiple-account realm. Its technical identifier remains stable when the player renames it through **Configure server**. The name is stored in SQLite (`settings`, key `server.name`), defaults to Krosforge, and is returned in the runtime snapshot. Configuration changes are rejected while any profile is preparing or running.

Before a native session starts, the runtime overrides the realm name's integer localization entry in all five language files under the shared copy. Original language files are pinned by SHA-256 for build `3.7.14.18` and remain read-only. Each output preserves the original bytes except for that entry's offset and an appended UTF-8 string. Renames regenerate from the original, so repeated changes do not accumulate data. No bundle rewriting or external patching executable is needed.

Each changed file is promoted from a temporary file. The completion record is informational: retries validate all language outputs, repair partial application, and start no native process until every language succeeds. Additional profiles reuse the presentation already applied in that runtime. Updating an existing shared copy does not copy the whole client again.

Synthetic tests cover persistence, localized dialogs, protocol identity and partial-application recovery. Native rendering and simultaneous Windows sessions require separate observation; automated binary lookup tests alone do not establish visual correctness.

The connection game-type discriminator is `1` for the classic multiple-account category, including its character quota. This differs from the static catalog’s regular game-type ID `0`. The pinned client’s mono-account getter classifies connection values `0` and `2` as single-account; reusing catalog IDs on the connection wire mislabels the server.

## Game connection and return

Selecting the local realm hands off to a shared IPv4 loopback game endpoint. The runtime writes the endpoint and a single-use credential before closing the connection socket. The game socket authenticates that credential, acknowledges readiness, serves the authenticated profile’s persisted character roster and answers keepalive messages. Character creation and confirmed deletion commit to SQLite before their successful responses are sent.

A profile registration belongs to the native process; a network lease belongs to one connection generation. Returning to server selection issues a new generation and a purpose-specific ticket. Cleanup checks both the generation and the expected connection phase. A late transfer-write failure cannot release a socket that has already consumed the ticket, even when it retains the same generation. Transfer and return tickets expire after 30 seconds and are invalidated when the profile closes. An unexpected socket loss releases its lease; a fresh login can retry without affecting other registrations. A runtime restart still requires reopening native windows.

The additional launch stages are `connecting-server`, `character-selection`, `returning-server`, and `disconnected`. `character-selection` records delivery of the roster, not visual confirmation of a screen. The native client may route an empty roster directly to character creation. Process activity remains independent of these network stages: updates, profile deletion and server changes remain blocked until native windows close.

The game adapter bounds frames to 64 KiB and protobuf objects to 128 fields. Unauthenticated game and connection sockets have an absolute 30-second authentication deadline. Authenticated sockets have no fixed age limit. Unknown auxiliary requests produce no synthetic success and have a budget of 128 requests per game connection.


## Character persistence

Characters belong to a profile and realm. Each profile has five slots. Names use NFC normalization, 3–20 characters, letters and at most one internal hyphen; their invariant-uppercase keys are unique within the realm. Accents remain significant. Deletion requires preparation and confirmation for the same owned character on the same game connection. Returning, reconnecting, listing, creating or attempting selection clears the pending confirmation. Successful deletion permanently releases the name and slot.

Each storage operation owns its SQLite connection. Immediate write transactions serialize the slot check and insert, with a five-second busy timeout. Socket failure does not undo a committed operation; reconnecting reads the database again. Storage errors close the affected connection rather than fabricating an empty roster. The existing schema is retained; appearance JSON carries its own format and client-build versions.

Creation reads the hash-pinned breed, head and body bundles locally from the shared client copy. The bounded managed reader supports the reference UnityFS index, LZ4/LZMA blocks, serialized v22 metadata and managed-reference registry v2. Nothing is extracted to disk or distributed with the application. Normal creation options must match the chosen breed/gender, be available at creation and not be marked payable. Omitted options use the first available catalog order/ID; absent colors use that breed/gender’s defaults. Character presentation starts at level one. Authentication includes each owned character’s name, connection-specific class ordinal, gender, level and an epoch last-access marker. The realm status remains online regardless of character count; the native server card derives its count from these summaries. World-entry requests remain rejected.

Automated catalog verification can be enabled with `KROSFORGE_TEST_CLIENT` pointing to a separate reference installation. That test is skipped in CI, which does not receive game assets. Automated TCP and SQLite tests use synthetic appearance data. Native screen acceptance, including all five languages and the behavior after creation, must be recorded separately in the pull request.

## Character navigation compatibility

The creation response places the newly committed character first because the native creation consumer selects the first roster entry. Ordinary list requests keep their stable order. Creation success and world selection remain distinct operations; refusing selection never rolls back character creation.

In reference build `3.7.14.18`, the rejection handler clears the cached roster, the back action omits it from the display event, and automatic-selection paths interfere with returning to the list. The managed copy receives four bounded compatibility changes before the first native process starts:

| Consumer | Native RVA / file offset | Change |
| --- | --- | --- |
| Selection rejection tail | `0x184534b` / `25442635` | Retain the failure notification and cached roster; clear only the pending selected ID. |
| Character-list display | `0x1f04ffd` / `32519677` | Always display the requested roster instead of automatically selecting its first entry. |
| Roster after server handoff | `0x183ff93` / `25421203` | Skip the remembered-character candidate; show the roster for manual selection. The separate newly-created-character selection path is retained. |
| Back from creation | `0x176f7b0`, `0x176f80b` / `24567216`, `24567307` | Preserve the cached roster in a saved register and pass it to the display event instead of null, allowing the first-ever list screen to initialize. |

These locations were derived from the pinned native consumers and exercised in an isolated Portuguese client. They are compatibility instructions, not game content. Server handoff always opens the roster for manual selection. The first change preserves the original failure dialog; no world-entry success is fabricated. After dismissing a refusal during creation, the back arrow returns to the roster in the same window.

`CharacterNavigation` accepts only the complete original assembly hash or the complete expected patched hash (`e97e15af3ac1b45df787870d364b35deef206965839a5a52f5ab01a57ece79e4`). It checks each original instruction range and the resulting digest, writes a separate temporary file, flushes it, and atomically promotes it. Cancellation or promotion failure leaves the previous assembly intact. A terminated process may leave an unreferenced temporary file; it is never treated as a valid prepared assembly. A retry derives the change from the unchanged original. Unknown or partially modified assemblies fail validation. Additional profiles reuse the verified shared assembly.

Only the managed copy changes. No native binaries or extracted assets are shipped. Existing prepared copies are updated without copying the whole installation again. Older launchers do not recognize the patched digest; to use an older launcher, close all native windows and prepare a fresh managed copy while preserving the database. Native acceptance and Windows locked-file recovery remain separate from the synthetic Linux tests.
