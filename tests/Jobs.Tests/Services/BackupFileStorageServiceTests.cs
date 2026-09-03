using Meshmakers.Octo.Backend.Jobs.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

public class BackupFileStorageServiceTests
{
    private const string TusStoragePath = "/data/tus-uploads";
    private const string DumpStoragePath = "/data/dumps";

    private readonly ILogger<BackupFileStorageService> _logger = Substitute.For<ILogger<BackupFileStorageService>>();

    private BackupFileStorageService CreateService()
    {
        return new BackupFileStorageService(TusStoragePath, DumpStoragePath, _logger);
    }

    [Test]
    public async Task GetTusUploadFilePath_PutsTheUploadUnderItsOwnTenant()
    {
        var service = CreateService();

        var result = service.GetTusUploadFilePath("tenant-1", "abc123");

        await Assert.That(result).IsEqualTo(
            Path.Combine(Path.GetFullPath(TusStoragePath), "tenant-1", "abc123"));
    }

    /// <summary>
    ///     🔴 The per-tenant directory <i>is</i> the ownership binding (AB#5060), so two tenants must
    ///     never resolve the same file — not even when handed the identical tus file id, which is the
    ///     case that matters: an id leaked from one tenant is worthless to another.
    /// </summary>
    [Test]
    public async Task GetTusUploadFilePath_ResolvesDifferentFilesForDifferentTenants()
    {
        var service = CreateService();

        var first = service.GetTusUploadFilePath("tenant-1", "abc123");
        var second = service.GetTusUploadFilePath("tenant-2", "abc123");

        await Assert.That(first).IsNotEqualTo(second);
    }

