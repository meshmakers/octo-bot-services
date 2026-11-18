using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Mongo;
using Hangfire.States;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.HangfireSetup;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.IntegrationTests;

/// <summary>
///     AB#9118 — the paging index upgrade job against the real engine and a real Hangfire job storage on MongoDB:
///     clean run, re-run, run that stopped after the new index was built, attach of a tenant with legacy indexes
///     (<c>PosCreateTenant</c> -> job -> new keys), the global limit across two Hangfire servers on one storage, and a
///     server stopped during a job.
/// </summary>
[NotInParallel]
public class IndexUpgradeIntegrationTests
{
    private const string FieldName = "SimpleField";
    private const string FieldPath = "attributes.simpleField";
    private static readonly string[] LegacyKeys = ["ckTypeId", FieldPath, "rtState"];
    private static readonly string[] TiebreakKeys = ["ckTypeId", FieldPath, "_id", "rtState"];

    private ExportEngineFixture _fixture = null!;
    private IndexUpgradeMongoClient _mongo = null!;
    private MongoClient _hangfireClient = null!;

    [Before(Test)]
    public async Task SetUpAsync()
    {
        _fixture = await ExportEngineFixture.GetAsync();
        var configuration = _fixture.Provider.GetRequiredService<IOptions<OctoSystemConfiguration>>();
        _mongo = new IndexUpgradeMongoClient(configuration);
        _hangfireClient = new MongoClient(IndexUpgradeMongoClient.CreateUrl(configuration.Value));
    }

    [After(Test)]
    public void TearDown()
    {
        _hangfireClient.Dispose();
    }

    [Test]
    public async Task CleanRun_ReRun_AndARunStoppedAfterTheBuild_EndInTheSameIndexes()
    {
        await WithTenantAsync("ixclean", async (tenant, collection) =>
        {
            await MakeLegacyAsync(collection);
            var storage = NewStorage();
            var runs = new MongoIndexUpgradeRunStore(_mongo);

            // Clean run: the legacy index is replaced by the tiebreak index.
            await NewJob(storage, runs).UpgradeTenantAsync(tenant.TenantId, "test", null, null);
            var afterClean = await DescribeAsync(collection);
            await Assert.That(afterClean).Contains(i => i.Keys.SequenceEqual(TiebreakKeys));
            await Assert.That(afterClean).DoesNotContain(i => i.Keys.SequenceEqual(LegacyKeys));
            await Assert.That(await Inspector().FindLegacyIndexesAsync(tenant.DatabaseName)).IsEmpty();
            var record = await runs.GetAsync(tenant.TenantId);
            await Assert.That(record!.Status).IsEqualTo(IndexUpgradeRunStatus.Succeeded);
            await Assert.That(record.CollectionsUpgraded).Contains(collection.CollectionNamespace.CollectionName);

            // Re-run: nothing to do, the indexes stay as they are.
            await NewJob(storage, runs).UpgradeTenantAsync(tenant.TenantId, "test", null, null);
            await Assert.That((await runs.GetAsync(tenant.TenantId))!.Status)
                .IsEqualTo(IndexUpgradeRunStatus.NothingToDo);
            await Assert.That(await DescribeAsync(collection)).IsEquivalentTo(afterClean);

            // A run stopped after the new index was built but before the legacy one was dropped: both exist.
            var legacyName = await MakeLegacyAsync(collection);
            await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "_id", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = legacyName + "_id" }));
            await NewJob(storage, runs).UpgradeTenantAsync(tenant.TenantId, "test", null, null);

