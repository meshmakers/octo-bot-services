using System.ComponentModel;
using Hangfire;
using Hangfire.Server;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Exports the data rows of an archive to a downloadable ZIP (<c>metadata.json</c> +
///     <c>data.ndjson</c>). Mirrors <see cref="IDumpRepositoryJob"/>. Archive data export/import
///     concept (AB#4230) §5.1.
/// </summary>
public interface IExportArchiveDataJob
{
    /// <summary>
    ///     Produces the export ZIP and returns its on-disk path (registered as the job's downloadable
    ///     result, exactly like the repository dump).
    /// </summary>
    /// <param name="tenantId">The tenant that owns the archive.</param>
    /// <param name="archiveRtId">Runtime id of the <c>CkArchive</c> entity.</param>
    /// <param name="fromUtc">Inclusive lower bound of the export window (UTC); null for whole archive.</param>
    /// <param name="toUtc">Exclusive upper bound of the export window (UTC); null for whole archive.</param>
    /// <param name="cancellationToken">A cancellation token to abort the job.</param>
    /// <returns>The path of the produced ZIP file.</returns>
    [DisplayName("Export archive data '{1}' of tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)]
    Task<string?> Run(string tenantId, string archiveRtId, DateTime? fromUtc, DateTime? toUtc,
        IBotCancellationToken? cancellationToken);

    /// <summary>
    ///     Exports the archive data for the asynchronous method <c>System.Bot/TenantRepository.ExportArchiveData-1</c>
    ///     (AB#8447). Same export as <see cref="Run" />; in addition the job reports the rows written as the job parameter
    ///     <c>OctoOperationProgress</c>, writes the row count as <c>OctoOperationResult</c> and runs <b>at most once</b>
    ///     (a requeued job and a job whose operation was ended are refused).
    /// </summary>
    /// <param name="tenantId">The tenant that owns the archive.</param>
    /// <param name="archiveRtId">Runtime id of the <c>CkArchive</c> entity.</param>
    /// <param name="fromUtc">See <see cref="Run" />.</param>
    /// <param name="toUtc">See <see cref="Run" />.</param>
    /// <param name="operationId">The operation id of the method call that enqueued the job.</param>
    /// <param name="performContext">Injected by Hangfire; pass <c>null</c> when enqueuing.</param>
    /// <param name="cancellationToken">A cancellation token to abort the job.</param>
    /// <returns>The path of the produced ZIP file (the job's downloadable result).</returns>
    [DisplayName("Export archive data '{1}' of tenant '{0}' (method operation {4})")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)]
    Task<string?> RunForOperation(string tenantId, string archiveRtId, DateTime? fromUtc, DateTime? toUtc,
        string operationId, PerformContext? performContext, IBotCancellationToken? cancellationToken);
}
