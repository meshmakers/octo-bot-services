---
description: Rules of the per-tenant paging index upgrade job: shape by keys, triggers, leases, incomplete runs, leftover categories, run events.
applies_to: src/Jobs/IndexUpgrade/**, tests/Jobs.Tests/IndexUpgrade/**
---

# Paging index upgrade per tenant: rules

## Paging index upgrade per tenant

`src/Jobs/IndexUpgrade`: one idempotent Hangfire job per tenant (`IIndexUpgradeJob`, queue `index-upgrade`, own lightweight
Hangfire server with one worker) calls `ITenantContext.UpgradeTiebreakIndexesAsync` (engine-mongodb). Full description,
configuration per environment and prod rollout: `docs/index-upgrade.md`. Rules:

- **Shape by index keys only** (`LegacyIndexShape`): legacy = not unique, no `_fts`, first key `ckTypeId`, an `rtState` key, no `_id`.
  Never decide by the name (hashed names `{Type}_{n}_id_<hash>` exist). No marker decides anything; the last-run record
  (system database, `IndexUpgradeRuns`, status as text) is status only.
- **Triggers**: after-deploy sweep (`IndexUpgradeStartupService` in the background, Immediate, after `StartDelay`, one sweep for all
  replicas), window sweep (recurring `index-upgrade-window`, window from the occurrence = `PerformContext.BackgroundJob.CreatedAt`),
  `PosCreateTenant` in `TenantManagementConsumer` (create and attach; restore and copy attach; scheduled after `StartDelay`). Every
  replica triggers; `IIndexUpgradeCoordination.EnqueueOnce` keeps one pending or running job per tenant once its job id is recorded
  (a replica stopping between creating the job and recording the id can leave a second job; the leases serialize the two).
- **Global limit = own lease** (`HangfireIndexUpgradeCoordination` slots on Hangfire distributed locks, failed releases swallowed), not
  `DisableConcurrentExecution`: its lock timeout fails waiting jobs during hours-long builds. Leases are taken before the tenant is
  resolved or inspected. A run that cannot work now throws `IndexUpgradeDeferredException`; `IndexUpgradeDeferAttribute` (order 0,
  before `AutomaticRetry`) makes it `Scheduled`, never `Failed`.
- **Incomplete, not Failed**: legacy indexes of collections the engine manages (collection roots of Available models) that are
  left after the run - a build the engine logged as failed - end it `Incomplete` (warning, no retry). So does a final read that
  failed after the engine returned (`LegacyIndexesAfter` null, "not verified": look at MongoDB, not at the models) and CK metadata
  that cannot be read to classify what is left.
- **Leftover categories**: every legacy index is *managed*, *model not Available* (root of a model in another state) or
  *orphaned* (no collection root), decided by the tenant's CK metadata (`CkCollectionRootReader`, reads the engine's `CkType`
  collection - a deliberate coupling pinned by `IndexUpgradeIntegrationTests.CollectionRootReader_ReadsWhatTheEngineWrote`), never by
  the name. Leftovers that are only model not Available or orphaned end the run `Succeeded` with `Warning` and `Leftovers` in the
  record (logged at Warning), without calling the engine when nothing is managed; the sweep enqueues a tenant only for a managed
  index. Details: "Leftover categories" in `docs/index-upgrade.md`.
- **CK imports go first**: `ImportModelJob` CK imports enter `ICkImportIndexUpgradeGate` (own yield request per import + tenant lease);
  a running upgrade stops before its next collection and is rescheduled. Embedded CK imports of other services do not pass the gate
  (90 s lock wait in the engine, then `TenantSetupRetryTracker`); documented remaining risk, engine fix tracked separately.
- **Cancellation**: the job takes Hangfire's `IJobCancellationToken` (the bot's `IBotCancellationToken` is never filled by Hangfire), so a
  shutdown stops it before the next collection and Hangfire requeues it. Status writes and final reads are bounded (4 s each, together below
  Hangfire's 15 s shutdown timeout).
- **Configuration never stops the service**: `IndexUpgradeOptions.Normalize` (post-configure) makes out-of-range values safe; a value the
  binder cannot convert is caught by the start-up registration and the import gate (upgrade off, imports as before).
- **Code defaults = production** (`Schedule = Window`, `Window:Enabled = false`): nothing is built without deployment values; the
  pre-production environments set `OCTO_INDEXUPGRADE__SCHEDULE=Immediate`. `MaxConcurrent = 0` switches the upgrade off in every schedule.
- **Run events at the shipped level**: bot-services ships `Warn` and above only, so the sweep's start and end and the end of
  every tenant job that is not deferred for a retry are logged at Warning (Error for `Failed`) with the prefixes and event ids of
  `IndexUpgradeRunEvents` (`IndexUpgrade run started:`, `IndexUpgrade run ended:`, `IndexUpgrade tenant ended:`). Nothing else of
  the upgrade goes to Warning on the success path; index names stay at Information (the record keeps them), only the leftover
  summary (models not Available, orphaned collections, with counts) stays in the end line. Never
  change a log level for this. Pinned by `IndexUpgradeRunEventsTests`.
- Tests: `tests/Jobs.Tests/IndexUpgrade/*` (unit; `IndexUpgradeWiringTests` pins that the host registrations give `ImportModelJob`
  its gate and `TenantManagementConsumer` its scheduler - both are optional constructor parameters),
  `tests/Jobs.IntegrationTests/IndexUpgradeIntegrationTests` (Docker: real engine,
  unmanaged collection, attach, two Hangfire servers on one storage, server stopped during a job).