            await Assert.That(await DescribeAsync(collection)).IsEquivalentTo(afterClean);
        });
    }

    [Test]
    public async Task LegacyIndexOfACollectionTheEngineDoesNotManage_RunEndsIncomplete_TheOthersAreUpgraded()
    {
        // Local stack check 2026-11-18: collections of models that are not Available (e.g. RtEntity_SystemStreamDataArchive
        // in a tenant without an Available StreamData model) keep their legacy indexes, because the engine upgrades only
        // the collection roots of Available models. A collection the engine has no CK type for behaves the same.
        await WithTenantAsync("ixincompl", async (tenant, collection) =>
        {
            await MakeLegacyAsync(collection);
            var unmanaged = _mongo.GetDatabase(tenant.DatabaseName).GetCollection<BsonDocument>("RtEntity_NotManaged");
            await unmanaged.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
                new BsonDocument { { "ckTypeId", 1 }, { "attributes.other", 1 }, { "rtState", 1 } },
                new CreateIndexOptions { Name = "NotManaged_1" }));
            var runs = new MongoIndexUpgradeRunStore(_mongo);

            await NewJob(NewStorage(), runs).UpgradeTenantAsync(tenant.TenantId, "test", null, null);

            var record = await runs.GetAsync(tenant.TenantId);
            await Assert.That(record!.Status).IsEqualTo(IndexUpgradeRunStatus.Incomplete);
            await Assert.That(record.Error).Contains("RtEntity_NotManaged.NotManaged_1");
            await Assert.That(record.LegacyIndexesAfter).IsEqualTo(1);
            await Assert.That(record.CollectionsUpgraded).Contains(collection.CollectionNamespace.CollectionName);
            await Assert.That(await DescribeAsync(collection)).Contains(i => i.Keys.SequenceEqual(TiebreakKeys));
        });
    }

    [Test]
    public async Task AttachedTenantWithLegacyIndexes_PosCreateTenant_EnqueuesTheJob_WhichUpgradesThem()
    {
        await WithTenantAsync("ixattach", async (tenant, collection) =>
        {
            await MakeLegacyAsync(collection);
            var systemContext = _fixture.SystemContext;
            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.DetachChildTenantAsync(session, tenant.TenantId);
                await session.CommitTransactionAsync();
            }

            using (var session = await systemContext.GetAdminSessionAsync())
            {
                session.StartTransaction();
                await systemContext.AttachChildTenantAsync(session, tenant.DatabaseName, tenant.TenantId);
                await session.CommitTransactionAsync();
            }

            // The attach kept the legacy index (a CK import never rebuilds one).
            await Assert.That(await Inspector().FindLegacyIndexesAsync(tenant.DatabaseName)).IsNotEmpty();

            var storage = NewStorage();
            var coordination = new HangfireIndexUpgradeCoordination(() => storage);
            var options = Options.Create(new IndexUpgradeOptions
                { Schedule = IndexUpgradeSchedule.Immediate, StartDelay = TimeSpan.Zero });
            var scheduler = new IndexUpgradeScheduler(new BackgroundJobClient(storage), coordination, options,
                NullLogger<IndexUpgradeScheduler>.Instance);
            var consumer = new TenantManagementConsumer(NullLogger<TenantManagementConsumer>.Instance,
                Options.Create(new OctoBotServicesOptions()), new NoJobCreator(),
                Substitute.For<ICkMethodOperationTenantPurge>(), Substitute.For<ITenantLifecycleStore>(), scheduler);

            // Every replica receives the event.
            var context = Substitute.For<IDistributedContext<PosCreateTenant>>();
            context.Message.Returns(new PosCreateTenant(tenant.TenantId, Guid.NewGuid(), DateTime.UtcNow));
            await consumer.ConsumeAsync(context);
            await consumer.ConsumeAsync(context);

            var jobId = coordination.GetJobId(tenant.TenantId);
            await Assert.That(jobId).IsNotNull();
            await Assert.That(storage.GetMonitoringApi().EnqueuedCount(IndexUpgradeConstants.Queue)).IsEqualTo(1);

            var job = NewJob(storage, new MongoIndexUpgradeRunStore(_mongo));
            using (NewServer(storage, "attach", job))
            {
                await WaitForStateAsync(storage, jobId!, SucceededState.StateName, TimeSpan.FromMinutes(2));
            }

            await Assert.That(await Inspector().FindLegacyIndexesAsync(tenant.DatabaseName)).IsEmpty();
            await Assert.That(await DescribeAsync(collection)).Contains(i => i.Keys.SequenceEqual(TiebreakKeys));
        });
    }

    [Test]
    public async Task TwoServersOnOneStorage_TwoTenantsDue_NeverTwoRebuilds_TheWaitingOneIsScheduledNotFailed()
    {
        var storage = NewStorage();
        var engine = new SlowFakeEngine(TimeSpan.FromMilliseconds(500)) { HoldFirstBuild = true };
        var systemContext = engine.SystemContextFor("tenant-1", "tenant-2");
        var options = new IndexUpgradeOptions
        {
            Schedule = IndexUpgradeSchedule.Immediate, RetryDelay = TimeSpan.FromSeconds(1),
            PollInterval = TimeSpan.FromMilliseconds(200)
        };
        var coordination = new HangfireIndexUpgradeCoordination(() => storage);
        var scheduler = new IndexUpgradeScheduler(new BackgroundJobClient(storage), coordination,
            Options.Create(options), NullLogger<IndexUpgradeScheduler>.Instance);
        var job1 = scheduler.Enqueue("tenant-1", "test").JobId;
        var job2 = scheduler.Enqueue("tenant-2", "test").JobId;

        IIndexUpgradeJob NewFakeJob() => new IndexUpgradeJob(NullLogger<IndexUpgradeJob>.Instance, systemContext,
            engine, coordination, new NoRunStore(), Options.Create(options));

        using (NewServer(storage, "replica-a", NewFakeJob()))
        using (NewServer(storage, "replica-b", NewFakeJob()))
        {
            try
            {
                // The first build holds until the other tenant's job was deferred, so the contention is certain.
                await engine.Started.Task.WaitAsync(TimeSpan.FromMinutes(1));
                await WaitForHistoryAsync(storage, [job1, job2], ScheduledState.StateName, TimeSpan.FromMinutes(1));
            }
            finally
            {
                engine.ReleaseFirstBuild();
            }

            await WaitForStateAsync(storage, job1, SucceededState.StateName, TimeSpan.FromMinutes(2));
            await WaitForStateAsync(storage, job2, SucceededState.StateName, TimeSpan.FromMinutes(2));
        }

        await Assert.That(engine.MaxConcurrent).IsEqualTo(1);
        await Assert.That(engine.Upgraded).IsEquivalentTo(new[] { "tenant-1", "tenant-2" });
        // The waiting job was never Failed: the failed set is empty, and the only Failed entries in the history are the
        // states the deferral filter elected away (Hangfire records them as traversed), each followed by Scheduled.
        var monitoring = storage.GetMonitoringApi();
        await Assert.That(monitoring.FailedCount()).IsEqualTo(0);
        var history = new[] { job1, job2 }.SelectMany(id => monitoring.JobDetails(id).History).ToList();
        await Assert.That(history.Select(h => h.StateName)).Contains(ScheduledState.StateName);
        // A deferral uses no attempt of AutomaticRetry: neither job has a retry count.
        using (var connection = storage.GetConnection())
        {
            foreach (var id in new[] { job1, job2 })
            {
                await Assert.That(connection.GetJobParameter(id, "RetryCount") ?? "0").IsEqualTo("0");
            }
        }

        var traversedFailures = history.Where(h => h.StateName == FailedState.StateName).ToList();
        await Assert.That(traversedFailures.All(h => h.Data.TryGetValue("ExceptionType", out var type) &&
                                                     type.Contains(nameof(IndexUpgradeDeferredException)))).IsTrue();
    }

    [Test]
    public async Task ServerStoppedDuringAJob_TheJobIsNotLeftProcessing_AndAnotherServerCompletesIt()
    {
        var storage = NewStorage();
        var engine = new SlowFakeEngine(Timeout.InfiniteTimeSpan);
        var systemContext = engine.SystemContextFor("tenant-r");
        var options = new IndexUpgradeOptions
        {
            Schedule = IndexUpgradeSchedule.Immediate, RetryDelay = TimeSpan.FromSeconds(1),
            PollInterval = TimeSpan.FromMilliseconds(200)
        };
        var coordination = new HangfireIndexUpgradeCoordination(() => storage);
        var runs = new RecordingRuns();
        var scheduler = new IndexUpgradeScheduler(new BackgroundJobClient(storage), coordination,
            Options.Create(options), NullLogger<IndexUpgradeScheduler>.Instance);
        var jobId = scheduler.Enqueue("tenant-r", "test").JobId;

        IIndexUpgradeJob NewFakeJob() => new IndexUpgradeJob(NullLogger<IndexUpgradeJob>.Instance, systemContext,
            engine, coordination, runs, Options.Create(options));

        using (NewServer(storage, "replica-old", NewFakeJob()))
        {
            await engine.Started.Task.WaitAsync(TimeSpan.FromMinutes(1));
        } // rolling update: the server shuts down, the job sees the shutdown token

        await Assert.That(runs.Statuses).Contains(IndexUpgradeRunStatus.Interrupted);

        engine.BuildTime = TimeSpan.FromMilliseconds(100);
        using (NewServer(storage, "replica-new", NewFakeJob()))
        {
            await WaitForStateAsync(storage, jobId, SucceededState.StateName, TimeSpan.FromMinutes(2));
        }

        await Assert.That(runs.Statuses.Last()).IsEqualTo(IndexUpgradeRunStatus.Succeeded);
        await Assert.That(engine.Upgraded).IsEquivalentTo(new[] { "tenant-r" });
    }

    private IndexUpgradeJob NewJob(JobStorage storage, IIndexUpgradeRunStore runs) =>
        new(NullLogger<IndexUpgradeJob>.Instance, _fixture.SystemContext, Inspector(),
            new HangfireIndexUpgradeCoordination(() => storage), runs,
            Options.Create(new IndexUpgradeOptions
                { Schedule = IndexUpgradeSchedule.Immediate, PollInterval = TimeSpan.FromMilliseconds(200) }));

    private TenantIndexInspector Inspector() => new(_mongo);

    private MongoStorage NewStorage()
    {
        var name = $"hangfire-ixup-{Guid.NewGuid():N}"[..30];
        var storageOptions = HangfireMongoStorageSetup.CreateStorageOptions();
        HangfireMongoStorageSetup.PrepareSchema(_hangfireClient.GetDatabase(name), storageOptions);
        return new MongoStorage(_hangfireClient, name, storageOptions);
    }

    private static BackgroundJobServer NewServer(JobStorage storage, string name, IIndexUpgradeJob job) =>
        new(new BackgroundJobServerOptions
        {
            ServerName = name,
            Queues = [IndexUpgradeConstants.Queue],
            WorkerCount = 1,
            SchedulePollingInterval = TimeSpan.FromMilliseconds(500),
            ShutdownTimeout = TimeSpan.FromSeconds(30),
            Activator = new SingleJobActivator(job)
        }, storage);

    private static async Task WaitForHistoryAsync(JobStorage storage, string[] jobIds, string state, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var monitoring = storage.GetMonitoringApi();
            if (jobIds.Any(id => monitoring.JobDetails(id).History.Any(h => h.StateName == state)))
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"None of the jobs {string.Join(", ", jobIds)} was {state}");
    }

    private static async Task WaitForStateAsync(JobStorage storage, string jobId, string state, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        string? current = null;
        while (DateTime.UtcNow < until)
        {
            using (var connection = storage.GetConnection())
            {
                current = connection.GetStateData(jobId)?.Name;
            }

            if (current == state)
            {
                return;
            }

            if (current == FailedState.StateName)
            {
                break;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Job {jobId} did not reach {state}; it is {current ?? "unknown"}");
    }

    private async Task WithTenantAsync(string prefix, Func<ITenantContext, IMongoCollection<BsonDocument>, Task> test)
    {
        var systemContext = _fixture.SystemContext;
        var tenantId = $"{prefix}{Guid.NewGuid():N}"[..20];
        using (var session = await systemContext.GetAdminSessionAsync())
        {
            session.StartTransaction();
            await systemContext.CreateChildTenantAsync(session, tenantId, tenantId);
            await session.CommitTransactionAsync();
        }

        try
        {
            var tenant = await systemContext.GetChildTenantContextAsync(tenantId);
            await tenant.ImportCkModelAsync(Model($"IxUp{prefix}"));
            var collection = await FindCollectionWithKeysAsync(tenant.DatabaseName, TiebreakKeys);
            await test(tenant, collection);
        }
        finally
        {
            using var cleanup = await systemContext.GetAdminSessionAsync();
            cleanup.StartTransaction();
            await systemContext.DropChildTenantAsync(cleanup, tenantId);
            await cleanup.CommitTransactionAsync();
        }
    }

    private async Task<IMongoCollection<BsonDocument>> FindCollectionWithKeysAsync(string databaseName, string[] keys)
    {
        var database = _mongo.GetDatabase(databaseName);
        foreach (var name in await (await database.ListCollectionNamesAsync()).ToListAsync())
        {
            var collection = database.GetCollection<BsonDocument>(name);
            var indexes = await (await collection.Indexes.ListAsync()).ToListAsync();
            if (indexes.Any(i => i["key"].AsBsonDocument.Names.SequenceEqual(keys)))
            {
                return collection;
            }
        }

        throw new InvalidOperationException($"No collection of '{databaseName}' has an index with the keys {string.Join(",", keys)}");
    }

    /// <summary>Replaces the tiebreak index with the legacy shape an engine before AB#8960 built. Returns its name.</summary>
    private static async Task<string> MakeLegacyAsync(IMongoCollection<BsonDocument> collection)
    {
        var tiebreak = (await (await collection.Indexes.ListAsync()).ToListAsync())
            .Single(i => i["key"].AsBsonDocument.Names.SequenceEqual(TiebreakKeys));
        var tiebreakName = tiebreak["name"].AsString;
        var legacyName = tiebreakName[..^"_id".Length];
        await collection.Indexes.DropOneAsync(tiebreakName);
        await collection.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(
            new BsonDocument { { "ckTypeId", 1 }, { FieldPath, 1 }, { "rtState", 1 } },
            new CreateIndexOptions { Name = legacyName }));
        return legacyName;
    }

    private static async Task<List<(string Name, string[] Keys)>> DescribeAsync(IMongoCollection<BsonDocument> collection) =>
        (await (await collection.Indexes.ListAsync()).ToListAsync())
        .Select(i => (i["name"].AsString, i["key"].AsBsonDocument.Names.ToArray())).ToList();

    private static CkCompiledModelRoot Model(string modelName)
    {
        var modelVersion = $"{modelName}-1.0.0";
        return new CkCompiledModelRoot
        {
            ModelId = new CkModelId(modelVersion),
            Dependencies = [SystemCkIds.CkModelId],
            Attributes =
            [
                new CkAttributeDto { AttributeId = new CkAttributeId(FieldName), ValueType = AttributeValueTypesDto.String }
            ],
            Types =
            [
                new()
                {
                    TypeId = new CkTypeId("SimpleType"),
                    DerivedFromCkTypeId = SystemCkIds.CkEntityTypeId,
                    IsCollectionRoot = true,
                    Attributes =
                    [
                        new()
                        {
                            CkAttributeId = new CkId<CkAttributeId>($"{modelVersion}/{FieldName}"),
                            AttributeName = FieldName
                        }
                    ],
                    Indexes =
                    [
                        new() { IndexType = IndexTypeDto.Ascending, Fields = [new() { AttributePaths = [FieldName] }] }
                    ]
                }
            ]
        };
    }

    /// <summary>Creates every job of the server from one instance.</summary>
    private sealed class SingleJobActivator(IIndexUpgradeJob job) : JobActivator
    {
        public override object ActivateJob(Type jobType) => job;
    }

    /// <summary>
    ///     The engine side of tenants with one legacy index each: the build takes <see cref="BuildTime" /> (the job's
    ///     token cancels it) and records how many builds run at the same time.
    /// </summary>
    private sealed class SlowFakeEngine(TimeSpan buildTime) : ITenantIndexInspector
    {
        private readonly ConcurrentDictionary<string, bool> _legacy = new();
        private int _running;
        private int _maxConcurrent;

        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _builds;

        public TimeSpan BuildTime { get; set; } = buildTime;

        /// <summary>The first build waits for <see cref="ReleaseFirstBuild" /> (bounded) before it ends.</summary>
        public bool HoldFirstBuild { get; init; }

        public void ReleaseFirstBuild() => _releaseFirst.TrySetResult();
        public int MaxConcurrent => _maxConcurrent;
        public ConcurrentBag<string> Upgraded { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ISystemContext SystemContextFor(params string[] tenantIds)
        {
            var systemContext = Substitute.For<ISystemContext>();
            systemContext.TenantId.Returns("system");
            foreach (var tenantId in tenantIds)
            {
                _legacy[tenantId] = true;
                var tenant = Substitute.For<ITenantContext>();
                tenant.TenantId.Returns(tenantId);
                tenant.DatabaseName.Returns(tenantId);
                tenant.GetAdminSessionAsync().Returns(_ => Substitute.For<IOctoAdminSession>());
                tenant.UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>())
                    .Returns(call => BuildAsync(tenantId, call.Arg<CancellationToken>()));
                systemContext.TryFindTenantContextAsync(tenantId).Returns(tenant);
            }

            return systemContext;
        }

        private async Task BuildAsync(string tenantId, CancellationToken cancellationToken)
        {
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _maxConcurrent, running);
            Started.TrySetResult();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HoldFirstBuild && Interlocked.Increment(ref _builds) == 1)
                {
                    await _releaseFirst.Task.WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
                }

                await Task.Delay(BuildTime, cancellationToken);
                _legacy[tenantId] = false;
                Upgraded.Add(tenantId);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public Task<IReadOnlyList<LegacyIndex>> FindLegacyIndexesAsync(string databaseName,
            IReadOnlyCollection<string>? collectionNames = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LegacyIndex>>(_legacy.GetValueOrDefault(databaseName)
                ? [new LegacyIndex("RtEntity_X", "X_1", ["ckTypeId", "attributes.x", "rtState"])]
                : []);

        public Task<IReadOnlyList<(string Name, IReadOnlyList<string> Keys)>> ListIndexesAsync(string databaseName,
            string collectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<(string Name, IReadOnlyList<string> Keys)>>([]);

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    private sealed class NoRunStore : IIndexUpgradeRunStore
    {
        public Task SaveAsync(IndexUpgradeRunRecord record, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IndexUpgradeRunRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IndexUpgradeRunRecord?>(null);
    }

    private sealed class RecordingRuns : IIndexUpgradeRunStore
    {
        private readonly ConcurrentQueue<IndexUpgradeRunStatus> _statuses = new();

        public IReadOnlyList<IndexUpgradeRunStatus> Statuses => _statuses.ToArray();

        public Task SaveAsync(IndexUpgradeRunRecord record, CancellationToken cancellationToken = default)
        {
            _statuses.Enqueue(record.Status);
            return Task.CompletedTask;
        }

        public Task<IndexUpgradeRunRecord?> GetAsync(string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IndexUpgradeRunRecord?>(null);
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
