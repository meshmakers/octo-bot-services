using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DistributedCache;
using Meshmakers.Octo.Common.Shared.Jobs;
using Meshmakers.Octo.SystematizedData.Persistence;
using NLog;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     HangFire Job that implements the export of CK and RT model files
/// </summary>
public class ExportModelJob : IExportModelJob
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IDistributedWithPubSubCache _distributedCache;
    private readonly ISystemContext _systemContext;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="systemContext">System context object</param>
    /// <param name="distributedCache">Redis distributed cache for file caching</param>
    public ExportModelJob(ISystemContext systemContext, IDistributedWithPubSubCache distributedCache)
    {
        _systemContext = systemContext;
        _distributedCache = distributedCache;
    }

    /// <summary>
    ///     Exports a runtime model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="queryId">Id of query, whose data is exported</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns>The key the result file is stored.</returns>
    [DisplayName("Export Runtime Metadata to data source '{0}'")]
    public async Task<string> ExportRtAsync(string tenantId, string queryId,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            Logger.Info($"Preparing output file for query '{queryId}' of data source '{tenantId}'");
            var tempFile = Path.GetTempFileName();
            var key = Guid.NewGuid().ToString();

            Logger.Info($"Starting export of file '{tempFile}'");

            var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);
            using var session = await tenantContext.Repository.StartSessionAsync();
            session.StartTransaction();

            await tenantContext.ExportRtModelAsync(session, new OctoObjectId(queryId), tempFile,
                cancellationToken?.ShutdownToken);

            await session.CommitTransactionAsync();

            await CacheFileToRedis(key, tempFile);

            Logger.Info($"Export of file '{tempFile}' completed.");

            return key;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Export failed with error.");
            throw;
        }
    }

    private async Task CacheFileToRedis(string key, string tempFile)
    {
        using (var streamReader = new StreamReader(tempFile))
        {
            await using (var memoryStream = new MemoryStream())
            {
                await streamReader.BaseStream.PackFileToZipAsync("RtEntities.json", memoryStream);
                await _distributedCache.CacheStreamAsync(key, memoryStream.ToArray(), "application/zip", TimeSpan.FromHours(1));
            }
        }
    }
}
