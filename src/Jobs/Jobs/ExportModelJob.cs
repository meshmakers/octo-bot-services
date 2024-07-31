using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     HangFire Job that implements the export of CK and RT model files
/// </summary>
public class ExportModelJob : IExportModelJob
{
    private readonly ILogger<ExportModelJob> _logger;
    private readonly IDistributedCacheService _distributedCache;
    private readonly IExportRtModelByQueryCommand _exportRtModelByQueryCommand;
    private readonly IExportRtModelByDeepGraphCommand _rtModelByDeepGraphCommand;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="logger">Logger instance</param>
    /// <param name="distributedCache">Distributed cache for file caching</param>
    /// <param name="exportRtModelByQueryCommand">Command to export runtime model by query</param>
    /// <param name="rtModelByDeepGraphCommand">Command to export runtime model by deep graph</param>
    public ExportModelJob(ILogger<ExportModelJob> logger, IDistributedCacheService distributedCache, 
        IExportRtModelByQueryCommand exportRtModelByQueryCommand, IExportRtModelByDeepGraphCommand rtModelByDeepGraphCommand)
    {
        _logger = logger;
        _distributedCache = distributedCache;
        _exportRtModelByQueryCommand = exportRtModelByQueryCommand;
        _rtModelByDeepGraphCommand = rtModelByDeepGraphCommand;
    }


    /// <inheritdoc />
    public async Task<string> ExportRtModelByQueryAsync(string tenantId, OctoObjectId queryId,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            _logger.LogError("Preparing output file for query \'{QueryId}\' of tenant \'{TenantId}\'", queryId, tenantId);
            var tempFile = Path.GetTempFileName();

            _logger.LogError("Starting export of file \'{TempFile}\'", tempFile);

            await _exportRtModelByQueryCommand.ExportAsync(tenantId, queryId, tempFile,
                cancellationToken?.ShutdownToken);

            var key = await CacheFileToDistributedCache(tenantId, tempFile);

            _logger.LogError("Export of file \'{TempFile}\' completed", tempFile);

            return key;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Export failed with error");
            throw;
        }
    }


    /// <inheritdoc />
    public async Task<string> ExportRtModelByDeepGraphAsync(string tenantId, IEnumerable<OctoObjectId> originRtIds, CkId<CkTypeId> originCkTypeId,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            _logger.LogError("Preparing output file for deep graph of tenant \'{TenantId}\'", tenantId);
            var tempFile = Path.GetTempFileName();

            _logger.LogError("Starting export of file \'{TempFile}\'", tempFile);

            await _rtModelByDeepGraphCommand.ExportAsync(tenantId, originRtIds, originCkTypeId, tempFile,
                cancellationToken?.ShutdownToken);

            var key = await CacheFileToDistributedCache(tenantId, tempFile);

            _logger.LogError("Export of file \'{TempFile}\' completed", tempFile);

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
        using var streamReader = new StreamReader(tempFile);
        await using var memoryStream = new MemoryStream();
        await streamReader.BaseStream.PackFileToZipAsync("RtEntities.json", memoryStream);
        return await _distributedCache.CreateStreamAsync(tenantId, memoryStream, "application/zip", "RtEntities.zip",
            TimeSpan.FromHours(1));
    }
}