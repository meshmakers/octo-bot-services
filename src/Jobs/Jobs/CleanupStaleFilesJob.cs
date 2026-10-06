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
    int secretBackupRetentionDays = 7) : ICleanupStaleFilesJob
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
            if (deletedSecretBackups > 0)
            {
                logger.LogInformation("Deleted {Count} pre-sweep secret backup(s) older than {Days} days",
                    deletedSecretBackups, secretBackupRetentionDays);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error during stale backup file cleanup");
            throw;
        }
    }
}
