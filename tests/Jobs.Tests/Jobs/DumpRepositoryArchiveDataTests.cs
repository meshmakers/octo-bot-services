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

            _tenantContext.GetStreamDataRepository().Returns(_repository);
            _tenantContext.GetArchiveRuntimeStore().Returns(_archiveStore);
            _archiveStore.EnumerateAsync().Returns(_ => Snapshots(activated, created));
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
}
