using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

public class RestoreArchiveCheckJobTests : ArtifactStoreJobTestBase
{
    private const string OtherTenant = "tenant-2";
    private readonly InMemoryDumpHashRegistry _registry = new();

    [Test]
    public async Task UneditedDumpOfTheOwnTenant_RoundTripsThroughDownloadAndUpload_AndRestores()
    {
        var downloadedDump = await DumpAndDownloadAsync();
        await StageUploadAsync(Tenant, "tus-own", downloadedDump);
        var restores = CaptureRestores();

        await RestoreJob().Run(Tenant, "db-1", "tus-own", null, false, false, ArchiveRegistrationCheck.Required,
            null, null);

        await Assert.That(restores.Single().Content.SequenceEqual(downloadedDump)).IsTrue();
        await Assert.That(Directory.EnumerateFiles(_env.ScratchDirectory).Any()).IsFalse();
    }

    [Test]
    public async Task EditedDump_IsRefusedBeforeMongorestore_AndTheUploadIsConsumed()
    {
        var editedDump = await DumpAndDownloadAsync();
        editedDump[editedDump.Length / 3] ^= 0x20;
        await StageUploadAsync(Tenant, "tus-edited", editedDump);
        var restores = CaptureRestores();

        await AssertRefusedAsync(() => RestoreJob().Run(Tenant, "db-1", "tus-edited", null, false, false,
            ArchiveRegistrationCheck.Required, null, null));

        await Assert.That(restores.Count).IsEqualTo(0);
        await Assert.That(await _env.Storage.FindRestoreStagingAsync(Tenant, "tus-edited")).IsNull();
        await Assert.That(Directory.EnumerateFiles(_env.ScratchDirectory).Any()).IsFalse();
    }

