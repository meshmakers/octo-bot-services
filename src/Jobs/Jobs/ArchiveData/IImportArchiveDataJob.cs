using System.ComponentModel;
using Hangfire;
using Hangfire.Server;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Imports archive data rows from an uploaded export ZIP into a target archive, after a strict
///     schema-match validation. Mirrors <see cref="IRestoreRepositoryJob"/> (reads an uploaded TUS
///     file, cleans it up on completion). Archive data export/import concept (AB#4230) §5.1.
/// </summary>
public interface IImportArchiveDataJob
{
    /// <summary>
    ///     Validates and imports the rows of an uploaded export ZIP into the target archive.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the target archive.</param>
    /// <param name="archiveRtId">Runtime id of the target <c>CkArchive</c> entity.</param>
    /// <param name="tusFileId">
    ///     The tus file id of the uploaded export ZIP (AB#8736: staged in the artifact store, so any replica runs the
    ///     job). An upload received on the local disk before AB#8736 is still found by its id. A job enqueued before
    ///     AB#8736 carries the local path of the upload instead: it is imported when that file (inside the tenant's tus
    ///     directory) is on the replica that runs it, and fails with "upload again" otherwise.
    /// </param>
    /// <param name="mode">Insert-only or upsert.</param>
    /// <param name="cancellationToken">A cancellation token to abort the job.</param>
    [DisplayName("Import archive data '{1}' of tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)]
    Task Run(string tenantId, string archiveRtId, string tusFileId,
        ArchiveImportMode mode, IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Imports archive data for the asynchronous method <c>System.Bot/TenantRepository.ImportArchiveData-1</c>
    ///     (AB#8447). Same import as <see cref="Run" />; in addition the job reports the rows read as the job parameter
    ///     <c>OctoOperationProgress</c>, writes the row count as <c>OctoOperationResult</c> and runs <b>at most once</b>
    ///     (a requeued job and a job whose operation was ended are refused).
    /// </summary>
    /// <param name="tenantId">The tenant that owns the target archive.</param>
    /// <param name="archiveRtId">Runtime id of the target <c>CkArchive</c> entity.</param>
    /// <param name="tusFileId">The tus file id of the uploaded export ZIP.</param>
    /// <param name="mode">Insert-only or upsert.</param>
    /// <param name="operationId">The operation id of the method call that enqueued the job.</param>
    /// <param name="performContext">Injected by Hangfire; pass <c>null</c> when enqueuing.</param>
    /// <param name="cancellationToken">A cancellation token to abort the job.</param>
    [DisplayName("Import archive data '{1}' of tenant '{0}' (method operation {4})")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)]
    Task RunForOperation(string tenantId, string archiveRtId, string tusFileId, ArchiveImportMode mode,
        string operationId, PerformContext? performContext, IBotCancellationToken? cancellationToken);
}
