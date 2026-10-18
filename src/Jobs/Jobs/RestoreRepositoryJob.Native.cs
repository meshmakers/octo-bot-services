using System.Diagnostics;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

// Format 2 restore (AB#5967, concept-tenant-backup-native-archive-dump.md §7) and the rollup reconcile that follows every
// restore of archive data (AB#5967 slice 2).
public partial class RestoreRepositoryJob
{
    private static readonly byte[] ZipLocalHeaderMagic = [0x50, 0x4b, 0x03, 0x04];

    /// <summary>What one pass over a container found; filled while it is read so a failure can still clean up.</summary>
    private sealed class ContainerPass
    {
        public BackupManifest? Manifest { get; set; }

        public string? MongoScratch { get; set; }

        public string? RunId { get; set; }

        public bool RowFormat { get; set; }
    }

    /// <summary>
    ///     Restores a format 2 container read as a stream: the mongodump blob goes to the scratch directory, the archive files
    ///     are copied byte for byte into the staging area, the manifest decides the rest. Returns <c>null</c> when the upload
    ///     is not a format 2 container (a mongodump, format 1, a ZIP without manifest) - nothing was changed then and the
    ///     caller takes the unchanged path.
    /// </summary>
    private async Task<RestoreRepositoryResult?> TryRestoreNativeAsync(string tenantId, string databaseName,
        string? oldDatabaseName, bool restoreArchiveData, Func<Task<Stream?>> open, CancellationToken ct)
    {
        if (artifactStorage == null)
        {
            return null;
        }

        var source = await open();
        if (source == null)
        {
            return null;
        }

        var pass = new ContainerPass();
        try
        {
            bool isContainer;
            await using (source)
            {
                isContainer = await ReadContainerAsync(tenantId, restoreArchiveData, source, pass, ct);
            }

            if (!isContainer || pass.RowFormat || pass.Manifest == null ||
                pass.Manifest.FormatVersion == BackupArchiveContainer.CurrentFormatVersion)
            {
                return null;
            }

            return await RestoreNativeAsync(tenantId, databaseName, oldDatabaseName, restoreArchiveData, pass, ct);
        }
        finally
        {
            artifactStorage.DeleteScratchFile(pass.MongoScratch);
            if (pass.RunId != null)
            {
                await DeleteStagingRunQuietlyAsync(tenantId, pass.RunId);
            }
        }
    }

    /// <summary>
    ///     One forward pass: mongo blob to scratch, archive files to the staging area (only when they will be loaded),
    ///     manifest parsed. Stops at the first row entry of a format 1 container. Returns false for a stream that is
    ///     not a ZIP.
    /// </summary>
    private async Task<bool> ReadContainerAsync(string tenantId, bool restoreArchiveData, Stream source,
        ContainerPass pass, CancellationToken ct)
    {
        var head = new byte[ZipLocalHeaderMagic.Length];
        var read = await source.ReadAtLeastAsync(head, head.Length, false, ct);
        if (read < head.Length || !head.AsSpan().SequenceEqual(ZipLocalHeaderMagic))
        {
            return false;
        }

        await using var content = new PrefixedReadStream(head, source);
        var reader = new ForwardZipReader(content);
        var stageArchives = restoreArchiveData && stagingFactory is { IsSupported: true };
        try
        {
            while (await reader.ReadNextAsync(ct) is { } entry)
            {
                if (!entry.IsStored && (entry.Name == BackupArchiveContainer.MongoBlobEntry ||
                                        entry.Name == BackupArchiveContainer.ManifestEntry))
                {
                    // Format 1 compresses its manifest (and any other ZIP its entries): the unchanged path reads those.
                    pass.RowFormat = true;
                    return true;
                }

                if (entry.Name == BackupArchiveContainer.MongoBlobEntry)
                {
                    pass.MongoScratch = artifactStorage!.CreateScratchFilePath(".mongo.tar.gz");
                    await using var target = OwnerOnlyFiles.CreateNew(pass.MongoScratch);
                    await entry.Data.CopyToAsync(target, ct);
                }
                else if (entry.Name == BackupArchiveContainer.ManifestEntry)
                {
                    pass.Manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(entry.Data,
                        ManifestJsonOptions, ct);
                }
                else if (BackupArchiveContainer.TryGetNativeRelativePath(entry.Name, out var relativePath))
                {
                    if (stageArchives)
                    {
                        pass.RunId ??= Guid.NewGuid().ToString("N")[..16];
                        await artifactStorage!.PutStagingAsync(tenantId, pass.RunId, relativePath, entry.Data, ct);
                    }
                }
                else if (entry.Name.StartsWith(BackupArchiveContainer.ArchivesFolder, StringComparison.Ordinal))
                {
                    pass.RowFormat = true; // archives/<rtId>.ndjson: format 1, taken by the unchanged path
                    return true;
                }
            }

            // Read to the very end: the integrity check of an encrypted upload covers its last chunk only when the
            // stream is consumed completely.
            await content.CopyToAsync(Stream.Null, ct);
            return true;
        }
        catch (InvalidDataException e)
        {
            throw new JobFailedException($"The backup container of tenant '{tenantId}' cannot be read: {e.Message}", e);
        }
    }

