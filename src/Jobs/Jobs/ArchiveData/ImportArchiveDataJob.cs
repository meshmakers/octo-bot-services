using System.IO.Compression;
using System.Runtime.CompilerServices;
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

    private readonly ILogger<ImportArchiveDataJob> _logger;
    private readonly ISystemContext _systemContext;
    private readonly IBackupFileStorageService _backupFileStorage;
    private readonly IBotArtifactStorage _artifactStorage;
    private readonly ISharedUploadStore _sharedUploads;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="systemContext">Access to the tenant contexts.</param>
    /// <param name="backupFileStorage">Local tus directory (uploads received before AB#8736).</param>
    /// <param name="artifactStorage">The artifact store (staged files and the scratch directory, AB#8736).</param>
    /// <param name="sharedUploads">The tus uploads in the artifact store (AB#8736).</param>
    public ImportArchiveDataJob(
        ILogger<ImportArchiveDataJob> logger,
        ISystemContext systemContext,
        IBackupFileStorageService backupFileStorage,
        IBotArtifactStorage artifactStorage,
        ISharedUploadStore sharedUploads)
    {
        _logger = logger;
        _systemContext = systemContext;
        _backupFileStorage = backupFileStorage;
        _artifactStorage = artifactStorage;
        _sharedUploads = sharedUploads;
    }

    /// <inheritdoc />
    public async Task Run(string tenantId, string archiveRtId, string tusFileId,
        ArchiveImportMode mode, IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;
        string? stagedFileName = null;
        string? scratchPath = null;
        string? localUploadPath = null;

        try
        {
            // AB#8736: the upload is in the artifact store, so this replica need not be the one that received it. The
            // ZIP reader needs a seekable file: the plaintext goes to the scratch directory. Order: a staged file
            // (restore-staging/<tenant>/<id>[.octoenc]), the parts of a complete tus upload, a file received on the
            // local disk before AB#8736.
            string uploadPath;
            var staged = await _sharedUploads.FindStagedAsync(tenantId, tusFileId, ct);
            if (staged != null)
            {
                stagedFileName = staged.Value.FileName;
                scratchPath = _artifactStorage.CreateScratchFilePath(".archive-import.zip");
                _logger.LogInformation(
                    "Reading staged upload '{FileName}' of tenant '{TenantId}' from the artifact store",
                    stagedFileName, tenantId);
                if (!await _artifactStorage.TryWritePlainToFileAsync(SharedUploadStore.Category, tenantId,
                        stagedFileName, scratchPath, ct))
                {
                    throw new JobFailedException(
                        $"The staged upload for tus file ID '{tusFileId}' disappeared from the artifact store " +
                        "(expired?). Upload the archive data file again.");
                }

                uploadPath = scratchPath;
            }
            else if (await _sharedUploads.IsCompleteAsync(tenantId, tusFileId, ct))
            {
                scratchPath = _artifactStorage.CreateScratchFilePath(".archive-import.zip");
                _logger.LogInformation(
                    "Reading upload '{UploadId}' of tenant '{TenantId}' from the artifact store", tusFileId, tenantId);
                await using (var scratch = OwnerOnlyFiles.CreateNew(scratchPath))
                {
                    if (!await _sharedUploads.TryCopyCompleteUploadAsync(tenantId, tusFileId, scratch, ct))
                    {
                        throw new JobFailedException(
                            $"The upload for tus file ID '{tusFileId}' disappeared from the artifact store " +
                            "(expired?). Upload the archive data file again.");
                    }
                }

                uploadPath = scratchPath;
            }
            else
            {
                localUploadPath = ResolveLocalUpload(tenantId, tusFileId);
                if (localUploadPath == null || !File.Exists(localUploadPath))
                {
                    throw new JobFailedException(Path.IsPathRooted(tusFileId)
                        ? $"The job was queued before AB#8736 with the local upload path '{tusFileId}', and the file " +
                          "is not on this replica (it was received by another pod). Upload the archive data file again."
                        : $"Uploaded archive data file not found for tus file ID '{tusFileId}', or the upload is not " +
                          "complete.");
                }

                uploadPath = localUploadPath;
            }

            var fileInfo = new FileInfo(uploadPath);
            if (fileInfo.Length == 0)
            {
                throw new JobFailedException(
                    $"Uploaded archive data file for tus file ID '{tusFileId}' is empty (0 bytes). " +
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
                new FileStream(uploadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
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

            // 3. §7.1 guard (enforced): the target archive MUST be Disabled during import so no live
            //    inserts/queries (or, for rollups, orchestrator re-aggregation) race the bulk load. The
            //    CrateDB table is preserved while Disabled. We fail fast rather than auto-disabling —
            //    auto-disabling would be an unrequested, potentially state-corrupting side effect; the
            //    Studio orchestrates the disable -> import -> re-enable flow.
            if (snapshot.Status != Meshmakers.Octo.Runtime.Contracts.StreamData.CkArchiveStatus.Disabled)
            {
                throw new JobFailedException(
                    $"Archive '{snapshot.RtWellKnownName ?? archiveRtId}' must be Disabled before importing data " +
                    $"(it is currently {snapshot.Status}). Disable the archive, import the data, then re-enable it.");
            }

            // 3b. Disabled does not imply a provisioned table (AB#5141): a blueprint seeds archives
            //     Disabled without ever activating them. Importing into a missing table would fail deep in
            //     the insert path with CrateDB's RelationUnknown; fail fast with the actionable sequence.
            var storageStats = await repository.GetArchiveStatsAsync(new[] { archiveObjectId }, ct);
            if (!storageStats.TryGetValue(archiveObjectId, out var storage) || !storage.TableExists)
            {
                throw new JobFailedException(
                    $"Archive '{snapshot.RtWellKnownName ?? archiveRtId}' has no storage table yet (it has never been " +
                    "activated). Activate the archive once to provision its table, disable it again, then import " +
                    "the data.");
            }

            var isRollup = snapshot.RollupAggregations is not null;
            _logger.LogInformation(
                "Importing archive data into '{ArchiveRtId}' (kind '{Kind}', mode '{Mode}').",
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
                await repository.ImportRowsAsync(archiveObjectId,
                    NdjsonRowReader.ReadRowsAsync(dataStream, onRow: null, ct), engineMode, ct);
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
            // The staged upload is consumed by this run, whether it succeeded or failed (like the restore).
            await CleanUpAsync(tenantId, tusFileId, stagedFileName, scratchPath, localUploadPath);
        }
    }

    /// <summary>
    ///     The path of an upload received on the local disk before AB#8736, or <c>null</c> when the id cannot name one
    ///     (it must stay a single file name inside the tenant's tus directory). A job queued before AB#8736 carries the
    ///     full path the old producer resolved; it is accepted only when it is exactly the tus file of that tenant.
    /// </summary>
    private string? ResolveLocalUpload(string tenantId, string tusFileId)
    {
        if (!string.IsNullOrWhiteSpace(tusFileId) && Path.IsPathRooted(tusFileId))
        {
            return ResolveLegacyQueuedPath(tenantId, tusFileId);
        }

        if (string.IsNullOrWhiteSpace(tusFileId) || tusFileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            tusFileId.Contains(Path.DirectorySeparatorChar) || tusFileId.Contains(Path.AltDirectorySeparatorChar) ||
            tusFileId is "." or "..")
        {
            return null;
        }

        try
        {
            return _backupFileStorage.GetTusUploadFilePath(tenantId, tusFileId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string? ResolveLegacyQueuedPath(string tenantId, string queuedPath)
    {
        var fileName = Path.GetFileName(queuedPath);
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        try
        {
            var expected = Path.GetFullPath(_backupFileStorage.GetTusUploadFilePath(tenantId, fileName));
            return string.Equals(Path.GetFullPath(queuedPath), expected, StringComparison.Ordinal) ? expected : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task CleanUpAsync(string tenantId, string tusFileId, string? stagedFileName, string? scratchPath,
        string? localUploadPath)
    {
        _artifactStorage.DeleteScratchFile(scratchPath);

        if (stagedFileName != null)
        {
            try
            {
                await _artifactStorage.DeleteAsync(SharedUploadStore.Category, tenantId, stagedFileName);
            }
            catch (Exception e)
            {
                // It expires with the retention of the category.
                _logger.LogWarning(e, "Could not delete the staged upload '{FileName}' of tenant '{TenantId}'",
                    stagedFileName, tenantId);
            }
        }

        try
        {
            // The tus upload itself (description and parts), complete or not.
            await _sharedUploads.DeleteAsync(tenantId, tusFileId);
        }
        catch (Exception e)
        {
            // They expire with the retention of the category.
            _logger.LogWarning(e, "Could not delete the parts of upload '{UploadId}' of tenant '{TenantId}'",
                tusFileId, tenantId);
        }

        if (localUploadPath != null)
        {
            await _backupFileStorage.DeleteFileAsync(localUploadPath);
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
