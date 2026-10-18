using System.Reflection;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

// Format 2 of the tenant backup (AB#5967, concept-tenant-backup-native-archive-dump.md §4 to §6).
public partial class DumpRepositoryJob
{
    private static readonly JsonSerializerOptions NativeManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    ///     Dumps the archives natively and streams the format 2 container into the artifact store. Returns the job result
    ///     (the store reference), or <c>null</c> when the dump must run as format 1 — the reason is in the job log and
    ///     nothing was changed (the mongodump blob is still on disk for the format 1 path).
    /// </summary>
    private async Task<string?> TryDumpNativeAsync(string tenantId, ITenantContext tenantContext,
        string mongoFilePath, DateTime mongoFinishedAtUtc, CancellationToken ct)
    {
        var repository = tenantContext.GetStreamDataRepository();
        var unavailable = NativeCopyUnavailableReason(repository);
        if (unavailable != null)
        {
            logger.LogInformation(
                "The dump of tenant '{TenantId}' runs as format 1 (archive rows through the service): {Reason}",
                tenantId, unavailable);
            return null;
        }

        var readiness = await repository!.CheckStagingCopyReadinessAsync(ct);
        if (readiness is { IsReady: false })
        {
            logger.LogInformation(
                "The dump of tenant '{TenantId}' runs as format 1 (archive rows through the service): {Reason}",
                tenantId, readiness.Reason ?? "the native copy is not ready");
            return null;
        }

        var runId = Guid.NewGuid().ToString("N")[..16];
        var fallsBack = false;
        try
        {
            var snapshots = await LoadArchiveSnapshotsAsync(tenantId, tenantContext);
            var provisioned = await LoadProvisionedTablesAsync(repository, snapshots, tenantId, ct);
            var location = stagingFactory!.Create(tenantId, runId, ArchiveCopyStagingAccess.Dump);

            logger.LogInformation(
                "Dumping {ProvisionedCount} of {ArchiveCount} archive(s) of tenant '{TenantId}' natively into copy " +
                "staging run '{RunId}' ({Location})", provisioned.Count, snapshots.Count, tenantId, runId, location);

            var results = new Dictionary<OctoObjectId, ArchiveCopyDumpResult>();
            foreach (var snapshot in snapshots.Where(s => provisioned.Contains(s.RtId)))
            {
                ct.ThrowIfCancellationRequested();
                ArchiveCopyDumpResult result;
                try
                {
                    result = await repository!.DumpArchiveToStagingAsync(snapshot.RtId, location, null, ct);
                }
                catch (ArchiveCopyRefusedException e) when (e.Reason == ArchiveCopyRefusalReason.JobLogNotFiltered)
                {
                    // Nothing was moved for this archive: the whole dump takes the row path instead.
                    logger.LogWarning(
                        "The dump of tenant '{TenantId}' runs as format 1 (archive rows through the service): {Reason}",
                        tenantId, location.Scrub(e.Message));
                    fallsBack = true;
                    return null;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // The text may echo the statement: scrub it, and keep the exception itself (its message and stack) out of the result.
                    throw NativeDumpFailed(tenantId, snapshot.RtId.ToString(), location.Scrub(e.Message), results, null);
                }

                if (!result.IsSuccess)
                {
                    var failed = location.Scrub(result.Slices.Where(s => !s.IsSuccess).Select(s => s.Message)
                        .FirstOrDefault() ?? "a slice did not copy");
                    throw NativeDumpFailed(tenantId, snapshot.RtId.ToString(), failed, results, null);
                }

                results[snapshot.RtId] = result;
            }

            var staged = new List<StagingObject>();
            await foreach (var item in artifactStorage!.ListStagingAsync(tenantId, runId, ct))
            {
                staged.Add(item);
            }

            var manifest = await BuildNativeManifestAsync(tenantId, repository!, snapshots, results, staged,
                mongoFinishedAtUtc, ct);
            var zipFileName = BuildBackupArchiveFileName(tenantId);
            var stored = await artifactStorage.StoreFromWriterAsync(ArtifactCategories.TenantDumps, tenantId,
                zipFileName,
                (target, token) => OctobakContainerWriter.WriteAsync(target, mongoFilePath, artifactStorage, tenantId,
                    runId, staged, manifest, NativeManifestJsonOptions, token),
                ArtifactEncryption.IfConfigured, ct);

            logger.LogInformation(
                "Dump (format 2) of tenant '{TenantId}' stored as artifact '{FileName}' ({Size} bytes, encrypted: " +
                "{Encrypted}); {ArchiveCount} archive(s), {FileCount} staged file(s)", tenantId, stored.FileName,
                stored.Size, stored.Encrypted, results.Count, staged.Count);
            return stored.ToResultReference();
        }
        finally
        {
            await DeleteRunQuietlyAsync(tenantId, runId);
            if (!fallsBack)
            {
                // The mongo blob is packed (or the dump failed); only a fallback to format 1 still needs it.
                await backupFileStorage.DeleteFileAsync(mongoFilePath);
            }
        }
    }

