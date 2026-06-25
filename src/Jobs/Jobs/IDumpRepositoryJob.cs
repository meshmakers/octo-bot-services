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
    /// <param name="includeArchiveData">
    /// When <c>true</c>, the tenant's CrateDB archive rows are bundled with the mongodump blob into an
    /// <c>.octobak.zip</c> container (concept AB#4231 §3/§4). When <c>false</c> (default), a single
    /// mongodump <c>.tar.gz</c> is produced exactly as before.
    /// </param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The path of the produced backup file (downloadable job result)</returns>
    [DisplayName("Dump repository of tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task<string?> Run(string tenantId, bool includeArchiveData, IBotCancellationToken? cancellationToken);
}