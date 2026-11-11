using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     AB#8736: the tus uploads in the artifact store, seen from two replicas over one real file system store.
/// </summary>
public class SharedUploadStoreTests
{
    private const string Tenant = "childtenant";

    [Test]
    public async Task PartsAppendedThroughTwoReplicas_AssembleToTheUploadedBytes_EncryptedAtRest()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, b) = Replicas(env);
        var content = RandomNumberGenerator.GetBytes(250_001);

        var id = await a.CreateAsync(Tenant, content.Length, "filename ZXhwb3J0LnppcA==");
        await Assert.That(await b.GetOffsetAsync(Tenant, id)).IsEqualTo(0L);
        await Assert.That(await a.AppendAsync(Tenant, id, new MemoryStream(content[..100_000]))).IsEqualTo(100_000L);
        await Assert.That(await b.GetOffsetAsync(Tenant, id)).IsEqualTo(100_000L);
        await Assert.That(await b.AppendAsync(Tenant, id, new MemoryStream(content[100_000..]))).IsEqualTo(150_001L);
        await Assert.That((await b.GetAsync(Tenant, id))!.Metadata).IsEqualTo("filename ZXhwb3J0LnppcA==");

        var parts = await ListAsync(env, id + SharedUploadStore.PartMarker);
        await Assert.That(parts.Count).IsEqualTo(2);
        await Assert.That(parts.All(p => p.EndsWith(SecretFileFormat.FileExtension, StringComparison.Ordinal)))
            .IsTrue();

        var staged = await a.EnsureStagedAsync(Tenant, id);

        await Assert.That(staged!.Value.FileName).IsEqualTo(id + SecretFileFormat.FileExtension);
        await Assert.That(await ReadPlainAsync(env, staged.Value.FileName)).IsEquivalentTo(content);
        await Assert.That(await ListAsync(env, id + ".tus-")).IsEmpty();
        await Assert.That(await b.GetAsync(Tenant, id)).IsNull();

        // A second call (the job start after the completion) finds the staged file and assembles nothing.
        await Assert.That((await b.EnsureStagedAsync(Tenant, id))!.Value.FileName).IsEqualTo(staged.Value.FileName);
    }

    [Test]
    public async Task WithoutKeyRing_AnUploadedEncryptedDump_IsStagedByteForByte()
    {
        // OpenPlainReadAsync sniffs the OCTOENC1 magic; the parts must not be read that way, or the first part of an
        // uploaded .octoenc dump would be "decrypted" and fail as truncated.
        using var env = new ArtifactTestEnvironment();
        var (a, b) = Replicas(env);
        var content = Encoding.ASCII.GetBytes(SecretFileFormat.Magic).Concat(RandomNumberGenerator.GetBytes(5000))
            .ToArray();

        var id = await a.CreateAsync(Tenant, content.Length, null);
        await a.AppendAsync(Tenant, id, new MemoryStream(content[..1000]));
        await b.AppendAsync(Tenant, id, new MemoryStream(content[1000..]));
        var staged = await b.EnsureStagedAsync(Tenant, id);

        await Assert.That(staged!.Value.FileName).IsEqualTo(id);
        await Assert.That(await env.ReadStoredAsync(SharedUploadStore.Category, Tenant, id)).IsEquivalentTo(content);
    }

    [Test]
    public async Task OverlappingPartsOfTwoReplicas_AndAGap_GiveTheContiguousOffset_AndTheRightBytes()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, _) = Replicas(env);
        var content = RandomNumberGenerator.GetBytes(1000);
        var id = await a.CreateAsync(Tenant, content.Length, null);

        // A client retried a part while the first request still ran on the other replica: both stored a part at 0.
        await StorePartAsync(env, id, content, 0, 300);
        await StorePartAsync(env, id, content, 0, 200);
        await StorePartAsync(env, id, content, 250, 350);
        await StorePartAsync(env, id, content, 800, 200);

        await Assert.That(await a.GetOffsetAsync(Tenant, id)).IsEqualTo(600L);
        await Assert.That(await a.EnsureStagedAsync(Tenant, id)).IsNull();

        await StorePartAsync(env, id, content, 600, 200);
        var staged = await a.EnsureStagedAsync(Tenant, id);

        await Assert.That(await ReadPlainAsync(env, staged!.Value.FileName)).IsEquivalentTo(content);
    }

    [Test]
    public async Task Append_MoreThanTheUploadHasLeft_IsRefused_AndStoresNothing()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, _) = Replicas(env);
        var id = await a.CreateAsync(Tenant, 10, null);

        await Assert.That(async () => await a.AppendAsync(Tenant, id, new MemoryStream(new byte[11])))
            .Throws<SharedUploadException>();
        await Assert.That(await a.GetOffsetAsync(Tenant, id)).IsEqualTo(0L);
        await Assert.That(await ListAsync(env, id + SharedUploadStore.PartMarker)).IsEmpty();
    }

    [Test]
    public async Task Append_ADroppedConnection_KeepsTheBytesThatArrived()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, b) = Replicas(env);
        var content = RandomNumberGenerator.GetBytes(4000);
        var id = await a.CreateAsync(Tenant, content.Length, null);

        var received = await a.AppendAsync(Tenant, id, new DroppingStream(content[..3000], 1234));

        await Assert.That(received).IsEqualTo(1234L);
        await Assert.That(await b.GetOffsetAsync(Tenant, id)).IsEqualTo(1234L);
        await b.AppendAsync(Tenant, id, new MemoryStream(content[1234..]));
        var staged = await b.EnsureStagedAsync(Tenant, id);
        await Assert.That(await ReadPlainAsync(env, staged!.Value.FileName)).IsEquivalentTo(content);
    }

    [Test]
    public async Task UnknownOrForeignIds_AreNoUploads_AndDeleteRemovesEverything()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, b) = Replicas(env);
        var id = await a.CreateAsync(Tenant, 100, null);
        await a.AppendAsync(Tenant, id, new MemoryStream(new byte[40]));

        await Assert.That(await b.GetAsync("othertenant", id)).IsNull();
        await Assert.That(await b.GetAsync(Tenant, "../" + id)).IsNull();
        await Assert.That(await b.GetAsync(Tenant, id.ToUpperInvariant())).IsNull();
        await Assert.That(async () => await b.AppendAsync(Tenant, Guid.NewGuid().ToString("N"), new MemoryStream([1])))
            .Throws<SharedUploadException>();
        await Assert.That(await b.EnsureStagedAsync(Tenant, id)).IsNull();

        await b.DeleteAsync(Tenant, id);

        await Assert.That(await a.GetAsync(Tenant, id)).IsNull();
        await Assert.That(await ListAsync(env, id)).IsEmpty();
    }

    [Test]
    public async Task EmptyUpload_IsStagedAsAnEmptyFile()
    {
        using var env = new ArtifactTestEnvironment();
        var (a, _) = Replicas(env);
        var id = await a.CreateAsync(Tenant, 0, null);

        var staged = await a.EnsureStagedAsync(Tenant, id);

        await Assert.That(staged!.Value.Size).IsEqualTo(0L);
    }

    private static (SharedUploadStore A, SharedUploadStore B) Replicas(ArtifactTestEnvironment env)
    {
        return (new SharedUploadStore(env.CreateReplicaStorage("a"), NullLogger<SharedUploadStore>.Instance),
            new SharedUploadStore(env.CreateReplicaStorage("b"), NullLogger<SharedUploadStore>.Instance));
    }

    private static async Task StorePartAsync(ArtifactTestEnvironment env, string id, byte[] content, int offset,
        int length)
    {
        await env.Storage.StoreStreamAsync(SharedUploadStore.Category, Tenant,
            SharedUploadStore.PartName(id, offset, length), new MemoryStream(content, offset, length),
            ArtifactEncryption.IfConfigured);
    }

    private static async Task<List<string>> ListAsync(ArtifactTestEnvironment env, string prefix)
    {
        var names = new List<string>();
        await foreach (var entry in env.Storage.ListAsync(SharedUploadStore.Category, Tenant, prefix))
        {
            names.Add(entry.FileName);
        }

        return names;
    }

    private static async Task<byte[]> ReadPlainAsync(ArtifactTestEnvironment env, string fileName)
    {
        await using var stream = await env.Storage.OpenStoredReadAsync(SharedUploadStore.Category, Tenant, fileName);
        using var buffer = new MemoryStream();
        await stream!.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>A request body whose connection drops after <c>limit</c> bytes (Kestrel throws an IOException).</summary>
    private sealed class DroppingStream(byte[] content, int limit) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var left = limit - (int)Position;
            if (left <= 0)
            {
                throw new IOException("The client disconnected.");
            }

            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, left)], cancellationToken);
        }
    }
}
