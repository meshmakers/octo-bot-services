using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Staging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     AB#6969 / AB#6968 — the restore of a format 2 container (read as a stream, archive files copied byte for byte into
///     the staging area and loaded by the data store) and the rollup reconcile that follows a restore of archive data.
/// </summary>
public class RestoreRepositoryNativeTests : ArtifactStoreJobTestBase
{
    private const string RtIdA = "665f00000000000000000e41";
    private const string RtIdB = "665f00000000000000000e42";
    private const string RollupId = "665f00000000000000000e43";
    private const string Secret = "S3cr3t/CANARY+key";

    private readonly CapturingLogger<RestoreRepositoryJob> _log = new();
    private readonly ITenantContext _tenantContext;
    private readonly IStreamDataRepository _repository = Substitute.For<IStreamDataRepository>();
    private readonly IArchiveRuntimeStore _archiveStore = Substitute.For<IArchiveRuntimeStore>();
    private readonly IArchiveLifecycleService _lifecycle = Substitute.For<IArchiveLifecycleService>();
    private readonly IRollupArchiveRuntimeStore _rollupStore = Substitute.For<IRollupArchiveRuntimeStore>();
    private readonly IArchiveRecomputeStateStore _stateStore = Substitute.For<IArchiveRecomputeStateStore>();
    private readonly IArchiveCopyStagingLocationFactory _factory = Substitute.For<IArchiveCopyStagingLocationFactory>();
    private readonly IStreamDataRepositoryFactory _streamFactory = Substitute.For<IStreamDataRepositoryFactory>();
    private readonly List<ArchiveCopyPlan> _plans = [];
    private readonly List<string> _stagedAtLoad = [];
    private string? _runId;

    public RestoreRepositoryNativeTests()
    {
        _tenantContext = _systemContext.FindTenantContextAsync(Tenant).Result!;
        _tenantContext.GetStreamDataRepository().Returns(_repository);
        _tenantContext.GetArchiveLifecycleService().Returns(_lifecycle);
        _tenantContext.GetArchiveRuntimeStore().Returns(_archiveStore);
        _tenantContext.GetRollupArchiveRuntimeStore().Returns(_rollupStore);
        _tenantContext.GetArchiveRecomputeStateStore().Returns(_stateStore);
        _rollupStore.EnumerateAsync().Returns(_ => NoRollups());

        _streamFactory.CheckStagingCopyReadinessAsync(Arg.Any<CancellationToken>())
            .Returns(new StagingCopyReadiness(true, true, "6.4.6", null));
        _factory.IsSupported.Returns(true);
        _factory.Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ArchiveCopyStagingAccess>(), Arg.Any<string?>())
            .Returns(ci =>
            {
                _runId = ci.ArgAt<string>(1);
                return new ArchiveCopyStagingLocation(
                    $"s3://AKIACANARY:{Uri.EscapeDataString(Secret)}@minio.octo-infra:9000/bucket/test/copy-staging/{ci.ArgAt<string>(0)}/{_runId}",
                    new Dictionary<string, string> { ["protocol"] = "http" });
            });

