using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Tests.DumpRegistry;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs;

public partial class RestoreRepositoryNativeTests
{
    private readonly InMemoryDumpHashRegistry _dumpRegistry = new();

    [Test]
    public async Task Format2_RegisteredDumpOfTheTenant_RestoresMongoAndArchivesFromAJobOwnedCopy()
    {
        var restores = CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 1200))]);
        await RegisterStagedContainerAsync();

        var result = await CheckedRestoreAsync();

        await Assert.That(restores.Single().Content.SequenceEqual(_dump)).IsTrue();
        await Assert.That(result!.BackupFormatVersion).IsEqualTo(2);
        await Assert.That(_plans.Count).IsEqualTo(1);
        await Assert.That(Directory.Exists(_env.ScratchDirectory)
            ? Directory.EnumerateFiles(_env.ScratchDirectory).Count()
            : 0).IsEqualTo(0);
        await Assert.That(await _env.Storage.FindRestoreStagingAsync(Tenant, "file-1")).IsNull();
    }

    [Test]
    public async Task Format2_UnregisteredContainer_IsRefusedBeforeAnyDatabaseOrArchiveWrite()
    {
        var restores = CaptureRestores();
        SetupPostRestore((RtIdA, CkArchiveStatus.Activated));
        await StageContainerAsync([NativeEntry(RtIdA, CkArchiveStatus.Activated, ("s1", 1200))]);
        await _dumpRegistry.RegisterAsync(new DumpHashEntry(Tenant, new string('0', 64), DateTime.UtcNow,
            DateTime.UtcNow.AddDays(1), "other.zip", 1, null, DumpHashSources.Dump));

        await Assert.That(async () => await CheckedRestoreAsync()).Throws<ArchiveNotRegisteredException>();

        await Assert.That(restores.Count).IsEqualTo(0);
        await Assert.That(_plans.Count).IsEqualTo(0);
        await _lifecycle.DidNotReceiveWithAnyArgs().DisableAsync(default(OctoObjectId));
        await _repository.DidNotReceiveWithAnyArgs().DeleteArchiveAsync(default(OctoObjectId));
        await Assert.That(await _env.Storage.FindRestoreStagingAsync(Tenant, "file-1")).IsNull();
    }

    private async Task RegisterStagedContainerAsync()
    {
        var staged = await _env.Storage.FindRestoreStagingAsync(Tenant, "file-1");
        await using var stagedPlaintext = await _env.Storage.OpenPlainReadAsync(ArtifactCategories.RestoreStaging,
            Tenant, staged!.Value.FileName);
        var sha256 = await Sha256OfContent.OfStreamAsync(stagedPlaintext!);
        await _dumpRegistry.RegisterAsync(new DumpHashEntry(Tenant, sha256, DateTime.UtcNow,
            DateTime.UtcNow.AddDays(1), "dump.octobak.zip", 1, "job-1", DumpHashSources.Dump));
    }

    private Task<RestoreRepositoryResult?> CheckedRestoreAsync()
    {
        var job = new RestoreRepositoryJob(_log, _systemContext, _files, null, _env.Storage, _factory, null,
            _streamFactory, archiveVerifier: new ArchiveRegistrationVerifier(_dumpRegistry, TimeProvider.System,
                NullLogger<ArchiveRegistrationVerifier>.Instance));
        return job.Run(Tenant, "tenantdb", "file-1", null, true, false, ArchiveRegistrationCheck.Required, null, null);
    }
}
