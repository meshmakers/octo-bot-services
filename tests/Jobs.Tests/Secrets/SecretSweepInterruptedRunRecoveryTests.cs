using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using Hangfire.Storage.Monitoring;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     AB#5539 — sweep runs left in Running by an ended bot process are marked Failed after a restart.
/// </summary>
public class SecretSweepInterruptedRunRecoveryTests
{
    private static readonly DateTime ServiceStartedAt = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = ServiceStartedAt.AddMinutes(2);

    private readonly ISecretSweepJobInspector _inspector = Substitute.For<ISecretSweepJobInspector>();
    private readonly InMemorySecretSweepRunStore _runs = new();

    private SecretSweepInterruptedRunRecovery CreateRecovery(ISecretSweepRunStore? store = null)
    {
        return new SecretSweepInterruptedRunRecovery(store ?? _runs, _inspector,
            Substitute.For<ILogger<SecretSweepInterruptedRunRecovery>>(), new FixedTimeProvider(Now));
    }

    private static SecretSweepRunDto Run(string id, DateTime startedAt,
        SecretSweepOutcomeDto outcome = SecretSweepOutcomeDto.Running)
    {
        return new SecretSweepRunDto
        {
            RunId = id, Mode = SecretSweepModeDto.Encrypt, Outcome = outcome, StartedAt = startedAt,
            CompletedAt = outcome == SecretSweepOutcomeDto.Running ? null : startedAt.AddSeconds(5)
        };
    }

    private async Task<SecretSweepRunDto> GetRun(string tenantId, string runId)
    {
        return (await _runs.GetRunsAsync(tenantId)).Single(r => r.RunId == runId);
    }

    [Test]
    public async Task JobNoLongerProcessing_IsMarkedFailedWithTheInterruptedReason()
    {
        await _runs.UpsertAsync("t1", Run("job-1", ServiceStartedAt.AddMinutes(-10)));
        _inspector.GetState("job-1", ServiceStartedAt).Returns(SecretSweepJobState.NotProcessing);

        var marked = await CreateRecovery().RecoverAsync(["t1"], ServiceStartedAt, CancellationToken.None);

        await Assert.That(marked).IsEqualTo(1);
        var run = await GetRun("t1", "job-1");
        await Assert.That(run.Outcome).IsEqualTo(SecretSweepOutcomeDto.Failed);
        await Assert.That(run.Reason).IsEqualTo("Interrupted (service restart)");
        await Assert.That(run.CompletedAt).IsEqualTo(Now);
        await Assert.That(run.StartedAt).IsEqualTo(ServiceStartedAt.AddMinutes(-10));
    }

    [Test]
    public async Task ProcessingOnALiveServer_IsLeftRunning()
    {
        // E.g. a long sweep on another replica, started before this instance.
        await _runs.UpsertAsync("t1", Run("job-2", ServiceStartedAt.AddMinutes(-10)));
        _inspector.GetState("job-2", ServiceStartedAt).Returns(SecretSweepJobState.ProcessingOnLiveServer);

        var marked = await CreateRecovery().RecoverAsync(["t1"], ServiceStartedAt, CancellationToken.None);

        await Assert.That(marked).IsEqualTo(0);
        var run = await GetRun("t1", "job-2");
        await Assert.That(run.Outcome).IsEqualTo(SecretSweepOutcomeDto.Running);
        await Assert.That(run.Reason).IsNull();
    }

    [Test]
    [Arguments(SecretSweepJobState.ProcessingOnDeadServer)]
    [Arguments(SecretSweepJobState.Unknown)]
    public async Task DeadServerOrNoJob_IsInterruptedOnlyWhenTheRunStartedBeforeTheService(SecretSweepJobState state)
    {
        await _runs.UpsertAsync("t1", Run("old", ServiceStartedAt.AddSeconds(-1)));
        await _runs.UpsertAsync("t1", Run("new", ServiceStartedAt.AddSeconds(30)));
        _inspector.GetState(Arg.Any<string>(), ServiceStartedAt).Returns(state);

        var marked = await CreateRecovery().RecoverAsync(["t1"], ServiceStartedAt, CancellationToken.None);

        await Assert.That(marked).IsEqualTo(1);
        await Assert.That((await GetRun("t1", "old")).Outcome).IsEqualTo(SecretSweepOutcomeDto.Failed);
        await Assert.That((await GetRun("t1", "new")).Outcome).IsEqualTo(SecretSweepOutcomeDto.Running);
    }