    private async Task<RestoreRepositoryResult> RestoreNativeAsync(string tenantId, string databaseName,
        string? oldDatabaseName, bool restoreArchiveData, ContainerPass pass, CancellationToken ct)
    {
        var manifest = pass.Manifest!;
        logger.LogInformation(
            "Detected .octobak backup for tenant '{TenantId}' (formatVersion {Version}, {ArchiveCount} archive(s)); " +
            "read as a stream", tenantId, manifest.FormatVersion, manifest.Archives.Count);

        if (!BackupArchiveContainer.IsSupported(manifest.FormatVersion))
        {
            throw new JobFailedException(
                $"Unsupported backup format version {manifest.FormatVersion}. This service supports format versions " +
                $"{BackupArchiveContainer.SupportedFormatVersionsText}.");
        }

        if (pass.MongoScratch == null)
        {
            throw new JobFailedException(
                $"The .octobak container for tenant '{tenantId}' is missing the '{BackupArchiveContainer.MongoBlobEntry}' entry.");
        }

        var hasNativeArchives = manifest.Archives.Any(a => a.Copy != null);
        if (restoreArchiveData && hasNativeArchives)
        {
            // Everything that can be decided before the tenant is touched is decided here.
            if (stagingFactory is not { IsSupported: true })
            {
                throw new JobFailedException(
                    $"The backup of tenant '{tenantId}' is format 2 (native archive copy) and its archive data needs a " +
                    $"staging area, but {stagingFactory?.UnsupportedReason ?? "no staging location factory is registered"}. " +
                    "Nothing was restored.");
            }

            // The cluster-level readiness (job log filter, native copy) is asked of the factory, which needs no tenant:
            // a tenant context would build the CK cache of the tenant from the database as it is before the Mongo restore.
            // A failure here leaves the tenant completely untouched.
            if (streamDataFactory != null)
            {
                var readiness = await streamDataFactory.CheckStagingCopyReadinessAsync(ct);
                if (readiness is { IsReady: false })
                {
                    throw new JobFailedException(
                        $"The backup of tenant '{tenantId}' is format 2 (native archive copy), but this installation " +
                        $"cannot run the native copy: {readiness.Reason ?? "not ready"} Nothing was restored.");
                }
            }
        }

        await RestoreMongoAsync(tenantId, databaseName, pass.MongoScratch, oldDatabaseName, ct);
        artifactStorage!.DeleteScratchFile(pass.MongoScratch);
        pass.MongoScratch = null;

        var result = new RestoreRepositoryResult
        {
            TenantId = tenantId,
            DatabaseName = databaseName,
            BackupFormatVersion = manifest.FormatVersion
        };
        if (!restoreArchiveData)
        {
            logger.LogInformation(
                "restoreArchiveData is off; restored Mongo only for tenant '{TenantId}', archives left untouched " +
                "(no Crate tables, identical to legacy behaviour).", tenantId);
            return result;
        }

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
            return result;
        }

