using System.ComponentModel;
using Hangfire;
using Meshmakers.Octo.Backend.Jobs.DTOs;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Job for comparing two backup archives
/// </summary>
public interface ICompareBackupsJob
{
    /// <summary>
    /// Compares two backup archives
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="sourceBackupCacheKey">The cache key of the source backup file</param>
    /// <param name="targetBackupCacheKey">The cache key of the target backup file</param>
    /// <param name="options">The options</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The cache key where the comparison report (JSON) is stored</returns>
    [DisplayName("Compare two backup archives")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task<string?> Run(string tenantId, string sourceBackupCacheKey, string targetBackupCacheKey,
        TenantComparisonOptionsDto? options,
        IBotCancellationToken? cancellationToken);
}
