using Hangfire;
using Hangfire.Server;
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
///     Order of the checks, each before anything is built: switched off (<c>MaxConcurrent</c> 0), build window (Window
///     schedule), a global slot
///     (<see cref="IndexUpgradeOptions.MaxConcurrent" /> across all replicas), no CK import of the tenant asking to go
///     first, the tenant lease, the tenant context, the legacy indexes by their keys (none: done), their category by the
///     tenant's CK metadata (none of an Available model's collection root: done, with a warning, AB#9322). Then the engine
///     upgrades collection by collection under the tenant's <c>index_update_lock</c>; the job's token stops it before the
///     next collection when the service shuts down, the window closes or a CK import of the tenant asks to go first.
/// </remarks>
public class IndexUpgradeJob(
    ILogger<IndexUpgradeJob> logger,
    ISystemContext systemContext,
    ITenantIndexInspector inspector,
    ICkCollectionRootReader collectionRoots,
    IIndexUpgradeCoordination coordination,
    IIndexUpgradeRunStore runStore,
    IOptions<IndexUpgradeOptions> options,
    TimeProvider? timeProvider = null) : IIndexUpgradeJob
{
    /// <summary>How soon a run that gave way to a CK import tries again.</summary>
    internal static readonly TimeSpan AfterYieldDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Upper bound of a status write and of each index read after the engine call. The three steps after a shutdown
    ///     (final progress read, remaining read, status write) stay below Hangfire's shutdown timeout of 15 s, so a stalled
    ///     MongoDB never keeps the leases or lets the host stop before the run is recorded <c>Interrupted</c>.
    /// </summary>
    internal static readonly TimeSpan BookkeepingTimeout = TimeSpan.FromSeconds(4);

    private const string IndexUpdateLockName = "index_update_lock";
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task UpgradeTenantAsync(string tenantId, string trigger, IJobCancellationToken? cancellationToken,
        PerformContext? context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        var settings = options.Value;
        var shutdown = cancellationToken?.ShutdownToken ?? CancellationToken.None;
        var clock = Stopwatch.StartNew();
        var record = new IndexUpgradeRunRecord
        {
            TenantId = tenantId,
            JobId = context?.BackgroundJob?.Id ?? TryGetJobId(tenantId),
            Trigger = trigger,
            Status = IndexUpgradeRunStatus.Running,
            StartedAt = Now()
        };

        if (settings.MaxConcurrent <= 0)
        {
            // Switched off: the job ends (no retry loop); the next sweep after switching on enqueues the tenant again.
            await EndAsync(record, clock, IndexUpgradeRunStatus.Deferred,
                "The upgrade is switched off (IndexUpgrade:MaxConcurrent = 0).", null);
            return;
        }

        DateTime? windowClosesAt = null;
        if (settings.Schedule != IndexUpgradeSchedule.Immediate)
        {
            windowClosesAt = settings.Window.Enabled ? coordination.GetWindowClosesAt() : null;
            if (windowClosesAt == null || Now() >= windowClosesAt)
            {
                // Nothing was inspected: the number of legacy indexes is unknown, not zero.
                await EndAsync(record, clock, IndexUpgradeRunStatus.Deferred,
                    settings.Window.Enabled
                        ? "The build window is closed; the tenant is picked up by the next window."
                        : "The build window is switched off (IndexUpgrade:Window:Enabled = false).", null);
                return;
            }
        }

        // Leases before the tenant is resolved or inspected: a tenant that waits for a slot costs no resolve and no
        // index listing per retry, and nothing of this run touches the tenant's indexes outside the limit.
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

        var tenantContext = await FindTenantContextAsync(tenantId);
        if (tenantContext == null)
        {
            logger.LogInformation("Index upgrade of tenant '{TenantId}' skipped: the tenant does not exist (any more)",
                tenantId);
            return;
        }

        var before = await inspector.FindLegacyIndexesAsync(tenantContext.DatabaseName, null, shutdown);
        record.LegacyIndexesBefore = before.Count;
        if (before.Count == 0)
        {
            await EndAsync(record, clock, IndexUpgradeRunStatus.NothingToDo, null, 0);
            return;
        }

        // AB#9322: the engine upgrades only collection roots of Available models. When nothing is for the engine, the
        // run ends without calling it (and without taking the tenant's index_update_lock); the leftovers are named.
        // If the CK metadata cannot be read, the engine runs as before and the classification after it decides.
        var beforeClassification = await TryClassifyAsync(tenantContext.DatabaseName, before, shutdown);
        if (beforeClassification is { HasManaged: false })
        {
            record.Leftovers = beforeClassification.ToRecord();
            record.Warning = beforeClassification.Warning();
            await EndAsync(record, clock, IndexUpgradeRunStatus.Succeeded, null, before.Count, LogLevel.Warning);
            return;
        }

        logger.LogInformation(
            "Index upgrade of tenant '{TenantId}' started ({Trigger}): {LegacyIndexCount} legacy index(es) in " +
            "{CollectionCount} collection(s) of database '{DatabaseName}'",
            tenantId, trigger, before.Count, before.Select(i => i.CollectionName).Distinct().Count(),
            tenantContext.DatabaseName);
        await SaveAsync(record);

        // The window may have closed during the inspection and the record write; a timer of zero would cancel only
        // asynchronously, after the engine may have started a collection.
        if (windowClosesAt != null && Now() >= windowClosesAt)
        {
            await EndAsync(record, clock, IndexUpgradeRunStatus.Deferred,
                "The build window closed; the rest follows in the next window.", before.Count);
            return;
        }

        // The progress log follows what the engine works on; leftovers it never touches would stay "pending".
        var progress = new IndexUpgradeProgress(inspector, logger, tenantId, tenantContext.DatabaseName,
            beforeClassification?.Managed ?? before, _time);
        // The window has its own token: whether it closed is read from the timer, not compared with the clock, so a timer
        // that fires a few milliseconds early still counts as the window closing.
        var untilClose = windowClosesAt == null ? Timeout.InfiniteTimeSpan : windowClosesAt.Value - Now();
        using var windowTimer = new CancellationTokenSource(
            untilClose == Timeout.InfiniteTimeSpan || untilClose > TimeSpan.Zero ? untilClose : TimeSpan.Zero, _time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown, windowTimer.Token);

        var yielded = 0;
        using var watcherStop = new CancellationTokenSource();
        // Two watchers: a slow index listing of the progress log never delays the reaction to a yield request.
        var yieldWatcher = WatchYieldAsync(tenantId, settings.PollInterval, () =>
        {
            Interlocked.Exchange(ref yielded, 1);
            // ReSharper disable once AccessToDisposedClosure - the watcher is joined before stop is disposed
            stop.Cancel();
        }, watcherStop.Token);
        var progressWatcher = WatchProgressAsync(progress, settings.PollInterval, watcherStop.Token);

        Exception? failure = null;
        var windowClosed = false;
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
            // Read when the engine call ended: a timer that fires during the reads below does not turn another failure
            // into a window close.
            windowClosed = windowTimer.IsCancellationRequested;
            await watcherStop.CancelAsync();
            await Task.WhenAll(yieldWatcher, progressWatcher);
        }

        // One final observation for the progress log (skipped on shutdown, to keep the bookkeeping short), then the
        // count and the names of what is left.
        if (!shutdown.IsCancellationRequested)
        {
            using var bounded = new CancellationTokenSource(BookkeepingTimeout);
            await progress.CheckAsync(bounded.Token);
        }

        // If the final read fails, what is left stays unknown (null); the run is then not reported as a success.
        var left = await RemainingAsync(tenantContext.DatabaseName, before);
        var leftCount = left?.Count;
        record.CollectionsUpgraded = progress.Done.ToList();

        if (failure is OperationCanceledException)
        {
            if (shutdown.IsCancellationRequested)
            {
                await EndAsync(record, clock, IndexUpgradeRunStatus.Interrupted,
                    "The service stopped; Hangfire runs the job again.", leftCount);
                ExceptionDispatchInfo.Capture(failure).Throw();
                return;
            }

            if (Volatile.Read(ref yielded) == 1)
            {
                await DeferAsync(record, clock, leftCount, "Gave way to a CK import of the tenant", AfterYieldDelay);
            }

            if (windowClosed && leftCount != 0)
            {
                await EndAsync(record, clock, IndexUpgradeRunStatus.Deferred,
                    "The build window closed; the rest follows in the next window.", leftCount);
                return;
            }
        }

        if (failure is TimeoutException timeout && timeout.Message.Contains(IndexUpdateLockName))
        {
            // The engine's RepositoryDistributedLockService throws a plain TimeoutException naming the lock; there is no
            // typed signal to match.
            await DeferAsync(record, clock, leftCount,
                "The tenant's index_update_lock is held (CK import or index update)", settings.RetryDelay);
        }

        if (failure is OperationCanceledException && windowClosed && leftCount == 0)
        {
            // The window closed while the engine finished the last collection: nothing is left, the run succeeded.
            failure = null;
        }

        if (failure != null)
        {
            await EndAsync(record, clock, IndexUpgradeRunStatus.Failed, failure.Message, leftCount);
            ExceptionDispatchInfo.Capture(failure).Throw();
            return;
        }

        if (left == null)
        {
            // The engine returned normally, but what is left could not be read: not verified, so not a success. The next
            // sweep looks again.
            await EndAsync(record, clock, IndexUpgradeRunStatus.Incomplete,
                "The engine finished, but the legacy indexes left could not be read; the result is not verified.", null,
                LogLevel.Warning);
            return;
        }

        if (left.Count == 0)
        {
            record.Leftovers = new IndexUpgradeLeftovers();
            await EndAsync(record, clock, IndexUpgradeRunStatus.Succeeded, null, 0);
            return;
        }

        // AB#9322: classify what is left by the CK metadata read now (a model may have changed state during the run).
        var leftClassification = await TryClassifyAsync(tenantContext.DatabaseName, left, CancellationToken.None,
            BookkeepingTimeout);
        if (leftClassification == null)
        {
            await EndAsync(record, clock, IndexUpgradeRunStatus.Incomplete,
                $"{left.Count} legacy index(es) are left, and the tenant's CK metadata could not be read to tell why: " +
                IndexNames(left), leftCount, LogLevel.Warning);
            return;
        }

        record.Leftovers = leftClassification.ToRecord();
        record.Warning = leftClassification.Warning();
        if (leftClassification.HasManaged)
        {
            // The engine logs (does not throw) an index it cannot build; a retry of this job changes nothing, the next
            // sweep looks again.
            var message = $"{leftClassification.Managed.Count} legacy index(es) of collections the engine manages " +
                          "(collection roots of Available models) are left; the index build failed - see the engine's " +
                          "warnings: " + IndexNames(leftClassification.Managed);
            await EndAsync(record, clock, IndexUpgradeRunStatus.Incomplete, message, leftCount, LogLevel.Warning);
            return;
        }

        // Only leftovers no run can change: a success, never a silent one (warning in the record and the log).
        await EndAsync(record, clock, IndexUpgradeRunStatus.Succeeded, null, leftCount, LogLevel.Warning);
    }

    private static string IndexNames(IEnumerable<LegacyIndex> indexes) =>
        string.Join(", ", indexes.Select(i => $"{i.CollectionName}.{i.IndexName}"));

    /// <summary>Classifies <paramref name="legacy" /> by the tenant's CK metadata; null when it cannot be read.</summary>
    private async Task<LegacyIndexClassification?> TryClassifyAsync(string databaseName,
        IReadOnlyList<LegacyIndex> legacy, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != null)
            {
                bounded.CancelAfter(timeout.Value);
            }

            return await LegacyIndexClassification.ClassifyAsync(collectionRoots, databaseName, legacy, bounded.Token);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Could not read the collection roots of database '{DatabaseName}'", databaseName);
            return null;
        }
    }

    private async Task WatchYieldAsync(string tenantId, TimeSpan interval, Action yield,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, _time, cancellationToken);
                // The storage read is synchronous and cannot be cancelled: it runs aside and is abandoned when the run
                // ends (it only reads), so a stalled read never delays the end of the run.
                var requested = await Task.Run(() => SafeIsYieldRequested(tenantId), CancellationToken.None)
                    .WaitAsync(cancellationToken);
                if (requested)
                {
                    logger.LogInformation(
                        "Index upgrade of tenant '{TenantId}': a CK import asks to go first; stopping before the next collection",
                        tenantId);
                    yield();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended.
        }
    }

    private async Task WatchProgressAsync(IndexUpgradeProgress progress, TimeSpan interval,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, _time, cancellationToken);
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

    /// <summary>The legacy indexes left in the collections of the run; null when they cannot be read.</summary>
    private async Task<IReadOnlyList<LegacyIndex>?> RemainingAsync(string databaseName, IReadOnlyList<LegacyIndex> before)
    {
        try
        {
            using var bounded = new CancellationTokenSource(BookkeepingTimeout);
            var collections = before.Select(i => i.CollectionName).Distinct().ToList();
            return await inspector.FindLegacyIndexesAsync(databaseName, collections, bounded.Token);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the legacy indexes left in database '{DatabaseName}'", databaseName);
            return null;
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

        var logLevel = level ?? (status == IndexUpgradeRunStatus.Failed ? LogLevel.Error : LogLevel.Information);
        if (record.Leftovers is { } leftovers)
        {
            // AB#9322: the same counts as the record; "model not Available" only names that category.
            logger.Log(logLevel,
                "Index upgrade of tenant '{TenantId}' ended {Status} after {DurationMs} ms ({Trigger}): " +
                "{CollectionsUpgraded} collection(s) upgraded, legacy indexes {LegacyIndexesBefore} -> {LegacyIndexesAfter} " +
                "(managed {ManagedIndexes}, model not Available {ModelNotAvailableIndexes}, orphaned {OrphanedIndexes})" +
                "{Reason}{Warning}",
                record.TenantId, status, record.DurationMs, record.Trigger, record.CollectionsUpgraded.Count,
                record.LegacyIndexesBefore, legacyLeft, leftovers.ManagedIndexes, leftovers.ModelNotAvailableIndexes,
                leftovers.OrphanedIndexes, error == null ? "" : ": " + error,
                record.Warning == null ? "" : ". Warning: " + record.Warning);
            return;
        }

        logger.Log(logLevel,
            "Index upgrade of tenant '{TenantId}' ended {Status} after {DurationMs} ms ({Trigger}): " +
            "{CollectionsUpgraded} collection(s) upgraded, legacy indexes {LegacyIndexesBefore} -> {LegacyIndexesAfter}" +
            "{Reason}",
            record.TenantId, status, record.DurationMs, record.Trigger, record.CollectionsUpgraded.Count,
            record.LegacyIndexesBefore, legacyLeft, error == null ? "" : ": " + error);
    }

    private async Task SaveAsync(IndexUpgradeRunRecord record)
    {
        try
        {
            using var bounded = new CancellationTokenSource(BookkeepingTimeout);
            await runStore.SaveAsync(record, bounded.Token);
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
