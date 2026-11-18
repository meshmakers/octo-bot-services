# Paging index upgrade (AB#9118)

Pages of runtime queries sort totally only when the CK Ascending indexes of a collection have the keys
`{ckTypeId, attr…, _id, rtState}` (engine-mongodb AB#8960, `PagingTiebreakGate`). Indexes built by an engine before
AB#8960 have the legacy keys `{ckTypeId, attr…, rtState}`; a CK import and `UpdateIndexesAsync` keep them on purpose. The
only way to replace them is `ITenantContext.UpgradeTiebreakIndexesAsync`. The bot service runs it as one idempotent
Hangfire job per tenant. Until a tenant was upgraded, its pages sorted by an attribute with a legacy index keep the order
they had before AB#8960.

## What the job does

`IIndexUpgradeJob.UpgradeTenantAsync(tenantId, trigger)` (queue `index-upgrade`, `src/Jobs/IndexUpgrade/IndexUpgradeJob.cs`):

1. **Window** (Window schedule only): the window must be enabled and open, otherwise the run ends `Deferred` without a build.
2. **Global slot**: at most `IndexUpgrade:MaxConcurrent` tenants (default 1) are rebuilt at the same time across all
   replicas. A run that finds every slot held ends `Scheduled` again after `RetryDelay` (logged at Debug; it reads no
   index list while it waits).
3. **CK import first**: when a CK import of the tenant waits or runs (tenant lease, see below), the run ends `Scheduled`.
4. **Legacy indexes by their keys** (`LegacyIndexShape`): not unique, no text index, first key `ckTypeId`, an `rtState`
   key, no `_id` key. The index name is never looked at: an upgraded index may carry a hashed name
   `{Type}_{n}_id_<hash>`. No legacy index: the run ends at once (`NothingToDo`), no index is built.
5. **Build**: `UpgradeTiebreakIndexesAsync` under the tenant's `index_update_lock`, collection by collection, the new
   index first, the legacy one dropped after it. The job's token stops the engine **before the next collection** (an
   index build already started finishes) when the service shuts down, the window closes, or a CK import of the tenant
   asks to go first.
6. **Check**: the legacy indexes are read again. Left over without a cancellation (the engine logs and skips an index it
   cannot build) means `Failed`.

A run is never a failure because it cannot work now: `IndexUpgradeDeferredException` + `IndexUpgradeDeferAttribute`
(state election filter, order 0, before `AutomaticRetry`) turn it into `Scheduled`: the job never enters `Failed`, the failed
counter stays as it is, and no retry is used up. Like `AutomaticRetry`, Hangfire lists the elected-away state in the job
history of the dashboard (a `Failed` line with `IndexUpgradeDeferredException` and the reason, followed by `Scheduled`). Real failures are retried three times
(1, 5, 15 min) and then stay `Failed` with the error. A failing job never stops the service or fails a health check.

### Triggers

| Trigger | Where | When |
| --- | --- | --- |
| `after-deploy` | `IndexUpgradeStartup.Apply` (Program.cs) schedules `IIndexUpgradeSweepJob` | Immediate schedule: `StartDelay` (default 5 min) after a service start; replicas that start together share one sweep (deduplicated like a tenant job) |
| `window` | recurring job `index-upgrade-window` at `Window:Cron` | Window schedule with `Window:Enabled = true`; opens the window for `Window:MaxDuration` and sweeps. A retry of the sweep inside an open window does not extend it; triggering the recurring job by hand outside a window opens one now |
| `tenant-created` | `TenantManagementConsumer` on `PosCreateTenant` | a tenant was created or attached; a restore and a copy attach as well. The job is scheduled `StartDelay` later, so that the services set the tenant up (embedded CK imports) before the rebuild takes `index_update_lock`. Window schedule: only while a window is open, otherwise the next window picks the tenant up |

The sweep reads the index list of every tenant (system tenant and every registry row) and enqueues a job only for a tenant
that still has a legacy index; tenants already upgraded cost one `listIndexes` per collection and no job. The start of
the service only writes to the job storage: no tenant is read and nothing is built while the service starts, and tenant
context loads and requests never run the build.

### One job per tenant across replicas