    /// <summary>
    ///     Why the dump cannot use the native copy, or <c>null</c> when it can. Each reason names the missing piece.
    /// </summary>
    private string? NativeCopyUnavailableReason(IStreamDataRepository? repository)
    {
        if (artifactStorage == null)
        {
            return "no artifact store is wired, so there is no staging area";
        }

        if (stagingFactory == null)
        {
            return "no staging location factory is registered";
        }

        if (!stagingFactory.IsSupported)
        {
            return stagingFactory.UnsupportedReason ?? "the artifact store cannot serve as a staging area";
        }

        if (repository == null)
        {
            return "stream data is not enabled for the tenant";
        }

        return repository.SupportsStagingCopy
            ? null
            : "the stream data repository has no native copy (IStreamDataRepository.SupportsStagingCopy is false)";
    }

    private JobFailedException NativeDumpFailed(string tenantId, string archiveRtId, string reason,
        IReadOnlyDictionary<OctoObjectId, ArchiveCopyDumpResult> done, Exception? inner)
    {
        var doneSlices = done.Values.Sum(r => r.Slices.Count(s => s.IsSuccess));
        var message =
            $"The native dump of archive '{archiveRtId}' of tenant '{tenantId}' failed: {reason} " +
            $"Done before: {done.Count} archive(s), {doneSlices} slice(s) ({string.Join(", ", done.Keys)}). " +
            "The staging files of the run were deleted.";
        logger.LogError("{Message}", message);
        return inner == null ? new JobFailedException(message) : new JobFailedException(message, inner);
    }

    private async Task DeleteRunQuietlyAsync(string tenantId, string runId)
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

    private async Task<BackupManifest> BuildNativeManifestAsync(string tenantId, IStreamDataRepository repository,
        IReadOnlyList<ArchiveSnapshot> snapshots, IReadOnlyDictionary<OctoObjectId, ArchiveCopyDumpResult> results,
        IReadOnlyList<StagingObject> staged, DateTime mongoFinishedAtUtc, CancellationToken ct)
    {
        var archives = new List<BackupManifestArchive>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var schema = ArchiveSchemaMapper.ToDto(snapshot);
            if (!results.TryGetValue(snapshot.RtId, out var result))
            {
                logger.LogInformation(
                    "Archive '{ArchiveRtId}' of tenant '{TenantId}' has status '{Status}' and no Crate table; recording " +
                    "it in the manifest without data", snapshot.RtId, tenantId, snapshot.Status);
                archives.Add(new BackupManifestArchive(schema, snapshot.Status.ToString(), RowCount: 0,
                    NdjsonEntry: null));
                continue;
            }

            var rtId = snapshot.RtId.ToString();
            var slices = new List<BackupManifestSlice>(result.Plan.Slices.Count);
            for (var i = 0; i < result.Plan.Slices.Count; i++)
            {
                var slice = result.Plan.Slices[i];
                var folder = slice.GetStagingPath(result.Plan.ArchiveRtId) + "/";
                var stagedBytes = staged.Where(s => s.RelativePath.StartsWith(folder, StringComparison.Ordinal))
                    .Sum(s => s.Size);
                var sliceResult = i < result.Slices.Count ? result.Slices[i] : null;
                slices.Add(new BackupManifestSlice(slice.Table.ToString(), slice.Key, slice.FromUtc, slice.ToUtc,
                    slice.RowCount, sliceResult?.Slice.RowCount ?? slice.RowCount,
                    sliceResult?.CopiedRows ?? slice.RowCount, stagedBytes, sliceResult?.Message));
            }

            var tables = result.Plan.Tables
                .Select(t => new BackupManifestTable(t.Table.ToString(),
                    t.Columns.Select(c => new BackupManifestColumn(c.Name, c.DataType)).ToList()))
                .ToList();
            var dataRows = result.Plan.Slices.Where(s => s.Table == ArchiveCopyTable.Data).Sum(s => s.RowCount);
            archives.Add(new BackupManifestArchive(schema, snapshot.Status.ToString(), dataRows, NdjsonEntry: null,
                new BackupManifestArchiveCopy(tables, slices, result.Attempts, result.MapReadAtUtc,
                    result.LastDataCopiedAtUtc)));
            logger.LogInformation(
                "Archive '{ArchiveRtId}': {SliceCount} slice(s), {Rows} row(s), {Attempts} attempt(s), map read at " +
                "{MapReadAt}, last data copied at {LastDataCopiedAt}", rtId, slices.Count, dataRows, result.Attempts,
                result.MapReadAtUtc, result.LastDataCopiedAtUtc);
        }

        string? storageVersion = null;
        try
        {
            storageVersion = await repository.GetStorageVersionAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not read the storage version of the source; the manifest records none");
        }

        var platformVersion = typeof(DumpRepositoryJob).Assembly
                                  .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                              ?? typeof(DumpRepositoryJob).Assembly.GetName().Version?.ToString() ?? "unknown";
        return new BackupManifest(BackupArchiveContainer.NativeFormatVersion,
            (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime, tenantId, IncludesArchiveData: true,
            archives, new BackupManifestNative(storageVersion, platformVersion, mongoFinishedAtUtc));
    }
}
