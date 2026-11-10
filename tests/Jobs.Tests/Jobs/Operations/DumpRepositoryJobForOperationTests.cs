using System.Reflection;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.Operations;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.Operations;

/// <summary>
///     AB#8445: the dump job started by the async method <c>System.Bot/TenantRepository.Dump-1</c> runs at most once
///     (no Hangfire retry, a requeued job is refused, a job whose operation was ended is refused) and reports its progress
///     as a job parameter. The classic <see cref="IDumpRepositoryJob.Run" /> is unchanged.
/// </summary>
public class DumpRepositoryJobForOperationTests
{
    private const string Tenant = "tenant-1";
    private const string OperationId = "0123456789abcdef0123456789abcdef";

    private readonly ILogger<DumpRepositoryJob> _logger = Substitute.For<ILogger<DumpRepositoryJob>>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();
    private readonly IDumpOperationGate _gate = Substitute.For<IDumpOperationGate>();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"dump-op-test-{Guid.NewGuid():N}");

    [After(Test)]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Test]
    public async Task RunForOperation_HasNoAutomaticRetry()
    {
        var method = typeof(IDumpRepositoryJob).GetMethod(nameof(IDumpRepositoryJob.RunForOperation))!;
        var retry = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(retry).IsNotNull();
        await Assert.That(retry!.Attempts).IsEqualTo(0);
        await Assert.That(method.GetCustomAttribute<DisableConcurrentExecutionAttribute>()).IsNotNull();
    }

    [Test]
    public async Task Run_ClassicSignatureIsUnchanged()
    {
        // Old CLI versions and stored jobs address Run(string, bool, IBotCancellationToken) by its signature.
        var run = typeof(IDumpRepositoryJob).GetMethod(nameof(IDumpRepositoryJob.Run))!;

        await Assert.That(run.GetParameters().Select(p => p.ParameterType).ToArray())
            .IsEquivalentTo(new[] { typeof(string), typeof(bool), typeof(IBotCancellationToken) });
        await Assert.That(run.GetCustomAttribute<AutomaticRetryAttribute>()!.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task RunForOperation_FirstStart_DumpsAndReportsProgress()
    {
        var expectedPath = ArrangeSuccessfulDump();
        _gate.IsOperationOpenAsync(Tenant, OperationId, Arg.Any<CancellationToken>()).Returns(true);
        var context = new FakePerformContext("job-1");

        var result = await CreateJob().RunForOperation(Tenant, false, OperationId, context.Context, null);

        await Assert.That(result).IsEqualTo(expectedPath);
        await Assert.That(context.GetJobParameter<bool>(DumpJobParameters.Started)).IsTrue();
        var progress = context.Progress;
        await Assert.That(progress.Select(p => p.StageCode).ToArray())
            .IsEquivalentTo(new[] { DumpStages.DumpingDatabase, DumpStages.StoringArtifact });
        await Assert.That(progress.Select(p => p.Percent).ToArray()).IsInOrder();
        await _systemContext.Received(1).BackupTenantAsync(Tenant, expectedPath, timeout: TimeSpan.FromHours(1));
    }

    [Test]
    public async Task RunForOperation_SecondStartOfTheSameJob_IsRefusedWithoutDump()
    {
        ArrangeSuccessfulDump();
        _gate.IsOperationOpenAsync(Tenant, OperationId, Arg.Any<CancellationToken>()).Returns(true);
        var context = new FakePerformContext("job-1");
        context.SetJobParameter(DumpJobParameters.Started, true); // the first run was interrupted by a restart

        await Assert.That(async () =>
                await CreateJob().RunForOperation(Tenant, false, OperationId, context.Context, null))
            .Throws<JobFailedException>();

        await Assert.That(context.GetJobParameter<string>(DumpJobParameters.Outcome))
            .IsEqualTo(DumpJobParameters.InterruptedOutcome);
        await _systemContext.DidNotReceiveWithAnyArgs().BackupTenantAsync(default!, default!, default);
        await _gate.DidNotReceiveWithAnyArgs().IsOperationOpenAsync(default!, default!, default);
    }

    [Test]
    public async Task RunForOperation_OperationNoLongerOpen_IsRefusedWithoutDump()
    {
        ArrangeSuccessfulDump();
        _gate.IsOperationOpenAsync(Tenant, OperationId, Arg.Any<CancellationToken>()).Returns(false);
        var context = new FakePerformContext("job-1");

        await Assert.That(async () =>
                await CreateJob().RunForOperation(Tenant, false, OperationId, context.Context, null))
            .Throws<JobFailedException>();

        await Assert.That(context.GetJobParameter<string>(DumpJobParameters.Outcome))
            .IsEqualTo(DumpJobParameters.OperationClosedOutcome);
        await _systemContext.DidNotReceiveWithAnyArgs().BackupTenantAsync(default!, default!, default);
    }

    [Test]
    public async Task Run_Classic_WritesNoJobParameters()
    {
        var expectedPath = ArrangeSuccessfulDump();

        var result = await CreateJob().Run(Tenant, false, null);

        await Assert.That(result).IsEqualTo(expectedPath);
        await _gate.DidNotReceiveWithAnyArgs().IsOperationOpenAsync(default!, default!, default);
    }

    [Test]
    public async Task ArchiveProgress_GoesFrom50To90WithNumberAndCount()
    {
        var first = DumpRepositoryJob.ArchiveProgress(1, 4);
        var last = DumpRepositoryJob.ArchiveProgress(4, 4);

        await Assert.That(first.Percent).IsEqualTo(50);
        await Assert.That(last.Percent).IsEqualTo(80);
        await Assert.That(last.StageCode).IsEqualTo(DumpStages.ExportingArchives);
        await Assert.That(last.Parameters!["archive"]).IsEqualTo("4");
        await Assert.That(last.Parameters!["archiveCount"]).IsEqualTo("4");
    }

    [Test]
    public async Task DumpProgress_RoundTripsThroughJson()
    {
        var progress = DumpRepositoryJob.ArchiveProgress(2, 3);

        var read = DumpProgress.FromJson(progress.ToJson());

        await Assert.That(read!.Percent).IsEqualTo(progress.Percent);
        await Assert.That(read.StageCode).IsEqualTo(progress.StageCode);
        await Assert.That(read.Parameters!["archive"]).IsEqualTo("2");
        await Assert.That(DumpProgress.FromJson("not json")).IsNull();
        await Assert.That(DumpProgress.FromJson(null)).IsNull();
    }

    private DumpRepositoryJob CreateJob() =>
        new(_logger, _systemContext, _backupFileStorage, operationGate: _gate);

    private string ArrangeSuccessfulDump()
    {
        var path = Path.Combine(_tempDir, Tenant, "dump.tar.gz");
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _systemContext.FindTenantContextAsync(Tenant).Returns(Substitute.For<ITenantContext>());
        _backupFileStorage.GenerateDumpFileName(Tenant).Returns("dump.tar.gz");
        _backupFileStorage.GetDumpFilePath(Tenant, "dump.tar.gz").Returns(path);
        _systemContext.BackupTenantAsync(Tenant, path, timeout: TimeSpan.FromHours(1))
            .Returns(new CommandResult { Success = true });
        return path;
    }

    /// <summary>A <see cref="PerformContext" /> whose job parameters live in a dictionary (Hangfire's JSON convention).</summary>
    private sealed class FakePerformContext
    {
        private readonly Dictionary<string, string> _parameters = new();

        public FakePerformContext(string jobId)
        {
            var connection = Substitute.For<IStorageConnection>();
            connection.When(c => c.SetJobParameter(jobId, Arg.Any<string>(), Arg.Any<string>()))
                .Do(call =>
                {
                    var name = call.ArgAt<string>(1);
                    var value = call.ArgAt<string>(2);
                    _parameters[name] = value;
                    if (name == DumpJobParameters.Progress)
                    {
                        Progress.Add(DumpProgress.FromJson(SerializationHelper.Deserialize<string>(value))!);
                    }
                });
            connection.GetJobParameter(jobId, Arg.Any<string>())
                .Returns(call => _parameters.GetValueOrDefault(call.ArgAt<string>(1)));
            var job = Job.FromExpression<IDumpRepositoryJob>(j =>
                j.RunForOperation(Tenant, false, OperationId, null, null));
            Context = new PerformContext(Substitute.For<JobStorage>(), connection,
                new BackgroundJob(jobId, job, DateTime.UtcNow), Substitute.For<IJobCancellationToken>());
        }

        public PerformContext Context { get; }

        public List<DumpProgress> Progress { get; } = [];

        public void SetJobParameter(string name, object value) =>
            _parameters[name] = SerializationHelper.Serialize(value, SerializationOption.User);

        public T? GetJobParameter<T>(string name) =>
            _parameters.TryGetValue(name, out var raw) ? SerializationHelper.Deserialize<T>(raw, SerializationOption.User) : default;
    }
}
