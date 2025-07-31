using System.ComponentModel;
using Hangfire;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Run a repository restore
/// </summary>
public interface IRestoreRepositoryJob
{
    /// <summary>
    /// Restores a repository from a file stored in the distributed cache.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="databaseName">The name of the database to restore</param>
    /// <param name="cacheKey">The cache key the file to restore is stored</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    [DisplayName("Restore repository '{1}' using tenant '{0}'")]
    [AutomaticRetry(Attempts = 0)]
    Task Run(string tenantId, string databaseName, string cacheKey, IBotCancellationToken? cancellationToken);
}