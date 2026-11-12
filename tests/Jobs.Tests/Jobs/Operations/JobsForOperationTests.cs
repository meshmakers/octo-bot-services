using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Methods;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ArchiveImportMode = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ArchiveImportMode;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.Operations;

/// <summary>
///     AB#8447: the restore, archive export and archive import jobs started by the async methods run at most once (no
///     Hangfire retry, a requeued job is refused, a job whose operation was ended is refused), report their progress as
///     the job parameter <c>OctoOperationProgress</c> and write a count summary. The classic <c>Run</c> signatures are
///     unchanged (stored jobs and older CLI versions address them).
/// </summary>
public class JobsForOperationTests
{
    private const string Tenant = "tenant-1";
    private const string OperationId = "0123456789abcdef0123456789abcdef";
    private const string ArchiveRtId = "665f00000000000000000e21";

    private readonly IDumpOperationGate _gate = Substitute.For<IDumpOperationGate>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"jobs-op-test-{Guid.NewGuid():N}");

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Test]
    [Arguments(typeof(IRestoreRepositoryJob))]
    [Arguments(typeof(IExportArchiveDataJob))]
    [Arguments(typeof(IImportArchiveDataJob))]
    public async Task RunForOperation_HasNoAutomaticRetry(Type jobInterface)
    {
        var method = jobInterface.GetMethod("RunForOperation")!;

        await Assert.That(method.GetCustomAttribute<AutomaticRetryAttribute>()!.Attempts).IsEqualTo(0);
        await Assert.That(method.GetCustomAttribute<DisableConcurrentExecutionAttribute>()).IsNotNull();
    }

    [Test]
    public async Task ClassicRunSignatures_AreUnchanged()
    {
        await Assert.That(Parameters<IRestoreRepositoryJob>(nameof(IRestoreRepositoryJob.Run))).IsEquivalentTo(new[]
        {
            typeof(string), typeof(string), typeof(string), typeof(string), typeof(bool),
            typeof(Hangfire.Server.PerformContext), typeof(IBotCancellationToken)
        });
        await Assert.That(Parameters<IExportArchiveDataJob>(nameof(IExportArchiveDataJob.Run))).IsEquivalentTo(new[]
            { typeof(string), typeof(string), typeof(DateTime?), typeof(DateTime?), typeof(IBotCancellationToken) });
        await Assert.That(Parameters<IImportArchiveDataJob>(nameof(IImportArchiveDataJob.Run))).IsEquivalentTo(new[]
            { typeof(string), typeof(string), typeof(string), typeof(ArchiveImportMode), typeof(IBotCancellationToken) });
    }

    // ---- restore ----

    [Test]
    public async Task Restore_SecondStartOfTheSameJob_IsRefusedWithoutTouchingTheDatabase()
    {
        _gate.IsOperationOpenAsync(Tenant, OperationId, Arg.Any<CancellationToken>()).Returns(true);
        var context = RestoreContext();
        context.SetJobParameter(OperationJobParameters.Started, true); // the first run was interrupted by a restart

        await Assert.That(async () => await RestoreJob().RunForOperation(Tenant, "db1", "upload-1", null, false,
            OperationId, context.Context, null)).Throws<JobFailedException>();

        await Assert.That(context.GetJobParameter<string>(OperationJobParameters.Outcome))
            .IsEqualTo(DumpJobParameters.InterruptedOutcome);
        await _systemContext.DidNotReceiveWithAnyArgs().IsSystemTenantExistingAsync();
        await _systemContext.DidNotReceiveWithAnyArgs()
            .RestoreTenantAsync(default!, default!, default!, default, default, default, default, default);
        await _gate.DidNotReceiveWithAnyArgs().IsOperationOpenAsync(default!, default!, default);
    }

    [Test]
    public async Task Restore_OperationNoLongerOpen_IsRefusedWithoutTouchingTheDatabase()
    {
        _gate.IsOperationOpenAsync(Tenant, OperationId, Arg.Any<CancellationToken>()).Returns(false);
        var context = RestoreContext();

        await Assert.That(async () => await RestoreJob().RunForOperation(Tenant, "db1", "upload-1", null, false,
            OperationId, context.Context, null)).Throws<JobFailedException>();

        await Assert.That(context.GetJobParameter<string>(OperationJobParameters.Outcome))
            .IsEqualTo(DumpJobParameters.OperationClosedOutcome);
        await Assert.That(context.GetJobParameter<bool>(OperationJobParameters.Started)).IsTrue();
        await _systemContext.DidNotReceiveWithAnyArgs().IsSystemTenantExistingAsync();
    }

    [Test]
    public async Task Restore_FirstStart_ReportsProgressAndWritesTheSummary()
    {
        _gate.IsOperationOpenAsync(Tenant, OperationId, Arg.Any<CancellationToken>()).Returns(true);
        var backupFiles = Substitute.For<IBackupFileStorageService>();
        Directory.CreateDirectory(_tempDir);
        var upload = Path.Combine(_tempDir, "upload-1");
        await File.WriteAllBytesAsync(upload, [1, 2, 3]); // a legacy mongodump (no manifest)
        backupFiles.GetTusUploadFilePath(Tenant, "upload-1").Returns(upload);
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _systemContext.RestoreTenantAsync(Tenant, "db1", upload, null, true, true, Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new CommandResult { Success = true });
        var context = RestoreContext();

        var result = await new RestoreRepositoryJob(Substitute.For<ILogger<RestoreRepositoryJob>>(), _systemContext,
                backupFiles, operationGate: _gate)
            .RunForOperation(Tenant, "db1", "upload-1", null, false, OperationId, context.Context, null);

        await Assert.That(result).IsNotNull();
        await Assert.That(context.Progress.Select(p => p.StageCode).ToArray())
            .IsEquivalentTo(new[] { RestoreStages.PreparingUpload, RestoreStages.RestoringDatabase });
        var summary = RestoreOperationSummary.FromJson(context.GetJobParameter<string>(OperationJobParameters.Result));
        await Assert.That(summary).IsEqualTo(new RestoreOperationSummary(0, 0, 0));
    }

    [Test]
    public async Task RestoreSummary_CountsArchivesFailuresAndSecretsOnly()
    {
        var result = new RestoreRepositoryResult
        {
            Archives =
            [
                new RestoredArchiveSummary { RtId = "a", Outcome = "Imported" },
                new RestoredArchiveSummary { RtId = "b", Outcome = "Failed", Detail = "boom" },
                new RestoredArchiveSummary { RtId = "c", Outcome = "Skipped" }
            ]
        };

        var summary = RestoreOperationSummary.From(result);

        await Assert.That(summary).IsEqualTo(new RestoreOperationSummary(3, 1, 0));
        await Assert.That(summary.ToJson()).DoesNotContain("boom");
        await Assert.That(RestoreOperationSummary.FromJson("nope")).IsNull();
    }

    [Test]
    public async Task RestoreArchiveProgress_GoesFrom50To85WithNumberAndCount()
    {
        var first = RestoreRepositoryJob.ArchiveProgress(1, 4);
        var last = RestoreRepositoryJob.ArchiveProgress(4, 4);

        await Assert.That(first.Percent).IsEqualTo(50);
        await Assert.That(last.Percent).IsLessThan(90);
        await Assert.That(last.Parameters!["archive"]).IsEqualTo("4");
        await Assert.That(last.Parameters!["archiveCount"]).IsEqualTo("4");
    }

    // ---- export ----

    [Test]
    public async Task Export_FirstStart_ReportsRowsAndWritesTheRowCount()
    {
        _gate.IsOperationOpenAsync(Tenant, OperationId, Arg.Any<CancellationToken>()).Returns(true);
        var (job, _) = ExportJob(rowCount: ArchiveDataStages.ReportEveryRows + 5);
        var context = new GenericFakePerformContext("job-1", Job.FromExpression<IExportArchiveDataJob>(j =>
            j.RunForOperation(Tenant, ArchiveRtId, null, null, OperationId, null, null)));

        var path = await job.RunForOperation(Tenant, ArchiveRtId, null, null, OperationId, context.Context, null);

        await Assert.That(File.Exists(path!)).IsTrue();
        await Assert.That(context.Progress.Select(p => p.Parameters!["rows"]).ToArray())
            .IsEquivalentTo(new[] { "0", ArchiveDataStages.ReportEveryRows.ToString() });
        await Assert.That(context.Progress.All(p => p.Percent < 0)).IsTrue();
        var summary = ArchiveDataOperationSummary.FromJson(context.GetJobParameter<string>(OperationJobParameters.Result));
        await Assert.That(summary!.RowCount).IsEqualTo(ArchiveDataStages.ReportEveryRows + 5L);
    }

    [Test]
    public async Task Export_SecondStart_IsRefusedWithoutReadingTheArchive()
    {
        var (job, repository) = ExportJob(rowCount: 1);
        var context = new GenericFakePerformContext("job-1", Job.FromExpression<IExportArchiveDataJob>(j =>
            j.RunForOperation(Tenant, ArchiveRtId, null, null, OperationId, null, null)));
        context.SetJobParameter(OperationJobParameters.Started, true);

        await Assert.That(async () =>
                await job.RunForOperation(Tenant, ArchiveRtId, null, null, OperationId, context.Context, null))
            .Throws<JobFailedException>();

        repository.DidNotReceiveWithAnyArgs().ExportRowsAsync(default, default, default);
    }

    [Test]
    public async Task Export_Classic_WritesNoParameters()
    {
        var (job, _) = ExportJob(rowCount: 2);

        var path = await job.Run(Tenant, ArchiveRtId, null, null, null);

        await Assert.That(File.Exists(path!)).IsTrue();
        await _gate.DidNotReceiveWithAnyArgs().IsOperationOpenAsync(default!, default!, default);
    }

    // ---- import ----

    [Test]
    public async Task Import_OperationNoLongerOpen_IsRefusedWithoutReadingTheUpload()
    {
        _gate.IsOperationOpenAsync(Tenant, OperationId, Arg.Any<CancellationToken>()).Returns(false);
        var uploads = Substitute.For<ISharedUploadStore>();
        var job = new ImportArchiveDataJob(Substitute.For<ILogger<ImportArchiveDataJob>>(), _systemContext,
            Substitute.For<IBackupFileStorageService>(), Substitute.For<IBotArtifactStorage>(), uploads, _gate);
        var context = new GenericFakePerformContext("job-1", Job.FromExpression<IImportArchiveDataJob>(j =>
            j.RunForOperation(Tenant, ArchiveRtId, "upload-1", ArchiveImportMode.InsertOnly, OperationId, null, null)));

        await Assert.That(async () => await job.RunForOperation(Tenant, ArchiveRtId, "upload-1",
            ArchiveImportMode.InsertOnly, OperationId, context.Context, null)).Throws<JobFailedException>();

        await Assert.That(context.GetJobParameter<string>(OperationJobParameters.Outcome))
            .IsEqualTo(DumpJobParameters.OperationClosedOutcome);
        await uploads.DidNotReceiveWithAnyArgs().FindStagedAsync(default!, default!, default);
    }

    [Test]
    public async Task OperationSummaries_RoundTripThroughJson()
    {
        await Assert.That(ArchiveDataOperationSummary.FromJson(new ArchiveDataOperationSummary(7).ToJson())!.RowCount)
            .IsEqualTo(7L);
        await Assert.That(ArchiveDataOperationSummary.FromJson(null)).IsNull();
    }

    // ---- helpers ----

    private static Type[] Parameters<T>(string name) =>
        typeof(T).GetMethod(name)!.GetParameters().Select(p => p.ParameterType).ToArray();

    private RestoreRepositoryJob RestoreJob() =>
        new(Substitute.For<ILogger<RestoreRepositoryJob>>(), _systemContext,
            Substitute.For<IBackupFileStorageService>(), operationGate: _gate);

    private static GenericFakePerformContext RestoreContext() =>
        new("job-1", Job.FromExpression<IRestoreRepositoryJob>(j =>
            j.RunForOperation(Tenant, "db1", "upload-1", null, false, OperationId, null, null)));

    private (ExportArchiveDataJob Job, IStreamDataRepository Repository) ExportJob(int rowCount)
    {
        var tenantContext = Substitute.For<ITenantContext>();
        var repository = Substitute.For<IStreamDataRepository>();
        var archiveStore = Substitute.For<IArchiveRuntimeStore>();
        var backupFiles = Substitute.For<IBackupFileStorageService>();
        _systemContext.FindTenantContextAsync(Tenant).Returns(tenantContext);
        tenantContext.GetStreamDataRepository().Returns(repository);
        tenantContext.GetArchiveRuntimeStore().Returns(archiveStore);
        archiveStore.GetAsync(Arg.Any<OctoObjectId>()).Returns(new ArchiveSnapshot(new OctoObjectId(ArchiveRtId),
            new RtCkId<CkTypeId>("System-1.0.0/Sensor"), CkArchiveStatus.Activated, "voltage-raw",
            new[] { new CkArchiveColumnSpec("voltage", true, false) }));
        repository.ExportRowsAsync(Arg.Any<OctoObjectId>(), null, Arg.Any<CancellationToken>())
            .Returns(_ => Rows(rowCount));
        backupFiles.GetDumpFilePath(Tenant, Arg.Any<string>()).Returns(ci => Path.Combine(_tempDir, (string)ci[1]));
        return (new ExportArchiveDataJob(Substitute.For<ILogger<ExportArchiveDataJob>>(), _systemContext, backupFiles,
            _gate), repository);
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> Rows(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new Dictionary<string, object?> { ["rtid"] = i.ToString(), ["voltage"] = 230.0 };
            await Task.CompletedTask;
        }
    }
}
