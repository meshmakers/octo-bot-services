using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Secrets;

/// <summary>
///     AB#5561 — run history and early deletion of pre-sweep dumps against the artifact store (real file system
///     store, generated keys).
/// </summary>
public class ArtifactStoreSecretsTests : IDisposable
{
    private const string Tenant = "t";
    private readonly ArtifactTestEnvironment _env = new("k1");
    private readonly IBackupFileStorageService _files = Substitute.For<IBackupFileStorageService>();
    private readonly InMemorySecretSweepRunStore _runs = new();

    public void Dispose()
    {
        _env.Dispose();
    }

    private SecretSweepRunService CreateRunService(IBotArtifactStorage? storage = null)
    {
        return new SecretSweepRunService(_runs, _files, Substitute.For<ILogger<SecretSweepRunService>>(), null,
            storage ?? _env.Storage);
    }

    private async Task<StoredArtifact> SeedStoredDumpAsync(string runId = "run-1")
    {
        var stored = await _env.Storage.StoreFileAsync(ArtifactCategories.Presweep, Tenant,
            PreSweepDumps.NewBaseFileName(Tenant, DateTime.UtcNow), _env.WriteFile($"{runId}.dump",
                RandomNumberGenerator.GetBytes(300)), ArtifactEncryption.Required);
        await _runs.UpsertAsync(Tenant, new SecretSweepRunDto
        {
            RunId = runId,
            Mode = SecretSweepModeDto.Encrypt,
            Outcome = SecretSweepOutcomeDto.Succeeded,
            Dump = new SecretSweepDumpDto { FileName = stored.FileName, Exists = true, SizeBytes = 1 }
        });
        return stored;
    }

    [Test]
    public async Task RunList_ReadsExistsAndSizeFromTheStore()
    {
        var stored = await SeedStoredDumpAsync();

        var dump = (await CreateRunService().GetRunsAsync(Tenant, 20)).Single().Dump!;

        await Assert.That(dump.Exists).IsTrue();
        await Assert.That(dump.SizeBytes).IsEqualTo(stored.Size);

        await _env.Storage.DeleteAsync(ArtifactCategories.Presweep, Tenant, stored.FileName);
        var gone = (await CreateRunService().GetRunsAsync(Tenant, 20)).Single().Dump!;
        await Assert.That(gone.Exists).IsFalse();
    }

    [Test]
    public async Task EarlyDelete_RemovesTheDumpFromTheStore_ThenAnswersAlreadyDeleted()
    {
        var stored = await SeedStoredDumpAsync();
        var service = CreateRunService();

        await Assert.That(await service.DeleteDumpAsync(Tenant, "run-1", "carol"))
            .IsEqualTo(SecretSweepDumpDeleteResultDto.Deleted);
        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.Presweep, Tenant, stored.FileName))
            .IsNull();
        var dump = (await _runs.GetRunsAsync(Tenant)).Single().Dump!;
        await Assert.That(dump.DeletedBy).IsEqualTo("carol");
        await Assert.That(await service.DeleteDumpAsync(Tenant, "run-1", "carol"))
            .IsEqualTo(SecretSweepDumpDeleteResultDto.AlreadyDeleted);
        await Assert.That(await service.DeleteDumpAsync(Tenant, "unknown", "carol"))
            .IsEqualTo(SecretSweepDumpDeleteResultDto.NotFound);
    }

}
