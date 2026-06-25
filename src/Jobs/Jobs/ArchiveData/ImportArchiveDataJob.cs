using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;
using ArchiveImportMode = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ArchiveImportMode;
using EngineArchiveImportMode = Meshmakers.Octo.Runtime.Contracts.StreamData.ArchiveImportMode;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Hangfire job that imports archive data rows from an uploaded export ZIP. Reads
///     <c>metadata.json</c>, performs strict §6 schema-match validation against the live target
///     archive (read <b>directly</b> from the tenant's <c>IArchiveRuntimeStore</c> via
///     <see cref="ISystemContext"/>), then streams <c>data.ndjson</c> directly into the tenant's
///     CrateDB-backed stream-data repository — no asset-repo HTTP hop. For rollup archives it freezes
///     the imported window afterwards using the engine's rollup lifecycle service in-process (§7). The
///     uploaded file is always deleted on completion (mirrors <c>RestoreRepositoryJob</c>). Archive
///     data export/import concept (AB#4230) §4.1, §5.1, §6, §7.
/// </summary>
public sealed class ImportArchiveDataJob : IImportArchiveDataJob
{
    private const int SupportedFormatVersion = 1;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions NdjsonJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ImportArchiveDataJob> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IBackupFileStorageService _backupFileStorage;

    /// <summary>
    ///     Constructor.
    /// </summary>
    public ImportArchiveDataJob(
        ILogger<ImportArchiveDataJob> logger,
        ISystemContext systemContext,
        IBackupFileStorageService backupFileStorage)
    {
        _logger = logger;
        _systemContext = systemContext;
        _backupFileStorage = backupFileStorage;
    }

