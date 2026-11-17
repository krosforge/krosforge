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

## Packaging and releases

`packaging/publish.ps1` builds a self-contained Windows x64 executable and creates `.artifacts/Krosforge-<version>-win-x64-portable.zip`. The ZIP must contain only `Krosforge.exe`. Licenses and dependencies are embedded in the executable.

To publish a release, maintainers run the **Release** workflow with a version and provide `release-notes/<version>.md` in the selected commit. The workflow tests, builds, and publishes the portable ZIP, checksums, and a source archive. It does not publish a standalone launcher executable.

Keep user-facing instructions in the README and version-specific capabilities and limitations in the release notes.

## Pull requests

- Describe the problem, the resulting behavior, and how you validated the change.
- Add focused tests when changing profile persistence, process handling, or other behavior that needs automated verification.
- Preserve the user's original client installation. Preparation and cleanup must remain limited to application-owned copies.
- Do not commit game clients, extracted assets, credentials, personal data, logs, or build artifacts.
- Update related documentation when changing user flows or packaging.

For issues, include the launcher version, client build, reproduction steps, and relevant error messages. Remove personal information and secrets before attaching any diagnostics.
