using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <summary>
///     Marks secret sweep runs that a crashed or killed bot process left in <c>Running</c> as <c>Failed</c>
///     with the reason <see cref="InterruptedReason" /> (AB#5539), so the run history does not show them as
///     running forever.
/// </summary>
/// <remarks>
///     A run is interrupted when its job (the run id is the Hangfire job id) is no longer processing. When
///     Hangfire still says processing, the run is interrupted only if the processing server is gone or has
///     not sent a heartbeat since this service started, and the run started before this service - a server
///     killed hard never updates its jobs, and its entry stays in the server list until it times out. Runs
///     without a job are interrupted when they started before this service. A run whose job is re-executed
///     by Hangfire later simply gets a new <c>Running</c> entry with the same id.
/// </remarks>
public class SecretSweepInterruptedRunRecovery(
    ISecretSweepRunStore runStore,
    ISecretSweepJobInspector jobInspector,
    ILogger<SecretSweepInterruptedRunRecovery> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>
    ///     Reason recorded on an interrupted run.
    /// </summary>
    public const string InterruptedReason = "Interrupted (service restart)";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    ///     Marks the interrupted runs of <paramref name="tenantIds" /> as failed.
    /// </summary>
    /// <param name="tenantIds">Tenants whose run history is checked</param>
    /// <param name="serviceStartedAt">When this service started (UTC)</param>
    /// <param name="cancellationToken">Cancellation</param>
    /// <returns>Number of runs marked failed.</returns>
    public async Task<int> RecoverAsync(IEnumerable<string> tenantIds, DateTime serviceStartedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        var marked = 0;
        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                marked += await RecoverTenantAsync(tenantId, serviceStartedAt);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Bookkeeping only: one broken tenant must not stop the others.
                logger.LogWarning(e, "Could not check the secret sweep runs of tenant '{TenantId}' for interruption",
                    tenantId);
            }
        }

        return marked;
    }

    private async Task<int> RecoverTenantAsync(string tenantId, DateTime serviceStartedAt)
    {
        var marked = 0;
        var runs = await runStore.GetRunsAsync(tenantId);
        foreach (var running in runs.Where(r => r.Outcome == SecretSweepOutcomeDto.Running))
        {
            var jobState = jobInspector.GetState(running.RunId, serviceStartedAt);
            var interrupted = jobState switch
            {
                SecretSweepJobState.NotProcessing => true,
                SecretSweepJobState.ProcessingOnLiveServer => false,
                _ => running.StartedAt < serviceStartedAt
            };
            if (!interrupted)
            {
                continue;
            }

            var startedAt = running.StartedAt;
            var completedAt = _time.GetUtcNow().UtcDateTime;
            var changed = false;
            await runStore.UpdateAsync(tenantId, running.RunId, run =>
            {
                // Finished meanwhile, or re-executed by Hangfire (new start): leave it.
                if (run.Outcome != SecretSweepOutcomeDto.Running || run.StartedAt != startedAt)
                {
                    return false;
                }

                run.Outcome = SecretSweepOutcomeDto.Failed;
                run.CompletedAt = completedAt;
                run.Reason = InterruptedReason;
                changed = true;
                return true;
            });

            if (changed)
            {
                marked++;
                logger.LogWarning(
                    "Secret sweep run '{RunId}' ({Mode}) of tenant '{TenantId}', started {StartedAt:o}, was interrupted " +
                    "by a service restart (job state {JobState}); marked failed. Run the sweep again",
                    running.RunId, running.Mode, tenantId, startedAt, jobState);
            }
        }

        return marked;
    }
}

/// <summary>
///     Runs <see cref="SecretSweepInterruptedRunRecovery" /> once,
///     <see cref="SecretSweepJobOptions.InterruptedRunCheckDelaySeconds" /> after the service started, over all
///     tenants (AB#5539).
/// </summary>
public class SecretSweepInterruptedRunRecoveryHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SecretSweepJobOptions> options,
    ILogger<SecretSweepInterruptedRunRecoveryHostedService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serviceStartedAt = _time.GetUtcNow().UtcDateTime;
        var delaySeconds = options.Value.InterruptedRunCheckDelaySeconds;
        if (delaySeconds <= 0)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _time, stoppingToken);
            await RunOnceAsync(serviceStartedAt, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>
    ///     Checks the run history of all tenants once.
    /// </summary>
    /// <param name="serviceStartedAt">When this service started (UTC)</param>
    /// <param name="cancellationToken">Cancellation</param>
    /// <returns>Number of runs marked failed; <c>0</c> when the check failed.</returns>
    internal async Task<int> RunOnceAsync(DateTime serviceStartedAt, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<ISecretSweepCoordinator>();
            var recovery = scope.ServiceProvider.GetRequiredService<SecretSweepInterruptedRunRecovery>();
            var tenantIds = await coordinator.GetTenantIdsAsync();
            var marked = await recovery.RecoverAsync(tenantIds, serviceStartedAt, cancellationToken);
            if (marked > 0)
            {
                logger.LogWarning("{Count} secret sweep run(s) interrupted by a service restart were marked failed",
                    marked);
            }

            return marked;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Checking the secret sweep run history for interrupted runs failed");
            return 0;
        }
    }
}
