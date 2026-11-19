using Meshmakers.Octo.Backend.Jobs.Jobs;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

public class JobOwnedCopyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("job-owned-copy-").FullName;

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    [Test]
    public async Task ACopy_WritesTheSourceBytesIntoANewFile()
    {
        var sourcePath = Path.Combine(_directory, "source");
        var targetPath = Path.Combine(_directory, "target");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);

        await RestoreRepositoryJob.CopyIntoNewFileAsync(sourcePath, targetPath, CancellationToken.None);

        await Assert.That((await File.ReadAllBytesAsync(targetPath)).SequenceEqual(new byte[] { 1, 2, 3 })).IsTrue();
    }

    [Test]
    public async Task ATargetThatAlreadyExists_IsRefused_AndLeftUntouched()
    {
        var sourcePath = Path.Combine(_directory, "source");
        var existingTargetPath = Path.Combine(_directory, "existing");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        await File.WriteAllBytesAsync(existingTargetPath, [9, 9]);

        await Assert.That(async () =>
                await RestoreRepositoryJob.CopyIntoNewFileAsync(sourcePath, existingTargetPath, CancellationToken.None))
            .Throws<IOException>();

        await Assert.That((await File.ReadAllBytesAsync(existingTargetPath)).SequenceEqual(new byte[] { 9, 9 }))
            .IsTrue();
    }

    [Test]
    public async Task AnInterruptedCopy_LeavesNoPartialTarget()
    {
        var sourcePath = Path.Combine(_directory, "source");
        var targetPath = Path.Combine(_directory, "target");
        await File.WriteAllBytesAsync(sourcePath, new byte[1_000_000]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.That(async () =>
                await RestoreRepositoryJob.CopyIntoNewFileAsync(sourcePath, targetPath, cancelled.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(File.Exists(targetPath)).IsFalse();
    }
}
