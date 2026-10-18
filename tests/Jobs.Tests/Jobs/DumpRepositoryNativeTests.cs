using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs;
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
///     AB#6969 — the format 2 dump: the archives are copied natively into the staging area and packed byte for byte into
///     one container that is streamed into the artifact store. The staging area is the real file system store; CrateDB is
///     replaced by a repository that writes the files a <c>COPY TO</c> would write.
/// </summary>
public class DumpRepositoryNativeTests : ArtifactStoreJobTestBase
{
    private const string RawRtId = "665f00000000000000000e31";
    private const string RollupRtId = "665f00000000000000000e32";
    private const string EmptyRtId = "665f00000000000000000e33";
    private const string Secret = "S3cr3t/CANARY+key";

    private readonly CapturingLogger<DumpRepositoryJob> _log = new();
    private readonly IStreamDataRepository _repository = Substitute.For<IStreamDataRepository>();
    private readonly IArchiveRuntimeStore _archiveStore = Substitute.For<IArchiveRuntimeStore>();
    private readonly IArchiveCopyStagingLocationFactory _factory = Substitute.For<IArchiveCopyStagingLocationFactory>();
    private readonly ManualClock _clock = new();
    private readonly Dictionary<string, byte[]> _staged = new();
    private string? _runId;

    public DumpRepositoryNativeTests()
    {
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
        _repository.GetStorageVersionAsync(Arg.Any<CancellationToken>()).Returns("5.9.10");
        _repository.DumpArchiveToStagingAsync(Arg.Any<OctoObjectId>(), Arg.Any<ArchiveCopyStagingLocation>(),
                Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => DumpAsync(ci.ArgAt<OctoObjectId>(0), ci.ArgAt<CancellationToken>(3)));
        _files.GenerateDumpFileName(Tenant).Returns("tenant-1-mongo.tar.gz");
        _files.GetDumpFilePath(Tenant, Arg.Any<string>())
            .Returns(ci => Path.Combine(_env.Directory, "dumps", (string)ci[1]));
    }

    private ITenantContext TenantContext => _systemContext.FindTenantContextAsync(Tenant).Result!;

    private DumpRepositoryJob CreateJob(IBotArtifactStorage? storage = null, bool withFactory = true)
    {
        return new DumpRepositoryJob(_log, _systemContext, _files, storage ?? _env.Storage,
            withFactory ? _factory : null, _clock);
    }

    private static ArchiveSnapshot Snapshot(string rtId, string name) =>
        new(new OctoObjectId(rtId), new RtCkId<CkTypeId>("System-1.0.0/Sensor"), CkArchiveStatus.Activated, name,
            new[] { new CkArchiveColumnSpec("voltage", true, false) });

    private void SetupArchives(params ArchiveSnapshot[] snapshots)
    {
        TenantContext.GetStreamDataRepository().Returns(_repository);
        TenantContext.GetArchiveRuntimeStore().Returns(_archiveStore);
        _archiveStore.EnumerateAsync().Returns(_ => ToAsync(snapshots));
        IReadOnlyDictionary<OctoObjectId, ArchiveStorageStats> stats = snapshots.ToDictionary(s => s.RtId,
            s => new ArchiveStorageStats(s.RtId, s.RtId.ToString() != EmptyRtId, RecordCount: 0, SizeBytes: 0,
                Health: ArchiveStorageHealth.Good));
        _repository.GetArchiveStatsAsync(Arg.Any<IReadOnlyList<OctoObjectId>>(), Arg.Any<CancellationToken>())
            .Returns(stats);
    }

