using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     AB#5559 — a plaintext scratch dump left behind by a crashed process is deleted at startup (older than one
///     hour), not only by the hourly cleanup after <c>fileRetentionHours</c>; younger files of running jobs stay.
/// </summary>
public class StaleScratchCleanupHostedServiceTests : IDisposable
{
    private readonly ArtifactTestEnvironment _env = new("k1");

    public void Dispose()
    {
        _env.Dispose();
    }

    [Test]
    public async Task Start_DeletesScratchFilesOlderThanAnHour_AndKeepsYoungerOnes()
    {
        var stale = _env.Storage.CreateScratchFilePath(".presweep.tar.gz");
        await File.WriteAllBytesAsync(stale, RandomNumberGenerator.GetBytes(16));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromMinutes(61));
        var running = _env.Storage.CreateScratchFilePath(".restore");
        await File.WriteAllBytesAsync(running, RandomNumberGenerator.GetBytes(16));
        File.SetLastWriteTimeUtc(running, DateTime.UtcNow - TimeSpan.FromMinutes(30));

        var service = new StaleScratchCleanupHostedService(_env.Storage,
            NullLogger<StaleScratchCleanupHostedService>.Instance);
        await service.StartAsync(CancellationToken.None);

        await Assert.That(File.Exists(stale)).IsFalse();
        await Assert.That(File.Exists(running)).IsTrue();
    }

    [Test]
    public async Task Start_WithoutScratchDirectory_DoesNothing()
    {
        var service = new StaleScratchCleanupHostedService(_env.Storage,
            NullLogger<StaleScratchCleanupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        await Assert.That(Directory.Exists(_env.ScratchDirectory)).IsFalse();
    }

    [Test]
    public async Task Start_CleanupFails_DoesNotStopTheHost()
    {
        var storage = Substitute.For<IBotArtifactStorage>();
        storage.CleanupScratch(Arg.Any<TimeSpan>()).Returns(_ => throw new IOException("disk"));
        var service = new StaleScratchCleanupHostedService(storage,
            NullLogger<StaleScratchCleanupHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        storage.Received(1).CleanupScratch(StaleScratchCleanupHostedService.MaxAge);
    }
}
