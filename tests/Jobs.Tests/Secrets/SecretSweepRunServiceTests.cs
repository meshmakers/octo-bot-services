using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     AB#5544 — run list with the live dump state, and the early dump deletion (204 / 404 / 409 semantics).
/// </summary>
public class SecretSweepRunServiceTests : IDisposable
{
    private const string FileName = "t-20261006-120000-abcd1234.presweep.tar.gz";
    private readonly InMemorySecretSweepRunStore _runs = new();
    private readonly IBackupFileStorageService _storage = Substitute.For<IBackupFileStorageService>();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"octo-run-service-{Guid.NewGuid():N}");
    private readonly DateTime _now = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);

    public SecretSweepRunServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _storage.GetSecretBackupFilePath("t", Arg.Any<string>())
            .Returns(ci => Path.Combine(_directory, ci.ArgAt<string>(1)));
        _storage.DeleteFileAsync(Arg.Any<string>()).Returns(ci =>
        {
            File.Delete(ci.Arg<string>());
            return Task.CompletedTask;
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private SecretSweepRunService CreateService()
    {
        return new SecretSweepRunService(_runs, _storage, Substitute.For<ILogger<SecretSweepRunService>>(),
            new FixedTime(new DateTimeOffset(_now)));
    }

    private async Task<string> SeedRunWithDumpAsync(string runId = "job-1")
    {
        var path = Path.Combine(_directory, FileName);
        await File.WriteAllTextAsync(path, "dump-content");
        await _runs.UpsertAsync("t", new SecretSweepRunDto
        {
            RunId = runId,
            Mode = SecretSweepModeDto.Encrypt,
            Outcome = SecretSweepOutcomeDto.Succeeded,
            Dump = new SecretSweepDumpDto { FileName = FileName, Exists = true, SizeBytes = 1 }
        });
        return path;
    }

    [Test]
    public async Task DeleteDump_DeletesTheFile_AndRecordsWhenAndWho()
    {
        var path = await SeedRunWithDumpAsync();

        var result = await CreateService().DeleteDumpAsync("t", "job-1", "alice");

        await Assert.That(result).IsEqualTo(SecretSweepDumpDeleteResultDto.Deleted);
        await Assert.That(File.Exists(path)).IsFalse();
        var dump = (await _runs.GetRunsAsync("t")).Single().Dump!;
        await Assert.That(dump.DeletedAt).IsEqualTo(_now);
        await Assert.That(dump.DeletedBy).IsEqualTo("alice");
        await Assert.That(dump.Exists).IsFalse();
    }

    [Test]
    public async Task DeleteDump_Twice_IsAlreadyDeleted()
    {
        await SeedRunWithDumpAsync();
        var service = CreateService();

        await service.DeleteDumpAsync("t", "job-1", "alice");
        var second = await service.DeleteDumpAsync("t", "job-1", "bob");

        await Assert.That(second).IsEqualTo(SecretSweepDumpDeleteResultDto.AlreadyDeleted);
        await Assert.That((await _runs.GetRunsAsync("t")).Single().Dump!.DeletedBy).IsEqualTo("alice");
    }

    [Test]
    public async Task DeleteDump_ExpiredByTheCleanup_IsAlreadyDeleted()
    {
        await SeedRunWithDumpAsync();
        await _runs.MarkDumpDeletedAsync("t", FileName, _now.AddDays(-1), null);

        var result = await CreateService().DeleteDumpAsync("t", "job-1", "alice");

        await Assert.That(result).IsEqualTo(SecretSweepDumpDeleteResultDto.AlreadyDeleted);
    }

    [Test]
    public async Task DeleteDump_UnknownRun_OrRunWithoutDump_IsNotFound()
    {
        await _runs.UpsertAsync("t", new SecretSweepRunDto { RunId = "verify-1", Mode = SecretSweepModeDto.Verify });
        var service = CreateService();

        await Assert.That(await service.DeleteDumpAsync("t", "nope", "alice"))
            .IsEqualTo(SecretSweepDumpDeleteResultDto.NotFound);
        await Assert.That(await service.DeleteDumpAsync("t", "verify-1", "alice"))
            .IsEqualTo(SecretSweepDumpDeleteResultDto.NotFound);
        await _storage.DidNotReceiveWithAnyArgs().DeleteFileAsync(default!);
    }

    [Test]
    public async Task GetRuns_RefreshesExistsAndSizeFromTheFile()
    {
        await SeedRunWithDumpAsync("job-1");
        await _runs.UpsertAsync("t", new SecretSweepRunDto
        {
            RunId = "job-2",
            Dump = new SecretSweepDumpDto { FileName = "gone.presweep.tar.gz", Exists = true, SizeBytes = 5 }
        });

        var runs = await CreateService().GetRunsAsync("t", 20);

        var gone = runs.Single(r => r.RunId == "job-2").Dump!;
        var present = runs.Single(r => r.RunId == "job-1").Dump!;
        await Assert.That(gone.Exists).IsFalse();
        await Assert.That(present.Exists).IsTrue();
        await Assert.That(present.SizeBytes).IsEqualTo("dump-content".Length);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
