using System.ComponentModel;
using Hangfire;
using Meshmakers.Octo.Backend.Jobs.DTOs;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Job for comparing a live tenant with a backup archive
/// </summary>
public interface ICompareLiveTenantWithBackupJob
{
    /// <summary>
    /// Compares a live tenant with a backup archive
    /// </summary>
    /// <param name="liveTenantId">The live tenant ID</param>
    /// <param name="backupCacheKey">The cache key of the uploaded backup file</param>
    /// <param name="options">The options</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The cache key where the comparison report (JSON) is stored</returns>
    [DisplayName("Compare live tenant '{0}' with backup")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task<string?> Run(string liveTenantId, string backupCacheKey, TenantComparisonOptionsDto? options,
        IBotCancellationToken? cancellationToken);
}
