using System.ComponentModel;
using Hangfire;
using Meshmakers.Octo.Backend.Jobs.DTOs;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Job for comparing a backup archive with a live tenant
/// </summary>
public interface ICompareBackupWithLiveTenantJob
{
    /// <summary>
    /// Compares a backup archive with a live tenant
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="backupCacheKey">The cache key of the uploaded backup file</param>
    /// <param name="liveTenantId">The live tenant ID</param>
    /// <param name="options">The options</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The cache key where the comparison report (JSON) is stored</returns>
    [DisplayName("Compare backup with live tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task<string?> Run(string tenantId, string backupCacheKey, string liveTenantId, TenantComparisonOptionsDto? options,
        IBotCancellationToken? cancellationToken);
}
