using System.ComponentModel;
using Hangfire;

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
    /// <param name="accessToken">The operator's bearer token, forwarded so the bot can call asset-repo.</param>
    /// <param name="fromUtc">Inclusive lower bound of the export window (UTC); null for whole archive.</param>
    /// <param name="toUtc">Exclusive upper bound of the export window (UTC); null for whole archive.</param>
    /// <param name="cancellationToken">A cancellation token to abort the job.</param>
    /// <returns>The path of the produced ZIP file.</returns>
    [DisplayName("Export archive data '{1}' of tenant '{0}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)]
    Task<string?> Run(string tenantId, string archiveRtId, string accessToken, DateTime? fromUtc, DateTime? toUtc,
        IBotCancellationToken? cancellationToken);
}
