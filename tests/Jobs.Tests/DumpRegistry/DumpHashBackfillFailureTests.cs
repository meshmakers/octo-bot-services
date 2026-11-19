using System.Globalization;
using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;

public class DumpHashBackfillFailureTests : IDisposable
{
    private const string Tenant = "tenant-1";
    private static readonly DateTime DumpNamedAtUtc = new(2026, 11, 1, 8, 15, 30, DateTimeKind.Utc);

    private readonly ArtifactTestEnvironment _artifacts = new("k1");
    private readonly InMemoryDumpHashRegistry _registry = new();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly byte[] _dumpContent = RandomNumberGenerator.GetBytes(10_000);

    public DumpHashBackfillFailureTests()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _systemContext.FindTenantContextAsync(Tenant).Returns(Substitute.For<ITenantContext>());
    }

    public void Dispose()
    {
        _artifacts.Dispose();
    }

    [Test]
    public async Task DumpNamedInTheFuture_IsNotRegistered()
    {
        await StoreDumpAsync(DumpName(DumpNamedAtUtc.AddDays(10)));

        var registeredCount = await BackfillJob(new FixedTimeProvider(new DateTimeOffset(DumpNamedAtUtc))).Run(null);

        await Assert.That(registeredCount).IsEqualTo(0);
        await Assert.That(_registry.Entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExhaustedRegistryWrites_FailTheJobAfterTheScan_AndTheOtherDumpsAreRegistered()
    {
        await StoreDumpAsync(DumpName(DumpNamedAtUtc));
        await StoreDumpAsync(DumpName(DumpNamedAtUtc.AddMinutes(1)));
        _registry.FailingRegistrations = 3;

        await Assert.That(async () =>
                await BackfillJob(new FixedTimeProvider(new DateTimeOffset(DumpNamedAtUtc.AddDays(1)))).Run(null))
            .Throws<JobFailedException>();

        await Assert.That(_registry.Entries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RetentionIsCheckedForEachDump_WhenTheScanCrossesAnExpiry()
    {
        await StoreDumpAsync(DumpName(DumpNamedAtUtc));
        await StoreDumpAsync(DumpName(DumpNamedAtUtc));
        var expiryUtc = DumpNamedAtUtc.AddDays(365);
        var clock = new AdvancingTimeProvider(new DateTimeOffset(expiryUtc.AddMinutes(-1)), TimeSpan.FromMinutes(2));

        var registeredCount = await BackfillJob(clock).Run(null);

        await Assert.That(registeredCount).IsEqualTo(1);
    }

    private static string DumpName(DateTime namedAtUtc) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Tenant}-{namedAtUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.tar.gz");

    private Task<StoredArtifact> StoreDumpAsync(string fileName) =>
        _artifacts.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, fileName,
            _artifacts.WriteFile(fileName, _dumpContent), ArtifactEncryption.IfConfigured);

    private DumpHashBackfillJob BackfillJob(TimeProvider clock)
    {
        var options = Options.Create(new DumpHashRegistryOptions());
        var recorder = new DumpHashRecorder(_registry, options, NullLogger<DumpHashRecorder>.Instance,
            _artifacts.Storage);
        return new DumpHashBackfillJob(_registry, recorder, _systemContext, options, clock,
            NullLogger<DumpHashBackfillJob>.Instance, _artifacts.Storage);
    }

    private sealed class AdvancingTimeProvider(DateTimeOffset start, TimeSpan stepPerRead) : TimeProvider
    {
        private DateTimeOffset _next = start;

        public override DateTimeOffset GetUtcNow()
        {
            var current = _next;
            _next += stepPerRead;
            return current;
        }
    }
}