        if (hasNativeArchives)
        {
            // Before the first table is dropped: a missing job log filter or native copy must not cost an archive its rows.
            var readiness = await repository.CheckStagingCopyReadinessAsync(ct);
            if (readiness is { IsReady: false })
            {
                throw new JobFailedException(
                    $"Tenant '{tenantId}' was restored in Mongo, but its archive data (format 2) cannot be loaded: " +
                    $"{readiness.Reason ?? "the native copy is not ready"} The archives were left untouched.");
            }
        }

        await LogStorageVersionsAsync(tenantId, manifest, repository, hasNativeArchives, ct);

        var staged = new List<StagingObject>();
        if (pass.RunId != null)
        {
            await foreach (var item in artifactStorage.ListStagingAsync(tenantId, pass.RunId, ct))
            {
                staged.Add(item);
            }
        }

        var archiveStore = tenantContext.GetArchiveRuntimeStore();
        var results = new List<ArchiveRestoreResult>(manifest.Archives.Count);
        string? stopReason = null;
        var (ordered, sourcesOfOthers) = await OrderSourcesFirstAsync(tenantContext, manifest.Archives);

        // A rollup is activated only while its source is Activated. A source that was not Activated in the backup but has
        // rollups in it is therefore left Activated until those are restored and set back at the end; for that time the
        // tenant's restore-reconcile marker keeps the forward aggregation and the recompute away from it.
        var deferred = ordered.Where(e => e.Copy != null && sourcesOfOthers.Contains(e.Schema.RtId) &&
                                          ParseStatus(e.Status) != CkArchiveStatus.Activated)
            .Select(e => e.Schema.RtId).ToHashSet(StringComparer.Ordinal);
        if (deferred.Count > 0)
        {
            await repository.SetRestoreReconcilePendingAsync(true, ct);
        }

        foreach (var entry in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (stopReason != null)
            {
                results.Add(ArchiveRestoreResult.Skipped(entry.Schema.RtId, $"not attempted: {stopReason}"));
                continue;
            }

            var archiveResult = await RestoreOneNativeArchiveAsync(tenantId, entry, pass.RunId, staged, repository,
                lifecycle, archiveStore, deferred.Contains(entry.Schema.RtId), ct);
            results.Add(archiveResult);
            if (archiveResult.Outcome == ArchiveRestoreOutcome.Failed && archiveResult.StopsTheRestore)
            {
                // Every further archive would be refused for the same reason, after its table was dropped.
                stopReason = archiveResult.Detail;
            }
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
        result.Archives = results.Select(ToSummary).ToList();
        result.RollupReconcile = await ReconcileRollupsAsync(tenantId, tenantContext, repository, results, ct);
        if (deferred.Count > 0 && result.RollupReconcile == null)
        {
            await repository.SetRestoreReconcilePendingAsync(false, ct);
        }

        return result;
    }

    private async Task LogStorageVersionsAsync(string tenantId, BackupManifest manifest, IStreamDataRepository repository,
        bool hasNativeArchives, CancellationToken ct)
    {
        if (!hasNativeArchives)
        {
            return;
        }

        string? target = null;
        try
        {
            target = await repository.GetStorageVersionAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not read the storage version of the target");
        }

        // A difference is logged, never refused: a backup from CrateDB 5.x restores on 6.x (concept §9).
        logger.LogInformation(
            "Restore of tenant '{TenantId}': the archives were copied from storage version '{SourceVersion}' (platform " +
            "'{PlatformVersion}'), the target runs '{TargetVersion}'", tenantId,
            manifest.Native?.StorageVersion ?? "unknown", manifest.Native?.PlatformVersion ?? "unknown",
            target ?? "unknown");
    }

