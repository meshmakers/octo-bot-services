using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Common.DistributionEventHub.Payloads;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.IndexUpgrade;

/// <summary>
///     AB#9118 — when index upgrade jobs are enqueued: once per tenant on <c>PosCreateTenant</c> although every replica
///     receives it, according to the schedule, by the sweep only for tenants with a legacy index; and a CK import job
///     enters the import gate around its import.
/// </summary>
public class IndexUpgradeSchedulingTests
{
    private readonly InMemoryIndexUpgradeCoordination _coordination = new();
    private readonly IBackgroundJobClient _jobClient = Substitute.For<IBackgroundJobClient>();
    private readonly List<Job> _created = [];
    private readonly List<IState> _states = [];
    private readonly IndexUpgradeOptions _options = new() { Schedule = IndexUpgradeSchedule.Immediate };

    public IndexUpgradeSchedulingTests()
    {
        _jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(call =>
        {
            lock (_created)
            {
                _created.Add(call.Arg<Job>());
                _states.Add(call.Arg<IState>());
                return $"job-{_created.Count}";
            }
        });
    }

    [Test]
    public async Task PosCreateTenant_ReceivedByTwoReplicas_EnqueuesOneJob()
    {
        var replicaA = Consumer(Scheduler());
        var replicaB = Consumer(Scheduler());
        var message = Context(new PosCreateTenant("tenant-a", Guid.NewGuid(), DateTime.UtcNow));

        await Task.WhenAll(Task.Run(() => replicaA.ConsumeAsync(message)), Task.Run(() => replicaB.ConsumeAsync(message)));

        await Assert.That(_created).Count().IsEqualTo(1);
        await Assert.That(_states.Single()).IsTypeOf<ScheduledState>(); // after StartDelay: the services set the tenant up first
        await Assert.That(_created[0].Type).IsEqualTo(typeof(IIndexUpgradeJob));
        await Assert.That((string)_created[0].Args[0]).IsEqualTo("tenant-a");
        await Assert.That((string)_created[0].Args[1]).IsEqualTo(IndexUpgradeConstants.TriggerTenantCreated);
    }

    [Test]
    public async Task PosCreateTenant_AfterTheJobEnded_EnqueuesANewOne()
    {
        // A restore re-attaches the tenant with its legacy indexes again: a finished job must not block the next one.
        var scheduler = Scheduler();
        scheduler.OnTenantCreatedOrAttached("tenant-a");
        _coordination.ActiveJobs.Clear(); // the job succeeded

        scheduler.OnTenantCreatedOrAttached("tenant-a");

        await Assert.That(_created).Count().IsEqualTo(2);
    }

    [Test]
    public async Task PosCreateTenant_WindowScheduleOutsideTheWindow_EnqueuesNothing()
    {
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = true;
        _coordination.WindowClosesAt = DateTime.UtcNow.AddMinutes(-5);

        Scheduler().OnTenantCreatedOrAttached("tenant-a");

        await Assert.That(_created).IsEmpty();
    }

    [Test]
    public async Task PosCreateTenant_WindowScheduleInsideAnOpenWindow_Enqueues()
    {
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = true;
        _coordination.WindowClosesAt = DateTime.UtcNow.AddHours(1);

        Scheduler().OnTenantCreatedOrAttached("tenant-a");

        await Assert.That(_created).Count().IsEqualTo(1);
    }

    [Test]
    public async Task PosCreateTenant_EnqueueFailure_DoesNotFailTheConsumer()
    {
        var coordination = Substitute.For<IIndexUpgradeCoordination>();
        coordination.EnqueueOnce(Arg.Any<string>(), Arg.Any<Func<string>>())
            .Returns(_ => throw new TimeoutException("storage down"));
        var scheduler = new IndexUpgradeScheduler(_jobClient, coordination, Options.Create(_options),
            NullLogger<IndexUpgradeScheduler>.Instance);

        await Consumer(scheduler).ConsumeAsync(Context(new PosCreateTenant("tenant-a", Guid.NewGuid(), DateTime.UtcNow)));
    }

    [Test]
    public async Task Sweep_EnqueuesOnlyTenantsWithALegacyIndex()
    {
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-system", "RtEntity_S", "S_1", "attributes.name");
        indexes.AddLegacy("db-b", "RtEntity_B", "B_1", "attributes.name");
        var systemContext = SystemWithTenants(("a", "db-a"), ("b", "db-b"));

        await Sweep(systemContext, indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null);

        await Assert.That(_created.Select(j => (string)j.Args[0])).IsEquivalentTo(new[] { "system", "b" });
        await Assert.That(_created.Select(j => (string)j.Args[1]).Distinct())
            .IsEquivalentTo(new[] { IndexUpgradeConstants.TriggerAfterDeploy });
    }

