using System.Security.Cryptography;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Services;

/// <summary>
///     AB#6969 "Stream access" — the bot's artifact storage writes and reads artifacts as streams: constant memory,
///     the existing OCTOENC1 encryption, nothing partial after a failure.
/// </summary>
public class BotArtifactStorageStreamTests : IDisposable
{
    private const string Tenant = "tenant-1";
    private readonly ArtifactTestEnvironment _env = new("k1");

    public void Dispose()
    {
        _env.Dispose();
    }

    private static long LargeSizeBytes()
    {
        // The unit run uses 256 MiB; OCTO_TEST_STREAM_GIB=6 runs the 6 GB case of the acceptance criteria.
        var gib = Environment.GetEnvironmentVariable("OCTO_TEST_STREAM_GIB");
        return double.TryParse(gib, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? (long)(value * 1024 * 1024 * 1024)
            : 256L * 1024 * 1024;
    }

    private static async Task<byte[]> HashAsync(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return hash.GetHashAndReset();
    }

    [Test]
    [NotInParallel("managed-memory-measurement")]
    [Arguments(ArtifactEncryption.None)]
    [Arguments(ArtifactEncryption.Required)]
    public async Task StoreStream_OfALargeGeneratedStream_KeepsMemoryFlat_AndReadsBackIdentical(
        ArtifactEncryption encryption)
    {
        var size = LargeSizeBytes();

        // "Flat memory" is checked structurally, not with a process-wide GC figure that every parallel test disturbs:
        // while the source is read, the bytes it has handed out never run more than a few pipe buffers ahead of the
        // bytes that already lie in the store. A buffering implementation would show the whole artifact as read-ahead.
        long maxReadAhead = 0;
        var source = new GeneratedStream(size, 7, progressStep: 8 * 1024 * 1024, onProgress: position =>
        {
            var onDisk = Directory.EnumerateFiles(_env.StoreRoot, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            maxReadAhead = Math.Max(maxReadAhead, position - onDisk);
        });

        var stored = await _env.Storage.StoreStreamAsync(ArtifactCategories.TenantDumps, Tenant, "large.octobak.zip",
            source, encryption);

        await Assert.That(maxReadAhead).IsLessThan(64L * 1024 * 1024);
        await Assert.That(stored.Encrypted).IsEqualTo(encryption == ArtifactEncryption.Required);
        await using var read = await _env.Storage.OpenPlainReadAsync(ArtifactCategories.TenantDumps, Tenant, stored.FileName);
        var expected = await HashAsync(new GeneratedStream(size, 7));
        var actual = await HashAsync(read!);
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task StoreStream_Required_ProducesAnOctoencFile_ThatTheExistingReadersDecrypt()
    {
        var plaintext = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 11);

        var stored = await _env.Storage.StoreStreamAsync(ArtifactCategories.TenantDumps, Tenant, "t.octobak.zip",
            new MemoryStream(plaintext), ArtifactEncryption.Required);

        await Assert.That(stored.FileName).IsEqualTo("t.octobak.zip.octoenc");
        var raw = await _env.ReadStoredAsync(ArtifactCategories.TenantDumps, Tenant, stored.FileName);
        await Assert.That(System.Text.Encoding.ASCII.GetString(raw, 0, 8)).IsEqualTo(SecretFileFormat.Magic);
        await using (var download = await _env.Storage.OpenDownloadAsync(ArtifactCategories.TenantDumps, Tenant, stored.FileName))
        {
            using var copy = new MemoryStream();
            await _env.Storage.CopyPlainAsync(download!, copy);
            await Assert.That(copy.ToArray().SequenceEqual(plaintext)).IsTrue();
        }

        var target = Path.Combine(_env.Directory, "plain.bin");
        await Assert.That(await _env.Storage.TryWritePlainToFileAsync(ArtifactCategories.TenantDumps, Tenant,
            stored.FileName, target)).IsTrue();
        await Assert.That((await File.ReadAllBytesAsync(target)).SequenceEqual(plaintext)).IsTrue();
    }

    [Test]
    [Arguments(ArtifactEncryption.None)]
    [Arguments(ArtifactEncryption.Required)]
    public async Task AnArtifactWrittenByStoreFile_IsReadableThroughTheStream(ArtifactEncryption encryption)
    {
        var plaintext = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 5);
        var source = _env.WriteFile("src.bin", plaintext);
        var stored = await _env.Storage.StoreFileAsync(ArtifactCategories.TenantDumps, Tenant, "src.bin", source, encryption);

        await using var read = await _env.Storage.OpenPlainReadAsync(ArtifactCategories.TenantDumps, Tenant, stored.FileName);

        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy);
        await Assert.That(copy.ToArray().SequenceEqual(plaintext)).IsTrue();
    }

    [Test]
    public async Task OpenPlainRead_OfAMissingArtifact_IsNull()
    {
        await Assert.That(await _env.Storage.OpenPlainReadAsync(ArtifactCategories.TenantDumps, Tenant, "none.zip")).IsNull();
    }

