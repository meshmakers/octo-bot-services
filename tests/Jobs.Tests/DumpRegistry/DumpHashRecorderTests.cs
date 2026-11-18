using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;

public class DumpHashRecorderTests : IDisposable
{
    private const string Tenant = "Tenant-1";
    private static readonly DateTime DumpStartedAtUtc = new(2026, 11, 18, 9, 30, 0, DateTimeKind.Utc);

    private readonly ArtifactTestEnvironment _artifacts = new("k1");
    private readonly InMemoryDumpHashRegistry _registry = new();
    private readonly byte[] _dumpContent = RandomNumberGenerator.GetBytes(100_000);

    public void Dispose()
    {
        _artifacts.Dispose();
    }

    [Test]
    public async Task EncryptedStoredDump_IsRegisteredWithTheHashOfTheDownloadedPlaintext()
    {
        var stored = await _artifacts.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "dump.tar.gz",
            _artifacts.WriteFile("dump", _dumpContent), ArtifactEncryption.IfConfigured);
        await Assert.That(stored.Encrypted).IsTrue();

        var recordedHash = await CreateRecorder().RecordAsync(Tenant, stored.ToResultReference(), "job-1",
            DumpStartedAtUtc, CancellationToken.None);

        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(_dumpContent));
        await Assert.That(recordedHash).IsEqualTo(expectedHash);
        var entry = _registry.Entries.Single();
        await Assert.That(entry.TenantId).IsEqualTo("tenant-1");
        await Assert.That(entry.Sha256).IsEqualTo(expectedHash);
        await Assert.That(entry.JobId).IsEqualTo("job-1");
        await Assert.That(entry.FileName).IsEqualTo(stored.FileName);
        await Assert.That(entry.SizeBytes).IsEqualTo(_dumpContent.LongLength);
        await Assert.That(entry.Source).IsEqualTo(DumpHashSources.Dump);
        await Assert.That(entry.DumpedAtUtc).IsEqualTo(DumpStartedAtUtc);
        await Assert.That(entry.ExpiresAtUtc).IsEqualTo(DumpStartedAtUtc.AddDays(30));
    }

    [Test]
    public async Task ARegistryWriteThatFailsOnce_IsTriedAgain()
    {
        var localPath = _artifacts.WriteFile("retry-dump.tar.gz", _dumpContent);
        _registry.FailingRegistrations = 1;

        await CreateRecorder(withArtifactStore: false).RecordAsync(Tenant, localPath, null, DumpStartedAtUtc,
            CancellationToken.None);

        await Assert.That(_registry.Entries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ARegistryThatKeepsFailing_FailsTheRegistration()
    {
        var localPath = _artifacts.WriteFile("failing-dump.tar.gz", _dumpContent);
        _registry.FailingRegistrations = 3;

        await Assert.That(async () => await CreateRecorder(withArtifactStore: false).RecordAsync(Tenant, localPath,
            null, DumpStartedAtUtc, CancellationToken.None)).Throws<InvalidOperationException>();
        await Assert.That(_registry.Entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LocalDumpWithoutArtifactStore_IsRegisteredWithTheHashOfTheFile()
    {
        var localPath = _artifacts.WriteFile("local-dump.tar.gz", _dumpContent);

        var recordedHash = await CreateRecorder(withArtifactStore: false).RecordAsync(Tenant, localPath, null,
            DumpStartedAtUtc, CancellationToken.None);

        await Assert.That(recordedHash).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(_dumpContent)));
        await Assert.That(_registry.Entries.Single().FileName).IsEqualTo("local-dump.tar.gz");
    }

    [Test]
    public async Task ReferenceToAMissingArtifact_FailsAndRegistersNothing()
    {
        var missingReference = new StoredArtifact(ArtifactCategories.TenantDumps, Tenant, "gone.tar.gz", 1, false)
            .ToResultReference();

        await Assert.That(async () => await CreateRecorder().RecordAsync(Tenant, missingReference, "job-1",
            DumpStartedAtUtc, CancellationToken.None)).Throws<JobFailedException>();
        await Assert.That(_registry.Entries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ReferenceToAnotherTenantsArtifact_FailsAndRegistersNothing()
    {
        var stored = await _artifacts.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, "other-tenant",
            "dump.tar.gz", _artifacts.WriteFile("dump", _dumpContent), ArtifactEncryption.IfConfigured);

        await Assert.That(async () => await CreateRecorder().RecordAsync(Tenant, stored.ToResultReference(), "job-1",
            DumpStartedAtUtc, CancellationToken.None)).Throws<JobFailedException>();
        await Assert.That(_registry.Entries.Count).IsEqualTo(0);
    }

    private DumpHashRecorder CreateRecorder(bool withArtifactStore = true)
    {
        var options = Options.Create(new DumpHashRegistryOptions { RetentionDays = 30 });
        return new DumpHashRecorder(_registry, options, NullLogger<DumpHashRecorder>.Instance,
            withArtifactStore ? _artifacts.Storage : null);
    }
}
