using System.ComponentModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     HangFire Job that implements the export of CK and RT model files
/// </summary>
public class ExportModelJob : IExportModelJob
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IDistributedCacheService _distributedCache;
    private readonly IExportRtModelCommand _exportRtModelCommand;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="distributedCache">Redis distributed cache for file caching</param>
    /// <param name="exportRtModelCommand"></param>
    public ExportModelJob(IDistributedCacheService distributedCache, IExportRtModelCommand exportRtModelCommand)
    {
        _distributedCache = distributedCache;
        _exportRtModelCommand = exportRtModelCommand;
    }

    /// <summary>
    ///     Exports a runtime model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="queryId">Id of query, whose data is exported</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns>The key the result file is stored.</returns>
    [DisplayName("Export Runtime Metadata to data source '{0}'")]
    public async Task<string> ExportRtAsync(string tenantId, OctoObjectId queryId,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            Logger.Info($"Preparing output file for query '{queryId}' of data source '{tenantId}'");
            var tempFile = Path.GetTempFileName();

            Logger.Info($"Starting export of file '{tempFile}'");

            await _exportRtModelCommand.ExportAsync(tenantId, queryId, tempFile,
                cancellationToken?.ShutdownToken);

            var key = await CacheFileToRedis(tenantId, tempFile);

            Logger.Info($"Export of file '{tempFile}' completed.");

            return key;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Export failed with error.");
            throw;
        }
    }

    private async Task<string> CacheFileToRedis(string tenantId, string tempFile)
    {
        using (var streamReader = new StreamReader(tempFile))
        {
            await using (var memoryStream = new MemoryStream())
            {
                await streamReader.BaseStream.PackFileToZipAsync("RtEntities.json", memoryStream);
                return await _distributedCache.CreateStreamAsync(tenantId, memoryStream, "application/zip", "RtEntities.zip",
                    TimeSpan.FromHours(1));
            }
        }
    }
}