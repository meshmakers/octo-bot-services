using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>
///     Registers every MongoDB dump in the artifact store that has no entry yet, dated by the start time in its file name
///     (<see cref="DumpFileStartTime" />), never by the time it was stored: a dump of an earlier tenant with the same id
///     stays filtered by the tenant generation. Only the dump job writes the tenant dump category, so every artifact there
///     is a dump the platform produced. Skips dumps of tenants that no longer exist, dumps whose retention has run out
///     (checked for each dump), names without a start time and names dated in the future. A dump that cannot be read is
///     logged and skipped; when the registry cannot be read or written, the scan goes on and the job fails at its end, so
///     Hangfire runs it again.
/// </summary>
public sealed class DumpHashBackfillJob(
    IDumpHashRegistry registry,
    DumpHashRecorder recorder,
    ISystemContext systemContext,
    IOptions<DumpHashRegistryOptions> options,
    TimeProvider timeProvider,
    ILogger<DumpHashBackfillJob> logger,
    IBotArtifactStorage? artifactStorage = null) : IDumpHashBackfillJob
{
    private enum BackfillOutcome
    {
        Registered,
        Skipped,
        RegistryFailed
    }

    /// <inheritdoc />
    public async Task<int> Run(IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;
        if (artifactStorage == null || !await systemContext.IsSystemTenantExistingAsync())
        {
            return 0;
        }

        var existingTenants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var registeredCount = 0;
        var registryFailureCount = 0;
        await foreach (var artifact in artifactStorage.ListCategoryAsync(ArtifactCategories.TenantDumps, ct))
        {
            if (await CandidateStartAsync(artifact, existingTenants) is not { } earliestStartUtc)
            {
                continue;
            }

            var outcome = await TryRegisterAsync(artifact, earliestStartUtc, ct);
            registeredCount += outcome == BackfillOutcome.Registered ? 1 : 0;
            registryFailureCount += outcome == BackfillOutcome.RegistryFailed ? 1 : 0;
        }

        logger.LogInformation("Dump registry backfill registered {Count} stored tenant dump(s)", registeredCount);
        if (registryFailureCount > 0)
        {
            throw new JobFailedException(
                $"The dump registry could not be read or written for {registryFailureCount} stored dump(s); " +
                "the backfill runs again and skips the dumps registered meanwhile.");
        }

        return registeredCount;
    }

    private async Task<DateTime?> CandidateStartAsync(StoredTenantArtifactEntry artifact,
        Dictionary<string, bool> existingTenants)
    {
        if (!DumpFileStartTime.TryRead(artifact.FileName, out var earliestStartUtc))
        {
            logger.LogInformation(
                "Dump '{FileName}' of tenant '{TenantId}' is not registered by the backfill: only a MongoDB dump carries a safe start time; a system operator restores it",
                artifact.FileName, artifact.TenantId);
            return null;
        }

        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        if (earliestStartUtc > nowUtc)
        {
            logger.LogWarning(
                "Dump '{FileName}' of tenant '{TenantId}' is not registered by the backfill: its name is dated in the future",
                artifact.FileName, artifact.TenantId);
            return null;
        }

        var isWithinRetention = earliestStartUtc > nowUtc - options.Value.Retention;
        var isCandidate = isWithinRetention && await TenantExistsAsync(artifact.TenantId, existingTenants);
        return isCandidate ? earliestStartUtc : null;
    }

    private async Task<BackfillOutcome> TryRegisterAsync(StoredTenantArtifactEntry artifact, DateTime earliestStartUtc,
        CancellationToken ct)
    {
        try
        {
            if (await registry.ContainsFileAsync(artifact.TenantId, artifact.FileName, ct))
            {
                return BackfillOutcome.Skipped;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRegistryFailure(exception, artifact);
            return BackfillOutcome.RegistryFailed;
        }

        try
        {
            await recorder.RecordStoredDumpAsync(artifact.TenantId, artifact.FileName, earliestStartUtc, ct);
            return BackfillOutcome.Registered;
        }
        catch (DumpRegistryWriteException exception)
        {
            LogRegistryFailure(exception, artifact);
            return BackfillOutcome.RegistryFailed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Dump '{FileName}' of tenant '{TenantId}' cannot be read; the backfill skips it",
                artifact.FileName, artifact.TenantId);
            return BackfillOutcome.Skipped;
        }
    }

    private void LogRegistryFailure(Exception exception, StoredTenantArtifactEntry artifact) =>
        logger.LogWarning(exception,
            "The dump registry failed for dump '{FileName}' of tenant '{TenantId}'; the backfill will fail and run again",
            artifact.FileName, artifact.TenantId);

    private async Task<bool> TenantExistsAsync(string tenantId, Dictionary<string, bool> existingTenants)
    {
        if (!existingTenants.TryGetValue(tenantId, out var exists))
        {
            exists = await systemContext.FindTenantContextAsync(tenantId) != null;
            existingTenants[tenantId] = exists;
        }

        return exists;
    }
}
