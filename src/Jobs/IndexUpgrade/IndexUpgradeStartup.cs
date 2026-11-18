using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.IndexUpgrade;

/// <summary>
///     Registers the triggers of the index upgrade at the service start (AB#9118). Only writes to the job storage;
///     never inspects a tenant and never builds.
/// </summary>
public static class IndexUpgradeStartup
{
    /// <summary>The dedupe key of the after-deploy sweep (no tenant id can contain a tilde).</summary>
    public const string SweepKey = "~after-deploy-sweep";

    /// <summary>
    ///     Immediate: schedules one sweep over all tenants after <see cref="IndexUpgradeOptions.StartDelay" /> - one for
    ///     all replicas that start together (deduplicated like a tenant job under <see cref="SweepKey" />) - and removes
    ///     the window job. Window: keeps the recurring window job at <see cref="IndexUpgradeWindowOptions.Cron" /> while
    ///     the window is enabled and removes it otherwise. A configuration that builds nothing registers nothing.
    ///     Returns false when the registration failed (logged); never throws.
    /// </summary>
    public static bool Apply(IOptions<IndexUpgradeOptions> optionsAccessor, IRecurringJobManager recurringJobManager,
        IBackgroundJobClient backgroundJobClient, IIndexUpgradeCoordination coordination, ILogger logger)
    {
        IndexUpgradeOptions options;
        try
        {
            options = optionsAccessor.Value;
        }
        catch (Exception e)
        {
            // A value the configuration binder cannot convert: the upgrade stays off, the service starts.
            logger.LogError(e,
                "Index upgrade: the configuration section IndexUpgrade is invalid; no paging index is upgraded");
            return true;
        }

        try
        {
            if (options.Schedule != IndexUpgradeSchedule.Immediate && options.CanBuild)
            {
                try
                {
                    recurringJobManager.AddOrUpdate<IIndexUpgradeSweepJob>(IndexUpgradeConstants.WindowRecurringJobId,
                        job => job.RunAsync(IndexUpgradeConstants.TriggerWindow, JobCancellationToken.Null, null),
                        options.Window.Cron);
                }
                catch (ArgumentException e)
                {
                    // An invalid cron: Hangfire refuses the update and would keep an older trigger. Remove it, so no
                    // window opens on a schedule nobody configured any more.
                    recurringJobManager.RemoveIfExists(IndexUpgradeConstants.WindowRecurringJobId);
                    logger.LogError(e,
                        "Index upgrade: IndexUpgrade:Window:Cron '{Cron}' is invalid; the build window is switched off",
                        options.Window.Cron);
                    return true;
                }

                logger.LogInformation(
                    "Index upgrade: Window schedule, the build window opens at '{Cron}' (UTC) for {MaxDuration}",
                    options.Window.Cron, options.Window.MaxDuration);
                return true;
            }

            recurringJobManager.RemoveIfExists(IndexUpgradeConstants.WindowRecurringJobId);
            if (!options.CanBuild)
            {
                logger.LogInformation(
                    "Index upgrade: switched off (Schedule {Schedule}, Window:Enabled {WindowEnabled}, MaxConcurrent " +
                    "{MaxConcurrent}); no paging index is upgraded",
                    options.Schedule, options.Window.Enabled, options.MaxConcurrent);
                return true;
            }

            var (jobId, created) = coordination.EnqueueOnce(SweepKey, () =>
                backgroundJobClient.Schedule<IIndexUpgradeSweepJob>(
                    job => job.RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, JobCancellationToken.Null, null),
                    options.StartDelay));
            logger.LogInformation(
                "Index upgrade: Immediate schedule, sweep over all tenants in {StartDelay} (job {JobId}{Shared})",
                options.StartDelay, jobId, created ? "" : ", scheduled by another replica");
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Index upgrade: could not register its jobs; trying again");
            return false;
        }
    }
}

/// <summary>
///     Runs <see cref="IndexUpgradeStartup.Apply" /> in the background after the start, and again with a growing pause
///     (30 s doubling to 5 min, at most 10 attempts) while the job storage refuses it. The host never waits for it.
/// </summary>
/// <remarks>
///     The Hangfire services are resolved inside the attempt, not in the constructor: building them builds the job
///     storage, which fails while MongoDB cannot be reached, and that must not stop the host either.
/// </remarks>
public sealed class IndexUpgradeStartupService(
    IServiceProvider services,
    ILogger<IndexUpgradeStartupService> logger) : BackgroundService
{
    internal const int MaxAttempts = 10;
    internal static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leave the start of the host first; the registration is no part of it.
        await Task.Yield();
        var pause = FirstPause;
        for (var attempt = 1; attempt <= MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            if (TryApply())
            {
                return;
            }

            try
            {
                await Task.Delay(pause, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            pause = pause * 2 > MaxPause ? MaxPause : pause * 2;
        }

        logger.LogError(
            "Index upgrade: its jobs could not be registered after {Attempts} attempts; the next start tries again",
            MaxAttempts);
    }

    private bool TryApply()
    {
        try
        {
            return IndexUpgradeStartup.Apply(services.GetRequiredService<IOptions<IndexUpgradeOptions>>(),
                services.GetRequiredService<IRecurringJobManager>(),
                services.GetRequiredService<IBackgroundJobClient>(),
                services.GetRequiredService<IIndexUpgradeCoordination>(), logger);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Index upgrade: the job storage is not available yet; trying again");
            return false;
        }
    }
}
