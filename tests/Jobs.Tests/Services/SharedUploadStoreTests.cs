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
        await Assert.That(await a.AppendAsync(Tenant, id, 0, new MemoryStream(content[..100_000]))).IsEqualTo(100_000L);
        await Assert.That(await b.GetOffsetAsync(Tenant, id)).IsEqualTo(100_000L);
        await Assert.That(await b.AppendAsync(Tenant, id, 100_000, new MemoryStream(content[100_000..]))).IsEqualTo(150_001L);
        await Assert.That((await b.GetAsync(Tenant, id))!.Metadata).IsEqualTo("filename ZXhwb3J0LnppcA==");

        var parts = await ListAsync(env, id + SharedUploadStore.PartMarker);
        await Assert.That(parts.Count).IsEqualTo(2);
        await Assert.That(parts.All(p => p.EndsWith(SecretFileFormat.FileExtension, StringComparison.Ordinal)))
            .IsTrue();

        var staged = await a.EnsureStagedAsync(Tenant, id);

        await Assert.That(staged!.Value.FileName).IsEqualTo(id + SecretFileFormat.FileExtension);
        await AssertSameBytesAsync(await ReadPlainAsync(env, staged.Value.FileName), content);
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
        await a.AppendAsync(Tenant, id, 0, new MemoryStream(content[..1000]));
        await b.AppendAsync(Tenant, id, 1000, new MemoryStream(content[1000..]));
        var staged = await b.EnsureStagedAsync(Tenant, id);

        await Assert.That(staged!.Value.FileName).IsEqualTo(id);
        await AssertSameBytesAsync(await env.ReadStoredAsync(SharedUploadStore.Category, Tenant, id), content);
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

        await AssertSameBytesAsync(await ReadPlainAsync(env, staged!.Value.FileName), content);
    }

    [Test]
    public async Task Append_MoreThanTheUploadHasLeft_IsRefused_AndStoresNothing()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, _) = Replicas(env);
        var id = await a.CreateAsync(Tenant, 10, null);

        await Assert.That(async () => await a.AppendAsync(Tenant, id, 0, new MemoryStream(new byte[11])))
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

        var received = await a.AppendAsync(Tenant, id, 0, new DroppingStream(content[..3000], 1234));

        await Assert.That(received).IsEqualTo(1234L);
        await Assert.That(await b.GetOffsetAsync(Tenant, id)).IsEqualTo(1234L);
        await b.AppendAsync(Tenant, id, 1234, new MemoryStream(content[1234..]));
        var staged = await b.EnsureStagedAsync(Tenant, id);
        await AssertSameBytesAsync(await ReadPlainAsync(env, staged!.Value.FileName), content);
    }

    /// <summary>
    ///     Two requests validated at the same offset (a client retry while the first request still ran on the other
    ///     replica) both store at that offset, never at an offset read later; the bytes count once.
    /// </summary>
    [Test]
    public async Task TwoConcurrentAppendsAtTheSameOffset_StoreAtThatOffset_AndExactlyOneCounts()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, b) = Replicas(env);
        var content = RandomNumberGenerator.GetBytes(6000);
        var id = await a.CreateAsync(Tenant, content.Length, null);

        // Both bodies are held until both requests are reading, so both run against offset 0 at the same time.
        var bothReading = new Barrier(2);
        await Task.WhenAll(
            Task.Run(() => a.AppendAsync(Tenant, id, 0, new GatedStream(content[..3000], bothReading))),
            Task.Run(() => b.AppendAsync(Tenant, id, 0, new GatedStream(content[..3000], bothReading))));

        await Assert.That(await a.GetOffsetAsync(Tenant, id)).IsEqualTo(3000L);
        var names = await ListAsync(env, id + SharedUploadStore.PartMarker);
        await Assert.That(names.Count).IsEqualTo(2);
        var chain = SharedUploadStore.Chain(names.Select(ParsePart), content.Length);
        await Assert.That(chain.Count).IsEqualTo(1);

        await b.AppendAsync(Tenant, id, 3000, new MemoryStream(content[3000..]));
        var staged = await a.EnsureStagedAsync(Tenant, id);
        await AssertSameBytesAsync(await ReadPlainAsync(env, staged!.Value.FileName), content);
    }

    /// <summary>
    ///     The request validated at offset 0 arrives after the other replica stored that range: it still stores at
    ///     0 (not at the advanced offset 3, which would give ABCABC instead of ABCDEF).
    /// </summary>
    [Test]
    public async Task ALateAppendValidatedAtAnEarlierOffset_DoesNotLandAtTheAdvancedOffset()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, b) = Replicas(env);
        var content = "ABCDEF"u8.ToArray();
        var id = await a.CreateAsync(Tenant, content.Length, null);

        await a.AppendAsync(Tenant, id, 0, new MemoryStream(content[..3]));
        await b.AppendAsync(Tenant, id, 0, new MemoryStream(content[..3]));

        await Assert.That(await a.GetOffsetAsync(Tenant, id)).IsEqualTo(3L);
        await a.AppendAsync(Tenant, id, 3, new MemoryStream(content[3..]));
        var staged = await b.EnsureStagedAsync(Tenant, id);
        await AssertSameBytesAsync(await ReadPlainAsync(env, staged!.Value.FileName), content);
    }

    [Test]
    public async Task Append_BeyondTheReceivedBytes_IsRefused_AndStoresNothing()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, _) = Replicas(env);
        var id = await a.CreateAsync(Tenant, 10, null);

        await Assert.That(async () => await a.AppendAsync(Tenant, id, 4, new MemoryStream(new byte[3])))
            .Throws<SharedUploadException>();
        await Assert.That(await ListAsync(env, id + SharedUploadStore.PartMarker)).IsEmpty();
    }

    /// <summary>
    ///     tusdotnet's guarded stream reports a dropped connection as an end of stream with the request token
    ///     cancelled; the bytes that arrived are still stored (with a token that is not the cancelled one).
    /// </summary>
    [Test]
    public async Task Append_EndOfStreamWithTheTokenCancelled_KeepsTheBytesThatArrived()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, b) = Replicas(env);
        var content = RandomNumberGenerator.GetBytes(300_000);
        var id = await a.CreateAsync(Tenant, content.Length, null);
        using var request = new CancellationTokenSource();

        var received = await a.AppendAsync(Tenant, id, 0, new CancellingStream(content, 200_000, request),
            request.Token);

        await Assert.That(received).IsEqualTo(200_000L);
        await Assert.That(await b.GetOffsetAsync(Tenant, id)).IsEqualTo(200_000L);
        await b.AppendAsync(Tenant, id, 200_000, new MemoryStream(content[200_000..]));
        var staged = await b.EnsureStagedAsync(Tenant, id);
        await AssertSameBytesAsync(await ReadPlainAsync(env, staged!.Value.FileName), content);
    }

    /// <summary>
    ///     Two attempts for the same range get keys of their own, so a write that fails (and deletes its key) cannot
    ///     remove the part another request stored.
    /// </summary>
    [Test]
    public async Task TwoAttemptsForTheSameRange_HaveTheirOwnKeys_AndRemovingOneKeepsTheOther()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, b) = Replicas(env);
        var content = RandomNumberGenerator.GetBytes(500);
        var id = await a.CreateAsync(Tenant, content.Length, null);

        await a.AppendAsync(Tenant, id, 0, new MemoryStream(content[..200]));
        var first = (await ListAsync(env, id + SharedUploadStore.PartMarker)).Single();
        await b.AppendAsync(Tenant, id, 0, new MemoryStream(content[..200]));
        var second = (await ListAsync(env, id + SharedUploadStore.PartMarker)).Single(n => n != first);

        // What the failed write of the second attempt does to its own key.
        await env.Storage.DeleteAsync(SharedUploadStore.Category, Tenant, second);

        await Assert.That(await a.GetOffsetAsync(Tenant, id)).IsEqualTo(200L);
        await Assert.That(await ListAsync(env, id + SharedUploadStore.PartMarker)).IsEquivalentTo([first]);
    }

    [Test]
    public async Task TryParsePart_ReadsOffsetLengthAndAttempt_AndRefusesOtherNames()
    {
        var id = Guid.NewGuid().ToString("N");
        var attempt = SharedUploadStore.NewAttemptId();
        var name = SharedUploadStore.PartName(id, 1024, 512, attempt);

        await Assert.That(SharedUploadStore.TryParsePart(id, name, out var plain)).IsTrue();
        await Assert.That(plain.Offset).IsEqualTo(1024L);
        await Assert.That(plain.Length).IsEqualTo(512L);
        await Assert.That(SharedUploadStore.TryParsePart(id, name + SecretFileFormat.FileExtension, out var encrypted))
            .IsTrue();
        await Assert.That(encrypted.Offset).IsEqualTo(1024L);

        string[] refused =
        [
            $"{id}{SharedUploadStore.PartMarker}{1024:D19}.{512:D19}",
            $"{id}{SharedUploadStore.PartMarker}{1024:D19}.{512:D19}.{attempt.ToUpperInvariant()}",
            $"{id}{SharedUploadStore.PartMarker}{1024:D19}.{0:D19}.{attempt}",
            $"{id}{SharedUploadStore.InfoSuffix}"
        ];
        foreach (var other in refused)
        {
            await Assert.That(SharedUploadStore.TryParsePart(id, other, out _)).IsFalse();
        }

        await Assert.That(() => SharedUploadStore.PartName(id, 0, 1, "../x")).Throws<ArgumentException>();
    }

    [Test]
    public async Task UnknownOrForeignIds_AreNoUploads_AndDeleteRemovesEverything()
    {
        using var env = new ArtifactTestEnvironment("k1");
        var (a, b) = Replicas(env);
        var id = await a.CreateAsync(Tenant, 100, null);
        await a.AppendAsync(Tenant, id, 0, new MemoryStream(new byte[40]));

        await Assert.That(await b.GetAsync("othertenant", id)).IsNull();
        await Assert.That(await b.GetAsync(Tenant, "../" + id)).IsNull();
        await Assert.That(await b.GetAsync(Tenant, id.ToUpperInvariant())).IsNull();
        await Assert.That(async () => await b.AppendAsync(Tenant, Guid.NewGuid().ToString("N"), 0, new MemoryStream([1])))
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
            SharedUploadStore.PartName(id, offset, length, SharedUploadStore.NewAttemptId()), new MemoryStream(content, offset, length),
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

    private static async Task AssertSameBytesAsync(byte[] actual, byte[] expected)
    {
        // Order matters: reordered parts would be a corrupt upload with the same bytes.
        await Assert.That(actual.Length).IsEqualTo(expected.Length);
        await Assert.That(actual.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    private static SharedUploadStore.UploadPart ParsePart(string fileName)
    {
        var uploadId = fileName[..32];
        return SharedUploadStore.TryParsePart(uploadId, fileName, out var part)
            ? part
            : throw new InvalidOperationException($"'{fileName}' is not a part.");
    }

    /// <summary>A request body that holds its data until <c>barrier</c> sees both requests reading.</summary>
    private sealed class GatedStream(byte[] content, Barrier barrier) : MemoryStream(content)
    {
        private bool _waited;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_waited)
            {
                _waited = true;
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30), cancellationToken))
                {
                    throw new TimeoutException("The other request never started reading.");
                }
            }

            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <summary>
    ///     A request body whose connection drops after <c>limit</c> bytes the way tusdotnet's guarded stream reports
    ///     it: the request token is cancelled and the read returns 0, without an exception.
    /// </summary>
    private sealed class CancellingStream(byte[] content, int limit, CancellationTokenSource request)
        : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var left = limit - (int)Position;
            if (left <= 0)
            {
                await request.CancelAsync();
                return 0;
            }

            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, left)], CancellationToken.None);
        }
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
