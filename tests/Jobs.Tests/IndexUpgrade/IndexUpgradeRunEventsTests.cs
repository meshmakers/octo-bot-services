using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.IndexUpgrade;

/// <summary>
///     AB#9709 — the run events reach Dash0 at the shipped level (Warn): one start and one end line per sweep run with
///     result, tenant count and duration; one end line per tenant job with result, status and duration; nothing per index
///     at Warning or above, and no event for a deferral Hangfire retries.
/// </summary>
public class IndexUpgradeRunEventsTests
{
    private const string TenantId = "tenant-a";
    private const string DatabaseName = "db-tenant-a";

    private readonly InMemoryIndexUpgradeCoordination _coordination = new();
    private readonly FakeTenantIndexes _indexes = new();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly ListLogger<IndexUpgradeJob> _jobLog = new();
    private readonly ListLogger<IndexUpgradeSweepJob> _sweepLog = new();
    private readonly IndexUpgradeOptions _options = new()
    {
        Schedule = IndexUpgradeSchedule.Immediate,
        RetryDelay = TimeSpan.FromSeconds(7),
        PollInterval = TimeSpan.FromMilliseconds(20)
    };

    public IndexUpgradeRunEventsTests()
    {
        _systemContext.TenantId.Returns("system");
        _systemContext.DatabaseName.Returns("db-system");
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _systemContext.GetAdminSessionAsync().Returns(_ => Substitute.For<IOctoAdminSession>());
        _systemContext.TryFindTenantContextAsync(TenantId).Returns(_tenant);
        var resultSet = Substitute.For<IResultSet<OctoTenant>>();
        resultSet.Items.Returns([new OctoTenant(TenantId, DatabaseName)]);
        _systemContext.GetAllTenantsAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<int?>(), Arg.Any<int?>())
            .Returns(resultSet);
        _tenant.TenantId.Returns(TenantId);
        _tenant.DatabaseName.Returns(DatabaseName);
        _tenant.GetAdminSessionAsync().Returns(_ => Substitute.For<IOctoAdminSession>());
    }

    [Test]
    public async Task TenantJob_Completed_EmitsOneEndEventAtWarning()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        EngineUpgrades("RtEntity_A");

        await Job().UpgradeTenantAsync(TenantId, "test", null, null);

        var end = await SingleTenantEndAsync(LogLevel.Warning);
        await Assert.That(end).StartsWith("IndexUpgrade tenant ended: Completed, ");
        await Assert.That(end).Contains($"tenant '{TenantId}' ended Succeeded after ");
        await Assert.That(end).Contains(" ms (test)");
        await AssertNoIndexNameAtWarningAsync();
    }

    [Test]
    public async Task TenantJob_Incomplete_EmitsOneEndEventAtWarning_IndexNamesOnlyBelow()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _indexes.AddLegacy(DatabaseName, "RtEntity_B", "B_1", "attributes.name");
        EngineUpgrades("RtEntity_B"); // the build of RtEntity_A fails in the engine (logged, not thrown)

        await Job().UpgradeTenantAsync(TenantId, "test", null, null);

        var end = await SingleTenantEndAsync(LogLevel.Warning);
        await Assert.That(end).StartsWith("IndexUpgrade tenant ended: Incomplete, ");
        await Assert.That(end).Contains("ended Incomplete");
        await Assert.That(end).Contains("legacy indexes 2 -> 1");
        await AssertNoIndexNameAtWarningAsync();
        var names = _jobLog.Entries.Single(e => e.Message.Contains("legacy indexes left:"));
        await Assert.That(names.Level).IsEqualTo(LogLevel.Information);
        await Assert.That(names.Message).Contains("RtEntity_A.A_1");
    }

    [Test]
    public async Task TenantJob_Failed_EmitsOneEndEventAtError()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        _tenant.UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Job().UpgradeTenantAsync(TenantId, "test", null, null));

        var end = await SingleTenantEndAsync(LogLevel.Error);
        await Assert.That(end).StartsWith("IndexUpgrade tenant ended: Failed, ");
        await Assert.That(end).Contains("ended Failed");
        await Assert.That(end).EndsWith(": boom");
    }

    [Test]
    public async Task TenantJob_DeferredForARetry_EmitsNoEndEvent()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        using var other = _coordination.TryAcquireGlobalSlot(1);

        await Assert.ThrowsAsync<IndexUpgradeDeferredException>(() =>
            Job().UpgradeTenantAsync(TenantId, "test", null, null));

        await Assert.That(_jobLog.Events.Where(e => e.EventId == IndexUpgradeRunEvents.TenantEnded.Id)).IsEmpty();
        await Assert.That(_jobLog.Entries.Where(e => e.Level >= LogLevel.Warning)).IsEmpty();
    }

    [Test]
    public async Task TenantJob_StartLine_StaysBelowWarning()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");
        EngineUpgrades("RtEntity_A");

        await Job().UpgradeTenantAsync(TenantId, "test", null, null);

        var started = _jobLog.Entries.Single(e => e.Message.Contains($"tenant '{TenantId}' started"));
        await Assert.That(started.Level).IsEqualTo(LogLevel.Information);
        await Assert.That(_jobLog.Entries.Count(e => e.Level >= LogLevel.Warning)).IsEqualTo(1);
    }

    [Test]
    public async Task Sweep_Completed_EmitsStartAndEndAtWarning_WithTenantCountAndDuration()
    {
        _indexes.AddLegacy(DatabaseName, "RtEntity_A", "A_1", "attributes.name");

        await Sweep(_indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null);

        var (start, end) = await SweepEventsAsync(LogLevel.Warning);
        await Assert.That(start).IsEqualTo("IndexUpgrade run started: sweep (after-deploy) looks at every tenant");
        await Assert.That(end).StartsWith("IndexUpgrade run ended: Completed, sweep (after-deploy) done after ");
        await Assert.That(end).Contains("2 tenant(s) checked, 1 with legacy indexes the engine upgrades, 1 job(s) enqueued, 0 not checked");
        await Assert.That(_sweepLog.Entries.Count(e => e.Level >= LogLevel.Warning)).IsEqualTo(2);
    }

    [Test]
    public async Task Sweep_TenantNotChecked_EndsIncomplete()
    {
        var inspector = Substitute.For<ITenantIndexInspector>();
        inspector.FindLegacyIndexesAsync(DatabaseName, null, Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<LegacyIndex>>>(_ => throw new InvalidOperationException("broken"));
        inspector.FindLegacyIndexesAsync("db-system", null, Arg.Any<CancellationToken>()).Returns([]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sweep(inspector).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null));

        var (_, end) = await SweepEventsAsync(LogLevel.Warning);
        await Assert.That(end).StartsWith("IndexUpgrade run ended: Incomplete, sweep (after-deploy) done after ");
        await Assert.That(end).Contains("2 tenant(s) checked, 0 with legacy indexes the engine upgrades, 0 job(s) enqueued, 1 not checked");
    }

    [Test]
    public async Task Sweep_TenantListUnreadable_EndsFailedAtError()
    {
        _systemContext.GetAllTenantsAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<int?>(), Arg.Any<int?>())
            .Returns<Task<IResultSet<OctoTenant>>>(_ => throw new InvalidOperationException("no tenants"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Sweep(_indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null));

        var (_, end) = await SweepEventsAsync(LogLevel.Error);
        await Assert.That(end).StartsWith("IndexUpgrade run ended: Failed, sweep (after-deploy) after ");
        await Assert.That(end).EndsWith(": no tenants");
    }

    [Test]
    public async Task Sweep_SwitchedOff_EmitsNoRunEvent()
    {
        _options.MaxConcurrent = 0;

        await Sweep(_indexes).RunAsync(IndexUpgradeConstants.TriggerAfterDeploy, null, null);

        await Assert.That(_sweepLog.Entries.Where(e => e.Level >= LogLevel.Warning)).IsEmpty();
    }

    private void EngineUpgrades(params string[] collections) =>
        _tenant.UpgradeTiebreakIndexesAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                foreach (var collection in collections)
                {
                    _indexes.UpgradeOneCollection(DatabaseName, collection);
                }

                return Task.CompletedTask;
            });

    private async Task<string> SingleTenantEndAsync(LogLevel level)
    {
        var ends = _jobLog.Events.Where(e => e.EventId == IndexUpgradeRunEvents.TenantEnded.Id).ToList();
        await Assert.That(ends).Count().IsEqualTo(1);
        await Assert.That(ends[0].Level).IsEqualTo(level);
        return ends[0].Message;
    }

    private async Task<(string Start, string End)> SweepEventsAsync(LogLevel endLevel)
    {
        var starts = _sweepLog.Events.Where(e => e.EventId == IndexUpgradeRunEvents.RunStarted.Id).ToList();
        var ends = _sweepLog.Events.Where(e => e.EventId == IndexUpgradeRunEvents.RunEnded.Id).ToList();
        await Assert.That(starts).Count().IsEqualTo(1);
        await Assert.That(ends).Count().IsEqualTo(1);
        await Assert.That(starts[0].Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(ends[0].Level).IsEqualTo(endLevel);
        return (starts[0].Message, ends[0].Message);
    }

    private async Task AssertNoIndexNameAtWarningAsync()
    {
        foreach (var (_, message) in _jobLog.Entries.Where(e => e.Level >= LogLevel.Warning))
        {
            await Assert.That(message).DoesNotContain("A_1");
            await Assert.That(message).DoesNotContain("B_1");
        }
    }

    private IndexUpgradeJob Job() =>
        new(_jobLog, _systemContext, _indexes, _indexes, _coordination, new RecordingRunStore(),
            Options.Create(_options));

    private IndexUpgradeSweepJob Sweep(ITenantIndexInspector inspector)
    {
        var jobClient = Substitute.For<IBackgroundJobClient>();
        var created = 0;
        jobClient.Create(Arg.Any<Job>(), Arg.Any<IState>()).Returns(_ => $"job-{Interlocked.Increment(ref created)}");
        var scheduler = new IndexUpgradeScheduler(jobClient, _coordination, Options.Create(_options),
            NullLogger<IndexUpgradeScheduler>.Instance);
        return new IndexUpgradeSweepJob(_systemContext, inspector, _indexes, scheduler, _coordination,
            Options.Create(_options), _sweepLog);
    }
}
