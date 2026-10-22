using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Hangfire.Server;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that restores a tenant database from a backup file. Auto-detects whether the
/// uploaded file is a legacy mongodump <c>.tar.gz</c> (restored exactly as before) or an
/// <c>.octobak.zip</c> container carrying archive data (concept AB#4231 §5). When the artifact is an
/// <c>.octobak</c> and <c>restoreArchiveData</c> is set, each archive in the backup is restored into
/// CrateDB via a clean drop/recreate/import sequence (concept §5.1); per-archive failures are
/// recorded and the job still succeeds (continue + report, §2 decision #4).
/// After the restore the secret sweep runs on the restored tenant (AB#5539, concept AB#5528 §6, decision 5):
/// values of an unknown key id become "not set" and are reported for re-entry, older plaintext is encrypted.
/// AB#5561: the staged upload is read from the artifact store (<c>restore-staging/&lt;tenant&gt;/&lt;uploadId&gt;</c>,
/// plain or <c>.octoenc</c>) into the local scratch directory, decrypted when needed; an upload staged on the local
/// disk before AB#5561 (or when staging into the store failed) is still restored from there.
/// AB#5967: a format 2 container (the files of the native archive copy) is read as a stream - the mongodump blob goes to the
/// scratch directory, the archive files are copied byte for byte into the <c>copy-staging</c> area and loaded by the data store
/// itself (<c>COPY FROM</c>); see <c>RestoreRepositoryJob.Native.cs</c>. After the archives, the rollups are validated and
/// repaired when needed (AB#5967 slice 2).
/// </summary>
public partial class RestoreRepositoryJob(
    ILogger<RestoreRepositoryJob> logger,
    ISystemContext systemContext,
    IBackupFileStorageService backupFileStorage,
    ISecretSweepCoordinator? secretSweepCoordinator = null,
    IBotArtifactStorage? artifactStorage = null,
    IArchiveCopyStagingLocationFactory? stagingFactory = null,
    TimeProvider? timeProvider = null,
    IStreamDataRepositoryFactory? streamDataFactory = null) : IRestoreRepositoryJob
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<RestoreRepositoryResult?> Run(string tenantId, string databaseName, string cacheKey,
        string? oldDatabaseName, bool restoreArchiveData, PerformContext? performContext,
        IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;

        // cacheKey is used as the tus file ID (or legacy cache key). The tenant is part of the
        // address since AB#5060: uploads live under a per-tenant directory (and, since AB#5561, under the
        // tenant's restore-staging prefix of the artifact store), so a restore can only ever resolve a file
        // staged for the very tenant it is restoring.
        var localUploadPath = backupFileStorage.GetTusUploadFilePath(tenantId, cacheKey);
        var filePath = localUploadPath;
        string? stagedFileName = null;
        string? scratchPath = null;

        try
        {
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                return null;
            }

            var staged = artifactStorage == null
                ? null
                : await artifactStorage.FindRestoreStagingAsync(tenantId, cacheKey, ct);
            if (staged != null)
            {
                stagedFileName = staged.Value.FileName;

                // Format 2 is read as a stream from the store; anything else (a mongodump, format 1) is not handled
                // here and takes the unchanged path below.
                var nativeFromStore = await TryRestoreNativeAsync(tenantId, databaseName, oldDatabaseName,
                    restoreArchiveData,
                    () => artifactStorage!.OpenPlainReadAsync(ArtifactCategories.RestoreStaging, tenantId,
                        stagedFileName, ct), ct);
                if (nativeFromStore != null)
                {
                    nativeFromStore.SecretSweep = await RunPostRestoreSecretSweepAsync(tenantId,
                        performContext?.BackgroundJob?.Id, ct);
                    return nativeFromStore;
                }

                scratchPath = artifactStorage!.CreateScratchFilePath(".restore");
                logger.LogInformation(
                    "Reading staged upload '{FileName}' of tenant '{TenantId}' from the artifact store", stagedFileName,
                    tenantId);
                if (!await artifactStorage.TryWritePlainToFileAsync(ArtifactCategories.RestoreStaging, tenantId,
                        stagedFileName, scratchPath, ct))
                {
                    throw new JobFailedException(
                        $"The staged upload for tus file ID '{cacheKey}' disappeared from the artifact store " +
                        "(expired?). Upload the backup again.");
                }

                filePath = scratchPath;
            }
            else if (!File.Exists(filePath))
            {
                throw new JobFailedException(
                    $"Backup file not found at '{filePath}' for tus file ID '{cacheKey}'.");
            }
            else if (artifactStorage != null)
            {
                // Local fallback (pre-AB#5561 upload, or staging into the store failed): an uploaded .octoenc
                // file is decrypted into the scratch directory exactly like a staged one, never fed to mongorestore
                // as ciphertext.
                var decryptedPath = artifactStorage.CreateScratchFilePath(".restore");
                if (await artifactStorage.TryUnprotectLocalFileAsync(tenantId, filePath, decryptedPath, ct))
                {
                    logger.LogInformation("Decrypted the encrypted local upload of tenant '{TenantId}' for the restore",
                        tenantId);
                    scratchPath = decryptedPath;
                    filePath = decryptedPath;
                }
            }

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0)
            {
                throw new JobFailedException(
                    $"Backup file at '{filePath}' for tus file ID '{cacheKey}' is empty (0 bytes). The upload may not have completed successfully.");
            }

            if (staged == null)
            {
                // A local upload (no staging in the store): format 2 is read from the file as a stream as well.
                var nativeFromFile = await TryRestoreNativeAsync(tenantId, databaseName, oldDatabaseName,
                    restoreArchiveData,
                    () => Task.FromResult<Stream?>(new FileStream(filePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan)), ct);
                if (nativeFromFile != null)
                {
                    nativeFromFile.SecretSweep = await RunPostRestoreSecretSweepAsync(tenantId,
                        performContext?.BackgroundJob?.Id, ct);
                    return nativeFromFile;
                }
            }

            // Auto-detect the artifact format: an .octobak is a ZIP carrying manifest.json; anything
            // else is treated as a legacy mongodump .tar.gz (today's path, unchanged).
            var manifest = TryReadManifest(filePath);
            RestoreRepositoryResult? octoBakResult = null;

            if (manifest is null)
            {
                if (restoreArchiveData)
                {
                    logger.LogWarning(
                        "restoreArchiveData was requested for tenant '{TenantId}' but the uploaded artifact is a " +
                        "legacy mongodump (no manifest.json); restoring Mongo only.", tenantId);
                }

                await RestoreMongoAsync(tenantId, databaseName, filePath, oldDatabaseName, ct);
            }
            else
            {
                octoBakResult = await RestoreOctoBakAsync(tenantId, databaseName, oldDatabaseName, filePath, manifest,
                    restoreArchiveData, ct);
            }

            return new RestoreRepositoryResult
            {
                TenantId = tenantId,
                DatabaseName = databaseName,
                BackupFormatVersion = manifest?.FormatVersion,
                Archives = octoBakResult?.Archives ?? [],
                RollupReconcile = octoBakResult?.RollupReconcile,
                SecretSweep = await RunPostRestoreSecretSweepAsync(tenantId, performContext?.BackgroundJob?.Id, ct)
            };
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while restoring database for tenant '{TenantId}'", tenantId);
            throw;
        }
        finally
        {
            await backupFileStorage.DeleteFileAsync(localUploadPath);
            artifactStorage?.DeleteScratchFile(scratchPath);
            if (stagedFileName != null)
            {
                await DeleteStagedUploadAsync(tenantId, stagedFileName);
            }
        }
    }

    /// <summary>
    ///     A staged upload is consumed by its restore attempt (as the local tus file always was); the retention and
    ///     the store's lifecycle rule remove it otherwise.
    /// </summary>
    private async Task DeleteStagedUploadAsync(string tenantId, string fileName)
    {
        try
        {
            await artifactStorage!.DeleteAsync(ArtifactCategories.RestoreStaging, tenantId, fileName);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not delete the staged upload '{FileName}' of tenant '{TenantId}'", fileName,
                tenantId);
        }
    }

    /// <summary>
    ///     Runs the post-restore secret sweep (AB#5539). The restore itself has succeeded at this point, so
    ///     a problem of the sweep is reported, never thrown.
    /// </summary>
    private async Task<SecretSweepReport?> RunPostRestoreSecretSweepAsync(string tenantId, string? jobId,
        CancellationToken ct)
    {
        if (secretSweepCoordinator == null)
        {
            return null;
        }

        var report = await secretSweepCoordinator.RunAfterRestoreAsync(tenantId, new SecretSweepRunInfo(jobId), ct);
        if (report.SecretsToReEnter.Count > 0)
        {
            logger.LogWarning(
                "Restore of tenant '{TenantId}': {Count} secret(s) are encrypted with a key unknown to this " +
                "environment; they were kept, read as not set (key missing) and must be re-entered unless the " +
                "key is added to the key ring (listed in the job result and in the tenant's secret sweep report)",
                tenantId, report.SecretsToReEnter.Count);
        }

        return report;
    }

    /// <summary>
    ///     Restores an <c>.octobak.zip</c>: extracts the mongo blob, runs the Mongo restore unchanged,
    ///     then — when <paramref name="restoreArchiveData"/> is set — restores each archive's CrateDB
    ///     rows via the clean per-archive sequence (concept §5/§5.1).
    /// </summary>
    private async Task<RestoreRepositoryResult> RestoreOctoBakAsync(string tenantId, string databaseName,
        string? oldDatabaseName, string filePath, BackupManifest manifest, bool restoreArchiveData,
        CancellationToken ct)
    {
        logger.LogInformation(
            "Detected .octobak backup for tenant '{TenantId}' (formatVersion {Version}, {ArchiveCount} archive(s))",
            tenantId, manifest.FormatVersion, manifest.Archives.Count);

        if (!BackupArchiveContainer.IsSupported(manifest.FormatVersion))
        {
            throw new JobFailedException(
                $"Unsupported backup format version {manifest.FormatVersion}. This service supports format versions " +
                $"{BackupArchiveContainer.SupportedFormatVersionsText}.");
        }

        if (manifest.FormatVersion == BackupArchiveContainer.NativeFormatVersion)
        {
            // Reached only when the native path was not available at all (no artifact store).
            throw new JobFailedException(
                $"The backup of tenant '{tenantId}' is format {manifest.FormatVersion} (native archive copy) and needs a " +
                "staging area in the artifact store, but none is configured.");
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
            return new RestoreRepositoryResult();
        }

        return await RestoreArchivesAsync(tenantId, manifest, zip, ct);
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
    private async Task<RestoreRepositoryResult> RestoreArchivesAsync(string tenantId, BackupManifest manifest,
        ZipArchive zip, CancellationToken ct)
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
            return new RestoreRepositoryResult();
        }

        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var results = new List<ArchiveRestoreResult>(manifest.Archives.Count);

        var (ordered, sourcesOfOthers) = await OrderSourcesFirstAsync(tenantContext, manifest.Archives);

        // See RestoreArchivesNativeAsync: a source that was not Activated but has rollups stays Activated until they are
        // restored (the restore-reconcile marker keeps the forward aggregation away meanwhile).
        var deferred = ordered.Where(e => e.NdjsonEntry != null && sourcesOfOthers.Contains(e.Schema.RtId) &&
                                          ParseStatus(e.Status) != CkArchiveStatus.Activated)
            .Select(e => e.Schema.RtId).ToHashSet(StringComparer.Ordinal);
        if (deferred.Count > 0)
        {
            await repository.SetRestoreReconcilePendingAsync(true, ct);
        }

        foreach (var entry in ordered)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RestoreOneArchiveAsync(tenantId, entry, zip, repository, lifecycle, archiveStore,
                deferred.Contains(entry.Schema.RtId), ct));
        }

        for (var i = 0; i < results.Count; i++)
        {
            if (deferred.Contains(results[i].RtId) && results[i].Outcome == ArchiveRestoreOutcome.Imported)
            {
                await lifecycle.DisableAsync(new OctoObjectId(results[i].RtId));
                results[i] = results[i] with { RestoredStatus = CkArchiveStatus.Disabled };
            }
        }

        LogRestoreSummary(tenantId, results);
        var reconcile = await ReconcileRollupsAsync(tenantId, tenantContext, repository, results, ct);
        if (deferred.Count > 0 && reconcile == null)
        {
            await repository.SetRestoreReconcilePendingAsync(false, ct);
        }

        return new RestoreRepositoryResult { Archives = results.Select(ToSummary).ToList(), RollupReconcile = reconcile };
    }

    /// <summary>
    ///     The archives of the manifest with every source before the rollups that read it (raw archives first, then
    ///     rollups by the length of their source chain; manifest order otherwise). A rollup cannot be activated before its
    ///     source is <c>Activated</c> again, and the dump lists the archives in no dependency order.
    /// </summary>
    private static async Task<(IReadOnlyList<BackupManifestArchive> Ordered, HashSet<string> SourcesOfOthers)>
        OrderSourcesFirstAsync(ITenantContext tenantContext, IReadOnlyList<BackupManifestArchive> archives)
    {
        var sourcesOfOthers = new HashSet<string>(StringComparer.Ordinal);
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore();
        if (rollupStore == null || archives.Count < 2)
        {
            return (archives, sourcesOfOthers);
        }

        var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await foreach (var rollup in rollupStore.EnumerateAsync())
        {
            sources[rollup.RtId.ToString()] = rollup.Sources.Select(r => r.SourceArchiveRtId.ToString()).ToList();
        }

        if (sources.Count == 0)
        {
            return (archives, sourcesOfOthers);
        }

        var inManifest = archives.Select(a => a.Schema.RtId).ToHashSet(StringComparer.Ordinal);
        foreach (var (rollupId, parents) in sources.Where(kv => inManifest.Contains(kv.Key)))
        {
            foreach (var parent in parents.Where(inManifest.Contains))
            {
                sourcesOfOthers.Add(parent);
            }
        }

        var depths = new Dictionary<string, int>(StringComparer.Ordinal);

        int DepthOf(string rtId, HashSet<string> visiting)
        {
            if (depths.TryGetValue(rtId, out var known))
            {
                return known;
            }

            if (!sources.TryGetValue(rtId, out var parents) || !visiting.Add(rtId))
            {
                return 0;
            }

            var depth = 1 + parents.Select(p => DepthOf(p, visiting)).DefaultIfEmpty(0).Max();
            visiting.Remove(rtId);
            depths[rtId] = depth;
            return depth;
        }

        var ordered = archives.Select((entry, index) => (entry, index, depth: DepthOf(entry.Schema.RtId, [])))
            .OrderBy(x => x.depth).ThenBy(x => x.index).Select(x => x.entry).ToList();
        return (ordered, sourcesOfOthers);
    }

    /// <summary>
    ///     Restores a single archive's rows via the clean drop/recreate/import sequence (concept §5.1).
    ///     Wrapped so a failure on one archive is recorded and reported without aborting the whole job.
    /// </summary>
    private async Task<ArchiveRestoreResult> RestoreOneArchiveAsync(string tenantId, BackupManifestArchive entry,
        ZipArchive zip, IStreamDataRepository repository, IArchiveLifecycleService lifecycle,
        IArchiveRuntimeStore archiveStore, bool keepActivatedForDependents, CancellationToken ct)
    {
        var rtId = entry.Schema.RtId;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (entry.NdjsonEntry is null)
            {
                // The archive had no provisioned Crate table at backup time (never activated, e.g. a
                // blueprint-seeded Disabled archive) — nothing to restore, and deliberately no table is
                // created: the archive stays exactly as backed up (AB#5141).
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

            // Restore the archive's backed-up status (concept §10): Activated -> re-enable; anything else
            // (Disabled, or a Failed archive whose table was backed up) -> leave Disabled, the safe state.
            // The result reports the status the archive actually ends up in, not the parsed backup value.
            var backedUpStatus = ParseStatus(entry.Status);
            var restoredStatus = CkArchiveStatus.Disabled;
            if (keepActivatedForDependents)
            {
                await lifecycle.EnableAsync(objectId);
                restoredStatus = CkArchiveStatus.Activated;
            }
            else if (backedUpStatus == CkArchiveStatus.Activated)
            {
                await lifecycle.EnableAsync(objectId);
                restoredStatus = CkArchiveStatus.Activated;
            }
            else if (!string.Equals(entry.Status, nameof(CkArchiveStatus.Disabled), StringComparison.OrdinalIgnoreCase))
            {
                // Compare the raw manifest value: ParseStatus maps unknown (e.g. forward-version) values
                // to Disabled as well, and that mapping must be visible too.
                logger.LogWarning(
                    "Archive '{ArchiveRtId}' of tenant '{TenantId}' was '{BackedUpStatus}' at backup time; its data was " +
                    "restored and it is left Disabled — re-enable it once the cause of the failed activation is fixed",
                    rtId, tenantId, entry.Status);
            }

            return ArchiveRestoreResult.Imported(rtId, counter.Count, restoredStatus) with
            {
                Seconds = stopwatch.Elapsed.TotalSeconds
            };
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

    private static RestoredArchiveSummary ToSummary(ArchiveRestoreResult result) => new()
    {
        RtId = result.RtId,
        Outcome = result.Outcome.ToString(),
        Rows = result.RowCount,
        Seconds = Math.Round(result.Seconds, 1),
        Detail = result.Outcome == ArchiveRestoreOutcome.Imported ? $"status {result.RestoredStatus}" : result.Detail
    };

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
        public double Seconds { get; init; }

        /// <summary>True when a failure applies to every further archive as well (the job log filter is missing).</summary>
        public bool StopsTheRestore { get; init; }

        public static ArchiveRestoreResult Imported(string rtId, long rowCount, CkArchiveStatus restoredStatus) =>
            new(rtId, ArchiveRestoreOutcome.Imported, null, rowCount, restoredStatus);

        public static ArchiveRestoreResult Skipped(string rtId, string reason) =>
            new(rtId, ArchiveRestoreOutcome.Skipped, reason, 0, null);

        public static ArchiveRestoreResult Failed(string rtId, string reason) =>
            new(rtId, ArchiveRestoreOutcome.Failed, reason, 0, null);
    }
}

