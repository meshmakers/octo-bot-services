using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

public class CleanupStaleFilesJobTests
{
    private readonly ILogger<CleanupStaleFilesJob> _logger = Substitute.For<ILogger<CleanupStaleFilesJob>>();
    private readonly IBackupFileStorageService _backupFileStorage = Substitute.For<IBackupFileStorageService>();

    private CleanupStaleFilesJob CreateJob(int retentionHours = 4)
    {
        return new CleanupStaleFilesJob(_logger, _backupFileStorage, retentionHours);
    }

    [Test]
    public async Task Run_CallsCleanupWithCorrectRetention()
    {
        _backupFileStorage.CleanupStaleFilesAsync(Arg.Any<TimeSpan>()).Returns(Task.FromResult(0));
        var job = CreateJob(6);

        await job.Run(null);

        await _backupFileStorage.Received(1).CleanupStaleFilesAsync(TimeSpan.FromHours(6));
    }

    [Test]
    public async Task Run_WithDefaultRetention_Uses4Hours()
    {
        _backupFileStorage.CleanupStaleFilesAsync(Arg.Any<TimeSpan>()).Returns(Task.FromResult(0));
        var job = CreateJob(4);

        await job.Run(null);

        await _backupFileStorage.Received(1).CleanupStaleFilesAsync(TimeSpan.FromHours(4));
    }

    [Test]
    public async Task Run_CompletesSuccessfully_WhenFilesDeleted()
    {
        _backupFileStorage.CleanupStaleFilesAsync(Arg.Any<TimeSpan>()).Returns(Task.FromResult(5));
        var job = CreateJob();

        await job.Run(null);

        await _backupFileStorage.Received(1).CleanupStaleFilesAsync(Arg.Any<TimeSpan>());
    }

    [Test]
    public async Task Run_PropagatesException_WhenCleanupFails()
    {
        _backupFileStorage.CleanupStaleFilesAsync(Arg.Any<TimeSpan>())
            .ThrowsAsync(new IOException("Disk error"));
        var job = CreateJob();

        await Assert.That(async () => await job.Run(null)).Throws<IOException>();
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(24)]
    [Arguments(168)]
    public async Task Run_ConvertsHoursToTimeSpanCorrectly(int hours)
    {
        _backupFileStorage.CleanupStaleFilesAsync(Arg.Any<TimeSpan>()).Returns(Task.FromResult(0));
        var job = CreateJob(hours);

        await job.Run(null);

        await _backupFileStorage.Received(1).CleanupStaleFilesAsync(TimeSpan.FromHours(hours));
    }
}
