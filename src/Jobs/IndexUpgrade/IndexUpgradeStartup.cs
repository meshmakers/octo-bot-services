using Hangfire;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     Registers the triggers of the index upgrade at the service start (AB#9118). Only writes to the job storage;
///     never inspects a tenant and never builds, so the start is not delayed.
/// </summary>
public static class IndexUpgradeStartup
{
    /// <summary>The dedupe key of the after-deploy sweep (no tenant id can contain a tilde).</summary>
    public const string SweepKey = "~after-deploy-sweep";

    /// <summary>
    ///     Immediate: schedules one sweep over all tenants after <see cref="IndexUpgradeOptions.StartDelay" /> - one for
    ///     all replicas that start together (deduplicated like a tenant job under <see cref="SweepKey" />) - and removes
    ///     the window job. Window: keeps the
    ///     recurring window job at <see cref="IndexUpgradeWindowOptions.Cron" /> while the window is enabled and removes
    ///     it otherwise. Never throws: the upgrade must not stop the service from starting.
    /// </summary>
    public static void Apply(IndexUpgradeOptions options, IRecurringJobManager recurringJobManager,
        IBackgroundJobClient backgroundJobClient, IIndexUpgradeCoordination coordination, ILogger logger)
    {
        try
        {
            if (options.Schedule == IndexUpgradeSchedule.Window && options.Window.Enabled)
            {
                recurringJobManager.AddOrUpdate<IIndexUpgradeSweepJob>(IndexUpgradeConstants.WindowRecurringJobId,
                    job => job.RunAsync(IndexUpgradeConstants.TriggerWindow, JobCancellationToken.Null),
                    options.Window.Cron);
                logger.LogInformation(
                    "Index upgrade: Window schedule, the build window opens at '{Cron}' (UTC) for {MaxDuration}",
                    options.Window.Cron, options.Window.MaxDuration);
                return;
            }

            recurringJobManager.RemoveIfExists(IndexUpgradeConstants.WindowRecurringJobId);
            if (options.Schedule == IndexUpgradeSchedule.Window)
            {
                logger.LogInformation(
                    "Index upgrade: Window schedule with the window switched off; no paging index is upgraded");
                return;
            }

            var (jobId, created) = coordination.EnqueueOnce(SweepKey, () =>
                backgroundJobClient.Schedule<IIndexUpgradeSweepJob>(
                    job => job.RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, JobCancellationToken.Null),
                    options.StartDelay));
            logger.LogInformation(
                "Index upgrade: Immediate schedule, sweep over all tenants in {StartDelay} (job {JobId}{Shared})",
                options.StartDelay, jobId, created ? "" : ", scheduled by another replica");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Index upgrade: could not register its jobs; no paging index is upgraded until the next start");
        }
    }
}
