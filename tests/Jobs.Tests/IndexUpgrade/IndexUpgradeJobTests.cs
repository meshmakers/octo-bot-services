using Hangfire;
using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Microsoft.Extensions.Logging;
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
        // AB#9322: only managed indexes are left (both collections are roots of Available models).
        await Assert.That(record.Error).DoesNotContain("model not Available");
        await Assert.That(record.Leftovers!.ManagedIndexes).IsEqualTo(1);
        await Assert.That(record.Leftovers.ModelNotAvailableIndexes).IsEqualTo(0);
        await Assert.That(record.Leftovers.OrphanedIndexes).IsEqualTo(0);
        await Assert.That(record.Warning).IsNull();
    }

    [Test]
    public async Task OnlyIndexesOfAModelNotAvailable_Octosystem_SucceedsWithAWarning_WithoutTheEngine()
    {
        // AB#9322, test-2 octosystem: the retired Monitoring-2.1.0 is ResolveFailed; its 4 legacy indexes stay.
        AddMonitoringLeftovers();
        var logger = new ListLogger<IndexUpgradeJob>();

        await NewJob(logger: logger).UpgradeTenantAsync(TenantId, "test", null, null);

        await _tenant.DidNotReceiveWithAnyArgs().UpgradeTiebreakIndexesAsync(default!, default);
        var record = _runs.Last[TenantId];
        await Assert.That(record.Status).IsEqualTo(IndexUpgradeRunStatus.Succeeded);
        await Assert.That(record.Error).IsNull();
        await Assert.That(record.LegacyIndexesBefore).IsEqualTo(4);
        await Assert.That(record.LegacyIndexesAfter).IsEqualTo(4);
        await Assert.That(record.Leftovers!.ManagedIndexes).IsEqualTo(0);
        await Assert.That(record.Leftovers.ModelNotAvailableIndexes).IsEqualTo(4);
        await Assert.That(record.Leftovers.OrphanedIndexes).IsEqualTo(0);
        await Assert.That(record.Leftovers.ModelsNotAvailable).Count().IsEqualTo(1);
        var model = record.Leftovers.ModelsNotAvailable[0];
        await Assert.That((model.ModelId, model.State, model.IndexCount)).IsEqualTo(("Monitoring-2.1.0", "ResolveFailed", 4));
        await Assert.That(record.Warning).Contains("model not Available: Monitoring-2.1.0 ResolveFailed (4 indexes)");
        var end = logger.Entries.Single(e => e.Message.Contains("ended Succeeded"));
        await Assert.That(end.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(end.Message).Contains("managed 0, model not Available 4, orphaned 0");
        await Assert.That(end.Message).Contains("Monitoring-2.1.0 ResolveFailed");
    }

    [Test]
    public async Task OnlyOrphanedIndexes_Mrotest_SucceedsWithAWarning_WithoutTheEngine()
    {
        // AB#9322, test-2 mrotest: RtEntity_SystemQuery was left by the System 1.x -> 2.0.0 migration.
        AddSystemQueryLeftovers();
        var logger = new ListLogger<IndexUpgradeJob>();

        await NewJob(logger: logger).UpgradeTenantAsync(TenantId, "test", null, null);

        await _tenant.DidNotReceiveWithAnyArgs().UpgradeTiebreakIndexesAsync(default!, default);
        var record = _runs.Last[TenantId];
        await Assert.That(record.Status).IsEqualTo(IndexUpgradeRunStatus.Succeeded);
        await Assert.That(record.Leftovers!.OrphanedIndexes).IsEqualTo(2);
        await Assert.That(record.Leftovers.OrphanedCollections).IsEquivalentTo(new[] { "RtEntity_SystemQuery" });
        await Assert.That(record.Leftovers.OrphanedCollectionsTotal).IsEqualTo(1);
        await Assert.That(record.Leftovers.ModelsNotAvailable).IsEmpty();
        await Assert.That(record.Warning).Contains("orphaned (no collection root): RtEntity_SystemQuery (2 indexes)");
        await Assert.That(record.Warning).DoesNotContain("model not Available");
        var end = logger.Entries.Single(e => e.Message.Contains("ended Succeeded"));
        await Assert.That(end.Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(end.Message).DoesNotContain("model not Available:");
    }

    [Test]
    public async Task ManagedAndLeftovers_EngineUpgradesTheManaged_RunSucceedsWithAWarning()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        AddMonitoringLeftovers();
        AddSystemQueryLeftovers();

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        await _tenant.Received(1).UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>());
        var record = _runs.Last[TenantId];
        await Assert.That(record.Status).IsEqualTo(IndexUpgradeRunStatus.Succeeded);
        await Assert.That(record.CollectionsUpgraded).IsEquivalentTo(new[] { "RtEntity_A" });
        await Assert.That(record.LegacyIndexesBefore).IsEqualTo(7);
        await Assert.That(record.LegacyIndexesAfter).IsEqualTo(6);
        await Assert.That((record.Leftovers!.ManagedIndexes, record.Leftovers.ModelNotAvailableIndexes,
            record.Leftovers.OrphanedIndexes)).IsEqualTo((0, 4, 2));
        await Assert.That(record.Warning).Contains("Monitoring-2.1.0 ResolveFailed (4 indexes)");
        await Assert.That(record.Warning).Contains("RtEntity_SystemQuery (2 indexes)");
    }

    [Test]
    public async Task ManagedIndexLeftWithOtherLeftovers_EndsIncomplete_ErrorNamesOnlyTheManaged()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        AddMonitoringLeftovers();
        AddSystemQueryLeftovers();
        // The engine logs a failed build of RtEntity_A and returns normally.
        _tenant.UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        var record = _runs.Last[TenantId];
        await Assert.That(record.Status).IsEqualTo(IndexUpgradeRunStatus.Incomplete);
        await Assert.That(record.Error).Contains("1 legacy index(es) of collections the engine manages");
        await Assert.That(record.Error).Contains("RtEntity_A.A_1");
        await Assert.That(record.Error).DoesNotContain("Monitoring");
        await Assert.That(record.Error).DoesNotContain("SystemQuery");
        await Assert.That(record.Error).DoesNotContain("model not Available");
        await Assert.That((record.Leftovers!.ManagedIndexes, record.Leftovers.ModelNotAvailableIndexes,
            record.Leftovers.OrphanedIndexes)).IsEqualTo((1, 4, 2));
        await Assert.That(record.Warning).Contains("Monitoring-2.1.0 ResolveFailed");
    }

    [Test]
    public async Task CkMetadataUnreadable_EngineRunsAsBefore_LeftoversEndIncompleteUnclassified()
    {
        AddSystemQueryLeftovers();
        _indexes.FailRootReads = true;

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        // Not classified before: the engine runs (it skips the orphaned collection); not classified after: Incomplete.
        await _tenant.Received(1).UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>());
        var record = _runs.Last[TenantId];
        await Assert.That(record.Status).IsEqualTo(IndexUpgradeRunStatus.Incomplete);
        await Assert.That(record.Error).Contains("CK metadata could not be read");
        await Assert.That(record.Error).Contains("RtEntity_SystemQuery.SystemQuery_1");
        await Assert.That(record.Leftovers).IsNull();
    }

    /// <summary>octosystem on test-2: 4 legacy indexes of the ResolveFailed Monitoring-2.1.0.</summary>
    private void AddMonitoringLeftovers()
    {
        foreach (var collection in new[] { "RtEntity_MonitoringCkHealthMonitorConfig", "RtEntity_MonitoringMonitoredTenant" })
        {
            _indexes.AddLegacy(DatabaseName, collection, "SystemEntity_0", "attributes.a");
            _indexes.AddLegacy(DatabaseName, collection, "SystemEntity_2", "attributes.b");
            _indexes.SetRoot(DatabaseName, collection, "Monitoring-2.1.0", ModelState.ResolveFailed);
        }
    }

    /// <summary>mrotest on test-2: 2 legacy indexes of the orphaned RtEntity_SystemQuery.</summary>
    private void AddSystemQueryLeftovers()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_SystemQuery", "SystemQuery_1", "attributes.name");
        _indexes.AddLegacy(DatabaseName, "RtEntity_SystemQuery", "SystemQuery_2", "attributes.queryType");
        _indexes.MarkOrphaned(DatabaseName, "RtEntity_SystemQuery");
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

        using var importCancel = new CancellationTokenSource();
        var importEntered = gate.EnterAsync(TenantId, importCancel.Token);
        var importHandled = false;
        try
        {
            try
            {
                await WaitUntilAsync(() => _coordination.IsYieldRequested(TenantId));
                // The job's watcher sees the request and cancels the engine's token while collection A still builds.
                await _engine.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Assert.That(importEntered.IsCompleted).IsFalse(); // the import waits for the running collection
            }
            finally
            {
                releaseFirstCollection.TrySetResult();
            }

            var deferred = await Assert.ThrowsAsync<IndexUpgradeDeferredException>(() => upgrade);
            await Assert.That(deferred!.Delay).IsEqualTo(IndexUpgradeJob.AfterYieldDelay);
            await Assert.That(_runs.Last[TenantId].Status).IsEqualTo(IndexUpgradeRunStatus.Deferred);
            await Assert.That(_runs.Last[TenantId].CollectionsUpgraded).IsEquivalentTo(new[] { "RtEntity_A" });
            await Assert.That(_indexes.PendingCollections(DatabaseName))
                .IsEquivalentTo(new[] { "RtEntity_B", "RtEntity_C" });

            // The import gets the tenant and runs; meanwhile the upgrade is deferred again. importHandled is set only
            // once the import has its handle, and the handle must hold the tenant lease (AB#9317): a gate that let the
            // import run without the lease fails here.
            var hold = await importEntered.WaitAsync(TimeSpan.FromSeconds(10));
            importHandled = true;
            using (hold)
            {
                await Assert.That(_coordination.IsTenantHeld(TenantId)).IsTrue();
                await Assert.ThrowsAsync<IndexUpgradeDeferredException>(() =>
                    NewJob().UpgradeTenantAsync(TenantId, "test", null, null));
            }

            await Assert.That(_coordination.IsTenantHeld(TenantId)).IsFalse();
        }
        finally
        {
            if (!importHandled)
            {
                // A failed assertion: stop the import and release a lease it may hold.
                await importCancel.CancelAsync();
                try
                {
                    (await importEntered).Dispose();
                }
                catch (OperationCanceledException)
                {
                    // The import stopped waiting.
                }
            }
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
    public async Task FinalReadFails_RunIsNotReportedAsSuccess()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _indexes.FailCollectionReads = true;

        await NewJob().UpgradeTenantAsync(TenantId, "test", null, null);

        var record = _runs.Last[TenantId];
        await Assert.That(record.Status).IsEqualTo(IndexUpgradeRunStatus.Incomplete);
        await Assert.That(record.LegacyIndexesAfter).IsNull();
        await Assert.That(record.Error).Contains("not verified");
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

    private IndexUpgradeJob NewJob(IIndexUpgradeRunStore? store = null, ILogger<IndexUpgradeJob>? logger = null) =>
        new(logger ?? NullLogger<IndexUpgradeJob>.Instance, _systemContext, _indexes, _indexes, _coordination,
            store ?? _runs,
            Options.Create(_options));

    private async Task FakeEngineAsync(CancellationToken cancellationToken)
    {
        await using var signal = cancellationToken.Register(() => _engine.Cancelled.TrySetResult());
        // Like the engine: only collection roots of Available models (AB#9322).
        foreach (var collection in _indexes.PendingCollections(DatabaseName)
                     .Where(c => _indexes.IsManagedByEngine(DatabaseName, c)))
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
