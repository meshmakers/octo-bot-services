using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.ServiceClient.AssetRepositoryServices.StreamData;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.ArchiveData;

public class ImportArchiveDataJobTests
{
    private readonly ILogger<ImportArchiveDataJob> _logger = Substitute.For<ILogger<ImportArchiveDataJob>>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();
    private readonly IArchiveDataClientFactory _clientFactory = Substitute.For<IArchiveDataClientFactory>();
    private readonly IStreamDataServicesClient _client = Substitute.For<IStreamDataServicesClient>();

    private ImportArchiveDataJob CreateJob()
    {
        _clientFactory.Create(Arg.Any<string>(), Arg.Any<string>()).Returns(_client);
        return new ImportArchiveDataJob(_logger, _backupFileStorage, _clientFactory);
    }

    private static ArchiveSchemaDto Schema(string kind = "raw",
        IReadOnlyList<ArchiveRollupAggregationDto>? rollups = null)
    {
        return new ArchiveSchemaDto(
            RtId: "665f00000000000000000e21",
            RtWellKnownName: "voltage-raw",
            Kind: kind,
            TargetCkTypeId: "Sensor",
            Columns: new[] { new ArchiveColumnDto("voltage", true, false) },
            RollupAggregations: rollups,
            PeriodMs: null);
    }

    private static string WriteZip(ArchiveExportMetadata? metadata, string? data, bool includeData = true,
        bool includeMetadata = true)
    {
        var path = Path.GetTempFileName();
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            if (includeMetadata)
            {
                var entry = zip.CreateEntry("metadata.json");
                using var s = entry.Open();
                JsonSerializer.Serialize(s, metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            if (includeData)
            {
                var entry = zip.CreateEntry("data.ndjson");
                using var s = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(data ?? "");
                s.Write(bytes, 0, bytes.Length);
            }
        }

        return path;
    }

    private static ArchiveExportMetadata Metadata(ArchiveSchemaDto schema, ArchiveExportWindow? window = null,
        int formatVersion = 1)
    {
        return new ArchiveExportMetadata(formatVersion, DateTime.UtcNow, "source-tenant", schema, window, null);
    }

    [Test]
    public async Task Run_MatchingSchema_StreamsImportAndDeletesFile()
    {
        var path = WriteZip(Metadata(Schema()), "{\"rtid\":\"61a\"}\n");
        try
        {
            _client.GetArchiveSchemaAsync("tenant-1", "arch-1", Arg.Any<CancellationToken>()).Returns(Schema());

            var job = CreateJob();

            await job.Run("tenant-1", "arch-1", path, "tok", ArchiveImportMode.InsertOnly, null);

            await _client.Received(1).ImportArchiveRowsAsync("tenant-1", "arch-1", Arg.Any<Stream>(),
                ArchiveImportMode.InsertOnly, Arg.Any<CancellationToken>());
            await _backupFileStorage.Received(1).DeleteFileAsync(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_SchemaMismatch_FailsWithFieldLevelMessageAndDeletesFile()
    {
        // Export schema has a column the target lacks.
        var sourceSchema = new ArchiveSchemaDto("id", "voltage-raw", "raw", "Sensor",
            new[] { new ArchiveColumnDto("voltage", true, false), new ArchiveColumnDto("phase", false, false) },
            null, null);
        var path = WriteZip(Metadata(sourceSchema), "{}\n");
        try
        {
            _client.GetArchiveSchemaAsync("tenant-1", "arch-1", Arg.Any<CancellationToken>()).Returns(Schema());

            var job = CreateJob();

            JobFailedException? captured = null;
            try
            {
                await job.Run("tenant-1", "arch-1", path, "tok", ArchiveImportMode.InsertOnly, null);
            }
            catch (JobFailedException e)
            {
                captured = e;
            }

            await Assert.That(captured).IsNotNull();
            await Assert.That(captured!.Message).Contains("phase");
            await _client.DidNotReceive().ImportArchiveRowsAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Stream>(), Arg.Any<ArchiveImportMode>(), Arg.Any<CancellationToken>());
            await _backupFileStorage.Received(1).DeleteFileAsync(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_UnsupportedFormatVersion_Fails()
    {
        var path = WriteZip(Metadata(Schema(), formatVersion: 99), "{}\n");
        try
        {
            _client.GetArchiveSchemaAsync("tenant-1", "arch-1", Arg.Any<CancellationToken>()).Returns(Schema());

            var job = CreateJob();

            await Assert.That(async () =>
                    await job.Run("tenant-1", "arch-1", path, "tok", ArchiveImportMode.InsertOnly, null))
                .Throws<JobFailedException>();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_MissingFile_Fails()
    {
        var job = CreateJob();

        await Assert.That(async () =>
                await job.Run("tenant-1", "arch-1", "/nonexistent/file.zip", "tok", ArchiveImportMode.InsertOnly, null))
            .Throws<JobFailedException>();

        await _backupFileStorage.Received(1).DeleteFileAsync("/nonexistent/file.zip");
    }

    [Test]
    public async Task Run_RollupImport_FreezesImportedWindow()
    {
        var rollups = new[] { new ArchiveRollupAggregationDto("voltage", "avg", "voltage_avg") };
        var window = new ArchiveExportWindow(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var path = WriteZip(Metadata(Schema("rollup", rollups), window), "{}\n");
        try
        {
            _client.GetArchiveSchemaAsync("tenant-1", "arch-1", Arg.Any<CancellationToken>())
                .Returns(Schema("rollup", rollups));

            var job = CreateJob();

            await job.Run("tenant-1", "arch-1", path, "tok", ArchiveImportMode.Upsert, null);

            await _client.Received(1).FreezeRollupArchiveAsync("tenant-1", "arch-1", window.ToUtc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_RawImport_DoesNotFreeze()
    {
        var path = WriteZip(Metadata(Schema()), "{}\n");
        try
        {
            _client.GetArchiveSchemaAsync("tenant-1", "arch-1", Arg.Any<CancellationToken>()).Returns(Schema());

            var job = CreateJob();

            await job.Run("tenant-1", "arch-1", path, "tok", ArchiveImportMode.InsertOnly, null);

            await _client.DidNotReceive().FreezeRollupArchiveAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<DateTime>());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
