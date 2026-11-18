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

The `server-selection` launch status means that the authenticated server-list response was written to the native connection. Automated protocol tests establish this transport behavior; observing the native screen is a separate validation step. Server selection returns a protocol failure until a world session is available, rather than fabricating a world handoff.

Unauthenticated launcher and connection requests have a 30-second timeout. Once authenticated, connections remain open while the user waits at server selection; closing the profile revokes them. Cancellation and the transition to starting a native process are serialized: an accepted preparation cancellation prevents that process from starting.

## Diagnostics

Native logs are written under `Krosforge-data/logs/<profile-id>`. The native client may include launcher arguments and session secrets in these files. Do not attach raw logs to issues or commits. Loopback authentication tests use synthetic accounts and no client assets.

## Local server presentation

The runtime advertises one classic, multiple-account realm. Its technical identifier remains stable when the player renames it through **Configure server**. The name is stored in SQLite (`settings`, key `server.name`), defaults to Krosforge, and is returned in the runtime snapshot. Configuration changes are rejected while any profile is preparing or running.

Before a native session starts, the runtime overrides the realm name's integer localization entry in all five language files under the shared copy. Original language files are pinned by SHA-256 for build `3.7.14.18` and remain read-only. Each output preserves the original bytes except for that entry's offset and an appended UTF-8 string. Renames regenerate from the original, so repeated changes do not accumulate data. No bundle rewriting or external patching executable is needed.

Each changed file is promoted from a temporary file. The completion record is informational: retries validate all language outputs, repair partial application, and start no native process until every language succeeds. Additional profiles reuse the presentation already applied in that runtime. Updating an existing shared copy does not copy the whole client again.

Synthetic tests cover persistence, localized dialogs, protocol identity and partial-application recovery. Native rendering and simultaneous Windows sessions require separate observation; automated binary lookup tests alone do not establish visual correctness.
