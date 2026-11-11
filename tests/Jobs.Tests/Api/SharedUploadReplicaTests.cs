using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Backend.Jobs.Tests.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ArchiveImportMode = Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ArchiveImportMode;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Api;

/// <summary>
///     AB#8736, acceptance criterion 2: the archive data import works with more than one bot replica. Two hosts play
///     two replicas (each with its own process state and an empty local tus directory) over one artifact store; the
///     ingress has no affinity, so every request of the upload goes to whichever replica, and the job runs on a third.
/// </summary>
public class SharedUploadReplicaTests
{
    private const string Child = JobsApiTestHost.Child;
    private const string ArchiveRtId = "665f00000000000000000e21";

    [Test]
    public async Task ArchiveImport_UploadAcrossTwoReplicas_JobStartOnEither_JobOnAThird_ReadsTheSameBytes()
    {
        using var artifacts = new ArtifactTestEnvironment("k1");
        using var replicaA = await JobsApiTestHost.StartReplicaAsync(artifacts, "a");
        using var replicaB = await JobsApiTestHost.StartReplicaAsync(artifacts, "b");
        var token = JobsApiTestHost.UserToken(Child);

        var snapshot = Snapshot();
        var zip = BuildExportZip(snapshot, "{\"voltage\":1.5}\n{\"voltage\":2.5}\n");

        // Creation on A, the offset HEAD on B (the 404 of the bug), the parts alternately on A and B.
        var location = await CreateUploadAsync(replicaA, token, zip.Length);
        await Assert.That(await ReadOffsetAsync(replicaB, token, location)).IsEqualTo(0L);

        var half = zip.Length / 2;
        await Assert.That(await PatchAsync(replicaA, token, location, 0, zip[..half])).IsEqualTo((long)half);
        await Assert.That(await ReadOffsetAsync(replicaB, token, location)).IsEqualTo((long)half);
        await Assert.That(await PatchAsync(replicaB, token, location, half, zip[half..])).IsEqualTo((long)zip.Length);

        // Nothing is copied at the completion. A client that lost the response of the final PATCH and asks again gets
        // the full offset from either replica (and so finishes instead of failing).
        var uploadId = location.Split('/').Last();
        await Assert.That(await ReadOffsetAsync(replicaA, token, location)).IsEqualTo((long)zip.Length);
        await Assert.That(await artifacts.Storage.FindRestoreStagingAsync(Child, uploadId)).IsNull();
        await Assert.That(await CountUploadObjectsAsync(artifacts, uploadId)).IsEqualTo(3);

        // The job start reaches A, which never held a byte of the upload on its disk.
        var start = await replicaA.PostAsync(
            $"/{Child}/v1/jobs/import-archive-data-from-upload?tusFileId={uploadId}&archiveRtId={ArchiveRtId}", token);
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var enqueued = replicaA.LastEnqueuedJob();
        await Assert.That(enqueued.Type).IsEqualTo(typeof(IImportArchiveDataJob));
        await Assert.That((string)enqueued.Args[2]!).IsEqualTo(uploadId);

        // The job runs on a third replica and reads exactly the uploaded rows.
        var importedRows = new List<IReadOnlyDictionary<string, object?>>();
        var job = CreateImportJob(artifacts.CreateReplicaStorage("c"), snapshot, importedRows);
        await job.Run(Child, ArchiveRtId, uploadId, ArchiveImportMode.InsertOnly, null);

        await Assert.That(importedRows.Count).IsEqualTo(2);
        await Assert.That(Convert.ToDouble(importedRows[1]["voltage"], System.Globalization.CultureInfo.InvariantCulture))
            .IsEqualTo(2.5);

        // The upload is consumed by the job; nothing of it is left in the store or a scratch directory.
        await Assert.That(await CountUploadObjectsAsync(artifacts, uploadId)).IsEqualTo(0);
        await Assert.That(await artifacts.Storage.FindRestoreStagingAsync(Child, uploadId)).IsNull();
        await Assert.That(Directory.EnumerateFiles(Path.Combine(artifacts.Directory, "scratch-c")).Any()).IsFalse();
    }

