using Meshmakers.Octo.Backend.BotServices;
using Meshmakers.Octo.Backend.BotServices.Consumers;
using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Methods.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;

public class DumpHashLifecycleTests
{
    private const string Tenant = "tenant-1";
    private static readonly DateTime TenantCreatedUtc = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly InMemoryDumpHashRegistry _registry = new();
    private readonly ITenantLifecycleStore _lifecycle = Substitute.For<ITenantLifecycleStore>();
    private readonly ICkMethodOperationTenantPurge _operationPurge = Substitute.For<ICkMethodOperationTenantPurge>();
    private readonly ICkMethodOperationTenantGeneration _generation =
        Substitute.For<ICkMethodOperationTenantGeneration>();

    [Test]
    public async Task DeletedTenant_LosesItsDumpEntries()
    {
        await RegisterAsync(Tenant, "a1", TenantCreatedUtc.AddDays(1));
        await RegisterAsync("tenant-2", "a1", TenantCreatedUtc.AddDays(1));
        Lifecycle(TenantLifecycleState.Deleting);

        await Consumer().ConsumeAsync(Context(new PosDeleteTenant(Tenant, Guid.NewGuid(), DateTime.UtcNow)));

        await Assert.That(_registry.Entries.Select(entry => entry.TenantId).ToArray())
            .IsEquivalentTo(new[] { "tenant-2" });
    }

    [Test]
    public async Task DeletedTenant_LosesItsDumpEntries_AlsoWhenTheOperationPurgeFails()
    {
        await RegisterAsync(Tenant, "a1", TenantCreatedUtc.AddDays(1));
        Lifecycle(TenantLifecycleState.Deleting);
        _operationPurge.DeleteTenantAsync(Tenant, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("store down"));

        await Consumer().ConsumeAsync(Context(new PosDeleteTenant(Tenant, Guid.NewGuid(), DateTime.UtcNow)));

        await Assert.That(_registry.Entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RestoredTenant_KeepsItsDumpEntries()
    {
        await RegisterAsync(Tenant, "a1", TenantCreatedUtc.AddDays(1));
        Lifecycle(TenantLifecycleState.Active);

        await Consumer().ConsumeAsync(Context(new PosDeleteTenant(Tenant, Guid.NewGuid(), DateTime.UtcNow)));

        await Assert.That(_registry.Entries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ReusedTenantId_DoesNotFindTheEarlierTenantsDump()
    {
        await RegisterAsync(Tenant, "earlier", TenantCreatedUtc.AddSeconds(-1));
        await RegisterAsync(Tenant, "current", TenantCreatedUtc.AddSeconds(1));
        _generation.GetStartUtcAsync(Tenant, Arg.Any<CancellationToken>()).Returns(TenantCreatedUtc);
        var registry = new TenantGenerationDumpHashRegistry(_registry, _generation);

        await Assert.That(await registry.FindAsync(Tenant, "earlier")).IsNull();
        await Assert.That(await registry.FindAsync(Tenant, "current")).IsNotNull();
    }

    [Test]
    public async Task UnknownGeneration_DoesNotFilter()
    {
        await RegisterAsync(Tenant, "legacy", TenantCreatedUtc.AddYears(-1));
        _generation.GetStartUtcAsync(Tenant, Arg.Any<CancellationToken>()).Returns((DateTime?)null);
        var registry = new TenantGenerationDumpHashRegistry(_registry, _generation);

        await Assert.That(await registry.FindAsync(Tenant, "legacy")).IsNotNull();
    }

    [Test]
    public async Task SetupOfAReusedTenantId_RemovesTheEarlierTenantsEntries()
    {
        await RegisterAsync(Tenant, "earlier", TenantCreatedUtc.AddSeconds(-1));
        await RegisterAsync(Tenant, "current", TenantCreatedUtc.AddSeconds(1));
        _generation.GetStartUtcAsync(Tenant, Arg.Any<CancellationToken>()).Returns(TenantCreatedUtc);

        var removedCount = await Cleanup().RunAsync(Tenant);

        await Assert.That(removedCount).IsEqualTo(1L);
        await Assert.That(_registry.Entries.Single().Sha256).IsEqualTo("current");
    }

    [Test]
    public async Task SetupCleanup_NeverThrows()
    {
        _generation.GetStartUtcAsync(Tenant, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("lifecycle store down"));

        await Assert.That(await Cleanup().RunAsync(Tenant)).IsEqualTo(0L);
    }

    private Task RegisterAsync(string tenantId, string sha256, DateTime dumpedAtUtc) =>
        _registry.RegisterAsync(new DumpHashEntry(tenantId, sha256, dumpedAtUtc, dumpedAtUtc.AddDays(365),
            $"{sha256}.tar.gz", 1, null, DumpHashSources.Dump));

    private DumpHashGenerationCleanup Cleanup() =>
        new(_generation, _registry, NullLogger<DumpHashGenerationCleanup>.Instance);

    private TenantManagementConsumer Consumer() =>
        new(NullLogger<TenantManagementConsumer>.Instance, Options.Create(new OctoBotServicesOptions()),
            new IgnoringJobCreator(), _operationPurge, _lifecycle, dumpHashRegistry: _registry);

    private void Lifecycle(TenantLifecycleState state) =>
        _lifecycle.GetAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantLifecycleRecord { TenantId = Tenant, State = state });

    private static IDistributedContext<T> Context<T>(T message) where T : class
    {
        var context = Substitute.For<IDistributedContext<T>>();
        context.Message.Returns(message);
        return context;
    }

    private sealed class IgnoringJobCreator : IJobCreatorService
    {
        public void CreateJobs(string instancePrefix, string tenantId)
        {
        }

        public void DeleteJobs(string instancePrefix, string tenantId)
        {
        }
    }
}
