# CLAUDE.md - octo-bot-services

Job service of the Octo Mesh platform (Hangfire: dump, restore, export, import, secret sweep). Build and test with the
root `CLAUDE.md` rules (`dotnet build Octo.Bots.sln -c DebugL`, tests are TUnit: run the test executable of
`tests/Jobs.Tests` or `dotnet test`). Detailed behaviour is documented in `README.md`.

## Authorization (AB#4497)

| Surface | Required |
| --- | --- |
| Dump, restore, archive export / import, fixup script run, tus upload, artifact download | scope `octo_api` (full access) + tenant role `TenantManagement` (`BotServiceConstants.TenantJobManagementPolicy`; tus: `TusUploadAuthorization`) |
| Secret sweep trigger / delete dump | scope + `SecretManagement`; sweep report / runs: read scope + `AdminPanelManagement` |
| Job status / delete (System API) | scope + job tenant binding (`JobTenantAccessGuard`) |
| Hangfire dashboard `/ui/jobs` | scope + caller of the **system tenant** (`tenant_id` claim) **and** tenant role `TenantManagement` (`HangfireDashboardAccessMiddleware` + `HangfireDashboardAuthorizationFilter`; others get 403) |

Rules:
- The role is checked on the caller's own token. A parent-tenant user (`[AllowParentTenantAdministration]`) needs
  `TenantManagement` in the parent tenant, not in the target tenant.
