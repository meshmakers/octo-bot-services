using Hangfire;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.BotServices.Controllers;

/// <summary>
///     The tenant-addressed job operations of this service, in one place: repository dump, restore
///     from a tus upload, archive data export, archive data import and the fixup script run.
/// </summary>
/// <remarks>
///     <para>
///         AB#5060 — these five operations are served on <b>two</b> route shapes: the historical
///         System API (<c>system/v1/jobs/...?tenantId=…</c>, see
///         <see cref="SystemApi.v1.Controllers.JobsController" />) and the tenant API
///         (<c>{tenantId}/v1/jobs/...</c>, see <see cref="TenantApi.v1.Controllers.JobsController" />).
///         Only the latter puts the tenant where the transport tenant gate can see it — the gate reads
///         the <b>route value</b>, so a tenant travelling as a query argument is never checked.
///     </para>
///     <para>
///         The bodies live here rather than being duplicated per controller so the two surfaces cannot
///         drift: the tenant route must enqueue the very same Hangfire job with the very same
///         arguments as the system route, which is what makes the System API safe to keep as a
///         deprecated fallback until every caller has moved (stage 3 of AB#5060).
///     </para>
///     <para>
///         The class is <c>abstract</c> and its operations are <c>protected</c>, so MVC's controller
///         discovery ignores it and none of these methods becomes an action of its own.
///     </para>
/// </remarks>
public abstract class JobsControllerBase : ControllerBase
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IBackupFileStorageService _backupFileStorage;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="backgroundJobClient">Hangfire client used to enqueue the jobs.</param>
    /// <param name="backupFileStorage">Storage service resolving tus upload file paths.</param>
    protected JobsControllerBase(IBackgroundJobClient backgroundJobClient,
        IBackupFileStorageService backupFileStorage)
    {
        _backgroundJobClient = backgroundJobClient;
        _backupFileStorage = backupFileStorage;
    }

    /// <summary>
    ///     Enqueues the fixup script run for <paramref name="tenantId" />.
    /// </summary>
    protected IActionResult EnqueueRunFixupScripts(string tenantId)
    {
        try
        {
            var id = _backgroundJobClient.Enqueue<IRunFixupJob>(job =>
                job.Run(tenantId, BotCancellationToken.Null));

            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(new InternalServerErrorDto(ex.Message));
        }
    }

    /// <summary>
    ///     Enqueues the repository restore of <paramref name="tenantId" /> from a completed tus upload.
    /// </summary>
    protected IActionResult EnqueueRestoreFromUpload(string tusFileId, string tenantId, string databaseName,
        string? oldDatabaseName, bool restoreArchiveData)
    {
        try
        {
            // Verify the tus upload file exists on disk and has content
            var uploadCheck = ValidateTusUpload(tusFileId, out _);
            if (uploadCheck != null)
            {
                return uploadCheck;
            }

            var id = _backgroundJobClient.Enqueue<IRestoreRepositoryJob>(job =>
                job.Run(tenantId, databaseName, tusFileId, oldDatabaseName, restoreArchiveData,
                    BotCancellationToken.Null));

            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    ///     Enqueues the repository dump of <paramref name="tenantId" />.
    /// </summary>
    protected IActionResult EnqueueDumpRepository(string tenantId, bool includeArchiveData)
    {
        try
        {
            var id = _backgroundJobClient.Enqueue<IDumpRepositoryJob>(job =>
                job.Run(tenantId, includeArchiveData, BotCancellationToken.Null));

            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    ///     Enqueues the archive data export of <paramref name="archiveRtId" /> owned by
    ///     <paramref name="tenantId" />.
    /// </summary>
    protected IActionResult EnqueueExportArchiveData(string tenantId, string archiveRtId, DateTime? fromUtc,
        DateTime? toUtc)
    {
        try
        {
            var id = _backgroundJobClient.Enqueue<IExportArchiveDataJob>(job =>
                job.Run(tenantId, archiveRtId, fromUtc, toUtc, BotCancellationToken.Null));

            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    ///     Enqueues the archive data import into <paramref name="archiveRtId" /> of
    ///     <paramref name="tenantId" /> from a completed tus upload.
    /// </summary>
    protected IActionResult EnqueueImportArchiveDataFromUpload(string tusFileId, string tenantId, string archiveRtId,
        ArchiveImportMode mode)
    {
        try
        {
            var uploadCheck = ValidateTusUpload(tusFileId, out var filePath);
            if (uploadCheck != null)
            {
                return uploadCheck;
            }

            var id = _backgroundJobClient.Enqueue<IImportArchiveDataJob>(job =>
                job.Run(tenantId, archiveRtId, filePath, mode, BotCancellationToken.Null));

            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    ///     Verifies that the tus upload exists on disk and is not empty.
    /// </summary>
    /// <param name="tusFileId">The tus file ID from the completed upload.</param>
    /// <param name="filePath">The resolved path of the uploaded file.</param>
    /// <returns>
    ///     <c>null</c> when the upload is usable, otherwise the error result to return to the caller.
    /// </returns>
    /// <remarks>
    ///     The tus upload itself carries no tenant (AB#5060): the upload sink is a tenant-neutral
    ///     staging area keyed by the tus file id, and the tenant is decided by the restore / import
    ///     call that consumes it. This check therefore only answers "is there a file", never "whose
    ///     file is it".
    /// </remarks>
    private IActionResult? ValidateTusUpload(string tusFileId, out string filePath)
    {
        filePath = _backupFileStorage.GetTusUploadFilePath(tusFileId);
        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new NotFoundErrorDto(
                $"Upload file not found for tus file ID '{tusFileId}'. Ensure the upload completed successfully."));
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length == 0)
        {
            return BadRequest(new InternalServerErrorDto(
                $"Upload file for tus file ID '{tusFileId}' is empty (0 bytes). The upload may not have completed successfully."));
        }

        return null;
    }
}
