using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;

namespace Meshmakers.Octo.Backend.Jobs.Tests.Jobs.TenantBackup;

/// <summary>
///     AB#6969 — the forward-only ZIP reader that the format 2 restore uses on the decrypting read of a container, which
///     cannot seek: it must read what the .NET ZIP writer produces for a non-seekable output (stored entries with a data
///     descriptor) and fail loudly on everything else.
/// </summary>
public class ForwardZipReaderTests
{
    private static readonly byte[] DescriptorSignature = [0x50, 0x4b, 0x07, 0x08];

    /// <summary>Builds a container exactly as the dump writes it: ZipArchive onto a stream that cannot seek.</summary>
    private static byte[] BuildStreamedZip(params (string Name, byte[] Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(new WriteOnlyStream(buffer), ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static async Task<List<(string Name, byte[] Content)>> ReadAllAsync(byte[] zip, int chunk = int.MaxValue)
    {
        var reader = new ForwardZipReader(new ChunkedReadStream(zip, chunk));
        var result = new List<(string, byte[])>();
        while (await reader.ReadNextAsync(CancellationToken.None) is { } entry)
        {
            using var content = new MemoryStream();
            await entry.Data.CopyToAsync(content);
            result.Add((entry.Name, content.ToArray()));
        }

        return result;
    }

    [Test]
    [Arguments(int.MaxValue)]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(4096)]
    public async Task ReadsWhatTheStreamingZipWriterProduces_AtAnyReadSize(int chunk)
    {
        var entries = new (string, byte[])[]
        {
            ("mongo.tar.gz", RandomNumberGenerator.GetBytes(300_000)),
            ("empty.bin", []),
            ("archives/rt1/data/s1/p0.json.gz", RandomNumberGenerator.GetBytes(1)),
            ("archives/rt1/genmap/all/p0.json.gz", RandomNumberGenerator.GetBytes(2_500_000)),
            ("manifest.json", Encoding.UTF8.GetBytes("{\"formatVersion\":2}"))
        };
        var zip = BuildStreamedZip(entries);

        var read = await ReadAllAsync(zip, chunk);

        await Assert.That(read.Select(e => e.Name).SequenceEqual(entries.Select(e => e.Item1))).IsTrue();
        for (var i = 0; i < entries.Length; i++)
        {
            await Assert.That(read[i].Content.SequenceEqual(entries[i].Item2)).IsTrue();
        }

        // The same bytes are an ordinary ZIP for every other reader.
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        await Assert.That(archive.Entries.Count).IsEqualTo(entries.Length);
    }

    [Test]
    public async Task TheStreamingWriterSetsTheDataDescriptorFlag_SoTheScanPathIsWhatIsTested()
    {
        var zip = BuildStreamedZip(("a.bin", [1, 2, 3]));

        await Assert.That((BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(6)) & 0x0008) != 0).IsTrue();
        await Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(18))).IsEqualTo(0u + (zip[18] == 0xff ? uint.MaxValue : 0u));
    }

    [Test]
    public async Task ASignatureInsideTheData_WithoutMatchingSizes_DoesNotEndTheEntry()
    {
        var content = new byte[50_000];
        RandomNumberGenerator.Fill(content);
        for (var offset = 100; offset < content.Length - 40; offset += 4_321)
        {
            // Signature, a CRC field and sizes that are not the number of bytes before them.
            DescriptorSignature.CopyTo(content, offset);
            BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(offset + 8), (uint)(offset + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(offset + 12), (uint)(offset + 1));
        }

        var zip = BuildStreamedZip(("a.bin", content), ("b.bin", [1, 2, 3]));

        var read = await ReadAllAsync(zip, 13);

        await Assert.That(read[0].Content.SequenceEqual(content)).IsTrue();
        await Assert.That(read[1].Content.SequenceEqual(new byte[] { 1, 2, 3 })).IsTrue();
    }

    [Test]
    public async Task AnEntryWithKnownSizes_AndNoDescriptor_IsReadToItsSize()
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in new[] { ("a", new byte[] { 1, 2, 3, 4 }), ("b", new byte[] { 9 }) })
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(content);
            }
        }

        var read = await ReadAllAsync(buffer.ToArray(), 5);

        await Assert.That(read.Count).IsEqualTo(2);
        await Assert.That(read[0].Content.SequenceEqual(new byte[] { 1, 2, 3, 4 })).IsTrue();
        await Assert.That(read[1].Content.SequenceEqual(new byte[] { 9 })).IsTrue();
    }

    [Test]
    public async Task AZip64Descriptor_EndsTheEntry()
    {
        var content = RandomNumberGenerator.GetBytes(10_000);
        var zip = new MemoryStream();
        // Local header with the Zip64 extra field, flags: data descriptor; sizes 0xFFFFFFFF as a writer does for an
        // entry of unknown size that may exceed 4 GB; the descriptor then carries 8-byte sizes.
        Span<byte> header = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 45);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0x0008);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..], uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], uint.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 20);
        zip.Write(header);
        zip.Write("z"u8);
        Span<byte> extra = stackalloc byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(extra, 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(extra[2..], 16);
        zip.Write(extra);
        zip.Write(content);
        Span<byte> descriptor = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x08074b50);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[8..], (ulong)content.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[16..], (ulong)content.Length);
        zip.Write(descriptor);
        zip.Write([0x50, 0x4b, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        var read = await ReadAllAsync(zip.ToArray(), 11);

        await Assert.That(read.Count).IsEqualTo(1);
        await Assert.That(read[0].Name).IsEqualTo("z");
        await Assert.That(read[0].Content.SequenceEqual(content)).IsTrue();
    }

    [Test]
    public async Task ACompressedEntry_CanBeSkipped_ButItsDataIsNeverHandedOutAsStored()
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var stream = zip.CreateEntry("a", CompressionLevel.Optimal).Open())
            {
                stream.Write(new byte[1000]);
            }

            using var second = zip.CreateEntry("b", CompressionLevel.NoCompression).Open();
            second.Write([7, 8]);
        }

        var reader = new ForwardZipReader(new ChunkedReadStream(buffer.ToArray(), 50));
        var first = await reader.ReadNextAsync(CancellationToken.None);
        await Assert.That(first!.IsStored).IsFalse();
        await Assert.That(async () => await first.Data.ReadAsync(new byte[10])).Throws<InvalidDataException>();
        var next = await reader.ReadNextAsync(CancellationToken.None);
        await Assert.That(next!.Name).IsEqualTo("b");
        await Assert.That(next.IsStored).IsTrue();
    }

    [Test]
    public async Task ACompressedEntryWithADataDescriptor_IsRefused()
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(new WriteOnlyStream(buffer), ZipArchiveMode.Create, leaveOpen: false))
        {
            using var stream = zip.CreateEntry("a", CompressionLevel.Optimal).Open();
            stream.Write(new byte[1000]);
        }

        await Assert.That(async () => await ReadAllAsync(buffer.ToArray())).Throws<InvalidDataException>();
    }

    [Test]
    public async Task ANonZipStream_IsRefused()
    {
        await Assert.That(async () => await ReadAllAsync(RandomNumberGenerator.GetBytes(1000))).Throws<InvalidDataException>();
    }

    [Test]
    public async Task AnEmptyZip_HasNoEntries()
    {
        var read = await ReadAllAsync(BuildStreamedZip());

        await Assert.That(read.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ATruncatedStream_IsDetected()
    {
        var zip = BuildStreamedZip(("a.bin", RandomNumberGenerator.GetBytes(100_000)));

        await Assert.That(async () => await ReadAllAsync(zip[..60_000], 1000)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task AnEntryThatIsNotReadToItsEnd_IsSkippedByTheNextRead()
    {
        var zip = BuildStreamedZip(("a.bin", RandomNumberGenerator.GetBytes(200_000)), ("b.bin", [5, 6]));
        var reader = new ForwardZipReader(new ChunkedReadStream(zip, 1000));

        var first = await reader.ReadNextAsync(CancellationToken.None);
        var buffer = new byte[10];
        await first!.Data.ReadExactlyAsync(buffer);
        var second = await reader.ReadNextAsync(CancellationToken.None);

        await Assert.That(second!.Name).IsEqualTo("b.bin");
        using var content = new MemoryStream();
        await second.Data.CopyToAsync(content);
        await Assert.That(content.ToArray().SequenceEqual(new byte[] { 5, 6 })).IsTrue();
        await Assert.That(await reader.ReadNextAsync(CancellationToken.None)).IsNull();
    }

    /// <summary>
    ///     The container of the 115 M-row tenant is above 4 GB, so offsets and sizes need Zip64: written by the .NET writer
    ///     onto a stream that cannot seek and read back by the forward reader and by <see cref="ZipFile" />.
    ///     Opt-in (<c>OCTO_TEST_ZIP_GIB=5</c>): it writes that much to a temp file.
    /// </summary>
    [Test]
    public async Task ALargeContainer_AboveFourGigabytes_IsReadBackByTheForwardReaderAndByZipArchive()
    {
        var gib = Environment.GetEnvironmentVariable("OCTO_TEST_ZIP_GIB");
        if (!double.TryParse(gib, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var size) || size <= 0)
        {
            Skip.Test("OCTO_TEST_ZIP_GIB is not set");
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"octobak-zip64-{Guid.NewGuid():N}.zip");
        try
        {
            var total = (long)(size * 1024 * 1024 * 1024);
            const int entrySize = 700 * 1024 * 1024;
            var entries = (int)((total + entrySize - 1) / entrySize);
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            using (var zip = new ZipArchive(new WriteOnlyStream(file), ZipArchiveMode.Create, leaveOpen: false))
            {
                var block = new byte[1 << 20];
                for (var i = 0; i < entries; i++)
                {
                    new Random(i).NextBytes(block);
                    using var entry = zip.CreateEntry($"archives/rt/data/s{i:D3}/p0.json.gz", CompressionLevel.NoCompression).Open();
                    for (var written = 0; written < entrySize; written += block.Length)
                    {
                        entry.Write(block);
                    }
                }
            }

            await Assert.That(new FileInfo(path).Length).IsGreaterThan(4L * 1024 * 1024 * 1024);

            await using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var reader = new ForwardZipReader(read);
                var count = 0;
                while (await reader.ReadNextAsync(CancellationToken.None) is { } entry)
                {
                    long bytes = 0;
                    var buffer = new byte[1 << 20];
                    int n;
                    while ((n = await entry.Data.ReadAsync(buffer)) > 0)
                    {
                        bytes += n;
                    }

                    await Assert.That(bytes).IsEqualTo((long)entrySize);
                    count++;
                }

                await Assert.That(count).IsEqualTo(entries);
            }

            using var archive = ZipFile.OpenRead(path);
            await Assert.That(archive.Entries.Count).IsEqualTo(entries);
            await Assert.That(archive.Entries.All(e => e.Length == entrySize)).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A write-only stream (no seek, no position), like the pipe the container is built on.</summary>
    private sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }

    /// <summary>A read-only stream that hands out at most <c>chunk</c> bytes per read and cannot seek.</summary>
    private sealed class ChunkedReadStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunk), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Math.Min(Math.Min(buffer.Length, chunk), data.Length - _position);
            data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return new ValueTask<int>(n);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
