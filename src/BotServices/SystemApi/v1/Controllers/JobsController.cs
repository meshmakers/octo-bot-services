using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Hangfire;
using IdentityModel;
using Meshmakers.Octo.Backend.BotServices.Controllers;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers;

/// <summary>
///     REST Controller for job management
/// </summary>
/// <remarks>
///     🔴 <b>This controller holds job-<i>instance</i> operations only. Do not add a
///     tenant-addressed operation here.</b> The five that used to live here —
///     <c>run-fixup-scripts</c>, <c>restore-from-upload</c>, <c>dump-repository</c>,
///     <c>export-archive-data</c> and <c>import-archive-data-from-upload</c> — were removed in stage 3
///     of AB#5060. They took their tenant as the query parameter <c>?tenantId=…</c>, which the
///     transport tenant gate never sees, because it reads the route value. The replacements are the
///     identical operations on <see cref="TenantApi.v1.Controllers.JobsController" />
///     (<c>{tenantId}/v1/jobs/...</c>); both surfaces shared their implementation through
///     <see cref="JobsControllerBase" /> throughout the migration, so nothing about their behaviour
///     changed when the callers moved.
///     <para>
///         The removal was verified against the whole checkout first: no production caller was left.
///         The SDK stopped addressing them when its five job verbs moved to per-call tenant routes,
///         octo-cli and octo-mcp-service inherit that through the package, and the frontend builds
///         <c>{tenantId}/v1/jobs/…</c> itself. An external caller that still uses the old paths now
///         gets a 404 — deliberately, since a silent tenant-ungated path is the thing this epic set
///         out to remove.
///     </para>
///     <para>
///         🔴 <b>The three job-instance actions are gated in code, not by the middleware (AB#5070).</b>
///         <c>GET</c> by job id, <c>download</c> and <c>DELETE</c> address a Hangfire job id, which is
///         global to the instance — so the route carries no tenant segment and the transport gate
///         returns early on all three. "Unmarked" there means <b>unchecked</b>, not "exactly matched":
///         until AB#5070 the download resolved a result file from the job id alone and handed any
///         tenant's backup to any caller holding the job-read scope. Each of the three therefore
///         resolves the tenant the job belongs to and asks <see cref="IJobTenantAccessGuard" />, which
///         is a faithful port of the middleware's decision — <c>tenant_id</c> match, the parent-tenant
///         administration rule for user tokens only, the same staging options, fail closed.
///     </para>
///     <para>
///         <c>download</c> additionally exists on the tenant route
///         (<c>{tenantId}/v1/jobs/download?id=…</c>) since AB#5070, where the middleware does the
///         caller check and the endpoint only has to confirm ownership. <c>GET</c> and <c>DELETE</c>
///         deliberately stay System-only: they are instance operations on a job id, they return or
///         change nothing that a tenant route would scope better, and the guard already answers the
///         same question there.
///     </para>
/// </remarks>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class JobsController : JobsControllerBase
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="distributedCache"></param>
    /// <param name="backgroundJobClient"></param>
    /// <param name="backupFileStorage"></param>
    /// <param name="jobStorage"></param>
    /// <param name="tenantAccessGuard"></param>
    /// <param name="logger"></param>
    public JobsController(IDistributedCacheService distributedCache,
        IBackgroundJobClient backgroundJobClient, IBackupFileStorageService backupFileStorage,
        IJobStorageAccessor jobStorage, IJobTenantAccessGuard tenantAccessGuard,
        ILogger<JobsControllerBase> logger)
        : base(backgroundJobClient, backupFileStorage, jobStorage, tenantAccessGuard, distributedCache, logger)
    {
    }

    /// <summary>
    ///     Returns the job description of the given job id
    /// </summary>
    /// <param name="id">The job id</param>
    /// <returns></returns>
    /// <remarks>
    ///     Answers <c>403</c> when the job belongs to a tenant the caller was not issued a token for
    ///     and is not the administering parent of (AB#5070).
    /// </remarks>
    // GET: system/Jobs?id=abc
    [HttpGet]
    [Authorize(BotServiceConstants.JobApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(JobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Get([Required] string id)
    {
        return GetJobAsync(id, null);
    }

    /// <summary>
    ///     Downloads the job result as binary file
    /// </summary>
    /// <param name="tenantId">
    ///     🔴 <b>Ignored since AB#5070, kept only so existing callers keep compiling and calling.</b>
    ///     The artifact is resolved and authorized against the tenant the <i>job</i> belongs to, read
    ///     from the job's own stored arguments; a tenant supplied by the caller can never widen that.
    ///     Before AB#5070 this argument was consulted only by the legacy GridFS fallback, which is why
    ///     the endpoint handed out every on-disk artifact to every caller.
    /// </param>
    /// <param name="id">Job ID</param>
    /// <returns></returns>
    /// <remarks>
    ///     Deprecated in favour of <c>GET {tenantId}/v1/jobs/download?id=…</c> (AB#5070), which the
    ///     transport gate can see. This variant stays functional and now performs the equivalent check
    ///     itself, because leaving an active hole open until stage 3 of AB#5060 is not acceptable.
    /// </remarks>
    // GET: system/jobs/download?id=abc
    [HttpGet]
    [Route("download")]
    [Authorize(BotServiceConstants.JobApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(NotFoundErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> DownloadExportRtResult(string tenantId, string id)
    {
        return DownloadJobResultAsync(id, null);
    }

    // DELETE: system/Jobs/abc
    /// <summary>
    ///     Deletes a job
    /// </summary>
    /// <param name="id">The job id</param>
    /// <returns></returns>
    /// <remarks>
    ///     Answers <c>403</c> when the job belongs to a tenant the caller was not issued a token for
    ///     and is not the administering parent of (AB#5070) — an ungated delete would let any holder of
    ///     the job-write scope cancel another tenant's running restore.
    /// </remarks>
    [HttpDelete("{id}")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<IActionResult> Delete([Required] string id)
    {
        return DeleteJobAsync(id, null);
    }
}