    /// <summary>
    ///     Restores one archive of a format 2 backup: the table is dropped and created fresh with the platform's DDL, the
    ///     staged slices are loaded by the data store and counted against the manifest, the backed-up status is restored.
    ///     A failure is recorded and reported; the loop goes on with the next archive.
    /// </summary>
    private async Task<ArchiveRestoreResult> RestoreOneNativeArchiveAsync(string tenantId, BackupManifestArchive entry,
        string? runId, IReadOnlyList<StagingObject> staged, IStreamDataRepository repository,
        IArchiveLifecycleService lifecycle, IArchiveRuntimeStore archiveStore, bool keepActivatedForDependents,
        CancellationToken ct)
    {
        var rtId = entry.Schema.RtId;
        var stopwatch = Stopwatch.StartNew();
        ArchiveCopyStagingLocation? location = null;

        try
        {
            if (entry.Copy is null)
            {
                return ArchiveRestoreResult.Skipped(rtId,
                    $"no archive data in the backup (archive was '{entry.Status}', had no Crate table)");
            }

            var objectId = new OctoObjectId(rtId);
            var postSnapshot = await archiveStore.GetAsync(objectId);
            if (postSnapshot is null)
            {
                return ArchiveRestoreResult.Skipped(rtId, "archive does not exist in the tenant after the restore");
            }

            var mismatch = ArchiveSchemaMatcher.FindMismatch(entry.Schema, ArchiveSchemaMapper.ToDto(postSnapshot));
            if (mismatch != null)
            {
                return ArchiveRestoreResult.Skipped(rtId, mismatch);
            }

            var plan = BuildPlan(objectId, entry.Copy);
            var missing = plan.Slices.FirstOrDefault(s =>
                s.RowCount > 0 && !staged.Any(o => o.RelativePath.StartsWith(s.GetStagingPath(plan.ArchiveRtId) + "/",
                    StringComparison.Ordinal)));
            if (missing != null || runId == null)
            {
                // Decided before the table is touched: a backup with files missing must not cost the archive its rows.
                return ArchiveRestoreResult.Skipped(rtId,
                    $"the files of slice '{missing?.Key ?? "?"}' are missing from the backup container");
            }

            location = stagingFactory!.Create(tenantId, runId, ArchiveCopyStagingAccess.Restore);

            // Clean restore (concept §7): disable, drop, create fresh (the platform's current DDL), disable.
            if (postSnapshot.Status == CkArchiveStatus.Activated)
            {
                await lifecycle.DisableAsync(objectId);
            }

            await repository.DeleteArchiveAsync(objectId);
            await lifecycle.ActivateAsync(objectId);
            await lifecycle.DisableAsync(objectId);

            var load = await repository.LoadArchiveFromStagingAsync(objectId, plan, location, null, ct);
            if (!load.IsSuccess)
            {
                var reason = location.Scrub(load.Slices.FirstOrDefault(s => !s.IsSuccess)?.Message ??
                                            $"the tables hold {load.LoadedRows?.ToString() ?? "an unknown number of"} row(s), " +
                                            $"{load.ExpectedRows} expected");
                logger.LogWarning("Archive '{ArchiveRtId}' of tenant '{TenantId}' was not loaded: {Reason}", rtId,
                    tenantId, reason);
                return ArchiveRestoreResult.Failed(rtId, reason) with { Seconds = stopwatch.Elapsed.TotalSeconds };
            }

            var restoredStatus = CkArchiveStatus.Disabled;
            if (keepActivatedForDependents)
            {
                await lifecycle.EnableAsync(objectId);
                restoredStatus = CkArchiveStatus.Activated;
            }
            else if (ParseStatus(entry.Status) == CkArchiveStatus.Activated)
            {
                await lifecycle.EnableAsync(objectId);
                restoredStatus = CkArchiveStatus.Activated;
            }
            else if (!string.Equals(entry.Status, nameof(CkArchiveStatus.Disabled), StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Archive '{ArchiveRtId}' of tenant '{TenantId}' was '{BackedUpStatus}' at backup time; its data was " +
                    "restored and it is left Disabled - re-enable it once the cause of the failed activation is fixed",
                    rtId, tenantId, entry.Status);
            }

            // The data rows of the archive, as the manifest counts them; the generation map rows of a rollup are not part of it.
            var dataRows = plan.Slices.Where(sl => sl.Table == ArchiveCopyTable.Data).Sum(sl => sl.RowCount);
            return ArchiveRestoreResult.Imported(rtId, dataRows, restoredStatus) with
            {
                Seconds = stopwatch.Elapsed.TotalSeconds
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveCopyRefusedException e) when (e.Reason == ArchiveCopyRefusalReason.JobLogNotFiltered)
        {
            var reason = location?.Scrub(e.Message) ?? e.Message;
            logger.LogError("Archive '{ArchiveRtId}' of tenant '{TenantId}' was not loaded: {Reason}", rtId, tenantId,
                reason);
            return ArchiveRestoreResult.Failed(rtId, reason) with { StopsTheRestore = true };
        }
        catch (Exception e)
        {
            var reason = location?.Scrub(e.Message) ?? e.Message;
            logger.LogError("Failed to restore archive '{ArchiveRtId}' for tenant '{TenantId}'; continuing with the " +
                            "next archive: {Reason}", rtId, tenantId, reason);
            return ArchiveRestoreResult.Failed(rtId, reason) with { Seconds = stopwatch.Elapsed.TotalSeconds };
        }
    }

    private static ArchiveCopyPlan BuildPlan(OctoObjectId archiveRtId, BackupManifestArchiveCopy copy)
    {
        var tables = copy.Tables
            .Select(t => new ArchiveTableDescription(archiveRtId, Enum.Parse<ArchiveCopyTable>(t.Table),
                t.Columns.Select(c => new ArchiveCopyColumn(c.Name, c.DataType)).ToList()))
            .ToList();
        var slices = copy.Slices
            .Select(s => new ArchiveCopySlice(Enum.Parse<ArchiveCopyTable>(s.Table), s.Key, s.RowCount, s.FromUtc,
                s.ToUtc, s.StagedBytes))
            .ToList();
        return new ArchiveCopyPlan(archiveRtId, tables, slices);
    }

    private async Task DeleteStagingRunQuietlyAsync(string tenantId, string runId)
    {
        try
        {
            await artifactStorage!.DeleteRunAsync(tenantId, runId, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Could not delete the copy staging run '{RunId}' of tenant '{TenantId}'; the retention removes it",
                runId, tenantId);
        }
    }

    /// <summary>
    ///     The rollup reconcile after the archives of a restore (AB#5967 slice 2). Reset: a recompute flag restored from the
    ///     backup is cleared (it would suppress the orphan check). The restore-reconcile marker is set (reclaim, forward
    ///     aggregation and recompute stand still for the tenant), the rollups are validated; a consistent restore needs no
    ///     repair and clears the marker, otherwise <c>RepairRollupsAsync</c> (validate, repair, validate, synchronously
    ///     inside this job) runs and clears the marker only when the tenant validates ok afterwards. A failing step leaves
    ///     the marker set and is named in the result; the restore itself has succeeded and does not fail.
    /// </summary>
    private async Task<RollupReconcileSummary?> ReconcileRollupsAsync(string tenantId, ITenantContext tenantContext,
        IStreamDataRepository repository, IReadOnlyList<ArchiveRestoreResult> archives, CancellationToken ct)
    {
        var failed = archives.Where(a => a.Outcome == ArchiveRestoreOutcome.Failed).ToList();
        if (failed.Count == 0 && !archives.Any(a => a.Outcome == ArchiveRestoreOutcome.Imported))
        {
            return null;
        }

        var summary = new RollupReconcileSummary();
        summary.FailedArchives.AddRange(failed.Select(f => new FailedArchiveSummary
        {
            ArchiveRtId = f.RtId, Reason = f.Detail ?? string.Empty
        }));
        try
        {
            await ResetStaleRecomputeFlagsAsync(tenantId, tenantContext, summary);
            var failedIds = failed.Select(f => f.RtId).ToHashSet(StringComparer.Ordinal);

            // The marker first: no tick may reclaim or recompute between the load and the validation.
            await repository.SetRestoreReconcilePendingAsync(true, ct);
            var before = await repository.ValidateRollupsAsync(null, ct);
            if (before is null)
            {
                await repository.SetRestoreReconcilePendingAsync(false, ct);
                return null;
            }

            AddNotChecked(summary, before, failedIds);
            if (before.IsOk && failed.Count == 0)
            {
                await repository.SetRestoreReconcilePendingAsync(false, ct);
                summary.Ok = true;
                summary.Rollups.AddRange(before.Rollups.Select(r => new RollupReconcileEntry
                {
                    RollupRtId = r.RollupRtId.ToString()
                }));
                logger.LogInformation(
                    "Rollup reconcile of tenant '{TenantId}': {RollupCount} rollup(s) are consistent, no repair needed",
                    tenantId, before.Rollups.Count);
                LogNotChecked(tenantId, summary);
                return summary;
            }

            if (before.IsOk)
            {
                // Consistent as far as the validator can see, but archives of the backup failed: nothing to repair, and the
                // marker stays set until an operator has dealt with them.
                summary.Ok = false;
                summary.FailedStep = $"{failed.Count} archive(s) of the backup failed to restore";
                summary.Rollups.AddRange(before.Rollups.Select(r => new RollupReconcileEntry
                {
                    RollupRtId = r.RollupRtId.ToString()
                }));
                LogReconcileBlocked(tenantId, summary);
                return summary;
            }

            summary.RepairRan = true;
            logger.LogWarning(
                "Rollup reconcile of tenant '{TenantId}': {Count} rollup(s) are inconsistent after the restore; repairing",
                tenantId, before.Rollups.Count(r => !r.IsOk));
            var repair = await repository.RepairRollupsAsync(null, false, ct);
            foreach (var rollup in repair.Before.Rollups)
            {
                var id = rollup.RollupRtId;
                summary.Rollups.Add(new RollupReconcileEntry
                {
                    RollupRtId = id.ToString(),
                    Before = rollup.Findings.Select(f => f.Message).ToList(),
                    Repair = repair.Actions.Where(a => a.RollupRtId == id)
                        .Select(a => $"{a.Kind}: {a.Description} [{a.Status}]{(a.Error == null ? string.Empty : " - " + a.Error)}")
                        .ToList(),
                    After = repair.After?.Rollups.FirstOrDefault(r => r.RollupRtId == id)?.Findings
                        .Select(f => f.Message).ToList() ?? []
                });
            }

            if (repair.After is not null)
            {
                AddNotChecked(summary, repair.After, failedIds);
            }

            summary.Ok = repair.IsOk && failed.Count == 0;
            summary.FailedStep = repair.FailedStep ??
                                 (!repair.IsOk
                                     ? "the rollups still do not validate after the repair"
                                     : failed.Count > 0
                                         ? $"{failed.Count} archive(s) of the backup failed to restore"
                                         : null);
            if (failed.Count > 0 && repair.IsOk)
            {
                // The repair cleared the marker; the failed archives keep it set.
                await repository.SetRestoreReconcilePendingAsync(true, ct);
            }

            if (!summary.Ok)
            {
                LogReconcileBlocked(tenantId, summary);
            }

            return summary;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveCopyNotSupportedException)
        {
            logger.LogInformation(
                "Rollup reconcile of tenant '{TenantId}' skipped: the stream data repository has no rollup validation",
                tenantId);
            return null;
        }
        catch (Exception e)
        {
            summary.Ok = false;
            summary.FailedStep = $"reconcile: {e.Message}";
            logger.LogError(e,
                "Rollup reconcile of tenant '{TenantId}' failed; the restore-reconcile marker stays as it is", tenantId);
            return summary;
        }
    }

    private static void AddNotChecked(RollupReconcileSummary summary, RollupConsistencyReport report,
        HashSet<string> failedArchiveIds)
    {
        foreach (var entry in report.Rollups)
        {
            var id = entry.RollupRtId.ToString();
            if (entry.NotChecked is { } gap && !summary.NotCheckedRollups.Any(n => n.RollupRtId == id))
            {
                summary.NotCheckedRollups.Add(new NotCheckedRollupSummary
                {
                    RollupRtId = id, Cause = gap.Cause.ToString(), Reason = gap.Reason, IsBlocking = gap.IsBlocking
                });
            }
        }

        // A rollup whose archive failed is not checked whatever the validator saw (a Created one is informational there).
        foreach (var id in failedArchiveIds.Where(id => report.Rollups.Any(r => r.RollupRtId.ToString() == id)))
        {
            if (!summary.NotCheckedRollups.Any(n => n.RollupRtId == id && n.IsBlocking))
            {
                summary.NotCheckedRollups.RemoveAll(n => n.RollupRtId == id);
                summary.NotCheckedRollups.Add(new NotCheckedRollupSummary
                {
                    RollupRtId = id, Cause = "ArchiveFailed", IsBlocking = true,
                    Reason = "the archive failed to restore, so its rows were not checked"
                });
            }
        }
    }

    private void LogNotChecked(string tenantId, RollupReconcileSummary summary)
    {
        foreach (var gap in summary.NotCheckedRollups)
        {
            logger.LogWarning("Rollup reconcile of tenant '{TenantId}': rollup '{RollupRtId}' was not checked ({Cause}): {Reason}",
                tenantId, gap.RollupRtId, gap.Cause, gap.Reason);
        }
    }

    private void LogReconcileBlocked(string tenantId, RollupReconcileSummary summary)
    {
        LogNotChecked(tenantId, summary);
        logger.LogError(
            "Rollup reconcile of tenant '{TenantId}' is not ok ({FailedStep}); the restore-reconcile marker stays set " +
            "and the tenant's reclaim, aggregation and recompute stay off until an operator has dealt with it. " +
            "Failed archives: {FailedArchives}",
            tenantId, summary.FailedStep,
            string.Join("; ", summary.FailedArchives.Select(f => $"{f.ArchiveRtId} ({f.Reason})")));
    }

    /// <summary>
    ///     A rollup restored with <c>RecomputeInProgress = true</c> (the backup was taken during a recompute) would suppress
    ///     the orphan check of the validator; nothing is recomputing it any more, so the flag is cleared.
    /// </summary>
    private async Task ResetStaleRecomputeFlagsAsync(string tenantId, ITenantContext tenantContext,
        RollupReconcileSummary summary)
    {
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore();
        if (rollupStore == null)
        {
            return;
        }

        var stateStore = tenantContext.GetArchiveRecomputeStateStore();
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        await foreach (var rollup in rollupStore.EnumerateAsync())
        {
            if (!rollup.RecomputeInProgress)
            {
                continue;
            }

            await stateStore.MarkRecomputeFailedAsync(rollup.RtId, now,
                "Recompute flag restored from a backup taken during a recompute; reset by the restore.");
            summary.ResetRecomputeFlags.Add(rollup.RtId.ToString());
            logger.LogWarning(
                "Rollup '{RollupRtId}' of tenant '{TenantId}' came back from the backup marked as recomputing; the flag " +
                "was reset", rollup.RtId, tenantId);
        }
    }
}