    [Test]
    public async Task OpenPlainRead_OfATamperedArtifact_ThrowsBeforeTheEndAndTheCallerCanDiscard()
    {
        var plaintext = RandomNumberGenerator.GetBytes(8 * 1024 * 1024);
        var stored = await _env.Storage.StoreStreamAsync(ArtifactCategories.TenantDumps, Tenant, "t.zip",
            new MemoryStream(plaintext), ArtifactEncryption.Required);
        var path = Path.Combine(_env.StoreRoot, _env.Keys.Build(ArtifactCategories.TenantDumps, Tenant, stored.FileName)
            .Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length * 3 / 4] ^= 0x5a; // flip a byte three quarters in
        await File.WriteAllBytesAsync(path, bytes);

        long consumed = 0;
        await Assert.That(async () =>
        {
            await using var read = await _env.Storage.OpenPlainReadAsync(ArtifactCategories.TenantDumps, Tenant, stored.FileName);
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await read!.ReadAsync(buffer)) > 0)
            {
                consumed += n;
            }
        }).Throws<InvalidSecretFileException>();

        await Assert.That(consumed).IsLessThan((long)plaintext.Length);
    }

    [Test]
    public async Task OpenPlainRead_OfATruncatedArtifact_Throws()
    {
        var plaintext = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        var stored = await _env.Storage.StoreStreamAsync(ArtifactCategories.TenantDumps, Tenant, "t.zip",
            new MemoryStream(plaintext), ArtifactEncryption.Required);
        var path = Path.Combine(_env.StoreRoot, _env.Keys.Build(ArtifactCategories.TenantDumps, Tenant, stored.FileName)
            .Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 2)]);

        await Assert.That(async () =>
        {
            await using var read = await _env.Storage.OpenPlainReadAsync(ArtifactCategories.TenantDumps, Tenant, stored.FileName);
            await read!.CopyToAsync(Stream.Null);
        }).Throws<InvalidSecretFileException>();
    }

    [Test]
    public async Task StoreStream_Cancelled_LeavesNothingUnderTheFinalKey()
    {
        using var cts = new CancellationTokenSource();
        var stream = new GeneratedStream(64L * 1024 * 1024, 3, afterBytes: 8 * 1024 * 1024, onReached: cts.Cancel);

        await Assert.That(async () => await _env.Storage.StoreStreamAsync(ArtifactCategories.TenantDumps, Tenant,
                "cancelled.zip", stream, ArtifactEncryption.Required, cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(await _env.Storage.GetInfoAsync(ArtifactCategories.TenantDumps, Tenant, "cancelled.zip.octoenc"))
            .IsNull();
        var listed = new List<ArtifactInfo>();
        await foreach (var info in _env.Store.ListAsync(""))
        {
            listed.Add(info);
        }

        await Assert.That(listed).IsEmpty();
    }

    [Test]
    public async Task StoreFromWriter_AProducerThatThrows_StoresNothing_AndItsExceptionPropagates()
    {
        await Assert.That(async () => await _env.Storage.StoreFromWriterAsync(ArtifactCategories.TenantDumps, Tenant,
                "broken.zip", async (target, ct) =>
                {
                    await target.WriteAsync(new byte[2 * 1024 * 1024], ct);
                    throw new InvalidOperationException("producer failed");
                }, ArtifactEncryption.IfConfigured))
            .Throws<InvalidOperationException>();

        var listed = new List<ArtifactInfo>();
        await foreach (var info in _env.Store.ListAsync(""))
        {
            listed.Add(info);
        }

        await Assert.That(listed).IsEmpty();
    }

    [Test]
    public async Task StoreFromWriter_StreamsTheProducedContent()
    {
        var plaintext = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 3);

        var stored = await _env.Storage.StoreFromWriterAsync(ArtifactCategories.TenantDumps, Tenant, "w.zip",
            async (target, ct) =>
            {
                for (var offset = 0; offset < plaintext.Length; offset += 1024 * 1024)
                {
                    await target.WriteAsync(plaintext.AsMemory(offset, Math.Min(1024 * 1024, plaintext.Length - offset)), ct);
                }
            }, ArtifactEncryption.Required);

        await using var read = await _env.Storage.OpenPlainReadAsync(ArtifactCategories.TenantDumps, Tenant, stored.FileName);
        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy);
        await Assert.That(copy.ToArray().SequenceEqual(plaintext)).IsTrue();
    }
}

/// <summary>
///     A deterministic, non-seekable stream of <c>length</c> bytes generated on the fly (no source file).
///     <paramref name="onReached" /> runs once when <paramref name="afterBytes" /> have been read.
/// </summary>
internal sealed class GeneratedStream(long length, int seed, long afterBytes = long.MaxValue, Action? onReached = null,
    long progressStep = long.MaxValue, Action<long>? onProgress = null) : Stream
{
    private readonly Random _random = new(seed);
    private long _position;
    private bool _fired;
    private long _nextProgress = -1;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, Math.Min(length - _position, 1024 * 1024));
        if (count <= 0)
        {
            return 0;
        }

        _random.NextBytes(buffer[..count]);
        _position += count;
        if (_nextProgress < 0)
        {
            _nextProgress = progressStep;
        }

        if (_position >= _nextProgress)
        {
            _nextProgress = _position + progressStep;
            onProgress?.Invoke(_position);
        }

        if (!_fired && _position >= afterBytes)
        {
            _fired = true;
            onReached?.Invoke();
        }

        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span));
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }
}
