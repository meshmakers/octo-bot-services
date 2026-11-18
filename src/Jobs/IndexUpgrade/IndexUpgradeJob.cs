using Hangfire;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     Hangfire job of the paging index upgrade of one tenant (AB#9118). See <see cref="IIndexUpgradeJob" /> and
///     <c>docs/index-upgrade.md</c>.
/// </summary>
/// <remarks>
///     Order of the checks, each before anything is built: build window (Window schedule), legacy indexes by their keys
///     (none: done), a global slot (<see cref="IndexUpgradeOptions.MaxConcurrent" /> across all replicas), no CK import
///     of the tenant asking to go first, the tenant lease. Then the engine upgrades collection by collection under the
///     tenant's <c>index_update_lock</c>; the job's token stops it before the next collection when the service shuts
///     down, the window closes or a CK import of the tenant asks to go first.
/// </remarks>
public class IndexUpgradeJob(
    ILogger<IndexUpgradeJob> logger,
    ISystemContext systemContext,
    ITenantIndexInspector inspector,
    IIndexUpgradeCoordination coordination,
    IIndexUpgradeRunStore runStore,
    IOptions<IndexUpgradeOptions> options,
    TimeProvider? timeProvider = null) : IIndexUpgradeJob
{
    /// <summary>How soon a run that gave way to a CK import tries again.</summary>
    internal static readonly TimeSpan AfterYieldDelay = TimeSpan.FromSeconds(30);

    private const string IndexUpdateLockName = "index_update_lock";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task UpgradeTenantAsync(string tenantId, string trigger, IJobCancellationToken? cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var settings = options.Value;
        var shutdown = cancellationToken?.ShutdownToken ?? CancellationToken.None;
        var clock = Stopwatch.StartNew();
        var record = new IndexUpgradeRunRecord
        {
            TenantId = tenantId,
            JobId = TryGetJobId(tenantId),
            Trigger = trigger,
            Status = IndexUpgradeRunStatus.Running,
            StartedAt = Now()
        };

        DateTime? windowClosesAt = null;
        if (settings.Schedule == IndexUpgradeSchedule.Window)
        {
            windowClosesAt = settings.Window.Enabled ? coordination.GetWindowClosesAt() : null;
            if (windowClosesAt == null || Now() >= windowClosesAt)
            {
                await EndAsync(record, clock, IndexUpgradeRunStatus.Deferred,
                    settings.Window.Enabled
                        ? "The build window is closed; the tenant is picked up by the next window."
                        : "The build window is switched off (IndexUpgrade:Window:Enabled = false).", 0);
                return;
            }
        }

        var tenantContext = await FindTenantContextAsync(tenantId);
        if (tenantContext == null)
        {
            logger.LogInformation("Index upgrade of tenant '{TenantId}' skipped: the tenant does not exist (any more)",
                tenantId);
            return;
        }

        // Leases first, inspection after: a tenant that waits for a slot costs no index listing per retry.
        using var slot = coordination.TryAcquireGlobalSlot(settings.MaxConcurrent);
        if (slot == null)
        {
            await DeferAsync(record, clock, null,
                $"{settings.MaxConcurrent} tenant(s) are being rebuilt already (IndexUpgrade:MaxConcurrent)",
                settings.RetryDelay, LogLevel.Debug);
        }

        if (coordination.IsYieldRequested(tenantId))
        {
            await DeferAsync(record, clock, null, "A CK import of the tenant is waiting", settings.RetryDelay,
                LogLevel.Debug);
        }

        using var tenantLease = coordination.TryAcquireTenant(tenantId, TimeSpan.Zero);
        if (tenantLease == null)
        {
            await DeferAsync(record, clock, null, "A CK import of the tenant is running", settings.RetryDelay,
                LogLevel.Debug);
        }

        var before = await inspector.FindLegacyIndexesAsync(tenantContext.DatabaseName, null, shutdown);
        record.LegacyIndexesBefore = before.Count;
        if (before.Count == 0)
        {
            await EndAsync(record, clock, IndexUpgradeRunStatus.NothingToDo, null, 0);
            return;
        }

        logger.LogInformation(
            "Index upgrade of tenant '{TenantId}' started ({Trigger}): {LegacyIndexCount} legacy index(es) in " +
            "{CollectionCount} collection(s) of database '{DatabaseName}'",
            tenantId, trigger, before.Count, before.Select(i => i.CollectionName).Distinct().Count(),
            tenantContext.DatabaseName);
        await SaveAsync(record);

        var progress = new IndexUpgradeProgress(inspector, logger, tenantId, tenantContext.DatabaseName, before);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (windowClosesAt != null)
        {
            var left = windowClosesAt.Value - Now();
            stop.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }

        var yielded = false;
        using var watcherStop = new CancellationTokenSource();
        var watcher = WatchAsync(tenantId, progress, settings.PollInterval, () =>
        {
            yielded = true;
            // ReSharper disable once AccessToDisposedClosure - the watcher ends before stop is disposed
            stop.Cancel();
        }, watcherStop.Token);

        Exception? failure = null;
        try
        {
            using var adminSession = await tenantContext.GetAdminSessionAsync();
            await tenantContext.UpgradeTiebreakIndexesAsync(adminSession, stop.Token);
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            await watcherStop.CancelAsync();
            await watcher;
        }

        await progress.CheckAsync(CancellationToken.None);
        var after = await RemainingAsync(tenantContext.DatabaseName, before, progress);
        record.CollectionsUpgraded = progress.Done.ToList();

        if (failure is OperationCanceledException)
        {
            if (shutdown.IsCancellationRequested)
            {
                await EndAsync(record, clock, IndexUpgradeRunStatus.Interrupted,
                    "The service stopped; Hangfire runs the job again.", after);
                ExceptionDispatchInfo.Capture(failure).Throw();
                return;
            }

            if (yielded)
            {
                await DeferAsync(record, clock, after, "Gave way to a CK import of the tenant", AfterYieldDelay);
            }

            if (windowClosesAt != null && Now() >= windowClosesAt)
            {
                await EndAsync(record, clock, IndexUpgradeRunStatus.Deferred,
                    "The build window closed; the rest follows in the next window.", after);
                return;
            }
        }

        if (failure is TimeoutException timeout && timeout.Message.Contains(IndexUpdateLockName))
        {
            await DeferAsync(record, clock, after,
                "The tenant's index_update_lock is held (CK import or index update)", settings.RetryDelay);
        }

        if (failure != null)
        {
            await EndAsync(record, clock, IndexUpgradeRunStatus.Failed, failure.Message, after);
            ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }

        if (after > 0)
        {
            var message = $"{after} legacy index(es) are left after the upgrade: " +
                          string.Join(", ", progress.Remaining.Select(i => $"{i.CollectionName}.{i.IndexName}"));
            await EndAsync(record, clock, IndexUpgradeRunStatus.Failed, message, after);
            throw new InvalidOperationException($"Index upgrade of tenant '{tenantId}': {message}");
        }

        await EndAsync(record, clock, IndexUpgradeRunStatus.Succeeded, null, 0);
    }

    private async Task WatchAsync(string tenantId, IndexUpgradeProgress progress, TimeSpan interval, Action yield,
        CancellationToken cancellationToken)
    {
        var asked = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, _time, cancellationToken);
                if (!asked && SafeIsYieldRequested(tenantId))
                {
                    asked = true;
                    logger.LogInformation(
                        "Index upgrade of tenant '{TenantId}': a CK import asks to go first; stopping before the next collection",
                        tenantId);
                    yield();
                }

                await progress.CheckAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended.
        }
    }

    private bool SafeIsYieldRequested(string tenantId)
    {
        try
        {
            return coordination.IsYieldRequested(tenantId);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Index upgrade of tenant '{TenantId}': could not read the yield request", tenantId);
            return false;
        }
    }

    private async Task<int> RemainingAsync(string databaseName, IReadOnlyList<LegacyIndex> before,
        IndexUpgradeProgress progress)
    {
        try
        {
            var collections = before.Select(i => i.CollectionName).Distinct().ToList();
            return (await inspector.FindLegacyIndexesAsync(databaseName, collections, CancellationToken.None)).Count;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not count the legacy indexes left in database '{DatabaseName}'", databaseName);
            return progress.Remaining.Count;
        }
    }

    private async Task<ITenantContext?> FindTenantContextAsync(string tenantId)
    {
        if (string.Equals(tenantId, systemContext.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            return systemContext;
        }

        return await systemContext.TryFindTenantContextAsync(tenantId);
    }

    private async Task DeferAsync(IndexUpgradeRunRecord record, Stopwatch clock, int? legacyLeft, string reason,
        TimeSpan delay, LogLevel level = LogLevel.Information)
    {
        await EndAsync(record, clock, IndexUpgradeRunStatus.Deferred, reason, legacyLeft, level);
        throw new IndexUpgradeDeferredException(
            $"Index upgrade of tenant '{record.TenantId}' deferred: {reason}; next try in {delay}", delay);
    }

    private async Task EndAsync(IndexUpgradeRunRecord record, Stopwatch clock, IndexUpgradeRunStatus status,
        string? error, int? legacyLeft, LogLevel? level = null)
    {
        record.Status = status;
        record.Error = error;
        record.EndedAt = Now();
        record.DurationMs = (long)clock.Elapsed.TotalMilliseconds;
        record.LegacyIndexesAfter = legacyLeft;
        await SaveAsync(record);

        logger.Log(level ?? (status == IndexUpgradeRunStatus.Failed ? LogLevel.Error : LogLevel.Information),
            "Index upgrade of tenant '{TenantId}' ended {Status} after {DurationMs} ms ({Trigger}): " +
            "{CollectionsUpgraded} collection(s) upgraded, legacy indexes {LegacyIndexesBefore} -> {LegacyIndexesAfter}" +
            "{Reason}",
            record.TenantId, status, record.DurationMs, record.Trigger, record.CollectionsUpgraded.Count,
            record.LegacyIndexesBefore, legacyLeft?.ToString() ?? "?", error == null ? "" : ": " + error);
    }

    private async Task SaveAsync(IndexUpgradeRunRecord record)
    {
        try
        {
            await runStore.SaveAsync(record, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Status only: a lost record must not fail or stop the upgrade.
            logger.LogWarning(e, "Could not write the index upgrade record of tenant '{TenantId}'", record.TenantId);
        }
    }

    private string? TryGetJobId(string tenantId)
    {
        try
        {
            return coordination.GetJobId(tenantId);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Could not read the job id of the index upgrade of tenant '{TenantId}'", tenantId);
            return null;
        }
    }

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;
}
