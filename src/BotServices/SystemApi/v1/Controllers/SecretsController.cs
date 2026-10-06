using Asp.Versioning;
using Duende.IdentityModel;
using Hangfire;
using Meshmakers.Octo.Backend.BotServices.Controllers;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers;

/// <summary>
///     Instance-wide secret sweep operations (AB#5539, concept AB#5528 §5.2 phase 4, §5.3): start a sweep
///     over all tenants and read the last report of every tenant. Per-tenant operations live on the tenant
///     route (<c>{tenantId}/v1/jobs/secret-sweep</c>).
/// </summary>
/// <remarks>
///     🔴 The route carries no tenant, so the transport tenant gate never sees these calls. Both actions
///     therefore require the caller to be entitled to the <b>system tenant</b> - the very check
///     <see cref="IJobTenantAccessGuard" /> performs for a job owned by the system tenant. A token of any
///     other tenant gets a bare <c>403</c>.
/// </remarks>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiController]
[ApiVersion("1.0")]
public class SecretsController : ControllerBase
{
    private readonly IBackgroundJobClient _backgroundJobClient;
    private readonly ISecretSweepReportStore _reportStore;
    private readonly IJobTenantAccessGuard _tenantAccessGuard;
    private readonly IOptions<OctoSystemConfiguration> _systemConfiguration;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="backgroundJobClient">Hangfire client.</param>
    /// <param name="reportStore">Secret sweep report store.</param>
    /// <param name="tenantAccessGuard">Authorizes the caller against the system tenant.</param>
    /// <param name="systemConfiguration">Provides the system tenant id.</param>
    public SecretsController(IBackgroundJobClient backgroundJobClient, ISecretSweepReportStore reportStore,
        IJobTenantAccessGuard tenantAccessGuard, IOptions<OctoSystemConfiguration> systemConfiguration)
    {
        _backgroundJobClient = backgroundJobClient;
        _reportStore = reportStore;
        _tenantAccessGuard = tenantAccessGuard;
        _systemConfiguration = systemConfiguration;
    }

    /// <summary>
    ///     Starts a secret sweep over all tenants of the instance (system tenant and every registered tenant,
    ///     child tenants included). Writing modes take a pre-sweep dump per tenant first; a tenant whose dump
    ///     fails is skipped. The job fails at the end when any tenant was skipped or failed; the per-tenant
    ///     reasons are in <see cref="GetReports" />.
    /// </summary>
    /// <param name="mode">
    ///     <c>Verify</c> (default), <c>Encrypt</c>, <c>Reprotect</c> or <c>ClearUnknownKid</c> (name or number).
    ///     <c>Decrypt</c> is refused with <c>400</c>.
    /// </param>
    // POST: system/v1/secrets/sweep?mode=Encrypt
    [HttpPost("sweep")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    [ProducesResponseType(typeof(JobResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Sweep([FromQuery] SecretSweepMode mode = SecretSweepMode.Verify)
    {
        var systemTenantId = _systemConfiguration.Value.SystemTenantId;
        if (!await _tenantAccessGuard.MayAccessJobAsync(User, systemTenantId, "secret-sweep:all-tenants"))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!JobsControllerBase.IsSweepModeOffered(mode))
        {
            return BadRequest(new InternalServerErrorDto(
                $"Secret sweep mode '{mode}' is not available; use Verify, Encrypt, Reprotect or ClearUnknownKid."));
        }

        try
        {
            var id = _backgroundJobClient.Enqueue<ISecretSweepJob>(job =>
                job.RunAllTenants(systemTenantId, mode, SecretSweepTrigger.Manual, BotCancellationToken.Null));
            return Ok(new JobResponseDto(id));
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new InternalServerErrorDto(e.Message));
        }
    }

    /// <summary>
    ///     Returns the last secret sweep report of every tenant that has one, ordered by tenant id. Never
    ///     contains a value.
    /// </summary>
    // GET: system/v1/secrets/reports
    [HttpGet("reports")]
    [Authorize(BotServiceConstants.JobApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<SecretSweepReport>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetReports()
    {
        var systemTenantId = _systemConfiguration.Value.SystemTenantId;
        if (!await _tenantAccessGuard.MayAccessJobAsync(User, systemTenantId, "secret-sweep:reports"))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return new JsonResult(await _reportStore.GetAllLastAsync(), SecretSweepReportJson.Options);
    }
}
