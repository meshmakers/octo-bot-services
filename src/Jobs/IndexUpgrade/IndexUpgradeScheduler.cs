using Hangfire;
using Hangfire.Server;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>Enqueues the index upgrade jobs (AB#9118).</summary>
public interface IIndexUpgradeScheduler
{
    /// <summary>
    ///     Enqueues the job of <paramref name="tenantId" /> (after <paramref name="delay" />, if given) unless it already
    ///     has one that is enqueued, scheduled or running (on any replica). Returns the job id and whether it is new.
    /// </summary>
    (string JobId, bool Created) Enqueue(string tenantId, string trigger, TimeSpan? delay = null);

    /// <summary>
    ///     A tenant was created or attached (<c>PosCreateTenant</c>, received by every replica; a restore and a copy
    ///     attach as well): enqueues its job when the schedule builds now (Immediate, or Window while a window is
    ///     open), after <see cref="IndexUpgradeOptions.StartDelay" />: the services set the tenant up first, and their
    ///     embedded CK imports need the index_update_lock of the tenant. Otherwise the next window picks the tenant up.
    ///     Never throws.
    /// </summary>
    void OnTenantCreatedOrAttached(string tenantId);
}

/// <inheritdoc />
public class IndexUpgradeScheduler(
    IBackgroundJobClient backgroundJobClient,
    IIndexUpgradeCoordination coordination,
    IOptions<IndexUpgradeOptions> options,
    ILogger<IndexUpgradeScheduler> logger,
    TimeProvider? timeProvider = null) : IIndexUpgradeScheduler
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public (string JobId, bool Created) Enqueue(string tenantId, string trigger, TimeSpan? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var result = coordination.EnqueueOnce(tenantId,
            () => delay > TimeSpan.Zero
                ? backgroundJobClient.Schedule<IIndexUpgradeJob>(
                    job => job.UpgradeTenantAsync(tenantId, trigger, JobCancellationToken.Null, null), delay.Value)
                : backgroundJobClient.Enqueue<IIndexUpgradeJob>(job =>
                    job.UpgradeTenantAsync(tenantId, trigger, JobCancellationToken.Null, null)));
        if (result.Created)
        {
            logger.LogInformation("Index upgrade of tenant '{TenantId}' enqueued as job {JobId} ({Trigger})", tenantId,
                result.JobId, trigger);
        }
        else
        {
            logger.LogDebug("Index upgrade of tenant '{TenantId}' already has job {JobId}; nothing enqueued ({Trigger})",
                tenantId, result.JobId, trigger);
        }

        return result;
    }

    /// <inheritdoc />
    public void OnTenantCreatedOrAttached(string tenantId)
    {
        try
        {
            var settings = options.Value;
            if (!settings.CanBuild)
            {
                logger.LogInformation(
                    "Tenant '{TenantId}' created or attached: the index upgrade is switched off", tenantId);
                return;
            }

            if (settings.Schedule != IndexUpgradeSchedule.Immediate &&
                !(coordination.GetWindowClosesAt() is { } closesAt && closesAt > _time.GetUtcNow().UtcDateTime))
            {
                logger.LogInformation(
                    "Tenant '{TenantId}' created or attached: its index upgrade waits for the next build window",
                    tenantId);
                return;
            }

            Enqueue(tenantId, IndexUpgradeConstants.TriggerTenantCreated, settings.StartDelay);
        }
        catch (Exception e)
        {
            // The tenant lifecycle must never fail because of the upgrade; the next sweep picks the tenant up.
            logger.LogWarning(e, "Could not enqueue the index upgrade of tenant '{TenantId}'", tenantId);
        }
    }
}

/// <summary>
///     Looks at every tenant and enqueues the index upgrade of each one that still has a legacy index (AB#9118): once
///     after the service start (Immediate) and when the build window opens (Window). Every replica may run it; the
///     per-tenant dedupe makes that harmless. Tenants already in the new shape cost one <c>listIndexes</c> per
///     collection, no job and no index build.
/// </summary>
public interface IIndexUpgradeSweepJob
{
    /// <summary>
    ///     Runs the sweep. A tenant whose index list could not be read fails the run after every other tenant was
    ///     checked, so Hangfire retries it (tenants already enqueued are not enqueued twice).
    /// </summary>
    /// <param name="trigger"><see cref="IndexUpgradeConstants.TriggerAfterDeploy" /> or <see cref="IndexUpgradeConstants.TriggerWindow" /></param>
    /// <param name="cancellationToken">Hangfire's shutdown token</param>
    /// <param name="context">
    ///     Hangfire's perform context (enqueue <c>null</c>): the window opens at the creation time of this job, the
    ///     occurrence of the recurring job, so a retry of the sweep never opens a later window.
    /// </param>
    [JobDisplayName("Looking for tenants whose paging indexes need the upgrade ({0})")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 900], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    Task RunAsync(string trigger, IJobCancellationToken? cancellationToken, PerformContext? context);
}

