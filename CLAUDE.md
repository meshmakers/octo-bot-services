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
- ImportRt: `InitiatedBySubjectId` (AB#6392) is enough for the lock protection - it is a per-type policy opt-in
  (`ProtectBlueprintLocked`), not a per-role classification - so no roles are passed. Note: the import session stays the
  system session by design of AB#6392 (write grants are not evaluated per caller there).
- Export/import round trip of record arrays (AB#7446): `Jobs.IntegrationTests/DataPointRoundTripTests` (needs Docker) exports an entity
  with a seed-owned RecordArray of `DataPointSample` (the ownership layout of `System.Communication/DataPoint`: `Name` and the
  required `ExternalId` seed-owned, `CurrentValue` / `LastUpdate` runtime state; `tests/ExportTestCkModel`, `Control`) by deep graph
  and by query, checks that the file carries identity and no live values, imports it into an emptied tenant and reads the records
  back. Export evaluates record MEMBERS one by one, so a member that is runtime state is missing from every exported record - the
  bug behind `Loxone/Control.States` losing `ExternalId` (fixed in the System.Communication model, 4.11.0). AB#7450: the engine's import validation
  now covers required record members: a file without `ExternalId` is still imported by default but produces one audit event per
  entity (`<ckAttributeId> at States[n]`, recorded by `RecordingRtImportAuditTrail` in `ExportEngineFixture`), and is rejected
  before anything is written with `Import:StrictMandatoryValidation` (tests switch `RtImportOptions` on the fixture; `ResetAsync` restores it).
- Tests: `tests/Jobs.IntegrationTests` (`ExportPermissionTests`, needs Docker): the two real export commands against the real engine and a
  Testcontainers MongoDB with a restricted policy table and the tiny `tests/ExportTestCkModel` - denied type, owned-only, allowed caller,
  unrestricted tenant (same bytes for every caller), system context refused, for the query and the deep-graph export. Unit tests:
  `ExportCallerContextTests` (session for the caller, system/empty/no-factory refused), `ExportModelJobCallerTests`,
  `ModelCommandsConsumerExportCallerTests`, `ExportArtifactSubjectBindingTests` (enqueue through the real consumer, then
  403/200 per subject, parent tenant, client id, status/delete, other job types unaffected).

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
