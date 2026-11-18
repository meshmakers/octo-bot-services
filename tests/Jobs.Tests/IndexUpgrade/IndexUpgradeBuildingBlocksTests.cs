using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.IndexUpgrade;

/// <summary>
///     AB#9118 — the shape rule by index keys, the deferral filter, the CK import gate and the start-up registration.
/// </summary>
public class IndexUpgradeBuildingBlocksTests
{
    [Test]
    [Arguments("{ ckTypeId: 1, 'attributes.name': 1, rtState: 1 }", true)]
    [Arguments("{ ckTypeId: 1, 'attributes.a': 1, 'attributes.b': 1, rtState: 1 }", true)]
    [Arguments("{ ckTypeId: 1, rtState: 1, 'attributes.a': 1 }", true)]
    [Arguments("{ ckTypeId: 1, 'attributes.name': 1, _id: 1, rtState: 1 }", false)]
    [Arguments("{ ckTypeId: 1, _id: 1, rtState: 1 }", false)]
    [Arguments("{ rtState: 1 }", false)]
    [Arguments("{ ckTypeId: 1, rtCreatedBy: 1 }", false)]
    [Arguments("{ 'attributes.name': 1, rtState: 1 }", false)]
    [Arguments("{ _fts: 'text', _ftsx: 1, ckTypeId: 1, rtState: 1 }", false)]
    public async Task Shape_IsDecidedByTheKeys(string keys, bool legacy)
    {
        var index = new BsonDocument { { "name", "Any_1" }, { "key", BsonDocument.Parse(keys) } };

        await Assert.That(LegacyIndexShape.IsLegacy(index)).IsEqualTo(legacy);
    }

    [Test]
    [Arguments("Type_1")]
    [Arguments("Type_1_id")]
    [Arguments("Type_1_id_a1b2c3")]
    [Arguments("attributes.name_1")]
    public async Task Shape_IgnoresTheName(string name)
    {
        var legacy = new BsonDocument
            { { "name", name }, { "key", BsonDocument.Parse("{ ckTypeId: 1, 'attributes.name': 1, rtState: 1 }") } };
        var upgraded = new BsonDocument
        {
            { "name", name }, { "key", BsonDocument.Parse("{ ckTypeId: 1, 'attributes.name': 1, _id: 1, rtState: 1 }") }
        };

        await Assert.That(LegacyIndexShape.IsLegacy(legacy)).IsTrue();
        await Assert.That(LegacyIndexShape.IsLegacy(upgraded)).IsFalse();
    }

    [Test]
    public async Task Shape_UniqueIndexesKeepTheirShape()
    {
        var unique = new BsonDocument
        {
            { "name", "Type_2" }, { "unique", true },
            { "key", BsonDocument.Parse("{ ckTypeId: 1, 'attributes.code': 1, rtState: 1 }") }
        };

        await Assert.That(LegacyIndexShape.IsLegacy(unique)).IsFalse();
    }

    [Test]
    public async Task TiebreakKeys_PutIdRightBeforeRtState()
    {
        var keys = LegacyIndexShape.TiebreakKeysOf(["ckTypeId", "a", "b", "rtState"]);

        await Assert.That(keys.SequenceEqual(["ckTypeId", "a", "b", "_id", "rtState"])).IsTrue();
    }

    [Test]
    public async Task DeferFilter_TurnsADeferralIntoScheduled_AndLeavesOtherFailuresAlone()
    {
        var delay = TimeSpan.FromMinutes(2);
        var before = DateTime.UtcNow;
        var deferred = Elect(new FailedState(new IndexUpgradeDeferredException("busy", delay)));
        var after = DateTime.UtcNow;
        var failed = Elect(new FailedState(new InvalidOperationException("boom")));

        await Assert.That(deferred).IsTypeOf<ScheduledState>();
        var scheduled = (ScheduledState)deferred;
        await Assert.That(scheduled.Reason).IsEqualTo("busy");
        await Assert.That(scheduled.EnqueueAt).IsGreaterThanOrEqualTo(before + delay - TimeSpan.FromMilliseconds(1));
        await Assert.That(scheduled.EnqueueAt).IsLessThanOrEqualTo(after + delay + TimeSpan.FromMilliseconds(1));
        await Assert.That(failed).IsTypeOf<FailedState>();
    }