/// <summary>
///     Result of a repository restore (job result).
/// </summary>
public class RestoreRepositoryResult
{
    /// <summary>
    ///     Restored tenant.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    ///     Restored database.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    ///     The post-restore secret sweep (AB#5539), including the secrets to re-enter
    ///     (<see cref="SecretSweepReport.SecretsToReEnter" />); <c>null</c> when not wired.
    /// </summary>
    public SecretSweepReport? SecretSweep { get; set; }

    /// <summary>
    ///     The <c>formatVersion</c> of the restored <c>.octobak</c> (1 or 2); <c>null</c> for a mongodump.
    /// </summary>
    public int? BackupFormatVersion { get; set; }

    /// <summary>
    ///     One entry per archive of the backup: what happened, rows loaded and seconds spent (AB#5967).
    /// </summary>
    public List<RestoredArchiveSummary> Archives { get; set; } = [];

    /// <summary>
    ///     The rollup reconcile after the archives (validate, repair, validate; AB#5967 slice 2); <c>null</c> when no
    ///     archive data was restored or the data store has no rollup validation.
    /// </summary>
    public RollupReconcileSummary? RollupReconcile { get; set; }
}

/// <summary>
///     Outcome of one archive of a restore (job result).
/// </summary>
public class RestoredArchiveSummary
{
    /// <summary>The archive.</summary>
    public string RtId { get; set; } = string.Empty;