    /// <summary>
    ///     🔴 The tenant id becomes a directory name. On the tus route the shared
    ///     <c>TenantIdRouteConstraint</c> would already have rejected these, but a dump reaches the
    ///     same code as a Hangfire job argument on no route at all — so this class checks rather than
    ///     assumes. Every one of these would otherwise write, or read, outside the storage root.
    /// </summary>
    [Test]
    [Arguments("..")]
    [Arguments("../other")]
    [Arguments("..\\other")]
    [Arguments("/etc")]
    [Arguments("tenant/../../etc")]
    [Arguments(".")]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("tenant 1")]
    public async Task GetTusUploadFilePath_RefusesATenantIdThatIsNotASafePathSegment(string tenantId)
    {
        var service = CreateService();

        await Assert.That(() => service.GetTusUploadFilePath(tenantId, "abc123"))
            .Throws<ArgumentException>();
    }

    /// <summary>
    ///     The same guard protects the dump path, which builds a per-tenant directory the same way.
    /// </summary>
    [Test]
    public async Task GetDumpFilePath_RefusesATraversingTenantId()
    {
        var service = CreateService();

        await Assert.That(() => service.GetDumpFilePath("../escape", "dump.tar.gz"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetDumpFilePath_ReturnsCorrectPath()
    {
        var service = CreateService();

        var result = service.GetDumpFilePath("tenant-1", "dump.tar.gz");

        await Assert.That(result).IsEqualTo(Path.Combine(DumpStoragePath, "tenant-1", "dump.tar.gz"));
    }

    [Test]
    public async Task GenerateDumpFileName_ContainsTenantId()
    {
        var service = CreateService();

        var result = service.GenerateDumpFileName("myTenant");

        await Assert.That(result).StartsWith("myTenant-");
    }

    [Test]
    public async Task GenerateDumpFileName_EndsWithTarGz()
    {
        var service = CreateService();

        var result = service.GenerateDumpFileName("myTenant");

        await Assert.That(result).EndsWith(".tar.gz");
    }

    [Test]
    public async Task GenerateDumpFileName_ContainsTimestamp()
    {
        var service = CreateService();

        var result = service.GenerateDumpFileName("myTenant");

        // Format: {tenantId}-{yyyyMMdd-HHmmss}-{guid8}.tar.gz
        var parts = result.Replace(".tar.gz", "").Split('-');
        // parts: myTenant, yyyyMMdd, HHmmss, guid8
        await Assert.That(parts.Length).IsGreaterThanOrEqualTo(4);
        await Assert.That(parts[1].Length).IsEqualTo(8); // yyyyMMdd
        await Assert.That(parts[2].Length).IsEqualTo(6); // HHmmss
    }

    [Test]
    public async Task GenerateDumpFileName_ContainsGuidSuffix()
    {
        var service = CreateService();

        var result = service.GenerateDumpFileName("myTenant");

        // Last part before .tar.gz should be 8 chars (truncated GUID)
        var withoutExtension = result.Replace(".tar.gz", "");
        var lastDash = withoutExtension.LastIndexOf('-');
        var guidPart = withoutExtension[(lastDash + 1)..];
        await Assert.That(guidPart.Length).IsEqualTo(8);
    }

    [Test]
    public async Task GenerateDumpFileName_IsUnique()
    {
        var service = CreateService();

        var result1 = service.GenerateDumpFileName("myTenant");
        var result2 = service.GenerateDumpFileName("myTenant");

        await Assert.That(result1).IsNotEqualTo(result2);
    }

    [Test]
    public async Task TusStoragePath_ReturnsConfiguredPath()
    {
        var service = CreateService();

        await Assert.That(service.TusStoragePath).IsEqualTo(TusStoragePath);
    }

    [Test]
    public async Task DumpStoragePath_ReturnsConfiguredPath()
    {
        var service = CreateService();

        await Assert.That(service.DumpStoragePath).IsEqualTo(DumpStoragePath);
    }

    [Test]
    public async Task DeleteFileAsync_NonExistentFile_DoesNotThrow()
    {
        var service = CreateService();

        // Should complete without throwing
        await service.DeleteFileAsync("/nonexistent/file.tar.gz");
    }

    [Test]
    public async Task DeleteFileAsync_ExistingFile_DeletesFile()
    {
        var service = CreateService();
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "test content");

        await service.DeleteFileAsync(tempFile);

        await Assert.That(File.Exists(tempFile)).IsFalse();
    }

    [Test]
    public async Task CleanupStaleFilesAsync_EmptyDirectories_ReturnsZero()
    {
        var tempTus = Path.Combine(Path.GetTempPath(), $"tus-test-{Guid.NewGuid():N}");
        var tempDump = Path.Combine(Path.GetTempPath(), $"dump-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempTus);
        Directory.CreateDirectory(tempDump);

        try
        {
            var service = new BackupFileStorageService(tempTus, tempDump, _logger);

            var result = await service.CleanupStaleFilesAsync(TimeSpan.FromHours(4));

            await Assert.That(result).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(tempTus, true);
            Directory.Delete(tempDump, true);
        }
    }

    [Test]
    public async Task CleanupStaleFilesAsync_StaleFiles_DeletesAndReturnsCount()
    {
        var tempTus = Path.Combine(Path.GetTempPath(), $"tus-test-{Guid.NewGuid():N}");
        var tempDump = Path.Combine(Path.GetTempPath(), $"dump-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempTus);
        Directory.CreateDirectory(tempDump);

        try
        {
            // Create a stale file (set last write time to 5 hours ago)
            var staleFile = Path.Combine(tempTus, "stale-file.bin");
            await File.WriteAllTextAsync(staleFile, "stale content");
            File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow.AddHours(-5));

            // Create a fresh file
            var freshFile = Path.Combine(tempTus, "fresh-file.bin");
            await File.WriteAllTextAsync(freshFile, "fresh content");

            var service = new BackupFileStorageService(tempTus, tempDump, _logger);

            var result = await service.CleanupStaleFilesAsync(TimeSpan.FromHours(4));

            await Assert.That(result).IsEqualTo(1);
            await Assert.That(File.Exists(staleFile)).IsFalse();
            await Assert.That(File.Exists(freshFile)).IsTrue();
        }
        finally
        {
            Directory.Delete(tempTus, true);
            Directory.Delete(tempDump, true);
        }
    }

    [Test]
    public async Task CleanupStaleFilesAsync_NonExistentDirectories_ReturnsZero()
    {
        var service = new BackupFileStorageService("/nonexistent/tus", "/nonexistent/dump", _logger);

        var result = await service.CleanupStaleFilesAsync(TimeSpan.FromHours(4));

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task EnsureDirectoriesExist_CreatesDirectories()
    {
        var tempTus = Path.Combine(Path.GetTempPath(), $"tus-test-{Guid.NewGuid():N}");
        var tempDump = Path.Combine(Path.GetTempPath(), $"dump-test-{Guid.NewGuid():N}");

        try
        {
            var service = new BackupFileStorageService(tempTus, tempDump, _logger);

            service.EnsureDirectoriesExist();

            await Assert.That(Directory.Exists(tempTus)).IsTrue();
            await Assert.That(Directory.Exists(tempDump)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempTus)) Directory.Delete(tempTus, true);
            if (Directory.Exists(tempDump)) Directory.Delete(tempDump, true);
        }
    }
}
