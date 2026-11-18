using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

/// <summary>
///     Registers every tenant dump in the artifact store that has no entry yet, dated by the artifact. Only the dump job
///     writes the tenant dump category, so every artifact there is a dump the platform produced. Skips dumps of tenants
///     that no longer exist and dumps whose retention has already run out. A dump that cannot be read is logged and
///     skipped.
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
    /// <inheritdoc />
    public async Task<int> Run(IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;
        if (artifactStorage == null || !await systemContext.IsSystemTenantExistingAsync())
        {
            return 0;
        }

        var existingTenants = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var oldestStillValidUtc = timeProvider.GetUtcNow().UtcDateTime - options.Value.Retention;
        var registeredCount = 0;
        await foreach (var artifact in artifactStorage.ListCategoryAsync(ArtifactCategories.TenantDumps, ct))
        {
            var storedAtUtc = artifact.CreatedAt.UtcDateTime;
            if (storedAtUtc <= oldestStillValidUtc ||
                !await TenantExistsAsync(artifact.TenantId, existingTenants) ||
                await registry.ContainsFileAsync(artifact.TenantId, artifact.FileName, ct))
            {
                continue;
            }

            if (await TryRegisterAsync(artifact, storedAtUtc, ct))
            {
                registeredCount++;
            }
        }

        logger.LogInformation("Dump registry backfill registered {Count} stored tenant dump(s)", registeredCount);
        return registeredCount;
    }

    private async Task<bool> TryRegisterAsync(StoredTenantArtifactEntry artifact, DateTime storedAtUtc,
        CancellationToken ct)
    {
        try
        {
            await recorder.RecordStoredDumpAsync(artifact.TenantId, artifact.FileName, storedAtUtc, ct);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Dump '{FileName}' of tenant '{TenantId}' could not be registered by the backfill", artifact.FileName,
                artifact.TenantId);
            return false;
        }
    }

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