    private static async IAsyncEnumerable<ArchiveSnapshot> ToAsync(IEnumerable<ArchiveSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            yield return snapshot;
            await Task.CompletedTask;
        }
    }

    /// <summary>What a <c>COPY TO</c> of the archive would leave in the staging area, and the engine's result for it.</summary>
    private async Task<ArchiveCopyDumpResult> DumpAsync(OctoObjectId rtId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var isRollup = rtId.ToString() == RollupRtId;
        var mapReadAt = isRollup ? _clock.Advance(TimeSpan.FromSeconds(10)) : (DateTime?)null;
        var slices = new List<ArchiveCopySlice>
        {
            ArchiveCopySlice.ForTimeRange(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 1200),
            ArchiveCopySlice.ForTimeRange(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), 800)
        };
        if (isRollup)
        {
            slices.Add(ArchiveCopySlice.ForGenerationMap(5));
        }

        var results = new List<ArchiveCopySliceResult>();
        foreach (var slice in slices)
        {
            var path = $"{slice.GetStagingPath(rtId)}/part-0.json.gz";
            var content = Encoding.UTF8.GetBytes($"{rtId}|{slice.Key}|{new string('x', 100 + slice.Key.Length)}");
            await _env.Storage.PutStagingAsync(Tenant, _runId!, path, new MemoryStream(content), ct);
            _staged[path] = content;
            results.Add(new ArchiveCopySliceResult(slice, ArchiveCopySliceStatus.Succeeded, slice.RowCount, 0,
                TimeSpan.FromSeconds(1)));
        }

        var lastData = _clock.Advance(TimeSpan.FromSeconds(30));
        var plan = new ArchiveCopyPlan(rtId,
            [new ArchiveTableDescription(rtId, ArchiveCopyTable.Data, [new ArchiveCopyColumn("rtid", "text")])], slices);
        return new ArchiveCopyDumpResult(rtId, plan, results, isRollup ? 2 : 1, mapReadAt, lastData);
    }

    private async Task<(ZipArchive Zip, BackupManifest Manifest)> OpenResultAsync(string? result)
    {
        await Assert.That(result).IsNotNull();
        await Assert.That(StoredArtifact.TryParseResultReference(result, out var parts)).IsTrue();
        await using var read = await _env.Storage.OpenPlainReadAsync(parts!.Category, parts.TenantId, parts.FileName);
        var buffer = new MemoryStream();
        await read!.CopyToAsync(buffer);
        buffer.Position = 0;
        var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        await using var manifestStream = zip.GetEntry(BackupArchiveContainer.ManifestEntry)!.Open();
        var manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(manifestStream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        return (zip, manifest);
    }

    private async Task<List<string>> StagingRunAsync()
    {
        var list = new List<string>();
        await foreach (var item in _env.Storage.ListStagingAsync(Tenant, _runId ?? "none"))
        {
            list.Add(item.RelativePath);
        }

        return list;
    }

    [Test]
    public async Task Run_WithAStagingArea_ProducesOneFormat2ContainerWithTheStagedFilesAndTheManifest()
    {
        SetupArchives(Snapshot(RawRtId, "raw"), Snapshot(RollupRtId, "rollup"), Snapshot(EmptyRtId, "never-activated"));
        var job = CreateJob();

        var result = await job.Run(Tenant, true, null);

        var (zip, manifest) = await OpenResultAsync(result);
        using (zip)
        {
            await Assert.That(result!.EndsWith(".octobak.zip.octoenc")).IsTrue();
            await Assert.That(manifest.FormatVersion).IsEqualTo(2);
            await Assert.That(manifest.Archives.Count).IsEqualTo(3);
            await Assert.That(manifest.Native!.StorageVersion).IsEqualTo("5.9.10");

            var entryNames = zip.Entries.Select(e => e.FullName).ToList();
            await Assert.That(entryNames.First()).IsEqualTo(BackupArchiveContainer.MongoBlobEntry);
            await Assert.That(entryNames.Last()).IsEqualTo(BackupArchiveContainer.ManifestEntry);
            await using (var mongo = zip.GetEntry(BackupArchiveContainer.MongoBlobEntry)!.Open())
            {
                using var mongoBytes = new MemoryStream();
                await mongo.CopyToAsync(mongoBytes);
                await Assert.That(mongoBytes.ToArray().SequenceEqual(_dump)).IsTrue();
            }

            // Every staged file is an entry with identical bytes; generation maps come before the data slices.
            foreach (var (path, content) in _staged)
            {
                var entry = zip.GetEntry(BackupArchiveContainer.NativeEntryFor(path));
                await Assert.That(entry).IsNotNull();
                await using var stream = entry!.Open();
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes);
                await Assert.That(bytes.ToArray().SequenceEqual(content)).IsTrue();
            }

            var genmapIndex = entryNames.FindIndex(n => n.Contains("/genmap/"));
            var firstDataIndex = entryNames.FindIndex(n => n.Contains("/data/"));
            await Assert.That(genmapIndex).IsLessThan(firstDataIndex);

            var rollup = manifest.Archives.Single(a => a.Schema.RtId == RollupRtId);
            await Assert.That(rollup.NdjsonEntry).IsNull();
            await Assert.That(rollup.Copy!.Attempts).IsEqualTo(2);
            await Assert.That(rollup.Copy.Slices.Count).IsEqualTo(3);
            await Assert.That(rollup.Copy.Slices.Single(s => s.Table == "GenerationMap").RowCount).IsEqualTo(5L);
            await Assert.That(rollup.RowCount).IsEqualTo(2000L);
            await Assert.That(rollup.Copy.Slices.All(s => s.StagedBytes > 0)).IsTrue();
            var empty = manifest.Archives.Single(a => a.Schema.RtId == EmptyRtId);
            await Assert.That(empty.Copy).IsNull();
            await Assert.That(empty.RowCount).IsEqualTo(0L);
        }

        // The run's staging files are gone, and no local copy of the container exists on the scratch disk.
        await Assert.That((await StagingRunAsync()).Count).IsEqualTo(0);
        await Assert.That(Directory.Exists(_env.ScratchDirectory)
            ? Directory.EnumerateFiles(_env.ScratchDirectory).Count() : 0).IsEqualTo(0);
    }

    [Test]
    public async Task Run_TheManifestShowsTheMongoDumpBeforeTheMapReadBeforeTheLastData_WithAFakeClock()
    {
        _systemContext.BackupTenantAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken?>())
            .Returns(ci =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ci.ArgAt<string>(1))!);
                File.WriteAllBytes(ci.ArgAt<string>(1), _dump);
                _clock.Advance(TimeSpan.FromMinutes(1)); // the dump takes a minute
                return Task.FromResult(new CommandResult { Success = true });
            });
        SetupArchives(Snapshot(RollupRtId, "rollup"));

        var (zip, manifest) = await OpenResultAsync(await CreateJob().Run(Tenant, true, null));

        using (zip)
        {
            var copy = manifest.Archives.Single().Copy!;
            await Assert.That(manifest.Native!.MongoDumpFinishedAtUtc).IsLessThan(copy.MapReadAtUtc!.Value);
            await Assert.That(copy.MapReadAtUtc.Value).IsLessThan(copy.LastDataCopiedAtUtc!.Value);
        }
    }

    [Test]
    public async Task Run_TheFinishedContainerIsEncrypted_IncludingItsArchiveEntries()
    {
        SetupArchives(Snapshot(RawRtId, "raw"));

        var result = await CreateJob().Run(Tenant, true, null);

        StoredArtifact.TryParseResultReference(result, out var parts);
        var raw = await _env.ReadStoredAsync(parts!.Category, parts.TenantId, parts.FileName);
        await Assert.That(Encoding.ASCII.GetString(raw, 0, 8)).IsEqualTo(SecretFileFormat.Magic);
        var marker = _staged.Values.First();
        await Assert.That(raw.AsSpan().IndexOf(marker.AsSpan(0, 24)) < 0).IsTrue();
    }

    [Test]
    public async Task Run_WithoutAStagingArea_FallsBackToFormat1_AndTheLogNamesTheReason()
    {
        SetupArchives(Snapshot(RawRtId, "raw"));
        _factory.IsSupported.Returns(false);
        _factory.UnsupportedReason.Returns("The FileSystem artifact store cannot be reached by the CrateDB nodes");
        _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
            .Returns(_ => ToRows());

        var result = await CreateJob().Run(Tenant, true, null);

        var (zip, manifest) = await OpenResultAsync(result);
        using (zip)
        {
            await Assert.That(manifest.FormatVersion).IsEqualTo(1);
            await Assert.That(zip.GetEntry(BackupArchiveContainer.NdjsonEntryFor(RawRtId))).IsNotNull();
        }

        await Assert.That(_log.Messages.Any(m => m.Contains("format 1") && m.Contains("FileSystem artifact store"))).IsTrue();
        await _repository.DidNotReceive().DumpArchiveToStagingAsync(Arg.Any<OctoObjectId>(),
            Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_WhenThePreflightSaysNotReady_FallsBackToFormat1_BeforeAnythingIsCopied()
    {
        SetupArchives(Snapshot(RawRtId, "raw"));
        _repository.CheckStagingCopyReadinessAsync(Arg.Any<CancellationToken>())
            .Returns(new StagingCopyReadiness(true, false, "5.10.10", "stats.jobs_log_filter is not set."));
        _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
            .Returns(_ => ToRows());

        var (zip, manifest) = await OpenResultAsync(await CreateJob().Run(Tenant, true, null));

        using (zip)
        {
            await Assert.That(manifest.FormatVersion).IsEqualTo(1);
        }

        await Assert.That(_log.Messages.Any(m => m.Contains("format 1") && m.Contains("stats.jobs_log_filter"))).IsTrue();
        await _repository.DidNotReceive().DumpArchiveToStagingAsync(Arg.Any<OctoObjectId>(),
            Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_WhenTheRepositoryHasNoNativeCopy_FallsBackToFormat1()
    {
        SetupArchives(Snapshot(RawRtId, "raw"));
        _repository.SupportsStagingCopy.Returns(false);
        _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
            .Returns(_ => ToRows());

        var (zip, manifest) = await OpenResultAsync(await CreateJob().Run(Tenant, true, null));

        using (zip)
        {
            await Assert.That(manifest.FormatVersion).IsEqualTo(1);
        }

        await Assert.That(_log.Messages.Any(m => m.Contains("SupportsStagingCopy"))).IsTrue();
    }

    [Test]
    public async Task Run_WhenTheJobLogIsNotFiltered_FallsBackToFormat1_DeletesTheStagingFiles_AndNamesTheReason()
    {
        SetupArchives(Snapshot(RawRtId, "raw"), Snapshot(RollupRtId, "rollup"));
        _repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
            .Returns(_ => ToRows());
        var calls = 0;
        _repository.DumpArchiveToStagingAsync(Arg.Any<OctoObjectId>(), Arg.Any<ArchiveCopyStagingLocation>(),
                Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (++calls == 2)
                {
                    throw new ArchiveCopyRefusedException(ArchiveCopyRefusalReason.JobLogNotFiltered,
                        "The staging copy needs the cluster setting stats.jobs_log_filter to keep COPY statements out of sys.jobs_log.");
                }

                return DumpAsync(ci.ArgAt<OctoObjectId>(0), ci.ArgAt<CancellationToken>(3));
            });

        var (zip, manifest) = await OpenResultAsync(await CreateJob().Run(Tenant, true, null));

        using (zip)
        {
            await Assert.That(manifest.FormatVersion).IsEqualTo(1);
            await Assert.That(zip.GetEntry(BackupArchiveContainer.MongoBlobEntry)).IsNotNull();
        }

        await Assert.That(_log.Messages.Any(m => m.Contains("stats.jobs_log_filter"))).IsTrue();
        await Assert.That((await StagingRunAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Run_WhenAnArchiveFailsInTheMiddle_DeletesTheStagingFiles_StoresNothing_AndNamesTheSlicesDone()
    {
        SetupArchives(Snapshot(RawRtId, "raw"), Snapshot(RollupRtId, "rollup"));
        var calls = 0;
        _repository.DumpArchiveToStagingAsync(Arg.Any<OctoObjectId>(), Arg.Any<ArchiveCopyStagingLocation>(),
                Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ++calls == 2
                ? throw new InvalidOperationException("the store is unreachable")
                : DumpAsync(ci.ArgAt<OctoObjectId>(0), ci.ArgAt<CancellationToken>(3)));

        var error = await Assert.That(async () => await CreateJob().Run(Tenant, true, null)).Throws<JobFailedException>();

        await Assert.That(error!.Message.Contains(RollupRtId)).IsTrue();
        await Assert.That(error.Message.Contains("the store is unreachable")).IsTrue();
        await Assert.That(error.Message.Contains("Done before: 1 archive(s), 2 slice(s)")).IsTrue();
        await Assert.That((await StagingRunAsync()).Count).IsEqualTo(0);
        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.TenantDumps, Tenant,
            "none")).IsNull();
        var dumps = new List<ArtifactInfo>();
        await foreach (var info in _env.Store.ListAsync(_env.Keys.CategoryPrefix(ArtifactCategories.TenantDumps)))
        {
            dumps.Add(info);
        }

        await Assert.That(dumps).IsEmpty();
        await _files.Received().DeleteFileAsync(Arg.Is<string>(p => p.EndsWith("tenant-1-mongo.tar.gz")));
    }

    [Test]
    public async Task Run_WhenCancelledInTheMiddle_DeletesTheStagingFiles_AndLeavesNoDump()
    {
        SetupArchives(Snapshot(RawRtId, "raw"), Snapshot(RollupRtId, "rollup"));
        using var cts = new CancellationTokenSource();
        var calls = 0;
        _repository.DumpArchiveToStagingAsync(Arg.Any<OctoObjectId>(), Arg.Any<ArchiveCopyStagingLocation>(),
                Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var result = await DumpAsync(ci.ArgAt<OctoObjectId>(0), ci.ArgAt<CancellationToken>(3));
                if (++calls == 1)
                {
                    cts.Cancel();
                }

                return result;
            });
        var token = Substitute.For<IBotCancellationToken>();
        token.ShutdownToken.Returns(cts.Token);

        await Assert.That(async () => await CreateJob().Run(Tenant, true, token)).Throws<OperationCanceledException>();

        await Assert.That((await StagingRunAsync()).Count).IsEqualTo(0);
        var dumps = new List<ArtifactInfo>();
        await foreach (var info in _env.Store.ListAsync(_env.Keys.CategoryPrefix(ArtifactCategories.TenantDumps)))
        {
            dumps.Add(info);
        }

        await Assert.That(dumps).IsEmpty();
    }

    [Test]
    public async Task Run_NoKeySecretOrTokenReachesTheJobLogOrTheException()
    {
        SetupArchives(Snapshot(RawRtId, "raw"));
        _repository.DumpArchiveToStagingAsync(Arg.Any<OctoObjectId>(), Arg.Any<ArchiveCopyStagingLocation>(),
                Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>())
            .Returns<Task<ArchiveCopyDumpResult>>(ci =>
            {
                // A faulty engine that echoes the statement: the location holds the credential.
                var location = ci.ArgAt<ArchiveCopyStagingLocation>(1);
                throw new InvalidOperationException($"COPY failed for '{location.RevealUri()}'");
            });

        var error = await Assert.That(async () => await CreateJob().Run(Tenant, true, null)).Throws<JobFailedException>();

        var everything = string.Join('\n', _log.Messages) + error!.Message + error.InnerException;
        await Assert.That(everything.Contains(Secret)).IsFalse();
        await Assert.That(everything.Contains(Uri.EscapeDataString(Secret))).IsFalse();
        await Assert.That(everything.Contains("AKIACANARY")).IsFalse();
    }

    [Test]
    public async Task Run_WithoutArchiveData_IsUnchanged()
    {
        SetupArchives(Snapshot(RawRtId, "raw"));

        var result = await CreateJob().Run(Tenant, false, null);

        StoredArtifact.TryParseResultReference(result, out var parts);
        await Assert.That(parts!.FileName.StartsWith("tenant-1-mongo.tar.gz")).IsTrue();
        await _repository.DidNotReceive().DumpArchiveToStagingAsync(Arg.Any<OctoObjectId>(),
            Arg.Any<ArchiveCopyStagingLocation>(), Arg.Any<ArchiveCopyOptions?>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ToRows()
    {
        yield return new Dictionary<string, object?> { ["rtid"] = "61a", ["voltage"] = 230.1 };
        await Task.CompletedTask;
    }

    /// <summary>A clock the test moves.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private DateTime _now = new(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);

        public override DateTimeOffset GetUtcNow() => new(_now, TimeSpan.Zero);

        public DateTime Advance(TimeSpan by)
        {
            _now += by;
            return _now;
        }
    }
}
