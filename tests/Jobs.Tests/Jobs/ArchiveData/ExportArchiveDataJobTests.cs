using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.ArchiveData;

public class ExportArchiveDataJobTests
{
    private readonly ILogger<ExportArchiveDataJob> _logger = Substitute.For<ILogger<ExportArchiveDataJob>>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();
    private readonly IArchiveDataClientFactory _clientFactory = Substitute.For<IArchiveDataClientFactory>();
    private readonly IStreamDataServicesClient _client = Substitute.For<IStreamDataServicesClient>();

    private ExportArchiveDataJob CreateJob()
    {
        _clientFactory.Create(Arg.Any<string>(), Arg.Any<string>()).Returns(_client);
        return new ExportArchiveDataJob(_logger, _backupFileStorage, _clientFactory);
    }

    private static ArchiveSchemaDto Schema()
    {
        return new ArchiveSchemaDto(
            RtId: "665f00000000000000000e21",
            RtWellKnownName: "voltage-raw",
            Kind: "raw",
            TargetCkTypeId: "Sensor",
            Columns: new[] { new ArchiveColumnDto("voltage", true, false) },
            RollupAggregations: null,
            PeriodMs: null);
    }

    [Test]
    public async Task Run_WritesZipWithMetadataAndData()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"export-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            _client.GetArchiveSchemaAsync("tenant-1", "arch-1", Arg.Any<CancellationToken>()).Returns(Schema());

            const string ndjson = "{\"rtid\":\"61a\",\"voltage\":230.1}\n{\"rtid\":\"61b\",\"voltage\":229.8}\n";
            _client.ExportArchiveRowsAsync("tenant-1", "arch-1", null, null, Arg.Any<CancellationToken>())
                .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(ndjson)));

            _backupFileStorage.GetDumpFilePath("tenant-1", Arg.Any<string>())
                .Returns(ci => Path.Combine(tempDir, (string)ci[1]));

            var job = CreateJob();

            var resultPath = await job.Run("tenant-1", "arch-1", "tok", null, null, null);

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
            await Assert.That(metadata.Archive.TargetCkTypeId).IsEqualTo("Sensor");
            await Assert.That(metadata.Window).IsNull();

            await using var dataStream = dataEntry!.Open();
            using var reader = new StreamReader(dataStream);
            var data = await reader.ReadToEndAsync();
            await Assert.That(data).IsEqualTo(ndjson);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_WithWindow_RecordsWindowInMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"export-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var from = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

            _client.GetArchiveSchemaAsync("tenant-1", "arch-1", Arg.Any<CancellationToken>()).Returns(Schema());
            _client.ExportArchiveRowsAsync("tenant-1", "arch-1", from, to, Arg.Any<CancellationToken>())
                .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("")));
            _backupFileStorage.GetDumpFilePath("tenant-1", Arg.Any<string>())
                .Returns(ci => Path.Combine(tempDir, (string)ci[1]));

            var job = CreateJob();

            var resultPath = await job.Run("tenant-1", "arch-1", "tok", from, to, null);

            using var zip = ZipFile.OpenRead(resultPath!);
            await using var metaStream = zip.GetEntry("metadata.json")!.Open();
            var metadata = await JsonSerializer.DeserializeAsync<ArchiveExportMetadata>(metaStream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await Assert.That(metadata!.Window).IsNotNull();
            await Assert.That(metadata.Window!.FromUtc).IsEqualTo(from);
            await Assert.That(metadata.Window.ToUtc).IsEqualTo(to);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_SchemaFetchFails_Throws()
    {
        _client.GetArchiveSchemaAsync("tenant-1", "arch-1", Arg.Any<CancellationToken>())
            .Returns<Task<ArchiveSchemaDto>>(_ => throw new InvalidOperationException("boom"));

        var job = CreateJob();

        await Assert.That(async () => await job.Run("tenant-1", "arch-1", "tok", null, null, null))
            .Throws<InvalidOperationException>();
    }
}
