using MassTransit;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.DependencyInjection;
using Meshmakers.Octo.Common.DistributionEventHub.Configuration.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Configuration;

/// <summary>
///     AB#5867 (N7) — the bot service must run the scheduler on its own, instance-scoped queue.
/// </summary>
/// <remarks>
///     <para>
///         This host is the only one that consumes MassTransit's scheduling messages
///         (<c>AddHangfireMessageScheduler</c> in <c>Program.cs</c>). Before DistributionEventHub
///         0.2.2610.8006 the scheduler queue was the un-prefixed <c>hangfire</c> in every OctoMesh
///         instance, so on a broker shared by two instances (test-2: main and dev) RabbitMQ handed the
///         schedules round-robin to both bot services. Re-registering an unchanged schedule (every tenant
///         update, every DeployTriggers) first removes it from the own Hangfire database and then
///         publishes it again — and every second publish was stored by the OTHER instance: the job
///         vanished from the own database on every second re-registration.
///     </para>
///     <para>
///         🔴 The fix lives in DistributionEventHub, but whether this service gets it is decided by this
///         repository's package graph. Transitively NuGet resolves the lowest version the contracts
///         package was packed against, and the 0.2-dev image of 2026-10-07 shipped 0.2.2610.7002 although
///         the fix had been published. main (3.x) has the same transitive path, so the direct reference
///         and this guard were backported (prefix "main", the instance prefix of the main instance on the
///         shared test-2 broker). This test runs against the same graph (it references the host
///         project), so it turns red in CI when the host is built against a DistributionEventHub without
///         the instance-scoped scheduler queue.
///     </para>
/// </remarks>
public class HangfireSchedulerTopologyGuardTests
{
    [Test]
    public async Task TheSchedulerQueue_IsScopedToTheInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<DistributionEventHubOptions>(o =>
        {
            o.InstancePrefix = "main";
            o.BrokerHost = "localhost";
        });
        services.AddDistributionEventHub(config =>
        {
            config.UniqueServiceAddress = "BotService";
            config.AddHangfireMessageScheduler();
        });

        await using var provider = services.BuildServiceProvider();
        var queueName = provider.GetRequiredService<IOptions<HangfireEndpointOptions>>().Value.QueueName;

        await Assert.That(queueName).IsEqualTo("main-hangfire");
    }
}
