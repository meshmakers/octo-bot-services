---
description: Export jobs run with the caller data permissions: fail-closed identity, artifact binding, hidden attributes, session disposal, import caller context.
applies_to: src/Jobs/Commands/**, src/Jobs/Jobs/*Model*Job.cs, src/Jobs/Jobs/RunFixupJob.cs, tests/Jobs.IntegrationTests/Export*.cs
---

# Export, import and fixup jobs: caller identity and sessions

## Export jobs run as the caller

`ExportRtByQuery` / `ExportRtByDeepGraph` (REST on the asset repository -> bus command -> `ModelCommandsConsumer` ->
Hangfire `IExportModelJob`) run with the data permissions of the user who requested them:

- The asset repository puts subject id (`sub` / NameIdentifier / `client_id`, the GraphQL mapping), role names and client id
  into the command (`InitiatedBySubjectId`, `InitiatedByRoleNames`, `InitiatedByClientId`; additive optional members in
  `octo-common-services`) and answers 401 without an identity. A client-credentials token without `sub` is evaluated as its
  client with the client's roles; **a service token is never the system**.
- Fail closed, in three places: the consumer refuses a request without subject (no job is enqueued); the job fails a request
  without subject with a clear message (an old job of an older asset repository, or a message enqueued before the update); the
  commands (`ExportRtModel*Command`) refuse the system context and open the session through
  `ISecureSessionFactory.GetSessionAsync(RtSecurityContext)` directly - the `GetSessionAsync(RtSecurityContext)` extension would
  fall back to a system session on a repository that cannot open one, so it is not used (`ExportCallerContext.OpenSessionAsync`).
  Never call the parameterless `GetSessionAsync()` in an export.
- The engine filters the rows (`GetRtEntitiesByTypeAsync`, `GetRtEntitiesByIdAsync`) and prunes the deep graph
  (`GetRtDeepGraphAsync`, engine-mongodb): no rows, no associations pointing to them, and model dependencies are derived
  only from the exported entities. A tenant without data policies exports exactly as before.
- Artifact binding: the result of an export belongs to the initiating subject. `JobTenantBinding.IsInitiatorBound` /
  `TryResolveInitiatingSubject` read it from the job's own arguments (the export request), `JobsControllerBase.AuthorizeJobAsync`
  checks it after the tenant binding for download, status and delete: another subject of the same tenant gets 403 and
  no data. Exempt as before: a user token of an ancestor tenant (parent-tenant administration) and an allow-listed cross-tenant
  service client (`IJobTenantAccessGuard.MayAccessInitiatorBoundJobAsync`). A service-token initiator is bound to its client id.
  Dump, restore, archive export and import jobs stay bound to the tenant only.
- Dump = whole-tenant backup **by design** (Gerald, 2026-10-16): a physical mongodump of the whole tenant database (plus archive
  data) with no repository session and no caller context, so it bypasses row-level data permissions. The protection is the gate:
  `TenantManagement` + tenant gate. Document it as "a dump contains all data of the tenant" to users. Pinned by
  `DumpWholeTenantByDesignTests`.
- **Stored queries on Hidden attributes are refused.** `ExportRtModelByQueryByQueryCommand` checks the stored query's
  `FieldFilter` and `Sorting` with `Commands/StoredQueryAccessGuard` before the query runs: a path that reaches a CK v2 `Hidden`
  attribute (e.g. `System.Identity/User.PasswordHash`) fails the job with `ATTRIBUTE_NOT_QUERYABLE` (query, use, path with selector
  values removed, hidden attribute; never a comparison value). The exported entities never carry Hidden values, but the set of
  exported rows would answer a guess of the value. Rule = asset repository / mesh adapter: query type and derived types via the
  engine's `ArchiveHiddenColumnGuard.FindHiddenAttribute`; navigation tails, entity-selector keys and unknown query types against
  every type of the tenant. The check runs even though the asset repository refuses to save such a query, because a query can
  arrive through `ImportRt` or a restore. Tests: `Jobs.Tests/Commands/StoredQueryAccessGuardTests` (unit) and
  `Jobs.IntegrationTests/StoredQueryHiddenAttributeExportTests` (real engine; `tests/ExportTestCkModel` is `ckLanguage: 2` with the
  Hidden attribute `Country.Secret`).
- **Credential guard regression tests**: `Jobs.IntegrationTests/IdentityCredentialGuardExportTests` runs both export commands
  against the **real** System.Identity model (compiler output `testData/ck-models/ck-system.identity-2.30.0.yaml`, embedded by
  `SystemIdentitySnapshotCkModel`, refresh in the README there) with a user whose `PasswordHash`, `SecurityStamp` and `UserTokens` hold
  fixed dummy values: export by query and by deep graph (from the user; from its role, whose graph does not reach the user today) never contain them, a stored filter (equals for
  the right and a wrong guess, like, regex, range; record path `UserTokens.Value`) or sort order on a credential is refused, and no log line
  (`ExportEngineFixture.Logs`, Debug incl. the MongoDB command log, kept only while a test sets `Enabled`) carries a dummy value. The class
  seeds once (re-seeds after another class's `ResetAsync`, `ResetGeneration`): a refused export leaves its session open and a reset
  then waits for MongoDB to expire the transaction.
- ImportRt: `InitiatedBySubjectId` is enough for the lock protection - it is a per-type policy opt-in
  (`ProtectBlueprintLocked`), not a per-role classification - so no roles are passed. Note: the import session stays the
  system session by design of the lock protection (write grants are not evaluated per caller there).
- ImportRt principal kind: the consumer enqueues `ImportRtAsync(..., initiatedBySubjectId, initiatedByPrincipalKind, ...)`
  with `ImportRtCommandRequest.InitiatedByPrincipalKind` (set by asset-repo from the token: Interactive / Client / Unknown); the job
  passes it to `RtImportCallerContext.UserInitiated(subject, kind)`, where CK types with a `writeAccess` block (engine) admit or
  refuse the import (message 144). The overloads without the kind stay for jobs enqueued before (Hangfire resolves the stored signature)
  and run with `Unknown`. Tests: `Jobs.Tests/Jobs/ImportModelJobRtTests`, `Jobs.Tests/Consumers/ModelCommandsConsumerImportCallerTests`.
- Export/import round trip of record arrays: `Jobs.IntegrationTests/DataPointRoundTripTests` (needs Docker) exports an entity
  with a seed-owned RecordArray of `DataPointSample` (the ownership layout of `System.Communication/DataPoint`: `Name` and the
  required `ExternalId` seed-owned, `CurrentValue` / `LastUpdate` runtime state; `tests/ExportTestCkModel`, `Control`) by deep graph
  and by query, checks that the file carries identity and no live values, imports it into an emptied tenant and reads the records
  back. Export evaluates record MEMBERS one by one, so a member that is runtime state is missing from every exported record - the
  bug behind `Loxone/Control.States` losing `ExternalId` (fixed in the System.Communication model, 4.11.0). The engine's import validation
  now covers required record members: a file without `ExternalId` is still imported by default but produces one audit event per
  entity (`<ckAttributeId> at States[n]`, recorded by `RecordingRtImportAuditTrail` in `ExportEngineFixture`), and is rejected
  before anything is written with `Import:StrictMandatoryValidation` (tests switch `RtImportOptions` on the fixture; `ResetAsync` restores it).
- **Every repository session is disposed.** Both export commands and `RepositoryFixupService` open their session with
  `using var session = ...`. Disposing an engine session aborts a transaction that was not committed (the abort error is logged,
  not thrown, so the original failure stays the job's error) and returns the server session. Do not add an explicit
  `AbortTransactionAsync` in a `catch`: after a failed commit it throws and hides the cause. Before the fix a refused or failed
  export left its transaction open until MongoDB expired it (`transactionLifetimeLimitSeconds`, default 60 s); a tenant reset,
  drop or index build on that database waited for it, and repeated failing jobs piled up open transactions. Tests:
  `Jobs.IntegrationTests/ExportSessionDisposalTests` (refused stored query twice, query and deep-graph export failing after their
  reads, successful export: no open transaction in `$currentOp`, and a tenant reset right after completes within 30 s; without the
  fix each reset waits about 90 s) and `Jobs.Tests/Services/RepositoryFixupServiceSessionTests` (failing and successful script run
  dispose their session). Integration test classes may call `ExportEngineFixture.ResetAsync()` after a failing export again; seeding
  once per class to avoid the wait is no longer needed.
- Tests: `tests/Jobs.IntegrationTests` (`ExportPermissionTests`, needs Docker): the two real export commands against the real engine and a
  Testcontainers MongoDB with a restricted policy table and the tiny `tests/ExportTestCkModel` - denied type, owned-only, allowed caller,
  unrestricted tenant (same bytes for every caller), system context refused, for the query and the deep-graph export. Unit tests:
  `ExportCallerContextTests` (session for the caller, system/empty/no-factory refused), `ExportModelJobCallerTests`,
  `ModelCommandsConsumerExportCallerTests`, `ExportArtifactSubjectBindingTests` (enqueue through the real consumer, then
  403/200 per subject, parent tenant, client id, status/delete, other job types unaffected).