    [Test]
    public async Task DeferFilter_RunsBeforeAutomaticRetry()
    {
        // AutomaticRetry (order 20) would count the deferral as a failed attempt and finally fail the job.
        var method = typeof(IIndexUpgradeJob).GetMethod(nameof(IIndexUpgradeJob.UpgradeTenantAsync))!;
        var defer = method.GetCustomAttributes(typeof(IndexUpgradeDeferAttribute), false).Cast<JobFilterAttribute>()
            .Single();
        var retry = method.GetCustomAttributes(typeof(AutomaticRetryAttribute), false).Cast<JobFilterAttribute>()
            .Single();
        var queue = (QueueAttribute)method.GetCustomAttributes(typeof(QueueAttribute), false).Single();

        await Assert.That(defer.Order).IsLessThan(retry.Order);
        await Assert.That(queue.Queue).IsEqualTo(IndexUpgradeConstants.Queue);
    }

    [Test]
    public async Task ImportGate_FreeTenant_HoldsItUntilDisposed_AndClearsTheYieldRequest()
    {
        var coordination = new InMemoryIndexUpgradeCoordination();
        var gate = NewGate(coordination);

        var held = await gate.EnterAsync("t1", CancellationToken.None);

        await Assert.That(coordination.IsTenantHeld("t1")).IsTrue();
        held.Dispose();
        await Assert.That(coordination.IsTenantHeld("t1")).IsFalse();
        await Assert.That(coordination.IsYieldRequested("t1")).IsFalse();
    }

    [Test]
    public async Task ImportGate_WaitsForTheUpgrade_ThenHoldsTheTenant()
    {
        var coordination = new InMemoryIndexUpgradeCoordination();
        var upgrade = coordination.TryAcquireTenant("t1", TimeSpan.Zero)!;
        var gate = NewGate(coordination);

        var entering = gate.EnterAsync("t1", CancellationToken.None);
        await Task.Delay(200);
        await Assert.That(entering.IsCompleted).IsFalse();
        await Assert.That(coordination.IsYieldRequested("t1")).IsTrue();

        upgrade.Dispose();
        using var held = await entering.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(coordination.IsTenantHeld("t1")).IsTrue();
    }

    [Test]
    public async Task ImportGate_GivesUpAfterTheWaitTimeout_AndLetsTheImportRun()
    {
        var coordination = new InMemoryIndexUpgradeCoordination();
        using var upgrade = coordination.TryAcquireTenant("t1", TimeSpan.Zero);
        var gate = NewGate(coordination, TimeSpan.Zero);

        using var held = await gate.EnterAsync("t1", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(coordination.IsYieldRequested("t1")).IsFalse();
    }

    [Test]
    public async Task ImportGate_CoordinationFailure_LetsTheImportRun()
    {
        var coordination = Substitute.For<IIndexUpgradeCoordination>();
        coordination.When(c => c.RequestYield(Arg.Any<string>(), Arg.Any<string>()))
            .Do(_ => throw new InvalidOperationException("down"));
        var gate = new CkImportIndexUpgradeGate(coordination,
            Options.Create(new IndexUpgradeOptions { Schedule = IndexUpgradeSchedule.Immediate }),
            NullLogger<CkImportIndexUpgradeGate>.Instance);

        using var held = await gate.EnterAsync("t1", CancellationToken.None);

        await Assert.That(held).IsNotNull();
    }

    [Test]
    public async Task ImportGate_UpgradeSwitchedOff_TouchesNoStorage()
    {
        // Production default (Window, window off): no upgrade builds, so a CK import neither waits nor writes.
        var coordination = Substitute.For<IIndexUpgradeCoordination>();
        var gate = new CkImportIndexUpgradeGate(coordination, Options.Create(new IndexUpgradeOptions()),
            NullLogger<CkImportIndexUpgradeGate>.Instance);

        using var held = await gate.EnterAsync("t1", CancellationToken.None);

        await Assert.That(coordination.ReceivedCalls()).IsEmpty();
    }

    [Test]
    public async Task ImportGate_ConfigurationThatCannotBeRead_LetsTheImportRun()
    {
        var options = Substitute.For<IOptions<IndexUpgradeOptions>>();
        options.Value.Returns(_ => throw new InvalidOperationException("Failed to convert 'one' to Int32"));
        var gate = new CkImportIndexUpgradeGate(Substitute.For<IIndexUpgradeCoordination>(), options,
            NullLogger<CkImportIndexUpgradeGate>.Instance);

        using var held = await gate.EnterAsync("t1", CancellationToken.None);

        await Assert.That(held).IsNotNull();
    }

    [Test]
    public async Task ImportGate_TwoImportsWait_TheFirstGivingUpLeavesTheSecondRequest()
    {
        var coordination = new InMemoryIndexUpgradeCoordination();
        using var upgrade = coordination.TryAcquireTenant("t1", TimeSpan.Zero);
        var giveUpSoon = NewGate(coordination, TimeSpan.FromMilliseconds(300));
        var waitLong = NewGate(coordination, TimeSpan.FromMinutes(5));
        using var cancel = new CancellationTokenSource();

        var second = waitLong.EnterAsync("t1", cancel.Token);
        using (await giveUpSoon.EnterAsync("t1", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)))
        {
        }

        await Assert.That(coordination.IsYieldRequested("t1")).IsTrue();
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        await Assert.That(coordination.IsYieldRequested("t1")).IsFalse();
    }

