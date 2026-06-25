using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RepositoryUpdate;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

public class DumpRepositoryJobTests
{
    private readonly ILogger<DumpRepositoryJob> _logger = Substitute.For<ILogger<DumpRepositoryJob>>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();

    private DumpRepositoryJob CreateJob()
    {
        return new DumpRepositoryJob(_logger, _systemContext, _backupFileStorage);
    }

    [Test]
    public async Task Run_SystemTenantDoesNotExist_ReturnsNull()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(false);
        var job = CreateJob();

        var result = await job.Run("tenant-1", false, null);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Run_TenantContextNotFound_ThrowsRepositoryUpdateException()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _systemContext.FindTenantContextAsync("tenant-1").Returns((ITenantContext)null!);
        var job = CreateJob();

        await Assert.That(async () => await job.Run("tenant-1", false, null))
            .Throws<RepositoryUpdateException>();
    }

    [Test]
    public async Task Run_SuccessfulDump_ReturnsFilePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dump-test-{Guid.NewGuid():N}");
        try
        {
            var expectedPath = Path.Combine(tempDir, "tenant-1", "tenant-1-20260219-120000-abcd1234.tar.gz");

            var tenantContext = Substitute.For<ITenantContext>();
            _systemContext.IsSystemTenantExistingAsync().Returns(true);
            _systemContext.FindTenantContextAsync("tenant-1").Returns(tenantContext);

            _backupFileStorage.GenerateDumpFileName("tenant-1").Returns("tenant-1-20260219-120000-abcd1234.tar.gz");
            _backupFileStorage.GetDumpFilePath("tenant-1", "tenant-1-20260219-120000-abcd1234.tar.gz")
                .Returns(expectedPath);

            var commandResult = new CommandResult { Success = true };
            _systemContext.BackupTenantAsync("tenant-1", expectedPath,
                timeout: TimeSpan.FromHours(1)).Returns(commandResult);

            var job = CreateJob();

            var result = await job.Run("tenant-1", false, null);

            await Assert.That(result).IsEqualTo(expectedPath);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_FailedDump_ThrowsJobFailedException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dump-test-{Guid.NewGuid():N}");
        try
        {
            var dumpPath = Path.Combine(tempDir, "tenant-1", "dump.tar.gz");

            var tenantContext = Substitute.For<ITenantContext>();
            _systemContext.IsSystemTenantExistingAsync().Returns(true);
            _systemContext.FindTenantContextAsync("tenant-1").Returns(tenantContext);

            _backupFileStorage.GenerateDumpFileName("tenant-1").Returns("dump.tar.gz");
            _backupFileStorage.GetDumpFilePath("tenant-1", "dump.tar.gz").Returns(dumpPath);

            var commandResult = new CommandResult { Success = false, ExitCode = 1 };
            _systemContext.BackupTenantAsync("tenant-1", dumpPath,
                timeout: TimeSpan.FromHours(1)).Returns(commandResult);

            var job = CreateJob();

            await Assert.That(async () => await job.Run("tenant-1", false, null))
                .Throws<JobFailedException>();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_GeneratesFileNameFromService()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dump-test-{Guid.NewGuid():N}");
        try
        {
            var dumpPath = Path.Combine(tempDir, "tenant-1", "dump.tar.gz");

            var tenantContext = Substitute.For<ITenantContext>();
            _systemContext.IsSystemTenantExistingAsync().Returns(true);
            _systemContext.FindTenantContextAsync("tenant-1").Returns(tenantContext);

            _backupFileStorage.GenerateDumpFileName("tenant-1").Returns("dump.tar.gz");
            _backupFileStorage.GetDumpFilePath("tenant-1", "dump.tar.gz").Returns(dumpPath);

            var commandResult = new CommandResult { Success = true };
            _systemContext.BackupTenantAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>()).Returns(commandResult);

            var job = CreateJob();

            await job.Run("tenant-1", false, null);

            _backupFileStorage.Received(1).GenerateDumpFileName("tenant-1");
            _backupFileStorage.Received(1).GetDumpFilePath("tenant-1", "dump.tar.gz");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public async Task Run_PassesCorrectFilePathToBackup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"dump-test-{Guid.NewGuid():N}");
        try
        {
            var dumpPath = Path.Combine(tempDir, "tenant-1", "dump.tar.gz");

            var tenantContext = Substitute.For<ITenantContext>();
            _systemContext.IsSystemTenantExistingAsync().Returns(true);
            _systemContext.FindTenantContextAsync("tenant-1").Returns(tenantContext);

            _backupFileStorage.GenerateDumpFileName("tenant-1").Returns("dump.tar.gz");
            _backupFileStorage.GetDumpFilePath("tenant-1", "dump.tar.gz").Returns(dumpPath);

            var commandResult = new CommandResult { Success = true };
            _systemContext.BackupTenantAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>()).Returns(commandResult);

            var job = CreateJob();

            await job.Run("tenant-1", false, null);

            await _systemContext.Received(1).BackupTenantAsync("tenant-1", dumpPath,
                Arg.Any<bool>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken?>());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
