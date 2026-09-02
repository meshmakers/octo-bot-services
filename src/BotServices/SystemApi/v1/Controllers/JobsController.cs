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
///     🔴 <b>The five tenant-addressed operations here are deprecated (AB#5060).</b>
///     <c>run-fixup-scripts</c>, <c>restore-from-upload</c>, <c>dump-repository</c>,
///     <c>export-archive-data</c> and <c>import-archive-data-from-upload</c> take their tenant as the
///     query parameter <c>?tenantId=…</c>, which the transport tenant gate never sees — it reads the
///     route value. Their replacements are the identical operations on
///     <see cref="TenantApi.v1.Controllers.JobsController" /> (<c>{tenantId}/v1/jobs/...</c>), which
///     share their implementation through <see cref="JobsControllerBase" /> and therefore behave
///     identically. They stay here, unchanged and functional, only until every caller (SDK, octo-cli,
///     octo-mcp-service, Studio) has moved — stage 3 of AB#5060 removes them. Do not add a new
///     tenant-addressed operation to this controller.
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
    /// Runs the fixup scripts for the given tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <returns></returns>
    /// <remarks>
    ///     Deprecated (AB#5060) — use <c>POST {tenantId}/v1/jobs/run-fixup-scripts</c>. The tenant is a
    ///     query parameter here, so the transport tenant gate cannot check it.
    /// </remarks>
    // POST: system/jobs/run-fixup-scripts?tenantId=abc
    [HttpPost]
    [Route("run-fixup-scripts")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult RunFixupScripts(string tenantId)
    {
        return EnqueueRunFixupScripts(tenantId);
    }

    /// <summary>
    /// Restores the repository for the given tenant from a tus resumable upload.
    /// The file must have been uploaded via the tus endpoint at /system/v1/tus-upload first.
    /// </summary>
    /// <param name="tusFileId">The tus file ID from the completed upload.</param>
    /// <param name="tenantId">The tenant id.</param>
    /// <param name="databaseName">The name of the database to restore.</param>
    /// <param name="oldDatabaseName">Optional parameter. To be used, when the new db name does not match the original one.</param>
    /// <param name="restoreArchiveData">When <c>true</c> and the uploaded artifact is an <c>.octobak.zip</c> carrying archive data, the tenant's CrateDB archives are also restored (concept AB#4231). Default <c>false</c> (Mongo only).</param>
    /// <returns></returns>
    /// <remarks>
    ///     Deprecated (AB#5060) — use <c>POST {tenantId}/v1/jobs/restore-from-upload</c>. The tenant is a
    ///     query parameter here, so the transport tenant gate cannot check it.
    /// </remarks>
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
        string? oldDatabaseName = null,
        [FromQuery] bool restoreArchiveData = false)
    {
        return EnqueueRestoreFromUpload(tusFileId, tenantId, databaseName, oldDatabaseName, restoreArchiveData);
    }

    /// <summary>
    /// Dumps the repository for the given tenant
    /// </summary>
    /// <param name="tenantId">The tenant id</param>
    /// <param name="includeArchiveData">
    /// When <c>true</c>, the tenant's CrateDB archive rows are bundled with the mongodump blob into an
    /// <c>.octobak.zip</c> container (concept AB#4231). When <c>false</c> (default), a single mongodump
    /// <c>.tar.gz</c> is produced exactly as before.
    /// </param>
    /// <returns></returns>
    /// <remarks>
    ///     Deprecated (AB#5060) — use <c>POST {tenantId}/v1/jobs/dump-repository</c>. The tenant is a
    ///     query parameter here, so the transport tenant gate cannot check it.
    /// </remarks>
    [HttpPost]
    [Route("dump-repository")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult DumpRepository(string tenantId, [FromQuery] bool includeArchiveData = false)
    {
        return EnqueueDumpRepository(tenantId, includeArchiveData);
    }

    /// <summary>
    /// Exports the data rows of an archive to a downloadable ZIP (AB#4230). The produced ZIP is
    /// registered as the job's downloadable result and retrieved via the existing
    /// <c>jobs/download</c> endpoint. When both <paramref name="fromUtc"/> and <paramref name="toUtc"/>
    /// are omitted the whole archive is exported; when supplied, only rows in the half-open window
    /// <c>[fromUtc, toUtc)</c> are exported.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the archive.</param>
    /// <param name="archiveRtId">Runtime id of the <c>CkArchive</c> entity.</param>
    /// <param name="fromUtc">Optional inclusive lower bound of the export window (ISO-8601 UTC).</param>
    /// <param name="toUtc">Optional exclusive upper bound of the export window (ISO-8601 UTC).</param>
    /// <remarks>
    ///     Deprecated (AB#5060) — use <c>POST {tenantId}/v1/jobs/export-archive-data</c>. The tenant is a
    ///     query parameter here, so the transport tenant gate cannot check it.
    /// </remarks>
    [HttpPost]
    [Route("export-archive-data")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ExportArchiveData(
        [Required] string tenantId,
        [Required] string archiveRtId,
        DateTime? fromUtc = null,
        DateTime? toUtc = null)
    {
        return EnqueueExportArchiveData(tenantId, archiveRtId, fromUtc, toUtc);
    }

    /// <summary>
    /// Imports archive data rows into the given archive from a tus resumable upload (AB#4230). The
    /// export ZIP must have been uploaded via the tus endpoint at <c>/system/v1/tus-upload</c> first.
    /// Schema-match validation runs inside the job; on mismatch the job ends Failed with a
    /// field-level reason surfaced through <c>jobs/{id}</c>.
    /// </summary>
    /// <param name="tusFileId">The tus file ID from the completed upload.</param>
    /// <param name="tenantId">The tenant that owns the target archive.</param>
    /// <param name="archiveRtId">Runtime id of the target <c>CkArchive</c> entity.</param>
    /// <param name="mode">Import mode (<c>InsertOnly</c>/<c>Upsert</c>; binds from <c>0</c>/<c>1</c> or the name).</param>
    /// <remarks>
    ///     Deprecated (AB#5060) — use <c>POST {tenantId}/v1/jobs/import-archive-data-from-upload</c>. The
    ///     tenant is a query parameter here, so the transport tenant gate cannot check it.
    /// </remarks>
    [HttpPost]
    [Route("import-archive-data-from-upload")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult ImportArchiveDataFromUpload(
        [Required] string tusFileId,
        [Required] string tenantId,
        [Required] string archiveRtId,
        [FromQuery] ArchiveImportMode mode = ArchiveImportMode.InsertOnly)
    {
        return EnqueueImportArchiveDataFromUpload(tusFileId, tenantId, archiveRtId, mode);
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
