# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

Job service of the Octo Mesh platform: Hangfire jobs for repository dump, restore, export, import, archive data
export and import, the secret sweep and the paging index upgrade, plus the CK methods that start them. User-facing
behaviour is documented in `README.md`.

### Solution layout

- `src/BotServices` - ASP.NET Core host: `Program.cs`, REST controllers, bus command consumers, the CK method host.
- `src/Jobs` - Hangfire jobs, commands, secret sweep and the index upgrade (`src/Jobs/IndexUpgrade`).
- `src/RepositoryUpdate` - repository fixup scripts. `src/BotServices.Resources` - localized resource texts.
- `src/SystemBotCkModel` - the embedded `System.Bot` CK model.
- `tests/Jobs.Tests` - unit tests. `tests/Jobs.IntegrationTests` - integration tests against real MongoDB and
  RabbitMQ containers (Docker). `tests/ExportTestCkModel` - the small CK model the export tests use.

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

Locally only `-c DebugL` builds: version 999.0.0, packages from `../nuget`. A command without a configuration stops
with `OCTO0001`. On a fresh workspace build the dependency chain first (`Invoke-BuildAll -configuration DebugL`,
octo-tools). Tests are TUnit on Microsoft.Testing.Platform.

```bash
dotnet build Octo.Bots.sln -c DebugL
dotnet test --project tests/Jobs.Tests/Jobs.Tests.csproj -c DebugL                    # unit tests
dotnet test --project tests/Jobs.IntegrationTests/Jobs.IntegrationTests.csproj -c DebugL  # needs Docker
```

## Before you commit

1. Build, then run both test projects above: all green. Without Docker say so and ask the developer; do not skip the
   integration tests silently.
2. Update the doc in `docs/` that covers the code you changed, in the same commit.
3. Touched `AGENTS.md` or a file in `docs/`: `Test-OctoAgentDocs -Fix` (octo-tools) reports 0 errors. It rewrites the
   generated routing table, so run it before you stage.

## Rules

- **This repository is public.** No work item ids, internal hostnames, environment, customer or tenant names, or
  secrets in docs and in new or changed code comments and test names; remove an existing one when you touch its line.
- **Every entry point of a tenant-wide data job is gated by the tenant role `TenantManagement`, read from the caller's
  own token.** An HTTP route carries `BotServiceConstants.TenantJobManagementPolicy` (`TenantApi`
  `JobsController.cs:117`, `:139`, `:169`, `:192`, `:217`, `:311`; `SystemApi` `JobsController.cs:133`), a CK method
  declares the role for the method host (`BotMethodHost.cs:36`), a tus route goes through
  `TusUploadAuthorization.cs:29`; bus consumers have no gate of their own. Do not override the tenant gate defaults
  (`Enforce`) in `Program.cs`. Exception: the dump is a whole-tenant backup by design and bypasses row-level data
  permissions; its protection is the gate. `docs/authorization.md`; enforced by `TenantJobRoleWiringTests.cs:94` (an
  explicit list of HTTP entry points: add yours), `:108` and `:118`, `DumpWholeTenantByDesignTests.cs:21`.
- **A job that reads runtime data for a user runs as that user and fails closed.** It takes the subject from the
  command, refuses a request without one and opens its session through `ExportCallerContext.OpenSessionAsync`, never
  through the parameterless `GetSessionAsync()`; a service token is never the system. Exceptions: the dump (a
  whole-tenant backup by design), the import session, which stays a system session, and jobs without a requesting
  user (aggregation, fixup: `AttributeValueAggregatorJob.cs:56`, `RepositoryFixupService.cs:28`, `:65`).
  `docs/export-jobs.md`; enforced by `ExportCallerContextTests.cs:57` and `:84`,
  `ExportArtifactSubjectBindingTests.cs:18`.
- **No `catch` aborts a repository transaction itself.** After a failed commit the abort throws and hides the cause;
  disposing the session aborts an uncommitted transaction and keeps the original error. `docs/export-jobs.md`; enforced
  by `ExportSessionDisposalTests.cs:36` (needs Docker).
- **The service runs with several replicas and no session affinity.** No upload state, and no state that one request or
  job run hands to another, lives on a pod's disk or in process memory (scratch files inside one job run are fine);
  tus state goes through `ISharedUploadStore`. Bus command consumers are registered shared through
  `BotCommandConsumers.SharedOptions` (`BotCommandConsumers.cs:30`), with a comment why concurrent consumers are safe.
  `docs/tus-uploads.md`, `docs/command-consumers.md`; enforced by `SharedUploadReplicaTests.cs:25`,
  `BotCommandConsumerReplicaTests.cs:34` (needs Docker).
- **The Hangfire job storage gets a `MongoUrl` and one `MongoClient`, never a connection string.** The string form of
  the driver's URL types is redacted, so a client built from it logs in as a wrong user.
  `docs/hangfire-mongo-connection.md`; enforced by `HangfireMongoConnectionTests.cs:27`.
- **A job whose signature is stored in Hangfire keeps that signature; add an overload instead of changing it.** Jobs
  enqueued before an update must still run (an old overload runs with the safe default). `docs/export-jobs.md`;
  enforced by `ImportModelJobRtTests.cs:73`.
