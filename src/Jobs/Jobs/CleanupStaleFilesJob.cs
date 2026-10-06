using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a recurring job that cleans up stale backup files from disk storage: tus uploads and dumps
/// after <c>fileRetentionHours</c>, pre-sweep secret backups (AB#5539) after <c>secretBackupRetentionDays</c>.
/// </summary>
public class CleanupStaleFilesJob(
    ILogger<CleanupStaleFilesJob> logger,
    IBackupFileStorageService backupFileStorage,
    int fileRetentionHours,
    int secretBackupRetentionDays = 7,
    ISecretSweepRunStore? secretSweepRunStore = null,
    TimeProvider? timeProvider = null) : ICleanupStaleFilesJob
{
    /// <inheritdoc />
    public async Task Run(IBotCancellationToken? cancellationToken)
    {
        try
        {
            logger.LogInformation("Running cleanup of stale backup files (retention: {Hours} hours)",
                fileRetentionHours);

            var retention = TimeSpan.FromHours(fileRetentionHours);
            var deletedCount = await backupFileStorage.CleanupStaleFilesAsync(retention);

            logger.LogInformation("Cleanup completed. Deleted {Count} stale files", deletedCount);

            // Pre-sweep secret backups are secret material with their own retention (decision 10).
            var deletedSecretBackups = await backupFileStorage.CleanupStaleSecretBackupsAsync(
                TimeSpan.FromDays(secretBackupRetentionDays));
            if (deletedSecretBackups.Count > 0)
            {
                logger.LogInformation("Deleted {Count} pre-sweep secret backup(s) older than {Days} days",
                    deletedSecretBackups.Count, secretBackupRetentionDays);
                await RecordExpiredDumpsAsync(deletedSecretBackups);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error during stale backup file cleanup");
            throw;
        }
    }

    /// <summary>
    ///     Marks the expired dumps as deleted in the sweep run history (AB#5544, <c>deletedAt</c> without
    ///     <c>deletedBy</c>). The files are already gone; a bookkeeping problem is only logged.
    /// </summary>
    private async Task RecordExpiredDumpsAsync(IReadOnlyList<string> deletedFiles)
    {
        if (secretSweepRunStore == null)
        {
            return;
        }

        var deletedAt = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        foreach (var file in deletedFiles)
        {
            // Layout: <secret backup root>/<tenant>/<file name>.
            var tenantId = Path.GetFileName(Path.GetDirectoryName(file));
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                continue;
            }

            try
            {
                await secretSweepRunStore.MarkDumpDeletedAsync(tenantId, Path.GetFileName(file), deletedAt, null);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Could not record the expiry of pre-sweep secret backup '{FileName}' of " +
                                     "tenant '{TenantId}' in the sweep run history", Path.GetFileName(file), tenantId);
            }
        }
    }
}
