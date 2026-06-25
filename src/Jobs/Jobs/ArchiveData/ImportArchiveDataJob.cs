using System.IO.Compression;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Hangfire job that imports archive data rows from an uploaded export ZIP. Reads
///     <c>metadata.json</c>, performs strict §6 schema-match validation against the live target
///     schema, then streams <c>data.ndjson</c> to the asset-repo import endpoint. For rollup
///     archives it freezes the imported window afterwards (§7). The uploaded file is always deleted
///     on completion (mirrors <c>RestoreRepositoryJob</c>). Archive data export/import concept
///     (AB#4230) §5.1, §6, §7.
/// </summary>
public sealed class ImportArchiveDataJob : IImportArchiveDataJob
{
    private const int SupportedFormatVersion = 1;
    private const string RollupKind = "rollup";

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ImportArchiveDataJob> _logger;
    private readonly IBackupFileStorageService _backupFileStorage;
    private readonly IArchiveDataClientFactory _clientFactory;

    /// <summary>
    ///     Constructor.
    /// </summary>
    public ImportArchiveDataJob(
        ILogger<ImportArchiveDataJob> logger,
        IBackupFileStorageService backupFileStorage,
        IArchiveDataClientFactory clientFactory)
    {
        _logger = logger;
        _backupFileStorage = backupFileStorage;
        _clientFactory = clientFactory;
    }

    /// <inheritdoc />
    public async Task Run(string tenantId, string archiveRtId, string uploadedTusFilePath, string accessToken,
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

            var client = _clientFactory.Create(tenantId, accessToken);

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

            // 2. Schema-match validation (§6) against the live target schema.
            _logger.LogInformation(
                "Validating import schema for archive '{ArchiveRtId}' of tenant '{TenantId}'", archiveRtId, tenantId);

            var targetSchema = await client.GetArchiveSchemaAsync(tenantId, archiveRtId, ct);

            var mismatch = ArchiveSchemaMatcher.FindMismatch(metadata.Archive, targetSchema);
            if (mismatch != null)
            {
                throw new JobFailedException(mismatch);
            }

            // 3. §7 guard: surface the disabled-archive precondition. The SDK exposes no read of a
            //    source archive's lifecycle status, so the bot cannot verify "Disabled" itself; the
            //    Studio orchestrates the disable -> import -> re-enable flow. We do NOT auto-disable
            //    (that would be an unrequested, potentially state-corrupting side effect), but we log
            //    the precondition loudly so a misuse is traceable.
            var isRollup = string.Equals(targetSchema.Kind, RollupKind, StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation(
                "Importing archive data into '{ArchiveRtId}' (kind '{Kind}', mode '{Mode}'). " +
                "Precondition: the target archive must be Disabled during import (concept §7.1).",
                archiveRtId, targetSchema.Kind, mode);

            // 4. Stream data.ndjson to the import endpoint.
            var dataEntry = archive.GetEntry("data.ndjson")
                            ?? throw new JobFailedException(
                                "The uploaded ZIP does not contain a 'data.ndjson' entry. The file is not a valid " +
                                "archive data export.");

            await using (var dataStream = dataEntry.Open())
            {
                await client.ImportArchiveRowsAsync(tenantId, archiveRtId, dataStream, mode, ct);
            }

            // 5. §7.2 — for rollups, freeze the imported window so the orchestrator does not
            //    re-aggregate over the imported buckets after the archive is re-enabled.
            if (isRollup)
            {
                await FreezeImportedRollupWindowAsync(client, tenantId, archiveRtId, metadata);
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

    private async Task FreezeImportedRollupWindowAsync(IStreamDataServicesClient client, string tenantId,
        string archiveRtId, ArchiveExportMetadata metadata)
    {
        // Freeze up to the upper bound of the imported window, or up to "now" when the whole archive
        // was imported (covers every imported bucket; FreezeRollupArchiveAsync is monotonic).
        var until = metadata.Window?.ToUtc ?? DateTime.UtcNow;

        _logger.LogInformation(
            "Freezing rollup archive '{ArchiveRtId}' of tenant '{TenantId}' until {Until:O} to protect the imported " +
            "window from re-aggregation (concept §7.2)", archiveRtId, tenantId, until);

        await client.FreezeRollupArchiveAsync(tenantId, archiveRtId, until);
    }
}
