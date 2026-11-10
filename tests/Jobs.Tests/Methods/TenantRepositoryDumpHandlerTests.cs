using System.Text.Json;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage.Monitoring;
using Meshmakers.Octo.Backend.BotServices.Methods;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v3;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Methods;

/// <summary>
///     AB#8445: the handler of <c>System.Bot/TenantRepository.Dump-1</c> accepts the call without side effects, and its
///     work enqueues the Hangfire dump job once, relays the job's progress and completes with the job id and download
///     path, or with a declared error.
/// </summary>
public class TenantRepositoryDumpHandlerTests
{
    private const string Tenant = "tenant-1";
    private const string JobId = "job-42";

    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly FakeJobStorage _storage = new();
    private readonly RecordingProgress _progress = new();
    private Job? _enqueued;

    public TenantRepositoryDumpHandlerTests()
    {
        _jobs.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            _enqueued = call.ArgAt<Job>(0);
            return JobId;
        });
    }

    [Test]
    public async Task HandleAsync_AcceptsWithoutEnqueuing()
    {
        var outcome = await CreateHandler().HandleAsync(Context(includeArchiveData: true), CancellationToken.None);

        await Assert.That(outcome.IsAccepted).IsTrue();
        await Assert.That(_enqueued).IsNull();
    }

    [Test]
    public async Task Work_EnqueuesTheDumpJobOnceWithTheOperationAndRecordsTheStarter()
    {
        _storage.States.Enqueue("Succeeded");

        var outcome = await CreateHandler().RunAsync(Tenant, true, "user-1", "studio", _progress, CancellationToken.None);

        await Assert.That(outcome.IsSuccess).IsTrue();
        await Assert.That(_enqueued).IsNotNull();
        _jobs.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        await Assert.That(_enqueued!.Method.Name).IsEqualTo(nameof(IDumpRepositoryJob.RunForOperation));
        await Assert.That(_enqueued.Args[0]).IsEqualTo(Tenant);
        await Assert.That((bool)_enqueued.Args[1]!).IsTrue();
        await Assert.That(_enqueued.Args[2]).IsEqualTo(_progress.OperationId);
        await Assert.That(_storage.Written[JobTenantBinding.StartedBySubjectParameter]).IsEqualTo("user-1");
        await Assert.That(_storage.Written[JobTenantBinding.StartedByClientIdParameter]).IsEqualTo("studio");
        await Assert.That(_storage.Written[JobTenantBinding.StartedForTenantParameter]).IsEqualTo(Tenant);
        await Assert.That(_storage.Written[DumpJobParameters.OperationId]).IsEqualTo(_progress.OperationId);
    }

    [Test]
    public async Task Work_RelaysProgressAndCompletesWithJobIdAndDownloadPath()
    {
        _storage.States.Enqueue("Enqueued");
        _storage.ProgressSequence.Enqueue(new DumpProgress(5, DumpStages.DumpingDatabase));
        _storage.States.Enqueue("Processing");
        _storage.ProgressSequence.Enqueue(DumpRepositoryJob.ArchiveProgress(1, 2));
        _storage.States.Enqueue("Processing");
        _storage.ProgressSequence.Enqueue(DumpRepositoryJob.ArchiveProgress(1, 2)); // unchanged: not reported again
        _storage.States.Enqueue("Succeeded");

        var outcome = await CreateHandler().RunAsync(Tenant, true, "user-1", null, _progress, CancellationToken.None);

        await Assert.That(_progress.Reports.Select(r => r.StageCode ?? "").ToArray()).IsEquivalentTo(new[]
            { DumpStages.Queued, DumpStages.DumpingDatabase, DumpStages.ExportingArchives });
        await Assert.That(_progress.Reports.All(r => r.Parameters["jobId"] == JobId)).IsTrue();
        await Assert.That(_progress.Reports[2].Parameters["archive"]).IsEqualTo("1");
        await Assert.That(outcome.Value!.Value!.JobId).IsEqualTo(JobId);
        await Assert.That(outcome.Value.Value.DownloadPath).IsEqualTo($"{Tenant}/v1/jobs/download?id={JobId}");
    }

    [Test]
    [Arguments("Failed")]
    [Arguments("Deleted")]
    public async Task Work_FailedJob_IsDumpFailedWithTheJobId(string state)
    {
        _storage.States.Enqueue(state);

        var outcome = await CreateHandler().RunAsync(Tenant, false, "user-1", null, _progress, CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(TenantRepositoryDumpHandler.DumpFailed);
        await Assert.That(outcome.Failure.Parameters["jobId"]).IsEqualTo(JobId);
    }

    [Test]
    [Arguments(DumpJobParameters.InterruptedOutcome)]
    [Arguments(DumpJobParameters.OperationClosedOutcome)]
    public async Task Work_RefusedJob_IsDumpInterrupted(string outcomeParameter)
    {
        _storage.Outcome = outcomeParameter;
        _storage.States.Enqueue("Deleted");

        var outcome = await CreateHandler().RunAsync(Tenant, false, "user-1", null, _progress, CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(TenantRepositoryDumpHandler.DumpInterrupted);
    }

    [Test]
    public async Task Work_LockTimeout_IsDumpFailedWithReasonAnotherDumpRunning()
    {
        // A second method dump that waited longer than the DisableConcurrentExecution timeout fails before it starts.
        _storage.FailedExceptionType = "Hangfire.Storage.DistributedLockTimeoutException";
        _storage.States.Enqueue("Deleted");

        var outcome = await CreateHandler().RunAsync(Tenant, false, "user-1", null, _progress, CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(TenantRepositoryDumpHandler.DumpFailed);
        await Assert.That(outcome.Failure.Parameters["reason"]).IsEqualTo(TenantRepositoryDumpHandler.AnotherDumpRunningReason);
        await Assert.That(outcome.Failure.Parameters["jobId"]).IsEqualTo(JobId);
    }

    [Test]
    public async Task Work_OtherFailure_HasNoReason()
    {
        _storage.FailedExceptionType = "Meshmakers.Octo.Backend.Jobs.JobFailedException";
        _storage.States.Enqueue("Deleted");

        var outcome = await CreateHandler().RunAsync(Tenant, false, "user-1", null, _progress, CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(TenantRepositoryDumpHandler.DumpFailed);
        await Assert.That(outcome.Failure.Parameters.ContainsKey("reason")).IsFalse();
    }

    [Test]
    public async Task Work_JobVanished_IsDumpFailed()
    {
        _storage.Vanished = true;

        var outcome = await CreateHandler().RunAsync(Tenant, false, "user-1", null, _progress, CancellationToken.None);

        await Assert.That(outcome.Failure!.Code).IsEqualTo(TenantRepositoryDumpHandler.DumpFailed);
    }

    [Test]
    public async Task Work_HostStopping_ThrowsCancellation()
    {
        using var cts = new CancellationTokenSource();
        _storage.States.Enqueue("Processing");
        _storage.OnRead = () => cts.Cancel();

        await Assert.That(async () =>
                await CreateHandler().RunAsync(Tenant, false, "user-1", null, _progress, cts.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Work_AlreadyStopping_EnqueuesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () =>
                await CreateHandler().RunAsync(Tenant, false, "user-1", null, _progress, cts.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(_enqueued).IsNull();
    }

    [Test]
    public async Task Result_IsTheBareCamelCaseRecordOnTheWire()
    {
        var result = new TenantRepositoryDumpResult
        {
            Value = new RtRepositoryDumpResultRecord { JobId = JobId, DownloadPath = "t/v1/jobs/download?id=job-42" }
        };

        var json = JsonSerializer.Serialize(result.Value, CkMethodJson.Options);

        await Assert.That(json).IsEqualTo("{\"jobId\":\"job-42\",\"downloadPath\":\"t/v1/jobs/download?id=job-42\"}");
        var read = JsonSerializer.Deserialize<RtRepositoryDumpResultRecord>(json, CkMethodJson.Options)!;
        await Assert.That(read.JobId).IsEqualTo(JobId);
    }

    [Test]
    public async Task ToOperationProgress_KeepsTheJobIdAndAtMost16Parameters()
    {
        var many = Enumerable.Range(0, 30).ToDictionary(i => $"p{i}", i => i.ToString());

        var progress = TenantRepositoryDumpHandler.ToOperationProgress(
            new DumpProgress(150, DumpStages.ExportingArchives, many), JobId);

        await Assert.That(progress.Percent).IsEqualTo(100);
        await Assert.That(progress.Parameters.Count).IsEqualTo(CkMethodOperationProgress.MaxParameters);
        await Assert.That(progress.Parameters["jobId"]).IsEqualTo(JobId);
        await Assert.That(TenantRepositoryDumpHandler.TryToOperationProgress(new DumpProgress(1, "bad code"), JobId))
            .IsNull();
    }

    private TenantRepositoryDumpHandler CreateHandler() =>
        new(_jobs, _storage, Options.Create(new RepositoryDumpMethodOptions { PollInterval = TimeSpan.FromMilliseconds(1) }),
            TimeProvider.System, NullLogger<TenantRepositoryDumpHandler>.Instance);

    private static CkMethodContext<TenantRepositoryDumpParameters> Context(bool includeArchiveData) =>
        new(new CkMethodRequest(Tenant)
            {
                InvocationId = Guid.NewGuid(),
                MethodId = SystemBotCkIds.TenantRepositoryDumpMethodId,
                CkTypeId = "System.Bot/TenantRepository",
                DeadlineUtc = DateTime.UtcNow.AddSeconds(15)
            },
            new TenantRepositoryDumpParameters { IncludeArchiveData = includeArchiveData })
        {
            CallerPrincipal = new CkMethodPrincipal("user-1", ["TenantManagement"], ["octo_api"])
        };

    private sealed class RecordingProgress : ICkMethodOperationProgress
    {
        public string OperationId { get; } = Guid.NewGuid().ToString("N");

        public List<CkMethodOperationProgress> Reports { get; } = [];

        public Task ReportAsync(CkMethodOperationProgress progress, CancellationToken cancellationToken = default)
        {
            Reports.Add(progress);
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers one queued state (and progress) per read; the job writes progress in Hangfire's JSON convention.</summary>
    private sealed class FakeJobStorage : IJobStorageAccessor
    {
        public Queue<string> States { get; } = new();
        public Queue<DumpProgress> ProgressSequence { get; } = new();
        public Dictionary<string, string> Written { get; } = new();
        public string? Outcome { get; set; }
        public string? FailedExceptionType { get; set; }
        public bool Vanished { get; set; }
        public Action? OnRead { get; set; }

        public JobDetailsDto? GetJobDetails(string jobId)
        {
            OnRead?.Invoke();
            if (Vanished)
            {
                return null;
            }

            var state = States.Count > 0 ? States.Dequeue() : "Processing";
            var properties = new Dictionary<string, string>(Written);
            if (ProgressSequence.Count > 0)
            {
                properties[DumpJobParameters.Progress] =
                    SerializationHelper.Serialize(ProgressSequence.Dequeue().ToJson(), SerializationOption.User);
            }

            if (Outcome != null)
            {
                properties[DumpJobParameters.Outcome] = SerializationHelper.Serialize(Outcome, SerializationOption.User);
            }

            var history = new List<StateHistoryDto>
            {
                new() { StateName = state, CreatedAt = DateTime.UtcNow, Data = new Dictionary<string, string>() }
            };
            if (FailedExceptionType != null)
            {
                // Hangfire's history is newest first: Deleted after the Failed state that names the exception.
                history.Add(new StateHistoryDto
                {
                    StateName = "Failed", CreatedAt = DateTime.UtcNow,
                    Data = new Dictionary<string, string> { ["ExceptionType"] = FailedExceptionType }
                });
            }

            return new JobDetailsDto { Properties = properties, History = history };
        }

        public void SetJobParameter(string jobId, string name, string value) => Written[name] = value;
    }
}
