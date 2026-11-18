using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;

public class ArchiveRegistrationVerifierTests : IDisposable
{
    private const string Tenant = "tenant-1";
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private readonly ArtifactTestEnvironment _artifacts = new("k1");
    private readonly InMemoryDumpHashRegistry _registry = new();
    private readonly byte[] _dumpContent = RandomNumberGenerator.GetBytes(50_000);

    public void Dispose()
    {
        _artifacts.Dispose();
    }

    [Test]
    public async Task UneditedDumpOfTheTargetTenant_IsAdmitted()
    {
        await RegisterAsync(Tenant, _dumpContent, Now.UtcDateTime.AddDays(1));
        var archivePath = _artifacts.WriteFile("archive", _dumpContent);

        await CreateVerifier().VerifyAsync("Tenant-1", archivePath, CancellationToken.None);
    }

    [Test]
    public async Task EditedDump_IsRefused()
    {
        await RegisterAsync(Tenant, _dumpContent, Now.UtcDateTime.AddDays(1));
        var editedContent = (byte[])_dumpContent.Clone();
        editedContent[editedContent.Length / 2] ^= 0x01;
        var archivePath = _artifacts.WriteFile("archive", editedContent);

        await AssertRefusedAsync(Tenant, archivePath);
    }

    [Test]
    public async Task UnknownArchive_IsRefused()
    {
        var archivePath = _artifacts.WriteFile("archive", _dumpContent);

        await AssertRefusedAsync(Tenant, archivePath);
    }

    [Test]
    public async Task DumpOfAnotherTenant_IsRefused()
    {
        await RegisterAsync("tenant-a", _dumpContent, Now.UtcDateTime.AddDays(1));
        var archivePath = _artifacts.WriteFile("archive", _dumpContent);

        await AssertRefusedAsync("tenant-b", archivePath);
    }

    [Test]
    public async Task ExpiredEntry_IsRefused()
    {
        await RegisterAsync(Tenant, _dumpContent, Now.UtcDateTime.AddSeconds(-1));
        var archivePath = _artifacts.WriteFile("archive", _dumpContent);

        await AssertRefusedAsync(Tenant, archivePath);
    }

    [Test]
    public async Task WithoutARegistry_EveryArchiveIsRefused()
    {
        await RegisterAsync(Tenant, _dumpContent, Now.UtcDateTime.AddDays(1));
        var archivePath = _artifacts.WriteFile("archive", _dumpContent);
        var verifierWithoutRegistry = new ArchiveRegistrationVerifier(null, new FixedTimeProvider(Now),
            NullLogger<ArchiveRegistrationVerifier>.Instance);

        var refusal = await Assert.That(async () =>
                await verifierWithoutRegistry.VerifyAsync(Tenant, archivePath, CancellationToken.None))
            .Throws<ArchiveNotRegisteredException>();
        await Assert.That(refusal!.Message).IsEqualTo(ArchiveNotRegisteredException.RefusalMessage);
    }

    private async Task AssertRefusedAsync(string tenantId, string archivePath)
    {
        var refusal = await Assert.That(async () =>
                await CreateVerifier().VerifyAsync(tenantId, archivePath, CancellationToken.None))
            .Throws<ArchiveNotRegisteredException>();
        await Assert.That(refusal!.Message).IsEqualTo(ArchiveNotRegisteredException.RefusalMessage);
    }

    private Task RegisterAsync(string tenantId, byte[] content, DateTime expiresAtUtc)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        return _registry.RegisterAsync(new DumpHashEntry(tenantId, sha256, Now.UtcDateTime.AddDays(-1), expiresAtUtc,
            "dump.tar.gz", content.LongLength, "job-1", DumpHashSources.Dump));
    }

    private ArchiveRegistrationVerifier CreateVerifier() =>
        new(_registry, new FixedTimeProvider(Now), NullLogger<ArchiveRegistrationVerifier>.Instance);
}
