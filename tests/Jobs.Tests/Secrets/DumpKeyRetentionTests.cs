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
///     AB#5559 — restore check of a run's dump and the key-id retention status (<c>requiredKeyIds</c>,
///     <c>DumpKeyMissing</c>) against the artifact store (real file system store, generated keys).
/// </summary>
public class DumpKeyRetentionTests : IDisposable
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
    public async Task RestoreCheck_ReportsRestorable_Missing_Deleted_KeyMissing_AndNotFound()
    {
        var stored = await SeedStoredDumpAsync("run-ok");
        var service = CreateRunService();

        await Assert.That((await service.CheckDumpRestorableAsync(Tenant, "run-ok")).State)
            .IsEqualTo(SecretSweepDumpRestoreState.Restorable);
        await Assert.That((await service.CheckDumpRestorableAsync(Tenant, "nope")).State)
            .IsEqualTo(SecretSweepDumpRestoreState.NotFound);
        await Assert.That((await service.CheckDumpRestorableAsync("other", "run-ok")).State)
            .IsEqualTo(SecretSweepDumpRestoreState.NotFound);

        var rotated = _env.CreateStorage(ArtifactTestEnvironment.CreateProtector(
            new Dictionary<string, byte[]> { ["k2"] = RandomNumberGenerator.GetBytes(32) }, "k2"));
        var keyMissing = await CreateRunService(rotated).CheckDumpRestorableAsync(Tenant, "run-ok");
        await Assert.That(keyMissing.State).IsEqualTo(SecretSweepDumpRestoreState.KeyMissing);
        await Assert.That(keyMissing.KeyId).IsEqualTo("k1");

        await _env.Storage.DeleteAsync(ArtifactCategories.Presweep, Tenant, stored.FileName);
        await Assert.That((await service.CheckDumpRestorableAsync(Tenant, "run-ok")).State)
            .IsEqualTo(SecretSweepDumpRestoreState.Missing);

        await SeedStoredDumpAsync("run-del");
        await service.DeleteDumpAsync(Tenant, "run-del", "carol");
        await Assert.That((await service.CheckDumpRestorableAsync(Tenant, "run-del")).State)
            .IsEqualTo(SecretSweepDumpRestoreState.Deleted);
    }

    [Test]
    public async Task Status_ListsRequiredKeyIds_AndWarnsDumpKeyMissing_WhenAKeyLeftTheRing()
    {
        await SeedStoredDumpAsync();
        var attributeProtector = Substitute.For<ISecretAttributeProtector>();
        attributeProtector.IsConfigured.Returns(true);
        attributeProtector.ActiveKeyId.Returns("k2");

        // Same ring as the dump: no warning.
        var withKey = await CreateStatusService(attributeProtector, _env.Storage).GetStatusAsync(Tenant);
        await Assert.That(withKey.RequiredKeyIds.ToArray()).IsEquivalentTo(new[] { "k1" });
        await Assert.That(withKey.Warnings).DoesNotContain(BotSecretEnvironmentWarningCodes.DumpKeyMissing);

        // k1 removed from the ring while its dump still exists.
        var rotated = _env.CreateStorage(ArtifactTestEnvironment.CreateProtector(
            new Dictionary<string, byte[]> { ["k2"] = RandomNumberGenerator.GetBytes(32) }, "k2"));
        var withoutKey = await CreateStatusService(attributeProtector, rotated).GetStatusAsync(Tenant);
        await Assert.That(withoutKey.RequiredKeyIds.ToArray()).IsEquivalentTo(new[] { "k1" });
        await Assert.That(withoutKey.Warnings).Contains(BotSecretEnvironmentWarningCodes.DumpKeyMissing);
    }

    [Test]
    public async Task Status_IgnoresRestoreStagingUploads_WithForeignKeyIds()
    {
        // A tenant user uploads an .octoenc of another environment (key id 'foreign'): restore staging is not
        // written by this instance and must neither appear in requiredKeyIds nor raise DumpKeyMissing.
        var foreign = _env.CreateStorage(ArtifactTestEnvironment.CreateProtector(
            new Dictionary<string, byte[]> { ["foreign"] = RandomNumberGenerator.GetBytes(32) }, "foreign"));
        await foreign.StoreFileAsync(ArtifactCategories.RestoreStaging, Tenant, "tus-foreign",
            _env.WriteFile("upload", RandomNumberGenerator.GetBytes(300)), ArtifactEncryption.Required);
        await SeedStoredDumpAsync();
        var attributeProtector = Substitute.For<ISecretAttributeProtector>();
        attributeProtector.IsConfigured.Returns(true);
        attributeProtector.ActiveKeyId.Returns("k1");

        var status = await CreateStatusService(attributeProtector, _env.Storage).GetStatusAsync(Tenant);

        await Assert.That(status.RequiredKeyIds.ToArray()).IsEquivalentTo(new[] { "k1" });
        await Assert.That(status.Warnings).DoesNotContain(BotSecretEnvironmentWarningCodes.DumpKeyMissing);
    }

    [Test]
    public async Task Status_StoreUnreachable_StillAnswers()
    {
        var storage = Substitute.For<IBotArtifactStorage>();
        storage.GetEncryptedArtifactHeadersAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<EncryptedArtifactHeader>>(new IOException("down")));
        var attributeProtector = Substitute.For<ISecretAttributeProtector>();

        var status = await CreateStatusService(attributeProtector, storage).GetStatusAsync(Tenant);

        await Assert.That(status.RequiredKeyIds).IsEmpty();
        await Assert.That(status.Warnings).Contains(SecretEnvironmentWarningCodes.NoKeyRing);
    }

    private SecretEnvironmentStatusService CreateStatusService(ISecretAttributeProtector protector,
        IBotArtifactStorage storage)
    {
        return new SecretEnvironmentStatusService(protector, Options.Create(new SecretEncryptionOptions()),
            Options.Create(new SecretSweepJobOptions()), _runs, null, storage);
    }
}