    [Test]
    public async Task Sweep_TwoReplicas_EnqueueEachTenantOnce()
    {
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-a", "RtEntity_A", "A_1", "attributes.name");
        var systemContext = SystemWithTenants(("a", "db-a"));

        await Task.WhenAll(Sweep(systemContext, indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null),
            Sweep(systemContext, indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null));

        await Assert.That(_created).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Sweep_WindowSwitchedOff_LooksAtNoTenant()
    {
        _options.Schedule = IndexUpgradeSchedule.Window;
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-a", "RtEntity_A", "A_1", "attributes.name");

        await Sweep(SystemWithTenants(("a", "db-a")), indexes).RunAsync(IndexUpgradeConstants.TriggerWindow, null);

        await Assert.That(indexes.ReadCount).IsEqualTo(0);
        await Assert.That(_created).IsEmpty();
        await Assert.That(_coordination.WindowClosesAt).IsNull();
    }

    [Test]
    public async Task Sweep_WindowOpens_ForMaxDuration_AndEnqueues()
    {
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = true;
        _options.Window.MaxDuration = TimeSpan.FromHours(3);
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-a", "RtEntity_A", "A_1", "attributes.name");

        await Sweep(SystemWithTenants(("a", "db-a")), indexes).RunAsync(IndexUpgradeConstants.TriggerWindow, null);

        await Assert.That(_coordination.WindowClosesAt).IsNotNull();
        await Assert.That(_coordination.WindowClosesAt!.Value).IsGreaterThan(DateTime.UtcNow.AddHours(2.9));
        await Assert.That(_created).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Sweep_RetriedInsideAnOpenWindow_DoesNotExtendIt()
    {
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = true;
        var open = DateTime.UtcNow.AddMinutes(30);
        _coordination.WindowClosesAt = open;

        await Sweep(SystemWithTenants(), new FakeTenantIndexes()).RunAsync(IndexUpgradeConstants.TriggerWindow, null);

        await Assert.That(_coordination.WindowClosesAt).IsEqualTo(open);
    }

    [Test]
    public async Task Sweep_OneTenantUnreadable_TheOthersAreStillChecked()
    {
        var indexes = Substitute.For<ITenantIndexInspector>();
        indexes.FindLegacyIndexesAsync("db-a", null, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<LegacyIndex>>>(_ => throw new InvalidOperationException("broken"));
        indexes.FindLegacyIndexesAsync("db-b", null, Arg.Any<CancellationToken>())
            .Returns([new LegacyIndex("RtEntity_B", "B_1", ["ckTypeId", "a", "rtState"])]);
        indexes.FindLegacyIndexesAsync("db-system", null, Arg.Any<CancellationToken>()).Returns([]);

        await Sweep(SystemWithTenants(("a", "db-a"), ("b", "db-b")), indexes)
            .RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null);

        await Assert.That(_created.Select(j => (string)j.Args[0])).IsEquivalentTo(new[] { "b" });
    }

    [Test]
    public async Task CkImportJob_EntersTheGateAroundTheImport()
    {
        var steps = new List<string>();
        var gate = Substitute.For<ICkImportIndexUpgradeGate>();
        var held = Substitute.For<IDisposable>();
        held.When(h => h.Dispose()).Do(_ => steps.Add("released"));
        gate.EnterAsync("tenant-1", Arg.Any<CancellationToken>()).Returns(_ =>
        {
            steps.Add("entered");
            return Task.FromResult(held);
        });
        var cache = Substitute.For<IDistributedCacheService>();
        cache.GetCacheStreamByIdAsync("tenant-1", "key-1").Returns(_ => new CacheStream
            { Stream = new MemoryStream("{}"u8.ToArray()), ContentType = "application/json", FileName = "k.json" });
        var command = Substitute.For<IImportCkModelCommand>();
        command.ImportAsync("tenant-1", Arg.Any<string>(), Arg.Any<CancellationToken?>())
            .Returns(_ =>
            {
                steps.Add("imported");
                return Task.CompletedTask;
            });
        var job = new ImportModelJob(Substitute.For<ILogger<ImportModelJob>>(), Substitute.For<ISystemContext>(), cache,
            Substitute.For<ICompressionService>(), command, Substitute.For<IImportRtModelCommand>(),
            [TimeSpan.Zero, TimeSpan.Zero], gate);

        await job.ImportCkAsync("tenant-1", "key-1", null);
        await job.ImportCkBatchAsync("tenant-1", ["key-1"], null);

        await Assert.That(string.Join(",", steps)).IsEqualTo("entered,imported,released,entered,imported,released");
    }

    private IndexUpgradeScheduler Scheduler() =>
        new(_jobClient, _coordination, Options.Create(_options), NullLogger<IndexUpgradeScheduler>.Instance);

    private IndexUpgradeSweepJob Sweep(ISystemContext systemContext, ITenantIndexInspector inspector) =>
        new(systemContext, inspector, Scheduler(), _coordination, Options.Create(_options),
            NullLogger<IndexUpgradeSweepJob>.Instance);

    private static ISystemContext SystemWithTenants(params (string TenantId, string DatabaseName)[] tenants)
    {
        var systemContext = Substitute.For<ISystemContext>();
        systemContext.TenantId.Returns("system");
        systemContext.DatabaseName.Returns("db-system");
        systemContext.IsSystemTenantExistingAsync().Returns(true);
        systemContext.GetAdminSessionAsync().Returns(_ => Substitute.For<IOctoAdminSession>());
        var resultSet = Substitute.For<IResultSet<OctoTenant>>();
        resultSet.Items.Returns(tenants.Select(t => new OctoTenant(t.TenantId, t.DatabaseName)).ToList());
        systemContext.GetAllTenantsAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<int?>(), Arg.Any<int?>())
            .Returns(resultSet);
        return systemContext;
    }

    private static TenantManagementConsumer Consumer(IIndexUpgradeScheduler scheduler) =>
        new(NullLogger<TenantManagementConsumer>.Instance, Options.Create(new OctoBotServicesOptions()),
            new NoJobCreator(), Substitute.For<ICkMethodOperationTenantPurge>(),
            Substitute.For<ITenantLifecycleStore>(), scheduler);

    private static IDistributedContext<T> Context<T>(T message) where T : class
    {
        var context = Substitute.For<IDistributedContext<T>>();
        context.Message.Returns(message);
        return context;
    }

    private sealed class NoJobCreator : IJobCreatorService
    {
        public void CreateJobs(string instancePrefix, string tenantId)
        {
        }

        public void DeleteJobs(string instancePrefix, string tenantId)
        {
        }
    }
}
