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
    private readonly IReadOnlyList<TimeSpan> _cacheLookupRetryDelays;

    /// <summary>
    ///     Waits between the lookups of a cache stream that is not (yet) there (AB#7256): the import is enqueued right
    ///     after the asset repository wrote the file, so the first lookup can be early. Three lookups in total.
    /// </summary>
    internal static readonly IReadOnlyList<TimeSpan> DefaultCacheLookupRetryDelays =
        [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)];

    /// <summary>
    ///     Constructor
    /// </summary>
    /// <param name="logger">Instance of the logger</param>
    /// <param name="systemContext">System context object</param>
    /// <param name="distributedCacheService">Service for distributed caching</param>
    /// <param name="compressionService">Service for compressing and decompressing files</param>
    /// <param name="importCkModelCommand">Command to import a CK model</param>
    /// <param name="importRtModelCommand">Command to import an RT model</param>
    /// <param name="cacheLookupRetryDelays">Waits between repeated lookups of a missing cache stream (tests only)</param>
    public ImportModelJob(ILogger<ImportModelJob> logger, ISystemContext systemContext,
        IDistributedCacheService distributedCacheService, ICompressionService compressionService,
        IImportCkModelCommand importCkModelCommand,
        IImportRtModelCommand importRtModelCommand,
        IReadOnlyList<TimeSpan>? cacheLookupRetryDelays = null)
    {
        _logger = logger;
        _systemContext = systemContext;
        _distributedCacheService = distributedCacheService;
        _compressionService = compressionService;
        _importCkModelCommand = importCkModelCommand;
        _importRtModelCommand = importRtModelCommand;
        _cacheLookupRetryDelays = cacheLookupRetryDelays ?? DefaultCacheLookupRetryDelays;
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
    public async Task ImportCkBatchAsync(string tenantId, List<string> keys,
        IBotCancellationToken? cancellationToken)
    {
        _logger.LogInformation(
            "Starting sequential batch import of {Count} CK models to '{TenantId}'",
            keys.Count, tenantId);

        for (var i = 0; i < keys.Count; i++)
        {
            var key = keys[i];
            try
            {
                _logger.LogInformation(
                    "Batch import step {Step}/{Total}: Reading input file from cache for CK import to '{TenantId}'",
                    i + 1, keys.Count, tenantId);

                var tempFile = await GetTempFile(tenantId, key);

                _logger.LogInformation(
                    "Batch import step {Step}/{Total}: Starting import of file '{TempFile}'",
                    i + 1, keys.Count, tempFile);

                await _importCkModelCommand.ImportAsync(tenantId, tempFile.Item1,
                    cancellationToken?.ShutdownToken);

                _logger.LogInformation(
                    "Batch import step {Step}/{Total}: Import of file '{TempFile}' completed",
                    i + 1, keys.Count, tempFile);
            }
            catch (Exception e)
            {
                _logger.LogError(e,
                    "Batch import step {Step}/{Total}: Import failed with error",
                    i + 1, keys.Count);
                throw;
            }
        }

        // AB#7256: the cache streams are cleared only when every step succeeded, so that a retry of the job (see
        // IImportModelJob) finds all of them again. After a failure they expire with the cache (one hour).
        foreach (var key in keys)
        {
            await ClearCache(tenantId, key);
        }

        _logger.LogInformation(
            "Sequential batch import of {Count} CK models to '{TenantId}' completed",
            keys.Count, tenantId);
    }

    /// <inheritdoc />
    public Task ImportRtAsync(string tenantId, ImportStrategy importStrategy, string key,
        IBotCancellationToken? cancellationToken)
    {
        // A job enqueued before AB#6392 carries no caller. It is still a user import: protect, never system.
        return ImportRtAsync(tenantId, importStrategy, key, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ImportRtAsync(string tenantId, ImportStrategy importStrategy, string key,
        string? initiatedBySubjectId, IBotCancellationToken? cancellationToken)
    {
        try
        {
            _logger.LogInformation("Reading input file from cache for RT import to \'{TenantId}\'", tenantId);
            var tempFile = await GetTempFile(tenantId, key);

            _logger.LogInformation("Starting import of file \'{TempFile}\'", tempFile);

            var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);
            // AB#6392: the session of the import stays the system session; the caller context only adds the
            // blueprint-lock protection of opted-in CK types.
            await _importRtModelCommand.ImportAsCallerAsync(tenantRepository, tempFile.Item1, tempFile.Item2,
                importStrategy, RtImportCallerContext.UserInitiated(initiatedBySubjectId),
                cancellationToken?.ShutdownToken);

            var lockSummary = _importRtModelCommand.BlueprintLockSummary;
            if (lockSummary.HasFindings)
            {
                _logger.LogWarning(
                    "Import to \'{TenantId}\' by \'{SubjectId}\': {StrippedAttributes} blueprint attribute value(s) of {StrippedEntities} entity(ies) were removed, {Audited} locked entity(ies) were overwritten under an audit-only policy",
                    tenantId, initiatedBySubjectId, lockSummary.StrippedAttributeCount,
                    lockSummary.StrippedEntityCount, lockSummary.AuditedViolationCount);
            }

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
        for (var attempt = 0; cacheStream == null && attempt < _cacheLookupRetryDelays.Count; attempt++)
        {
            _logger.LogWarning(
                "Cache stream '{Key}' of tenant '{TenantId}' not found, looking again in {Delay} (lookup {Attempt}/{Total})",
                key, tenantId, _cacheLookupRetryDelays[attempt], attempt + 2, _cacheLookupRetryDelays.Count + 1);
            await Task.Delay(_cacheLookupRetryDelays[attempt]);
            cacheStream = await _distributedCacheService.GetCacheStreamByIdAsync(tenantId, key);
        }

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