- The dump is a whole-tenant backup and bypasses row-level data permissions by design (see "Export jobs run as the
  caller" below); the runtime-data exports are the opposite.
- Role policies probe both claim spellings (`role` and `ClaimTypes.Role`) via `BotAuthorizationPolicies.HasTenantRole`.
- Tenant gate defaults (`UserTokenEnforcement` / `ServiceTokenEnforcement` = Enforce) must not be overridden in
  `Program.cs`; `TenantJobRoleWiringTests` pins this.
- New tenant-wide data job = `TenantJobManagementPolicy` + a negative test per entry point.

## CORS and cookies (AB#7270)

- CORS = the shared known-origins policy of `AddOctoServiceInfrastructure()` (`app.UseCors()` without a policy): allowed
  origins are the registered client origins of the addressed tenant (system routes and unknown tenants: system tenant),
  never `*`, never credentials. `BotCorsConfiguration` only adds the tus response headers (`Upload-Offset`,
  `Upload-Length`, `Tus-Resumable`, `Location`) and a 1 h preflight cache via `OctoCorsOptions`. A browser app that calls
  bots must be a registered client origin. `CorsAndCookieWiringTests` pins this; the policy itself is tested in
  octo-common-services (`KnownOriginsCorsPolicyTests`).
- Session cookie of the OIDC login (`Octo-BotServices`): HttpOnly, Secure, `SameSite=Lax`. No cross-site flow needs
  `None`: the identity callback is same-site and the SPAs use bearer headers.
- `OctoIdentityAccessToken` (Studio opens `/ui/jobs?jwt_token=…`): set by `UseOctoCookieBasedAuthentication(
  BotServiceConstants.HangfireDashboardBranchPath)` with HttpOnly, Secure, `SameSite=Strict`, `Path=/ui`; outside `/ui`
  neither the query token nor the cookie becomes an Authorization header. After the update an old cookie with
  `Path=/` stays in the browser until it expires; it is ignored outside `/ui`.

## Export jobs run as the caller (AB#6734)

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
  (`GetRtDeepGraphAsync`, engine-mongodb AB#6734): no rows, no associations pointing to them, and model dependencies are derived
  only from the exported entities. A tenant without data policies exports exactly as before.
- Artifact binding: the result of an export belongs to the initiating subject. `JobTenantBinding.IsInitiatorBound` /
  `TryResolveInitiatingSubject` read it from the job's own arguments (the export request), `JobsControllerBase.AuthorizeJobAsync`
  checks it after the tenant binding (AB#5070) for download, status and delete: another subject of the same tenant gets 403 and
  no data. Exempt as before: a user token of an ancestor tenant (parent-tenant administration) and an allow-listed cross-tenant
  service client (`IJobTenantAccessGuard.MayAccessInitiatorBoundJobAsync`). A service-token initiator is bound to its client id.
  Dump, restore, archive export and import jobs stay bound to the tenant only.
- Dump = whole-tenant backup **by design** (Gerald, 2026-10-16): a physical mongodump of the whole tenant database (plus archive
  data) with no repository session and no caller context, so it bypasses row-level data permissions. The protection is the gate:
  `TenantManagement` + tenant gate (AB#4497). Document it as "a dump contains all data of the tenant" to users. Pinned by
  `DumpWholeTenantByDesignTests`.
- **Stored queries on Hidden attributes are refused (AB#8049).** `ExportRtModelByQueryByQueryCommand` checks the stored query's
  `FieldFilter` and `Sorting` with `Commands/StoredQueryAccessGuard` before the query runs: a path that reaches a CK v2 `Hidden`
  attribute (e.g. `System.Identity/User.PasswordHash`) fails the job with `ATTRIBUTE_NOT_QUERYABLE` (query, use, path with selector
  values removed, hidden attribute; never a comparison value). The exported entities never carry Hidden values, but the set of
  exported rows would answer a guess of the value. Rule = asset repository / mesh adapter: query type and derived types via the
  engine's `ArchiveHiddenColumnGuard.FindHiddenAttribute`; navigation tails, entity-selector keys and unknown query types against
  every type of the tenant. The check runs even though the asset repository refuses to save such a query, because a query can
  arrive through `ImportRt` or a restore. Tests: `Jobs.Tests/Commands/StoredQueryAccessGuardTests` (unit) and
  `Jobs.IntegrationTests/StoredQueryHiddenAttributeExportTests` (real engine; `tests/ExportTestCkModel` is `ckLanguage: 2` with the
  Hidden attribute `Country.Secret`).
- **Credential guard regression tests (AB#7781)**: `Jobs.IntegrationTests/IdentityCredentialGuardExportTests` runs both export commands
  against the **real** System.Identity model (compiler output `testData/ck-models/ck-system.identity-2.30.0.yaml`, embedded by
  `SystemIdentitySnapshotCkModel`, refresh in the README there) with a user whose `PasswordHash`, `SecurityStamp` and `UserTokens` hold
  fixed dummy values: export by query and by deep graph (from the user; from its role, whose graph does not reach the user today) never contain them, a stored filter (equals for
  the right and a wrong guess, like, regex, range; record path `UserTokens.Value`) or sort order on a credential is refused, and no log line
  (`ExportEngineFixture.Logs`, Debug incl. the MongoDB command log, kept only while a test sets `Enabled`) carries a dummy value. The class
  seeds once (re-seeds after another class's `ResetAsync`, `ResetGeneration`): a refused export leaves its session open (AB#8080) and a reset
  then waits for MongoDB to expire the transaction.
- ImportRt: `InitiatedBySubjectId` (AB#6392) is enough for the lock protection - it is a per-type policy opt-in
  (`ProtectBlueprintLocked`), not a per-role classification - so no roles are passed. Note: the import session stays the
  system session by design of AB#6392 (write grants are not evaluated per caller there).
- ImportRt principal kind (AB#8705): the consumer enqueues `ImportRtAsync(..., initiatedBySubjectId, initiatedByPrincipalKind, ...)`
  with `ImportRtCommandRequest.InitiatedByPrincipalKind` (set by asset-repo from the token: Interactive / Client / Unknown); the job
  passes it to `RtImportCallerContext.UserInitiated(subject, kind)`, where CK types with a `writeAccess` block (engine AB#8574) admit or
  refuse the import (message 144). The overloads without the kind stay for jobs enqueued before (Hangfire resolves the stored signature)
  and run with `Unknown`. Tests: `Jobs.Tests/Jobs/ImportModelJobRtTests`, `Jobs.Tests/Consumers/ModelCommandsConsumerImportCallerTests`.
- Export/import round trip of record arrays (AB#7446): `Jobs.IntegrationTests/DataPointRoundTripTests` (needs Docker) exports an entity
  with a seed-owned RecordArray of `DataPointSample` (the ownership layout of `System.Communication/DataPoint`: `Name` and the
  required `ExternalId` seed-owned, `CurrentValue` / `LastUpdate` runtime state; `tests/ExportTestCkModel`, `Control`) by deep graph
  and by query, checks that the file carries identity and no live values, imports it into an emptied tenant and reads the records
  back. Export evaluates record MEMBERS one by one, so a member that is runtime state is missing from every exported record - the
  bug behind `Loxone/Control.States` losing `ExternalId` (fixed in the System.Communication model, 4.11.0). AB#7450: the engine's import validation
  now covers required record members: a file without `ExternalId` is still imported by default but produces one audit event per
  entity (`<ckAttributeId> at States[n]`, recorded by `RecordingRtImportAuditTrail` in `ExportEngineFixture`), and is rejected
  before anything is written with `Import:StrictMandatoryValidation` (tests switch `RtImportOptions` on the fixture; `ResetAsync` restores it).
- **Every repository session is disposed (AB#8080).** Both export commands and `RepositoryFixupService` open their session with
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

## Command consumers run on every replica (AB#6910)

The six bus commands of this service (`ImportCk`, `ImportCkBatch`, `ImportRt`, `ExportRtByQuery`, `ExportRtByDeepGraph`:
`ModelCommandsConsumer`; `RemoveRecurringJobsByScheduleGroup`: `RecurringJobConsumer`) are registered **shared**
(competing consumers) in `Consumers/BotCommandConsumers.cs`, called from `Program.cs`. With two or more replicas every
replica consumes them and each command is processed **exactly once** by one of them; before AB#6910 they were classic
exclusive consumers (one replica consumed, the others waited).

- `CommandConsumerOptions { MigratesLegacyCommand = true }` is **required**: the senders (asset repository, communication
  controller, AI and MCP service; `AddCommandClient`) still send to the classic address. The consumers use the queues
  `…::shared` and serve the classic address only while no old exclusive replica is alive, so a rolling update starts
  without errors and loses or duplicates nothing. Rollback: stop the new replicas first (event hub `docs/DEVELOPER.md`,
  "Migrating exclusive command queues"). Needs the event hub fix AB#8368 (DistributionEventHub 3.5.65 or newer, one
  `LegacyCommandBridgeObserver` per migrated command): before it, only the first migrated command of a process served
  classic senders, and `BotCommandConsumerReplicaTests` fails.
- Safe under competing consumers: `ModelCommandsConsumer` only enqueues a Hangfire job in the shared MongoDB job storage;
  `RecurringJobConsumer` removes with the idempotent `RemoveIfExists` and takes `JobStorage` / `IRecurringJobManager` from
  DI (registered by `AddHangfire`, same storage as `JobStorage.Current`). The exclusive endpoints had no concurrency limit,
  so one replica already ran these commands concurrently.
- The AB#6909 deprecation warning ("classic EXCLUSIVE topology") is no longer logged at start. A new command consumer is
  added to `BotCommandConsumers` with the shared options and a comment why it is safe.
- Tests: `Jobs.IntegrationTests/BotCommandConsumerReplicaTests` (Docker, RabbitMQ Testcontainer): two replicas with the
  production registration and recording Hangfire fakes, 12 commands of each kind from classic senders, each processed
  once, both replicas consume; no deprecation warning at start; rolling update old exclusive + new shared replica.
- Operations: queues `*::shared` and exchanges `*::send` are new names; broker policies or monitoring matching command
  queue names must include them.

## Failed tenant setup is retried without MongoDB (AB#7249)

Staging-1, 2026-10-21: during a MongoDB primary election the setup of tenants `sbeg` and `eegroider` failed, the log
said "this tenant will be retried in the background", but this service wired no retry store, so nothing was ever
retried and the tenants' adapters stayed offline ("Tenant not enabled") until a manual pod restart. The shared
`TenantSetupRetryTracker` (octo-common-services, in-memory, process-wide) now queues every failed `SetupAsync` and
the 30 s `FailedTenantRetryBackgroundService` re-runs it with exponential backoff (30 s doubling to 5 min, never
giving up). `DefaultConfigurationCreatorService` takes the DI instance (`tenantSetupRetryTracker`) and passes it as
`setupRetryTracker:` to `DefaultConfigurationCreatorServiceStandardized` and now also wires `FailedTenantRegistry` (a failed deferred start used to be logged as "No retry registry available" and dropped). Health check `TenantSetup` on `/health` turns
`Degraded` when a tenant fails longer than `TenantSetupRetry:DegradedAfter` (default 10 min); metrics
`octo.tenant_setup.failed` / `octo.tenant_setup.pending_seconds` (tag `octo.tenant.id`) feed the Dash0 alert. Details,
options and tests: octo-common-services `CLAUDE.md`, section "Tenant Setup - Failure Handling". Test here:
`tests/Jobs.Tests/Services/TenantSetupRetryWiringTests.cs`. Tests use one isolated `TenantSetupRetryTracker` per test and share it across the creator instances of that test when exercising a retry.

## System.Bot on ckLanguage 2 (AB#8593; System.Bot 3.6.0)

`src/SystemBotCkModel` is `System.Bot-3.6.0`, the first `ckLanguage: 2` release, a **Minor** against 3.5.0 (dependency
`System-[2.5,3.0)`, range-retaining). Changelog: `src/SystemBotCkModel/ConstructionKit/CHANGELOG.md`.

- **`derivable: Any` on every type.** ckLanguage 2 changes the default from `Any` to `Model`; `AttributeAggregateConfiguration`
  and `Fixup` declare `Any` explicitly, otherwise the switch is a Major (row T6 of `ck-semver-rules.md` in
  `octo-construction-kit-engine`). A **new** type (e.g. the job type of AB#8445) defaults to `Model`; declare `derivable: Any`
  only where another model should derive from it.
- **Interfaces: none yet.** `Fixup` carries `Name` and `Enabled` and could implement `System/Named-1` and `System/Enableable-1`,
  but the interfaces exist from System 2.7 on: the compiler refuses them under the floor 2.5 ("elements that do not exist at
  the floor"), and raising the floor to 2.7 is a **Major** (row D1, the published 3.5.0 resolved System 2.5.0).
  `AttributeAggregateConfiguration` assigns `System/Enabled` under the name `IsAutoCompleteEnabled` (error 98 for
  `Enableable-1`; renaming is a Major). Both go with a later major.
- **Access modifiers: none tightened.** Refinery Studio (`fixup-scripts-form`) writes `Name`, `Script`, `Enabled`, `IsApplied`
  and `Order`, and octo-cli `CreateFixupScript` sends the generated `RtFixupDto` through generic GraphQL. `AppliedAt`,
  `IsSuccess`, `Output` and `Error` are written only by `RepositoryFixupService` (repository write, not guarded), but none of
  them is `securitySensitive`, so `ReadOnly` would be a Major (row T7; the first-release exception T7a needs the marker). No
  System.Bot attribute holds credentials.
- **Methods** (`methods:` on a type, `execution.mode: async`) are now possible in this model; a new type or method is a Minor.
- **Rollout.** `minEngineVersion` 3.5.1, published under `ck-models/v3/` only. The service embeds the model and imports it on
  tenant resolve; the embedded-import downgrade guard keeps a newer installed System.Bot when an older bot service starts.
  Publishing follows the explicit release decision below (rollout story AB#8602).
- Tests: `tests/Jobs.Tests/CkModels/SystemBotCkLanguage2Tests` (embedded model is 3.6.0 / ckLanguage 2 / minEngineVersion,
  `derivable: Any` on both types, no tightened attribute) and `Jobs.IntegrationTests/SystemBotCkLanguage2PersistenceTests`
  (Docker: import into a tenant, re-import of the same version succeeds, the CK cache rebuilt from MongoDB still has
  `derivable: Any`; `ExportEngineFixture` registers the embedded System.Bot model for it).

## Publishing the system CK model: explicit release decision (AB#7433)

A merge to `main` does **not** publish `System.Bot` to the CK catalogs. The pipeline parameter `publishSystemCkModels`
(boolean, **default false**; same name and semantics as AB#7343 in `octo-construction-kit-engine`, AB#7432 in the
communication controller) feeds `publishOnMain` of `validate-and-publish-ck-versions.yml` (`tpl-v1.8.0`). A main build still
runs ValidateVersion, the schema compile and the cascade check, logs `CK publish on main is OFF (publishOnMain=false)` and
publishes nothing. Reason: a new System* version breaks exact-pinned dependents on live tenants until they are re-pinned.

**To publish explicitly** (needs the release decision of the platform owner): Azure DevOps -> pipeline `octo-bot-services-CI` ->
*Run pipeline* -> branch `main` -> tick *Publish System CK models to the CK catalogs (main)*; CLI:
`az pipelines run --id 94 --branch main --parameters publishSystemCkModels=true`. `r*` tags (release train) publish as
before; `dev/*`/`test/*` builds never publish to the shared catalogs. A merge that bumps the model therefore leaves the version
unpublished until that run; repos that restore it from the catalog must wait for it.
