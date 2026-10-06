using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

public class RestoreRepositoryJobTests
{
    private readonly ILogger<RestoreRepositoryJob> _logger = Substitute.For<ILogger<RestoreRepositoryJob>>();
    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();

    private RestoreRepositoryJob CreateJob()
    {
        return new RestoreRepositoryJob(_logger, _systemContext, _backupFileStorage);
    }

    [Test]
    public async Task Run_SystemTenantDoesNotExist_ReturnsEarly()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(false);
        _backupFileStorage.GetTusUploadFilePath(Arg.Any<string>(), Arg.Any<string>()).Returns("/data/tus-uploads/abc123");
        var job = CreateJob();

        await job.Run("tenant-1", "db-1", "abc123", null, false, null, null);

        await _systemContext.DidNotReceive().RestoreTenantAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Run_SystemTenantDoesNotExist_StillDeletesFile()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(false);
        _backupFileStorage.GetTusUploadFilePath("tenant-1", "abc123").Returns("/data/tus-uploads/abc123");
        var job = CreateJob();

        await job.Run("tenant-1", "db-1", "abc123", null, false, null, null);

        await _backupFileStorage.Received(1).DeleteFileAsync("/data/tus-uploads/abc123");
    }

    [Test]
    public async Task Run_FileDoesNotExist_ThrowsJobFailedException()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _backupFileStorage.GetTusUploadFilePath("tenant-1", "abc123").Returns("/nonexistent/abc123");
        var job = CreateJob();

        await Assert.That(async () => await job.Run("tenant-1", "db-1", "abc123", null, false, null, null))
            .Throws<JobFailedException>();
    }

    [Test]
    public async Task Run_FileDoesNotExist_DeletesFileInFinally()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        _backupFileStorage.GetTusUploadFilePath("tenant-1", "abc123").Returns("/nonexistent/abc123");
        var job = CreateJob();

        try
        {
            await job.Run("tenant-1", "db-1", "abc123", null, false, null, null);
        }
        catch (JobFailedException)
        {
            // Expected
        }

        await _backupFileStorage.Received(1).DeleteFileAsync("/nonexistent/abc123");
    }

    [Test]
    public async Task Run_SuccessfulRestore_DeletesBackupFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "backup content");
            _systemContext.IsSystemTenantExistingAsync().Returns(true);
            _backupFileStorage.GetTusUploadFilePath("tenant-1", "abc123").Returns(tempFile);

            var commandResult = new CommandResult { Success = true };
            _systemContext.RestoreTenantAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(),
                    Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(commandResult);

            var job = CreateJob();

            await job.Run("tenant-1", "db-1", "abc123", null, false, null, null);

            await _backupFileStorage.Received(1).DeleteFileAsync(tempFile);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Run_SuccessfulRestore_PassesCorrectParameters()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "backup content");
            _systemContext.IsSystemTenantExistingAsync().Returns(true);
            _backupFileStorage.GetTusUploadFilePath("tenant-1", "abc123").Returns(tempFile);

            var commandResult = new CommandResult { Success = true };
            _systemContext.RestoreTenantAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(),
                    Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(commandResult);

            var job = CreateJob();

            await job.Run("tenant-1", "db-1", "abc123", "old-db", false, null, null);

            await _systemContext.Received(1).RestoreTenantAsync(
                "tenant-1", "db-1", tempFile, "old-db",
                true, true, TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Run_FailedRestore_ThrowsJobFailedException()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "backup content");
            _systemContext.IsSystemTenantExistingAsync().Returns(true);
            _backupFileStorage.GetTusUploadFilePath("tenant-1", "abc123").Returns(tempFile);

            var commandResult = new CommandResult { Success = false, ExitCode = 1 };
            _systemContext.RestoreTenantAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(),
                    Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(commandResult);

            var job = CreateJob();

            await Assert.That(async () => await job.Run("tenant-1", "db-1", "abc123", null, false, null, null))
                .Throws<JobFailedException>();
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Run_UsesCorrectTusFileIdForPath()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(false);
        _backupFileStorage.GetTusUploadFilePath(Arg.Any<string>(), Arg.Any<string>()).Returns("/data/tus-uploads/myFileId");
        var job = CreateJob();

        await job.Run("tenant-1", "db-1", "myFileId", null, false, null, null);

        _backupFileStorage.Received(1).GetTusUploadFilePath("tenant-1", "myFileId");
    }

    [Test]
    public async Task Run_SuccessfulRestore_RunsThePostRestoreSecretSweep_AndReturnsSecretsToReEnter()
    {
        // AB#5539 (decision 5): after a restore the coordinator clears unknown key ids, encrypts and
        // verifies; the secrets to re-enter travel in the job result.
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "backup content");
            _systemContext.IsSystemTenantExistingAsync().Returns(true);
            _backupFileStorage.GetTusUploadFilePath("tenant-1", "abc123").Returns(tempFile);
            _systemContext.RestoreTenantAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(),
                    Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(new CommandResult { Success = true });

            var coordinator = Substitute.For<ISecretSweepCoordinator>();
            var sweepReport = new SecretSweepReport
            {
                TenantId = "tenant-1",
                Trigger = SecretSweepTrigger.Restore,
                Outcome = SecretSweepOutcome.Succeeded,
                SecretsToReEnter = [new SecretValueReference { CkTypeId = "T/Type", RtId = "r1", AttributePath = "Password" }]
            };
            coordinator.RunAfterRestoreAsync("tenant-1", Arg.Any<SecretSweepRunInfo?>(), Arg.Any<CancellationToken>()).Returns(sweepReport);

            var job = new RestoreRepositoryJob(_logger, _systemContext, _backupFileStorage, coordinator);

            var result = await job.Run("tenant-1", "db-1", "abc123", "other-env-db", false, null, null);

            await Assert.That(result).IsNotNull();
            await Assert.That(result!.TenantId).IsEqualTo("tenant-1");
            await Assert.That(result.SecretSweep).IsSameReferenceAs(sweepReport);
            await Assert.That(result.SecretSweep!.SecretsToReEnter.Count).IsEqualTo(1);
            Received.InOrder(() =>
            {
                _systemContext.RestoreTenantAsync("tenant-1", "db-1", tempFile, "other-env-db", true, true,
                    TimeSpan.FromHours(1), Arg.Any<CancellationToken>());
                coordinator.RunAfterRestoreAsync("tenant-1", Arg.Any<SecretSweepRunInfo?>(), Arg.Any<CancellationToken>());
            });
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Run_FailedRestore_DoesNotRunTheSecretSweep()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "backup content");
            _systemContext.IsSystemTenantExistingAsync().Returns(true);
            _backupFileStorage.GetTusUploadFilePath("tenant-1", "abc123").Returns(tempFile);
            _systemContext.RestoreTenantAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(),
                    Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(new CommandResult { Success = false, ExitCode = 1 });
            var coordinator = Substitute.For<ISecretSweepCoordinator>();
            var job = new RestoreRepositoryJob(_logger, _systemContext, _backupFileStorage, coordinator);

            await Assert.That(async () => await job.Run("tenant-1", "db-1", "abc123", null, false, null, null))
                .Throws<JobFailedException>();

            await coordinator.DidNotReceiveWithAnyArgs().RunAfterRestoreAsync(default!, default, default);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
