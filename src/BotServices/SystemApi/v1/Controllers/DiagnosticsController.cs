using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers;

/// <summary>
/// Manages the diagnostics settings of the service
/// </summary>
[ApiController]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class DiagnosticsController: ControllerBase
{
    private readonly ILogger<DiagnosticsController> _logger;
    private readonly IDiagnosticsService _diagnosticsService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="diagnosticsService"></param>
    public DiagnosticsController(ILogger<DiagnosticsController> logger, IDiagnosticsService diagnosticsService)
    {
        _logger = logger;
        _diagnosticsService = diagnosticsService;
    }

    /// <summary>
    /// Reconfigures the log level of the service
    /// </summary>
    /// <param name="minLogLevel">The minimal log level to be logged.</param>
    /// <returns></returns>
    [HttpPost("reconfigureLogLevel")]
    [Authorize(BotServiceConstants.JobApiReadWritePolicy)]
    public async Task<IActionResult> Enable([Required] LogLevelDto minLogLevel)
    {
        try
        {
            _logger.LogInformation("Reconfiguring log level to {MinLogLevel}", minLogLevel);
            await _diagnosticsService.ReconfigureLogLevelAsync(minLogLevel);
            return NoContent();
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }
}