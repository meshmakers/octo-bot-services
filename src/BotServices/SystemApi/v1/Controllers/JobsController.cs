using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Hangfire;
using Hangfire.Storage.Monitoring;
using IdentityModel;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.DTOs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
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

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="distributedCache"></param>
    /// <param name="systemContext"></param>
    /// <param name="backgroundJobClient"></param>
    public JobsController(IDistributedCacheService distributedCache, ISystemContext systemContext,
        IBackgroundJobClient backgroundJobClient)
    {
        _distributedCache = distributedCache;
        _systemContext = systemContext;
        _backgroundJobClient = backgroundJobClient;
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
    /// Restores the repository for the given tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="databaseName">The name of the database to restore</param>
    /// <param name="file">The file with the gzipped file</param>
    /// <param name="oldDatabaseName">Optional parameter. To be used, when the new db name does not match the original one.</param>
    /// <returns></returns>
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

                var key = result;
                var resultTuple = await GetResultStream(tenantId, key.Replace("\"", ""));

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
    /// Compares two live tenants
    /// </summary>
    /// <param name="request">Comparison request containing source and target tenant IDs and options</param>
    /// <param name="tenantId">Tenant ID for cache operations</param>
    /// <returns></returns>
    [HttpPost]
    [Route("compare-live-tenants")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult CompareLiveTenants([FromBody] CompareLiveTenantsRequest request, [FromQuery] string tenantId)
    {
        try
        {
            var id = _backgroundJobClient.Enqueue<ICompareLiveTenantsJob>(job =>
                job.Run(tenantId, request.SourceTenantId, request.TargetTenantId, request.Options, BotCancellationToken.Null));

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
    /// Compares a live tenant with a backup archive
    /// </summary>
    /// <param name="request">Comparison request containing tenant ID, backup file and options</param>
    /// <param name="tenantId">Tenant ID for cache operations</param>
    /// <returns></returns>
    [HttpPost]
    [RequestSizeLimit(300_000_000)]
    [Route("compare-tenant-with-backup")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompareTenantWithBackupAsync([FromForm] CompareTenantWithBackupRequest request, [FromQuery] string tenantId)
    {
        try
        {
            var cacheKey = await AddFileToCache(tenantId, request.BackupFile);

            var id = _backgroundJobClient.Enqueue<ICompareLiveTenantWithBackupJob>(job =>
                job.Run(tenantId, request.TenantId, cacheKey, request.Options, BotCancellationToken.Null));

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
    /// Compares two backup archives
    /// </summary>
    /// <param name="request">Comparison request containing source and target backup files and options</param>
    /// <param name="tenantId">Tenant ID for cache operations</param>
    /// <returns></returns>
    [HttpPost]
    [RequestSizeLimit(300_000_000)]
    [Route("compare-backups")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompareBackupsAsync([FromForm] CompareBackupsRequest request, [FromQuery] string tenantId)
    {
        try
        {
            var sourceCacheKey = await AddFileToCache(tenantId, request.SourceBackupFile);
            var targetCacheKey = await AddFileToCache(tenantId, request.TargetBackupFile);

            var id = _backgroundJobClient.Enqueue<ICompareBackupsJob>(job =>
                job.Run(tenantId, sourceCacheKey, targetCacheKey, request.Options, BotCancellationToken.Null));

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
    /// Compares a backup archive with a live tenant
    /// </summary>
    /// <param name="request">Comparison request containing backup file, tenant ID and options</param>
    /// <param name="tenantId">Tenant ID for cache operations</param>
    /// <returns></returns>
    [HttpPost]
    [RequestSizeLimit(300_000_000)]
    [Route("compare-backup-with-live-tenant")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompareBackupWithLiveTenantAsync([FromForm] CompareBackupWithLiveTenantRequest request, [FromQuery] string tenantId)
    {
        try
        {
            var cacheKey = await AddFileToCache(tenantId, request.BackupFile);

            var id = _backgroundJobClient.Enqueue<ICompareBackupWithLiveTenantJob>(job =>
                job.Run(tenantId, cacheKey, request.TenantId, request.Options, BotCancellationToken.Null));

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