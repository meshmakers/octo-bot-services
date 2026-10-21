using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     AB#7249 — bot-services wired no retry store and no <see cref="FailedTenantRegistry"/>, so a tenant whose
///     setup (or start) failed during a MongoDB primary election was never retried (staging-1, 2026-10-21, the
///     same gap as the communication controller). The shared <see cref="TenantSetupRetryTracker"/> is the retry
///     that needs no MongoDB; the backoff, health and metrics are covered in octo-common-services.
/// </summary>
public class TenantSetupRetryWiringTests
{
    private const string TenantId = "sbeg";

    [Test]
    public async Task FailedSetup_IsQueuedInTheInjectedTracker_AndRetriedWithoutARetryStore()
    {
        var systemContext = Substitute.For<ISystemContext>();
        systemContext.IsSystemTenantExistingAsync().Returns(true);
        var lookups = 0;
        systemContext.FindTenantContextAsync(TenantId).Returns(_ =>
        {
            lookups++;
            return Task.FromException<ITenantContext>(new TimeoutException("selecting a server. Mode: Primary"));
        });
        var tracker = new TenantSetupRetryTracker(options: new TenantSetupRetryOptions
        {
            InitialDelay = TimeSpan.FromTicks(1), MaxDelay = TimeSpan.FromTicks(1)
        });

        DefaultConfigurationCreatorService CreateSut() => new(
            NullLogger<DefaultConfigurationCreatorService>.Instance,
            Substitute.For<IDiagnosticsService>(),
            systemContext,
            new NoOpJobCreatorService(),
            Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>(),
            Options.Create(new OctoBotServicesOptions()),
            new FailedTenantRegistry(),
            tracker);

        await Assert.ThrowsAsync<TimeoutException>(() => CreateSut().SetupAsync(TenantId));
        await Assert.That(tracker.Snapshot().Select(e => e.TenantId)).IsEquivalentTo([TenantId]);

        // the background service's next tick resolves a fresh creator; the queue is shared
        await Task.Delay(20);
        await CreateSut().RetryFailedTenantsAsync();

        await Assert.That(lookups).IsEqualTo(2);
        await Assert.That(tracker.Snapshot().Single().Attempts).IsEqualTo(2);
    }

    private sealed class NoOpJobCreatorService : IJobCreatorService
    {
        public void CreateJobs(string instancePrefix, string tenantId)
        {
        }

        public void DeleteJobs(string instancePrefix, string tenantId)
        {
        }
    }
}
