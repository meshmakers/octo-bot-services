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
    /// <param name="oldDatabaseName"></param>
    /// <param name="restoreArchiveData">
    /// When <c>true</c> and the uploaded artifact is an <c>.octobak.zip</c> carrying archive data, the
    /// tenant's CrateDB archives are also restored via the clean per-archive sequence (concept AB#4231
    /// §5/§5.1). When <c>false</c> (default), only the Mongo database is restored; archives are left
    /// untouched. A legacy <c>.tar.gz</c> always restores Mongo only.
    /// </param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    [DisplayName("Restore repository '{1}' using tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task Run(string tenantId, string databaseName, string cacheKey, string? oldDatabaseName, bool restoreArchiveData, IBotCancellationToken? cancellationToken);
}