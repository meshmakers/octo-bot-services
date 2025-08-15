using System.ComponentModel;
using Hangfire;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Runs a repository dump job
/// </summary>
public interface IDumpRepositoryJob
{
    /// <summary>
    /// Dumps the repository of a tenant to a file and stores it in the distributed cache.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The cache key where the dump is stored</returns>
    [DisplayName("Dump repository of tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task<string?> Run(string tenantId, IBotCancellationToken? cancellationToken);
}