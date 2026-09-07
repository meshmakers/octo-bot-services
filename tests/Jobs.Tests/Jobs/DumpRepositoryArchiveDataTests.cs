using System.IO.Compression;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     Covers the AB#4231 <c>includeArchiveData</c> dump path: bundling the mongodump blob with the
///     tenant's CrateDB archive rows into an <c>.octobak.zip</c> container.
/// </summary>
public class DumpRepositoryArchiveDataTests
{
    private const string ActivatedRtId = "665f00000000000000000e21";
    private const string CreatedRtId = "665f00000000000000000e22";
    private const string SeededDisabledRtId = "665f00000000000000000e23";
    private const string FailedRtId = "665f00000000000000000e24";

    private readonly ILogger<DumpRepositoryJob> _logger = Substitute.For<ILogger<DumpRepositoryJob>>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly IStreamDataRepository _repository = Substitute.For<IStreamDataRepository>();
    private readonly IArchiveRuntimeStore _archiveStore = Substitute.For<IArchiveRuntimeStore>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();

    private DumpRepositoryJob CreateJob() => new(_logger, _systemContext, _backupFileStorage);

    private void SetupCommon(string tempDir)
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _systemContext.FindTenantContextAsync("tenant-1").Returns(_tenantContext);

        _backupFileStorage.GenerateDumpFileName("tenant-1").Returns("tenant-1-mongo.tar.gz");
        _backupFileStorage.GetDumpFilePath("tenant-1", Arg.Any<string>())
            .Returns(ci => Path.Combine(tempDir, (string)ci[1]));

