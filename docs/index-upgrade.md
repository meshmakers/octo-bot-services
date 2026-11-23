# Paging index upgrade (AB#9118, AB#9322)

Pages of runtime queries sort totally only when the CK Ascending indexes of a collection have the keys
`{ckTypeId, attr…, _id, rtState}` (engine-mongodb AB#8960, `PagingTiebreakGate`). Indexes built by an engine before
AB#8960 have the legacy keys `{ckTypeId, attr…, rtState}`; a CK import and `UpdateIndexesAsync` keep them on purpose. The
only way to replace them is `ITenantContext.UpgradeTiebreakIndexesAsync`. The bot service runs it as one idempotent
Hangfire job per tenant. Until a tenant was upgraded, its pages sorted by an attribute with a legacy index keep the order
they had before AB#8960.

## What the job does

`IIndexUpgradeJob.UpgradeTenantAsync(tenantId, trigger)` (queue `index-upgrade`, `src/Jobs/IndexUpgrade/IndexUpgradeJob.cs`):

1. **Switched off or outside the window**: with `MaxConcurrent` 0, or (Window schedule) a window that is not enabled or not
   open, the run records `Deferred` (number of legacy indexes unknown) without a build and ends `Succeeded` in Hangfire;
   the next sweep enqueues a new job.
2. **Global slot**: at most `IndexUpgrade:MaxConcurrent` tenants (default 1) are rebuilt at the same time across all
   replicas. A run that finds every slot held ends `Scheduled` again after `RetryDelay` (logged at Debug). It neither
   resolves the tenant nor reads an index list while it waits.
3. **CK import first**: when a CK import of the tenant waits or runs (tenant lease, see below), the run ends `Scheduled`.
4. **Tenant and legacy indexes by their keys** (`LegacyIndexShape`): not unique, no text index, first key `ckTypeId`, an
   `rtState` key, no `_id` key. The index name is never looked at: an upgraded index may carry a hashed name
   `{Type}_{n}_id_<hash>`. No legacy index: the run ends at once (`NothingToDo`), no index is built.
   **Category by the CK metadata** (AB#9322, see "Leftover categories" below): when none of the legacy indexes is
   *managed*, the run ends `Succeeded` with a warning at once - the engine is not called and the tenant's
   `index_update_lock` is not taken. If the CK metadata cannot be read, the engine runs as before.
5. **Build**: `UpgradeTiebreakIndexesAsync` under the tenant's `index_update_lock`, collection by collection, the new
   index first, the legacy one dropped after it. The job's token stops the engine **before the next collection** (an
   index build already started finishes) when the service shuts down, the window closes, or a CK import of the tenant
   asks to go first. The window end is checked once more right before the engine is called.
6. **Check**: one final read of the legacy indexes gives the count and the names; what is left is classified by the CK
   metadata read again at that moment. Left over without a cancellation:
   - *managed* indexes left (an index build of an `Available` model that failed: the engine logs the error and keeps the
     legacy index) -> `Incomplete`, warning, the error lists only the managed indexes; no retry of this job, the next sweep
     looks again (a transient cause then succeeds);
   - only *model not Available* and/or *orphaned* indexes left -> `Succeeded` with a warning in the record (`Warning`,
     `Leftovers`) and in the log (level Warning) - never a silent success;
   - the CK metadata cannot be read -> `Incomplete` ("could not be read to tell why", the indexes listed).

### Leftover categories (AB#9322)

Every legacy index is counted in exactly one category, decided by the tenant's CK metadata - the collection roots
(`IsCollectionRoot`) and the state of their models - never by the index name:

| Category | Meaning | Keeps the tenant `Incomplete` | Gets a job from the sweep | Operator action |
| --- | --- | --- | --- | --- |
| *managed* | collection root of an `Available` model: the engine upgrades it | yes | yes | read the engine's warnings (index build failed) |
| *model not Available* | collection root of a model in another state (`ResolveFailed`, `Importing`); the record names model id and state, e.g. `Monitoring-2.1.0 ResolveFailed` | no | no | `octo-cli LibraryStatus` of the tenant, then Fix or Update the model. A retired model without a catalog version (e.g. `Monitoring-2.1.0` on test-2 octosystem, retired 2026-09-30) stays until it is removed (AB#9525) |
| *orphaned* | a collection without a collection root, left by an older model or a migration (e.g. `RtEntity_SystemQuery` after System 1.0.3 -> 2.0.0) | no | no | none; the collection and its indexes are not deleted |

A model that becomes `Available` later (library update or Fix) turns its indexes *managed*, so the next sweep enqueues the
tenant. A CK library update never creates legacy indexes: the engine creates missing indexes in the tiebreak shape and
keeps an existing legacy index, so no extra sweep is needed after a model update beyond the normal one.

**How the CK metadata is read** (`CkCollectionRootReader`): the engine's public CK type queries
(`ITenantRepository.GetCkTypeAsync`) return types of `Available` models only, so a model in another state could not be
named. The reader therefore reads the engine's CK metadata collection `CkType` of the tenant database directly (read-only;
fields `_id`, `ckModelId`, `modelState`, `isCollectionRoot`) and derives the collection name as the engine does
(`RtEntity_` + `ToStorageIdentifier()` of the type id). This couples bot-services to the engine's storage schema; the
coupling is kept in that one class, and the integration test `CollectionRootReader_ReadsWhatTheEngineWrote` reads metadata
the real engine wrote, so a schema change in the engine fails that test, not production. A document the reader cannot map
(no or an unknown `modelState`) fails the read; the job and the sweep then fall back to the behaviour without
classification.

A run is never a failure because it cannot work now: `IndexUpgradeDeferredException` + `IndexUpgradeDeferAttribute`
(state election filter, order 0, before `AutomaticRetry`) turn it into `Scheduled`: the job never enters `Failed`, the
failed counter stays as it is, and no retry is used up. Like `AutomaticRetry`, Hangfire lists the elected-away state in the
job history of the dashboard (a `Failed` line with `IndexUpgradeDeferredException` and the reason, followed by
`Scheduled`). Real failures are retried three times (1, 5, 15 min) and then stay `Failed` with the error. A failing job
never stops the service or fails a health check. Status writes and the index reads after the engine call are bounded
(4 s each, together below Hangfire's 15 s shutdown timeout), so a stalled MongoDB never keeps the leases. The bound limits
the attempt, it does not guarantee the write: during a MongoDB stall the final record can be missing or stale. If the final
read fails while the engine returned normally, the run ends `Incomplete` ("not verified", legacy indexes after unknown),
never `Succeeded`.

### Triggers

| Trigger | Where | When |
| --- | --- | --- |
| `after-deploy` | `IndexUpgradeStartupService` (background service) schedules `IIndexUpgradeSweepJob` | Immediate schedule: `StartDelay` (default 5 min) after a service start; replicas that start together share one sweep (deduplicated like a tenant job) |
| `window` | recurring job `index-upgrade-window` at `Window:Cron` | Window schedule with `Window:Enabled = true`; opens the window from the occurrence of the recurring job (the creation time of the sweep job) for `Window:MaxDuration` and sweeps. A retry of the sweep keeps that end, a retry after it opens nothing; triggering the recurring job by hand opens a window from now |
| `tenant-created` | `TenantManagementConsumer` on `PosCreateTenant` | a tenant was created or attached; a restore and a copy attach as well. The job is scheduled `StartDelay` later, so that the services set the tenant up (embedded CK imports) before the rebuild takes `index_update_lock`. Window schedule: only while a window is open, otherwise the next window picks the tenant up |

The sweep reads the index list of every tenant (system tenant and every registry row) and enqueues a job only for a tenant
that still has a *managed* legacy index (AB#9322); tenants with only *model not Available* or *orphaned* leftovers are
counted in the sweep summary but get no job and take no `index_update_lock` (if their CK metadata cannot be read, they are
enqueued as before and the job decides). Tenants already upgraded cost one `listIndexes` per collection and no job. A tenant whose
index list could not be read fails the sweep after all other tenants were checked, so Hangfire retries it (a retry reads
every tenant again; tenants whose job is still active are not enqueued twice, tenants whose job ended get a new one, which
builds only what is left).

The start of the service never waits for the upgrade: the registration runs in a background service (retried with a
growing pause while the job storage refuses it), it only writes to the job storage, no tenant is read and nothing is built
while the service starts, and tenant context loads and requests never run the build. A configuration that builds nothing
(Window with the window off, or `MaxConcurrent` 0) registers nothing.

### One job per tenant across replicas

`PosCreateTenant` arrives on every replica, and every replica runs the after-deploy registration.
`IIndexUpgradeCoordination.EnqueueOnce` takes a short Hangfire distributed lock per tenant, looks at the job remembered for
the tenant (hash `octo:index-upgrade:job:<tenant>`) and enqueues only when that job is not `Enqueued`, `Scheduled`,
`Awaiting` or `Processing` - once its job id is recorded. A finished job does not block the next one (a restore brings legacy
indexes back). If a replica
stops between creating the job and remembering its id, a second job can follow; both are idempotent and serialize on the
slot and the tenant lease, the second ends `NothingToDo`.

### Global limit: own lease, not `DisableConcurrentExecution`

`DisableConcurrentExecution(timeout)` waits for its lock up to the timeout and then fails the job
(`DistributedLockTimeoutException`; seen with dumps, `JobOperationRelay`). A tenant rebuild can take hours, so every waiting
tenant would end as a failed job, retry after retry. The global limit is therefore an own lease: the slots
`octo:index-upgrade:slot:0..N-1` are Hangfire distributed locks of the shared MongoDB job storage, taken without waiting
(`HangfireDistributedLockOwner`, held on one thread because the Hangfire.Mongo lock is thread-affine, AB#5539). Hangfire.Mongo
keeps a held lock alive with a heartbeat; after a killed pod it expires. A failed release is swallowed (the lock expires),
so it never changes the outcome of the import or upgrade that held it. A run that gets no slot is `Scheduled` again and
holds no worker while it waits. Each replica runs a lightweight Hangfire server (workers only, the full server schedules)
for the queue `index-upgrade` with one worker, so a long rebuild never takes a worker of the other jobs.

Limit of the lease: Hangfire.Mongo releases a lock by its resource name. A process that stalls longer than the lock
lifetime (30 s) while holding a slot can lose it to another replica and later delete that replica's lock, so for that time
two tenants may build at once. Each tenant stays protected by its own `index_update_lock` in the engine.

### CK imports go first (AC "no clash with CK import")

The engine holds the tenant's `index_update_lock` for a whole upgrade run; a CK import waits for that lock only 90 s
(`RepositoryDistributedLockService`) and then fails. So:

- **CK import jobs of this service** (`ImportModelJob.ImportCkAsync` / `ImportCkBatchAsync`) enter `ICkImportIndexUpgradeGate`
  first: each import sets its own yield request for the tenant (renewed with every attempt, valid 60 s) and waits for the
  tenant lease (`octo:index-upgrade:tenant:<tenant>`). A running upgrade sees a request within `PollInterval` (the yield
  check runs apart from the progress log), stops before its next collection, records `Deferred` and is `Scheduled` again
  (30 s); the import runs; then the upgrade continues with the collections left. After `ImportWaitTimeout` (default 30 min)
  the import runs anyway and may meet the 90 s lock wait of the engine (then its retries). The wait holds a worker of the
  default queue and, like every CK import job, does not see a shutdown. CK imports of one tenant through this service now
  run one after the other (a batch holds the tenant once for all its models). While the upgrade builds nothing, or its
  configuration cannot be read, the gate does nothing.
- **The upgrade waits for a CK import**: while the tenant lease is held or a yield request is set, the upgrade does not
  start. When `index_update_lock` is held by someone else (an embedded import or `UpdateIndexesAsync` of another service), the
  engine's `TimeoutException` after 90 s makes the run `Scheduled` again after `RetryDelay`.
- **Remaining risk** (accepted, Odin 2026-11-18, option A): the **embedded** CK model imports of the other services at their
  tenant setup (asset repository, communication controller, …; after a deploy that raises a model version, or right after
  an attach) do not go through this gate. Meeting a running rebuild they time out after 90 s, and the service retries the
  tenant setup (`TenantSetupRetryTracker`, AB#7249, 30 s doubling to 5 min) until the rebuild of the current collection
  ended; the `TenantSetup` health check turns `Degraded` after 10 minutes. `StartDelay` (5 min) lets those imports finish
  before the after-deploy sweep starts and before the job of a created or attached tenant runs. The engine fix (release
  `index_update_lock` between collections) is AB#9302.

### Restart during a job

The job takes Hangfire's own `IJobCancellationToken` (not `IBotCancellationToken`, whose token the other bot jobs never get):
on a graceful shutdown the engine stops before the next collection, the run is recorded `Interrupted`, and Hangfire requeues
the job. A killed pod leaves it `Processing` until Hangfire.Mongo's sliding invisibility timeout (5 min) requeues it; its
leases expire with the lock lifetime. The next run completes the collections left: a re-run skips upgraded collections and
completes interrupted ones, and the engine never drops an index before its replacement exists.

**Rolling update from a version without the upgrade:** an older replica that shares the job storage cannot load the job
types of the upgrade. If it runs the delayed after-deploy sweep, Hangfire moves the sweep to `Failed` (no data is touched);
the next start of a new replica schedules it again. Switch `Schedule=Immediate` on only for a deployment in which every
replica runs this version, or accept that the first sweep may need the next start.

## Last-run record and logs

One document per tenant in the **system database**, collection `IndexUpgradeRuns` (`_id` = tenant id in lower case): job id
(of the executing Hangfire job), trigger, status (`Running`, `Succeeded`, `NothingToDo`, `Deferred`, `Interrupted`,
`Incomplete`, `Failed`, stored as text), start, end, duration, legacy indexes before and after (empty = not read, e.g. a
run that ended at the window or a lease),
collections upgraded (only collections whose replacement index was seen), error. Status only: nothing reads it to decide
whether to build. The status endpoint of AB#9301 reads it. A tenant that never needed a job has no record.

Fields added by AB#9322 (absent in records of older versions; null when the run ended before it looked or could not
classify):

| Field | Content |
| --- | --- |
| `Leftovers.ManagedIndexes` | legacy indexes left of collection roots of `Available` models |
| `Leftovers.ModelNotAvailableIndexes` | legacy indexes left of collection roots of models that are not `Available` |
| `Leftovers.OrphanedIndexes` | legacy indexes left of collections without a collection root |
| `Leftovers.ModelsNotAvailable` | one entry per model `{ModelId, State, IndexCount}`, e.g. `Monitoring-2.1.0`, `ResolveFailed`, 4, ordered by model id |
| `Leftovers.OrphanedCollections` | the orphaned collection names, ordered, at most 20 |
| `Leftovers.OrphanedCollectionsTotal` | how many orphaned collections there are (also beyond the 20 listed) |
| `Warning` | the same leftovers as text, set whenever model not Available or orphaned indexes are left, e.g. `6 legacy index(es) are left that the job cannot upgrade; model not Available: Monitoring-2.1.0 ResolveFailed (4 indexes); orphaned (no collection root): RtEntity_SystemQuery (2 indexes)` |

The names are the properties of `IndexUpgradeRunRecord` / `IndexUpgradeLeftovers`; in the stored document they follow the
service's MongoDB naming convention. After a successful run with nothing left `Leftovers` holds zeros.

Structured logs (Dash0, service `octo-bot-services`):

- per collection: `Index upgrade of tenant '{TenantId}': collection '{Collection}' index '{LegacyIndexName}' {LegacyKeys}
  replaced by '{NewIndexName}' {NewKeys} (seen {SinceLastMs} ms after the previous collection)` - the time since the
  previous observation that confirmed a replacement, shared by all collections found in the same observation (precision
  `PollInterval`);
- per run: `Index upgrade of tenant '{TenantId}' ended {Status} after {DurationMs} ms ({Trigger}): {CollectionsUpgraded}
  collection(s) upgraded, legacy indexes {LegacyIndexesBefore} -> {LegacyIndexesAfter}`, when classified followed by
  `(managed {ManagedIndexes}, model not Available {ModelNotAvailableIndexes}, orphaned {OrphanedIndexes})` and the warning;
  level Warning when indexes are left. The words "model not Available" appear only for that category;
- per sweep: `Index upgrade sweep ({Trigger}) done: … checked, … with legacy indexes the engine upgrades, … job(s)
  enqueued, … not checked; … tenant(s) with only leftovers no job can change and no job (legacy indexes: model not
  Available …, orphaned …)`;
- at the start: `Index upgrade: Immediate schedule, …`, `Window schedule, …` or `switched off …`.

## Configuration

Section `IndexUpgrade` (environment `OCTO_INDEXUPGRADE__*`):

| Key | Default | Meaning |
| --- | --- | --- |
| `Schedule` | `Window` | `Immediate` or `Window` |
| `Window:Enabled` | `false` | the window opens only when set |
| `Window:Cron` | `0 1 * * *` | when the window opens (Hangfire cron, UTC). Omitted: the default. Set to blank: the window is off. An expression Hangfire rejects removes the window job (error in the log, an older trigger is removed) - no window opens until the value is fixed |
| `Window:MaxDuration` | `04:00:00` | how long it stays open; no collection starts after it closed |
| `MaxConcurrent` | `1` | tenants rebuilt at the same time across all replicas; `0` builds nothing |
| `StartDelay` | `00:05:00` | after-deploy sweep after the service start (Immediate); delay of a job for a created or attached tenant (up to 30 days) |
| `RetryDelay` | `00:05:00` | next try of a deferred run (up to 1 day) |
| `ImportWaitTimeout` | `00:30:00` | how long a CK import waits for the upgrade to stop; `0` = do not wait |
| `PollInterval` | `00:00:05` | how often a running job checks yield requests and progress (up to 10 min) |

The code defaults are the production setting, so a cluster without values builds nothing. Values the code cannot use are
made safe (`IndexUpgradeOptions.Normalize`): a `Schedule` that names no member becomes `Window` with the window off, a
negative `MaxConcurrent` 0, durations out of range their defaults. A value the configuration binder cannot convert at all
(text for a number, an unknown schedule name) leaves the upgrade off with an error in the log; the service and its CK
imports run as before.

| Environment | Values |
| --- | --- |
| test-2 | `OCTO_INDEXUPGRADE__SCHEDULE=Immediate` |
| staging-1 | `OCTO_INDEXUPGRADE__SCHEDULE=Immediate` |
| prod-1, prod-2 | defaults (`Window`, window off); switched on per cluster after the go (below) |

The deployment values live in `octo-mesh-deployment` (bot-services values per environment); they are set there by a
separate change.

## Production rollout procedure

1. **Measure on staging-1** (a copy of prod-2): with `Schedule=Immediate`, after the deploy read the run records
   (`db.IndexUpgradeRuns.find()` in the system database) or the end lines in Dash0 and note the duration of the largest
   tenants. Pick `Window:MaxDuration` and `Window:Cron` so that the largest collection fits into a quiet time.
2. **Check the tenants' LibraryStatus** before switching a prod window on: `octo-cli LibraryStatus` per tenant; a model that
   is not `Available` keeps its legacy indexes (category *model not Available*) until it is fixed or updated - decide per
   model whether to fix it first. The run records name such models after the first window.
3. **Go per cluster**: switching the window on for prod-1 or prod-2 needs the developer's go for that cluster.
4. **Switch on**: set `OCTO_INDEXUPGRADE__WINDOW__ENABLED=true` (and `…__WINDOW__CRON`, `…__WINDOW__MAXDURATION` if not the
   defaults) for the cluster and roll the bot service out. At the next cron time the window opens and the sweep enqueues
   every tenant with legacy indexes; they are rebuilt one after the other (`MaxConcurrent`). A tenant created or attached
   outside a window is picked up by the next window; a run that did not finish continues in the next window.
5. **Verify**: per tenant, no CK Ascending index without `_id` of a *managed* collection is left:
   `db.getCollectionNames().forEach(c => db[c].getIndexes().filter(i => !i.unique && Object.keys(i.key)[0] == "ckTypeId"
   && "rtState" in i.key && !("_id" in i.key) && !("_fts" in i.key)).forEach(i => print(c, i.name)))` prints nothing -
   this live check decides (it also prints *model not Available* and *orphaned* leftovers; the run record's `leftovers`
   tells them apart). The run record is a hint only: a tenant that was clean already has none, and a record left
   `Running` or missing after a failed status write is a bookkeeping matter (the next sweep skips a clean tenant). In the
   Hangfire dashboard (`/ui/jobs`, queue `index-upgrade`) look into failed jobs of the rollout that are not resolved; an
   older `Failed` entry whose tenant a later window completed (the index check above passes) needs nothing.
6. **Switch off**: first stop new builds - `OCTO_INDEXUPGRADE__WINDOW__ENABLED=false` (or `OCTO_INDEXUPGRADE__MAXCONCURRENT=0`,
   which also stops an Immediate schedule) - and roll the bot service out: the recurring job `index-upgrade-window` is
   removed at the start, pending jobs end without a build. Deleting pending jobs in the dashboard alone does not stop the
   upgrade: the next sweep enqueues them again. A rebuild in progress finishes its current collection; the rollout stops it
   before the next one.

## Tests

- Unit (`tests/Jobs.Tests/IndexUpgrade`): shape by keys incl. hashed names and key order; deferral filter before
  `AutomaticRetry` and its exact deadline; options made safe; job: nothing to do, upgrade, re-run, global slot held (no
  resolve, no inspection), `MaxConcurrent` 0, tenant lease held, `index_update_lock` timeout, engine failure, legacy indexes
  the engine does not upgrade (`Incomplete`), leftovers by category (the two test-2 shapes: model not Available only and
  orphaned only end `Succeeded` with the warning stored and logged at Warning, without the engine; managed and leftovers
  mixed; managed left with leftovers -> `Incomplete` naming only the managed; CK metadata unreadable), CK import during
  the upgrade (stops before the next collection, import runs, the lease proven held while the import runs),
  upgrade continues), shutdown (`Interrupted`, re-run completes), window off / closed / closing during the run; scheduler:
  two replicas receive `PosCreateTenant` -> one job, window rules, enqueue failure does not fail the consumer; sweep (two
  overlapping sweeps, window from the occurrence, retry after the window, unreadable tenant retried, only tenants with a
  managed index enqueued and the others counted, a model that becomes Available enqueued by the next sweep, CK metadata
  unreadable -> enqueued); classification (`LegacyIndexClassificationTests`: each category, two roots on one collection,
  the cap of 20, collection naming, `CkType` document mapping); import gate (two imports, configuration that cannot be
  read, upgrade off, an attempt started late by the thread pool waits only for the rest of `ImportWaitTimeout` or gives up
  at once, AB#9317); start-up registration; wiring (`IndexUpgradeWiringTests`: the host registrations give
  `ImportModelJob` its gate and `TenantManagementConsumer` its scheduler, `Program.cs` calls `AddOctoIndexUpgrade` -
  comments do not count).
- Integration (`tests/Jobs.IntegrationTests/IndexUpgradeIntegrationTests`, Docker): real engine clean run, re-run and a run
  stopped after the build end in the same indexes; a legacy index in a collection without a collection root -> `Succeeded`
  with the warning naming it, the managed collection upgraded; the collection root reader on metadata the real engine wrote
  (collection name, `Available`, and `ResolveFailed` written with the engine's class map); attach of a tenant with legacy indexes -> `PosCreateTenant` -> job on a real
  Hangfire.Mongo storage -> new keys; two Hangfire servers on one storage with two tenants due (the first build held until
  the other job was deferred) -> never two rebuilds, the waiting job `Scheduled`, never `Failed`; a server stopped during a
  job -> `Interrupted`, another server completes it.