    [Test]
    public async Task Startup_TwoReplicasStartingTogether_ScheduleOneSweep()
    {
        var coordination = new InMemoryIndexUpgradeCoordination();
        var client = Substitute.For<IBackgroundJobClient>();
        client.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("sweep-1");
        var options = new IndexUpgradeOptions { Schedule = IndexUpgradeSchedule.Immediate };

        IndexUpgradeStartup.Apply(Options.Create(options), Substitute.For<IRecurringJobManager>(), client, coordination,
            NullLogger.Instance);
        IndexUpgradeStartup.Apply(Options.Create(options), Substitute.For<IRecurringJobManager>(), client, coordination,
            NullLogger.Instance);

        client.Received(1).Create(Arg.Any<Job>(), Arg.Any<IState>());
        await Task.CompletedTask;
    }

    [Test]
    public async Task Startup_Immediate_SchedulesOneSweepAfterTheStartDelay_AndRemovesTheWindowJob()
    {
        var recurring = Substitute.For<IRecurringJobManager>();
        var client = Substitute.For<IBackgroundJobClient>();
        client.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns("sweep-1");
        var options = new IndexUpgradeOptions
            { Schedule = IndexUpgradeSchedule.Immediate, StartDelay = TimeSpan.FromMinutes(5) };

        var before = DateTime.UtcNow;
        IndexUpgradeStartup.Apply(Options.Create(options), recurring, client, new InMemoryIndexUpgradeCoordination(),
            NullLogger.Instance);
        var after = DateTime.UtcNow;

        recurring.Received(1).RemoveIfExists(IndexUpgradeConstants.WindowRecurringJobId);
        client.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(IIndexUpgradeSweepJob) &&
                             (string)j.Args[0] == IndexUpgradeConstants.TriggerAfterDeploy),
            Arg.Is<IState>(s => s is ScheduledState));
        var scheduled = (ScheduledState)client.ReceivedCalls().Single().GetArguments()[1]!;
        await Assert.That(scheduled.EnqueueAt).IsGreaterThanOrEqualTo(before + options.StartDelay - TimeSpan.FromMilliseconds(1));
        await Assert.That(scheduled.EnqueueAt).IsLessThanOrEqualTo(after + options.StartDelay + TimeSpan.FromMilliseconds(1));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Startup_WindowEnabled_RegistersTheWindowJobAtItsCron_AndSchedulesNoSweep()
    {
        var recurring = Substitute.For<IRecurringJobManager>();
        var client = Substitute.For<IBackgroundJobClient>();
        var options = new IndexUpgradeOptions
            { Schedule = IndexUpgradeSchedule.Window, Window = { Enabled = true, Cron = "0 2 * * *" } };

        IndexUpgradeStartup.Apply(Options.Create(options), recurring, client, new InMemoryIndexUpgradeCoordination(),
            NullLogger.Instance);

        recurring.Received(1).AddOrUpdate(IndexUpgradeConstants.WindowRecurringJobId, Arg.Any<Job>(), "0 2 * * *",
            Arg.Any<RecurringJobOptions>());
        client.DidNotReceiveWithAnyArgs().Create(default!, default!);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Startup_DefaultOptions_BuildNothing()
    {
        // The code default is the production setting: Window with the window off.
        var recurring = Substitute.For<IRecurringJobManager>();
        var client = Substitute.For<IBackgroundJobClient>();

        IndexUpgradeStartup.Apply(Options.Create(new IndexUpgradeOptions()), recurring, client,
            new InMemoryIndexUpgradeCoordination(), NullLogger.Instance);

        recurring.Received(1).RemoveIfExists(IndexUpgradeConstants.WindowRecurringJobId);
        recurring.DidNotReceiveWithAnyArgs().AddOrUpdate(default!, default!, default!, default!);
        client.DidNotReceiveWithAnyArgs().Create(default!, default!);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Startup_StorageFailure_DoesNotStopTheService()
    {
        var recurring = Substitute.For<IRecurringJobManager>();
        recurring.When(r => r.RemoveIfExists(Arg.Any<string>())).Do(_ => throw new InvalidOperationException("down"));

        var options = new IndexUpgradeOptions { Schedule = IndexUpgradeSchedule.Immediate };

        var registered = IndexUpgradeStartup.Apply(Options.Create(options), recurring,
            Substitute.For<IBackgroundJobClient>(), new InMemoryIndexUpgradeCoordination(), NullLogger.Instance);

        await Assert.That(registered).IsFalse(); // the background service tries again
    }

    [Test]
    public async Task Startup_ConfigurationThatCannotBeRead_DoesNotStopTheService_AndRegistersNothing()
    {
        var options = Substitute.For<IOptions<IndexUpgradeOptions>>();
        options.Value.Returns(_ => throw new InvalidOperationException("Failed to convert 'Imediate'"));
        var recurring = Substitute.For<IRecurringJobManager>();
        var client = Substitute.For<IBackgroundJobClient>();

        var done = IndexUpgradeStartup.Apply(options, recurring, client, new InMemoryIndexUpgradeCoordination(),
            NullLogger.Instance);

        await Assert.That(done).IsTrue();
        await Assert.That(recurring.ReceivedCalls()).IsEmpty();
        await Assert.That(client.ReceivedCalls()).IsEmpty();
    }

    [Test]
    [Arguments(7)]
    [Arguments(-1)]
    public async Task Options_ValuesOutsideTheirRange_BecomeSafe(int schedule)
    {
        var options = new IndexUpgradeOptions
        {
            Schedule = (IndexUpgradeSchedule)schedule, Window = { Enabled = true }, MaxConcurrent = -2,
            PollInterval = TimeSpan.FromMilliseconds(-2)
        };

        options.Normalize();

        await Assert.That(options.Schedule).IsEqualTo(IndexUpgradeSchedule.Window);
        await Assert.That(options.Window.Enabled).IsFalse();
        await Assert.That(options.MaxConcurrent).IsEqualTo(0);
        await Assert.That(options.PollInterval).IsEqualTo(TimeSpan.FromSeconds(5));
        await Assert.That(options.CanBuild).IsFalse();
    }

    private static CkImportIndexUpgradeGate NewGate(IIndexUpgradeCoordination coordination,
        TimeSpan? waitTimeout = null) =>
        new(coordination,
            Options.Create(new IndexUpgradeOptions
            {
                Schedule = IndexUpgradeSchedule.Immediate, ImportWaitTimeout = waitTimeout ?? TimeSpan.FromMinutes(5)
            }), NullLogger<CkImportIndexUpgradeGate>.Instance);

    private static IState Elect(IState candidate)
    {
        var storage = Substitute.For<JobStorage>();
        var connection = Substitute.For<IStorageConnection>();
        var transaction = Substitute.For<IWriteOnlyTransaction>();
        var job = new BackgroundJob("1", Job.FromExpression<IIndexUpgradeJob>(j => j.UpgradeTenantAsync("t", "x", null, null)),
            DateTime.UtcNow);
        var apply = new ApplyStateContext(storage, connection, transaction, job, candidate, ProcessingState.StateName);
        var context = new ElectStateContext(apply);
        new IndexUpgradeDeferAttribute().OnStateElection(context);
        return context.CandidateState;
    }
}
