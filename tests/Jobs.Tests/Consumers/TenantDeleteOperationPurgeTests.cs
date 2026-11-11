using System.Reflection;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.BotServices.SystemApi.v1;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Methods.Operations;
using Meshmakers.Octo.Services.RestDeprecation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SystemJobsController = Meshmakers.Octo.Backend.BotServices.SystemApi.v1.Controllers.JobsController;
using TenantJobsController = Meshmakers.Octo.Backend.BotServices.TenantApi.v1.Controllers.JobsController;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Consumers;

/// <summary>
///     AB#8447: a deleted tenant takes its method operation records with it (<c>PosDeleteTenant</c>, sent after the
///     database drop, purges the store when the tenant carries the Deleting tombstone), a restore or detach (same events, no
///     tombstone) keeps them, <c>PreDeleteTenant</c> only removes the recurring jobs, a purge failure never blocks the
///     delete, and the job polling API <c>system/v1/jobs</c> is marked deprecated (wave
///     <c>BotJobs1</c>) while the tenant job routes are not.
/// </summary>
public class TenantDeleteOperationPurgeTests
{
    private readonly ICkMethodOperationTenantPurge _purge = Substitute.For<ICkMethodOperationTenantPurge>();
    private readonly RecordingJobCreator _jobCreator = new();
    private readonly ITenantLifecycleStore _lifecycle = Substitute.For<ITenantLifecycleStore>();

    public TenantDeleteOperationPurgeTests()
    {
        Lifecycle("gone", TenantLifecycleState.Deleting);
    }

    [Test]
    public async Task PreDeleteTenant_RemovesTheRecurringJobsButNotTheOperationRecords()
    {
        // Sent before the delete is committed: a delete that fails after it must keep the records.
        await Consumer().ConsumeAsync(Context(new PreDeleteTenant("gone", Guid.NewGuid(), DateTime.UtcNow)));

        await Assert.That(_jobCreator.Deleted).IsEquivalentTo(new[] { "gone" });
        await _purge.DidNotReceiveWithAnyArgs().DeleteTenantAsync(default!, default);
    }

    [Test]
    public async Task PosDeleteTenant_WithDeletingTombstone_RemovesTheOperationRecords()
    {
        _purge.DeleteTenantAsync("gone", Arg.Any<CancellationToken>()).Returns(3L);

        await Consumer().ConsumeAsync(Context(new PosDeleteTenant("gone", Guid.NewGuid(), DateTime.UtcNow)));

        await _purge.Received(1).DeleteTenantAsync("gone", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PosDeleteTenant_TenantIdInOtherCasing_ReadsTheNormalizedLifecycleRecord()
    {
        await Consumer().ConsumeAsync(Context(new PosDeleteTenant("Gone", Guid.NewGuid(), DateTime.UtcNow)));

        await _purge.Received(1).DeleteTenantAsync("Gone", Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(TenantLifecycleState.Active)]
    [Arguments(TenantLifecycleState.Creating)]
    [Arguments(TenantLifecycleState.Failed)]
    public async Task PosDeleteTenant_WithoutDeletingTombstone_KeepsTheOperationRecords(TenantLifecycleState state)
    {
        // A restore drops and re-attaches the tenant (and a detach moves it): the same event, but the tenant stays. The
        // running restore operation must keep its record (local E2E of AB#8447 lost it before this gate).
        Lifecycle("restored", state);

        await Consumer().ConsumeAsync(Context(new PosDeleteTenant("restored", Guid.NewGuid(), DateTime.UtcNow)));

        await _purge.DidNotReceiveWithAnyArgs().DeleteTenantAsync(default!, default);
    }

    [Test]
    public async Task PosDeleteTenant_WithoutLifecycleRecord_KeepsTheOperationRecords()
    {
        await Consumer().ConsumeAsync(Context(new PosDeleteTenant("legacy", Guid.NewGuid(), DateTime.UtcNow)));

        await _purge.DidNotReceiveWithAnyArgs().DeleteTenantAsync(default!, default);
    }

    [Test]
    public async Task PosDeleteTenant_LifecycleLookupFails_KeepsTheRecordsAndDoesNotBlock()
    {
        _lifecycle.GetAsync("broken", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database down"));

        await Consumer().ConsumeAsync(Context(new PosDeleteTenant("broken", Guid.NewGuid(), DateTime.UtcNow)));

        await _purge.DidNotReceiveWithAnyArgs().DeleteTenantAsync(default!, default);
    }

    [Test]
    public async Task PosDeleteTenant_PurgeFailure_DoesNotBlockTheDelete()
    {
        _purge.DeleteTenantAsync("gone", Arg.Any<CancellationToken>())
            .ThrowsAsync(new CkMethodOperationStoreException("database down"));

        // Does not throw: the delete has happened, the records expire with their retention.
        await Consumer().ConsumeAsync(Context(new PosDeleteTenant("gone", Guid.NewGuid(), DateTime.UtcNow)));

        await _purge.Received(1).DeleteTenantAsync("gone", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TenantUpdate_DoesNotPurge()
    {
        // A restore replaces the tenant database, not the tenant; tenant updates never touch the operation records.
        await Consumer().ConsumeAsync(Context(new PosUpdateTenant("kept", Guid.NewGuid(), DateTime.UtcNow)));

        await _purge.DidNotReceiveWithAnyArgs().DeleteTenantAsync(default!, default);
    }

    [Test]
    public async Task SystemJobsApi_IsDeprecatedThroughTheBotJobsWave()
    {
        var marker = typeof(SystemJobsController).GetCustomAttribute<OctoRestDeprecatedAttribute>();

        await Assert.That(marker).IsNotNull();
        await Assert.That(marker!.Wave).IsEqualTo(BotRestDeprecation.JobsWave1);
        await Assert.That(string.IsNullOrEmpty(marker.Since)).IsTrue();
        await Assert.That(string.IsNullOrEmpty(marker.Sunset)).IsTrue();
        await Assert.That(marker.Link).StartsWith("https://");
        await Assert.That(typeof(TenantJobsController).GetCustomAttribute<OctoRestDeprecatedAttribute>()).IsNull();
    }

    private TenantManagementConsumer Consumer() =>
        new(NullLogger<TenantManagementConsumer>.Instance, Options.Create(new OctoBotServicesOptions()), _jobCreator,
            _purge, _lifecycle);

    private void Lifecycle(string tenantId, TenantLifecycleState state) =>
        _lifecycle.GetAsync(tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantLifecycleRecord { TenantId = tenantId, State = state });

    private static IDistributedContext<T> Context<T>(T message) where T : class
    {
        var context = Substitute.For<IDistributedContext<T>>();
        context.Message.Returns(message);
        return context;
    }

    private sealed class RecordingJobCreator : IJobCreatorService
    {
        public List<string> Deleted { get; } = [];

        public void CreateJobs(string instancePrefix, string tenantId)
        {
        }

        public void DeleteJobs(string instancePrefix, string tenantId) => Deleted.Add(tenantId);
    }
}
