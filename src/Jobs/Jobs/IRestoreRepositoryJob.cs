using System.ComponentModel;
using Hangfire;
using Hangfire.Server;

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
    /// <param name="performContext">
    /// Supplied by Hangfire (pass <c>null</c>); its job id is the run id of the post-restore secret sweep in
    /// the tenant's run history (AB#5544).
    /// </param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>
    /// The restore result including the post-restore secret sweep report and the secrets to re-enter
    /// (AB#5539); <c>null</c> when the system tenant does not exist.
    /// </returns>
    [DisplayName("Restore repository '{1}' using tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task<RestoreRepositoryResult?> Run(string tenantId, string databaseName, string cacheKey, string? oldDatabaseName, bool restoreArchiveData, PerformContext? performContext, IBotCancellationToken? cancellationToken);

    /// <summary>
    /// Restores a repository like <see cref="Run(string, string, string, string?, bool, PerformContext?, IBotCancellationToken?)" />,
    /// with the deliberate administrator override of the cross-instance ownership guard (AB#8672). The overload
    /// above stays for restore jobs enqueued before it existed and runs without the override.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="databaseName">The name of the database to restore</param>
    /// <param name="cacheKey">The cache key the file to restore is stored</param>
    /// <param name="oldDatabaseName">The database name the backup was taken from, when it differs</param>
    /// <param name="restoreArchiveData">See the overload above.</param>
    /// <param name="overrideForeignOwnership">
    /// When <c>true</c>, the restore overwrites a tenant database that belongs to another OctoMesh instance on the
    /// same MongoDB server instead of refusing it; the override is logged as a warning naming the owner. Another
    /// instance's system database is never overwritten.
    /// </param>
    /// <param name="performContext">Supplied by Hangfire (pass <c>null</c>).</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>See the overload above.</returns>
    [DisplayName("Restore repository '{1}' using tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task<RestoreRepositoryResult?> Run(string tenantId, string databaseName, string cacheKey, string? oldDatabaseName, bool restoreArchiveData, bool overrideForeignOwnership, PerformContext? performContext, IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Restores the repository of a tenant for the asynchronous method <c>System.Bot/TenantRepository.Restore-1</c>
    ///     (AB#8447). Same restore as <see cref="Run(string, string, string, string?, bool, PerformContext?, IBotCancellationToken?)" />; in addition the job reports its progress as the job parameter
    ///     <c>OctoOperationProgress</c>, writes a count summary as <c>OctoOperationResult</c>, and runs <b>at most once</b>:
    ///     a second start of the same job (Hangfire requeues a job whose server stopped or died) and a start after the
    ///     operation was ended (lost handler) are refused without touching the tenant database.
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="databaseName">The name of the database to restore</param>
    /// <param name="tusFileId">The tus file id of the uploaded backup</param>
    /// <param name="oldDatabaseName">See <see cref="Run(string, string, string, string?, bool, PerformContext?, IBotCancellationToken?)" />.</param>
    /// <param name="restoreArchiveData">See <see cref="Run(string, string, string, string?, bool, PerformContext?, IBotCancellationToken?)" />.</param>
    /// <param name="operationId">The operation id of the method call that enqueued the job.</param>
    /// <param name="performContext">Injected by Hangfire; pass <c>null</c> when enqueuing.</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The restore result, as <see cref="Run(string, string, string, string?, bool, PerformContext?, IBotCancellationToken?)" />.</returns>
    [DisplayName("Restore repository '{1}' using tenant '{0}' (method operation {5})")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task<RestoreRepositoryResult?> RunForOperation(string tenantId, string databaseName, string tusFileId,
        string? oldDatabaseName, bool restoreArchiveData, string operationId, PerformContext? performContext,
        IBotCancellationToken? cancellationToken);
}
