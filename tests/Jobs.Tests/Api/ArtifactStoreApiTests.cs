using System.Net;
using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Services.ArtifactStorage;
using NSubstitute;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#5561 through the real request pipeline: the tenant dump download from the artifact store (decrypted on
///     the fly, same contract as before) and the restore upload validation against the store.
/// </summary>
public class ArtifactStoreApiTests
{
    private const string Child = JobsApiTestHost.Child;
    private const string Unrelated = JobsApiTestHost.Unrelated;
    private const string JobId = "6512a1b2c3d4e5f60102bbbb";


    [Test]
    public async Task Download_OfAnEncryptedTenantDump_IsThePlainFile_UnderItsOriginalName()
    {
        using var host = await JobsApiTestHost.StartAsync();
        var plaintext = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 11);
        var stored = await host.Artifacts.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Child,
            "childtenant-20261006-120000-abcd1234.tar.gz", host.Artifacts.WriteFile("dump", plaintext),
            ArtifactEncryption.IfConfigured);
        await Assert.That(stored.Encrypted).IsTrue();
        await SeedDumpJobAsync(host, Child, stored.ToResultReference());

        var response = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Content.Headers.ContentType!.MediaType).IsEqualTo("application/gzip");
        await Assert.That(response.Content.Headers.ContentDisposition!.FileName!.Trim('"'))
            .IsEqualTo("childtenant-20261006-120000-abcd1234.tar.gz");
        await Assert.That(response.Content.Headers.ContentLength).IsEqualTo(plaintext.LongLength);
        await Assert.That((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(plaintext)).IsTrue();
    }

    [Test]
    public async Task Download_ArtifactExpired_Is404()
    {
        using var host = await JobsApiTestHost.StartAsync();
        await SeedDumpJobAsync(host, Child, "octo-artifact:tenant-dumps/childtenant/gone.tar.gz.octoenc");

        var response = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("octo-artifact:tenant-dumps/othertenant/x.tar.gz")]
    [Arguments("octo-artifact:presweep/childtenant/x.presweep.octoenc")]
    public async Task Download_ReferenceToAnotherTenantOrToAPreSweepDump_IsForbidden(string reference)
    {
        using var host = await JobsApiTestHost.StartAsync();
        await host.Artifacts.Storage.StoreFileAsync(ArtifactCategories.Presweep, Child, "x.presweep",
            host.Artifacts.WriteFile("p", [1, 2, 3]), ArtifactEncryption.Required);
        await SeedDumpJobAsync(host, Child, reference);

        var response = await host.GetAsync($"/{Child}/v1/jobs/download?id={JobId}", JobsApiTestHost.UserToken(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task RestoreFromUpload_StagedInTheStore_IsValidatedThere()
    {
        using var host = await JobsApiTestHost.StartAsync();
        await host.Artifacts.Storage.StoreFileAsync(ArtifactCategories.RestoreStaging, Child, "emptyupload",
            host.Artifacts.WriteFile("empty", []), ArtifactEncryption.None);

        // An empty staged upload is refused although the (substituted) local tus path holds a file: the store wins.
        var empty = await host.PostAsync(
            $"/{Child}/v1/jobs/restore-from-upload?tusFileId=emptyupload&databaseName=db1",
            JobsApiTestHost.UserToken(Child));
        await Assert.That(empty.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(host.EnqueuedJobCount).IsEqualTo(0);

        await host.Artifacts.Storage.StoreFileAsync(ArtifactCategories.RestoreStaging, Child, "goodupload",
            host.Artifacts.WriteFile("good", [1, 2, 3]), ArtifactEncryption.IfConfigured);
        var good = await host.PostAsync(
            $"/{Child}/v1/jobs/restore-from-upload?tusFileId=goodupload&databaseName=db1",
            JobsApiTestHost.UserToken(Child));
        await Assert.That(good.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(host.LastEnqueuedJob().Type).IsEqualTo(typeof(IRestoreRepositoryJob));
    }

    private static async Task SeedDumpJobAsync(JobsApiTestHost host, string tenantId, string result)
    {
        var response = await host.PostAsync($"/{tenantId}/v1/jobs/dump-repository",
            JobsApiTestHost.UserToken(tenantId));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        host.SeedSucceededJob(JobId, host.LastEnqueuedJob(), result);
    }
}