/// <inheritdoc />
public class IndexUpgradeSweepJob(
    ISystemContext systemContext,
    ITenantIndexInspector inspector,
    IIndexUpgradeScheduler scheduler,
    IIndexUpgradeCoordination coordination,
    IOptions<IndexUpgradeOptions> options,
    ILogger<IndexUpgradeSweepJob> logger,
    TimeProvider? timeProvider = null) : IIndexUpgradeSweepJob
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task RunAsync(string trigger, IJobCancellationToken? cancellationToken, PerformContext? context)
    {
        var settings = options.Value;
        var token = cancellationToken?.ShutdownToken ?? CancellationToken.None;
        if (!settings.CanBuild)
        {
            logger.LogInformation(
                "Index upgrade sweep ({Trigger}) skipped: the upgrade builds nothing (Schedule {Schedule}, " +
                "Window:Enabled {WindowEnabled}, MaxConcurrent {MaxConcurrent})",
                trigger, settings.Schedule, settings.Window.Enabled, settings.MaxConcurrent);
            return;
        }

        if (settings.Schedule != IndexUpgradeSchedule.Immediate && trigger == IndexUpgradeConstants.TriggerWindow)
        {
            // The window belongs to the occurrence of the recurring job (the creation time of this job), not to the time
            // of this attempt: a retry inside the window keeps its end, a retry after it opens nothing.
            var now = _time.GetUtcNow().UtcDateTime;
            var opensAt = context?.BackgroundJob?.CreatedAt ?? now;
            var closesAt = opensAt + settings.Window.MaxDuration;
            if (closesAt <= now)
            {
                logger.LogInformation(
                    "Index upgrade sweep ({Trigger}) skipped: its window ({OpensAt:O} - {ClosesAt:O}) is over",
                    trigger, opensAt, closesAt);
                return;
            }

            if (coordination.GetWindowClosesAt() is { } open && open >= closesAt)
            {
                logger.LogInformation("Index upgrade build window is open until {ClosesAt:O}", open);
            }
            else
            {
                coordination.OpenWindow(closesAt);
                logger.LogInformation("Index upgrade build window opened until {ClosesAt:O}", closesAt);
            }
        }

        if (!await systemContext.IsSystemTenantExistingAsync())
        {
            logger.LogInformation("Index upgrade sweep ({Trigger}) skipped: no system tenant yet", trigger);
            return;
        }

        var tenants = new List<(string TenantId, string DatabaseName)>
        {
            (systemContext.TenantId, systemContext.DatabaseName)
        };
        using (var adminSession = await systemContext.GetAdminSessionAsync())
        {
            var resultSet = await systemContext.GetAllTenantsAsync(adminSession);
            tenants.AddRange(resultSet.Items.Select(t => (t.TenantId, t.DatabaseName)));
        }

        int due = 0, enqueued = 0, failed = 0;
        foreach (var (tenantId, databaseName) in tenants
                     .Where(t => !string.IsNullOrWhiteSpace(t.TenantId) && !string.IsNullOrWhiteSpace(t.DatabaseName))
                     .DistinctBy(t => t.TenantId, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var legacy = await inspector.FindLegacyIndexesAsync(databaseName, null, token);
                if (legacy.Count == 0)
                {
                    continue;
                }

                due++;
                if (scheduler.Enqueue(tenantId, trigger).Created)
                {
                    enqueued++;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failed++;
                logger.LogWarning(e, "Index upgrade sweep ({Trigger}): could not check tenant '{TenantId}'", trigger,
                    tenantId);
            }
        }

        logger.LogInformation(
            "Index upgrade sweep ({Trigger}) done: {TenantCount} tenant(s) checked, {DueCount} with legacy indexes, " +
            "{EnqueuedCount} job(s) enqueued, {FailedCount} not checked",
            trigger, tenants.Count, due, enqueued, failed);
        if (failed > 0)
        {
            throw new InvalidOperationException(
                $"Index upgrade sweep ({trigger}): {failed} tenant(s) could not be checked; the sweep is retried.");
        }
    }
}
