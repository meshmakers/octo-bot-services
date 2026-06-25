using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;

/// <summary>
///     Hangfire job that exports an archive's CrateDB rows to a downloadable ZIP. The row I/O is done
///     <b>directly</b> against the tenant's CrateDB-backed <see cref="IStreamDataRepository"/> obtained
///     through <see cref="ISystemContext"/> — the same way <c>DumpRepositoryJob</c> talks to MongoDB
///     directly — instead of calling the asset-repo REST endpoints over HTTP. The NDJSON body is
///     streamed straight into the ZIP entry without buffering the whole dataset. Archive data
///     export/import concept (AB#4230) §4.1 / §5.1.
/// </summary>
public sealed class ExportArchiveDataJob : IExportArchiveDataJob
{
    private const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions NdjsonJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<ExportArchiveDataJob> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IBackupFileStorageService _backupFileStorage;

    /// <summary>
    ///     Constructor.
    /// </summary>
    public ExportArchiveDataJob(
        ILogger<ExportArchiveDataJob> logger,
        ISystemContext systemContext,
        IBackupFileStorageService backupFileStorage)
    {
        _logger = logger;
        _systemContext = systemContext;
        _backupFileStorage = backupFileStorage;
    }

    /// <inheritdoc />
    public async Task<string?> Run(string tenantId, string archiveRtId, DateTime? fromUtc,
        DateTime? toUtc, IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;

        try
        {
            var tenantContext = await _systemContext.FindTenantContextAsync(tenantId)
                                ?? throw new JobFailedException(
                                    $"Tenant context not found for tenant '{tenantId}'.");

            var repository = tenantContext.GetStreamDataRepository()
                             ?? throw new JobFailedException(
                                 $"StreamData is not enabled for tenant '{tenantId}'. Enable stream data before " +
                                 "exporting archive data.");

            _logger.LogInformation(
                "Reading schema for archive '{ArchiveRtId}' of tenant '{TenantId}' for data export", archiveRtId,
                tenantId);

            var archiveObjectId = new OctoObjectId(archiveRtId);

            var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(archiveObjectId)
                           ?? throw new JobFailedException(
                               $"Archive '{archiveRtId}' was not found in tenant '{tenantId}'.");

            var schema = ArchiveSchemaMapper.ToDto(snapshot);

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

            var rowWindow = BuildRowWindow(fromUtc, toUtc);

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

                // data.ndjson — pump the rows straight through as NDJSON (one row per line, no buffering
                // of the whole dataset). Each row dictionary is serialized exactly like the asset-repo
                // export-stream endpoint did, so the ZIP body is unchanged.
                var dataEntry = archive.CreateEntry("data.ndjson", CompressionLevel.Optimal);
                await using (var dataEntryStream = dataEntry.Open())
                await using (var writer = new StreamWriter(dataEntryStream, new UTF8Encoding(false), 64 * 1024))
                {
                    // Force '\n' line endings so the NDJSON body is deterministic across platforms.
                    writer.NewLine = "\n";

                    var rowsSinceFlush = 0;
                    await foreach (var row in repository.ExportRowsAsync(archiveObjectId, rowWindow, ct))
                    {
                        var line = JsonSerializer.Serialize(row, NdjsonJsonOptions);
                        await writer.WriteLineAsync(line.AsMemory(), ct);

                        if (++rowsSinceFlush >= 256)
                        {
                            await writer.FlushAsync(ct);
                            rowsSinceFlush = 0;
                        }
                    }

                    await writer.FlushAsync(ct);
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

    /// <summary>
    ///     Builds the half-open <c>[from, to)</c> row window passed to <see cref="IStreamDataRepository"/>.
    ///     Both bounds omitted ⇒ whole archive; one supplied ⇒ the other treated as open (concept §4.2).
    /// </summary>
    private static TimeWindow? BuildRowWindow(DateTime? fromUtc, DateTime? toUtc)
    {
        if (fromUtc is null && toUtc is null)
        {
            return null;
        }

        var from = (fromUtc ?? DateTime.MinValue).ToUniversalTime();
        var to = (toUtc ?? DateTime.MaxValue).ToUniversalTime();
        return new TimeWindow(from, to);
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
