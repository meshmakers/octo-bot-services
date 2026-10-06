using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <inheritdoc />
public class SecretSweepJob(
    ILogger<SecretSweepJob> logger,
    ISecretSweepCoordinator coordinator) : ISecretSweepJob
{
    /// <inheritdoc />
    public async Task<SecretSweepReport> Run(string tenantId, SecretSweepMode mode,
        IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;
        var report = await coordinator.SweepTenantAsync(tenantId, mode, SecretSweepTrigger.Manual, ct);

        if (report.Outcome is SecretSweepOutcome.Skipped or SecretSweepOutcome.Failed)
        {
            throw new JobFailedException(
                $"Secret sweep {mode} of tenant '{tenantId}' {report.Outcome.ToString().ToLowerInvariant()}: {report.Reason}");
        }

        return report;
    }

    /// <inheritdoc />
    public async Task<SecretSweepRunSummary> RunAllTenants(string tenantId, SecretSweepMode mode,
        SecretSweepTrigger trigger, IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;
        var summary = new SecretSweepRunSummary
        {
            Mode = mode,
            Trigger = trigger,
            StartedAt = DateTime.UtcNow
        };

        var tenantIds = await coordinator.GetTenantIdsAsync();
        logger.LogInformation("Secret sweep {Mode} of all tenants started for {Count} tenant(s)", mode,
            tenantIds.Count);

        foreach (var id in tenantIds)
        {
            ct.ThrowIfCancellationRequested();
            summary.Tenants.Add(await coordinator.SweepTenantAsync(id, mode, trigger, ct));
        }

        summary.CompletedAt = DateTime.UtcNow;

        var succeeded = summary.Count(SecretSweepOutcome.Succeeded);
        var withFailures = summary.Count(SecretSweepOutcome.CompletedWithFailures);
        var skipped = summary.Count(SecretSweepOutcome.Skipped);
        var failed = summary.Count(SecretSweepOutcome.Failed);
        var remainingLegacy = summary.Tenants.Sum(t => t.RemainingLegacyValues);

        logger.LogInformation(
            "Secret sweep {Mode} of all tenants done: {Succeeded} succeeded, {WithFailures} with failures, " +
            "{Skipped} skipped, {Failed} failed; {RemainingLegacy} legacy value(s) (plaintext + enc_v1) remain",
            mode, succeeded, withFailures, skipped, failed, remainingLegacy);

        if (skipped > 0 || failed > 0)
        {
            var problems = string.Join(", ", summary.Tenants
                .Where(t => t.Outcome is SecretSweepOutcome.Skipped or SecretSweepOutcome.Failed)
                .Select(t => $"{t.TenantId} ({t.Outcome})"));
            throw new JobFailedException(
                $"Secret sweep {mode} of all tenants: {succeeded + withFailures} completed, {skipped} skipped, " +
                $"{failed} failed: {problems}. See system/v1/secrets/reports for the reasons.");
        }

        return summary;
    }
}
