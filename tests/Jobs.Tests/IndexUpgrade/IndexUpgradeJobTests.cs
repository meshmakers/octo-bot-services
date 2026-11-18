using Hangfire;
using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.IndexUpgrade;

/// <summary>
///     AB#9118 — the per-tenant index upgrade job with a fake engine (one collection per step, the token checked before
///     each collection as <c>UpgradeTiebreakIndexesAsync</c> does): idempotent, deferred instead of failed when it cannot
///     work now, yields to a CK import, honours the build window and the shutdown, writes the last-run record.
/// </summary>
public class IndexUpgradeJobTests
{
    private const string TenantId = "tenant-a";
    private const string DatabaseName = "db-tenant-a";

    private readonly InMemoryIndexUpgradeCoordination _coordination = new();
    private readonly FakeTenantIndexes _indexes = new();
    private readonly RecordingRunStore _runs = new();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IndexUpgradeOptions _options = new()
    {
        Schedule = IndexUpgradeSchedule.Immediate,
        RetryDelay = TimeSpan.FromSeconds(7),
        PollInterval = TimeSpan.FromMilliseconds(20)
    };

    /// <summary>Hooks of the fake engine.</summary>
    private readonly EngineHooks _engine = new();

    public IndexUpgradeJobTests()
    {
        _systemContext.TenantId.Returns("system");
        _systemContext.TryFindTenantContextAsync(TenantId).Returns(_tenant);
        _tenant.TenantId.Returns(TenantId);
        _tenant.DatabaseName.Returns(DatabaseName);
        _tenant.GetAdminSessionAsync().Returns(_ => Substitute.For<IOctoAdminSession>());
        _tenant.UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>())
            .Returns(call => FakeEngineAsync(call.Arg<CancellationToken>()));
    }

    [Test]
    public async Task TenantInTheNewShape_EndsAtOnce_WithoutCallingTheEngine()
    {
        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await _tenant.DidNotReceiveWithAnyArgs().UpgradeTiebreakIndexesAsync(default!, default);
        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.NothingToDo);
        await Assert.That(_runs.Last[TenantId].LegacyIndexesBefore).IsEqualTo(0);
    }

    [Test]
    public async Task LegacyIndexes_AreUpgraded_AndTheRecordListsTheCollections()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _indexes.AddLegacy(DatabaseName, "RtEntity_B", "B_1", "attributes.code");

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await _tenant.Received(1).UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>());
        var record = _runs.Last[TenantId];
        await Assert.That(record.Status).IsEqualTo(IndexUpgradeRunStatus.Succeeded);
        await Assert.That(record.LegacyIndexesBefore).IsEqualTo(2);
        await Assert.That(record.LegacyIndexesAfter).IsEqualTo(0);
        await Assert.That(record.CollectionsUpgraded).IsEquivalentTo(new[] { "RtEntity_A", "RtEntity_B" });
        await Assert.That(record.Trigger).IsEqualTo("test");
        await Assert.That(record.EndedAt).IsNotNull();
        await Assert.That(_runs.Written.Select(w => w.Status)).Contains(IndexUpgradeRunStatus.Running);
    }

    [Test]
    public async Task SecondRun_AfterAnUpgrade_BuildsNothing()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await _tenant.Received(1).UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>());
        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.NothingToDo);
    }

    [Test]
    public async Task AllGlobalSlotsHeld_DefersWithoutCallingTheEngine()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        using var other = _coordination.TryAcquireGlobalSlot(1);

        var deferred = await Assert.ThrowsAsync<IndexUpgradeDeferredException>(() =>
            NewJob().UpgradeTenantAsync(TenantId, "test", null, null));

        await Assert.That(deferred!.Delay).IsEqualTo(_options.RetryDelay);
        await Assert.That(deferred.Message).Contains("MaxConcurrent");
        await _tenant.DidNotReceiveWithAnyArgs().UpgradeTiebreakIndexesAsync(default!, default);
        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Deferred);
    }

    [Test]
    public async Task CkImportHoldsTheTenant_Defers()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        using var import = _coordination.TryAcquireTenant(TenantId, TimeSpan.Zero);

        await Assert.ThrowsAsync<IndexUpgradeDeferredException>(() => NewJob().UpgradeTenantAsync(TenantId, "test", null, null));

        await _tenant.DidNotReceiveWithAnyArgs().UpgradeTiebreakIndexesAsync(default!, default);
    }

    [Test]
    public async Task IndexUpdateLockHeldInTheEngine_DefersInsteadOfFailing()
    {
        // The CK import or UpdateIndexesAsync of another service holds index_update_lock: the engine waits 90 s and
        // throws a TimeoutException naming the lock.
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _tenant.UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new TimeoutException(
                "Could not acquire distributed lock 'index_update_lock' after 90 seconds. Lock has no expiry time set"));

        var deferred = await Assert.ThrowsAsync<IndexUpgradeDeferredException>(() =>
            NewJob().UpgradeTenantAsync(TenantId, "test", null, null));

        await Assert.That(deferred!.Delay).IsEqualTo(_options.RetryDelay);
        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Deferred);
        await Assert.That(_coordination.TryAcquireGlobalSlot(1)).IsNotNull(); // the slot was released
    }

    [Test]
    public async Task EngineFailure_FailsTheJob_AndRecordsTheError()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _tenant.UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewJob().UpgradeTenantAsync(TenantId, "test", null, null));

        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Failed);
        await Assert.That(_runs.Last[TenantId].Error).IsEqualTo("boom");
    }

    [Test]
    public async Task LegacyIndexesTheEngineDoesNotUpgrade_EndIncomplete_WithoutARetry()
    {
        // The engine upgrades only collections of Available models and logs (not throws) an index it cannot build; the
        // job sees the legacy index still there. A retry would change nothing: no exception, status Incomplete.
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _indexes.AddLegacy(DatabaseName, "RtEntity_B", "B_1", "attributes.name");
        _tenant.UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _indexes.UpgradeOneCollection(DatabaseName, "RtEntity_B");
                return Task.CompletedTask;
            });

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        var record = _runs.Last[TenantId];
        await Assert.That(record.Status).IsEqualTo(IndexUpgradeRunStatus.Incomplete);
        await Assert.That(record.Error).Contains("RtEntity_A.A_1");
        await Assert.That(record.LegacyIndexesAfter).IsEqualTo(1);
        await Assert.That(record.CollectionsUpgraded).IsEquivalentTo(new[] { "RtEntity_B" });
    }

    [Test]
    public async Task CkImportArrivesDuringTheUpgrade_UpgradeStopsBeforeTheNextCollection_ImportRuns_UpgradeContinues()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _indexes.AddLegacy(DatabaseName, "RtEntity_B", "B_1", "attributes.name");
        _indexes.AddLegacy(DatabaseName, "RtEntity_C", "C_1", "attributes.name");
        var gate = new CkImportIndexUpgradeGate(_coordination, Options.Create(_options),
            NullLogger<CkImportIndexUpgradeGate>.Instance);
        var firstCollectionStarted = new TaskCompletionSource();
        var releaseFirstCollection = new TaskCompletionSource();
        _engine.BeforeCollection = async (collection, _) =>
        {
            if (collection == "RtEntity_A")
            {
                firstCollectionStarted.TrySetResult();
                // An index build already started finishes (the engine checks the token between collections only).
                await releaseFirstCollection.Task;
            }
        };

        var upgrade = NewJob().UpgradeTenantAsync(TenantId, "test", null, null);
        await firstCollectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var importEntered = gate.EnterAsync(TenantId, CancellationToken.None);
        await WaitUntilAsync(() => _coordination.IsYieldRequested(TenantId));
        // The job's watcher sees the request and cancels the engine's token while collection A still builds.
        await _engine.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(importEntered.IsCompleted).IsFalse(); // the import waits for the running collection
        releaseFirstCollection.SetResult();

        var deferred = await Assert.ThrowsAsync<IndexUpgradeDeferredException>(() => upgrade);
        await Assert.That(deferred!.Delay).IsEqualTo(IndexUpgradeJob.AfterYieldDelay);
        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Deferred);
        await Assert.That(_runs.Last[TenantId].CollectionsUpgraded).IsEquivalentTo(new[] { "RtEntity_A" });
        await Assert.That(_indexes.PendingCollections(DatabaseName)).IsEquivalentTo(new[] { "RtEntity_B", "RtEntity_C" });

        // The import gets the tenant and runs; meanwhile the upgrade is deferred again.
        using (await importEntered.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            await Assert.ThrowsAsync<IndexUpgradeDeferredException>(() =>
                NewJob().UpgradeTenantAsync(TenantId, "test", null, null));
        }

        // After the import the upgrade continues with the rest.
        _engine.BeforeCollection = (_, _) => Task.CompletedTask;
        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Succeeded);
        await Assert.That(_runs.Last[TenantId].CollectionsUpgraded).IsEquivalentTo(new[] { "RtEntity_B", "RtEntity_C" });
        await Assert.That(_indexes.PendingCollections(DatabaseName)).IsEmpty();
    }

    [Test]
    public async Task ServiceShutdownDuringTheUpgrade_RecordsInterrupted_AndRethrowsTheCancellation()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _indexes.AddLegacy(DatabaseName, "RtEntity_B", "B_1", "attributes.name");
        using var shutdown = new CancellationTokenSource();
        _engine.BeforeCollection = (collection, _) =>
        {
            if (collection == "RtEntity_A")
            {
                shutdown.Cancel();
            }

            return Task.CompletedTask;
        };
        var token = Substitute.For<IJobCancellationToken>();
        token.ShutdownToken.Returns(shutdown.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => NewJob().UpgradeTenantAsync(TenantId, "test", token, null));

        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Interrupted);
        // Hangfire runs the job again; the re-run completes the rest only.
        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);
        await Assert.That(_runs.Last[TenantId].CollectionsUpgraded).IsEquivalentTo(new[] { "RtEntity_B" });
    }

    [Test]
    public async Task WindowSchedule_WindowSwitchedOff_BuildsNothing()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = false;
        _coordination.WindowClosesAt = DateTime.UtcNow.AddHours(1);

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await _tenant.DidNotReceiveWithAnyArgs().UpgradeTiebreakIndexesAsync(default!, default);
        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Deferred);
        await Assert.That(_runs.Last[TenantId].Error).Contains("switched off");
    }

    [Test]
    public async Task WindowSchedule_WindowClosed_BuildsNothing()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = true;
        _coordination.WindowClosesAt = DateTime.UtcNow.AddMinutes(-1);

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await _tenant.DidNotReceiveWithAnyArgs().UpgradeTiebreakIndexesAsync(default!, default);
        await Assert.That(_runs.Last[TenantId].Error).Contains("closed");
    }

    [Test]
    public async Task WindowClosesDuringTheUpgrade_NoFurtherCollectionStarts_RestFollowsInTheNextWindow()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _indexes.AddLegacy(DatabaseName, "RtEntity_B", "B_1", "attributes.name");
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = true;
        _coordination.WindowClosesAt = DateTime.UtcNow.AddSeconds(2);
        _engine.BeforeCollection = async (collection, _) =>
        {
            if (collection == "RtEntity_A")
            {
                // The build of A outlasts the window: the job's timer cancels the engine's token meanwhile.
                await _engine.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Deferred);
        await Assert.That(_runs.Last[TenantId].Error).Contains("next window");
        await Assert.That(_indexes.PendingCollections(DatabaseName)).IsEquivalentTo(new[] { "RtEntity_B" });
    }

    [Test]
    public async Task WindowSwitchedOff_RecordsTheLegacyCountAsUnknown()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _options.Schedule = IndexUpgradeSchedule.Window;
        _options.Window.Enabled = false;

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await Assert.That(_runs.Last[TenantId].LegacyIndexesAfter).IsNull();
        await Assert.That(_indexes.ReadCount).IsEqualTo(0);
    }

    [Test]
    public async Task MaxConcurrentZero_BuildsNothing()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _options.MaxConcurrent = 0;

        // Ends (no retry loop while switched off) without a build.
        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Deferred);
        await Assert.That(_runs.Last[TenantId].Error).Contains("switched off");
        await _tenant.DidNotReceiveWithAnyArgs().UpgradeTiebreakIndexesAsync(default!, default);
    }

    [Test]
    public async Task SlotHeld_ResolvesAndInspectsNothing()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        using var other = _coordination.TryAcquireGlobalSlot(1);

        await Assert.ThrowsAsync<IndexUpgradeDeferredException>(() =>
            NewJob().UpgradeTenantAsync(TenantId, "test", null, null));

        await _systemContext.DidNotReceiveWithAnyArgs().TryFindTenantContextAsync(default!);
        await Assert.That(_indexes.ReadCount).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownTenant_EndsWithoutWork()
    {
        _systemContext.TryFindTenantContextAsync("gone").Returns((ITenantContext?)null);

        await NewJob().UpgradeTenantAsync("gone", "test", null, null);

        await Assert.That(_runs.Last).IsEmpty();
    }

    [Test]
    public async Task RunStoreFailure_DoesNotStopTheUpgrade()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        var store = Substitute.For<IIndexUpgradeRunStore>();
        store.SaveAsync(Arg.Any<IndexUpgradeRunRecord>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("system database down"));

        await NewJob(store).UpgradeTenantAsync(TenantId, "test", null, null);

        await Assert.That(_indexes.PendingCollections(DatabaseName)).IsEmpty();
    }

    private IndexUpgradeJob NewJob(IIndexUpgradeRunStore? store = null) =>
        new(NullLogger<IndexUpgradeJob>.Instance, _systemContext, _indexes, _coordination, store ?? _runs,
            Options.Create(_options));

    private async Task FakeEngineAsync(CancellationToken cancellationToken)
    {
        await using var signal = cancellationToken.Register(() => _engine.Cancelled.TrySetResult());
        foreach (var collection in _indexes.PendingCollections(DatabaseName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _engine.BeforeCollection(collection, cancellationToken);
            _indexes.UpgradeOneCollection(DatabaseName, collection);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Condition not reached");
            }

            await Task.Delay(10);
        }
    }

    private sealed class EngineHooks
    {
        /// <summary>Runs before the engine works on a collection (the collection name is passed).</summary>
        public Func<string, CancellationToken, Task> BeforeCollection { get; set; } = (_, _) => Task.CompletedTask;

        /// <summary>Set when the job cancels the engine's token (yield, window, shutdown).</summary>
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
