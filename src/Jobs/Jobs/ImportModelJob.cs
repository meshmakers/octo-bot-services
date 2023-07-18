using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.Common.Shared.DistributedCache;
using Meshmakers.Octo.Common.Shared.Jobs;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using NLog;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Hangfire Job that implements the import of CK and RT model files
/// </summary>
public class ImportModelJob : IImportModelJob
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IDistributedWithPubSubCache _distributedCache;
    private readonly ISystemContext _systemContext;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="systemContext">System context object</param>
    /// <param name="distributedCache">Redis distributed cache for file caching</param>
    public ImportModelJob(ISystemContext systemContext, IDistributedWithPubSubCache distributedCache)
    {
        _systemContext = systemContext;
        _distributedCache = distributedCache;
    }

    /// <summary>
    ///     Imports a CK model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="scopeId">The scope id</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns></returns>
    [DisplayName("Importing ConstructionKit Metadata to data source '{0}'")]
    public async Task ImportCkAsync(string tenantId, string key, ScopeIdsDto scopeId,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            if (scopeId == ScopeIdsDto.System)
            {
                throw new InvalidOperationException(
                    "Scope SYSTEM cannot be imported, because this scope is handled by system.");
            }

            Logger.Info($"Reading input file from cache for CK import to '{tenantId}'");
            var tempFile = await GetTempFile(key);

            Logger.Info($"Starting import of file '{tempFile}'");
            using var systemSession = await _systemContext.StartSystemSessionAsync();
            systemSession.StartTransaction();

            await _systemContext.ImportCkModelAsync(systemSession, tenantId, (ScopeIds)scopeId, tempFile,
                cancellationToken?.ShutdownToken);

            await systemSession.CommitTransactionAsync();

            await ClearCache(key);

            Logger.Info($"Import of file '{tempFile}' completed.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Import failed with error.");
            throw;
        }
    }

    /// <summary>
    ///     Imports a runtime model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns></returns>
    [DisplayName("Importing Runtime Metadata to data source '{0}'")]
    public async Task ImportRtAsync(string tenantId, string key, IBotCancellationToken? cancellationToken)
    {
        try
        {
            Logger.Info($"Reading input file from cache for RT import to '{tenantId}'");
            var tempFile = await GetTempFile(key);

            Logger.Info($"Starting import of file '{tempFile}'");
            var tenantContext = await _systemContext.CreateOrGetTenantContextAsync(tenantId);
            using var session = await tenantContext.Repository.StartSessionAsync();
            // session.StartTransaction();

            await tenantContext.ImportRtModelAsync(session, tempFile, cancellationToken?.ShutdownToken);

            // await session.CommitTransactionAsync();

            await ClearCache(key);

            Logger.Info($"Import of file '{tempFile}' completed.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Import failed with error.");
            throw;
        }
    }

    private async Task<string> GetTempFile(string key)
    {
        CacheStream? cacheStream = await _distributedCache.GetCacheStreamAsync(key);
        if (cacheStream == null)
        {
            throw new JobFailedException("No value in distribute cache found.");
        }

        var tempFile = Path.GetTempFileName();

        using (var memoryStream = new MemoryStream(cacheStream.Stream))
        {
            if (cacheStream.ContentType.ToLower() == "application/zip")
            {
                await memoryStream.ExtractFileFromZipAsync(cacheStream.ContentType, ".json", tempFile);
            }
            else if (cacheStream.ContentType.ToLower() == "application/json")
            {
                await using (var streamWriter = new StreamWriter(tempFile))
                {
                    await memoryStream.CopyToAsync(streamWriter.BaseStream);
                }
            }
            else
            {
                throw new JobFailedException("File type is not supported.");
            }
        }

        return tempFile;
    }

    private async Task ClearCache(string key)
    {
        await _distributedCache.DeleteCacheStreamAsync(key);
    }
}
