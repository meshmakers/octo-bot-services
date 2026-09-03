using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that restores a tenant database from a backup file. Auto-detects whether the
/// uploaded file is a legacy mongodump <c>.tar.gz</c> (restored exactly as before) or an
/// <c>.octobak.zip</c> container carrying archive data (concept AB#4231 §5). When the artifact is an
/// <c>.octobak</c> and <c>restoreArchiveData</c> is set, each archive in the backup is restored into
/// CrateDB via a clean drop/recreate/import sequence (concept §5.1); per-archive failures are
/// recorded and the job still succeeds (continue + report, §2 decision #4).
/// </summary>
public class RestoreRepositoryJob(
    ILogger<RestoreRepositoryJob> logger,
    ISystemContext systemContext,
    IBackupFileStorageService backupFileStorage) : IRestoreRepositoryJob
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task Run(string tenantId, string databaseName, string cacheKey,
        string? oldDatabaseName, bool restoreArchiveData,
        IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;

        // cacheKey is used as the tus file ID (or legacy cache key). The tenant is part of the
        // address since AB#5060: uploads live under a per-tenant directory, so a restore can only
        // ever resolve a file staged for the very tenant it is restoring.
        var filePath = backupFileStorage.GetTusUploadFilePath(tenantId, cacheKey);

        try
        {
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                return;
            }

            if (!File.Exists(filePath))
            {
                throw new JobFailedException(
                    $"Backup file not found at '{filePath}' for tus file ID '{cacheKey}'.");
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0)
            {
                throw new JobFailedException(
                    $"Backup file at '{filePath}' for tus file ID '{cacheKey}' is empty (0 bytes). The upload may not have completed successfully.");
            }

            // Auto-detect the artifact format: an .octobak is a ZIP carrying manifest.json; anything
            // else is treated as a legacy mongodump .tar.gz (today's path, unchanged).
            var manifest = TryReadManifest(filePath);

            if (manifest is null)
            {
                if (restoreArchiveData)
                {
                    logger.LogWarning(
                        "restoreArchiveData was requested for tenant '{TenantId}' but the uploaded artifact is a " +
                        "legacy mongodump (no manifest.json); restoring Mongo only.", tenantId);
                }

                await RestoreMongoAsync(tenantId, databaseName, filePath, oldDatabaseName, ct);
                return;
            }

            await RestoreOctoBakAsync(tenantId, databaseName, oldDatabaseName, filePath, manifest, restoreArchiveData,
                ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while restoring database for tenant '{TenantId}'", tenantId);
            throw;
        }
        finally
        {
            await backupFileStorage.DeleteFileAsync(filePath);
        }
    }

    /// <summary>
    ///     Restores an <c>.octobak.zip</c>: extracts the mongo blob, runs the Mongo restore unchanged,
    ///     then — when <paramref name="restoreArchiveData"/> is set — restores each archive's CrateDB
    ///     rows via the clean per-archive sequence (concept §5/§5.1).
    /// </summary>
    private async Task RestoreOctoBakAsync(string tenantId, string databaseName, string? oldDatabaseName,
        string filePath, BackupManifest manifest, bool restoreArchiveData, CancellationToken ct)
    {
        logger.LogInformation(
            "Detected .octobak backup for tenant '{TenantId}' (formatVersion {Version}, {ArchiveCount} archive(s))",
            tenantId, manifest.FormatVersion, manifest.Archives.Count);

        if (manifest.FormatVersion != BackupArchiveContainer.CurrentFormatVersion)
        {
            throw new JobFailedException(
                $"Unsupported backup format version {manifest.FormatVersion}. This service supports format version " +
                $"{BackupArchiveContainer.CurrentFormatVersion} only.");
        }

        await using var zipFileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(zipFileStream, ZipArchiveMode.Read);

        // 1. Extract the verbatim mongo blob to a temp file and feed it to the existing restore path.
        var mongoTempPath = filePath + ".mongo.tar.gz";
        try
        {
            var mongoEntry = zip.GetEntry(BackupArchiveContainer.MongoBlobEntry)
                             ?? throw new JobFailedException(
                                 $"The .octobak container for tenant '{tenantId}' is missing the " +
                                 $"'{BackupArchiveContainer.MongoBlobEntry}' entry.");

            await using (var entryStream = mongoEntry.Open())
            await using (var tempStream = new FileStream(mongoTempPath, FileMode.Create, FileAccess.Write,
                             FileShare.None))
            {
                await entryStream.CopyToAsync(tempStream, ct);
            }

            await RestoreMongoAsync(tenantId, databaseName, mongoTempPath, oldDatabaseName, ct);
        }
        finally
        {
            await backupFileStorage.DeleteFileAsync(mongoTempPath);
        }

        // 2. Archive data is opt-in even when the artifact carries archives.
        if (!restoreArchiveData)
        {
            logger.LogInformation(
                "restoreArchiveData is off; restored Mongo only for tenant '{TenantId}', archives left untouched " +
                "(no Crate tables, identical to legacy behaviour).", tenantId);
            return;
        }

        await RestoreArchivesAsync(tenantId, manifest, zip, ct);
    }

    private async Task RestoreMongoAsync(string tenantId, string databaseName, string blobPath,
        string? oldDatabaseName, CancellationToken ct)
    {
        logger.LogInformation("Running restore command for '{TenantId}' from file '{FilePath}'", tenantId, blobPath);

        var r = await systemContext.RestoreTenantAsync(tenantId, databaseName, blobPath, oldDatabaseName,
            true, true, TimeSpan.FromHours(1), ct);

        if (!r.Success)
        {
            throw JobFailedException.CommandExecutionFailed(r, tenantId, "mongorestore");
        }

        logger.LogInformation("Restored database '{DatabaseName}' for tenant '{TenantId}'", databaseName, tenantId);
    }

    /// <summary>
    ///     Restores every archive in the manifest that exists post-restore, via the clean per-archive
    ///     sequence. One archive's failure is recorded and the loop continues (concept §2 decision #4);
    ///     the job succeeds even with per-archive warnings.
    /// </summary>
    private async Task RestoreArchivesAsync(string tenantId, BackupManifest manifest, ZipArchive zip,
        CancellationToken ct)
    {
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId)
                            ?? throw new JobFailedException(
                                $"Tenant context not found for tenant '{tenantId}' after the Mongo restore.");

        var repository = tenantContext.GetStreamDataRepository();
        var lifecycle = tenantContext.GetArchiveLifecycleService();

        if (repository is null || lifecycle is null)
        {
            logger.LogWarning(
                "restoreArchiveData was requested but stream data is not enabled for tenant '{TenantId}' after the " +
                "Mongo restore; skipping archive data restore. {ArchiveCount} archive(s) in the backup were not " +
                "imported.", tenantId, manifest.Archives.Count);
            return;
        }

        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var results = new List<ArchiveRestoreResult>(manifest.Archives.Count);

        foreach (var entry in manifest.Archives)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RestoreOneArchiveAsync(tenantId, entry, zip, repository, lifecycle, archiveStore, ct));
        }

        LogRestoreSummary(tenantId, results);
    }

    /// <summary>
    ///     Restores a single archive's rows via the clean drop/recreate/import sequence (concept §5.1).
    ///     Wrapped so a failure on one archive is recorded and reported without aborting the whole job.
    /// </summary>
    private async Task<ArchiveRestoreResult> RestoreOneArchiveAsync(string tenantId, BackupManifestArchive entry,
        ZipArchive zip, IStreamDataRepository repository, IArchiveLifecycleService lifecycle,
        IArchiveRuntimeStore archiveStore, CancellationToken ct)
    {
        var rtId = entry.Schema.RtId;

        try
        {
            if (entry.NdjsonEntry is null)
            {
                // The archive had no provisioned Crate table at backup time — nothing to restore.
                return ArchiveRestoreResult.Skipped(rtId,
                    $"no archive data in the backup (archive was '{entry.Status}', had no Crate table)");
            }

            var objectId = new OctoObjectId(rtId);

            var postSnapshot = await archiveStore.GetAsync(objectId);
            if (postSnapshot is null)
            {
                return ArchiveRestoreResult.Skipped(rtId, "archive does not exist in the tenant after the restore");
            }

            // §6 schema-match against the post-restore archive. On a faithful same-tenant restore these
            // match by construction; mismatches arise on cross-tenant / cross-CK-version restores.
            var mismatch = ArchiveSchemaMatcher.FindMismatch(entry.Schema, ArchiveSchemaMapper.ToDto(postSnapshot));
            if (mismatch != null)
            {
                return ArchiveRestoreResult.Skipped(rtId, mismatch);
            }

            var dataEntry = zip.GetEntry(entry.NdjsonEntry);
            if (dataEntry is null)
            {
                return ArchiveRestoreResult.Skipped(rtId,
                    $"NDJSON entry '{entry.NdjsonEntry}' is missing from the backup container");
            }

            // Clean restore (concept §5.1): drop -> recreate -> disable -> import -> restore status.
            // ActivateAsync is a no-op when the archive is already Activated, but after a Mongo-only
            // restore the Crate table does NOT exist even at status Activated. Normalise to Disabled
            // first so the subsequent ActivateAsync actually provisions a fresh table.
            if (postSnapshot.Status == CkArchiveStatus.Activated)
            {
                await lifecycle.DisableAsync(objectId);
            }

            await repository.DeleteArchiveAsync(objectId); // DROP TABLE IF EXISTS — discard stale rows
            await lifecycle.ActivateAsync(objectId); // recreate a fresh table; status -> Activated
            await lifecycle.DisableAsync(objectId); // status -> Disabled (import precondition, AB#4230 §7.1)

            var counter = new RowCounter();
            await using (var dataStream = dataEntry.Open())
            {
                await repository.ImportRowsAsync(objectId,
                    NdjsonRowReader.ReadRowsAsync(dataStream, () => counter.Count++, ct),
                    ArchiveImportMode.InsertOnly, ct);
            }

            // Restore the archive's backed-up status (concept §10): Activated -> re-enable; Disabled ->
            // leave Disabled. Created/Failed never reach here (they carry no NdjsonEntry).
            var backedUpStatus = ParseStatus(entry.Status);
            if (backedUpStatus == CkArchiveStatus.Activated)
            {
                await lifecycle.EnableAsync(objectId);
            }

            return ArchiveRestoreResult.Imported(rtId, counter.Count, backedUpStatus);
        }
        catch (OperationCanceledException)
        {
            throw; // honour cancellation between archives
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Failed to restore archive '{ArchiveRtId}' for tenant '{TenantId}'; continuing with the next archive",
                rtId, tenantId);
            return ArchiveRestoreResult.Failed(rtId, e.Message);
        }
    }

    private void LogRestoreSummary(string tenantId, IReadOnlyList<ArchiveRestoreResult> results)
    {
        var imported = results.Count(r => r.Outcome == ArchiveRestoreOutcome.Imported);
        var skipped = results.Count(r => r.Outcome == ArchiveRestoreOutcome.Skipped);
        var failed = results.Count(r => r.Outcome == ArchiveRestoreOutcome.Failed);

        logger.LogInformation(
            "Archive data restore summary for tenant '{TenantId}': {ImportedCount} imported, {SkippedCount} skipped, " +
            "{FailedCount} failed (of {TotalCount} archive(s) in the backup).",
            tenantId, imported, skipped, failed, results.Count);

        foreach (var result in results)
        {
            switch (result.Outcome)
            {
                case ArchiveRestoreOutcome.Imported:
                    logger.LogInformation(
                        "  archive '{ArchiveRtId}': imported {RowCount} row(s), status restored to {RestoredStatus}",
                        result.RtId, result.RowCount, result.RestoredStatus);
                    break;
                case ArchiveRestoreOutcome.Skipped:
                    logger.LogWarning("  archive '{ArchiveRtId}': skipped — {SkipReason}", result.RtId, result.Detail);
                    break;
                case ArchiveRestoreOutcome.Failed:
                    logger.LogWarning("  archive '{ArchiveRtId}': failed — {FailReason}", result.RtId, result.Detail);
                    break;
            }
        }
    }

    /// <summary>
    ///     Reads the uploaded file as a ZIP and returns the parsed <c>manifest.json</c>, or <c>null</c>
    ///     when the file is not a ZIP (legacy mongodump) or carries no manifest. A ZIP that carries a
    ///     malformed manifest fails loudly (it IS an <c>.octobak</c>, just broken).
    /// </summary>
    private static BackupManifest? TryReadManifest(string filePath)
    {
        try
        {
            using var zipFileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(zipFileStream, ZipArchiveMode.Read);

            var manifestEntry = zip.GetEntry(BackupArchiveContainer.ManifestEntry);
            if (manifestEntry is null)
            {
                return null;
            }

            using var manifestStream = manifestEntry.Open();
            return JsonSerializer.Deserialize<BackupManifest>(manifestStream, ManifestJsonOptions);
        }
        catch (InvalidDataException)
        {
            // Not a ZIP (a legacy .tar.gz mongodump blob) — treat as legacy.
            return null;
        }
    }

    private static CkArchiveStatus ParseStatus(string status)
    {
        // Unknown / unparseable status defaults to Disabled — the safe outcome (the table exists with
        // restored data but is not live until an operator re-enables it).
        return Enum.TryParse<CkArchiveStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : CkArchiveStatus.Disabled;
    }

    private sealed class RowCounter
    {
        public long Count;
    }

    private enum ArchiveRestoreOutcome
    {
        Imported,
        Skipped,
        Failed
    }

    private sealed record ArchiveRestoreResult(
        string RtId,
        ArchiveRestoreOutcome Outcome,
        string? Detail,
        long RowCount,
        CkArchiveStatus? RestoredStatus)
    {
        public static ArchiveRestoreResult Imported(string rtId, long rowCount, CkArchiveStatus restoredStatus) =>
            new(rtId, ArchiveRestoreOutcome.Imported, null, rowCount, restoredStatus);

        public static ArchiveRestoreResult Skipped(string rtId, string reason) =>
            new(rtId, ArchiveRestoreOutcome.Skipped, reason, 0, null);

        public static ArchiveRestoreResult Failed(string rtId, string reason) =>
            new(rtId, ArchiveRestoreOutcome.Failed, reason, 0, null);
    }
}
