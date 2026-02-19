using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Asp.Versioning;
using Hangfire;
using Hangfire.Storage.Monitoring;
using IdentityModel;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers;

/// <summary>
///     REST Controller for job management
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class JobsController : ControllerBase
{
    private readonly IDistributedCacheService _distributedCache;
    private readonly ISystemContext _systemContext;
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly IBackupFileStorageService _backupFileStorage;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="distributedCache"></param>
    /// <param name="systemContext"></param>
    /// <param name="backgroundJobClient"></param>
    /// <param name="backupFileStorage"></param>
    public JobsController(IDistributedCacheService distributedCache, ISystemContext systemContext,
        IBackgroundJobClient backgroundJobClient, IBackupFileStorageService backupFileStorage)
    {
        _distributedCache = distributedCache;
        _systemContext = systemContext;
        _backgroundJobClient = backgroundJobClient;
        _backupFileStorage = backupFileStorage;
    }

    /// <summary>
    ///     Returns the job description of the given job id
    /// </summary>
    /// <param name="id">The job id</param>
    /// <returns></returns>
    // GET: system/Jobs?id=abc
    [HttpGet]
    [Authorize(BotServiceConstants.JobApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(JobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Get([Required] string id)
    {
        try
        {
            var jobDetails = JobStorage.Current.GetMonitoringApi().JobDetails(id);
            if (jobDetails == null)
            {
                return NotFound();
            }

            return Ok(CreateJobDto(id, jobDetails));
        }
        catch (Exception ex)
        {
            return BadRequest(new InternalServerErrorDto(ex.Message));
        }
    }

    /// <summary>
    /// Runs the fixup scripts for the given tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <returns></returns>
    // POST: system/jobs/run-fixup-scripts?tenantId=abc
    [HttpPost]
    [Route("run-fixup-scripts")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult RunFixupScripts(string tenantId)
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
    /// Restores the repository for the given tenant.
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="databaseName">The name of the database to restore</param>
    /// <param name="file">The file with the gzipped file</param>
    /// <param name="oldDatabaseName">Optional parameter. To be used, when the new db name does not match the original one.</param>
    /// <returns></returns>
    [Obsolete("Use restore-from-upload with tus resumable upload instead.")]
    [HttpPost]
    [RequestSizeLimit(300_000_000)]
    [Route("restore-repository")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RestoreRepositoryAsync(string tenantId, string databaseName, IFormFile file,
        string? oldDatabaseName = null)
    {
        try
        {
            var cacheKey = await AddFileToCache(_systemContext.TenantId, file);

            var id = _backgroundJobClient.Enqueue<IRestoreRepositoryJob>(job =>
                job.Run(tenantId, databaseName, cacheKey, oldDatabaseName, BotCancellationToken.Null));

            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    /// Restores the repository for the given tenant from a tus resumable upload.
    /// The file must have been uploaded via the tus endpoint at /system/v1/tus-upload first.
    /// </summary>
    /// <param name="tusFileId">The tus file ID from the completed upload.</param>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="databaseName">The name of the database to restore.</param>
    /// <param name="oldDatabaseName">Optional parameter. To be used, when the new db name does not match the original one.</param>
    /// <returns></returns>
    [HttpPost]
    [Route("restore-from-upload")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult RestoreFromUpload(
        [Required] string tusFileId,
        [Required] string tenantId,
        [Required] string databaseName,
        string? oldDatabaseName = null)
    {
        try
        {
            // Verify the tus upload file exists on disk
            var filePath = _backupFileStorage.GetTusUploadFilePath(tusFileId);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new NotFoundErrorDto(
                    $"Upload file not found for tus file ID '{tusFileId}'. Ensure the upload completed successfully."));
            }

            var id = _backgroundJobClient.Enqueue<IRestoreRepositoryJob>(job =>
                job.Run(tenantId, databaseName, tusFileId, oldDatabaseName, BotCancellationToken.Null));

            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    /// Dumps the repository for the given tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <returns></returns>
    [HttpPost]
    [Route("dump-repository")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult DumpRepository(string tenantId)
    {
        try
        {
            var id = _backgroundJobClient.Enqueue<IDumpRepositoryJob>(job =>
                job.Run(tenantId, BotCancellationToken.Null));

            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    ///     Downloads the job result as binary file
    /// </summary>
    /// <param name="tenantId">Corresponding tenant id, null if system tenant is used.</param>
    /// <param name="id">Job ID</param>
    /// <returns></returns>
    // POST: system/jobs/download?id=abc
    [HttpGet]
    [Route("download")]
    [Authorize(BotServiceConstants.JobApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(NotFoundErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> DownloadExportRtResult(string tenantId, string id)
    {
        try
        {
            var jobDetails = JobStorage.Current.GetMonitoringApi().JobDetails(id);

            if (jobDetails == null)
            {
                return NotFound(new NotFoundErrorDto("No job found with id: " + id));
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
                var resultTuple = await GetResultStream(tenantId, key);

                return new FileStreamResult(resultTuple.Item2, resultTuple.Item1);
            }

            return BadRequest(new InternalServerErrorDto("The job with id: " + id + " is not in a final state."));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    // DELETE: system/Jobs/abc
    /// <summary>
    ///     Deletes a job
    /// </summary>
    /// <param name="id">The job id</param>
    /// <returns></returns>
    [HttpDelete("{id}")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Delete([Required] string id)
    {
        try
        {
            var result = BackgroundJob.Delete(id);
            return Ok(result);
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    private JobDto CreateJobDto(string id, JobDetailsDto jobDetails)
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

    private async Task<string> AddFileToCache(string tenantId, IFormFile file)
    {
        await using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream);
        memoryStream.Position = 0;
        var key = await _distributedCache.CreateStreamAsync(tenantId, memoryStream, file.ContentType, file.FileName,
            TimeSpan.FromHours(1));
        return key;
    }
}