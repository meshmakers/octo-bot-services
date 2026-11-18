using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

public partial class RestoreRepositoryJob
{
    private const string VerifiedCopySuffix = ".verified-restore";

    private async Task VerifyRegisteredArchiveAsync(string tenantId, string archivePath, CancellationToken ct)
    {
        if (archiveVerifier == null)
        {
            logger.LogError(
                "Restore of tenant '{TenantId}' refused: the archive must be a registered dump, but no verifier is wired",
                tenantId);
            throw new ArchiveNotRegisteredException();
        }

        await archiveVerifier.VerifyAsync(tenantId, archivePath, ct);
    }

    private async Task<string> CopyToJobOwnedFileAsync(string sourcePath, CancellationToken ct)
    {
        var copyPath = artifactStorage?.CreateScratchFilePath(VerifiedCopySuffix) ??
                       sourcePath + VerifiedCopySuffix + "-" + Guid.NewGuid().ToString("N");
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous);
        await source.CopyToAsync(target, ct);
        return copyPath;
    }

    private void DeleteJobOwnedFile(string? path)
    {
        if (path == null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not delete the verified restore copy '{Path}'", path);
        }
    }
}
