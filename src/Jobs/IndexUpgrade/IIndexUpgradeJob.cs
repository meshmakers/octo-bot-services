using Hangfire;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>Constants of the paging index upgrade (AB#9118).</summary>
public static class IndexUpgradeConstants
{
    /// <summary>
    ///     The Hangfire queue of the upgrade jobs. A server of its own with one worker listens to it on every replica;
    ///     the global limit across replicas is <see cref="IIndexUpgradeCoordination.TryAcquireGlobalSlot" />.
    /// </summary>
    public const string Queue = "index-upgrade";

    /// <summary>The recurring job that opens the build window (Window schedule).</summary>
    public const string WindowRecurringJobId = "index-upgrade-window";

    /// <summary>Trigger names written into the last-run record and the logs.</summary>
    public const string TriggerTenantCreated = "tenant-created";

    /// <inheritdoc cref="TriggerTenantCreated" />
    public const string TriggerAfterDeploy = "after-deploy";

    /// <inheritdoc cref="TriggerTenantCreated" />
    public const string TriggerWindow = "window";
}

/// <summary>
///     Upgrades the paging indexes of one tenant to the tiebreak shape <c>{ckTypeId, attr…, _id, rtState}</c>
///     (<c>ITenantContext.UpgradeTiebreakIndexesAsync</c>, engine AB#8960; this job AB#9118). Idempotent: a tenant
///     without a legacy index ends at once, an interrupted run is completed by the next one.
/// </summary>
/// <remarks>
///     Retries: a job that cannot work now (global limit, CK import of the tenant, <c>index_update_lock</c> held,
///     window closed while waiting) is scheduled again (<see cref="IndexUpgradeDeferAttribute" />) and never counts as
///     a failure. A real failure is retried three times (1, 5, 15 minutes) and then stays <c>Failed</c>.
/// </remarks>
public interface IIndexUpgradeJob
{
    /// <summary>Upgrades the indexes of <paramref name="tenantId" />.</summary>
    /// <param name="tenantId">The tenant</param>
    /// <param name="trigger">What enqueued the job (<see cref="IndexUpgradeConstants" />)</param>
    /// <param name="cancellationToken">
    ///     Hangfire's job cancellation token (enqueue <see cref="JobCancellationToken.Null" />; Hangfire passes the real
    ///     one). Unlike the other bot jobs this is Hangfire's own type, so the job sees the server shutting down and
    ///     stops before the next collection; Hangfire then runs it again instead of leaving it in Processing.
    /// </param>
    [JobDisplayName("Upgrading the paging indexes of tenant '{0}'")]
    [Queue(IndexUpgradeConstants.Queue)]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 900], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    [IndexUpgradeDefer]
    Task UpgradeTenantAsync(string tenantId, string trigger, IJobCancellationToken? cancellationToken);
}
