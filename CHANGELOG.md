# Changelog

## Unreleased

- AB#9326 New: a restore the engine refuses because a service-managed CK model of the dump (System, System.*) is not a
  genuine published one fails the restore job (REST restore job, `Restore-1` method, pre-sweep dump restore) with the
  engine's message as it is (`RESTORE_SYSTEM_MODEL_REFUSED`, model, version and reason; never model content) instead of
  the generic mongorestore command details (`JobFailedException.RestoreFailed`). The `Restore-1` method answers
  `RESTORE_FAILED` as for every failed restore. The check itself runs in engine-mongodb; this service needs that engine
  version to refuse anything. Docs: README "Restore: refused service-managed CK models", `docs/repository-methods.md`.
  Tests: `RestoreRepositoryJobTests`, `PreSweepDumpRestoreJobTests`.

- AB#9339 Fix: CK, CK batch and RT imports of a zip with a `.json` entry import that entry as JSON instead of failing
  with "No file to extract"; a `.yml` entry is accepted like `.yaml`, JSON wins when a zip has both, and a zip without a
  model entry fails with a message naming `.json`, `.yaml` and `.yml`. Also fixes AB#8572.

- AB#9118 Docs: `docs/index-upgrade.md` records the staging measurement of the index upgrade (2026-11-25: whole run about
  16 minutes, largest tenant 14 minutes for one collection of 885,240 documents, about 15-16 s per index per 100,000
  documents, slower MongoDB for every tenant of the cluster, no errors). The production rollout procedure now derives
  from it the window length, the quiet time, the memory and disk check, the chart values per cluster, the Dash0 check
  and a stop procedure during a window.
- AB#9709 New: the index upgrade reports its runs at the shipped log level, so Dash0 shows them without a log level
  change. Each sweep (after-deploy or window) logs `IndexUpgrade run started:` and `IndexUpgrade run ended: {Result}` with
  the tenant count, the jobs enqueued and the duration; each tenant job that ends without a deferral retry logs
  `IndexUpgrade tenant ended: {Result}` with status, duration and index counts. Result is `Completed`, `Incomplete` or
  `Failed`; a `Failed` run logs one end line per Hangfire attempt. The events are informational but logged at Warning
  (Error for `Failed`), because bot-services ships Warn and above only: alert rules that count its warnings should exclude
  these prefixes. The names of legacy indexes left moved from the Warning line to an Information line (the run record
  keeps them); the tenant end line of a run that ends `Succeeded` or `NothingToDo` is now Warning instead of Information.
  Details: `docs/index-upgrade.md`.
