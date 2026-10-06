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
///     AB#5561 — the jobs on the artifact store over a real file system store and generated keys: pre-sweep dump
///     (encrypt, store), tenant dump → store, staged upload → restore, hourly cleanup.
/// </summary>
public class ArtifactStoreJobsTests : ArtifactStoreJobTestBase
{
    [Test]
    public async Task PreSweepDump_IsEncryptedIntoTheStore_NeverPlain_AndLeavesNoScratchCopy()
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
    }

    [Test]
    public async Task TenantDump_IsStoredEncrypted_ResultIsAStoreReference_AndNoLocalCopyRemains()
    {
        var local = Path.Combine(_env.Directory, "dumps", Tenant, "tenant-1-20261006-120000-abcd1234.tar.gz");
        _files.GenerateDumpFileName(Tenant).Returns(Path.GetFileName(local));
        _files.GetDumpFilePath(Tenant, Path.GetFileName(local)).Returns(local);
        var job = new DumpRepositoryJob(Substitute.For<ILogger<DumpRepositoryJob>>(), _systemContext, _files,
            _env.Storage);

        var result = await job.Run(Tenant, false, null);

        await Assert.That(result).IsEqualTo(
            "octo-artifact:tenant-dumps/tenant-1/tenant-1-20261006-120000-abcd1234.tar.gz.octoenc");
        await Assert.That(File.Exists(local)).IsFalse();
        await using var download = await _env.Storage.OpenDownloadAsync(ArtifactCategories.TenantDumps, Tenant,
            "tenant-1-20261006-120000-abcd1234.tar.gz.octoenc");
        using var plain = new MemoryStream();
        await _env.Storage.CopyPlainAsync(download!, plain);
        await Assert.That(plain.ToArray().SequenceEqual(_dump)).IsTrue();
    }

    [Test]
    public async Task TenantDump_WithoutKeyRing_IsStoredPlain()
    {
        var storage = _env.CreateStorage(
            ArtifactTestEnvironment.CreateProtector(new Dictionary<string, byte[]>(), null));
        var local = Path.Combine(_env.Directory, "dumps", Tenant, "d.tar.gz");
        _files.GenerateDumpFileName(Tenant).Returns("d.tar.gz");
        _files.GetDumpFilePath(Tenant, "d.tar.gz").Returns(local);

        var result = await new DumpRepositoryJob(Substitute.For<ILogger<DumpRepositoryJob>>(), _systemContext, _files,
            storage).Run(Tenant, false, null);

        await Assert.That(result).IsEqualTo("octo-artifact:tenant-dumps/tenant-1/d.tar.gz");
        var raw = await _env.ReadStoredAsync(ArtifactCategories.TenantDumps, Tenant, "d.tar.gz");
        await Assert.That(raw.SequenceEqual(_dump)).IsTrue();
    }

    [Test]
    public async Task Restore_FromAnEncryptedStagedUpload_RestoresThePlainDump_AndConsumesTheStagingArtifact()
    {
        await _env.Storage.StoreFileAsync(ArtifactCategories.RestoreStaging, Tenant, "tus01",
            _env.WriteFile("upload", _dump), ArtifactEncryption.IfConfigured);
        _files.GetTusUploadFilePath(Tenant, "tus01").Returns(Path.Combine(_env.Directory, "no-local-upload"));
        var restores = CaptureRestores();
        var job = new RestoreRepositoryJob(Substitute.For<ILogger<RestoreRepositoryJob>>(), _systemContext, _files,
            null, _env.Storage);

        await job.Run(Tenant, "db-1", "tus01", null, false, null, null);

        await Assert.That(restores.Single().Content.SequenceEqual(_dump)).IsTrue();
        await Assert.That(await _env.Storage.FindRestoreStagingAsync(Tenant, "tus01")).IsNull();
        await Assert.That(Directory.EnumerateFiles(_env.ScratchDirectory).Any()).IsFalse();
    }

    [Test]
    public async Task Restore_WithoutStagedArtifact_FallsBackToTheLocalUpload()
    {
        var local = _env.WriteFile("tus02", _dump);
        _files.GetTusUploadFilePath(Tenant, "tus02").Returns(local);
        var restores = CaptureRestores();
        var job = new RestoreRepositoryJob(Substitute.For<ILogger<RestoreRepositoryJob>>(), _systemContext, _files,
            null, _env.Storage);

        await job.Run(Tenant, "db-1", "tus02", null, false, null, null);

        await Assert.That(restores.Single().Content.SequenceEqual(_dump)).IsTrue();
        await Assert.That(File.Exists(local)).IsFalse();
    }

    [Test]
    public async Task Cleanup_ExpiresStoreArtifactsPerCategory_AndRecordsExpiredPreSweepDumps()
    {
        await CreateRealCoordinator().SweepTenantAsync(Tenant, SecretSweepMode.Encrypt, SecretSweepTrigger.Manual,
            new SecretSweepRunInfo("run-6"), CancellationToken.None);
        var presweep = (await _runs.GetRunsAsync(Tenant)).Single().Dump!.FileName;
        _env.Age(ArtifactCategories.Presweep, Tenant, presweep, TimeSpan.FromDays(8));
        await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "old.tar.gz",
            _env.WriteFile("o", _dump), ArtifactEncryption.None);
        await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "new.tar.gz",
            _env.WriteFile("n", _dump), ArtifactEncryption.None);
        await _env.Storage.StoreFileAsync(ArtifactCategories.RestoreStaging, Tenant, "tus-old",
            _env.WriteFile("s", _dump), ArtifactEncryption.IfConfigured);
        _env.Age(ArtifactCategories.TenantDumps, Tenant, "old.tar.gz", TimeSpan.FromHours(25));
        _env.Age(ArtifactCategories.RestoreStaging, Tenant, "tus-old.octoenc", TimeSpan.FromHours(25));
        _files.CleanupStaleSecretBackupsAsync(Arg.Any<TimeSpan>()).Returns(Array.Empty<string>());

        var job = new CleanupStaleFilesJob(Substitute.For<ILogger<CleanupStaleFilesJob>>(), _files, 4, 7, _runs,
            null, _env.Storage);
        await job.Run(null);

        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.Presweep, Tenant, presweep)).IsNull();
        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.TenantDumps, Tenant, "old.tar.gz"))
            .IsNull();
        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.TenantDumps, Tenant, "new.tar.gz"))
            .IsNotNull();
        await Assert.That(await _env.Storage.FindRestoreStagingAsync(Tenant, "tus-old")).IsNull();
        var dump = (await _runs.GetRunsAsync(Tenant)).Single().Dump!;
        await Assert.That(dump.DeletedAt).IsNotNull();
        await Assert.That(dump.DeletedBy).IsNull();
    }
}
