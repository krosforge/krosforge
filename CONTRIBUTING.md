# Contributing to Krosforge

## Development setup

Install the .NET 10 SDK specified in [global.json](global.json), then clone the repository:

```sh
git clone https://github.com/krosforge/krosforge.git
cd krosforge
dotnet build Krosforge.slnx
dotnet test Krosforge.slnx
```

Run the launcher from source:

```sh
dotnet run --project src/Krosforge.Launcher
```

The portable data directory is created beside the built application. Do not commit it.

## Project structure

| Project | Responsibility |
| --- | --- |
| `Krosforge.Core` | Shared domain rules, including profile names. |
| `Krosforge.Contracts` | Local runtime messages, framing, and application paths. |
| `Krosforge.Runtime` | SQLite persistence, client discovery, and management of application-owned copies. |
| `Krosforge.Launcher` | Avalonia interface and runtime lifecycle. |

Package versions are centralized in `Directory.Packages.props`. Shared build settings are in `Directory.Build.props`.

## Tests

Run the solution tests before submitting a code change:

```sh
dotnet test Krosforge.slnx -c Release
```

On Windows, verify the packaged executable and portable data persistence:

```powershell
./packaging/publish.ps1 -Version 0.0.1
./tests/PowerShell/PortablePackage.Tests.ps1 -PackageRoot .artifacts/portable -WithWindow
```

The Windows smoke test uses a temporary copy of the package. It checks profile persistence after moving the folder and can open and close the application window. This verifies the launcher, not game integration.

The Windows updater has a separate integration test using two published test packages:

```powershell
./tests/PowerShell/UpdateInstaller.Tests.ps1 -CurrentPackage .artifacts/update-current -CandidatePackage .artifacts/update-candidate
```

Both folders must contain a self-contained `Krosforge.exe` built with updater support. The test copies them into temporary directories and checks successful replacement, startup acknowledgement, preserved data, and rollback when a candidate cannot start. It does not publish a release or replace your working installation.

## Translations

Launcher text lives in `Strings.resx` (English) and the `fr`, `de`, `es`, and `pt` resource files. Keep the same keys and format arguments in every language; the localization tests verify both. Use `Text.Get` when creating dialogs and the `LocalizedAction` / `LocalizedLabel` helpers for persistent controls that must update when the user changes languages.

Expected runtime and domain failures use stable resource identifiers, which the launcher translates at the UI boundary. Unknown system errors are presented with a localized fallback rather than showing raw messages or untranslated identifiers. `Text.Culture` contains the selected launcher language; it does not change the process-wide culture.

## Packaging and releases

`packaging/publish.ps1` builds a self-contained Windows x64 executable and creates `.artifacts/Krosforge-<version>-win-x64-portable.zip`. The ZIP must contain only `Krosforge.exe`. Licenses and dependencies are embedded in the executable.

To publish a release, maintainers run the **Release** workflow with a version and provide `release-notes/<version>.md` in the selected commit. The workflow tests, builds, and publishes the portable ZIP, checksums, and a source archive. It does not publish a standalone launcher executable.

The updater reads the latest stable release from the public GitHub API. A compatible release must include the exact `Krosforge-<version>-win-x64-portable.zip` asset and `SHA256SUMS`. Downloads are limited to GitHub HTTPS hosts, verified against the checksum, and restricted to a ZIP containing only `Krosforge.exe`. Checksums provide download integrity; they are not a code-signing identity.

Updates are opt-in. A helper copied from the running launcher waits for that exact process to exit, verifies both binaries again, and replaces only the launcher executable. It retains the old executable until the new launcher acknowledges that its window opened. Failed restarts restore the backup. Native game processes and user data are not update targets.

Keep user-facing instructions in the README and version-specific capabilities and limitations in the release notes.

## Pull requests

- Describe the problem, the resulting behavior, and how you validated the change.
- Add focused tests when changing profile persistence, process handling, or other behavior that needs automated verification.
- Preserve the user's original client installation. Preparation and cleanup must remain limited to application-owned copies.
- Do not commit game clients, extracted assets, credentials, personal data, logs, or build artifacts.
- Update related documentation when changing user flows or packaging.

For issues, include the launcher version, client build, reproduction steps, and relevant error messages. Remove personal information and secrets before attaching any diagnostics.
