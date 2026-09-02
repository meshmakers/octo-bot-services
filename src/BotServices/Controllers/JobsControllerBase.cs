using System.Security.Claims;
using Hangfire;
using Hangfire.Storage.Monitoring;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.BotServices.Controllers;

/// <summary>
///     The tenant-addressed job operations of this service, in one place: repository dump, restore
///     from a tus upload, archive data export, archive data import and the fixup script run — plus the
///     three job-instance operations (status, artifact download, delete) that act on the job those
///     operations produced.
/// </summary>
/// <remarks>
///     <para>
///         AB#5060 — the five enqueueing operations are served on <b>two</b> route shapes: the
///         historical System API (<c>system/v1/jobs/...?tenantId=…</c>, see
///         <see cref="SystemApi.v1.Controllers.JobsController" />) and the tenant API
///         (<c>{tenantId}/v1/jobs/...</c>, see <see cref="TenantApi.v1.Controllers.JobsController" />).
///         Only the latter puts the tenant where the transport tenant gate can see it — the gate reads
///         the <b>route value</b>, so a tenant travelling as a query argument is never checked.
///     </para>
///     <para>
///         🔴 <b>AB#5070 — the job instance is bound to its tenant here.</b> The artifact download used
///         to resolve a result file from the job id alone, so any caller holding the job-read scope
///         could fetch any tenant's backup, and the <c>tenantId</c> argument it was handed was only
///         ever used for the legacy GridFS fallback. Every job-instance operation below therefore
///         first resolves the tenant the job <i>belongs to</i> (<see cref="JobTenantBinding" />, read
///         off the job's own stored arguments) and then authorizes against it:
///     </para>
///     <list type="bullet">
///         <item>
///             on a <b>tenant route</b> the job must belong to the route tenant — the middleware has
///             already decided whether the caller may address that tenant, parent-tenant
///             administration included;
///         </item>
///         <item>
///             on the <b>System route</b>, which has no route tenant and is therefore never seen by the
///             middleware, <see cref="IJobTenantAccessGuard" /> performs the very check the middleware
///             would have performed, ancestor rule and staging options included.
///         </item>
///     </list>
///     <para>
///         The bodies live here rather than being duplicated per controller so the two surfaces cannot
///         drift: the tenant route must enqueue the very same Hangfire job with the very same
///         arguments as the system route, and — since AB#5070 — must reach the very same artifact under
///         the very same rules.
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
    private readonly IDistributedCacheService _distributedCache;
    private readonly IJobStorageAccessor _jobStorage;
    private readonly ILogger<JobsControllerBase> _logger;
    private readonly IJobTenantAccessGuard _tenantAccessGuard;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="backgroundJobClient">Hangfire client used to enqueue the jobs.</param>
    /// <param name="backupFileStorage">Storage service resolving tus upload file paths.</param>
    /// <param name="jobStorage">Reads job details and writes job parameters (AB#5070).</param>
    /// <param name="tenantAccessGuard">Authorizes a job instance against its tenant (AB#5070).</param>
    /// <param name="distributedCache">Backing store of the legacy GridFS artifact fallback.</param>
    /// <param name="logger">Logger.</param>
    protected JobsControllerBase(IBackgroundJobClient backgroundJobClient,
        IBackupFileStorageService backupFileStorage,
        IJobStorageAccessor jobStorage,
        IJobTenantAccessGuard tenantAccessGuard,
        IDistributedCacheService distributedCache,
        ILogger<JobsControllerBase> logger)
    {
        _backgroundJobClient = backgroundJobClient;
        _backupFileStorage = backupFileStorage;
        _jobStorage = jobStorage;
        _tenantAccessGuard = tenantAccessGuard;
        _distributedCache = distributedCache;
        _logger = logger;
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

            RecordStarter(id, tenantId);
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

            RecordStarter(id, tenantId);
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

            RecordStarter(id, tenantId);
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

            RecordStarter(id, tenantId);
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

            RecordStarter(id, tenantId);
            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    ///     Returns the job description of <paramref name="id" />, if the caller may see that job.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="routeTenantId">
    ///     The tenant taken from the route, or <c>null</c> on a surface that carries none.
    /// </param>
    /// <remarks>
    ///     A job status is less than an artifact, but it is not nothing: it carries the state and the
    ///     failure message of the job, and a failure message of a restore or an archive export names
    ///     database names, file names and archive ids of the tenant it ran for. Hangfire job ids are
    ///     Mongo ObjectIds and therefore partly guessable, so the status is gated with exactly the same
    ///     rule as the artifact (AB#5070).
    /// </remarks>
    protected async Task<IActionResult> GetJobAsync(string id, string? routeTenantId)
    {
        try
        {
            var jobDetails = _jobStorage.GetJobDetails(id);
            if (jobDetails == null)
            {
                return NotFound();
            }

            var denial = await AuthorizeJobAsync(id, jobDetails, routeTenantId);
            if (denial.Denied != null)
            {
                return denial.Denied;
            }

            return Ok(CreateJobDto(id, jobDetails));
        }
        catch (Exception ex)
        {
            return BadRequest(new InternalServerErrorDto(ex.Message));
        }
    }

    /// <summary>
    ///     Streams the artifact the job produced, if the caller may see that job.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="routeTenantId">
    ///     The tenant taken from the route, or <c>null</c> on a surface that carries none.
    /// </param>
    /// <remarks>
    ///     🔴 The legacy GridFS fallback is looked up under the tenant <b>the job belongs to</b>, never
    ///     under a tenant the caller supplied. Before AB#5070 the caller-supplied <c>tenantId</c> was
    ///     the only place that argument was used at all, which is why the new-path (file on disk)
    ///     artifacts were reachable by anyone: nothing in the lookup ever consulted a tenant.
    /// </remarks>
    protected async Task<IActionResult> DownloadJobResultAsync(string id, string? routeTenantId)
    {
        try
        {
            var jobDetails = _jobStorage.GetJobDetails(id);

            if (jobDetails == null)
            {
                return NotFound(new NotFoundErrorDto("No job found with id: " + id));
            }

            var (denied, jobTenantId) = await AuthorizeJobAsync(id, jobDetails, routeTenantId);
            if (denied != null)
            {
                return denied;
            }

            // Check if the job is in a final state
            var status = jobDetails.History.FirstOrDefault();
            if (status is { StateName: "Deleted" })
            {
                status = jobDetails.History.Skip(1).FirstOrDefault();

                if (status?.StateName == "Failed")
                {
                    var errorMessage = status.Data.TryGetValue("ExceptionMessage", out var value) ? value : null;
                    return BadRequest(
                        new InternalServerErrorDto("The job with id: " + id + " has failed: " + errorMessage));
                }

                return BadRequest(new InternalServerErrorDto("The job with id: " + id + " has been deleted at " +
                                                             status?.CreatedAt + ". " +
                                                             "Please check the job status and server logs and try again."));
            }

            if (status?.StateName == "Failed")
            {
                var errorMessage = status.Data.TryGetValue("ExceptionMessage", out var value) ? value : null;
                return BadRequest(
                    new InternalServerErrorDto("The job with id: " + id + " has failed: " + errorMessage));
            }

            if (status?.StateName == "Succeeded")
            {
                if (!status.Data.TryGetValue("Result", out var result))
                {
                    return NotFound(new NotFoundErrorDto("No result found for the job with id: " + id));
                }

                var key = result.Replace("\"", "");

                // New path: result is a file path on disk
                if (System.IO.File.Exists(key))
                {
                    var fileStream = new FileStream(key, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return new FileStreamResult(fileStream, "application/gzip")
                    {
                        FileDownloadName = Path.GetFileName(key),
                        EnableRangeProcessing = true
                    };
                }

                // Fallback: result is a GridFS cache key (backward compatibility)
                var resultTuple = await GetResultStream(jobTenantId!, key);

                return new FileStreamResult(resultTuple.Item2, resultTuple.Item1);
            }

            return BadRequest(new InternalServerErrorDto("The job with id: " + id + " is not in a final state."));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    ///     Deletes the job <paramref name="id" />, if the caller may address that job.
    /// </summary>
    /// <param name="id">The job id.</param>
    /// <param name="routeTenantId">
    ///     The tenant taken from the route, or <c>null</c> on a surface that carries none.
    /// </param>
    /// <remarks>
    ///     Deleting is a mutation, so it is gated for a stronger reason than the status read: an
    ///     ungated delete lets any holder of the job-write scope cancel another tenant's running
    ///     restore (AB#5070).
    /// </remarks>
    protected async Task<IActionResult> DeleteJobAsync(string id, string? routeTenantId)
    {
        try
        {
            var jobDetails = _jobStorage.GetJobDetails(id);
            if (jobDetails == null)
            {
                // Unchanged answer for an unknown job: Hangfire's own Delete reports "nothing was
                // deleted" rather than failing, and callers were built against that.
                return Ok(false);
            }

            var denial = await AuthorizeJobAsync(id, jobDetails, routeTenantId);
            if (denial.Denied != null)
            {
                return denial.Denied;
            }

            // Same state transition as Hangfire's static BackgroundJob.Delete, but through the
            // injected client — the static facade reads JobStorage.Current, which no test can hold.
            var result = _backgroundJobClient.Delete(id);
            return Ok(result);
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    ///     Resolves the tenant of <paramref name="jobDetails" /> and authorizes the caller against it.
    /// </summary>
    /// <returns>
    ///     The refusal to return to the caller (<c>403 Forbidden</c>), or <c>null</c> together with the
    ///     tenant the job belongs to when the access is allowed.
    /// </returns>
    private async Task<(IActionResult? Denied, string? JobTenantId)> AuthorizeJobAsync(string id,
        JobDetailsDto jobDetails, string? routeTenantId)
    {
        var jobTenantId = JobTenantBinding.TryResolveTenantId(jobDetails.Job);

        var allowed = routeTenantId == null
            ? await _tenantAccessGuard.MayAccessJobAsync(User, jobTenantId, id)
            : _tenantAccessGuard.IsJobOfTenant(routeTenantId, jobTenantId, id);

        // A bare 403, exactly like the transport gate: the refusal must not say whether the job
        // exists, which tenant it belongs to, or which of the two reasons applied.
        return allowed
            ? (null, jobTenantId)
            : (StatusCode(StatusCodes.Status403Forbidden), jobTenantId);
    }

    /// <summary>
    ///     Records who started the job, as Hangfire job parameters, best effort.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>Not an authorization input.</b> The binding that decides access is the tenant, read
    ///     from the job's arguments (<see cref="JobTenantBinding" />) — binding an artifact to the
    ///     starting subject would lock out a second administrator of the same tenant and make a dump
    ///     started by CI unreachable for every human. These parameters exist so that a later, finer
    ///     rule (a "only the starter may fetch it" mode, an audit answer to "who took this backup")
    ///     does not need a data migration for jobs that already ran. A failure to write them must never
    ///     fail the enqueue that already succeeded — the job exists at this point, and its id has to
    ///     reach the caller.
    /// </remarks>
    private void RecordStarter(string jobId, string tenantId)
    {
        try
        {
            var subject = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(subject))
            {
                _jobStorage.SetJobParameter(jobId, JobTenantBinding.StartedBySubjectParameter, subject);
            }

            var clientId = User.FindFirstValue("client_id");
            if (!string.IsNullOrEmpty(clientId))
            {
                _jobStorage.SetJobParameter(jobId, JobTenantBinding.StartedByClientIdParameter, clientId);
            }

            _jobStorage.SetJobParameter(jobId, JobTenantBinding.StartedForTenantParameter, tenantId);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e,
                "Could not record the starting subject of job '{JobId}' for tenant '{TenantId}'; the job " +
                "itself was enqueued (AB#5070)",
                jobId, tenantId);
        }
    }

    private static JobDto CreateJobDto(string id, JobDetailsDto jobDetails)
    {
        string? errorMessage = null;
        var status = jobDetails.History.FirstOrDefault();
        if (status is { StateName: "Deleted" })
        {
            status = jobDetails.History.Skip(1).FirstOrDefault();
        }

        if (status is { StateName: "Failed" } && status.Data.TryGetValue("ExceptionMessage", out var value))
        {
            errorMessage = value;
        }

        var jobDto = new JobDto
        {
            Id = id,
            CreatedAt = jobDetails.CreatedAt ?? DateTime.MinValue,
            StateChangedAt = status?.CreatedAt,
            Status = status?.StateName,
            Reason = status?.Reason,
            ErrorMessage = errorMessage
        };
        return jobDto;
    }

    private async Task<Tuple<string, Stream>> GetResultStream(string tenantId, string key)
    {
        var cacheStream = await _distributedCache.GetCacheStreamByIdAsync(tenantId, key);
        if (cacheStream == null)
        {
            throw new JobFailedException("No value in distribute cache found.");
        }

        return new Tuple<string, Stream>(cacheStream.ContentType, cacheStream.Stream);
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