    [Test]
    public async Task FinishedRuns_AreNotTouched_AndTheirJobIsNotLookedUp()
    {
        var finished = Run("done", ServiceStartedAt.AddHours(-1), SecretSweepOutcomeDto.Succeeded);
        finished.Reason = "remark";
        await _runs.UpsertAsync("t1", finished);
        await _runs.UpsertAsync("t1", Run("skipped", ServiceStartedAt.AddHours(-1), SecretSweepOutcomeDto.Skipped));

        var marked = await CreateRecovery().RecoverAsync(["t1"], ServiceStartedAt, CancellationToken.None);

        await Assert.That(marked).IsEqualTo(0);
        _inspector.DidNotReceiveWithAnyArgs().GetState(default!, default);
        var run = await GetRun("t1", "done");
        await Assert.That(run.Outcome).IsEqualTo(SecretSweepOutcomeDto.Succeeded);
        await Assert.That(run.Reason).IsEqualTo("remark");
    }

    [Test]
    public async Task RunReExecutedOrFinishedMeanwhile_IsNotOverwritten()
    {
        // The run is read as Running, but before the update Hangfire re-executes the job (new start time).
        var store = Substitute.For<ISecretSweepRunStore>();
        store.GetRunsAsync("t1", Arg.Any<int>()).Returns(Task.FromResult<IReadOnlyList<SecretSweepRunDto>>(
            new List<SecretSweepRunDto> { Run("job-3", ServiceStartedAt.AddMinutes(-10)) }));
        var restarted = Run("job-3", ServiceStartedAt.AddMinutes(1));
        var finished = Run("job-3", ServiceStartedAt.AddMinutes(-10), SecretSweepOutcomeDto.Succeeded);
        var applied = new List<bool>();
        store.UpdateAsync("t1", "job-3", Arg.Any<Func<SecretSweepRunDto, bool>>())
            .Returns(ci =>
            {
                var update = ci.Arg<Func<SecretSweepRunDto, bool>>();
                applied.Add(update(restarted));
                applied.Add(update(finished));
                return Task.FromResult<SecretSweepRunDto?>(restarted);
            });
        _inspector.GetState("job-3", ServiceStartedAt).Returns(SecretSweepJobState.NotProcessing);

        var marked = await CreateRecovery(store).RecoverAsync(["t1"], ServiceStartedAt, CancellationToken.None);

        await Assert.That(marked).IsEqualTo(0);
        await Assert.That(applied).IsEquivalentTo(new[] { false, false });
        await Assert.That(restarted.Outcome).IsEqualTo(SecretSweepOutcomeDto.Running);
        await Assert.That(finished.Reason).IsNull();
    }

    [Test]
    public async Task ABrokenTenant_DoesNotStopTheOthers()
    {
        var store = Substitute.For<ISecretSweepRunStore>();
        store.GetRunsAsync("broken", Arg.Any<int>()).ThrowsAsync(new InvalidOperationException("storage down"));
        store.GetRunsAsync("ok", Arg.Any<int>())
            .Returns(_ => _runs.GetRunsAsync("ok"));
        store.UpdateAsync("ok", Arg.Any<string>(), Arg.Any<Func<SecretSweepRunDto, bool>>())
            .Returns(ci => _runs.UpdateAsync("ok", ci.ArgAt<string>(1), ci.Arg<Func<SecretSweepRunDto, bool>>()));
        await _runs.UpsertAsync("ok", Run("job-4", ServiceStartedAt.AddMinutes(-1)));
        _inspector.GetState("job-4", ServiceStartedAt).Returns(SecretSweepJobState.NotProcessing);

        var marked = await CreateRecovery(store).RecoverAsync(["broken", "ok"], ServiceStartedAt,
            CancellationToken.None);

        await Assert.That(marked).IsEqualTo(1);
        await Assert.That((await GetRun("ok", "job-4")).Outcome).IsEqualTo(SecretSweepOutcomeDto.Failed);
    }

    [Test]
    public async Task HostedService_RunOnce_ChecksAllTenantsOfTheCoordinator()
    {
        await _runs.UpsertAsync("system", Run("job-5", ServiceStartedAt.AddMinutes(-3)));
        await _runs.UpsertAsync("acme", Run("job-6", ServiceStartedAt.AddMinutes(-3)));
        _inspector.GetState(Arg.Any<string>(), ServiceStartedAt).Returns(SecretSweepJobState.NotProcessing);
        var coordinator = Substitute.For<ISecretSweepCoordinator>();
        coordinator.GetTenantIdsAsync()
            .Returns(Task.FromResult<IReadOnlyList<string>>(new List<string> { "system", "acme" }));

        var services = new ServiceCollection();
        services.AddSingleton(coordinator);
        services.AddSingleton<ISecretSweepRunStore>(_runs);
        services.AddSingleton(_inspector);
        services.AddSingleton(Substitute.For<ILogger<SecretSweepInterruptedRunRecovery>>());
        services.AddTransient<SecretSweepInterruptedRunRecovery>();
        await using var provider = services.BuildServiceProvider();
        var hosted = new SecretSweepInterruptedRunRecoveryHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new SecretSweepJobOptions()),
            Substitute.For<ILogger<SecretSweepInterruptedRunRecoveryHostedService>>());

        var marked = await hosted.RunOnceAsync(ServiceStartedAt, CancellationToken.None);

        await Assert.That(marked).IsEqualTo(2);
        await Assert.That((await GetRun("system", "job-5")).Outcome).IsEqualTo(SecretSweepOutcomeDto.Failed);
        await Assert.That((await GetRun("acme", "job-6")).Outcome).IsEqualTo(SecretSweepOutcomeDto.Failed);
    }

    [Test]
    public async Task HostedService_RunOnce_SwallowsAFailedTenantListing()
    {
        var coordinator = Substitute.For<ISecretSweepCoordinator>();
        coordinator.GetTenantIdsAsync().ThrowsAsync(new InvalidOperationException("no system tenant"));
        var services = new ServiceCollection();
        services.AddSingleton(coordinator);
        await using var provider = services.BuildServiceProvider();
        var hosted = new SecretSweepInterruptedRunRecoveryHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new SecretSweepJobOptions()),
            Substitute.For<ILogger<SecretSweepInterruptedRunRecoveryHostedService>>());

        await Assert.That(await hosted.RunOnceAsync(ServiceStartedAt, CancellationToken.None)).IsEqualTo(0);
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }
}

