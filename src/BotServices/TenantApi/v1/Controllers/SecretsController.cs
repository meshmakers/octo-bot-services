using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Duende.IdentityModel;
using Meshmakers.Octo.Backend.BotServices.Controllers;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.BotServices.TenantApi.v1.Controllers;

/// <summary>
///     Secrets administration of a tenant (AB#5544, contract §9): encryption status of the environment, the
///     secret sweep run history and the early deletion of a run's pre-sweep dump. Sweeps themselves are
///     started on <c>{tenantId}/v1/jobs/secret-sweep</c>. Dumps are never downloadable.
/// </summary>
/// <remarks>
///     The tenant is a route segment, so the transport tenant gate checks every call against the caller's
///     <c>tenant_id</c> claim - exact match only: unlike <see cref="JobsController" /> this controller does
///     not carry <c>[AllowParentTenantAdministration]</c> (the marker is deliberately limited to the job
///     routes). Roles (tenant roles of the token): status - any user with tenant access; run list -
///     <c>AdminPanelManagement</c>; dump delete - <c>SecretManagement</c>.
/// </remarks>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class SecretsController : ControllerBase
{
    /// <summary>
    ///     Default number of runs returned by <see cref="GetSweepRuns" />.
    /// </summary>
    public const int DefaultRunLimit = 20;

    private readonly ISecretSweepRunService _runService;
    private readonly ISecretEnvironmentStatusService _statusService;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="statusService">Encryption status.</param>
    /// <param name="runService">Run history and dump management.</param>
    public SecretsController(ISecretEnvironmentStatusService statusService, ISecretSweepRunService runService)
    {
        _statusService = statusService;
        _runService = runService;
    }

    /// <summary>
    ///     Returns the encryption status of the environment as seen from the tenant: key ring configured,
    ///     active and known key ids (never key material), legacy <c>enc:v1</c> key, strict mode, the recurring
    ///     Verify schedule and the tenant's last Verify run. Any user with access to the tenant (editors use it
    ///     to disable secret inputs when no key ring is configured).
    /// </summary>
    /// <param name="tenantId">The tenant id, from the route.</param>
    // GET: {tenantId}/v1/secrets/status
    [HttpGet("status")]
    [Authorize(BotServiceConstants.JobApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(SecretEnvironmentStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStatus([FromRoute] [Required] string tenantId)
    {
        return new JsonResult(await _statusService.GetStatusAsync(tenantId), SecretSweepReportJson.Options);
    }

    /// <summary>
    ///     Returns the secret sweep runs of the tenant, newest first (the last 50 are kept): mode, trigger,
    ///     outcome (<c>Running</c> while in progress), who started it, counts and the state of its pre-sweep
    ///     dump. Requires the tenant role <c>AdminPanelManagement</c>.
    /// </summary>
    /// <param name="tenantId">The tenant id, from the route.</param>
    /// <param name="limit">Number of runs, 1 to 50 (default 20); more than 50 is capped.</param>
    // GET: {tenantId}/v1/secrets/sweep-runs?limit=20
    [HttpGet("sweep-runs")]
    [Authorize(BotServiceConstants.SecretAdministrationReadPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<SecretSweepRunDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CodedBadRequestErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSweepRuns([FromRoute] [Required] string tenantId,
        [FromQuery] int limit = DefaultRunLimit)
    {
        if (limit < 1)
        {
            return BadRequest(new CodedBadRequestErrorDto("InvalidLimit",
                "limit must be between 1 and " + ISecretSweepRunStore.MaxRunsPerTenant + "."));
        }

        var runs = await _runService.GetRunsAsync(tenantId, Math.Min(limit, ISecretSweepRunStore.MaxRunsPerTenant));
        return new JsonResult(runs, SecretSweepReportJson.Options);
    }

    /// <summary>
    ///     Deletes the pre-sweep dump of a run before its 7-day expiry and records when and by whom. There is
    ///     no download. Requires the tenant role <c>SecretManagement</c>.
    /// </summary>
    /// <param name="tenantId">The tenant id, from the route.</param>
    /// <param name="runId">The run id (<see cref="SecretSweepRunDto.RunId" />).</param>
    /// <response code="204">The dump was deleted.</response>
    /// <response code="404">Unknown run, or the run has no dump (e.g. Verify).</response>
    /// <response code="409">The dump was already deleted (early or expired).</response>
    // DELETE: {tenantId}/v1/secrets/sweep-runs/{runId}/dump
    [HttpDelete("sweep-runs/{runId}/dump")]
    [Authorize(BotServiceConstants.SecretManagementPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(NotFoundErrorDto), StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteSweepRunDump([FromRoute] [Required] string tenantId,
        [FromRoute] [Required] string runId)
    {
        var result = await _runService.DeleteDumpAsync(tenantId, runId, JobsControllerBase.GetUserName(User));
        return result switch
        {
            SecretSweepDumpDeleteResultDto.Deleted => NoContent(),
            SecretSweepDumpDeleteResultDto.AlreadyDeleted => Conflict(),
            _ => NotFound(new NotFoundErrorDto($"No pre-sweep dump for run '{runId}' of tenant '{tenantId}'."))
        };
    }
}
