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
    ///     many tenants are being rebuilt by any replica.
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

    /// <summary>A CK import of the tenant asks a running upgrade to stop before its next collection.</summary>
    void RequestYield(string tenantId);

    /// <summary>True while a CK import asked within <see cref="YieldRequestLifetime" />.</summary>
    bool IsYieldRequested(string tenantId);

    /// <summary>The CK import is done (or gave up).</summary>
    void ClearYield(string tenantId);

    /// <summary>The end of the current build window (Window schedule), null when no window was opened.</summary>
    DateTime? GetWindowClosesAt();

    /// <summary>Opens a build window until <paramref name="closesAtUtc" />.</summary>
    void OpenWindow(DateTime closesAtUtc);
}
