using Meshmakers.Octo.Backend.Jobs.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a recurring job that cleans up stale backup files from disk storage.
/// </summary>
public class CleanupStaleFilesJob(
    ILogger<CleanupStaleFilesJob> logger,
    IBackupFileStorageService backupFileStorage,
    int fileRetentionHours) : ICleanupStaleFilesJob
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
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error during stale backup file cleanup");
            throw;
        }
    }
}
