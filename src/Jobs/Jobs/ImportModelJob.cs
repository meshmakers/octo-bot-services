using System.ComponentModel;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using NLog;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Hangfire Job that implements the import of CK and RT model files
/// </summary>
public class ImportModelJob : IImportModelJob
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IDistributedCacheService _distributedCacheService;
    private readonly IImportCkModelCommand _importCkModelCommand;
    private readonly IImportRtModelCommand _importRtModelCommand;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="distributedCacheService"></param>
    /// <param name="importCkModelCommand">Redis distributed cache for file caching</param>
    /// <param name="importRtModelCommand"></param>
    public ImportModelJob(IDistributedCacheService distributedCacheService, IImportCkModelCommand importCkModelCommand,
        IImportRtModelCommand importRtModelCommand)
    {
        _distributedCacheService = distributedCacheService;
        _importCkModelCommand = importCkModelCommand;
        _importRtModelCommand = importRtModelCommand;
    }

    /// <summary>
    ///     Imports a CK model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="cancellationToken">An cancellation token to abort the job</param>
    /// <returns></returns>
    [DisplayName("Importing ConstructionKit Metadata to data source '{0}'")]
    public async Task ImportCkAsync(string tenantId, string key,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            Logger.Info($"Reading input file from cache for CK import to '{tenantId}'");
            var tempFile = await GetTempFile(tenantId, key);

            Logger.Info($"Starting import of file '{tempFile}'");

            await _importCkModelCommand.ImportAsync(tenantId, tempFile.Item1,
                cancellationToken?.ShutdownToken);

            await ClearCache(tenantId, key);

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
            var tempFile = await GetTempFile(tenantId, key);

            Logger.Info($"Starting import of file '{tempFile}'");

            await _importRtModelCommand.Import(tenantId, tempFile.Item1, tempFile.Item2, cancellationToken?.ShutdownToken);

            await ClearCache(tenantId, key);

            Logger.Info($"Import of file '{tempFile}' completed.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Import failed with error.");
            throw;
        }
    }

    private async Task<Tuple<string, string>> GetTempFile(string tenantId, string key)
    {
        var cacheStream = await _distributedCacheService.GetCacheStreamByIdAsync(tenantId, key);
        if (cacheStream == null)
        {
            throw new JobFailedException("No value in distribute cache found.");
        }

        var tempFile = Path.GetTempFileName();

        if (cacheStream.ContentType.ToLower() == "application/zip")
        {
            await cacheStream.Stream.ExtractFileFromZipAsync(cacheStream.ContentType, ".json", tempFile);
        }
        else if (cacheStream.ContentType.ToLower() == "application/json" || cacheStream.ContentType.ToLower() == "text/yaml")
        {
            await using (var streamWriter = new StreamWriter(tempFile))
            {
                await cacheStream.Stream.CopyToAsync(streamWriter.BaseStream);
            }
        }
        else
        {
            throw new JobFailedException("File type is not supported.");
        }

        return new Tuple<string, string>(tempFile, cacheStream.ContentType);
    }

    private async Task ClearCache(string tenantId, string key)
    {
        await _distributedCacheService.DeleteCacheStreamAsync(tenantId, key);
    }
}