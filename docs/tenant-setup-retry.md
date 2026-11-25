---
description: Tenant setup failures are retried in the background without MongoDB, with backoff, health check and metrics.
applies_to: src/BotServices/Services/DefaultConfigurationCreatorService.cs
---

# Failed tenant setup is retried

## Failed tenant setup is retried without MongoDB

On a staging cluster, 2026-10-21: during a MongoDB primary election the setup of two tenants failed, the log
said "this tenant will be retried in the background", but this service wired no retry store, so nothing was ever
retried and the tenants' adapters stayed offline ("Tenant not enabled") until a manual pod restart. The shared
`TenantSetupRetryTracker` (octo-common-services, in-memory, process-wide) now queues every failed `SetupAsync` and
the 30 s `FailedTenantRetryBackgroundService` re-runs it with exponential backoff (30 s doubling to 5 min, never
giving up). `DefaultConfigurationCreatorService` takes the DI instance (`tenantSetupRetryTracker`) and passes it as
`setupRetryTracker:` to `DefaultConfigurationCreatorServiceStandardized` and now also wires `FailedTenantRegistry` (a failed deferred start used to be logged as "No retry registry available" and dropped). Health check `TenantSetup` on `/health` turns
`Degraded` when a tenant fails longer than `TenantSetupRetry:DegradedAfter` (default 10 min); metrics
`octo.tenant_setup.failed` / `octo.tenant_setup.pending_seconds` (tag `octo.tenant.id`) feed the Dash0 alert. Details,
options and tests: `octo-common-services/docs/tenant-setup.md`, section "Tenant Setup - Failure Handling". Test here:
`tests/Jobs.Tests/Services/TenantSetupRetryWiringTests.cs`. Tests use one isolated `TenantSetupRetryTracker` per test and share it across the creator instances of that test when exercising a retry.
