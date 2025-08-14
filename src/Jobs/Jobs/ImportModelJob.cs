using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
///     Hangfire Job that implements the import of CK and RT model files
/// </summary>
public class ImportModelJob : IImportModelJob
{
    private readonly ILogger<ImportModelJob> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IDistributedCacheService _distributedCacheService;
    private readonly ICompressionService _compressionService;
    private readonly IImportCkModelCommand _importCkModelCommand;
    private readonly IImportRtModelCommand _importRtModelCommand;

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="logger">Instance of the logger</param>
    /// <param name="systemContext">System context object</param>
    /// <param name="distributedCacheService">Service for distributed caching</param>
    /// <param name="compressionService">Service for compressing and decompressing files</param>
    /// <param name="importCkModelCommand">Command to import a CK model</param>
    /// <param name="importRtModelCommand">Command to import an RT model</param>
    public ImportModelJob(ILogger<ImportModelJob> logger, ISystemContext systemContext,
        IDistributedCacheService distributedCacheService, ICompressionService compressionService,
        IImportCkModelCommand importCkModelCommand,
        IImportRtModelCommand importRtModelCommand)
    {
        _logger = logger;
        _systemContext = systemContext;
        _distributedCacheService = distributedCacheService;
        _compressionService = compressionService;
        _importCkModelCommand = importCkModelCommand;
        _importRtModelCommand = importRtModelCommand;
    }

    /// <summary>
    ///     Imports a CK model
    /// </summary>
    /// <param name="tenantId">The corresponding tenant id</param>
    /// <param name="key">The key definition in redis</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns></returns>
    public async Task ImportCkAsync(string tenantId, string key,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            _logger.LogInformation("Reading input file from cache for CK import to \'{TenantId}\'", tenantId);
            var tempFile = await GetTempFile(tenantId, key);

            _logger.LogInformation("Starting import of file \'{TempFile}\'", tempFile);

            await _importCkModelCommand.ImportAsync(tenantId, tempFile.Item1,
                cancellationToken?.ShutdownToken);

            await ClearCache(tenantId, key);

            _logger.LogInformation("Import of file \'{TempFile}\' completed", tempFile);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import failed with error");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task ImportRtAsync(string tenantId, ImportStrategy importStrategy, string key,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            _logger.LogInformation("Reading input file from cache for RT import to \'{TenantId}\'", tenantId);
            var tempFile = await GetTempFile(tenantId, key);

            _logger.LogInformation("Starting import of file \'{TempFile}\'", tempFile);

            var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);
            await _importRtModelCommand.ImportAsync(tenantRepository, tempFile.Item1, tempFile.Item2, importStrategy,
                cancellationToken?.ShutdownToken);

            await ClearCache(tenantId, key);

            _logger.LogInformation("Import of file \'{TempFile}\' completed", tempFile);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import failed with error");
            throw;
        }
    }

    private async Task<Tuple<string, string>> GetTempFile(string tenantId, string key)
    {
        var cacheStream = await _distributedCacheService.GetCacheStreamByIdAsync(tenantId, key);
        if (cacheStream == null)
        {
            throw JobFailedException.CacheStreamNotFound(tenantId, key);
        }

        var tempFile = Path.GetTempFileName();

        if (cacheStream.ContentType.ToLower() == MimeTypes.MimeTypeZip ||
            cacheStream.ContentType.ToLower() == MimeTypes.MimeTypeXZipCompressed)
        {
            string contentType = MimeTypes.MimeTypeJson;
            await _compressionService.ExtractFileFromZipAsync(cacheStream.Stream, cacheStream.ContentType, files =>
            {
                var compressedFiles = files as CompressedFile[] ?? files.ToArray();
                var jsonFile = compressedFiles.FirstOrDefault(x => Path.GetExtension(x.Name).ToLower() == ".json");
                if (jsonFile == null)
                {
                    contentType = MimeTypes.MimeTypeYaml;
                    return compressedFiles.FirstOrDefault(x => Path.GetExtension(x.Name).ToLower() == ".yaml");
                }

                return null;
            }, tempFile);
            return new Tuple<string, string>(tempFile, contentType);
        }

        if (cacheStream.ContentType.ToLower() == MimeTypes.MimeTypeJson ||
            cacheStream.ContentType.ToLower() == MimeTypes.MimeTypeYaml ||
            cacheStream.ContentType.ToLower() == MimeTypes.Unknown)
        {
            var contentType = cacheStream.ContentType;
            if (cacheStream.ContentType.ToLower() == MimeTypes.Unknown)
            {
                contentType = MimeTypes.MimeTypeYaml;
            }

            await using var streamWriter = new StreamWriter(tempFile);
            await cacheStream.Stream.CopyToAsync(streamWriter.BaseStream);
            return new Tuple<string, string>(tempFile, contentType);
        }

        throw JobFailedException.ContentTypeNotSupported(cacheStream.ContentType);
    }

    private async Task ClearCache(string tenantId, string key)
    {
        await _distributedCacheService.DeleteCacheStreamAsync(tenantId, key);
    }
}