    /// <inheritdoc />
    public async Task Run(string tenantId, string archiveRtId, string uploadedTusFilePath,
        ArchiveImportMode mode, IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;

        try
        {
            if (!File.Exists(uploadedTusFilePath))
            {
                throw new JobFailedException(
                    $"Uploaded archive data file not found at '{uploadedTusFilePath}'. " +
                    "The upload may not have completed successfully.");
            }

            var fileInfo = new FileInfo(uploadedTusFilePath);
            if (fileInfo.Length == 0)
            {
                throw new JobFailedException(
                    $"Uploaded archive data file at '{uploadedTusFilePath}' is empty (0 bytes). " +
                    "The upload may not have completed successfully.");
            }

            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId)
                                ?? throw new JobFailedException(
                                    $"Tenant context not found for tenant '{tenantId}'.");

            var repository = tenantContext.GetStreamDataRepository()
                             ?? throw new JobFailedException(
                                 $"StreamData is not enabled for tenant '{tenantId}'. Enable stream data before " +
                                 "importing archive data.");

            var archiveObjectId = new OctoObjectId(archiveRtId);

            // 1. Open the ZIP and read metadata.json.
            await using var zipFileStream =
                new FileStream(uploadedTusFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(zipFileStream, ZipArchiveMode.Read);

            var metadata = await ReadMetadataAsync(archive, ct);

            if (metadata.FormatVersion != SupportedFormatVersion)
            {
                throw new JobFailedException(
                    $"Unsupported export format version {metadata.FormatVersion}. " +
                    $"This service supports format version {SupportedFormatVersion} only.");
            }

            // 2. Schema-match validation (§6) against the live target schema, read directly from the
            //    tenant's archive runtime store.
            _logger.LogInformation(
                "Validating import schema for archive '{ArchiveRtId}' of tenant '{TenantId}'", archiveRtId, tenantId);

            var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(archiveObjectId)
                           ?? throw new JobFailedException(
                               $"Archive '{archiveRtId}' was not found in tenant '{tenantId}'.");

            var targetSchema = ArchiveSchemaMapper.ToDto(snapshot);

            var mismatch = ArchiveSchemaMatcher.FindMismatch(metadata.Archive, targetSchema);
            if (mismatch != null)
            {
                throw new JobFailedException(mismatch);
            }

            // 3. §7 guard: surface the disabled-archive precondition. The Studio orchestrates the
            //    disable -> import -> re-enable flow. We do NOT auto-disable (that would be an
            //    unrequested, potentially state-corrupting side effect), but we log the precondition
            //    loudly so a misuse is traceable.
            var isRollup = snapshot.RollupAggregations is not null;
            _logger.LogInformation(
                "Importing archive data into '{ArchiveRtId}' (kind '{Kind}', mode '{Mode}'). " +
                "Precondition: the target archive must be Disabled during import (concept §7.1).",
                archiveRtId, targetSchema.Kind, mode);

            // 4. Stream data.ndjson directly into the stream-data repository.
            var dataEntry = archive.GetEntry("data.ndjson")
                            ?? throw new JobFailedException(
                                "The uploaded ZIP does not contain a 'data.ndjson' entry. The file is not a valid " +
                                "archive data export.");

            var engineMode = mode == ArchiveImportMode.Upsert
                ? EngineArchiveImportMode.Upsert
                : EngineArchiveImportMode.InsertOnly;

            await using (var dataStream = dataEntry.Open())
            {
                await repository.ImportRowsAsync(archiveObjectId, ReadNdjsonRowsAsync(dataStream, ct), engineMode, ct);
            }

            // 5. §7.2 — for rollups, freeze the imported window so the orchestrator does not
            //    re-aggregate over the imported buckets after the archive is re-enabled.
            if (isRollup)
            {
                await FreezeImportedRollupWindowAsync(tenantContext, tenantId, archiveObjectId, metadata);
            }

            _logger.LogInformation("Archive data import completed for '{ArchiveRtId}' of tenant '{TenantId}'",
                archiveRtId, tenantId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Archive data import for '{ArchiveRtId}' of tenant '{TenantId}' was cancelled",
                archiveRtId, tenantId);
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while importing archive data '{ArchiveRtId}' for tenant '{TenantId}'",
                archiveRtId, tenantId);
            throw;
        }
        finally
        {
            await _backupFileStorage.DeleteFileAsync(uploadedTusFilePath);
        }
    }

    private static async Task<ArchiveExportMetadata> ReadMetadataAsync(ZipArchive archive, CancellationToken ct)
    {
        var metadataEntry = archive.GetEntry("metadata.json")
                            ?? throw new JobFailedException(
                                "The uploaded ZIP does not contain a 'metadata.json' entry. The file is not a valid " +
                                "archive data export.");

        await using var metadataStream = metadataEntry.Open();

        ArchiveExportMetadata? metadata;
        try
        {
            metadata = await JsonSerializer.DeserializeAsync<ArchiveExportMetadata>(metadataStream, MetadataJsonOptions,
                ct);
        }
        catch (JsonException e)
        {
            throw new JobFailedException($"The export 'metadata.json' could not be parsed: {e.Message}", e);
        }

        if (metadata?.Archive == null)
        {
            throw new JobFailedException(
                "The export 'metadata.json' is missing the required 'archive' schema block.");
        }

        return metadata;
    }

    /// <summary>
    ///     Reads the NDJSON data entry one line at a time, deserialising each non-blank line into a row
    ///     dictionary. Streamed (never fully buffered) so multi-GB imports stay flat in memory.
    /// </summary>
    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadNdjsonRowsAsync(
        Stream body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024, leaveOpen: true);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var row = JsonSerializer.Deserialize<Dictionary<string, object?>>(line, NdjsonJsonOptions);
            if (row is not null)
            {
                yield return row;
            }
        }
    }

    private async Task FreezeImportedRollupWindowAsync(ITenantContext tenantContext, string tenantId,
        OctoObjectId archiveRtId, ArchiveExportMetadata metadata)
    {
        // Freeze up to the upper bound of the imported window, or up to "now" when the whole archive
        // was imported (covers every imported bucket; FreezeAsync is monotonic).
        var until = metadata.Window?.ToUtc ?? DateTime.UtcNow;

        var lifecycle = tenantContext.GetRollupArchiveLifecycleService();
        if (lifecycle is null)
        {
            // The repository accepted the rollup rows (it resolved a stream-data repository), but no
            // rollup lifecycle service is wired for this tenant. Fail loud rather than silently leaving
            // the imported window exposed to re-aggregation once the archive is re-enabled (concept §7.2).
            _logger.LogWarning(
                "Rollup lifecycle service is not available for tenant '{TenantId}'; the imported window of rollup " +
                "archive '{ArchiveRtId}' was NOT frozen. Re-aggregation may overwrite the imported buckets once the " +
                "archive is re-enabled (concept §7.2). Freeze the archive manually until {Until:O}.",
                tenantId, archiveRtId, until);
            return;
        }

        _logger.LogInformation(
            "Freezing rollup archive '{ArchiveRtId}' of tenant '{TenantId}' until {Until:O} to protect the imported " +
            "window from re-aggregation (concept §7.2)", archiveRtId, tenantId, until);

        await lifecycle.FreezeAsync(archiveRtId, until);
    }
}