        // mongodump side-effect: actually write the blob so it can be copied into the ZIP verbatim.
        _systemContext.BackupTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>())
            .Returns(ci =>
            {
                var path = (string)ci[1];
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "MONGO-BLOB");
                return new CommandResult { Success = true };
            });
    }

    private static ArchiveSnapshot Snapshot(string rtId, CkArchiveStatus status, string name) =>
        new(new OctoObjectId(rtId), new RtCkId<CkTypeId>("System-1.0.0/Sensor"), status, name,
            new[] { new CkArchiveColumnSpec("voltage", true, false) });

    private static async IAsyncEnumerable<ArchiveSnapshot> Snapshots(params ArchiveSnapshot[] snapshots)
    {
        foreach (var s in snapshots)
        {
            yield return s;
            await Task.CompletedTask;
        }
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(
        params IReadOnlyDictionary<string, object?>[] rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            await Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Stubs the bulk storage-stats probe the dump uses to decide which archives have a Crate table
    ///     (AB#5141). The lifecycle status of a snapshot is irrelevant to that decision.
    /// </summary>
    private void StubTables(params (string RtId, bool TableExists)[] entries)
    {
        IReadOnlyDictionary<OctoObjectId, ArchiveStorageStats> stats = entries.ToDictionary(
            e => new OctoObjectId(e.RtId),
            e => new ArchiveStorageStats(new OctoObjectId(e.RtId), e.TableExists,
                RecordCount: 0, SizeBytes: 0, Health: ArchiveStorageHealth.Good));

        _repository.GetArchiveStatsAsync(Arg.Any<IReadOnlyList<OctoObjectId>>(), Arg.Any<CancellationToken>())
            .Returns(stats);
    }

    private void SetupTenantWithArchives(params ArchiveSnapshot[] snapshots)
    {
        _tenantContext.GetStreamDataRepository().Returns(_repository);
        _tenantContext.GetArchiveRuntimeStore().Returns(_archiveStore);
        _archiveStore.EnumerateAsync().Returns(_ => Snapshots(snapshots));
    }

    private static async Task<BackupManifest> ReadManifestAsync(string octobakPath)
    {
        using var zip = ZipFile.OpenRead(octobakPath);
        await using var manifestStream = zip.GetEntry(BackupArchiveContainer.ManifestEntry)!.Open();
        return (await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static bool HasNdjsonEntry(string octobakPath, string rtId)
    {
        using var zip = ZipFile.OpenRead(octobakPath);
        return zip.GetEntry(BackupArchiveContainer.NdjsonEntryFor(rtId)) is not null;
    }

    [Test]
    public async Task Run_IncludeArchiveData_ProducesOctoBakWithMongoManifestAndArchives()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"octobak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SetupCommon(tempDir);

            var activated = Snapshot(ActivatedRtId, CkArchiveStatus.Activated, "voltage-raw");
            var created = Snapshot(CreatedRtId, CkArchiveStatus.Created, "pending-archive");

            SetupTenantWithArchives(activated, created);
            StubTables((ActivatedRtId, true), (CreatedRtId, false));
            _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
                .Returns(_ => Rows(
                    new Dictionary<string, object?> { ["rtid"] = "61a", ["voltage"] = 230.1 },
                    new Dictionary<string, object?> { ["rtid"] = "61b", ["voltage"] = 229.8 }));

            var job = CreateJob();

            var resultPath = await job.Run("tenant-1", true, null);

            await Assert.That(resultPath).IsNotNull();
            await Assert.That(resultPath!.EndsWith(".octobak.zip")).IsTrue();
            await Assert.That(File.Exists(resultPath)).IsTrue();

            // The activated archive's rows are exported once; the Created archive (no table) is not.
            _repository.Received(1).ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>());

            using var zip = ZipFile.OpenRead(resultPath);

            var mongoEntry = zip.GetEntry(BackupArchiveContainer.MongoBlobEntry);
            await Assert.That(mongoEntry).IsNotNull();
            await using (var ms = mongoEntry!.Open())
            using (var reader = new StreamReader(ms))
            {
                await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("MONGO-BLOB");
            }

            var activatedNdjson = zip.GetEntry(BackupArchiveContainer.NdjsonEntryFor(ActivatedRtId));
            await Assert.That(activatedNdjson).IsNotNull();
            var createdNdjson = zip.GetEntry(BackupArchiveContainer.NdjsonEntryFor(CreatedRtId));
            await Assert.That(createdNdjson).IsNull();

            var manifestEntry = zip.GetEntry(BackupArchiveContainer.ManifestEntry);
            await Assert.That(manifestEntry).IsNotNull();

            await using var manifestStream = manifestEntry!.Open();
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await Assert.That(manifest!.FormatVersion).IsEqualTo(1);
            await Assert.That(manifest.IncludesArchiveData).IsTrue();
            await Assert.That(manifest.SourceTenantId).IsEqualTo("tenant-1");
            await Assert.That(manifest.Archives.Count).IsEqualTo(2);

            var activatedEntry = manifest.Archives.Single(a => a.Schema.RtId == ActivatedRtId);
            await Assert.That(activatedEntry.Status).IsEqualTo("Activated");
            await Assert.That(activatedEntry.RowCount).IsEqualTo(2L);
            await Assert.That(activatedEntry.NdjsonEntry).IsEqualTo(BackupArchiveContainer.NdjsonEntryFor(ActivatedRtId));

            var createdEntry = manifest.Archives.Single(a => a.Schema.RtId == CreatedRtId);
            await Assert.That(createdEntry.Status).IsEqualTo("Created");
            await Assert.That(createdEntry.RowCount).IsEqualTo(0L);
            await Assert.That(createdEntry.NdjsonEntry).IsNull();

            // The intermediate mongo blob is deleted once embedded.
            await _backupFileStorage.Received(1).DeleteFileAsync(Path.Combine(tempDir, "tenant-1-mongo.tar.gz"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_IncludeArchiveDataFalse_ReturnsMongoTarGzAndDoesNotEnumerateArchives()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"octobak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SetupCommon(tempDir);

            var job = CreateJob();

            var resultPath = await job.Run("tenant-1", false, null);

            await Assert.That(resultPath).IsEqualTo(Path.Combine(tempDir, "tenant-1-mongo.tar.gz"));
            await Assert.That(resultPath!.EndsWith(".tar.gz")).IsTrue();
            // Default path must never touch the archive store / stream repository.
            _archiveStore.DidNotReceive().EnumerateAsync();
            _tenantContext.DidNotReceive().GetStreamDataRepository();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_IncludeArchiveData_StreamDataNotEnabled_ProducesOctoBakWithEmptyArchives()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"octobak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SetupCommon(tempDir);
            _tenantContext.GetStreamDataRepository().Returns((IStreamDataRepository?)null);

            var job = CreateJob();

            var resultPath = await job.Run("tenant-1", true, null);

            await Assert.That(resultPath!.EndsWith(".octobak.zip")).IsTrue();
            _archiveStore.DidNotReceive().EnumerateAsync();

            using var zip = ZipFile.OpenRead(resultPath);
            await using var manifestStream = zip.GetEntry(BackupArchiveContainer.ManifestEntry)!.Open();
            var manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await Assert.That(manifest!.Archives.Count).IsEqualTo(0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_IncludeArchiveData_DisabledArchiveWithoutTable_IsListedWithoutData()
    {
        // AB#5141: a blueprint seeds archives Disabled (Archive.Status = 2) without ever activating
        // them, so they have no Crate table. The dump must not export them (that failed with 42P01)
        // but list them with zero rows and no data entry, exactly like a Created archive.
        var tempDir = Path.Combine(Path.GetTempPath(), $"octobak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SetupCommon(tempDir);
            var seeded = Snapshot(SeededDisabledRtId, CkArchiveStatus.Disabled, "energy-measurements-legacy-daily");
            SetupTenantWithArchives(seeded);
            StubTables((SeededDisabledRtId, false));

            var resultPath = await CreateJob().Run("tenant-1", true, null);

            _repository.DidNotReceive().ExportRowsAsync(Arg.Any<OctoObjectId>(), Arg.Any<TimeWindow?>(),
                Arg.Any<CancellationToken>());
            await Assert.That(HasNdjsonEntry(resultPath!, SeededDisabledRtId)).IsFalse();

            var manifest = await ReadManifestAsync(resultPath!);
            var entry = manifest.Archives.Single();
            await Assert.That(entry.Schema.RtId).IsEqualTo(SeededDisabledRtId);
            await Assert.That(entry.Status).IsEqualTo("Disabled");
            await Assert.That(entry.RowCount).IsEqualTo(0L);
            await Assert.That(entry.NdjsonEntry).IsNull();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_IncludeArchiveData_DisabledArchiveWithEmptyTable_GetsAnEmptyDataEntry()
    {
        // "Empty table" and "no table" must stay distinguishable in the manifest: the former is
        // backed up with a (empty) NDJSON entry so the restore recreates the table.
        var tempDir = Path.Combine(Path.GetTempPath(), $"octobak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SetupCommon(tempDir);
            var disabled = Snapshot(SeededDisabledRtId, CkArchiveStatus.Disabled, "voltage-raw");
            SetupTenantWithArchives(disabled);
            StubTables((SeededDisabledRtId, true));
            _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
                .Returns(_ => Rows());

            var resultPath = await CreateJob().Run("tenant-1", true, null);

            _repository.Received(1).ExportRowsAsync(Arg.Is<OctoObjectId>(o => o.ToString() == SeededDisabledRtId),
                null, Arg.Any<CancellationToken>());
            await Assert.That(HasNdjsonEntry(resultPath!, SeededDisabledRtId)).IsTrue();

            var entry = (await ReadManifestAsync(resultPath!)).Archives.Single();
            await Assert.That(entry.Status).IsEqualTo("Disabled");
            await Assert.That(entry.RowCount).IsEqualTo(0L);
            await Assert.That(entry.NdjsonEntry).IsEqualTo(BackupArchiveContainer.NdjsonEntryFor(SeededDisabledRtId));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_IncludeArchiveData_FailedArchiveWithTable_IsExported()
    {
        // A re-enable that fails leaves the archive Failed with its table and data intact. The old
        // status heuristic silently dropped such an archive from the backup; the table decides now.
        var tempDir = Path.Combine(Path.GetTempPath(), $"octobak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SetupCommon(tempDir);
            var failed = Snapshot(FailedRtId, CkArchiveStatus.Failed, "voltage-raw");
            SetupTenantWithArchives(failed);
            StubTables((FailedRtId, true));
            _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
                .Returns(_ => Rows(new Dictionary<string, object?> { ["rtid"] = "61a", ["voltage"] = 230.1 }));

            var resultPath = await CreateJob().Run("tenant-1", true, null);

            await Assert.That(HasNdjsonEntry(resultPath!, FailedRtId)).IsTrue();
            var entry = (await ReadManifestAsync(resultPath!)).Archives.Single();
            await Assert.That(entry.Status).IsEqualTo("Failed");
            await Assert.That(entry.RowCount).IsEqualTo(1L);
            await Assert.That(entry.NdjsonEntry).IsNotNull();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_IncludeArchiveData_ActivatedArchiveWithoutTable_IsListedWithoutData()
    {
        // Aftermath of a Mongo-only restore: Activated in Mongo, no table in Crate. The backup records
        // what exists (nothing) instead of failing on the export.
        var tempDir = Path.Combine(Path.GetTempPath(), $"octobak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SetupCommon(tempDir);
            var activated = Snapshot(ActivatedRtId, CkArchiveStatus.Activated, "voltage-raw");
            SetupTenantWithArchives(activated);
            StubTables((ActivatedRtId, false));

            var resultPath = await CreateJob().Run("tenant-1", true, null);

            _repository.DidNotReceive().ExportRowsAsync(Arg.Any<OctoObjectId>(), Arg.Any<TimeWindow?>(),
                Arg.Any<CancellationToken>());
            var entry = (await ReadManifestAsync(resultPath!)).Archives.Single();
            await Assert.That(entry.Status).IsEqualTo("Activated");
            await Assert.That(entry.RowCount).IsEqualTo(0L);
            await Assert.That(entry.NdjsonEntry).IsNull();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_IncludeArchiveData_ProbesTablesOnceForAllArchives()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"octobak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SetupCommon(tempDir);
            SetupTenantWithArchives(
                Snapshot(ActivatedRtId, CkArchiveStatus.Activated, "a"),
                Snapshot(SeededDisabledRtId, CkArchiveStatus.Disabled, "b"),
                Snapshot(CreatedRtId, CkArchiveStatus.Created, "c"));
            StubTables((ActivatedRtId, true), (SeededDisabledRtId, false), (CreatedRtId, false));
            _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
                .Returns(_ => Rows());

            await CreateJob().Run("tenant-1", true, null);

            // One bulk round-trip carrying every archive, never one probe per archive.
            await _repository.Received(1).GetArchiveStatsAsync(
                Arg.Is<IReadOnlyList<OctoObjectId>>(ids => ids.Count == 3
                    && ids.Any(i => i.ToString() == ActivatedRtId)
                    && ids.Any(i => i.ToString() == SeededDisabledRtId)
                    && ids.Any(i => i.ToString() == CreatedRtId)),
                Arg.Any<CancellationToken>());
            _repository.Received(1).ExportRowsAsync(Arg.Is<OctoObjectId>(o => o.ToString() == ActivatedRtId), null,
                Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_IncludeArchiveData_TableProbeFails_FailsTheJobAndLeavesNoArtifacts()
    {
        // A backup must never silently omit archive data: if the storage probe fails, the job fails
        // and neither the half-written .octobak nor the intermediate mongo blob survive.
        var tempDir = Path.Combine(Path.GetTempPath(), $"octobak-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            SetupCommon(tempDir);
            SetupTenantWithArchives(Snapshot(ActivatedRtId, CkArchiveStatus.Activated, "voltage-raw"));
            _repository.GetArchiveStatsAsync(Arg.Any<IReadOnlyList<OctoObjectId>>(), Arg.Any<CancellationToken>())
                .Returns<IReadOnlyDictionary<OctoObjectId, ArchiveStorageStats>>(
                    _ => throw new InvalidOperationException("CrateDB unreachable"));

            await Assert.That(async () => await CreateJob().Run("tenant-1", true, null))
                .Throws<InvalidOperationException>();

            _repository.DidNotReceive().ExportRowsAsync(Arg.Any<OctoObjectId>(), Arg.Any<TimeWindow?>(),
                Arg.Any<CancellationToken>());
            await _backupFileStorage.Received(1).DeleteFileAsync(Arg.Is<string>(p => p.EndsWith(".octobak.zip")));
            await _backupFileStorage.Received(1).DeleteFileAsync(Path.Combine(tempDir, "tenant-1-mongo.tar.gz"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
