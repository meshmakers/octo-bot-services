using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Meshmakers.Octo.Backend.BotServices.Methods;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ArchiveImportMode = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ArchiveImportMode;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Methods;

/// <summary>
///     AB#8447: the handlers of <c>System.Bot/TenantRepository.Restore-1</c>, <c>ExportArchiveData-1</c> and
///     <c>ImportArchiveData-1</c> validate synchronously (upload, archive id) and accept without side effects; their work
///     enqueues the same Hangfire job type as the REST endpoint once (with the operation id and the starter), relays the
///     job's progress and completes with the result record or a declared error.
/// </summary>
public class TenantRepositoryJobMethodHandlerTests
{
    private const string Tenant = "tenant-1";
    private const string JobId = "job-7";
    private const string ArchiveRtId = "65f0a1b2c3d4e5f6a7b8c9d0";
    private const string UploadId = "upload-1";

    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly GenericFakeJobStorage _storage = new();
    private readonly RecordingOperationProgress _progress = new();
    private readonly ISharedUploadStore _uploads = Substitute.For<ISharedUploadStore>();
    private readonly IBackupFileStorageService _backupFiles = Substitute.For<IBackupFileStorageService>();
    private Job? _enqueued;

    public TenantRepositoryJobMethodHandlerTests()
    {
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            _enqueued = call.ArgAt<Job>(0);
            return JobId;
        });
        _backupFiles.GetTusUploadFilePath(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Path.Combine(Path.GetTempPath(), $"no-such-upload-{Guid.NewGuid():N}"));
    }

    // ---- Restore-1 ----

    [Test]
    public async Task Restore_UsableUpload_AcceptsWithoutEnqueuing()
    {
        StagedUpload(1024);

        var outcome = await RestoreHandler().HandleAsync(RestoreContext(), CancellationToken.None);

        await Assert.That(outcome.IsAccepted).IsTrue();
        await Assert.That(_enqueued).IsNull();
    }

    [Test]
    [Arguments(null, false, BotJobMethodErrors.UploadMissingReason)]
    [Arguments(100L, false, BotJobMethodErrors.UploadIncompleteReason)]
    [Arguments(0L, true, BotJobMethodErrors.UploadEmptyReason)]
    public async Task Restore_UnusableUpload_IsUploadNotFoundAtOnce(long? tusLength, bool complete, string reason)
    {
        if (tusLength != null)
        {
            _uploads.GetAsync(Tenant, UploadId, Arg.Any<CancellationToken>())
                .Returns(new SharedUpload(UploadId, tusLength.Value, ""));
            _uploads.IsCompleteAsync(Tenant, UploadId, Arg.Any<CancellationToken>()).Returns(complete);
        }

        var outcome = await RestoreHandler().HandleAsync(RestoreContext(), CancellationToken.None);

        await Assert.That(outcome.IsAccepted).IsFalse();
        await Assert.That(outcome.Failure!.Code).IsEqualTo(BotJobMethodErrors.UploadNotFound);
        await Assert.That(outcome.Failure.Parameters["reason"]).IsEqualTo(reason);
        await Assert.That(_enqueued).IsNull();
    }

    [Test]
    public async Task Restore_UploadOfAnotherTenant_IsNotFound()
    {
        // The store key carries the tenant: an upload staged for another tenant is not found under this tenant.
        _uploads.FindStagedAsync("other-tenant", UploadId, Arg.Any<CancellationToken>()).Returns(("f", 10L));

        var outcome = await RestoreHandler().HandleAsync(RestoreContext(), CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(BotJobMethodErrors.UploadNotFound);
        await _uploads.Received().FindStagedAsync(Tenant, UploadId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Restore_MissingDatabaseName_IsValidationFailed()
    {
        var outcome = await RestoreHandler().HandleAsync(RestoreContext(databaseName: " "), CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(CkMethodErrorCodes.MethodValidationFailed);
    }

    [Test]
    public async Task Restore_Work_EnqueuesTheRestoreJobOnceAndCompletesWithTheSummary()
    {
        _storage.States.Enqueue("Enqueued");
        _storage.ProgressSequence.Enqueue(new DumpProgress(20, RestoreStages.RestoringDatabase));
        _storage.States.Enqueue("Processing");
        _storage.ProgressSequence.Enqueue(RestoreRepositoryJob.ArchiveProgress(1, 2));
        _storage.States.Enqueue("Succeeded");
        _storage.ResultJson = new RestoreOperationSummary(2, 1, 3).ToJson();

        var outcome = await RestoreHandler().RunAsync(Tenant, "db1", UploadId, "olddb", true, "user-1", "studio",
            _progress, CancellationToken.None);

        _jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        await Assert.That(_enqueued!.Method.Name).IsEqualTo(nameof(IRestoreRepositoryJob.RunForOperation));
        await Assert.That(_enqueued.Args.Take(6).ToArray())
            .IsEquivalentTo(new object?[] { Tenant, "db1", UploadId, "olddb", true, _progress.OperationId });
        await Assert.That(_storage.Written[JobTenantBinding.StartedBySubjectParameter]).IsEqualTo("user-1");
        await Assert.That(_storage.Written[JobTenantBinding.StartedByClientIdParameter]).IsEqualTo("studio");
        await Assert.That(_storage.Written[OperationJobParameters.OperationId]).IsEqualTo(_progress.OperationId);
        await Assert.That(_progress.Reports.Select(r => r.StageCode ?? "").ToArray()).IsEquivalentTo(new[]
            { RestoreStages.Queued, RestoreStages.RestoringDatabase, RestoreStages.RestoringArchives });
        await Assert.That(_progress.Reports.All(r => r.Parameters["jobId"] == JobId)).IsTrue();

        var record = outcome.Value!.Value!;
        await Assert.That(record.JobId).IsEqualTo(JobId);
        await Assert.That(record.ArchiveCount).IsEqualTo(2);
        await Assert.That(record.FailedArchiveCount).IsEqualTo(1);
        await Assert.That(record.SecretsToReEnterCount).IsEqualTo(3);
    }

    [Test]
    [Arguments(DumpJobParameters.InterruptedOutcome, TenantRepositoryRestoreHandler.RestoreInterrupted)]
    [Arguments(DumpJobParameters.OperationClosedOutcome, TenantRepositoryRestoreHandler.RestoreInterrupted)]
    [Arguments(null, TenantRepositoryRestoreHandler.RestoreFailed)]
    public async Task Restore_Work_EndedJob_IsTheDeclaredError(string? outcomeParameter, string code)
    {
        _storage.Outcome = outcomeParameter;
        _storage.States.Enqueue("Deleted");

        var outcome = await RestoreHandler().RunAsync(Tenant, "db1", UploadId, null, false, "user-1", null, _progress,
            CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(code);
        await Assert.That(outcome.Failure.Parameters["jobId"]).IsEqualTo(JobId);
    }

    [Test]
    public async Task Restore_Work_LockTimeout_IsFailedWithReasonAnotherJobRunning()
    {
        _storage.FailedExceptionType = "Hangfire.Storage.DistributedLockTimeoutException";
        _storage.States.Enqueue("Deleted");

        var outcome = await RestoreHandler().RunAsync(Tenant, "db1", UploadId, null, false, "user-1", null, _progress,
            CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(TenantRepositoryRestoreHandler.RestoreFailed);
        await Assert.That(outcome.Failure.Parameters["reason"]).IsEqualTo(BotJobMethodErrors.AnotherJobRunningReason);
    }

    [Test]
    public async Task Restore_Work_JobVanished_IsFailedWithReasonJobMissing()
    {
        _storage.Vanished = true;

        var outcome = await RestoreHandler().RunAsync(Tenant, "db1", UploadId, null, false, "user-1", null, _progress,
            CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(TenantRepositoryRestoreHandler.RestoreFailed);
        await Assert.That(outcome.Failure.Parameters["reason"]).IsEqualTo(BotJobMethodErrors.JobMissingReason);
    }

    [Test]
    public async Task Restore_Work_AlreadyStopping_EnqueuesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await RestoreHandler().RunAsync(Tenant, "db1", UploadId, null, false, "user-1",
            null, _progress, cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(_enqueued).IsNull();
    }

    // ---- ExportArchiveData-1 ----

    [Test]
    public async Task Export_AcceptsWithoutEnqueuing()
    {
        var outcome = await ExportHandler().HandleAsync(ExportContext(ArchiveRtId), CancellationToken.None);

        await Assert.That(outcome.IsAccepted).IsTrue();
        await Assert.That(_enqueued).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("not-an-id")]
    public async Task Export_InvalidArchiveId_IsValidationFailed(string archiveRtId)
    {
        var outcome = await ExportHandler().HandleAsync(ExportContext(archiveRtId), CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(CkMethodErrorCodes.MethodValidationFailed);
    }

    [Test]
    public async Task Export_WindowNotAscending_IsValidationFailed()
    {
        var from = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);

        var outcome = await ExportHandler().HandleAsync(ExportContext(ArchiveRtId, from, from), CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(CkMethodErrorCodes.MethodValidationFailed);
    }

    [Test]
    public async Task Export_Work_RelaysRowsWithoutPercentAndCompletesWithDownloadAndRowCount()
    {
        var from = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);
        _storage.ProgressSequence.Enqueue(ArchiveDataStages.Rows(ArchiveDataStages.ExportingRows, 10_000));
        _storage.States.Enqueue("Processing");
        _storage.States.Enqueue("Succeeded");
        _storage.ResultJson = new ArchiveDataOperationSummary(12_345).ToJson();

        var outcome = await ExportHandler().RunAsync(Tenant, ArchiveRtId, from, to, "user-1", null, _progress,
            CancellationToken.None);

        await Assert.That(_enqueued!.Method.Name).IsEqualTo(nameof(IExportArchiveDataJob.RunForOperation));
        await Assert.That(_enqueued.Args.Take(5).ToArray())
            .IsEquivalentTo(new object?[] { Tenant, ArchiveRtId, from, to, _progress.OperationId });
        var rows = _progress.Reports.Single(r => r.StageCode == ArchiveDataStages.ExportingRows);
        await Assert.That(rows.Percent).IsNull();
        await Assert.That(rows.Parameters["rows"]).IsEqualTo("10000");
        var record = outcome.Value!.Value!;
        await Assert.That(record.JobId).IsEqualTo(JobId);
        await Assert.That(record.RowCount).IsEqualTo(12_345L);
        await Assert.That(record.DownloadPath).IsEqualTo($"{Tenant}/v1/jobs/download?id={JobId}");
    }

    [Test]
    [Arguments(DumpJobParameters.InterruptedOutcome, TenantRepositoryExportArchiveDataHandler.ExportInterrupted)]
    [Arguments(null, TenantRepositoryExportArchiveDataHandler.ExportFailed)]
    public async Task Export_Work_EndedJob_IsTheDeclaredError(string? outcomeParameter, string code)
    {
        _storage.Outcome = outcomeParameter;
        _storage.States.Enqueue("Failed");

        var outcome = await ExportHandler().RunAsync(Tenant, ArchiveRtId, null, null, "user-1", null, _progress,
            CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(code);
        await Assert.That(outcome.Failure.Parameters["jobId"]).IsEqualTo(JobId);
    }

    // ---- ImportArchiveData-1 ----

    [Test]
    public async Task Import_UsableUpload_AcceptsWithoutEnqueuing()
    {
        StagedUpload(10);

        var outcome = await ImportHandler().HandleAsync(ImportContext(RtArchiveImportModeEnum.Upsert),
            CancellationToken.None);

        await Assert.That(outcome.IsAccepted).IsTrue();
        await Assert.That(_enqueued).IsNull();
    }

    [Test]
    public async Task Import_NoUpload_IsUploadNotFound()
    {
        var outcome = await ImportHandler().HandleAsync(ImportContext(null), CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(BotJobMethodErrors.UploadNotFound);
        await Assert.That(_enqueued).IsNull();
    }

    [Test]
    public async Task Import_Work_EnqueuesTheImportJobAndCompletesWithTheRowCount()
    {
        _storage.States.Enqueue("Succeeded");
        _storage.ResultJson = new ArchiveDataOperationSummary(42).ToJson();

        var outcome = await ImportHandler().RunAsync(Tenant, ArchiveRtId, UploadId, ArchiveImportMode.Upsert, "user-1",
            null, _progress, CancellationToken.None);

        await Assert.That(_enqueued!.Method.Name).IsEqualTo(nameof(IImportArchiveDataJob.RunForOperation));
        await Assert.That(_enqueued.Args.Take(5).ToArray()).IsEquivalentTo(new object?[]
            { Tenant, ArchiveRtId, UploadId, ArchiveImportMode.Upsert, _progress.OperationId });
        await Assert.That(outcome.Value!.Value!.RowCount).IsEqualTo(42L);
        await Assert.That(outcome.Value.Value.JobId).IsEqualTo(JobId);
    }

    [Test]
    [Arguments(DumpJobParameters.OperationClosedOutcome, TenantRepositoryImportArchiveDataHandler.ImportInterrupted)]
    [Arguments(null, TenantRepositoryImportArchiveDataHandler.ImportFailed)]
    public async Task Import_Work_EndedJob_IsTheDeclaredError(string? outcomeParameter, string code)
    {
        _storage.Outcome = outcomeParameter;
        _storage.States.Enqueue("Deleted");

        var outcome = await ImportHandler().RunAsync(Tenant, ArchiveRtId, UploadId, ArchiveImportMode.InsertOnly,
            "user-1", null, _progress, CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(code);
    }

    // ---- wire form ----

    [Test]
    public async Task Results_AreTheBareCamelCaseRecordsOnTheWire()
    {
        var restore = JsonSerializer.Serialize(new RtRepositoryRestoreResultRecord
        {
            JobId = JobId, ArchiveCount = 2, FailedArchiveCount = 0, SecretsToReEnterCount = 1
        }, CkMethodJson.Options);
        var export = JsonSerializer.Serialize(new RtArchiveDataExportResultRecord
        {
            JobId = JobId, DownloadPath = "t/v1/jobs/download?id=job-7", RowCount = 5_000_000_000L
        }, CkMethodJson.Options);
        var import = JsonSerializer.Serialize(new RtArchiveDataImportResultRecord { JobId = JobId, RowCount = 3 },
            CkMethodJson.Options);

        await Assert.That(restore).IsEqualTo(
            "{\"jobId\":\"job-7\",\"archiveCount\":2,\"failedArchiveCount\":0,\"secretsToReEnterCount\":1}");
        await Assert.That(export).IsEqualTo(
            "{\"jobId\":\"job-7\",\"downloadPath\":\"t/v1/jobs/download?id=job-7\",\"rowCount\":5000000000}");
        await Assert.That(import).IsEqualTo("{\"jobId\":\"job-7\",\"rowCount\":3}");
        var read = JsonSerializer.Deserialize<RtArchiveDataExportResultRecord>(export, CkMethodJson.Options)!;
        await Assert.That(read.RowCount).IsEqualTo(5_000_000_000L);
        await Assert.That(read.DownloadPath).IsEqualTo("t/v1/jobs/download?id=job-7");
    }

    // ---- helpers ----

    private void StagedUpload(long size) =>
        _uploads.FindStagedAsync(Tenant, UploadId, Arg.Any<CancellationToken>()).Returns(("staged", size));

    private IOptions<BotJobMethodOptions> FastOptions() =>
        Options.Create(new BotJobMethodOptions { PollInterval = TimeSpan.FromMilliseconds(1) });

    private TenantRepositoryRestoreHandler RestoreHandler() =>
        new(_jobs, _storage, _uploads, _backupFiles, FastOptions(), TimeProvider.System,
            NullLogger<TenantRepositoryRestoreHandler>.Instance);

    private TenantRepositoryExportArchiveDataHandler ExportHandler() =>
        new(_jobs, _storage, FastOptions(), TimeProvider.System,
            NullLogger<TenantRepositoryExportArchiveDataHandler>.Instance);

    private TenantRepositoryImportArchiveDataHandler ImportHandler() =>
        new(_jobs, _storage, _uploads, _backupFiles, FastOptions(), TimeProvider.System,
            NullLogger<TenantRepositoryImportArchiveDataHandler>.Instance);

    private static CkMethodRequest Request(string methodId) =>
        new(Tenant)
        {
            InvocationId = Guid.NewGuid(),
            MethodId = methodId,
            CkTypeId = "System.Bot/TenantRepository",
            DeadlineUtc = DateTime.UtcNow.AddSeconds(15)
        };

    private static CkMethodPrincipal Admin() => new("user-1", ["TenantManagement"], ["octo_api"]);

    private static CkMethodContext<TenantRepositoryRestoreParameters> RestoreContext(string databaseName = "db1") =>
        new(Request(SystemBotCkIds.TenantRepositoryRestoreMethodId),
            new TenantRepositoryRestoreParameters { TusFileId = UploadId, DatabaseName = databaseName })
        {
            CallerPrincipal = Admin()
        };

    private static CkMethodContext<TenantRepositoryExportArchiveDataParameters> ExportContext(string archiveRtId,
        DateTime? fromUtc = null, DateTime? toUtc = null) =>
        new(Request(SystemBotCkIds.TenantRepositoryExportArchiveDataMethodId),
            new TenantRepositoryExportArchiveDataParameters
            {
                ArchiveRtId = archiveRtId, FromUtc = fromUtc, ToUtc = toUtc
            })
        {
            CallerPrincipal = Admin()
        };

    private static CkMethodContext<TenantRepositoryImportArchiveDataParameters> ImportContext(
        RtArchiveImportModeEnum? mode) =>
        new(Request(SystemBotCkIds.TenantRepositoryImportArchiveDataMethodId),
            new TenantRepositoryImportArchiveDataParameters
            {
                TusFileId = UploadId, ArchiveRtId = ArchiveRtId, Mode = mode
            })
        {
            CallerPrincipal = Admin()
        };
}
