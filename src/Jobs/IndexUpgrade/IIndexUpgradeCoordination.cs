namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     The state the index upgrade shares across all bot replicas (AB#9118), kept in the shared Hangfire job storage:
///     the global limit, the per-tenant lease, the job of each tenant (dedupe), the yield request of a CK import and the
///     current build window.
/// </summary>
public interface IIndexUpgradeCoordination
{
    /// <summary>
    ///     How long a yield request counts without being renewed: a waiting import renews it every few seconds, a
    ///     crashed one stops holding the upgrade back after this time.
    /// </summary>
    static readonly TimeSpan YieldRequestLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Takes one of <paramref name="maxConcurrent" /> global slots without waiting. Null when all are held, i.e. as
    ///     many tenants are being rebuilt by any replica, and always for <paramref name="maxConcurrent" /> 0 or less (no
    ///     builds). A failure to release a held slot is swallowed (the lock expires), so it never changes the outcome of
    ///     the run that held it.
    /// </summary>
    IDisposable? TryAcquireGlobalSlot(int maxConcurrent);

    /// <summary>
    ///     Takes the lease of <paramref name="tenantId" />, held by a running upgrade or by a CK import of the tenant.
    ///     Null when it is not free within <paramref name="timeout" />.
    /// </summary>
    IDisposable? TryAcquireTenant(string tenantId, TimeSpan timeout);

    /// <summary>
    ///     Runs <paramref name="enqueue" /> unless the tenant already has a job that is enqueued, scheduled, awaiting or
    ///     processing; remembers the job id it returns. Returns the id of the job of the tenant and whether it is new.
    ///     Serialized per tenant across replicas.
    /// </summary>
    (string JobId, bool Created) EnqueueOnce(string tenantId, Func<string> enqueue);

    /// <summary>The id of the job last enqueued for the tenant, null when there was none.</summary>
    string? GetJobId(string tenantId);

    /// <summary>
    ///     The CK import <paramref name="requestId" /> asks a running upgrade of the tenant to stop before its next
    ///     collection (renewed while it waits). Every import keeps its own request.
    /// </summary>
    void RequestYield(string tenantId, string requestId);

    /// <summary>True while any CK import of the tenant asked within <see cref="YieldRequestLifetime" />.</summary>
    bool IsYieldRequested(string tenantId);

    /// <summary>The CK import <paramref name="requestId" /> is done (or gave up); the requests of other imports stay.</summary>
    void ClearYield(string tenantId, string requestId);

    /// <summary>The end of the current build window (Window schedule), null when no window was opened.</summary>
    DateTime? GetWindowClosesAt();

    /// <summary>Opens a build window until <paramref name="closesAtUtc" />.</summary>
    void OpenWindow(DateTime closesAtUtc);
}
