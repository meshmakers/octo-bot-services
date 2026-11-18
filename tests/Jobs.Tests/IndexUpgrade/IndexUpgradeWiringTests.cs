using System.Reflection;
using System.Runtime.CompilerServices;
using Hangfire;
using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.IndexUpgrade;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.IndexUpgrade;

/// <summary>
///     AB#9118 — the CK import gate (AC 6) and the tenant-created trigger (AC 5) reach production through optional
///     constructor parameters. These tests resolve both classes from the service registrations the host uses
///     (<c>AddOctoJobs</c> + <c>AddOctoIndexUpgrade</c>, host services as substitutes) and pin the call in
///     <c>Program.cs</c>, the convention of <c>TenantAuthorizationWiringTests</c>.
/// </summary>
public class IndexUpgradeWiringTests
{
    [Test]
    public async Task ImportModelJob_FromTheHostRegistrations_GetsTheImportGate()
    {
        await using var provider = HostServices();

        var job = provider.GetRequiredService<IImportModelJob>();

        await Assert.That(Field<ICkImportIndexUpgradeGate>(job, "_indexUpgradeGate")).IsNotNull();
    }

    [Test]
    public async Task TenantManagementConsumer_FromTheHostRegistrations_GetsTheScheduler()
    {
        await using var provider = HostServices();

        // The event hub creates consumers from the container the same way.
        var consumer = ActivatorUtilities.CreateInstance<TenantManagementConsumer>(provider);

        await Assert.That(Field<IIndexUpgradeScheduler>(consumer, "_indexUpgradeScheduler")).IsNotNull();
    }

    [Test]
    public async Task HostRegistrations_ContainTheStartupRegistration()
    {
        await using var provider = HostServices();

        await Assert.That(provider.GetServices<IHostedService>().OfType<IndexUpgradeStartupService>()).IsNotEmpty();
    }

    [Test]
    public async Task Program_RegistersTheIndexUpgrade_AndItsServer()
    {
        var program = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "BotServices", "Program.cs"));

        await Assert.That(program).Contains("builder.Services.AddOctoIndexUpgrade(builder.Configuration);");
        await Assert.That(program).Contains("options.Queues = [IndexUpgradeConstants.Queue];");
    }

    private static ServiceProvider HostServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.Configure<OctoSystemConfiguration>(_ => { });
        services.Configure<OctoBotServicesOptions>(_ => { });
        // Services of the engine, the event hub and the host that the classes under test need.
        services.AddSingleton(Substitute.For<ISystemContext>());
        services.AddSingleton(Substitute.For<IDistributedCacheService>());
        services.AddSingleton(Substitute.For<IImportRtModelCommand>());
        services.AddSingleton(Substitute.For<ICkSerializer>());
        services.AddSingleton(Substitute.For<IBackgroundJobClient>());
        services.AddSingleton(Substitute.For<IRecurringJobManager>());
        services.AddSingleton(Substitute.For<ICkMethodOperationTenantPurge>());
        services.AddSingleton(Substitute.For<ITenantLifecycleStore>());
        services.AddSingleton<IJobCreatorService>(new NoJobCreator());
        services.AddSingleton(Substitute.For<ICompressionService>());
        services.AddOctoJobs();
        services.AddOctoIndexUpgrade(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    private static T? Field<T>(object instance, string name) where T : class =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T;

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));

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
