using System.IO.Compression;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.ArchiveData;

public class ExportArchiveDataJobTests
{
    private const string ArchiveRtId = "665f00000000000000000e21";

    private readonly ILogger<ExportArchiveDataJob> _logger = Substitute.For<ILogger<ExportArchiveDataJob>>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly IStreamDataRepository _repository = Substitute.For<IStreamDataRepository>();
    private readonly IArchiveRuntimeStore _archiveStore = Substitute.For<IArchiveRuntimeStore>();

    private ExportArchiveDataJob CreateJob(ArchiveSnapshot? snapshot)
    {
        _systemContext.FindTenantContextAsync("tenant-1").Returns(_tenantContext);
        _tenantContext.GetStreamDataRepository().Returns(_repository);
        _tenantContext.GetArchiveRuntimeStore().Returns(_archiveStore);
        _archiveStore.GetAsync(Arg.Any<OctoObjectId>()).Returns(snapshot);
        return new ExportArchiveDataJob(_logger, _systemContext, _backupFileStorage);
    }

    private static ArchiveSnapshot Snapshot()
    {
        return new ArchiveSnapshot(
            new OctoObjectId(ArchiveRtId),
            new RtCkId<CkTypeId>("System-1.0.0/Sensor"),
            CkArchiveStatus.Activated,
            "voltage-raw",
            new[] { new CkArchiveColumnSpec("voltage", true, false) });
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
    public async Task Run_WritesZipWithMetadataAndData()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"export-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
                .Returns(_ => Rows(
                    new Dictionary<string, object?> { ["rtid"] = "61a", ["voltage"] = 230.1 },
                    new Dictionary<string, object?> { ["rtid"] = "61b", ["voltage"] = 229.8 }));

            _backupFileStorage.GetDumpFilePath("tenant-1", Arg.Any<string>())
                .Returns(ci => Path.Combine(tempDir, (string)ci[1]));

            var job = CreateJob(Snapshot());

            var resultPath = await job.Run("tenant-1", ArchiveRtId, null, null, null);

            await Assert.That(resultPath).IsNotNull();
            await Assert.That(File.Exists(resultPath!)).IsTrue();

            using var zip = ZipFile.OpenRead(resultPath!);
            var metadataEntry = zip.GetEntry("metadata.json");
            var dataEntry = zip.GetEntry("data.ndjson");
            await Assert.That(metadataEntry).IsNotNull();
            await Assert.That(dataEntry).IsNotNull();

            await using var metaStream = metadataEntry!.Open();
            var metadata = await JsonSerializer.DeserializeAsync<ArchiveExportMetadata>(metaStream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await Assert.That(metadata!.FormatVersion).IsEqualTo(1);
            await Assert.That(metadata.SourceTenantId).IsEqualTo("tenant-1");
            await Assert.That(metadata.Archive.Kind).IsEqualTo("raw");
            await Assert.That(metadata.Archive.TargetCkTypeId).Contains("Sensor");
            await Assert.That(metadata.Window).IsNull();

            await using var dataStream = dataEntry!.Open();
            using var reader = new StreamReader(dataStream);
            var data = await reader.ReadToEndAsync();
            var lines = data.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await Assert.That(lines.Length).IsEqualTo(2);

            var first = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(lines[0]);
            await Assert.That(first!["rtid"].GetString()).IsEqualTo("61a");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_WithWindow_RecordsWindowInMetadataAndScopesExport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"export-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

            _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), Arg.Any<TimeWindow?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Rows());
            _backupFileStorage.GetDumpFilePath("tenant-1", Arg.Any<string>())
                .Returns(ci => Path.Combine(tempDir, (string)ci[1]));

            var job = CreateJob(Snapshot());

            var resultPath = await job.Run("tenant-1", ArchiveRtId, from, to, null);

            using var zip = ZipFile.OpenRead(resultPath!);
            await using var metaStream = zip.GetEntry("metadata.json")!.Open();
            var metadata = await JsonSerializer.DeserializeAsync<ArchiveExportMetadata>(metaStream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await Assert.That(metadata!.Window).IsNotNull();
            await Assert.That(metadata.Window!.FromUtc).IsEqualTo(from);
            await Assert.That(metadata.Window.ToUtc).IsEqualTo(to);

            // The half-open [from, to) window must be passed through to the repository.
            _repository.Received(1).ExportRowsAsync(Arg.Any<OctoObjectId>(),
                Arg.Is<TimeWindow?>(w => w != null && w.FromUtc == from && w.ToUtc == to),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_ArchiveNotFound_Throws()
    {
        var job = CreateJob(snapshot: null);

        await Assert.That(async () => await job.Run("tenant-1", ArchiveRtId, null, null, null))
            .Throws<JobFailedException>();
    }

    [Test]
    public async Task Run_StreamDataNotEnabled_Throws()
    {
        _systemContext.FindTenantContextAsync("tenant-1").Returns(_tenantContext);
        _tenantContext.GetStreamDataRepository().Returns((IStreamDataRepository?)null);

        var job = new ExportArchiveDataJob(_logger, _systemContext, _backupFileStorage);

        await Assert.That(async () => await job.Run("tenant-1", ArchiveRtId, null, null, null))
            .Throws<JobFailedException>();
    }
}