        _repository.SupportsStagingCopy.Returns(true);
        _repository.GetStorageVersionAsync(Arg.Any<CancellationToken>()).Returns("6.4.6");
        _repository.LoadArchiveFromStagingAsync(Arg.Any<OctoObjectId>(), Arg.Any<ArchiveCopyPlan>(),
                Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => LoadAsync(ci.ArgAt<OctoObjectId>(0), ci.ArgAt<ArchiveCopyPlan>(1)));
        _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>())
            .Returns(new RollupConsistencyReport([], DateTime.UtcNow));
        _files.GetTusUploadFilePath(Tenant, "file-1").Returns(Path.Combine(_env.Directory, "no-local-upload"));
        _files.GetTusUploadFilePath(Arg.Any<string>(), "file-1")
            .Returns(Path.Combine(_env.Directory, "no-local-upload"));
    }

    private static async IAsyncEnumerable<RollupArchiveSnapshot> NoRollups()
    {
        await Task.CompletedTask;
        yield break;
    }

    private RestoreRepositoryJob CreateJob(bool withFactory = true)
    {
        return new RestoreRepositoryJob(_log, _systemContext, _files, null, _env.Storage,
            withFactory ? _factory : null, null, _streamFactory);
    }

    private async Task<ArchiveCopyLoadResult> LoadAsync(OctoObjectId rtId, ArchiveCopyPlan plan)
    {
        _plans.Add(plan);
        await foreach (var staged in _env.Storage.ListStagingAsync(Tenant, _runId!))
        {
            _stagedAtLoad.Add(staged.RelativePath);
        }

        var results = plan.Slices.Select(s =>
            new ArchiveCopySliceResult(s, ArchiveCopySliceStatus.Succeeded, s.RowCount, 0, TimeSpan.FromSeconds(1))).ToList();
        return new ArchiveCopyLoadResult(rtId, results, plan.TotalRows, plan.TotalRows, true);
    }

    private static ArchiveSnapshot Snapshot(string rtId, CkArchiveStatus status) =>
        new(new OctoObjectId(rtId), new RtCkId<CkTypeId>("System-1.0.0/Sensor"), status, "voltage-raw",
            new[] { new CkArchiveColumnSpec("voltage", true, false) });

    private void SetupPostRestore(params (string RtId, CkArchiveStatus Status)[] archives)
    {
        foreach (var (rtId, status) in archives)
        {
            var id = rtId;
            _archiveStore.GetAsync(Arg.Is<OctoObjectId>(o => o.ToString() == id)).Returns(Snapshot(id, status));
        }
    }

    private static BackupManifestArchive NativeEntry(string rtId, CkArchiveStatus status, params (string Key, long Rows)[] slices)
    {
        return new BackupManifestArchive(ArchiveSchemaMapper.ToDto(Snapshot(rtId, status)), status.ToString(),
            slices.Sum(s => s.Rows), null,
            new BackupManifestArchiveCopy(
                [new BackupManifestTable("Data", [new BackupManifestColumn("rtid", "text")])],
                slices.Select(s => new BackupManifestSlice("Data", s.Key, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), s.Rows, s.Rows, s.Rows, 10, null)).ToList(),
                1, null, null));
    }

    /// <summary>Builds a format 2 container exactly like the dump writes it and stages it as upload <c>file-1</c>.</summary>
    private async Task StageContainerAsync(IReadOnlyList<BackupManifestArchive> archives, int formatVersion = 2,
        string sourceTenant = "tenant-1", bool withFiles = true, ArtifactEncryption encryption = ArtifactEncryption.IfConfigured)
    {
        var manifest = new BackupManifest(formatVersion, DateTime.UtcNow, sourceTenant, true, archives,
            new BackupManifestNative("5.9.10", "test", DateTime.UtcNow));
        await _env.Storage.StoreFromWriterAsync(ArtifactCategories.RestoreStaging, Tenant, "file-1", async (target, ct) =>
        {
            using var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
            var mongo = zip.CreateEntry(BackupArchiveContainer.MongoBlobEntry, CompressionLevel.NoCompression);
            await using (var s = mongo.Open())
            {
                await s.WriteAsync(_dump, ct);
            }

            if (withFiles)
            {
                foreach (var archive in archives.Where(a => a.Copy != null))
                {
                    foreach (var slice in archive.Copy!.Slices)
                    {
                        var folder = slice.Table == "GenerationMap" ? "genmap" : "data";
                        var entry = zip.CreateEntry(
                            $"archives/{archive.Schema.RtId}/{folder}/{slice.Key}/part-0.json.gz", CompressionLevel.NoCompression);
                        await using var s = entry.Open();
                        await s.WriteAsync(Encoding.UTF8.GetBytes($"{archive.Schema.RtId}|{slice.Key}"), ct);
                    }
                }
            }

            var manifestEntry = zip.CreateEntry(BackupArchiveContainer.ManifestEntry, CompressionLevel.NoCompression);
            await using var m = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(m, manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
        }, encryption);
    }

    private async Task<int> StagingObjectsAsync(string runId)
    {
        var count = 0;
        await foreach (var _ in _env.Storage.ListStagingAsync(Tenant, runId))
        {
            count++;
        }

        return count;
    }

    private Task<RestoreRepositoryResult?> RunAsync(bool restoreArchiveData = true, string tenant = Tenant)
    {
        return CreateJob().Run(tenant, "tenantdb", "file-1", null, restoreArchiveData, null, null);
    }

    [Test]
    [Arguments("tenant-1")]
    [Arguments("source-tenant")]
    public async Task Run_Format2_RestoresMongo_LoadsEveryArchiveAndReportsRowsAndSeconds_UnderTheSameOrAnotherTenantId(
        string sourceTenant)
    {
        var restores = CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated), (RtIdB, CkArchiveStatus.Disabled));
        await StageContainerAsync(
            [NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 1200), ("s2", 800)),
             NativeEntry(RtIdB, CkArchiveStatus.Disabled, ("s1", 50))], sourceTenant: sourceTenant);

        var result = await RunAsync();

        await Assert.That(restores.Count).IsEqualTo(1);
        await Assert.That(restores[0].Content.SequenceEqual(_dump)).IsTrue();
        await Assert.That(result!.BackupFormatVersion).IsEqualTo(2);
        await Assert.That(result.TenantId).IsEqualTo(Tenant);
        await Assert.That(result.DatabaseName).IsEqualTo("tenantdb");
        await Assert.That(result!.Archives!.Count).IsEqualTo(2);
        await Assert.That(result.Archives.All(a => a.Outcome == "Imported")).IsTrue();
        await Assert.That(result.Archives.Single(a => a.RtId == RtIdA).Rows).IsEqualTo(2000L);
        await Assert.That(result.Archives.Single(a => a.RtId == RtIdB).Rows).IsEqualTo(50L);

        // The plan comes from the manifest; the files were in the staging area when the data store loaded them.
        await Assert.That(_plans.Count).IsEqualTo(2);
        await Assert.That(_plans[0].Slices.Select(s => s.Key).SequenceEqual(new[] { "s1", "s2" })).IsTrue();
        await Assert.That(_stagedAtLoad.Contains($"{RtIdA}/data/s1/part-0.json.gz")).IsTrue();

        // Clean sequence for the activated archive and its status restored; the disabled one stays disabled.
        var idA = new OctoObjectId(RtIdA);
        Received.InOrder(() =>
        {
            _lifecycle.DisableAsync(idA);
            _repository.DeleteArchiveAsync(idA);
            _lifecycle.ActivateAsync(idA);
            _lifecycle.DisableAsync(idA);
            _lifecycle.EnableAsync(idA);
        });
        await _lifecycle.DidNotReceive().EnableAsync(new OctoObjectId(RtIdB));

        await Assert.That(await StagingObjectsAsync(_runId!)).IsEqualTo(0);
        await Assert.That(Directory.Exists(_env.ScratchDirectory) ? Directory.EnumerateFiles(_env.ScratchDirectory).Count() : 0)
            .IsEqualTo(0);
        await Assert.That(await _env.Storage.FindRestoreStagingAsync(Tenant, "file-1")).IsNull();
        await Assert.That(_log.Messages.Any(m => m.Contains("5.9.10") && m.Contains("6.4.6"))).IsTrue();
    }

    [Test]
    public async Task Run_Format2_TheScratchHoldsTheMongoBlobOnly_NeverACopyOfTheContainer()
    {
        long scratchBytes = -1;
        var scratchFiles = 0;
        _systemContext.RestoreTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>())
            .Returns(ci =>
            {
                var files = Directory.EnumerateFiles(_env.ScratchDirectory).ToList();
                scratchFiles = files.Count;
                scratchBytes = files.Sum(f => new FileInfo(f).Length);
                return Task.FromResult(new CommandResult { Success = true });
            });
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);

        await RunAsync();

        await Assert.That(scratchFiles).IsEqualTo(1);
        await Assert.That(scratchBytes).IsEqualTo((long)_dump.Length);
    }

    [Test]
    public async Task Run_Format2_RestoreArchiveDataOff_RestoresMongoOnly_AndStagesNothing()
    {
        var restores = CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);

        var result = await RunAsync(restoreArchiveData: false);

        await Assert.That(restores.Count).IsEqualTo(1);
        await Assert.That(result!.Archives.Count).IsEqualTo(0);
        await Assert.That(_plans.Count).IsEqualTo(0);
        await Assert.That(_runId).IsNull();
        await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task Run_Format2_WithoutAStagingArea_FailsBeforeTouchingTheTenant_NamingTheMissingPiece()
    {
        var restores = CaptureRestores();
        _factory.IsSupported.Returns(false);
        _factory.UnsupportedReason.Returns("The FileSystem artifact store cannot be reached by the CrateDB nodes");
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);

        var error = await Assert.That(async () => await RunAsync()).Throws<JobFailedException>();

        await Assert.That(error!.Message.Contains("staging area")).IsTrue();
        await Assert.That(error.Message.Contains("FileSystem artifact store")).IsTrue();
        await Assert.That(restores.Count).IsEqualTo(0);
        await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task Run_Format2_OnAnInstallationWithoutNativeCopy_FailsBeforeAnyArchiveIsTouched()
    {
        var restores = CaptureRestores();
        _repository.SupportsStagingCopy.Returns(false);
        _repository.CheckStagingCopyReadinessAsync(Arg.Any<CancellationToken>())
            .Returns(new StagingCopyReadiness(false, false, null, "The repository has no native copy."));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);

        var error = await Assert.That(async () => await RunAsync()).Throws<JobFailedException>();

        await Assert.That(error!.Message.Contains("no native copy")).IsTrue();
        await Assert.That(restores.Count).IsEqualTo(1);
        await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task Run_AnUnknownFormatVersion_IsRefused_ListingTheSupportedVersions()
    {
        var restores = CaptureRestores();
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))], formatVersion: 3);

        var error = await Assert.That(async () => await RunAsync()).Throws<JobFailedException>();

        await Assert.That(error!.Message.Contains("format version 3")).IsTrue();
        await Assert.That(error.Message.Contains("1 and 2")).IsTrue();
        await Assert.That(restores.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Run_Format2_AnArchiveThatDoesNotLoad_IsReportedFailed_TheRestAreRestored_AndStagingIsDeleted()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated), (RtIdB, CkArchiveStatus.Activated));
        await StageContainerAsync(
            [NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 100)),
             NativeEntry(RtIdB, CkArchiveStatus.Activated, ("s1", 100))]);
        _repository.LoadArchiveFromStagingAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RtIdA),
                Arg.Any<ArchiveCopyPlan>(), Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var plan = ci.ArgAt<ArchiveCopyPlan>(1);
                return Task.FromResult(new ArchiveCopyLoadResult(plan.ArchiveRtId,
                    [new ArchiveCopySliceResult(plan.Slices[0], ArchiveCopySliceStatus.Failed, 40, 60,
                        TimeSpan.FromSeconds(1), "60 row(s) were rejected")], 100, 40, false));
            });

        var result = await RunAsync();

        var a = result!.Archives.Single(x => x.RtId == RtIdA);
        await Assert.That(a.Outcome).IsEqualTo("Failed");
        await Assert.That(a.Detail).IsEqualTo("60 row(s) were rejected");
        await Assert.That(result.Archives.Single(x => x.RtId == RtIdB).Outcome).IsEqualTo("Imported");
        await Assert.That(await StagingObjectsAsync(_runId!)).IsEqualTo(0);
    }

    [Test]
    public async Task Run_Format2_FilesMissingFromTheContainer_SkipTheArchiveBeforeItsTableIsTouched()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 100))], withFiles: false);

        var result = await RunAsync();

        await Assert.That(result!.Archives.Single().Outcome).IsEqualTo("Skipped");
        await Assert.That(result.Archives.Single().Detail!.Contains("missing")).IsTrue();
        await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task Run_Format2_TheJobLogFilterMissing_StopsAfterTheFirstArchive_AndNamesTheReasonWithoutTheCredential()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated), (RtIdB, CkArchiveStatus.Activated));
        await StageContainerAsync(
            [NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 100)),
             NativeEntry(RtIdB, CkArchiveStatus.Activated, ("s1", 100))]);
        _repository.LoadArchiveFromStagingAsync(Arg.Any<OctoObjectId>(), Arg.Any<ArchiveCopyPlan>(),
                Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ArchiveCopyLoadResult>>(ci => throw new ArchiveCopyRefusedException(
                ArchiveCopyRefusalReason.JobLogNotFiltered,
                $"stats.jobs_log_filter is not set; COPY FROM '{ci.ArgAt<ArchiveCopyStagingLocation>(2).RevealUri()}'"));

        var result = await RunAsync();

        await Assert.That(result!.Archives[0].Outcome).IsEqualTo("Failed");
        await Assert.That(result.Archives[0].Detail!.Contains("jobs_log_filter")).IsTrue();
        await Assert.That(result.Archives[1].Outcome).IsEqualTo("Skipped");
        await Assert.That(result.Archives[1].Detail!.Contains("not attempted")).IsTrue();
        var everything = string.Join('\n', _log.Messages) + JsonSerializer.Serialize(result);
        await Assert.That(everything.Contains(Secret)).IsFalse();
        await Assert.That(everything.Contains(Uri.EscapeDataString(Secret))).IsFalse();
        await Assert.That(everything.Contains("AKIACANARY")).IsFalse();
    }

    [Test]
    public async Task Run_Format2_ATamperedEncryptedUpload_IsDetected_BeforeAnythingIsRestored()
    {
        var restores = CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))],
            encryption: ArtifactEncryption.Required);
        var path = Path.Combine(_env.StoreRoot, _env.Keys.Build(ArtifactCategories.RestoreStaging, Tenant, "file-1.octoenc")
            .Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^3] ^= 0x01; // the last chunk, i.e. the central directory of the container
        await File.WriteAllBytesAsync(path, bytes);

        await Assert.That(async () => await RunAsync()).Throws<InvalidSecretFileException>();

        await Assert.That(restores.Count).IsEqualTo(0);
        await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task Run_Format2_WhenTheClusterIsNotReady_TheMongoRestoreNeverRuns_AndNothingIsTouched()
    {
        var restores = CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);
        _streamFactory.CheckStagingCopyReadinessAsync(Arg.Any<CancellationToken>())
            .Returns(new StagingCopyReadiness(true, false, "6.4.6",
                "stats.jobs_log_filter does not keep COPY out of sys.jobs_log."));
        _systemContext.ClearReceivedCalls();

        var error = await Assert.That(async () => await RunAsync()).Throws<JobFailedException>();

        await Assert.That(error!.Message.Contains("stats.jobs_log_filter")).IsTrue();
        await Assert.That(error.Message.Contains("Nothing was restored")).IsTrue();
        await Assert.That(restores.Count).IsEqualTo(0);
        await _systemContext.DidNotReceive().FindTenantContextAsync(Arg.Any<string>());
        await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task Run_Format2_WhenTheBackupDoesNotFitBelowTheDiskWatermark_TheMongoRestoreNeverRuns_AndTheTenantIsUnchanged()
    {
        var restores = CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);
        const string reason = "Loading the archive data of the backup needs about 30.0 GiB per node (12 times the 5.0 GiB of staged files, " +
                              "2 copies of every shard over 2 node(s)), but node 'data-hot-1' has 8.0 GiB available and room for 2.0 GiB " +
                              "below the high disk watermark (90%).";
        _streamFactory.CheckStagingDiskHeadroomAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new StagingDiskHeadroom(false, 5L << 30, 60L << 30,
                [new NodeDiskHeadroom("data-hot-1", 30L << 30, 8L << 30, 2L << 30, "90%")], reason));
        _systemContext.ClearReceivedCalls();

        var error = await Assert.That(async () => await RunAsync()).Throws<JobFailedException>();

        await Assert.That(error!.Message.Contains("30.0 GiB per node")).IsTrue();
        await Assert.That(error.Message.Contains("node 'data-hot-1' has 8.0 GiB available and room for 2.0 GiB")).IsTrue();
        await Assert.That(error.Message.Contains("high disk watermark (90%)")).IsTrue();
        await Assert.That(error.Message.Contains("Nothing was restored")).IsTrue();
        await Assert.That(restores.Count).IsEqualTo(0);
        await _systemContext.DidNotReceive().FindTenantContextAsync(Arg.Any<string>());
        await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task Run_Format2_WhenTheBackupFits_ItRunsAsBefore_AndTheCheckGetsTheStagedBytesOfAllArchives()
    {
        var restores = CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated), (RtIdB, CkArchiveStatus.Activated));
        await StageContainerAsync([
            NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10)),
            NativeEntry(RtIdB, CkArchiveStatus.Activated, ("s1", 25))]);
        _streamFactory.CheckStagingDiskHeadroomAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new StagingDiskHeadroom(true, 35, 420, [new NodeDiskHeadroom("data-hot-1", 420, 1L << 40, 1L << 40, "90%")], null));

        var result = await RunAsync();

        await Assert.That(restores.Count).IsEqualTo(1);
        await Assert.That(result!.Archives!.Count).IsEqualTo(2);
        await _streamFactory.Received(1).CheckStagingDiskHeadroomAsync(Arg.Is<long>(b => b >= 35), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_Format2_WhenTheJobLogFilterIsMissing_NoTableIsDropped_AndTheReasonIsNamed()
    {
        var restores = CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);
        _repository.CheckStagingCopyReadinessAsync(Arg.Any<CancellationToken>())
            .Returns(new StagingCopyReadiness(true, false, "5.10.10",
                "stats.jobs_log_filter does not keep COPY out of sys.jobs_log."));

        var error = await Assert.That(async () => await RunAsync()).Throws<JobFailedException>();

        await Assert.That(error!.Message.Contains("stats.jobs_log_filter")).IsTrue();
        await Assert.That(restores.Count).IsEqualTo(1);
        await _repository.DidNotReceive().DeleteArchiveAsync(Arg.Any<OctoObjectId>());
        await _lifecycle.DidNotReceive().DisableAsync(Arg.Any<OctoObjectId>());
        await _lifecycle.DidNotReceive().ActivateAsync(Arg.Any<OctoObjectId>());
    }

    // --- the rollup reconcile ---------------------------------------------------------------------------------------------

    [Test]
    public async Task Reconcile_AConsistentRestore_NeedsNoRepair_ReportsOk_AndLeavesTheMarkerClear()
    {
        CaptureRestores();
        SetupPostRestore((RollupId, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RollupId, CkArchiveStatus.Activated, ("s1", 10))]);
        _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>())
            .Returns(new RollupConsistencyReport([new RollupConsistencyEntry(new OctoObjectId(RollupId), [])], DateTime.UtcNow));

        var result = await RunAsync();

        await Assert.That(result!.RollupReconcile!.Ok).IsTrue();
        await Assert.That(result.RollupReconcile.RepairRan).IsFalse();
        await _repository.DidNotReceive().RepairRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _repository.SetRestoreReconcilePendingAsync(true, Arg.Any<CancellationToken>());
            _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>());
            _repository.SetRestoreReconcilePendingAsync(false, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Reconcile_AnInconsistentRestore_IsRepaired_AndTheResultCarriesValidateRepairValidate()
    {
        CaptureRestores();
        SetupPostRestore((RollupId, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RollupId, CkArchiveStatus.Activated, ("s1", 10))]);
        var id = new OctoObjectId(RollupId);
        var finding = new RollupConsistencyFinding(RollupConsistencyCheck.PointerWithoutRows, "pointer without rows");
        var bad = new RollupConsistencyReport([new RollupConsistencyEntry(id, [finding])], DateTime.UtcNow);
        var good = new RollupConsistencyReport([new RollupConsistencyEntry(id, [])], DateTime.UtcNow);
        _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>())
            .Returns(bad);
        _repository.RepairRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), false, Arg.Any<CancellationToken>())
            .Returns(new RollupRepairReport(false, bad,
                [new RollupRepairAction(id, RollupRepairKind.RecomputePointerRange, "recompute", RollupRepairStatus.Done)],
                good, null));

        var result = await RunAsync();

        var reconcile = result!.RollupReconcile!;
        await Assert.That(reconcile.Ok).IsTrue();
        await Assert.That(reconcile.RepairRan).IsTrue();
        var entry = reconcile.Rollups.Single();
        await Assert.That(entry.RollupRtId).IsEqualTo(RollupId);
        await Assert.That(entry.Before.Single()).IsEqualTo("pointer without rows");
        await Assert.That(entry.Repair.Single().Contains("RecomputePointerRange")).IsTrue();
        await Assert.That(entry.After.Count).IsEqualTo(0);
        // The repair clears the marker itself when it ends ok; the job must not clear it before.
        await _repository.DidNotReceive().SetRestoreReconcilePendingAsync(false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reconcile_AFailingRepair_LeavesTheMarkerSet_AndNamesTheFailingStep_WithoutFailingTheRestore()
    {
        CaptureRestores();
        SetupPostRestore((RollupId, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RollupId, CkArchiveStatus.Activated, ("s1", 10))]);
        var id = new OctoObjectId(RollupId);
        var bad = new RollupConsistencyReport(
            [new RollupConsistencyEntry(id, [new RollupConsistencyFinding(RollupConsistencyCheck.OrphansAbovePointer, "orphans")])],
            DateTime.UtcNow);
        _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>())
            .Returns(bad);
        _repository.RepairRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), false, Arg.Any<CancellationToken>())
            .Returns(new RollupRepairReport(false, bad,
                [new RollupRepairAction(id, RollupRepairKind.DeleteOrphans, "delete orphans", RollupRepairStatus.Failed,
                    Error: "CrateDB refused")],
                bad, $"{RollupId}: DeleteOrphans - CrateDB refused"));

        var result = await RunAsync();

        await Assert.That(result!.Archives.Single().Outcome).IsEqualTo("Imported");
        var reconcile = result.RollupReconcile!;
        await Assert.That(reconcile.Ok).IsFalse();
        await Assert.That(reconcile.FailedStep!.Contains("DeleteOrphans")).IsTrue();
        await Assert.That(reconcile.Rollups.Single().Repair.Single().Contains("CrateDB refused")).IsTrue();
        await _repository.DidNotReceive().SetRestoreReconcilePendingAsync(false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_Format2_RestoresTheSourceBeforeTheRollupThatReadsIt_WhateverTheManifestOrder()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated), (RollupId, CkArchiveStatus.Activated));
        // The dump lists the rollup before its source (as the real one does).
        await StageContainerAsync([
            NativeEntry(RollupId, CkArchiveStatus.Activated, ("r1", 10)),
            NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))
        ]);
        var rollup = new RollupArchiveSnapshot(new OctoObjectId(RollupId), new RtCkId<CkTypeId>("System-1.0.0/Sensor"),
            CkArchiveStatus.Activated, "rollup", [new RollupSourceReference(new OctoObjectId(RtIdA))],
            TimeSpan.FromHours(1), TimeSpan.Zero, null, [], null);
        _rollupStore.EnumerateAsync().Returns(_ => One(rollup));

        await RunAsync();

        Received.InOrder(() =>
        {
            _repository.LoadArchiveFromStagingAsync(new OctoObjectId(RtIdA), Arg.Any<ArchiveCopyPlan>(),
                Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>());
            _repository.LoadArchiveFromStagingAsync(new OctoObjectId(RollupId), Arg.Any<ArchiveCopyPlan>(),
                Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Run_Format2_ADisabledSourceOfAnActivatedRollup_StaysActivatedUntilTheRollupIsRestored()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Disabled), (RollupId, CkArchiveStatus.Activated));
        await StageContainerAsync([
            NativeEntry(RollupId, CkArchiveStatus.Activated, ("r1", 10)),
            NativeEntry(RtIdA, CkArchiveStatus.Disabled, ("s1", 10))
        ]);
        var rollup = new RollupArchiveSnapshot(new OctoObjectId(RollupId), new RtCkId<CkTypeId>("System-1.0.0/Sensor"),
            CkArchiveStatus.Activated, "rollup", [new RollupSourceReference(new OctoObjectId(RtIdA))],
            TimeSpan.FromHours(1), TimeSpan.Zero, null, [], null);
        _rollupStore.EnumerateAsync().Returns(_ => One(rollup));
        var source = new OctoObjectId(RtIdA);
        var rollupId = new OctoObjectId(RollupId);

        var result = await RunAsync();

        Received.InOrder(() =>
        {
            _repository.LoadArchiveFromStagingAsync(source, Arg.Any<ArchiveCopyPlan>(),
                Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>());
            _lifecycle.EnableAsync(source);
            _repository.LoadArchiveFromStagingAsync(rollupId, Arg.Any<ArchiveCopyPlan>(),
                Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>());
        });
        // Disabled after the clean-restore provisioning and again at the end, after the rollup was restored.
        await _lifecycle.Received(2).DisableAsync(source);
        await _lifecycle.Received(1).EnableAsync(source);
        await _repository.Received().SetRestoreReconcilePendingAsync(true, Arg.Any<CancellationToken>());
        var summary = result!.Archives!.Single(a => a.RtId == RtIdA);
        await Assert.That(summary.Detail).IsEqualTo("status Disabled");
    }

    [Test]
    public async Task Reconcile_AFailedArchive_KeepsTheMarkerSet_WhateverTheValidatorReports_AndNamesWhatWasNotChecked()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated), (RollupId, CkArchiveStatus.Disabled));
        await StageContainerAsync([
            NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10)),
            NativeEntry(RollupId, CkArchiveStatus.Disabled, ("r1", 10))
        ]);
        _repository.LoadArchiveFromStagingAsync(Arg.Is<OctoObjectId>(o => o.ToString() == RollupId),
                Arg.Any<ArchiveCopyPlan>(), Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<ArchiveCopyLoadResult>>(_ => throw new InvalidOperationException("table is gone"));
        // The validator sees nothing wrong (it skips what it cannot look at).
        _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>())
            .Returns(new RollupConsistencyReport([new RollupConsistencyEntry(new OctoObjectId(RollupId), [])], DateTime.UtcNow));

        var result = await RunAsync();

        var reconcile = result!.RollupReconcile!;
        await Assert.That(reconcile.Ok).IsFalse();
        await Assert.That(reconcile.FailedArchives.Single().ArchiveRtId).IsEqualTo(RollupId);
        await Assert.That(reconcile.FailedArchives.Single().Reason.Contains("table is gone")).IsTrue();
        var gap = reconcile.NotCheckedRollups.Single();
        await Assert.That(gap.RollupRtId).IsEqualTo(RollupId);
        await Assert.That(gap.Cause).IsEqualTo("ArchiveFailed");
        await Assert.That(gap.IsBlocking).IsTrue();
        await _repository.Received().SetRestoreReconcilePendingAsync(true, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().SetRestoreReconcilePendingAsync(false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reconcile_AValidatorGap_IsListedWithItsCause_AndABlockingOneKeepsTheMarker()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);
        var gap = new RollupNotChecked(RollupNotCheckedCause.NoLiveTable, "the rollup has no table");
        var report = new RollupConsistencyReport([new RollupConsistencyEntry(new OctoObjectId(RollupId), [], gap)], DateTime.UtcNow);
        _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>())
            .Returns(report);
        _repository.RepairRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), false, Arg.Any<CancellationToken>())
            .Returns(new RollupRepairReport(false, report, [], report, null));

        var result = await RunAsync();

        var reconcile = result!.RollupReconcile!;
        await Assert.That(reconcile.Ok).IsFalse();
        var listed = reconcile.NotCheckedRollups.Single();
        await Assert.That(listed.Cause).IsEqualTo("NoLiveTable");
        await Assert.That(listed.IsBlocking).IsTrue();
    }

    [Test]
    public async Task Reconcile_ANeverActivatedRollup_IsListedButDoesNotBlock()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);
        var gap = new RollupNotChecked(RollupNotCheckedCause.NeverActivated, "never activated");
        _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>())
            .Returns(new RollupConsistencyReport([new RollupConsistencyEntry(new OctoObjectId(RollupId), [], gap)], DateTime.UtcNow));

        var result = await RunAsync();

        var reconcile = result!.RollupReconcile!;
        await Assert.That(reconcile.Ok).IsTrue();
        await Assert.That(reconcile.NotCheckedRollups.Single().IsBlocking).IsFalse();
        await _repository.Received().SetRestoreReconcilePendingAsync(false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reconcile_AStaleRecomputeFlagFromTheBackup_IsResetBeforeTheValidation()
    {
        CaptureRestores();
        SetupPostRestore((RollupId, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RollupId, CkArchiveStatus.Activated, ("s1", 10))]);
        var stale = new RollupArchiveSnapshot(new OctoObjectId(RollupId), new RtCkId<CkTypeId>("System-1.0.0/Sensor"),
            CkArchiveStatus.Activated, "rollup", [], TimeSpan.FromHours(1), TimeSpan.Zero, null, [], null)
        {
            RecomputeInProgress = true
        };
        _rollupStore.EnumerateAsync().Returns(_ => One(stale));

        var result = await RunAsync();

        await Assert.That(result!.RollupReconcile!.ResetRecomputeFlags.Single()).IsEqualTo(RollupId);
        Received.InOrder(() =>
        {
            _stateStore.MarkRecomputeFailedAsync(new OctoObjectId(RollupId), Arg.Any<DateTime>(), Arg.Any<string>());
            _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task Reconcile_ARepositoryWithoutRollupValidation_SkipsTheReconcile()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 10))]);
        _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ArchiveCopyNotSupportedException());

        var result = await RunAsync();

        await Assert.That(result!.RollupReconcile).IsNull();
        await Assert.That(result.Archives.Single().Outcome).IsEqualTo("Imported");
    }

    [Test]
    public async Task Format1_Restore_ReportsRowsAndSeconds_AndRunsTheSameReconcile()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Disabled));
        var snapshot = Snapshot(RtIdA, CkArchiveStatus.Disabled);
        var entry = new BackupManifestArchive(ArchiveSchemaMapper.ToDto(snapshot), "Disabled", 1,
            BackupArchiveContainer.NdjsonEntryFor(RtIdA));
        var path = Path.Combine(_env.Directory, "format1.octobak.zip");
        await using (var fs = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry(BackupArchiveContainer.MongoBlobEntry, CompressionLevel.NoCompression).Open())
            {
                s.Write(_dump);
            }

            using (var s = zip.CreateEntry(entry.NdjsonEntry!, CompressionLevel.Optimal).Open())
            {
                s.Write(Encoding.UTF8.GetBytes("{\"rtid\":\"61a\"}\n"));
            }

            using (var s = zip.CreateEntry(BackupArchiveContainer.ManifestEntry, CompressionLevel.Optimal).Open())
            {
                JsonSerializer.Serialize(s, new BackupManifest(1, DateTime.UtcNow, "src", true, [entry]),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
        }

        _files.GetTusUploadFilePath(Tenant, "file-1").Returns(path);
        _repository.ValidateRollupsAsync(Arg.Any<IReadOnlyCollection<OctoObjectId>?>(), Arg.Any<CancellationToken>())
            .Returns(new RollupConsistencyReport([new RollupConsistencyEntry(new OctoObjectId(RollupId), [])], DateTime.UtcNow));

        var result = await RunAsync();

        await Assert.That(result!.BackupFormatVersion).IsEqualTo(1);
        await Assert.That(result.Archives.Single().Outcome).IsEqualTo("Imported");
        await Assert.That(result.Archives.Single().Seconds).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(result.RollupReconcile!.Ok).IsTrue();
        await _repository.DidNotReceive().LoadArchiveFromStagingAsync(Arg.Any<OctoObjectId>(), Arg.Any<ArchiveCopyPlan>(),
            Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_Format2_ReportsTheDataRowsOfTheArchive_NotTheGenerationMapRows()
    {
        CaptureRestores();
        SetupPostRestore((RollupId, CkArchiveStatus.Disabled));
        var plain = NativeEntry(RollupId, CkArchiveStatus.Disabled, ("s1", 100));
        var entry = plain with
        {
            Copy = plain.Copy! with
            {
                Slices = [.. plain.Copy.Slices, new BackupManifestSlice("GenerationMap", "all",
                    new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                    8, 8, 8, 10, null)]
            }
        };
        await StageContainerAsync([entry]);

        var result = await RunAsync();

        await Assert.That(result!.Archives.Single().Rows).IsEqualTo(100);
    }

    [Test]
    public async Task Format1_Restore_SourceFirst_AndADisabledSourceStaysActivatedUntilItsRollupIsRestored()
    {
        CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Disabled), (RollupId, CkArchiveStatus.Activated));
        BackupManifestArchive Entry(string rtId, CkArchiveStatus status) =>
            new(ArchiveSchemaMapper.ToDto(Snapshot(rtId, status)), status.ToString(), 1,
                BackupArchiveContainer.NdjsonEntryFor(rtId));
        // The rollup is listed before its source, as the dump lists them.
        var entries = new[] { Entry(RollupId, CkArchiveStatus.Activated), Entry(RtIdA, CkArchiveStatus.Disabled) };
        var path = Path.Combine(_env.Directory, "format1-order.octobak.zip");
        await using (var fs = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry(BackupArchiveContainer.MongoBlobEntry, CompressionLevel.NoCompression).Open())
            {
                s.Write(_dump);
            }

            foreach (var e in entries)
            {
                using var s = zip.CreateEntry(e.NdjsonEntry!, CompressionLevel.Optimal).Open();
                s.Write(Encoding.UTF8.GetBytes("{\"rtid\":\"61a\"}\n"));
            }

            using (var s = zip.CreateEntry(BackupArchiveContainer.ManifestEntry, CompressionLevel.Optimal).Open())
            {
                JsonSerializer.Serialize(s, new BackupManifest(1, DateTime.UtcNow, "src", true, entries),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }
        }

        _files.GetTusUploadFilePath(Tenant, "file-1").Returns(path);
        var rollup = new RollupArchiveSnapshot(new OctoObjectId(RollupId), new RtCkId<CkTypeId>("System-1.0.0/Sensor"),
            CkArchiveStatus.Activated, "rollup", [new RollupSourceReference(new OctoObjectId(RtIdA))],
            TimeSpan.FromHours(1), TimeSpan.Zero, null, [], null);
        _rollupStore.EnumerateAsync().Returns(_ => One(rollup));
        var source = new OctoObjectId(RtIdA);
        var rollupId = new OctoObjectId(RollupId);

        var result = await RunAsync();

        Received.InOrder(() =>
        {
            _repository.ImportRowsAsync(source, Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<ArchiveImportMode>(), Arg.Any<CancellationToken>());
            _lifecycle.EnableAsync(source);
            _repository.ImportRowsAsync(rollupId, Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<ArchiveImportMode>(), Arg.Any<CancellationToken>());
        });
        await _lifecycle.Received(2).DisableAsync(source);
        await Assert.That(result!.Archives.Count(a => a.Outcome == "Imported")).IsEqualTo(2);
        await Assert.That(result.Archives.Single(a => a.RtId == RtIdA).Detail).IsEqualTo("status Disabled");
    }

    private static async IAsyncEnumerable<RollupArchiveSnapshot> One(RollupArchiveSnapshot snapshot)
    {
        yield return snapshot;
        await Task.CompletedTask;
    }
}