/// <summary>
///     AB#5539 — Hangfire job state of a sweep run.
/// </summary>
public class HangfireSecretSweepJobInspectorTests
{
    private static readonly DateTime AliveSince = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly IStorageConnection _connection = Substitute.For<IStorageConnection>();
    private readonly IMonitoringApi _monitoring = Substitute.For<IMonitoringApi>();
    private readonly HangfireSecretSweepJobInspector _inspector;

    public HangfireSecretSweepJobInspectorTests()
    {
        var storage = Substitute.For<JobStorage>();
        storage.GetConnection().Returns(_connection);
        storage.GetMonitoringApi().Returns(_monitoring);
        _monitoring.Servers().Returns(new List<ServerDto>());
        _inspector = new HangfireSecretSweepJobInspector(() => storage);
    }

    private void SetupProcessing(string jobId, string? serverId)
    {
        _connection.GetJobData(jobId).Returns(new JobData { State = ProcessingState.StateName });
        _connection.GetStateData(jobId).Returns(new StateData
        {
            Name = ProcessingState.StateName,
            Data = serverId == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [HangfireSecretSweepJobInspector.ServerIdKey] = serverId }
        });
    }

    [Test]
    public async Task MissingJob_OrForeignId_IsUnknown()
    {
        _connection.GetJobData("gone").Returns((JobData?)null);
        _connection.GetJobData("not-an-id").Throws(new FormatException("bad id"));

        await Assert.That(_inspector.GetState("gone", AliveSince)).IsEqualTo(SecretSweepJobState.Unknown);
        await Assert.That(_inspector.GetState("not-an-id", AliveSince)).IsEqualTo(SecretSweepJobState.Unknown);
        await Assert.That(_inspector.GetState(" ", AliveSince)).IsEqualTo(SecretSweepJobState.Unknown);
    }

    [Test]
    [Arguments("Succeeded")]
    [Arguments("Failed")]
    [Arguments("Enqueued")]
    [Arguments("Deleted")]
    public async Task OtherStates_AreNotProcessing(string state)
    {
        _connection.GetJobData("j").Returns(new JobData { State = state });

        await Assert.That(_inspector.GetState("j", AliveSince)).IsEqualTo(SecretSweepJobState.NotProcessing);
    }

    [Test]
    public async Task Processing_OnAServerWithAHeartbeatSinceStart_IsLive()
    {
        SetupProcessing("j", "pod-b:1:abc");
        _monitoring.Servers().Returns(new List<ServerDto>
        {
            new() { Name = "pod-b:1:abc", Heartbeat = AliveSince.AddSeconds(20) }
        });

        await Assert.That(_inspector.GetState("j", AliveSince)).IsEqualTo(SecretSweepJobState.ProcessingOnLiveServer);
    }

    [Test]
    public async Task Processing_OnAServerWithoutRecentHeartbeat_OrGone_OrWithoutServerId_IsDead()
    {
        SetupProcessing("stale", "pod-a:1:old");
        SetupProcessing("gone", "pod-x:1:zzz");
        SetupProcessing("noserver", null);
        _monitoring.Servers().Returns(new List<ServerDto>
        {
            // The killed process: still listed until the server timeout, last heartbeat before this start.
            new() { Name = "pod-a:1:old", Heartbeat = AliveSince.AddSeconds(-5) },
            new() { Name = "pod-a:1:new", Heartbeat = AliveSince.AddSeconds(30) }
        });

        await Assert.That(_inspector.GetState("stale", AliveSince)).IsEqualTo(SecretSweepJobState.ProcessingOnDeadServer);
        await Assert.That(_inspector.GetState("gone", AliveSince)).IsEqualTo(SecretSweepJobState.ProcessingOnDeadServer);
        await Assert.That(_inspector.GetState("noserver", AliveSince)).IsEqualTo(SecretSweepJobState.ProcessingOnDeadServer);
    }
}