`PosCreateTenant` arrives on every replica, and every replica runs the after-deploy sweep. `IIndexUpgradeCoordination.EnqueueOnce`
takes a short Hangfire distributed lock per tenant, looks at the job remembered for the tenant (hash
`octo:index-upgrade:job:<tenant>`) and enqueues only when that job is not `Enqueued`, `Scheduled`, `Awaiting` or `Processing`.
A finished job does not block the next one (a restore brings legacy indexes back).

### Global limit: own lease, not `DisableConcurrentExecution`

`DisableConcurrentExecution(timeout)` waits for its lock up to the timeout and then fails the job
(`DistributedLockTimeoutException`; seen with dumps, `JobOperationRelay`). A tenant rebuild can take hours, so every waiting
tenant would end as a failed job, retry after retry. The global limit is therefore an own lease: the slots
`octo:index-upgrade:slot:0..N-1` are Hangfire distributed locks of the shared MongoDB job storage, taken without waiting
(`HangfireDistributedLockOwner`, held on one thread because the Hangfire.Mongo lock is thread-affine, AB#5539). Hangfire.Mongo
keeps a held lock alive with a heartbeat; after a killed pod it expires. A run that gets no slot is `Scheduled` again and
holds no worker while it waits. Each replica runs a Hangfire server of its own for the queue `index-upgrade` with one worker,
so a long rebuild never takes a worker of the other jobs.

### CK imports go first (AC "no clash with CK import")

The engine holds the tenant's `index_update_lock` for a whole upgrade run; a CK import waits for that lock only 90 s
(`RepositoryDistributedLockService`) and then fails. So:

- **CK import jobs of this service** (`ImportModelJob.ImportCkAsync` / `ImportCkBatchAsync`) enter `ICkImportIndexUpgradeGate`
  first: they set a yield request for the tenant (renewed every 5 s, valid 60 s) and wait for the tenant lease
  (`octo:index-upgrade:tenant:<tenant>`). A running upgrade sees the request within `PollInterval`, stops before its next
  collection, ends `Deferred` and is `Scheduled` again (30 s); the import runs; then the upgrade continues with the
  collections left. After `ImportWaitTimeout` (default 30 min) the import runs anyway and may meet the 90 s lock wait of
  the engine (then its retries). The wait holds a worker of the default queue and, like every CK import job, does not see
  a shutdown. CK imports of one tenant through this service now run one after the other. While the upgrade cannot build
  (Window schedule with the window off, the production default) the gate does nothing.
- **The upgrade waits for a CK import**: while the tenant lease is held or a yield request is set, the upgrade does not
  start. When `index_update_lock` is held by someone else (an embedded import or `UpdateIndexesAsync` of another service), the
  engine's `TimeoutException` after 90 s makes the run `Scheduled` again after `RetryDelay`.
- **Remaining risk** (accepted, Odin 2026-11-18, option A): the **embedded** CK model imports of the other services at their
  tenant setup (asset repository, communication controller, …; only after a deploy that raises a model version) do not go
  through this gate. Meeting a running rebuild they time out after 90 s, and the service retries the tenant setup
  (`TenantSetupRetryTracker`, AB#7249, 30 s doubling to 5 min) until the rebuild of the current collection ended; the
  `TenantSetup` health check turns `Degraded` after 10 minutes. `StartDelay` (5 min) lets those imports finish before the
  after-deploy sweep starts. A fix in the engine (release `index_update_lock` between collections) is a story of its own under
  AB#8793.

### Restart during a job

The job takes Hangfire's own `IJobCancellationToken` (not `IBotCancellationToken`, whose token the other bot jobs never get):
on a graceful shutdown the engine stops before the next collection, the run is recorded `Interrupted`, and Hangfire requeues
the job. A killed pod leaves it `Processing` until Hangfire.Mongo's sliding invisibility timeout (5 min) requeues it; its
leases expire with the lock lifetime. The next run completes the collections left: a re-run skips upgraded collections and
completes interrupted ones, and the engine never drops an index before its replacement exists.

## Last-run record and logs

One document per tenant in the **system database**, collection `IndexUpgradeRuns` (`_id` = tenant id in lower case): job id,
trigger, status (`Running`, `Succeeded`, `NothingToDo`, `Deferred`, `Interrupted`, `Failed`), start, end, duration, legacy
indexes before and after, collections upgraded, error. Status only: nothing reads it to decide whether to build. The
status endpoint of AB#9301 reads it.

Structured logs (Dash0, service `octo-bot-services`):

- per collection: `Index upgrade of tenant '{TenantId}': collection '{Collection}' index '{LegacyIndexName}' {LegacyKeys}
  replaced by '{NewIndexName}' {NewKeys} ({DurationMs} ms)` - the duration is the time since the previous collection
  finished (precision `PollInterval`);
- per run: `Index upgrade of tenant '{TenantId}' ended {Status} after {DurationMs} ms ({Trigger}): {CollectionsUpgraded}
  collection(s) upgraded, legacy indexes {LegacyIndexesBefore} -> {LegacyIndexesAfter}`;
- per sweep: `Index upgrade sweep ({Trigger}) done: … checked, … with legacy indexes, … job(s) enqueued`.

## Configuration

Section `IndexUpgrade` (environment `OCTO_INDEXUPGRADE__*`):

| Key | Default | Meaning |
| --- | --- | --- |
| `Schedule` | `Window` | `Immediate` or `Window` |
| `Window:Enabled` | `false` | the window opens only when set |
| `Window:Cron` | `0 1 * * *` | when the window opens (Hangfire cron, UTC) |
| `Window:MaxDuration` | `04:00:00` | how long it stays open; no collection starts after it closed |
| `MaxConcurrent` | `1` | tenants rebuilt at the same time across all replicas |
| `StartDelay` | `00:05:00` | after-deploy sweep after the service start (Immediate); delay of a job for a created or attached tenant |
| `RetryDelay` | `00:05:00` | next try of a deferred run |
| `ImportWaitTimeout` | `00:30:00` | how long a CK import waits for the upgrade to stop |
| `PollInterval` | `00:00:05` | how often a running job checks yield requests and progress |

The code defaults are the production setting, so a cluster without values builds nothing.

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
2. **Go per cluster**: switching the window on for prod-1 or prod-2 needs the developer's go for that cluster.
3. **Switch on**: set `OCTO_INDEXUPGRADE__WINDOW__ENABLED=true` (and `…__WINDOW__CRON`, `…__WINDOW__MAXDURATION` if not the
   defaults) for the cluster and roll the bot service out. At the next cron time the window opens and the sweep enqueues
   every tenant with legacy indexes; they are rebuilt one after the other (`MaxConcurrent`). A tenant created or attached
   outside a window is picked up by the next window; a run that did not finish continues in the next window.
4. **Verify**: per tenant, no CK Ascending index without `_id` is left:
   `db.getCollectionNames().forEach(c => db[c].getIndexes().filter(i => !i.unique && Object.keys(i.key)[0] == "ckTypeId"
   && "rtState" in i.key && !("_id" in i.key)).forEach(i => print(c, i.name)))` prints nothing; the run record says
   `Succeeded` or `NothingToDo`; the Hangfire dashboard (`/ui/jobs`, queue `index-upgrade`) has no failed job.
5. **Switch off**: `OCTO_INDEXUPGRADE__WINDOW__ENABLED=false` and roll out; the recurring job `index-upgrade-window` is
   removed at the start, pending jobs end `Deferred` without a build. In an emergency delete the pending jobs of queue
   `index-upgrade` in the dashboard; a rebuild in progress finishes its current collection after the next restart.

## Tests

- Unit (`tests/Jobs.Tests/IndexUpgrade`): shape by keys incl. hashed names; deferral filter before `AutomaticRetry`; job:
  nothing to do, upgrade, re-run, global slot held, tenant lease held, `index_update_lock` timeout, engine failure, legacy
  left, CK import during the upgrade (stops before the next collection, import runs, upgrade continues), shutdown
  (`Interrupted`, re-run completes), window off / closed / closing during the run; scheduler: two replicas receive
  `PosCreateTenant` -> one job, window rules, enqueue failure does not fail the consumer; sweep; import gate; startup.
- Integration (`tests/Jobs.IntegrationTests/IndexUpgradeIntegrationTests`, Docker): real engine clean run, re-run and a run
  stopped after the build end in the same indexes; attach of a tenant with legacy indexes -> `PosCreateTenant` -> job on a real
  Hangfire.Mongo storage -> new keys; two Hangfire servers on one storage with two tenants due -> never two rebuilds, the
  waiting job `Scheduled`, never `Failed`; a server stopped during a job -> `Interrupted`, another server completes it.
