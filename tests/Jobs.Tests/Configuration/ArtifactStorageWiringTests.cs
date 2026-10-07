using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Configuration;

/// <summary>
///     AB#5561 — root path precedence of the file system artifact store and the backwards-compatible
///     <c>Bot:SecretSweep:BackupStoragePath</c> alias for pre-sweep dumps.
/// </summary>
public class ArtifactStorageWiringTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"octo-wiring-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private IBotArtifactStorage Build(Dictionary<string, string?> settings, string? dumpPath = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ArtifactTestEnvironment.CreateProtector(
            new Dictionary<string, byte[]> { ["k1"] = RandomNumberGenerator.GetBytes(32) }, "k1"));
        var files = Substitute.For<IBackupFileStorageService>();
        files.TusStoragePath.Returns(Path.Combine(_directory, "tus"));
        files.DumpStoragePath.Returns(dumpPath ?? Path.Combine(_directory, "dumps"));
        services.AddSingleton(files);
        services.AddOctoBotArtifactStorage(configuration, Path.Combine(_directory, "scratch"));
        return services.BuildServiceProvider().GetRequiredService<IBotArtifactStorage>();
    }

    private async Task StorePreSweepAsync(IBotArtifactStorage storage)
    {
        var source = Path.Combine(_directory, "dump");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        await storage.StoreFileAsync(ArtifactCategories.Presweep, "t1", "a.presweep", source,
            ArtifactEncryption.Required);
    }

    private static string PresweepFile(string root)
    {
        return Path.Combine(root, "main", "presweep", "t1", "a.presweep.octoenc");
    }

    [Test]
    public async Task BackupStoragePathAlone_IsTheRootOfThePreSweepDumps()
    {
        var backups = Path.Combine(_directory, "secret-backups");
        var storage = Build(new Dictionary<string, string?>
        {
            ["ArtifactStorage:InstancePrefix"] = "main",
            ["Bot:SecretSweep:BackupStoragePath"] = backups
        });

        await StorePreSweepAsync(storage);

        await Assert.That(File.Exists(PresweepFile(backups))).IsTrue();
    }

    [Test]
    public async Task ConfiguredRootPath_WinsOverBackupStoragePath()
    {
        var root = Path.Combine(_directory, "artifacts");
        var backups = Path.Combine(_directory, "secret-backups");
        var storage = Build(new Dictionary<string, string?>
        {
            ["ArtifactStorage:InstancePrefix"] = "main",
            ["ArtifactStorage:FileSystem:RootPath"] = root,
            ["Bot:SecretSweep:BackupStoragePath"] = backups
        });

        await StorePreSweepAsync(storage);

        await Assert.That(File.Exists(PresweepFile(root))).IsTrue();
        await Assert.That(File.Exists(PresweepFile(backups))).IsFalse();
    }

    [Test]
    public async Task RootPathInsideTheDumpDirectory_IsRefused()
    {
        var dumps = Path.Combine(_directory, "dumps");

        await Assert.That(() => Build(new Dictionary<string, string?>
            {
                ["ArtifactStorage:FileSystem:RootPath"] = Path.Combine(dumps, "artifacts")
            }, dumps))
            .Throws<InvalidOperationException>();
    }
}
