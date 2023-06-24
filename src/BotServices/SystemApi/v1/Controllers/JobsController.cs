using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.Storage.Monitoring;
using IdentityModel;
using Meshmakers.Octo.Backend.Common.ApiErrors;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
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
    private readonly IDistributedWithPubSubCache _distributedCache;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="distributedCache"></param>
    public JobsController(IDistributedWithPubSubCache distributedCache)
    {
        _distributedCache = distributedCache;
    }

    /// <summary>
    ///     Returns the job description of the given job id
    /// </summary>
    /// <param name="id">The job id</param>
    /// <returns></returns>
    // GET: system/Jobs?id=abc
    [HttpGet]
    [Authorize(BotServiceConstants.JobApiReadOnlyPolicy)]
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
        catch (Exception)
        {
            return BadRequest();
        }
    }

    /// <summary>
    ///     Downloads the job result as binary file
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <returns></returns>
    // POST: system/jobs/download?id=abc
    [HttpGet]
    [Route("download")]
    [Authorize(BotServiceConstants.JobApiReadOnlyPolicy)]
    public async Task<IActionResult> DownloadExportRtResult(string id)
    {
        try
        {
            var job = JobStorage.Current.GetMonitoringApi().SucceededJobs(0, int.MaxValue)
                .FirstOrDefault(x => x.Key == id)
                .Value;

            if (job.Result == null)
            {
                return BadRequest();
            }

            var key = (string)job.Result;
            var resultTuple = await GetResultStream(key.Replace("\"", ""));

            return new FileStreamResult(resultTuple.Item2, resultTuple.Item1);
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerError(e.Message));
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
    public IActionResult Delete([Required] string id)
    {
        var result = BackgroundJob.Delete(id);
        return Ok(result);
    }

    private JobDto CreateJobDto(string id, JobDetailsDto jobDetails)
    {
        var status = jobDetails.History.FirstOrDefault();

        var jobDto = new JobDto
        {
            Id = id,
            CreatedAt = jobDetails.CreatedAt ?? DateTime.MinValue,
            StateChangedAt = status?.CreatedAt,
            Status = status?.StateName,
            Reason = status?.Reason
        };
        return jobDto;
    }

    private async Task<Tuple<string, Stream>> GetResultStream(string key)
    {
        var cacheStream = await _distributedCache.GetCacheStreamAsync(key);
        if (cacheStream == null)
        {
            throw new JobFailedException("No value in distribute cache found.");
        }

        return new Tuple<string, Stream>(cacheStream.ContentType, new MemoryStream(cacheStream.Stream));
    }
}
