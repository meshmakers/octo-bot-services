using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
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

    [Test]
    public async Task Run_DeletesPreSweepSecretBackupsAfterTheirOwnRetention()
    {
        // AB#5539, decision 10: pre-sweep secret backups are kept 7 days, not the hours of other files.
        _backupFileStorage.CleanupStaleFilesAsync(Arg.Any<TimeSpan>()).Returns(Task.FromResult(0));
        var job = new CleanupStaleFilesJob(_logger, _backupFileStorage, 4);

        await job.Run(null);

        await _backupFileStorage.Received(1).CleanupStaleSecretBackupsAsync(TimeSpan.FromDays(7));
    }

    [Test]
    public async Task Run_UsesTheConfiguredSecretBackupRetention()
    {
        _backupFileStorage.CleanupStaleFilesAsync(Arg.Any<TimeSpan>()).Returns(Task.FromResult(0));
        var job = new CleanupStaleFilesJob(_logger, _backupFileStorage, 4, 3);

        await job.Run(null);

        await _backupFileStorage.Received(1).CleanupStaleSecretBackupsAsync(TimeSpan.FromDays(3));
    }

    [Test]
    public async Task Run_RecordsExpiredDumpsInTheSweepRunHistory()
    {
        // AB#5544: an expired dump gets deletedAt (no deletedBy) in its run.
        var now = new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        var runStore = Substitute.For<ISecretSweepRunStore>();
        _backupFileStorage.CleanupStaleFilesAsync(Arg.Any<TimeSpan>()).Returns(Task.FromResult(0));
        var expired = Path.Combine("/data", "secret-backups", "tenant-a", "tenant-a-20261006-120000-abcd1234.presweep.tar.gz");
        _backupFileStorage.CleanupStaleSecretBackupsAsync(Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([expired]));
        var job = new CleanupStaleFilesJob(_logger, _backupFileStorage, 4, 7, runStore, new FixedTime(now));

        await job.Run(null);

        await runStore.Received(1).MarkDumpDeletedAsync("tenant-a",
            "tenant-a-20261006-120000-abcd1234.presweep.tar.gz", now.UtcDateTime, null);
    }

    [Test]
    public async Task Run_RunHistoryFailure_DoesNotFailTheCleanup()
    {
        var runStore = Substitute.For<ISecretSweepRunStore>();
        runStore.MarkDumpDeletedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("storage down"));
        _backupFileStorage.CleanupStaleFilesAsync(Arg.Any<TimeSpan>()).Returns(Task.FromResult(0));
        _backupFileStorage.CleanupStaleSecretBackupsAsync(Arg.Any<TimeSpan>())
            .Returns(Task.FromResult<IReadOnlyList<string>>([Path.Combine("/x", "t", "f.presweep.tar.gz")]));
        var job = new CleanupStaleFilesJob(_logger, _backupFileStorage, 4, 7, runStore);

        await job.Run(null);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
