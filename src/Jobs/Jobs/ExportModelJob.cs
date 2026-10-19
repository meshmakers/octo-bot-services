using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     HangFire Job that implements the export of CK and RT model files
/// </summary>
public class ExportModelJob : IExportModelJob
{
    private readonly ILogger<ExportModelJob> _logger;
    private readonly IDistributedCacheService _distributedCache;
    private readonly ICompressionService _compressionService;
    private readonly IExportRtModelByQueryCommand _exportRtModelByQueryCommand;
    private readonly IExportRtModelByDeepGraphCommand _rtModelByDeepGraphCommand;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="distributedCache">Distributed cache for file caching</param>
    /// <param name="compressionService">Service for compressing and decompressing files</param>
    /// <param name="exportRtModelByQueryCommand">Command to export runtime model by query</param>
    /// <param name="rtModelByDeepGraphCommand">Command to export runtime model by deep graph</param>
    public ExportModelJob(ILogger<ExportModelJob> logger, IDistributedCacheService distributedCache, ICompressionService compressionService,
        IExportRtModelByQueryCommand exportRtModelByQueryCommand, IExportRtModelByDeepGraphCommand rtModelByDeepGraphCommand)
    {
        _logger = logger;
        _distributedCache = distributedCache;
        _compressionService = compressionService;
        _exportRtModelByQueryCommand = exportRtModelByQueryCommand;
        _rtModelByDeepGraphCommand = rtModelByDeepGraphCommand;
    }


    /// <inheritdoc />
    public async Task<string> ExportRtModelByQueryAsync(ExportRtByQueryCommandRequest rtByQueryCommandRequest,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            // AB#6734: a job without caller context (enqueued by an older asset repository) fails before
            // anything is read or written - an export is never run as the system.
            var securityContext = ExportCallerContext.Require(rtByQueryCommandRequest.TenantId, "query",
                rtByQueryCommandRequest.InitiatedBySubjectId, rtByQueryCommandRequest.InitiatedByRoleNames);

            _logger.LogInformation(
                "Preparing output file for query \'{QueryId}\' of tenant \'{TenantId}\' for subject \'{SubjectId}\'",
                rtByQueryCommandRequest.QueryId, rtByQueryCommandRequest.TenantId, securityContext.SubjectId);
            var tempFile = Path.GetTempFileName();

            _logger.LogInformation("Starting export of file \'{TempFile}\'", tempFile);

            await _exportRtModelByQueryCommand.ExportAsync(rtByQueryCommandRequest.TenantId,
                rtByQueryCommandRequest.QueryId, securityContext, tempFile,
                cancellationToken?.ShutdownToken);

            var key = await CacheFileToDistributedCache(rtByQueryCommandRequest.TenantId, tempFile);

            _logger.LogInformation("Export of file \'{TempFile}\' completed", tempFile);

            return key;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Export failed with error");
            throw;
        }
    }


    /// <inheritdoc />
    public async Task<string> ExportRtModelByDeepGraphAsync(ExportRtByDeepGraphCommandRequest rtByDeepGraphCommandRequest,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            // AB#6734: see ExportRtModelByQueryAsync.
            var securityContext = ExportCallerContext.Require(rtByDeepGraphCommandRequest.TenantId, "deep graph",
                rtByDeepGraphCommandRequest.InitiatedBySubjectId, rtByDeepGraphCommandRequest.InitiatedByRoleNames);

            _logger.LogInformation(
                "Preparing output file for deep graph of tenant \'{TenantId}\' for subject \'{SubjectId}\'",
                rtByDeepGraphCommandRequest.TenantId, securityContext.SubjectId);
            var tempFile = Path.GetTempFileName();

            _logger.LogInformation("Starting export of file \'{TempFile}\'", tempFile);

            await _rtModelByDeepGraphCommand.ExportAsync(rtByDeepGraphCommandRequest.TenantId, rtByDeepGraphCommandRequest, securityContext, tempFile,
                cancellationToken?.ShutdownToken);

            var key = await CacheFileToDistributedCache(rtByDeepGraphCommandRequest.TenantId, tempFile);

            _logger.LogInformation("Export of file \'{TempFile}\' completed", tempFile);

            return key;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Export failed with error");
            throw;
        }
    }

    private async Task<string> CacheFileToDistributedCache(string tenantId, string tempFile)
    {
        await using var zipStream = new MemoryStream();
        using var streamReader = new StreamReader(tempFile);

        await _compressionService.PackFileToZipAsync(zipStream, streamReader.BaseStream, "RtEntities.yaml", true);
        zipStream.Position = 0;
        return await _distributedCache.CreateStreamAsync(tenantId, zipStream, "application/zip", "RtEntities.zip",
            TimeSpan.FromHours(1));
    }
}