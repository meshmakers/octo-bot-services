using System.Globalization;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs.Services;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     <see cref="IIndexUpgradeCoordination" /> on the Hangfire job storage (the bot's job database, shared by all
///     replicas): distributed locks for the global slots and the tenant lease (<see cref="HangfireDistributedLockOwner" />,
///     kept alive by the storage's heartbeat and expiring after a process was killed), hashes for the rest.
/// </summary>
/// <remarks>
///     The global limit is an own lease and not Hangfire's <c>DisableConcurrentExecution</c>: that attribute waits for
///     its lock up to a timeout and then fails the job (<c>DistributedLockTimeoutException</c>), so a rebuild of a large
///     tenant that runs for hours would turn every waiting tenant into a failed job, retry after retry. Here a job that
///     finds every slot held ends <c>Scheduled</c> again (<see cref="IndexUpgradeDeferredException" />) and never holds
///     a worker while it waits.
/// </remarks>
public class HangfireIndexUpgradeCoordination : IIndexUpgradeCoordination
{
    internal const string SlotResourcePrefix = "octo:index-upgrade:slot:";
    internal const string TenantResourcePrefix = "octo:index-upgrade:tenant:";
    internal const string EnqueueResourcePrefix = "octo:index-upgrade:enqueue:";
    internal const string JobHashPrefix = "octo:index-upgrade:job:";
    internal const string YieldHashPrefix = "octo:index-upgrade:yield:";
    internal const string WindowHash = "octo:index-upgrade:window";
    internal static readonly TimeSpan EnqueueLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long taking a free slot may take. Not zero: the storage's lock must get at least one attempt; a held slot
    ///     costs the caller this time once per slot.
    /// </summary>
    internal static readonly TimeSpan SlotAttemptTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly HashSet<string> ActiveStates =
    [
        EnqueuedState.StateName, ScheduledState.StateName, ProcessingState.StateName, AwaitingState.StateName
    ];

    private readonly Func<JobStorage> _storage;
    private readonly TimeProvider _time;

    /// <summary>Constructor using <see cref="JobStorage.Current" />.</summary>
    public HangfireIndexUpgradeCoordination() : this(() => JobStorage.Current)
    {
    }

    /// <summary>Constructor.</summary>
    /// <param name="storage">Resolves the job storage</param>
    /// <param name="timeProvider">Clock (tests)</param>
    public HangfireIndexUpgradeCoordination(Func<JobStorage> storage, TimeProvider? timeProvider = null)
    {
        _storage = storage;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IDisposable? TryAcquireGlobalSlot(int maxConcurrent)
    {
        for (var slot = 0; slot < Math.Max(1, maxConcurrent); slot++)
        {
            var held = Acquire(SlotResourcePrefix + slot.ToString(CultureInfo.InvariantCulture), SlotAttemptTimeout);
            if (held != null)
            {
                return held;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IDisposable? TryAcquireTenant(string tenantId, TimeSpan timeout) =>
        Acquire(TenantResourcePrefix + Key(tenantId), timeout < SlotAttemptTimeout ? SlotAttemptTimeout : timeout);

    /// <inheritdoc />
    public (string JobId, bool Created) EnqueueOnce(string tenantId, Func<string> enqueue)
    {
        var key = Key(tenantId);
        using var held = Acquire(EnqueueResourcePrefix + key, EnqueueLockTimeout) ??
                         throw new TimeoutException(
                             $"Could not take the enqueue lock of the index upgrade of tenant '{tenantId}'.");
        using var connection = _storage().GetConnection();
        var existing = GetHashValue(connection, JobHashPrefix + key, "jobId");
        if (!string.IsNullOrEmpty(existing) &&
            connection.GetStateData(existing)?.Name is { } state && ActiveStates.Contains(state))
        {
            return (existing, false);
        }

        var jobId = enqueue();
        SetHash(connection, JobHashPrefix + key, "jobId", jobId);
        return (jobId, true);
    }

    /// <inheritdoc />
    public string? GetJobId(string tenantId)
    {
        using var connection = _storage().GetConnection();
        var jobId = GetHashValue(connection, JobHashPrefix + Key(tenantId), "jobId");
        return string.IsNullOrEmpty(jobId) ? null : jobId;
    }

    /// <inheritdoc />
    public void RequestYield(string tenantId)
    {
        using var connection = _storage().GetConnection();
        SetHash(connection, YieldHashPrefix + Key(tenantId), "at",
            _time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public bool IsYieldRequested(string tenantId)
    {
        using var connection = _storage().GetConnection();
        var at = ParseTime(GetHashValue(connection, YieldHashPrefix + Key(tenantId), "at"));
        return at != null &&
               _time.GetUtcNow().UtcDateTime - at.Value < IIndexUpgradeCoordination.YieldRequestLifetime;
    }

    /// <inheritdoc />
    public void ClearYield(string tenantId)
    {
        using var connection = _storage().GetConnection();
        using var transaction = connection.CreateWriteTransaction();
        transaction.RemoveHash(YieldHashPrefix + Key(tenantId));
        transaction.Commit();
    }

    /// <inheritdoc />
    public DateTime? GetWindowClosesAt()
    {
        using var connection = _storage().GetConnection();
        return ParseTime(GetHashValue(connection, WindowHash, "closesAt"));
    }

    /// <inheritdoc />
    public void OpenWindow(DateTime closesAtUtc)
    {
        using var connection = _storage().GetConnection();
        SetHash(connection, WindowHash, "closesAt",
            closesAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private HangfireDistributedLockOwner? Acquire(string resource, TimeSpan timeout)
    {
        var owner = new HangfireDistributedLockOwner(_storage, resource, timeout, "octo-index-upgrade-lock");
        return owner.Start() ? owner : null;
    }

    private static void SetHash(IStorageConnection connection, string key, string field, string value)
    {
        using var transaction = connection.CreateWriteTransaction();
        transaction.SetRangeInHash(key, [new KeyValuePair<string, string>(field, value)]);
        transaction.Commit();
    }

    private static string? GetHashValue(IStorageConnection connection, string key, string field) =>
        connection.GetAllEntriesFromHash(key) is { } entries && entries.TryGetValue(field, out var value) ? value : null;

    private static DateTime? ParseTime(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static string Key(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return tenantId.Trim().ToLowerInvariant();
    }
}
