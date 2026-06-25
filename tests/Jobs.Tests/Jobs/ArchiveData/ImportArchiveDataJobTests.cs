using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ArchiveImportMode = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ArchiveImportMode;
using EngineArchiveImportMode = Meshmakers.Octo.Runtime.Contracts.StreamData.ArchiveImportMode;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.ArchiveData;

public class ImportArchiveDataJobTests
{
    private const string ArchiveRtId = "665f00000000000000000e21";

    private readonly ILogger<ImportArchiveDataJob> _logger = Substitute.For<ILogger<ImportArchiveDataJob>>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly IStreamDataRepository _repository = Substitute.For<IStreamDataRepository>();
    private readonly IArchiveRuntimeStore _archiveStore = Substitute.For<IArchiveRuntimeStore>();
    private readonly IRollupArchiveLifecycleService _rollupLifecycle =
        Substitute.For<IRollupArchiveLifecycleService>();

    private ImportArchiveDataJob CreateJob(ArchiveSnapshot? snapshot)
    {
        _systemContext.FindTenantContextAsync("tenant-1").Returns(_tenantContext);
        _tenantContext.GetStreamDataRepository().Returns(_repository);
        _tenantContext.GetArchiveRuntimeStore().Returns(_archiveStore);
        _tenantContext.GetRollupArchiveLifecycleService().Returns(_rollupLifecycle);
        _archiveStore.GetAsync(Arg.Any<OctoObjectId>()).Returns(snapshot);
        return new ImportArchiveDataJob(_logger, _systemContext, _backupFileStorage);
    }

    private static ArchiveSnapshot Snapshot(
        IReadOnlyList<CkRollupAggregationSpec>? rollups = null,
        IReadOnlyList<CkArchiveColumnSpec>? columns = null,
        CkArchiveStatus status = CkArchiveStatus.Disabled)
    {
        return new ArchiveSnapshot(
            new OctoObjectId(ArchiveRtId),
            new RtCkId<CkTypeId>("System-1.0.0/Sensor"),
            status,
            "voltage-raw",
            columns ?? new[] { new CkArchiveColumnSpec("voltage", true, false) })
        {
            RollupAggregations = rollups
        };
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
        var snapshot = Snapshot();
        var path = WriteZip(Metadata(ArchiveSchemaMapper.ToDto(snapshot)), "{\"rtid\":\"61a\"}\n");
        try
        {
            var job = CreateJob(snapshot);

            await job.Run("tenant-1", ArchiveRtId, path, ArchiveImportMode.InsertOnly, null);

            await _repository.Received(1).ImportRowsAsync(Arg.Any<OctoObjectId>(),
                Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                EngineArchiveImportMode.InsertOnly, Arg.Any<CancellationToken>());
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
        var snapshot = Snapshot();
        // Export schema declares a column the target lacks (same CK type, so the column diff surfaces).
        var sourceSchema = ArchiveSchemaMapper.ToDto(snapshot) with
        {
            Columns = new[]
            {
                new ArchiveColumnDto("voltage", true, false),
                new ArchiveColumnDto("phase", false, false)
            }
        };
        var path = WriteZip(Metadata(sourceSchema), "{}\n");
        try
        {
            var job = CreateJob(snapshot);

            JobFailedException? captured = null;
            try
            {
                await job.Run("tenant-1", ArchiveRtId, path, ArchiveImportMode.InsertOnly, null);
            }
            catch (JobFailedException e)
            {
                captured = e;
            }

            await Assert.That(captured).IsNotNull();
            await Assert.That(captured!.Message).Contains("phase");
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

    [Test]
    public async Task Run_ArchiveNotDisabled_FailsWithoutImportingAndDeletesFile()
    {
        // §7.1: import must be rejected unless the target archive is Disabled.
        var snapshot = Snapshot(status: CkArchiveStatus.Activated);
        var path = WriteZip(Metadata(ArchiveSchemaMapper.ToDto(snapshot)), "{\"rtid\":\"61a\"}\n");
        try
        {
            var job = CreateJob(snapshot);

            JobFailedException? captured = null;
            try
            {
                await job.Run("tenant-1", ArchiveRtId, path, ArchiveImportMode.InsertOnly, null);
            }
            catch (JobFailedException e)
            {
                captured = e;
            }

            await Assert.That(captured).IsNotNull();
            await Assert.That(captured!.Message).Contains("must be Disabled");
            await Assert.That(captured!.Message).Contains("Activated");
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

    [Test]
    public async Task Run_UnsupportedFormatVersion_Fails()
    {
        var snapshot = Snapshot();
        var path = WriteZip(Metadata(ArchiveSchemaMapper.ToDto(snapshot), formatVersion: 99), "{}\n");
        try
        {
            var job = CreateJob(snapshot);

            await Assert.That(async () =>
                    await job.Run("tenant-1", ArchiveRtId, path, ArchiveImportMode.InsertOnly, null))
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
        var job = CreateJob(Snapshot());

        await Assert.That(async () =>
                await job.Run("tenant-1", ArchiveRtId, "/nonexistent/file.zip", ArchiveImportMode.InsertOnly, null))
            .Throws<JobFailedException>();

        await _backupFileStorage.Received(1).DeleteFileAsync("/nonexistent/file.zip");
    }

    [Test]
    public async Task Run_ArchiveNotFound_Fails()
    {
        var path = WriteZip(Metadata(ArchiveSchemaMapper.ToDto(Snapshot())), "{}\n");
        try
        {
            var job = CreateJob(snapshot: null);

            await Assert.That(async () =>
                    await job.Run("tenant-1", ArchiveRtId, path, ArchiveImportMode.InsertOnly, null))
                .Throws<JobFailedException>();
            await _backupFileStorage.Received(1).DeleteFileAsync(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_RollupImport_FreezesImportedWindow()
    {
        var rollups = new[] { new CkRollupAggregationSpec("voltage", CkRollupFunction.Avg, "voltage_avg") };
        var snapshot = Snapshot(rollups);
        var window = new ArchiveExportWindow(
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var path = WriteZip(Metadata(ArchiveSchemaMapper.ToDto(snapshot), window), "{}\n");
        try
        {
            var job = CreateJob(snapshot);

            await job.Run("tenant-1", ArchiveRtId, path, ArchiveImportMode.Upsert, null);

            await _rollupLifecycle.Received(1).FreezeAsync(
                Arg.Is<OctoObjectId>(id => id.ToString() == ArchiveRtId), window.ToUtc);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public async Task Run_RawImport_DoesNotFreeze()
    {
        var snapshot = Snapshot();
        var path = WriteZip(Metadata(ArchiveSchemaMapper.ToDto(snapshot)), "{}\n");
        try
        {
            var job = CreateJob(snapshot);

            await job.Run("tenant-1", ArchiveRtId, path, ArchiveImportMode.InsertOnly, null);

            await _rollupLifecycle.DidNotReceive().FreezeAsync(Arg.Any<OctoObjectId>(), Arg.Any<DateTime>());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
