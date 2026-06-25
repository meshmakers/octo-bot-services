using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;
using RepositoryUpdate;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that dumps a tenant database to a backup file on disk. When
/// <c>includeArchiveData</c> is set, the mongodump blob is wrapped together with the tenant's CrateDB
/// archive rows into an <c>.octobak.zip</c> container (concept AB#4231 §3/§4); otherwise the legacy
/// single <c>.tar.gz</c> mongodump artifact is produced unchanged.
/// </summary>
public class DumpRepositoryJob(
    ILogger<DumpRepositoryJob> logger,
    ISystemContext systemContext,
    IBackupFileStorageService backupFileStorage) : IDumpRepositoryJob
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions NdjsonJsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<string?> Run(string tenantId, bool includeArchiveData,
        IBotCancellationToken? cancellationToken)
    {
        var ct = cancellationToken?.ShutdownToken ?? CancellationToken.None;

        try
        {
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                return null;
            }

            var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

            if (tenantContext == null)
            {
                throw RepositoryUpdateException.TenantContextNotFound(tenantId);
            }

            // 1. Always produce the mongodump blob first.
            var mongoFileName = backupFileStorage.GenerateDumpFileName(tenantId);
            var mongoFilePath = backupFileStorage.GetDumpFilePath(tenantId, mongoFileName);

            // Ensure tenant subdirectory exists
            var directory = Path.GetDirectoryName(mongoFilePath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            logger.LogInformation("Running dump repository command for '{TenantId}' to '{FilePath}'", tenantId,
                mongoFilePath);

            var r = await systemContext.BackupTenantAsync(tenantId, mongoFilePath,
                timeout: TimeSpan.FromHours(1));

            if (!r.Success)
            {
                throw JobFailedException.CommandExecutionFailed(r, tenantId, "mongodump");
            }

            if (!includeArchiveData)
            {
                // Default path — the mongodump blob IS the downloadable result, unchanged.
                logger.LogInformation("Dump completed for tenant '{TenantId}' at '{FilePath}'", tenantId,
                    mongoFilePath);
                return mongoFilePath;
            }

            // 2. includeArchiveData — wrap the mongo blob + archive rows into an .octobak.zip and
            //    register that as the downloadable result. The intermediate mongo blob is deleted.
            return await BuildBackupArchiveAsync(tenantId, tenantContext, mongoFilePath, ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Dump repository for tenant '{TenantId}' was cancelled", tenantId);
            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while dumping repository database for tenant '{TenantId}'", tenantId);
            throw;
        }
    }

    /// <summary>
    ///     Streams the mongo blob, every archive's NDJSON rows, and the manifest into the
    ///     <c>.octobak.zip</c> container (concept §3/§4). The intermediate mongo blob is removed once
    ///     embedded. Entries are streamed; no archive is buffered whole in memory.
    /// </summary>
    private async Task<string> BuildBackupArchiveAsync(string tenantId, ITenantContext tenantContext,
        string mongoFilePath, CancellationToken ct)
    {
        var zipFileName = BuildBackupArchiveFileName(tenantId);
        var zipFilePath = backupFileStorage.GetDumpFilePath(tenantId, zipFileName);

        try
        {
            // Materialise the archive list (one row per archive definition — cheap) so the Mongo
            // enumeration cursor is not held open while we stream Crate rows + write ZIP entries.
            var snapshots = await LoadArchiveSnapshotsAsync(tenantId, tenantContext);

            logger.LogInformation(
                "Building tenant backup archive '{FilePath}' for '{TenantId}' including {ArchiveCount} archive(s)",
                zipFilePath, tenantId, snapshots.Count);

            await using (var zipFileStream =
                         new FileStream(zipFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(zipFileStream, ZipArchiveMode.Create))
            {
                // mongo.tar.gz — copy the mongodump blob verbatim. Already gzipped, so no recompression.
                var mongoEntry = zip.CreateEntry(BackupArchiveContainer.MongoBlobEntry, CompressionLevel.NoCompression);
                await using (var mongoEntryStream = mongoEntry.Open())
                await using (var mongoBlobStream =
                             new FileStream(mongoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await mongoBlobStream.CopyToAsync(mongoEntryStream, ct);
                }

                // archives/<rtId>.ndjson — one entry per archive that HAS a provisioned table.
                var repository = tenantContext.GetStreamDataRepository();
                var manifestArchives = new List<BackupManifestArchive>(snapshots.Count);

                foreach (var snapshot in snapshots)
                {
                    ct.ThrowIfCancellationRequested();
                    manifestArchives.Add(await WriteArchiveAsync(zip, repository, snapshot, tenantId, ct));
                }

                // manifest.json — written last so each entry carries its final exported row count.
                var manifest = new BackupManifest(
                    BackupArchiveContainer.CurrentFormatVersion,
                    DateTime.UtcNow,
                    tenantId,
                    IncludesArchiveData: true,
                    manifestArchives);

                var manifestEntry = zip.CreateEntry(BackupArchiveContainer.ManifestEntry, CompressionLevel.Optimal);
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, ManifestJsonOptions, ct);
                }
            }

            logger.LogInformation("Tenant backup archive completed for '{TenantId}' at '{FilePath}'", tenantId,
                zipFilePath);

            return zipFilePath;
        }
        catch
        {
            // On failure, do not leave a half-written .octobak.zip behind.
            await backupFileStorage.DeleteFileAsync(zipFilePath);
            throw;
        }
        finally
        {
            // The intermediate mongo blob is now embedded (or the build failed); drop it either way.
            await backupFileStorage.DeleteFileAsync(mongoFilePath);
        }
    }

    /// <summary>
    ///     Reads every archive snapshot of the tenant. Returns an empty list when stream data is not
    ///     enabled (no <see cref="IStreamDataRepository"/>) — the backup then just carries the mongo
    ///     blob + an empty-archive manifest.
    /// </summary>
    private async Task<List<ArchiveSnapshot>> LoadArchiveSnapshotsAsync(string tenantId,
        ITenantContext tenantContext)
    {
        var snapshots = new List<ArchiveSnapshot>();

        if (tenantContext.GetStreamDataRepository() is null)
        {
            logger.LogInformation(
                "Stream data is not enabled for tenant '{TenantId}'; the backup carries no archive data", tenantId);
            return snapshots;
        }

        await foreach (var snapshot in tenantContext.GetArchiveRuntimeStore().EnumerateAsync())
        {
            snapshots.Add(snapshot);
        }

        return snapshots;
    }

    /// <summary>
    ///     Writes one archive's NDJSON rows into the ZIP and returns its manifest entry. An archive with
    ///     a provisioned Crate table (status <see cref="CkArchiveStatus.Activated"/> or
    ///     <see cref="CkArchiveStatus.Disabled"/>) has its rows streamed out; a
    ///     <see cref="CkArchiveStatus.Created"/>/<see cref="CkArchiveStatus.Failed"/> archive has no
    ///     table, so it is recorded with row count 0 and no NDJSON entry (concept §4).
    /// </summary>
    private async Task<BackupManifestArchive> WriteArchiveAsync(ZipArchive zip, IStreamDataRepository? repository,
        ArchiveSnapshot snapshot, string tenantId, CancellationToken ct)
    {
        var schema = ArchiveSchemaMapper.ToDto(snapshot);
        var rtId = snapshot.RtId.ToString();
        var hasTable = snapshot.Status is CkArchiveStatus.Activated or CkArchiveStatus.Disabled;

        if (!hasTable || repository is null)
        {
            logger.LogInformation(
                "Archive '{ArchiveRtId}' of tenant '{TenantId}' has status '{Status}' (no Crate table); recording it " +
                "in the manifest without data", rtId, tenantId, snapshot.Status);
            return new BackupManifestArchive(schema, snapshot.Status.ToString(), RowCount: 0, NdjsonEntry: null);
        }

        var ndjsonEntryName = BackupArchiveContainer.NdjsonEntryFor(rtId);
        var dataEntry = zip.CreateEntry(ndjsonEntryName, CompressionLevel.Optimal);

        long rowCount = 0;
        await using (var dataEntryStream = dataEntry.Open())
        await using (var writer = new StreamWriter(dataEntryStream, new UTF8Encoding(false), 64 * 1024))
        {
            // Deterministic '\n' line endings across platforms (matches the AB#4230 export writer).
            writer.NewLine = "\n";

            var rowsSinceFlush = 0;
            await foreach (var row in repository.ExportRowsAsync(snapshot.RtId, null, ct))
            {
                var line = JsonSerializer.Serialize(row, NdjsonJsonOptions);
                await writer.WriteLineAsync(line.AsMemory(), ct);
                rowCount++;

                if (++rowsSinceFlush >= 256)
                {
                    await writer.FlushAsync(ct);
                    rowsSinceFlush = 0;
                }
            }

            await writer.FlushAsync(ct);
        }

        logger.LogInformation("Exported {RowCount} row(s) of archive '{ArchiveRtId}' into the tenant backup",
            rowCount, rtId);

        return new BackupManifestArchive(schema, snapshot.Status.ToString(), rowCount, ndjsonEntryName);
    }

    private static string BuildBackupArchiveFileName(string tenantId)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var guid = Guid.NewGuid().ToString("N")[..8];
        return $"{tenantId}-{timestamp}-{guid}.octobak.zip";
    }
}
