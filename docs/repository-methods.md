---
description: Dump, restore and archive export and import as asynchronous CK methods: host, handlers, shared job pieces, at-most-once, operation records, restore warnings.
applies_to: src/BotServices/Methods/**, src/Jobs/Jobs/Operations/**, src/Jobs/Jobs/*Repository*.cs, src/Jobs/Jobs/ArchiveData/**
---

# Repository dump, restore and archive data as async CK methods

## Async repository dump as a CK method (AB#8445; System.Bot 3.7.0)

`System.Bot/TenantRepository.Dump-1` (static, `execution.mode: async`, `handler: service`, roles `TenantManagement`) is the first CK
method this service hosts. User documentation: README "Repository dump as an asynchronous CK method"; method runtime:
`octo-common-services/docs/method-handler-sdk.md` section 11.

- **Host**: `src/BotServices/Methods/BotMethodHost.cs` (`AddBotCkMethodHost`, called from `Program.cs`; `AddCkMethodHandlerDiscovery` on the
  event hub). It registers the handler, the caller token validation (authority = `Bot:AuthorityUrl`), the MongoDB operation store
  (system database, `MethodOperations`), the operation runtime (`AddCkMethodOperations`: runner, heartbeat, sweep) and
  `IDumpOperationGate`. A new bot method = one handler class + one `Handle<>` line there.
- **Handler** `TenantRepositoryDumpHandler`: `HandleAsync` only accepts (no side effect); the work enqueues
  `IDumpRepositoryJob.RunForOperation` (same job type as REST, `AutomaticRetry(Attempts = 0)`, `OnAttemptsExceeded = Delete`),
  records the starter (`JobStarterRecorder.Record(…, subject, clientId, …)` from the validated `CallerPrincipal`, never from
  `CkMethodRequest.Caller`) and the job parameter `OctoMethodOperationId`, then polls the job (`IJobStorageAccessor`) and relays
  `OctoDumpProgress` into the operation. Result `RtRepositoryDumpResultRecord { JobId, DownloadPath }`, errors `DUMP_FAILED` /
  `DUMP_INTERRUPTED` (both with `jobId`). A `Deleted` job is a failed one (the job deletes itself after a failure).
- **At most once (OD-5)**: Hangfire requeues a job whose server stopped (graceful: the shutdown token cancels it and it is requeued;
  killed: after the invisibility timeout). `AutomaticRetry` does not cover that, so `RunForOperation` sets `OctoDumpStarted` at the first
  start and fails a second start with `OctoDumpOutcome = interrupted`; it also refuses when the operation is no longer `Accepted` /
  `Running` (`operation-closed`). The operation of a stopped host ends `METHOD_OUTCOME_UNKNOWN` / `handler-lost` (runtime of AB#8443).
- **Replicas**: the work and the job may run on different replicas (shared Hangfire storage, job parameters as the channel). If the
  replica of the work dies, the operation ends `handler-lost` while the job may still finish; the dump is then reachable through
  `system/v1/jobs` only (outcome unknown by design).
- **`DisableConcurrentExecution(600)`** locks per job method: a REST dump and a method dump may run in parallel, and method dumps wait
  for each other **at most 10 minutes** (same timeout as the REST `Run`). A method dump that waited longer fails before it starts
  (`DistributedLockTimeoutException`, job deleted, nothing dumped); the handler answers `DUMP_FAILED` with `reason`
  `another-dump-running`, and the caller tries again later. Raising the timeout would keep a Hangfire worker blocked for as long.
- **Result JSON**: the generated `RtRecord` would serialize its attribute dictionary; `src/SystemBotCkModel/RtRepositoryDumpResultRecord.Json.cs`
  adds a System.Text.Json converter so the wire form is the bare `{"jobId","downloadPath"}` object (method wire contract, AB#8079).
- **CK model**: 3.7.0 adds `TenantRepository` (`derivable: Model`), the record `RepositoryDumpResult` and two attributes - a Minor.
  `minEngineVersion` rises from 3.5.1 to **3.5.67** (`execution.mode`, `ck-engine-version.md` of the engine): publish only where every
  service runs that engine.
- Tests: `Jobs.Tests/Methods/TenantRepositoryDumpHandlerTests` (accept without side effect, one enqueue with operation id and starter,
  progress relay, result, declared errors, cancellation, wire form of the result), `Jobs.Tests/Jobs/Operations/DumpRepositoryJobForOperationTests`
  (Attempts = 0, classic `Run` signature unchanged, progress, second start refused, closed operation refused),
  `Jobs.Tests/CkModels/SystemBotCkLanguage2Tests` and `Jobs.IntegrationTests/SystemBotCkLanguage2PersistenceTests` (method survives the
  round trip).

## Restore and archive data as async CK methods (AB#8447; System.Bot 3.8.0)

`TenantRepository.Restore-1`, `ExportArchiveData-1`, `ImportArchiveData-1` (static, async, `TenantManagement`). User docs: README
"Restore and archive data export/import as asynchronous CK methods".

- **Shared pieces** (use them for the next job method): `Methods/JobOperationRelay` (records starter + operation id, relays
  progress, reports the end: Succeeded / Failed / Interrupted / LockTimeout / Missing), `Methods/BotJobMethodErrors` (`UPLOAD_NOT_FOUND`
  reasons, `jobId` / `reason` parameters, `BotJobMethodOptions` = `Methods:BotJobs`), `Services/StagedUploadCheck` (same upload rule
  as the REST routes), `Jobs/Operations/OperationJobGuard` (at most once; generic parameter names `OctoOperation*`; the dump keeps its
  `OctoDump*` names through `OperationJobParameterNames.Dump`), `Jobs/Operations/OperationSummaries` (stage codes, count summary
  written as `OctoOperationResult`). The dump handler now uses the relay too.
- Handlers validate synchronously (upload, ids, window) and enqueue only inside the work. Results are bare camel-case records
  (`SystemBotCkModel/RtJobResultRecords.Json.cs`, generic `RtRecordWireJsonConverter<T>`; the dump keeps its own converter).
- Row-pump progress has **no percentage** (`DumpProgress.Percent = -1` → stored without percent).
- Restore: the operation record is in the system database, never touched by the restore. Deleted tenant: `TenantManagementConsumer`
  purges via `ICkMethodOperationTenantPurge` (common-services AB#8447) on **`PosDeleteTenant`** (after the drop, sent from a `finally` also when the drop fails; `PreDeleteTenant`
  only removes the recurring jobs), failures only logged. **The delete events are also sent by a restore (`TenantBackupService`
  drops and re-attaches the tenant) and a detach** — purge only when `ITenantLifecycleStore` shows `Deleting` (the delete endpoint
  marks it before it starts); without that gate a method restore deleted its own record (local E2E 2026-11-11).
- **Recreated tenant id (AB#8881, security)**: the purge can fail and a call admitted before the delete can write its record after the
  purge. `AddCkMethodMongoOperationStore()` wraps the store in `CkMethodTenantGenerationOperationStore` (common-services), so every read
  (`DumpOperationGate`, the gateway) ignores records accepted before the current tenant's `TenantLifecycleRecord.CreatedUtc`.
  `DefaultConfigurationCreatorService.StartTenantAsync` runs `Methods/OperationRecordGenerationCleanup` (removes those records via
  `DeleteTenantAcceptedBeforeAsync`; nothing while the generation is unknown; never throws). A restore keeps `CreatedUtc`, so its own record
  stays. Tests `tests/Jobs.Tests/Methods/OperationRecordGenerationCleanupTests.cs`.
- Upload ids: `GetTusUploadFilePath` refuses anything but a single file name (review of PR #59, path traversal).
- `system/v1/jobs` (System API `JobsController`) carries `[OctoRestDeprecated(Wave = BotRestDeprecation.JobsWave1)]`; dates only from
  configuration. Keep the sunset unset until every job type has a handle.
- CK model: 3.8.0 = additions only (Minor), `minEngineVersion` stays 3.5.67; enum `ArchiveImportMode` keys = REST enum keys (test).
- Tests: see README.

## Restore jobs report failed tenant notifications (System.Bot 3.9.0)

A restore whose tenant-created or tenant-deleted notification failed after the engine's commit succeeds, but the services may
not know the restored tenant; the recovery is a ClearCache of the tenant. `TenantNotificationWarnings.From(CommandResult, tenantId)`
reads the warnings (output lines naming ClearCache, plus a fallback for `TenantCreatedNotificationFailed` without a line);
`RestoreRepositoryJob` and `RestorePreSweepDumpJob` put them into `NotificationWarnings` of their result and log them;
`Controllers/RestoreOutcome` turns them into `Outcome = SucceededWithWarnings` and `JobDto.Reason`; `RestoreOperationSummary.ClearCacheRequired`
becomes `RepositoryRestoreResult.ClearCacheRequired` (optional record member, so 3.9.0 is a Minor). A failed restore already
carries the line in the job error (`JobFailedException.CommandExecutionFailed` embeds `CommandResult.ToString()`); a job that
throws after a successful engine restore with warnings rethrows `RestoreRepositoryJob.NotificationWarningsFailure` (message plus
warnings; cancellation is rethrown unchanged). The dump job never detaches, so it has no such warning. CLI and Studio display of
the text are separate changes; until then octo-cli `RestoreTenant` exits non-zero on such a restore and the Studio shows a plain
success. Details: README
"Restore: services that were not told about the tenant".