    /// <summary><c>Imported</c>, <c>Skipped</c> or <c>Failed</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Rows loaded (verified against the manifest for a native restore).</summary>
    public long Rows { get; set; }

    /// <summary>Seconds the archive took, from dropping the old table to the restored status.</summary>
    public double Seconds { get; set; }

    /// <summary>Why it was skipped or failed (redacted); the status it ended in for an imported archive.</summary>
    public string? Detail { get; set; }
}

/// <summary>
///     The rollup reconcile of a restore: the validation after the load, the repair steps and the validation after
///     them (job result).
/// </summary>
public class RollupReconcileSummary
{
    /// <summary>True when the tenant's rollups validate ok at the end (the restore-reconcile marker is clear).</summary>
    public bool Ok { get; set; }

    /// <summary>True when the first validation found something and a repair ran.</summary>
    public bool RepairRan { get; set; }

    /// <summary>The first failed step, named (rollup, step, reason); <c>null</c> when nothing failed.</summary>
    public string? FailedStep { get; set; }

    /// <summary>Rollups whose stale recompute flag (restored from the backup) was reset.</summary>
    public List<string> ResetRecomputeFlags { get; set; } = [];

    /// <summary>Per rollup: findings before, repair steps, findings after.</summary>
    public List<RollupReconcileEntry> Rollups { get; set; } = [];

