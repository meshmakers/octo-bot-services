---
description: Bus command consumers are registered shared (competing consumers) so any replica takes a command; delivery is at-least-once.
applies_to: src/BotServices/Consumers/**, src/BotServices/Program.cs
---

# Command consumers on every replica

## Command consumers run on every replica

The six bus commands of this service (`ImportCk`, `ImportCkBatch`, `ImportRt`, `ExportRtByQuery`, `ExportRtByDeepGraph`:
`ModelCommandsConsumer`; `RemoveRecurringJobsByScheduleGroup`: `RecurringJobConsumer`) are registered **shared**
(competing consumers) in `Consumers/BotCommandConsumers.cs`, called from `Program.cs`. With two or more replicas every
replica consumes them and each delivery goes to one of them; before they became shared they were classic
exclusive consumers (one replica consumed, the others waited). Delivery is **at-least-once**: after a lost acknowledgement the broker
delivers a command again, and `ModelCommandsConsumer` has no deduplication, so it then enqueues a second job.

- `CommandConsumerOptions { MigratesLegacyCommand = true }` is **required**: the senders (asset repository, communication
  controller, AI and MCP service; `AddCommandClient`) still send to the classic address. The consumers use the queues
  `…::shared` and serve the classic address only while no old exclusive replica is alive, so a rolling update starts
  without errors and loses or duplicates nothing. Rollback: stop the new replicas first (`octo-distributedEventHub/docs/DEVELOPER.md`,
  "Migrating exclusive command queues"). Needs the event hub fix (DistributionEventHub 3.5.65 or newer, one
  `LegacyCommandBridgeObserver` per migrated command): before it, only the first migrated command of a process served
  classic senders, and `BotCommandConsumerReplicaTests` fails.
- Safe under competing consumers: `ModelCommandsConsumer` only enqueues a Hangfire job in the shared MongoDB job storage;
  `RecurringJobConsumer` removes with the idempotent `RemoveIfExists` and takes `JobStorage` / `IRecurringJobManager` from
  DI (registered by `AddHangfire`, same storage as `JobStorage.Current`). The exclusive endpoints had no concurrency limit,
  so one replica already ran these commands concurrently.
- The deprecation warning ("classic EXCLUSIVE topology") is no longer logged at start. A new command consumer is
  added to `BotCommandConsumers` with the shared options and a comment why it is safe.
- Tests: `Jobs.IntegrationTests/BotCommandConsumerReplicaTests` (Docker, RabbitMQ Testcontainer): two replicas with the
  production registration and recording Hangfire fakes, 12 commands of each kind from classic senders, each handled by one
  replica, both replicas consume; no deprecation warning at start; rolling update old exclusive + new shared replica.
- Operations: queues `*::shared` and exchanges `*::send` are new names; broker policies or monitoring matching command
  queue names must include them.
