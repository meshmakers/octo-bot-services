using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Hangfire;
using IdentityModel;
using Meshmakers.Octo.Backend.BotServices.Controllers;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.BotServices.TenantApi.v1.Controllers;

/// <summary>
///     Tenant-scoped job operations of the bot service: repository dump, restore from a tus upload,
///     archive data export, archive data import and the fixup script run. Mirrors the System-API
///     controller one-to-one — same policies, same jobs, same arguments — but takes the tenant as a
///     <b>route segment</b> instead of a query parameter.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why the route shape matters (AB#5060).</b> The transport tenant gate
///         (<c>TenantAuthorizationMiddleware</c>, wired in <c>Program.cs</c>) reads the tenant from the
///         <b>route value</b>. As long as these operations addressed their tenant through
///         <c>?tenantId=…</c> the gate never saw them, so a token issued for one tenant could dump,
///         restore or export the repository of any other. On this controller the tenant is in the
///         route, so every call is matched against the caller's <c>tenant_id</c> claim.
///     </para>
///     <para>
///         <b>Every action carries <see cref="AllowParentTenantAdministrationAttribute" /></b> — the
///         marker is on the class because all five operations are the same kind of thing: an
///         administrator of a tenant <i>above</i> this one may back up, restore, export and fix up a
///         child tenant. None of these five endpoints returns tenant content in its own response, and
///         no data route of this service is or may be marked. Service tokens are never widened by the
///         rule — see <see cref="IAllowParentTenantAdministration" />.
///     </para>
///     <para>
///         🔴 <b>The artifact is not covered by any of that (AB#5070).</b> A dump or export produces a
///         job whose result is fetched through <c>system/v1/jobs/download?tenantId=…&amp;id=…</c>, and
///         that endpoint is <b>not</b> tenant-gated in either sense. It carries no route tenant, so
///         the middleware returns early and checks nothing at all — being unmarked does not make it
///         exact-matched, it makes it unchecked. And the artifact lookup never consults
///         <c>tenantId</c> on the current path: the job id alone resolves the result file, which is
///         then streamed. So the job id a parent administrator legitimately receives from the calls
///         below is by itself sufficient to fetch the child tenant's dump. Until AB#5070 binds a job
///         to its tenant and gates the retrieval, "may administer" is in practice "may read", and the
///         separation this remark describes is aspirational rather than enforced.
///     </para>
///     <para>
///         <b>The tus upload sink stays tenant-neutral, deliberately.</b> The resumable upload endpoint
///         (<c>/system/v1/tus-upload</c>) takes a <c>tenantId</c> upload metadata field, but nothing
///         reads it: the file is stored flat under its tus file id, and both consuming jobs take the
///         tenant from the request that starts them. Putting the upload on a tenant route would
///         therefore promise an ownership binding the storage does not have — the upload is a staging
///         area, and the tenant-carrying, gated decision is the restore / import call below. Binding
///         the sink to a tenant is a separate change (it needs the metadata to be persisted and
///         re-checked at consumption time), not a route rename.
///     </para>
/// </remarks>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[AllowParentTenantAdministration]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class JobsController : JobsControllerBase
{
    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="backgroundJobClient">Hangfire client used to enqueue the jobs.</param>
    /// <param name="backupFileStorage">Storage service resolving tus upload file paths.</param>
    public JobsController(IBackgroundJobClient backgroundJobClient, IBackupFileStorageService backupFileStorage)
        : base(backgroundJobClient, backupFileStorage)
    {
    }

    /// <summary>
    ///     Runs the fixup scripts for the tenant taken from the route.
    /// </summary>
    /// <param name="tenantId">The tenant id, from the route.</param>
    // POST: {tenantId}/v1/jobs/run-fixup-scripts
    [HttpPost]
    [Route("run-fixup-scripts")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult RunFixupScripts([FromRoute] [Required] string tenantId)
    {
        return EnqueueRunFixupScripts(tenantId);
    }

    /// <summary>
    ///     Restores the repository for the tenant taken from the route from a tus resumable upload.
    ///     The file must have been uploaded via the tus endpoint at <c>/system/v1/tus-upload</c> first.
    /// </summary>
    /// <param name="tenantId">The tenant id, from the route.</param>
    /// <param name="tusFileId">The tus file ID from the completed upload.</param>
    /// <param name="databaseName">The name of the database to restore.</param>
    /// <param name="oldDatabaseName">Optional parameter. To be used, when the new db name does not match the original one.</param>
    /// <param name="restoreArchiveData">When <c>true</c> and the uploaded artifact is an <c>.octobak.zip</c> carrying archive data, the tenant's CrateDB archives are also restored (concept AB#4231). Default <c>false</c> (Mongo only).</param>
    // POST: {tenantId}/v1/jobs/restore-from-upload
    [HttpPost]
    [Route("restore-from-upload")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult RestoreFromUpload(
        [FromRoute] [Required] string tenantId,
        [Required] string tusFileId,
        [Required] string databaseName,
        string? oldDatabaseName = null,
        [FromQuery] bool restoreArchiveData = false)
    {
        return EnqueueRestoreFromUpload(tusFileId, tenantId, databaseName, oldDatabaseName, restoreArchiveData);
    }

    /// <summary>
    ///     Dumps the repository for the tenant taken from the route.
    /// </summary>
    /// <param name="tenantId">The tenant id, from the route.</param>
    /// <param name="includeArchiveData">
    ///     When <c>true</c>, the tenant's CrateDB archive rows are bundled with the mongodump blob into an
    ///     <c>.octobak.zip</c> container (concept AB#4231). When <c>false</c> (default), a single mongodump
    ///     <c>.tar.gz</c> is produced exactly as before.
    /// </param>
    // POST: {tenantId}/v1/jobs/dump-repository
    [HttpPost]
    [Route("dump-repository")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult DumpRepository([FromRoute] [Required] string tenantId,
        [FromQuery] bool includeArchiveData = false)
    {
        return EnqueueDumpRepository(tenantId, includeArchiveData);
    }

    /// <summary>
    ///     Exports the data rows of an archive to a downloadable ZIP (AB#4230). The produced ZIP is
    ///     registered as the job's downloadable result and retrieved via the existing
    ///     <c>system/v1/jobs/download</c> endpoint. When both <paramref name="fromUtc" /> and
    ///     <paramref name="toUtc" /> are omitted the whole archive is exported; when supplied, only rows in
    ///     the half-open window <c>[fromUtc, toUtc)</c> are exported.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the archive, from the route.</param>
    /// <param name="archiveRtId">Runtime id of the <c>CkArchive</c> entity.</param>
    /// <param name="fromUtc">Optional inclusive lower bound of the export window (ISO-8601 UTC).</param>
    /// <param name="toUtc">Optional exclusive upper bound of the export window (ISO-8601 UTC).</param>
    // POST: {tenantId}/v1/jobs/export-archive-data
    [HttpPost]
    [Route("export-archive-data")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ExportArchiveData(
        [FromRoute] [Required] string tenantId,
        [Required] string archiveRtId,
        DateTime? fromUtc = null,
        DateTime? toUtc = null)
    {
        return EnqueueExportArchiveData(tenantId, archiveRtId, fromUtc, toUtc);
    }

    /// <summary>
    ///     Imports archive data rows into the given archive from a tus resumable upload (AB#4230). The
    ///     export ZIP must have been uploaded via the tus endpoint at <c>/system/v1/tus-upload</c> first.
    ///     Schema-match validation runs inside the job; on mismatch the job ends Failed with a
    ///     field-level reason surfaced through <c>system/v1/jobs/{id}</c>.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the target archive, from the route.</param>
    /// <param name="tusFileId">The tus file ID from the completed upload.</param>
    /// <param name="archiveRtId">Runtime id of the target <c>CkArchive</c> entity.</param>
    /// <param name="mode">Import mode (<c>InsertOnly</c>/<c>Upsert</c>; binds from <c>0</c>/<c>1</c> or the name).</param>
    // POST: {tenantId}/v1/jobs/import-archive-data-from-upload
    [HttpPost]
    [Route("import-archive-data-from-upload")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult ImportArchiveDataFromUpload(
        [FromRoute] [Required] string tenantId,
        [Required] string tusFileId,
        [Required] string archiveRtId,
        [FromQuery] ArchiveImportMode mode = ArchiveImportMode.InsertOnly)
    {
        return EnqueueImportArchiveDataFromUpload(tusFileId, tenantId, archiveRtId, mode);
    }
}