    [Test]
    public async Task UnknownArchive_IsRefused()
    {
        await StageUploadAsync(Tenant, "tus-unknown", _dump);
        var restores = CaptureRestores();

        await AssertRefusedAsync(() => RestoreJob().Run(Tenant, "db-1", "tus-unknown", null, false, false,
            ArchiveRegistrationCheck.Required, null, null));

        await Assert.That(restores.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DumpOfAnotherTenant_IsRefusedForTheTargetTenant()
    {
        var dumpOfTenantOne = await DumpAndDownloadAsync();
        await StageUploadAsync(OtherTenant, "tus-foreign", dumpOfTenantOne);
        var restores = CaptureRestores();

        await AssertRefusedAsync(() => RestoreJob().Run(OtherTenant, "db-2", "tus-foreign", null, false, false,
            ArchiveRegistrationCheck.Required, null, null));

        await Assert.That(restores.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SystemOperator_RestoresAnUnknownArchive()
    {
        await StageUploadAsync(Tenant, "tus-operator", _dump);
        var restores = CaptureRestores();

        await RestoreJob().Run(Tenant, "db-1", "tus-operator", null, false, false,
            ArchiveRegistrationCheck.NotRequired, null, null);

        await Assert.That(restores.Single().Content.SequenceEqual(_dump)).IsTrue();
    }

    [Test]
    public async Task OverloadsWithoutTheCheck_RequireARegisteredArchive()
    {
        await StageUploadAsync(Tenant, "tus-legacy-1", _dump);
        await StageUploadAsync(Tenant, "tus-legacy-2", _dump);
        await StageUploadAsync(Tenant, "tus-legacy-3", _dump);
        var restores = CaptureRestores();

        await AssertRefusedAsync(() => RestoreJob().Run(Tenant, "db-1", "tus-legacy-1", null, false, null, null));
        await AssertRefusedAsync(() => RestoreJob().Run(Tenant, "db-1", "tus-legacy-2", null, false, true, null,
            null));
        await AssertRefusedAsync(() => RestoreJob().RunForOperation(Tenant, "db-1", "tus-legacy-3", null, false,
            "operation-1", null, null));

        await Assert.That(restores.Count).IsEqualTo(0);
    }

    [Test]
    public async Task WithoutAVerifier_ACheckedRestoreIsRefused()
    {
        var downloadedDump = await DumpAndDownloadAsync();
        await StageUploadAsync(Tenant, "tus-no-verifier", downloadedDump);
        var restores = CaptureRestores();
        var jobWithoutVerifier = new RestoreRepositoryJob(Substitute.For<ILogger<RestoreRepositoryJob>>(),
            _systemContext, _files, null, _env.Storage);

        await AssertRefusedAsync(() => jobWithoutVerifier.Run(Tenant, "db-1", "tus-no-verifier", null, false, false,
            ArchiveRegistrationCheck.Required, null, null));

        await Assert.That(restores.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LocalUploadOfARegisteredDump_IsVerifiedOnAJobOwnedCopy_AndRestores()
    {
        var downloadedDump = await DumpAndDownloadAsync();
        var localUpload = _env.WriteFile("local-upload", downloadedDump);
        _files.GetTusUploadFilePath(Tenant, "tus-local").Returns(localUpload);
        var restores = CaptureRestores();

        await RestoreJob().Run(Tenant, "db-1", "tus-local", null, false, false, ArchiveRegistrationCheck.Required,
            null, null);

        await Assert.That(restores.Single().Content.SequenceEqual(downloadedDump)).IsTrue();
        await Assert.That(Directory.EnumerateFiles(_env.ScratchDirectory).Any()).IsFalse();
    }

    private async Task<byte[]> DumpAndDownloadAsync()
    {
        var localDumpPath = Path.Combine(_env.Directory, "dumps", Tenant, $"{Guid.NewGuid():N}.tar.gz");
        _files.GenerateDumpFileName(Tenant).Returns(Path.GetFileName(localDumpPath));
        _files.GetDumpFilePath(Tenant, Path.GetFileName(localDumpPath)).Returns(localDumpPath);
        var recorder = new DumpHashRecorder(_registry, Options.Create(new DumpHashRegistryOptions()),
            TimeProvider.System, NullLogger<DumpHashRecorder>.Instance, _env.Storage);
        var dumpJob = new DumpRepositoryJob(Substitute.For<ILogger<DumpRepositoryJob>>(), _systemContext, _files,
            _env.Storage, dumpHashRecorder: recorder);

        var jobResult = await dumpJob.Run(Tenant, false, null);

        StoredArtifact.TryParseResultReference(jobResult, out var artifact);
        await using var download = await _env.Storage.OpenDownloadAsync(ArtifactCategories.TenantDumps, Tenant,
            artifact!.FileName);
        using var downloaded = new MemoryStream();
        await _env.Storage.CopyPlainAsync(download!, downloaded);
        return downloaded.ToArray();
    }

    private async Task StageUploadAsync(string tenantId, string uploadId, byte[] content)
    {
        await _env.Storage.StoreFileAsync(ArtifactCategories.RestoreStaging, tenantId, uploadId,
            _env.WriteFile($"upload-{uploadId}", content), ArtifactEncryption.IfConfigured);
        _files.GetTusUploadFilePath(tenantId, uploadId).Returns(Path.Combine(_env.Directory, "no-local-upload"));
    }

    private RestoreRepositoryJob RestoreJob() =>
        new(Substitute.For<ILogger<RestoreRepositoryJob>>(), _systemContext, _files, null, _env.Storage,
            archiveVerifier: new ArchiveRegistrationVerifier(_registry, TimeProvider.System,
                NullLogger<ArchiveRegistrationVerifier>.Instance));

    private static async Task AssertRefusedAsync(Func<Task<RestoreRepositoryResult?>> restore)
    {
        var refusal = await Assert.That(async () => await restore()).Throws<ArchiveNotRegisteredException>();
        await Assert.That(refusal!.Message).IsEqualTo(ArchiveNotRegisteredException.RefusalMessage);
    }
}
