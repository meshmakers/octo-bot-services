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
- `src/RepositoryUpdate` - repository fixup scripts.
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
- **A new tenant-wide data job gets `BotServiceConstants.TenantJobManagementPolicy` on every entry point and a test
  that names the entry point.** The role is read from the caller's own token; the tenant gate defaults
  (`Enforce`) are not overridden in `Program.cs`. Exception: the dump is a whole-tenant backup by design and bypasses
  row-level data permissions; its protection is the gate. `docs/authorization.md`; enforced by
  `TenantJobRoleWiringTests.cs:94` (an explicit list of entry points: add yours) and `:118` (gate defaults),
  `DumpWholeTenantByDesignTests.cs:21`.
- **A job that reads runtime data for a user runs as that user and fails closed.** It takes the subject from the
  command, refuses a request without one and opens its session through `ExportCallerContext.OpenSessionAsync`,
  never through the parameterless `GetSessionAsync()`; a service token is never the system. Exceptions: the dump
  (above) and the import session, which stays a system session. `docs/export-jobs.md`; enforced by
  `ExportCallerContextTests.cs:57` and `:84`, `ExportArtifactSubjectBindingTests.cs:18`.
- **Every repository session is disposed with `using var session`, and no `catch` aborts the transaction itself.**
  After a failed commit the abort throws and hides the cause. `docs/export-jobs.md`; enforced by
  `ExportSessionDisposalTests.cs:36` (needs Docker), `RepositoryFixupServiceSessionTests.cs:20`.
- **A new bus command consumer is registered shared through `BotCommandConsumers.SharedOptions`, with a comment why
  concurrent consumers are safe.** `MigratesLegacyCommand` stays `true` while senders use the classic address.
  `docs/command-consumers.md`; enforced by `BotCommandConsumerReplicaTests.cs:34` (needs Docker),
  `BotCommandConsumers.cs:30`.
- **No upload or job state lives on a pod's disk or in process memory.** The service runs with several replicas
  without affinity: tus state goes through `ISharedUploadStore`, jobs read it by tus file id, and a request never
  copies a whole upload. `docs/tus-uploads.md`; enforced by `SharedUploadReplicaTests.cs:25`.
- **The Hangfire job storage gets a `MongoUrl` and one `MongoClient`, never a connection string.** The string form of
  the driver's URL types is redacted. `docs/hangfire-mongo-connection.md`; enforced by
  `HangfireMongoConnectionTests.cs:27`, `HangfireMongoConnection.cs`.
- **A job whose signature is stored in Hangfire keeps that signature; add an overload instead of changing it.** Jobs
  enqueued before an update must still run (an old overload runs with the safe default). Never bypass the refusal of
  service-managed CK models in the import jobs. `docs/ck-import-refusal.md`, `docs/export-jobs.md`; enforced by
  `ImportModelJobRtTests.cs:73`, `ImportModelJobServiceManagedRefusalTests.cs:54`.
- **A new async CK method of this service reuses `JobOperationRelay`, `OperationJobGuard` and `BotJobMethodErrors`,
  validates before it enqueues, and runs a job at most once.** `docs/repository-methods.md`; enforced by
  `TenantRepositoryDumpHandlerTests.cs:26`,
  `DumpRepositoryJobForOperationTests.cs:82`.
- **A change of the `System.Bot` model is judged against the semver rows before you edit it.** A new type declares
  `derivable` on purpose; raising `minEngineVersion` or the dependency floor is a decision, not a side effect.
  `docs/system-bot-ck-model.md`; enforced by `SystemBotCkLanguage2Tests.cs:13` and the CK version validation of the
  pipeline.
- **The index upgrade decides by index keys, takes its global limit from its own lease, and logs its run events at the
  shipped level.** Never decide by index name, never use `DisableConcurrentExecution` for the limit, never change a log
  level to make an event visible. `docs/index-upgrade-rules.md`; enforced by `IndexUpgradeBuildingBlocksTests.cs:28`
  (shape by keys, name ignored at `:40`) and `IndexUpgradeRunEventsTests.cs:20`; the lease rule has no test.
- **A failed tenant setup is queued in the shared retry tracker, never dropped.** `docs/tenant-setup-retry.md`;
  enforced by `TenantSetupRetryWiringTests.cs:20`.
