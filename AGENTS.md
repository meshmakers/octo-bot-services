# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

Job service of the Octo Mesh platform (Hangfire: dump, restore, export, import, secret sweep). Build and test with the
root `CLAUDE.md` rules (`dotnet build Octo.Bots.sln -c DebugL`, tests are TUnit: run the test executable of
`tests/Jobs.Tests` or `dotnet test`). Detailed behaviour is documented in `README.md`.

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `src/BotServices/Configuration/BotAuthorizationPolicies.cs`, `src/BotServices/*Authorization*.cs`, `src/BotServices/HangfireDashboard*.cs`, `src/BotServices/*Api/**` | `docs/authorization.md` |
| `src/Jobs/Commands/ImportCkModelCommand.cs`, `src/Jobs/Jobs/ImportModelJob.cs` | `docs/ck-import-refusal.md` |
| `src/BotServices/Consumers/**` | `docs/command-consumers.md` |
| `src/Jobs/Commands/**`, `src/Jobs/Jobs/*Model*Job.cs`, `src/Jobs/Jobs/RunFixupJob.cs`, `tests/Jobs.IntegrationTests/Export*.cs` | `docs/export-jobs.md` |
| `src/BotServices/HangfireSetup/**` | `docs/hangfire-mongo-connection.md` |
| `src/Jobs/IndexUpgrade/**`, `tests/Jobs.Tests/IndexUpgrade/**` | `docs/index-upgrade-rules.md` |
| `src/BotServices/Methods/**`, `src/Jobs/Jobs/Operations/**`, `src/Jobs/Jobs/*Repository*.cs`, `src/Jobs/Jobs/ArchiveData/**` | `docs/repository-methods.md` |
| `src/SystemBotCkModel/**`, `tests/Jobs.Tests/CkModels/**` | `docs/system-bot-ck-model.md` |
| `src/BotServices/Services/DefaultConfigurationCreatorService.cs` | `docs/tenant-setup-retry.md` |
| `src/BotServices/Tus*.cs`, `src/BotServices/Services/ArtifactTusStore.cs`, `src/Jobs/Services/*SharedUploadStore.cs` | `docs/tus-uploads.md` |
| `src/BotServices/Configuration/*Cors*.cs`, `src/BotServices/Configuration/Configure*Options.cs` | `docs/web-host-security.md` |
<!-- <<< end generated: routing -->

Background, not path-triggered: `docs/index-upgrade.md` (operator view of the paging index upgrade, linked from
`docs/index-upgrade-rules.md`).

## Build & test

Filled in a following commit.

## Before you commit

Filled in a following commit.

## Rules

Filled in a following commit.