    /// <summary>
    ///     Every archive of the backup that failed to restore. As long as one is listed the restore-reconcile marker stays
    ///     set, whatever the validator reports: the bot knows the manifest, the validator does not.
    /// </summary>
    public List<FailedArchiveSummary> FailedArchives { get; set; } = [];

    /// <summary>
    ///     The rollups that could not be checked (no table, source missing, archive failed, never activated), each with the
    ///     cause and whether it keeps the marker set.
    /// </summary>
    public List<NotCheckedRollupSummary> NotCheckedRollups { get; set; } = [];
}

/// <summary>An archive that failed to restore (job result).</summary>
public class FailedArchiveSummary
{
    /// <summary>The archive.</summary>
    public string ArchiveRtId { get; set; } = string.Empty;

    /// <summary>Why it failed (redacted).</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>A rollup the reconcile could not check (job result).</summary>
public class NotCheckedRollupSummary
{
    /// <summary>The rollup.</summary>
    public string RollupRtId { get; set; } = string.Empty;

    /// <summary><c>NoLiveTable</c>, <c>NeverActivated</c>, <c>Disabled</c>, <c>SourceMissing</c> or <c>ArchiveFailed</c>.</summary>
    public string Cause { get; set; } = string.Empty;

    /// <summary>English text for logs and the CLI.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>True when the gap keeps the restore-reconcile marker set.</summary>
    public bool IsBlocking { get; set; }
}

/// <summary>
///     Validate, repair, validate for one rollup (job result).
/// </summary>
public class RollupReconcileEntry
{
    /// <summary>The rollup archive.</summary>
    public string RollupRtId { get; set; } = string.Empty;

    /// <summary>Findings of the validation after the load; empty when it was ok.</summary>
    public List<string> Before { get; set; } = [];

    /// <summary>The repair steps with their status, in order.</summary>
    public List<string> Repair { get; set; } = [];

    /// <summary>Findings of the validation after the repair; empty when it was ok.</summary>
    public List<string> After { get; set; } = [];
}