    [Test]
    public async Task Restore_UploadOnA_JobStartOnB_IsAcceptedWithTheTusFileId_AndAnIncompleteUploadIsRefused()
    {
        using var artifacts = new ArtifactTestEnvironment("k1");
        using var replicaA = await JobsApiTestHost.StartReplicaAsync(artifacts, "a");
        using var replicaB = await JobsApiTestHost.StartReplicaAsync(artifacts, "b");
        var token = JobsApiTestHost.UserToken(Child);
        var dump = RandomNumberGenerator.GetBytes(4096);

        var location = await CreateUploadAsync(replicaA, token, dump.Length, "databaseName");
        var uploadId = location.Split('/').Last();
        await PatchAsync(replicaA, token, location, 0, dump[..1000]);
        var restorePath = $"/{Child}/v1/jobs/restore-from-upload?tusFileId={uploadId}&databaseName=db1";

        var early = await replicaB.PostAsync(restorePath, token);
        await Assert.That(early.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(replicaB.EnqueuedJobCount).IsEqualTo(0);

        await PatchAsync(replicaB, token, location, 1000, dump[1000..]);
        var start = await replicaB.PostAsync(restorePath, token);

        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((string)replicaB.LastEnqueuedJob().Args[2]!).IsEqualTo(uploadId);
    }

    [Test]
    public async Task ArchiveImport_FailingJob_DeletesTheStagedUploadToo()
    {
        using var artifacts = new ArtifactTestEnvironment("k1");
        using var replicaA = await JobsApiTestHost.StartReplicaAsync(artifacts, "a");
        var token = JobsApiTestHost.UserToken(Child);
        var zip = BuildExportZip(Snapshot(), "{}\n");
        var location = await CreateUploadAsync(replicaA, token, zip.Length);
        await PatchAsync(replicaA, token, location, 0, zip);
        var uploadId = location.Split('/').Last();

        // The live archive is Activated: the import refuses (§7.1) after the upload was read.
        var job = CreateImportJob(artifacts.CreateReplicaStorage("b"), Snapshot(CkArchiveStatus.Activated), []);
        await Assert.That(async () => await job.Run(Child, ArchiveRtId, uploadId, ArchiveImportMode.InsertOnly, null))
            .Throws<JobFailedException>();

        await Assert.That(await CountUploadObjectsAsync(artifacts, uploadId)).IsEqualTo(0);
        await Assert.That(Directory.EnumerateFiles(Path.Combine(artifacts.Directory, "scratch-b")).Any()).IsFalse();
    }

    [Test]
    public async Task UploadStartedOnOneReplica_IsUnknownToNoReplica_AndForeignTenantsCannotSeeIt()
    {
        using var artifacts = new ArtifactTestEnvironment("k1");
        using var replicaA = await JobsApiTestHost.StartReplicaAsync(artifacts, "a");
        using var replicaB = await JobsApiTestHost.StartReplicaAsync(artifacts, "b");
        var token = JobsApiTestHost.UserToken(Child);
        var location = await CreateUploadAsync(replicaA, token, 10);

        // Same id on another tenant's route: the parent administers the child, so it may address the route, but the
        // upload belongs to the child's key prefix only.
        var foreign = location.Replace($"/{Child}/", $"/{JobsApiTestHost.Parent}/", StringComparison.Ordinal);
        using var head = new HttpRequestMessage(HttpMethod.Head, foreign);
        head.Headers.Add("Tus-Resumable", "1.0.0");
        var response = await replicaB.SendAsync(head, JobsApiTestHost.UserToken(JobsApiTestHost.Parent));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(await ReadOffsetAsync(replicaB, token, location)).IsEqualTo(0L);
    }

    [Test]
    public async Task Upload_WithoutTheTenantManagementRole_IsForbidden_AndCreatesNothing()
    {
        using var artifacts = new ArtifactTestEnvironment("k1");
        using var replicaA = await JobsApiTestHost.StartReplicaAsync(artifacts, "a");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{Child}/v1/tus-upload");
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Length", "10");
        request.Headers.Add("Upload-Metadata", $"archiveRtId {Base64(ArchiveRtId)}");
        request.Content = new ByteArrayContent([]);

        var response = await replicaA.SendAsync(request, JobsApiTestHost.UserTokenWithRoles(Child));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        var objects = 0;
        await foreach (var _ in artifacts.Storage.ListAsync(ArtifactCategories.RestoreStaging, Child, ""))
        {
            objects++;
        }

        await Assert.That(objects).IsEqualTo(0);
    }

    private static async Task<string> CreateUploadAsync(JobsApiTestHost host, string token, long length,
        string kind = "archiveRtId")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{Child}/v1/tus-upload");
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("Upload-Metadata",
            kind == "archiveRtId"
                ? $"archiveRtId {Base64(ArchiveRtId)},filename {Base64("export.zip")}"
                : $"databaseName {Base64("db1")},filename {Base64("dump.tar.gz")}");
        request.Content = new ByteArrayContent([]);
        var response = await host.SendAsync(request, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
        var location = response.Headers.Location!;
        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
    }

    private static async Task<long> ReadOffsetAsync(JobsApiTestHost host, string token, string location)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, location);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        var response = await host.SendAsync(request, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        return long.Parse(response.Headers.GetValues("Upload-Offset").Single(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> PatchAsync(JobsApiTestHost host, string token, string location, long offset,
        byte[] body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, location);
        request.Headers.Add("Tus-Resumable", "1.0.0");
        request.Headers.Add("Upload-Offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        var response = await host.SendAsync(request, token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        return long.Parse(response.Headers.GetValues("Upload-Offset").Single(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The tus description and parts of the upload still in the store.</summary>
    private static async Task<int> CountUploadObjectsAsync(ArtifactTestEnvironment artifacts, string uploadId)
    {
        var count = 0;
        await foreach (var entry in artifacts.Storage.ListAsync(ArtifactCategories.RestoreStaging, Child,
                           uploadId + "."))
        {
            if (entry.FileName.Contains(".tus-", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static ImportArchiveDataJob CreateImportJob(BotArtifactStorage replicaStorage, ArchiveSnapshot snapshot,
        List<IReadOnlyDictionary<string, object?>> importedRows)
    {
        var systemContext = Substitute.For<ISystemContext>();
        var tenantContext = Substitute.For<ITenantContext>();
        var repository = Substitute.For<IStreamDataRepository>();
        var archiveStore = Substitute.For<IArchiveRuntimeStore>();
        systemContext.FindTenantContextAsync(Child).Returns(tenantContext);
        tenantContext.GetStreamDataRepository().Returns(repository);
        tenantContext.GetArchiveRuntimeStore().Returns(archiveStore);
        archiveStore.GetAsync(Arg.Any<OctoObjectId>()).Returns(snapshot);

        var id = new OctoObjectId(ArchiveRtId);
        IReadOnlyDictionary<OctoObjectId, ArchiveStorageStats> stats = new Dictionary<OctoObjectId, ArchiveStorageStats>
        {
            [id] = new(id, true, RecordCount: 0, SizeBytes: 0, Health: ArchiveStorageHealth.Good)
        };
        repository.GetArchiveStatsAsync(Arg.Any<IReadOnlyList<OctoObjectId>>(), Arg.Any<CancellationToken>())
            .Returns(stats);
        repository.ImportRowsAsync(Arg.Any<OctoObjectId>(),
                Arg.Any<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(),
                Arg.Any<Meshmakers.Octo.Runtime.Contracts.StreamData.ArchiveImportMode>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await foreach (var row in call.ArgAt<IAsyncEnumerable<IReadOnlyDictionary<string, object?>>>(1))
                {
                    importedRows.Add(row);
                }
            });

        // The local tus directory of the replica is empty: only the artifact store can deliver the upload.
        var backupFiles = Substitute.For<IBackupFileStorageService>();
        backupFiles.GetTusUploadFilePath(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Path.Combine(Path.GetTempPath(), $"octo-bot-missing-{Guid.NewGuid():N}"));

        return new ImportArchiveDataJob(NullLogger<ImportArchiveDataJob>.Instance, systemContext, backupFiles,
            replicaStorage, new SharedUploadStore(replicaStorage, NullLogger<SharedUploadStore>.Instance));
    }

    private static ArchiveSnapshot Snapshot(CkArchiveStatus status = CkArchiveStatus.Disabled)
    {
        return new ArchiveSnapshot(new OctoObjectId(ArchiveRtId), new RtCkId<CkTypeId>("System-1.0.0/Sensor"), status,
            "voltage-raw", new[] { new CkArchiveColumnSpec("voltage", true, false) });
    }

    private static byte[] BuildExportZip(ArchiveSnapshot snapshot, string ndjson)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            using (var metadata = zip.CreateEntry("metadata.json").Open())
            {
                JsonSerializer.Serialize(metadata,
                    new ArchiveExportMetadata(1, DateTime.UtcNow, "source-tenant", ArchiveSchemaMapper.ToDto(snapshot),
                        null, null), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            }

            using (var data = zip.CreateEntry("data.ndjson").Open())
            {
                data.Write(Encoding.UTF8.GetBytes(ndjson));
            }

            // Incompressible padding, so the upload spans more than one store buffer per part.
            using (var padding = zip.CreateEntry("padding.bin", CompressionLevel.NoCompression).Open())
            {
                padding.Write(RandomNumberGenerator.GetBytes(300_000));
            }
        }

        return buffer.ToArray();
    }

    private static string Base64(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }
}
