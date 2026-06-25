using System.IO.Compression;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Hangfire job that exports an archive's CrateDB rows to a downloadable ZIP. The row I/O is
///     delegated to <c>octo-asset-repo-services</c> over HTTP via the SDK; the NDJSON body is pumped
///     straight into the ZIP entry without buffering the whole dataset. Archive data export/import
///     concept (AB#4230) §5.1.
/// </summary>
public sealed class ExportArchiveDataJob : IExportArchiveDataJob
{
    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<ExportArchiveDataJob> _logger;
    private readonly IBackupFileStorageService _backupFileStorage;
    private readonly IArchiveDataClientFactory _clientFactory;

    /// <summary>
    ///     Constructor.
    /// </summary>
    public ExportArchiveDataJob(
        ILogger<ExportArchiveDataJob> logger,
        IBackupFileStorageService backupFileStorage,
        IArchiveDataClientFactory clientFactory)
    {
        _logger = logger;
        _backupFileStorage = backupFileStorage;
        _clientFactory = clientFactory;
    }

    /// <inheritdoc />
    public async Task<string?> Run(string tenantId, string archiveRtId, string accessToken, DateTime? fromUtc,
        DateTime? toUtc, IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;

        try
        {
            var client = _clientFactory.Create(tenantId, accessToken);

            _logger.LogInformation(
                "Fetching schema for archive '{ArchiveRtId}' of tenant '{TenantId}' for data export", archiveRtId,
                tenantId);

            var schema = await client.GetArchiveSchemaAsync(tenantId, archiveRtId, ct);

            var window = fromUtc.HasValue && toUtc.HasValue
                ? new ArchiveExportWindow(fromUtc.Value.ToUniversalTime(), toUtc.Value.ToUniversalTime())
                : null;

            var metadata = new ArchiveExportMetadata(
                FormatVersion: CurrentFormatVersion,
                ExportedAtUtc: DateTime.UtcNow,
                SourceTenantId: tenantId,
                Archive: schema,
                Window: window,
                RowCount: null);

            var fileName = BuildFileName(schema, archiveRtId);
            var filePath = _backupFileStorage.GetDumpFilePath(tenantId, fileName);

            var directory = Path.GetDirectoryName(filePath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            _logger.LogInformation("Writing archive data export for '{ArchiveRtId}' to '{FilePath}'", archiveRtId,
                filePath);

            await using (var zipFileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipFileStream, ZipArchiveMode.Create))
            {
                // metadata.json — read first on import.
                var metadataEntry = archive.CreateEntry("metadata.json", CompressionLevel.Optimal);
                await using (var metadataStream = metadataEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(metadataStream, metadata, MetadataJsonOptions, ct);
                }

                ct.ThrowIfCancellationRequested();

                // data.ndjson — pump the live NDJSON body straight through (no buffering).
                var dataEntry = archive.CreateEntry("data.ndjson", CompressionLevel.Optimal);
                await using (var dataEntryStream = dataEntry.Open())
                await using (var ndjsonStream = await client.ExportArchiveRowsAsync(tenantId, archiveRtId, fromUtc,
                                 toUtc, ct))
                {
                    await ndjsonStream.CopyToAsync(dataEntryStream, ct);
                }
            }

            _logger.LogInformation("Archive data export completed for '{ArchiveRtId}' at '{FilePath}'", archiveRtId,
                filePath);

            return filePath;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Archive data export for '{ArchiveRtId}' of tenant '{TenantId}' was cancelled",
                archiveRtId, tenantId);
            throw;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while exporting archive data '{ArchiveRtId}' for tenant '{TenantId}'",
                archiveRtId, tenantId);
            throw;
        }
    }

    private static string BuildFileName(ArchiveSchemaDto schema, string archiveRtId)
    {
        var label = string.IsNullOrWhiteSpace(schema.RtWellKnownName) ? archiveRtId : schema.RtWellKnownName;
        var safeLabel = Sanitize(label);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var guid = Guid.NewGuid().ToString("N")[..8];
        return $"export-{safeLabel}-{timestamp}-{guid}.zip";
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
