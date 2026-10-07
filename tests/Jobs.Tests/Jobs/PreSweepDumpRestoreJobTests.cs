using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Secrets;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

/// <summary>
///     AB#5559 — restore of a run's pre-sweep dump: decrypted from the artifact store, restored with mongorestore
///     into the same tenant, followed by a Verify; deleted dumps, foreign runs and missing keys restore nothing.
/// </summary>
public class PreSweepDumpRestoreJobTests : ArtifactStoreJobTestBase
{
    private RestorePreSweepDumpJob CreateRestoreJob(IBotArtifactStorage? storage = null)
    {
        return new RestorePreSweepDumpJob(Substitute.For<ILogger<RestorePreSweepDumpJob>>(), _systemContext, _runs,
            storage ?? _env.Storage, _files, _coordinator);
    }

    [Test]
    public async Task PreSweepDump_IsEncryptedIntoTheStore_AndTheRestoreJobRestoresTheOriginalDump()
    {
        var report = await CreateRealCoordinator().SweepTenantAsync(Tenant, SecretSweepMode.Encrypt,
            SecretSweepTrigger.Manual, new SecretSweepRunInfo("run-1", "alice"), CancellationToken.None);

        await Assert.That(report.Outcome).IsEqualTo(SecretSweepOutcome.Succeeded);
        var fileName = report.BackupFileName!;
        await Assert.That(fileName).StartsWith("tenant-1-");
        await Assert.That(fileName).EndsWith(PreSweepDumps.FileSuffix);
        // Stored encrypted only - never the plaintext dump - and no scratch copy left behind.
        var raw = await _env.ReadStoredAsync(ArtifactCategories.Presweep, Tenant, fileName);
        await Assert.That(System.Text.Encoding.ASCII.GetString(raw, 0, 8)).IsEqualTo(SecretFileFormat.Magic);
        await Assert.That(Directory.EnumerateFiles(_env.ScratchDirectory).Any()).IsFalse();
        var run = (await _runs.GetRunsAsync(Tenant)).Single();
        await Assert.That(run.Dump!.FileName).IsEqualTo(fileName);
        await Assert.That(run.Dump.SizeBytes).IsEqualTo(raw.LongLength);

        var restores = CaptureRestores();
        var result = await CreateRestoreJob().Run(Tenant, "run-1", "alice", null, null);

        await Assert.That(restores.Count).IsEqualTo(1);
        await Assert.That(restores[0].Database).IsEqualTo("tenant1db");
        await Assert.That(restores[0].Source).IsEqualTo("tenant1db");
        await Assert.That(restores[0].Content.SequenceEqual(_dump)).IsTrue();
        await Assert.That(result!.FileName).IsEqualTo(fileName);
        await Assert.That(result.SecretSweep!.RemainingLegacyValues).IsEqualTo(2);
        await _coordinator.Received(1).SweepTenantAsync(Tenant, SecretSweepMode.Verify, SecretSweepTrigger.Restore,
            Arg.Is<SecretSweepRunInfo?>(i => i!.TriggeredBy == "alice"), Arg.Any<CancellationToken>());
        await Assert.That(Directory.EnumerateFiles(_env.ScratchDirectory).Any()).IsFalse();
    }

    [Test]
    public async Task RestoreJob_DeletedDump_FailsWithoutRestoring()
    {
        await _runs.UpsertAsync(Tenant, new SecretSweepRunDto
        {
            RunId = "run-2",
            Dump = new SecretSweepDumpDto { FileName = "x.presweep.octoenc", DeletedAt = DateTime.UtcNow }
        });
        var restores = CaptureRestores();

        await Assert.That(async () => await CreateRestoreJob().Run(Tenant, "run-2", null, null, null))
            .Throws<JobFailedException>();
        await Assert.That(restores).IsEmpty();
    }

    [Test]
    public async Task RestoreJob_RunOfAnotherTenant_IsNotFound()
    {
        await CreateRealCoordinator().SweepTenantAsync(Tenant, SecretSweepMode.Encrypt, SecretSweepTrigger.Manual,
            new SecretSweepRunInfo("run-3"), CancellationToken.None);
        var restores = CaptureRestores();

        await Assert.That(async () => await CreateRestoreJob().Run("tenant-2", "run-3", null, null, null))
            .Throws<JobFailedException>();
        await Assert.That(restores).IsEmpty();
    }

    [Test]
    public async Task RestoreJob_KeyRemovedFromTheRing_FailsWithDumpKeyMissing_AndRestoresNothing()
    {
        await CreateRealCoordinator().SweepTenantAsync(Tenant, SecretSweepMode.Encrypt, SecretSweepTrigger.Manual,
            new SecretSweepRunInfo("run-4"), CancellationToken.None);
        var rotated = _env.CreateStorage(ArtifactTestEnvironment.CreateProtector(
            new Dictionary<string, byte[]> { ["k2"] = RandomNumberGenerator.GetBytes(32) }, "k2"));
        var restores = CaptureRestores();

        var error = await Assert.That(async () => await CreateRestoreJob(rotated).Run(Tenant, "run-4", null, null,
            null)).Throws<JobFailedException>();

        await Assert.That(error!.Message).Contains("DumpKeyMissing");
        await Assert.That(restores).IsEmpty();
        await Assert.That(Directory.EnumerateFiles(_env.ScratchDirectory).Any()).IsFalse();
    }

    [Test]
    public async Task RestoreJob_LegacyLocalDump_IsRestoredFromTheDisk()
    {
        var legacy = _env.WriteFile("t.presweep.tar.gz", _dump);
        _files.GetSecretBackupFilePath(Tenant, "t.presweep.tar.gz").Returns(legacy);
        await _runs.UpsertAsync(Tenant, new SecretSweepRunDto
        {
            RunId = "run-5", Dump = new SecretSweepDumpDto { FileName = "t.presweep.tar.gz" }
        });
        var restores = CaptureRestores();

        await CreateRestoreJob().Run(Tenant, "run-5", null, null, null);

        await Assert.That(restores.Single().Content.SequenceEqual(_dump)).IsTrue();
    }
}
