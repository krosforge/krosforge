# Agent instructions

## Commits, pull requests, and release notes

- Write commit messages, pull request titles and descriptions, and release notes in English.
- Use Conventional Commits, for example `fix: restore the battle map preview`.
- Every pull request must create or update `release-notes/<version>.md` for its target release. Add the relevant changes and retain existing entries for that version.
- Link the release notes and record how you verified the change in the [pull request template](.github/pull_request_template.md).
- Include `Co-Authored-By: Codex <noreply@openai.com>` in commits authored by Codex.

## Development

- Use the .NET SDK version in `global.json`. NuGet versions are managed in `Directory.Packages.props`.
- Follow the testing, packaging, and game installation guidance in the [contributing guide](CONTRIBUTING.md).
