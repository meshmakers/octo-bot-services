using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     Deletes scratch files a crashed or killed bot process left behind (AB#5559): a plaintext pre-sweep dump
///     written by mongodump before it was encrypted, or a decrypted dump waiting for mongorestore. Runs once at
///     startup, in addition to the hourly cleanup (which only removes files after <c>fileRetentionHours</c>), so
///     such plaintext does not sit on the disk for hours after a restart.
/// </summary>
/// <remarks>
///     Only files older than <see cref="MaxAge" /> are removed: a dump job of another bot replica that shares the
///     scratch directory (or a job that started right before this host) is still writing its younger files.
/// </remarks>
public class StaleScratchCleanupHostedService(
    IBotArtifactStorage artifactStorage,
    ILogger<StaleScratchCleanupHostedService> logger) : IHostedService
{
    /// <summary>
    ///     Minimum age of a scratch file deleted at startup.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var deleted = artifactStorage.CleanupScratch(MaxAge);
            if (deleted > 0)
            {
                logger.LogWarning(
                    "Deleted {Count} stale scratch file(s) older than {Hours} hour(s) left behind by an earlier process",
                    deleted, MaxAge.TotalHours);
            }
        }
        catch (Exception e)
        {
            // Housekeeping only: the hourly cleanup retries; the service must start anyway.
            logger.LogError(e, "Startup cleanup of the scratch directory '{Directory}' failed",
                artifactStorage.ScratchDirectory);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
