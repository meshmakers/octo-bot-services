using System.ComponentModel;
using Hangfire;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Cleans up stale backup files from disk storage.
/// </summary>
public interface ICleanupStaleFilesJob
{
    /// <summary>
    /// Removes files from tus upload and dump directories that are older than the configured retention period.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to abort the job.</param>
    [DisplayName("Cleanup stale backup files")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    Task Run(IBotCancellationToken? cancellationToken);
}
