using Meshmakers.Octo.Backend.BotServices.Services;
using Meshmakers.Octo.Backend.Jobs.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     AB#8447 review: the upload check of the REST job routes and the async restore/import methods never looks outside the
///     calling tenant's upload directory — an id that walks into another tenant's directory is "not found", even when the
///     file exists there.
/// </summary>
public class StagedUploadCheckTests
{
    [Test]
    public async Task UploadIdWalkingIntoAnotherTenant_IsNotFound()
    {
        var root = Directory.CreateTempSubdirectory("l2ab8447-tus-");
        try
        {
            var storage = new BackupFileStorageService(Path.Combine(root.FullName, "tus"), Path.Combine(root.FullName, "dumps"),
                NullLogger<BackupFileStorageService>.Instance);
            var foreign = storage.GetTusUploadFilePath("tenant-b", "abc.tus-info");
            Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
            await File.WriteAllTextAsync(foreign, "not empty");
            var store = Substitute.For<ISharedUploadStore>();

            var traversal = await StagedUploadCheck.CheckAsync(store, storage, "tenant-a", "../tenant-b/abc.tus-info",
                CancellationToken.None);
            var own = await StagedUploadCheck.CheckAsync(store, storage, "tenant-b", "abc.tus-info", CancellationToken.None);

            await Assert.That(traversal).IsEqualTo(StagedUploadProblem.NotFound);
            await Assert.That(own).IsEqualTo(StagedUploadProblem.None);
        }
        finally
        {
            root.Delete(true);
        }
    }
}
