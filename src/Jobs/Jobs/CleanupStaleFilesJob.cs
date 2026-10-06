using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a recurring job that cleans up stale backup files: local tus uploads, dumps and scratch files after
/// <c>fileRetentionHours</c>, pre-sweep secret backups (AB#5539) after <c>secretBackupRetentionDays</c> — in the
/// artifact store (AB#5561) and, for legacy dumps written before it, on the local disk — and the artifact store's
/// tenant dumps and restore staging files after <c>artifactRetentionHours</c>. The storage backend's lifecycle
/// rules are the backstop, not a replacement.
/// </summary>
public class CleanupStaleFilesJob(
    ILogger<CleanupStaleFilesJob> logger,
    IBackupFileStorageService backupFileStorage,
    int fileRetentionHours,
    int secretBackupRetentionDays = 7,
    ISecretSweepRunStore? secretSweepRunStore = null,
    TimeProvider? timeProvider = null,
    IBotArtifactStorage? artifactStorage = null,
    int artifactRetentionHours = 24) : ICleanupStaleFilesJob
{
    /// <inheritdoc />
    public async Task Run(IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;
        try
        {
            logger.LogInformation("Running cleanup of stale backup files (retention: {Hours} hours)",
                fileRetentionHours);

            var retention = TimeSpan.FromHours(fileRetentionHours);
            var deletedCount = await backupFileStorage.CleanupStaleFilesAsync(retention);

            logger.LogInformation("Cleanup completed. Deleted {Count} stale files", deletedCount);

            // Legacy pre-sweep secret backups on the local disk (before AB#5561) - secret material with their
            // own retention (decision 10). They are not migrated; they expire here.
            var deletedSecretBackups = await backupFileStorage.CleanupStaleSecretBackupsAsync(
                TimeSpan.FromDays(secretBackupRetentionDays));
            if (deletedSecretBackups.Count > 0)
            {
                logger.LogInformation("Deleted {Count} legacy pre-sweep secret backup(s) older than {Days} days",
                    deletedSecretBackups.Count, secretBackupRetentionDays);
                await RecordExpiredDumpsAsync(deletedSecretBackups
                    .Select(f => (Path.GetFileName(Path.GetDirectoryName(f)), Path.GetFileName(f))));
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error during stale backup file cleanup");
            throw;
        }

        if (artifactStorage != null)
        {
            await CleanupArtifactStoreAsync(artifactStorage, ct);
        }
    }

    /// <summary>
    ///     App-side retention in the artifact store. Every category is attempted even when one fails; the first
    ///     failure is rethrown at the end so the job shows it.
    /// </summary>
    private async Task CleanupArtifactStoreAsync(IBotArtifactStorage artifacts, CancellationToken ct)
    {
        Exception? firstError = null;

        try
        {
            var scratch = artifacts.CleanupScratch(TimeSpan.FromHours(fileRetentionHours));
            if (scratch > 0)
            {
                logger.LogInformation("Deleted {Count} stale scratch file(s)", scratch);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error during scratch directory cleanup");
            firstError ??= e;
        }

        try
        {
            var expired = await artifacts.DeleteOlderThanAsync(ArtifactCategories.Presweep,
                TimeSpan.FromDays(secretBackupRetentionDays), ct);
            if (expired.Count > 0)
            {
                logger.LogInformation("Deleted {Count} pre-sweep secret backup(s) older than {Days} days from the " +
                                      "artifact store", expired.Count, secretBackupRetentionDays);
                await RecordExpiredDumpsAsync(expired.Select(p => ((string?)p.TenantId, p.FileName)));
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error during the pre-sweep backup cleanup of the artifact store");
            firstError ??= e;
        }

        foreach (var category in new[] { ArtifactCategories.TenantDumps, ArtifactCategories.RestoreStaging })
        {
            try
            {
                var deleted = await artifacts.DeleteOlderThanAsync(category,
                    TimeSpan.FromHours(Math.Max(1, artifactRetentionHours)), ct);
                if (deleted.Count > 0)
                {
                    logger.LogInformation("Deleted {Count} artifact(s) of category '{Category}' older than {Hours} hours",
                        deleted.Count, category, artifactRetentionHours);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error during the '{Category}' cleanup of the artifact store", category);
                firstError ??= e;
            }
        }

        if (firstError != null)
        {
            throw new JobFailedException("The artifact store cleanup failed; see the error log.", firstError);
        }
    }

    /// <summary>
    ///     Marks the expired dumps as deleted in the sweep run history (AB#5544, <c>deletedAt</c> without
    ///     <c>deletedBy</c>). The files are already gone; a bookkeeping problem is only logged.
    /// </summary>
    private async Task RecordExpiredDumpsAsync(IEnumerable<(string? TenantId, string FileName)> deletedFiles)
    {
        if (secretSweepRunStore == null)
        {
            return;
        }

        var deletedAt = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        foreach (var (tenantId, fileName) in deletedFiles)
        {
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                continue;
            }

            try
            {
                await secretSweepRunStore.MarkDumpDeletedAsync(tenantId, fileName, deletedAt, null);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not record the expiry of pre-sweep secret backup '{FileName}' of " +
                                     "tenant '{TenantId}' in the sweep run history", fileName, tenantId);
            }
        }
    }
}
