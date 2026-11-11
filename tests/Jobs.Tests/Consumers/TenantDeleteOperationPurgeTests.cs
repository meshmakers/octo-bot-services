using System.Reflection;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.BotServices.SystemApi.v1;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
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
///     AB#8447: a deleted tenant takes its method operation records with it (<c>PreDeleteTenant</c> purges the store), a
///     purge failure never blocks the delete, and the job polling API <c>system/v1/jobs</c> is marked deprecated (wave
///     <c>BotJobs1</c>) while the tenant job routes are not.
/// </summary>
public class TenantDeleteOperationPurgeTests
{
    private readonly ICkMethodOperationTenantPurge _purge = Substitute.For<ICkMethodOperationTenantPurge>();
    private readonly RecordingJobCreator _jobCreator = new();

    [Test]
    public async Task PreDeleteTenant_RemovesTheRecurringJobsAndTheOperationRecords()
    {
        _purge.DeleteTenantAsync("gone", Arg.Any<CancellationToken>()).Returns(3L);

        await Consumer().ConsumeAsync(Context(new PreDeleteTenant("gone", Guid.NewGuid(), DateTime.UtcNow)));

        await Assert.That(_jobCreator.Deleted).IsEquivalentTo(new[] { "gone" });
        await _purge.Received(1).DeleteTenantAsync("gone", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PreDeleteTenant_PurgeFailure_DoesNotBlockTheDelete()
    {
        _purge.DeleteTenantAsync("gone", Arg.Any<CancellationToken>())
            .ThrowsAsync(new CkMethodOperationStoreException("database down"));

        await Consumer().ConsumeAsync(Context(new PreDeleteTenant("gone", Guid.NewGuid(), DateTime.UtcNow)));

        await Assert.That(_jobCreator.Deleted).IsEquivalentTo(new[] { "gone" });
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
            _purge);

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
