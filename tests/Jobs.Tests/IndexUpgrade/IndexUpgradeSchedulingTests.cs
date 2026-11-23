using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
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
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
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

        coordination.Received(1).EnqueueOnce("tenant-a", Arg.Any<Func<string>>());
    }

    [Test]
    public async Task Sweep_EnqueuesOnlyTenantsWithALegacyIndex()
    {
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-system", "RtEntity_S", "S_1", "attributes.name");
        indexes.AddLegacy("db-b", "RtEntity_B", "B_1", "attributes.name");
        var systemContext = SystemWithTenants(("a", "db-a"), ("b", "db-b"));

        await Sweep(systemContext, indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null);

        await Assert.That(_created.Select(j => (string)j.Args[0])).IsEquivalentTo(new[] { "system", "b" });
        await Assert.That(_created.Select(j => (string)j.Args[1]).Distinct())
            .IsEquivalentTo(new[] { IndexUpgradeConstants.TriggerAfterDeploy });
    }

    [Test]
    public async Task Sweep_EnqueuesOnlyTenantsWithAManagedLegacyIndex_AndCountsTheOthers()
    {
        // AB#9322: the two test-2 shapes get no job (no index_update_lock); a tenant with a managed index does.
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-system", "RtEntity_MonitoringMonitoredTenant", "SystemEntity_0", "attributes.a");
        indexes.SetRoot("db-system", "RtEntity_MonitoringMonitoredTenant", "Monitoring-2.1.0", ModelState.ResolveFailed);
        indexes.AddLegacy("db-a", "RtEntity_SystemQuery", "SystemQuery_1", "attributes.name");
        indexes.MarkOrphaned("db-a", "RtEntity_SystemQuery");
        indexes.AddLegacy("db-b", "RtEntity_SystemQuery", "SystemQuery_1", "attributes.name");
        indexes.MarkOrphaned("db-b", "RtEntity_SystemQuery");
        indexes.AddLegacy("db-b", "RtEntity_B", "B_1", "attributes.name");
        var logger = new ListLogger<IndexUpgradeSweepJob>();
        var sweep = new IndexUpgradeSweepJob(SystemWithTenants(("a", "db-a"), ("b", "db-b")), indexes, indexes,
            Scheduler(), _coordination, Options.Create(_options), logger);

        await sweep.RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null);

        await Assert.That(_created.Select(j => (string)j.Args[0])).IsEquivalentTo(new[] { "b" });
        var summary = logger.Messages.Single(m => m.Contains("sweep (after-deploy) done"));
        await Assert.That(summary).Contains("1 with legacy indexes the engine upgrades");
        await Assert.That(summary).Contains("2 tenant(s) with only leftovers no job can change");
        await Assert.That(summary).Contains("model not Available 1, orphaned 2");
    }

    [Test]
    public async Task Sweep_ModelBecomesAvailable_TheNextSweepEnqueuesTheTenant()
    {
        // AB#9322: a library update or fix makes the model Available; its indexes are managed from then on.
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-a", "RtEntity_MonitoringMonitoredTenant", "SystemEntity_0", "attributes.a");
        indexes.SetRoot("db-a", "RtEntity_MonitoringMonitoredTenant", "Monitoring-2.1.0", ModelState.ResolveFailed);
        var systemContext = SystemWithTenants(("a", "db-a"));

        await Sweep(systemContext, indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null);
        await Assert.That(_created).IsEmpty();

        indexes.SetRoot("db-a", "RtEntity_MonitoringMonitoredTenant", "Monitoring-2.2.0", ModelState.Available);
        await Sweep(systemContext, indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null);

        await Assert.That(_created.Select(j => (string)j.Args[0])).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task Sweep_CkMetadataUnreadable_EnqueuesTheTenantAsBefore()
    {
        var indexes = new FakeTenantIndexes { FailRootReads = true };
        indexes.AddLegacy("db-a", "RtEntity_SystemQuery", "SystemQuery_1", "attributes.name");
        indexes.MarkOrphaned("db-a", "RtEntity_SystemQuery");

        await Sweep(SystemWithTenants(("a", "db-a")), indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy,
            null, null);

        await Assert.That(_created.Select(j => (string)j.Args[0])).IsEquivalentTo(new[] { "a" });
    }

    [Test]
    public async Task Sweep_TwoReplicas_EnqueueEachTenantOnce()
    {
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-a", "RtEntity_A", "A_1", "attributes.name");
        // Both sweeps hold at their reading of db-a until both are there, so both reach the enqueue together.
        var barrier = new BarrierInspector(indexes, "db-a", 2);
        var systemContext = SystemWithTenants(("a", "db-a"));

        await Task.WhenAll(
            Task.Run(() => Sweep(systemContext, barrier, roots: indexes)
                .RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null)),
            Task.Run(() => Sweep(systemContext, barrier, roots: indexes)
                .RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null)))
            .WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(barrier.Arrived).IsEqualTo(2);
        await Assert.That(_created).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Sweep_WindowSwitchedOff_LooksAtNoTenant()
    {
        _options.Schedule = IndexUpgradeSchedule.Window;
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-a", "RtEntity_A", "A_1", "attributes.name");

        await Sweep(SystemWithTenants(("a", "db-a")), indexes).RunAsync(IndexUpgradeConstants.TriggerWindow, null, null);

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
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 18, 1, 0, 0, TimeSpan.Zero));

        await Sweep(SystemWithTenants(("a", "db-a")), indexes, clock)
            .RunAsync(IndexUpgradeConstants.TriggerWindow, null, null);

        await Assert.That(_coordination.WindowClosesAt).IsEqualTo(new DateTime(2026, 11, 18, 4, 0, 0, DateTimeKind.Utc));
        await Assert.That(_created).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Sweep_RetriedInsideAnOpenWindow_DoesNotExtendIt()
    {
        // The 01:00 occurrence failed and Hangfire retries it at 04:30: the window still ends at 05:00.
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = true;
        var occurrence = new DateTime(2026, 11, 18, 1, 0, 0, DateTimeKind.Utc);
        var clock = new FixedTimeProvider(new DateTimeOffset(occurrence.AddHours(3.5)));

        await Sweep(SystemWithTenants(), new FakeTenantIndexes(), clock)
            .RunAsync(IndexUpgradeConstants.TriggerWindow, null, Context(occurrence));

        await Assert.That(_coordination.WindowClosesAt).IsEqualTo(occurrence.AddHours(4));
    }

    [Test]
    public async Task Sweep_RetriedAfterItsWindow_OpensNoWindowAndEnqueuesNothing()
    {
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = true;
        var indexes = new FakeTenantIndexes();
        indexes.AddLegacy("db-a", "RtEntity_A", "A_1", "attributes.name");
        var occurrence = new DateTime(2026, 11, 18, 1, 0, 0, DateTimeKind.Utc);
        var clock = new FixedTimeProvider(new DateTimeOffset(occurrence.AddHours(5)));

        await Sweep(SystemWithTenants(("a", "db-a")), indexes, clock)
            .RunAsync(IndexUpgradeConstants.TriggerWindow, null, Context(occurrence));

        await Assert.That(_coordination.WindowClosesAt).IsNull();
        await Assert.That(_created).IsEmpty();
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
        var roots = new FakeTenantIndexes();
        roots.SetRoot("db-b", "RtEntity_B", "B-1.0.0", ModelState.Available);

        // The run checks every tenant, then fails so that Hangfire retries the unreadable one.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sweep(SystemWithTenants(("a", "db-a"), ("b", "db-b")), indexes, roots: roots)
                .RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null));

        await indexes.Received(1).FindLegacyIndexesAsync("db-a", null, Arg.Any<CancellationToken>());
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
        cache.GetCacheStreamByIdAsync("tenant-1", Arg.Any<string>()).Returns(_ => new CacheStream
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
        await job.ImportCkBatchAsync("tenant-1", ["key-1", "key-2"], null);

        // One hold around the single import, one hold around the whole batch.
        await Assert.That(string.Join(",", steps))
            .IsEqualTo("entered,imported,released,entered,imported,imported,released");
    }

    private IndexUpgradeScheduler Scheduler() =>
        new(_jobClient, _coordination, Options.Create(_options), NullLogger<IndexUpgradeScheduler>.Instance);

    private IndexUpgradeSweepJob Sweep(ISystemContext systemContext, ITenantIndexInspector inspector,
        TimeProvider? clock = null, ICkCollectionRootReader? roots = null) =>
        new(systemContext, inspector,
            roots ?? inspector as ICkCollectionRootReader ??
            throw new ArgumentException("pass the CK metadata", nameof(roots)),
            Scheduler(), _coordination, Options.Create(_options), NullLogger<IndexUpgradeSweepJob>.Instance, clock);

    /// <summary>A perform context of a job created at <paramref name="createdAt" /> (the recurring occurrence).</summary>
    private static PerformContext Context(DateTime createdAt) =>
        new(Substitute.For<JobStorage>(), Substitute.For<IStorageConnection>(),
            new BackgroundJob("sweep-1", Job.FromExpression<IIndexUpgradeSweepJob>(j =>
                j.RunAsync(IndexUpgradeConstants.TriggerWindow, null, null)), createdAt),
            Substitute.For<IJobCancellationToken>());

    /// <summary>Holds the reading of one database until <c>parties</c> callers wait there, then lets all through.</summary>
    private sealed class BarrierInspector(ITenantIndexInspector inner, string databaseName, int parties)
        : ITenantIndexInspector
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public int Arrived => Volatile.Read(ref _arrived);

        public async Task<IReadOnlyList<LegacyIndex>> FindLegacyIndexesAsync(string database,
            IReadOnlyCollection<string>? collectionNames = null, CancellationToken cancellationToken = default)
        {
            if (database == databaseName && Interlocked.Increment(ref _arrived) >= parties)
            {
                _all.TrySetResult();
            }

            if (database == databaseName)
            {
                await _all.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }

            return await inner.FindLegacyIndexesAsync(database, collectionNames, cancellationToken);
        }

        public Task<IReadOnlyList<(string Name, IReadOnlyList<string> Keys)>> ListIndexesAsync(string database,
            string collectionName, CancellationToken cancellationToken = default) =>
            inner.ListIndexesAsync(database, collectionName, cancellationToken);
    }

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
