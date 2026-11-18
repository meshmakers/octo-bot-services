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

public class DumpHashBackfillJobTests : IDisposable
{
    private const string ExistingTenant = "tenant-1";
    private const string DeletedTenant = "tenant-gone";

    private readonly ArtifactTestEnvironment _artifacts = new("k1");
    private readonly InMemoryDumpHashRegistry _registry = new();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly byte[] _dumpContent = RandomNumberGenerator.GetBytes(20_000);

    public DumpHashBackfillJobTests()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _systemContext.FindTenantContextAsync(ExistingTenant).Returns(Substitute.For<ITenantContext>());
        _systemContext.FindTenantContextAsync(DeletedTenant).Returns((ITenantContext)null!);
    }

    public void Dispose()
    {
        _artifacts.Dispose();
    }

    [Test]
    public async Task StoredDumpOfAnExistingTenant_IsRegisteredWithTheDownloadHash_DatedByTheArtifact()
    {
        var stored = await StoreDumpAsync(ExistingTenant, "old-dump.tar.gz");

        var registeredCount = await BackfillJob().Run(null);

        await Assert.That(registeredCount).IsEqualTo(1);
        var entry = _registry.Entries.Single();
        await Assert.That(entry.Sha256).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(_dumpContent)));
        await Assert.That(entry.FileName).IsEqualTo(stored.FileName);
        await Assert.That(entry.Source).IsEqualTo(DumpHashSources.Backfill);
        await Assert.That(entry.JobId).IsNull();
        await Assert.That(entry.ExpiresAtUtc - entry.DumpedAtUtc).IsEqualTo(TimeSpan.FromDays(365));
    }

    [Test]
    public async Task AlreadyRecordedDump_IsNotRegisteredAgain()
    {
        var stored = await StoreDumpAsync(ExistingTenant, "recorded.tar.gz");
        await _registry.RegisterAsync(new DumpHashEntry(ExistingTenant, "recorded-hash", DateTime.UtcNow,
            DateTime.UtcNow.AddDays(1), stored.FileName, 1, "job-1", DumpHashSources.Dump));

        var registeredCount = await BackfillJob().Run(null);

        await Assert.That(registeredCount).IsEqualTo(0);
        await Assert.That(_registry.Entries.Single().JobId).IsEqualTo("job-1");
    }

    [Test]
    public async Task DumpOfADeletedTenant_IsNotRegistered()
    {
        await StoreDumpAsync(DeletedTenant, "orphan.tar.gz");

        await Assert.That(await BackfillJob().Run(null)).IsEqualTo(0);
        await Assert.That(_registry.Entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DumpOlderThanTheRetention_IsNotRegistered()
    {
        await StoreDumpAsync(ExistingTenant, "ancient.tar.gz");

        var registeredCount = await BackfillJob(DateTimeOffset.UtcNow.AddDays(400)).Run(null);

        await Assert.That(registeredCount).IsEqualTo(0);
    }

    [Test]
    public async Task UnreadableDump_IsSkipped_AndTheOthersAreRegistered()
    {
        var damaged = await StoreDumpAsync(ExistingTenant, "damaged.tar.gz");
        var storedPath = Path.Combine(_artifacts.StoreRoot,
            _artifacts.Keys.Build(ArtifactCategories.TenantDumps, ExistingTenant, damaged.FileName));
        var storedBytes = await File.ReadAllBytesAsync(storedPath);
        storedBytes[^10] ^= 0xFF;
        await File.WriteAllBytesAsync(storedPath, storedBytes);
        await StoreDumpAsync(ExistingTenant, "intact.tar.gz");

        var registeredCount = await BackfillJob().Run(null);

        await Assert.That(registeredCount).IsEqualTo(1);
        await Assert.That(_registry.Entries.Single().FileName).StartsWith("intact.tar.gz");
    }

    private Task<StoredArtifact> StoreDumpAsync(string tenantId, string fileName) =>
        _artifacts.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, tenantId, fileName,
            _artifacts.WriteFile(fileName, _dumpContent), ArtifactEncryption.IfConfigured);

    private DumpHashBackfillJob BackfillJob(DateTimeOffset? now = null)
    {
        var options = Options.Create(new DumpHashRegistryOptions());
        TimeProvider clock = now == null ? TimeProvider.System : new FixedTimeProvider(now.Value);
        var recorder = new DumpHashRecorder(_registry, options, clock, NullLogger<DumpHashRecorder>.Instance,
            _artifacts.Storage);
        return new DumpHashBackfillJob(_registry, recorder, _systemContext, options, clock,
            NullLogger<DumpHashBackfillJob>.Instance, _artifacts.Storage);
    }
}
