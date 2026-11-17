# Local native-client sessions

The runtime owns preparation jobs, native process handles, and the local network endpoints for each profile. The launcher uses `launch`, `launch-status`, and `cancel-launch` requests so that copying game files does not occupy a control-pipe request for minutes.

## Isolation and ownership

The selected installation is read-only. A complete copy is prepared under `Krosforge-data/clients/<profile-id>` using a temporary directory and an atomic rename. The copy's Unity company/product identity is changed, and only its `StreamingAssets/version` file is pointed at the local configuration endpoint. Every launch validates the executable, native assembly, metadata, and modified player settings before starting the owned executable.

Native instances use profile-specific ports allocated on IPv4 loopback. The original client, existing server processes, and external interfaces are not used. Closing or deleting an active profile is rejected; close its native window first. Updates and runtime shutdown are also rejected while preparation or native sessions are active. A canceled preparation can be retried.

The database and prepared copy are inside `Krosforge-data`. Unity can additionally create local cache/settings directories under the isolated company/product identity. These native caches are not included in launcher-folder backups. Profile slots isolate accounts within one launcher data directory; separate launcher installations with the same slots should not be used concurrently on the same Windows account.

## Connection contract

The local adapter provides:

- HTTP configuration and version discovery, pointing the client at loopback services.
- Strict bounded Thrift launcher calls, authenticated with a random per-session secret.
- Short-lived, single-use connection tickets bound to one profile host.
- Length-delimited protobuf negotiation and authentication replies carrying the profile identity and server list.

Wire codecs were adapted from the original Krosforge protocol implementation. Technical field numbers and server identifiers belong to this compatibility adapter; game binaries, names, artwork, or extracted content are not distributed. The installation hashes in `ClientCompatibility` restrict native execution to build `3.7.14.18`.

The `server-selection` launch status means that the authenticated server-list response was written to the native connection. Automated protocol tests establish this transport behavior; observing the native screen is a separate validation step. Server selection returns a protocol failure until a world session is available, rather than fabricating a world handoff.

## Diagnostics

Native logs are written under `Krosforge-data/logs/<profile-id>`. The native client may include launcher arguments and session secrets in these files. Do not attach raw logs to issues or commits. Loopback authentication tests use synthetic accounts and no client assets.
