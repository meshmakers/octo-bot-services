using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;
using NSubstitute;
using EngineArchiveImportMode = Meshmakers.Octo.Runtime.Contracts.StreamData.ArchiveImportMode;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     Covers the AB#4231 <c>restoreArchiveData</c> restore path: format auto-detection, Mongo-only
///     restore for legacy / flag-off, and the clean per-archive CrateDB restore sequence with
///     continue-and-report on per-archive failures.
/// </summary>
public class RestoreRepositoryArchiveDataTests
{
    private const string RtIdA = "665f00000000000000000e21";
    private const string RtIdB = "665f00000000000000000e22";

    private readonly ILogger<RestoreRepositoryJob> _logger = Substitute.For<ILogger<RestoreRepositoryJob>>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly IStreamDataRepository _repository = Substitute.For<IStreamDataRepository>();
    private readonly IArchiveRuntimeStore _archiveStore = Substitute.For<IArchiveRuntimeStore>();
    private readonly IArchiveLifecycleService _lifecycle = Substitute.For<IArchiveLifecycleService>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();

    private RestoreRepositoryJob CreateJob() => new(_logger, _systemContext, _backupFileStorage);

    private void SetupCommon(string filePath)
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _backupFileStorage.GetTusUploadFilePath(Arg.Any<string>(), "file-1").Returns(filePath);
        _systemContext.RestoreTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CommandResult { Success = true });
    }

    private void SetupTenant(CkArchiveStatus postRestoreStatus, params string[] rtIds)
    {
        _systemContext.FindTenantContextAsync("tenant-1").Returns(_tenantContext);
        _tenantContext.GetStreamDataRepository().Returns(_repository);
        _tenantContext.GetArchiveLifecycleService().Returns(_lifecycle);
        _tenantContext.GetArchiveRuntimeStore().Returns(_archiveStore);

        foreach (var rtId in rtIds)
        {
            var id = rtId;
            _archiveStore.GetAsync(Arg.Is<OctoObjectId>(o => o.ToString() == id))
                .Returns(Snapshot(id, postRestoreStatus));
        }
    }

    private static ArchiveSnapshot Snapshot(string rtId, CkArchiveStatus status,
        IReadOnlyList<CkArchiveColumnSpec>? columns = null) =>
        new(new OctoObjectId(rtId), new RtCkId<CkTypeId>("System-1.0.0/Sensor"), status, "voltage-raw",
            columns ?? new[] { new CkArchiveColumnSpec("voltage", true, false) });

    private static BackupManifestArchive ManifestEntry(ArchiveSnapshot snapshot, long rowCount = 1) =>
        new(ArchiveSchemaMapper.ToDto(snapshot), snapshot.Status.ToString(), rowCount,
            BackupArchiveContainer.NdjsonEntryFor(snapshot.RtId.ToString()));

    private static string WriteOctoBak(IReadOnlyList<BackupManifestArchive> archives, string mongoContent = "MONGO",
        IReadOnlyDictionary<string, string>? ndjson = null, int formatVersion = 1)
    {
        var manifest = new BackupManifest(formatVersion, DateTime.UtcNow, "source-tenant", true, archives);
        var path = Path.GetTempFileName();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var mongo = zip.CreateEntry(BackupArchiveContainer.MongoBlobEntry);
        using (var s = mongo.Open())
        {
            var bytes = Encoding.UTF8.GetBytes(mongoContent);
            s.Write(bytes, 0, bytes.Length);
        }

        var manifestEntry = zip.CreateEntry(BackupArchiveContainer.ManifestEntry);
        using (var s = manifestEntry.Open())
        {
            JsonSerializer.Serialize(s, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        foreach (var archive in archives)
        {
            if (archive.NdjsonEntry is null)
            {
                continue;
            }

            var content = ndjson != null && ndjson.TryGetValue(archive.NdjsonEntry, out var c) ? c : "{\"rtid\":\"61a\"}\n";
            var entry = zip.CreateEntry(archive.NdjsonEntry);
            using var s = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            s.Write(bytes, 0, bytes.Length);
        }

        return path;
    }

    [Test]
    public async Task Run_OctoBak_RestoreArchiveData_Disabled_RunsCleanSequence()
    {
        var snapshot = Snapshot(RtIdA, CkArchiveStatus.Disabled);
        var path = WriteOctoBak(new[] { ManifestEntry(snapshot) });
        try
        {
            SetupCommon(path);
            SetupTenant(CkArchiveStatus.Disabled, RtIdA);

            await CreateJob().Run("tenant-1", "db-1", "file-1", null, true, null);
            // Mongo restored exactly once from the extracted blob.
            await _systemContext.Received(1).RestoreTenantAsync(Arg.Is("tenant-1"), Arg.Is("db-1"), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            // Clean sequence: drop -> activate -> disable -> import(InsertOnly); no re-enable (was Disabled).
            await _repository.Received(1).DeleteArchiveAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA));
            await _lifecycle.Received(1).ActivateAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA));
            await _lifecycle.Received(1).DisableAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA));
            await _repository.Received(1).ImportRowsAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA),
                Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                EngineArchiveImportMode.InsertOnly, Arg.Any<CancellationToken>());
            await _lifecycle.DidNotReceive().EnableAsync(Arg.Any<OctoObjectId>());
            await _backupFileStorage.Received(1).DeleteFileAsync(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_OctoBak_RestoreArchiveData_Activated_NormalizesThenReenables()
    {
        // Post-restore status Activated: ActivateAsync is a no-op when already Activated, so the job
        // must Disable first to force the recreate to provision a fresh Crate table.
        var snapshot = Snapshot(RtIdA, CkArchiveStatus.Activated);
        var path = WriteOctoBak(new[] { ManifestEntry(snapshot) });
        try
        {
            SetupCommon(path);
            SetupTenant(CkArchiveStatus.Activated, RtIdA);

            await CreateJob().Run("tenant-1", "db-1", "file-1", null, true, null);
            // Disable called twice: the pre-activate normalisation + the post-activate import precondition.
            await _lifecycle.Received(2).DisableAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA));
            await _repository.Received(1).DeleteArchiveAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA));
            await _lifecycle.Received(1).ActivateAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA));
            await _repository.Received(1).ImportRowsAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA),
                Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                EngineArchiveImportMode.InsertOnly, Arg.Any<CancellationToken>());
            // Backed-up status was Activated -> re-enabled at the end.
            await _lifecycle.Received(1).EnableAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_OctoBak_RestoreArchiveDataOff_RestoresMongoOnly()
    {
        var snapshot = Snapshot(RtIdA, CkArchiveStatus.Activated);
        var path = WriteOctoBak(new[] { ManifestEntry(snapshot) });
        try
        {
            SetupCommon(path);
            SetupTenant(CkArchiveStatus.Activated, RtIdA);

            await CreateJob().Run("tenant-1", "db-1", "file-1", null, false, null);

            await _systemContext.Received(1).RestoreTenantAsync(Arg.Is("tenant-1"), Arg.Is("db-1"), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            // Flag off: archives are left untouched.
            await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
            await _repository.DidNotReceive().ImportRowsAsync(Arg.Any<OctoObjectId>(),
                Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<EngineArchiveImportMode>(), Arg.Any<CancellationToken>());
            await _lifecycle.DidNotReceive().ActivateAsync(Arg.Any<OctoObjectId>());
            await _backupFileStorage.Received(1).DeleteFileAsync(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_OctoBak_SchemaMismatch_SkipsArchiveAndContinues()
    {
        // Archive A's manifest schema declares an extra column the post-restore archive lacks ->
        // mismatch -> skip. Archive B matches -> imported. The job still succeeds.
        var snapshotB = Snapshot(RtIdB, CkArchiveStatus.Disabled);
        var driftedSchemaA = ArchiveSchemaMapper.ToDto(Snapshot(RtIdA, CkArchiveStatus.Disabled)) with
        {
            Columns = new[]
            {
                new ArchiveColumnDto("voltage", true, false),
                new ArchiveColumnDto("phase", false, false)
            }
        };
        var entryA = new BackupManifestArchive(driftedSchemaA, "Disabled", 1,
            BackupArchiveContainer.NdjsonEntryFor(RtIdA));
        var path = WriteOctoBak(new[] { entryA, ManifestEntry(snapshotB) });
        try
        {
            SetupCommon(path);
            // Both archives exist post-restore with the (un-drifted) on-disk schema.
            SetupTenant(CkArchiveStatus.Disabled, RtIdA, RtIdB);

            await CreateJob().Run("tenant-1", "db-1", "file-1", null, true, null);
            // A skipped: never touched.
            await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA));
            await _lifecycle.DidNotReceive().ActivateAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA));
            // B imported via the clean sequence.
            await _repository.Received(1).DeleteArchiveAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdB));
            await _repository.Received(1).ImportRowsAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdB),
                Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                EngineArchiveImportMode.InsertOnly, Arg.Any<CancellationToken>());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_OctoBak_MissingPostRestoreArchive_SkipsAndStillSucceeds()
    {
        var snapshot = Snapshot(RtIdA, CkArchiveStatus.Disabled);
        var path = WriteOctoBak(new[] { ManifestEntry(snapshot) });
        try
        {
            SetupCommon(path);
            _systemContext.FindTenantContextAsync("tenant-1").Returns(_tenantContext);
            _tenantContext.GetStreamDataRepository().Returns(_repository);
            _tenantContext.GetArchiveLifecycleService().Returns(_lifecycle);
            _tenantContext.GetArchiveRuntimeStore().Returns(_archiveStore);
            // Archive absent in the tenant after the restore.
            _archiveStore.GetAsync(Arg.Any<OctoObjectId>()).Returns((ArchiveSnapshot?)null);

            // Must not throw — skip + report.
            await CreateJob().Run("tenant-1", "db-1", "file-1", null, true, null);

            await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
            await _repository.DidNotReceive().ImportRowsAsync(Arg.Any<OctoObjectId>(),
                Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<EngineArchiveImportMode>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_LegacyTarGz_WithFlagOn_RestoresMongoOnlyWithWarning()
    {
        // A non-ZIP artifact (legacy mongodump) with restoreArchiveData on must restore Mongo only.
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "this-is-not-a-zip-archive-blob");
        try
        {
            SetupCommon(path);

            await CreateJob().Run("tenant-1", "db-1", "file-1", null, true, null);

            await _systemContext.Received(1).RestoreTenantAsync(Arg.Is("tenant-1"), Arg.Is("db-1"), Arg.Is(path),
                Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await _repository.DidNotReceive().ImportRowsAsync(Arg.Any<OctoObjectId>(),
                Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<EngineArchiveImportMode>(), Arg.Any<CancellationToken>());
            await _backupFileStorage.Received(1).DeleteFileAsync(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
