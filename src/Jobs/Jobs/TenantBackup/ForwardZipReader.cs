using System.Buffers.Binary;
using System.Text;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;

/// <summary>
///     Reads the entries of a ZIP container one after the other from a stream that cannot seek (the decrypting read of
///     an artifact, AB#5967): <see cref="System.IO.Compression.ZipArchive" /> needs the central directory at the end and
///     copies a non-seekable stream into memory, which for a 5.5 GB container is not an option.
/// </summary>
/// <remarks>
///     <para>
///         Only what the <c>.octobak.zip</c> writer produces is supported: entries <b>stored</b> without compression,
///         with or without a data descriptor (the .NET writer sets the descriptor flag for a non-seekable output and puts
///         the sizes behind the data). The end of such an entry is found by its descriptor: the signature
///         <c>PK\x07\x08</c> counts only when the CRC field is followed by compressed and uncompressed sizes that both
///         equal the number of bytes read so far (32-bit or Zip64 form), so a signature inside the data does not end the
///         entry.
///     </para>
///     <para>
///         The reader stops at the central directory. A caller that wants the integrity check of an encrypted
///         source to reach the last byte reads the rest of the stream itself afterwards.
///     </para>
/// </remarks>
internal sealed class ForwardZipReader
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint DigitalSignatureSignature = 0x05054b50;
    private const uint ArchiveExtraDataSignature = 0x08064b50;
    private const ushort FlagEncrypted = 0x0001;
    private const ushort FlagDataDescriptor = 0x0008;
    private const ushort Zip64ExtraFieldId = 0x0001;
    private const int BufferSize = 1024 * 1024;
    private const int DescriptorProbeLength = 24;

    private static readonly byte[] DescriptorSignature = [0x50, 0x4b, 0x07, 0x08];

    private readonly byte[] _buffer = new byte[BufferSize];
    private readonly Stream _source;
    private int _end;
    private bool _eof;
    private bool _finished;
    private int _start;
    private EntryDataStream? _current;

    /// <summary>Creates a reader over <paramref name="source" />, positioned at the first local file header.</summary>
    public ForwardZipReader(Stream source)
    {
        _source = source;
    }

    /// <summary>
    ///     Returns the next entry, or <c>null</c> at the central directory. The previous entry is read to its end first.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     The stream is not a ZIP, an entry is compressed or encrypted (unsupported), or the stream ends inside an entry.
    /// </exception>
    public async Task<ForwardZipEntry?> ReadNextAsync(CancellationToken cancellationToken)
    {
        if (_current != null)
        {
            await _current.DrainAsync(cancellationToken);
            _current = null;
        }

        if (_finished)
        {
            return null;
        }

        if (!await EnsureAsync(4, cancellationToken))
        {
            throw new InvalidDataException("The ZIP stream ends before its central directory.");
        }

        var signature = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_start, 4));
        if (signature != LocalHeaderSignature)
        {
            if (signature is CentralHeaderSignature or EndOfCentralDirectorySignature
                or Zip64EndOfCentralDirectorySignature or DigitalSignatureSignature or ArchiveExtraDataSignature)
            {
                _finished = true;
                return null;
            }

            throw new InvalidDataException("The stream is not a ZIP container (no local file header).");
        }

        if (!await EnsureAsync(30, cancellationToken))
        {
            throw new InvalidDataException("The ZIP stream ends inside a local file header.");
        }

        var fixedHeader = ParseFixedHeader(_buffer.AsSpan(_start, 30));
        if (!await EnsureAsync(30 + fixedHeader.NameLength + fixedHeader.ExtraLength, cancellationToken))
        {
            throw new InvalidDataException("The ZIP stream ends inside a local file header.");
        }

        var name = Encoding.UTF8.GetString(_buffer, _start + 30, fixedHeader.NameLength);
        var compressedSize = fixedHeader.CompressedSize;
        var uncompressedSize = fixedHeader.UncompressedSize;
        ApplyZip64Extra(_buffer.AsSpan(_start + 30 + fixedHeader.NameLength, fixedHeader.ExtraLength),
            ref uncompressedSize, ref compressedSize);
        _start += 30 + fixedHeader.NameLength + fixedHeader.ExtraLength;
        var flags = fixedHeader.Flags;
        var method = fixedHeader.Method;

        if ((flags & FlagEncrypted) != 0)
        {
            throw new InvalidDataException($"The ZIP entry '{name}' is encrypted; that is not supported.");
        }

        var hasDescriptor = (flags & FlagDataDescriptor) != 0;
        if (method != 0 && hasDescriptor)
        {
            // The end of a compressed entry cannot be found without inflating it.
            throw new InvalidDataException(
                $"The ZIP entry '{name}' is compressed (method {method}) and has a data descriptor; that is not supported.");
        }

        // A compressed entry whose size is in its header can be skipped, not read: its Data throws.
        _current = new EntryDataStream(this, hasDescriptor ? null : compressedSize, readable: method == 0);
        return new ForwardZipEntry(name, _current, IsStored: method == 0);
    }

    private readonly record struct FixedHeader(
        ushort Flags, ushort Method, long CompressedSize, long UncompressedSize, ushort NameLength, ushort ExtraLength);

    private static FixedHeader ParseFixedHeader(ReadOnlySpan<byte> header)
    {
        return new FixedHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[18..]),
            BinaryPrimitives.ReadUInt32LittleEndian(header[22..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[26..]),
            BinaryPrimitives.ReadUInt16LittleEndian(header[28..]));
    }

    private static void ApplyZip64Extra(ReadOnlySpan<byte> extra, ref long uncompressedSize, ref long compressedSize)
    {
        var rawUncompressed = uncompressedSize;
        var rawCompressed = compressedSize;
        while (extra.Length >= 4)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(extra);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(extra[2..]);
            if (extra.Length < 4 + size)
            {
                return;
            }

            if (id == Zip64ExtraFieldId)
            {
                var field = extra.Slice(4, size);
                if (rawUncompressed == uint.MaxValue && field.Length >= 8)
                {
                    uncompressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(field);
                    field = field[8..];
                }

                if (rawCompressed == uint.MaxValue && field.Length >= 8)
                {
                    compressedSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(field);
                }

                return;
            }

            extra = extra[(4 + size)..];
        }
    }

    /// <summary>Makes at least <paramref name="count" /> bytes available at <c>_start</c>; false at the end of the stream.</summary>
    private async ValueTask<bool> EnsureAsync(int count, CancellationToken cancellationToken)
    {
        while (_end - _start < count)
        {
            if (await FillAsync(cancellationToken) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private async ValueTask<int> FillAsync(CancellationToken cancellationToken)
    {
        if (_eof)
        {
            return 0;
        }

        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        var read = await _source.ReadAsync(_buffer.AsMemory(_end), cancellationToken);
        if (read == 0)
        {
            _eof = true;
            return 0;
        }

        _end += read;
        return read;
    }

    /// <summary>The data of one entry; reading ends at the entry's last byte.</summary>
    private sealed class EntryDataStream : Stream
    {
        private readonly ForwardZipReader _reader;
        private long _consumed;
        private bool _done;
        private long? _remaining;

        private readonly bool _readable;

        public EntryDataStream(ForwardZipReader reader, long? knownSize, bool readable)
        {
            _reader = reader;
            _remaining = knownSize;
            _readable = readable;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task DrainAsync(CancellationToken cancellationToken)
        {
            var scratch = new byte[64 * 1024];
            while (await ReadCoreAsync(scratch, cancellationToken) > 0)
            {
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (!_readable)
            {
                throw new InvalidDataException("The data of a compressed ZIP entry cannot be read; only stored entries are supported.");
            }

            return ReadCoreAsync(destination, cancellationToken);
        }

        private ValueTask<int> ReadCoreAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (_done || destination.Length == 0)
            {
                return new ValueTask<int>(0);
            }

            return _remaining.HasValue
                ? ReadKnownAsync(destination, cancellationToken)
                : ReadUntilDescriptorAsync(destination, cancellationToken);
        }

        private async ValueTask<int> ReadKnownAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (_remaining == 0)
            {
                _done = true;
                return 0;
            }

            if (!await _reader.EnsureAsync(1, cancellationToken))
            {
                throw new InvalidDataException("The ZIP stream ends inside an entry.");
            }

            var count = (int)Math.Min(Math.Min(destination.Length, _reader._end - _reader._start), _remaining!.Value);
            _reader._buffer.AsMemory(_reader._start, count).CopyTo(destination);
            _reader._start += count;
            _remaining -= count;
            _consumed += count;
            return count;
        }

        private async ValueTask<int> ReadUntilDescriptorAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            while (true)
            {
                var available = _reader._end - _reader._start;
                var window = _reader._buffer.AsSpan(_reader._start, available);
                var deliverable = -1;
                var needsMoreData = false;
                var searchFrom = 0;
                var descriptorLength = 0;

                while (searchFrom < available)
                {
                    var index = window[searchFrom..].IndexOf(DescriptorSignature);
                    if (index < 0)
                    {
                        break;
                    }

                    index += searchFrom;
                    if (available - index < DescriptorProbeLength)
                    {
                        if (_reader._eof)
                        {
                            searchFrom = index + 1;
                            continue;
                        }

                        deliverable = index;
                        needsMoreData = true;
                        break;
                    }

                    if (IsDescriptor(window[index..], _consumed + index, out descriptorLength))
                    {
                        if (index == 0)
                        {
                            _reader._start += descriptorLength;
                            _done = true;
                            return 0;
                        }

                        deliverable = index;
                        break;
                    }

                    searchFrom = index + 1;
                }

                if (deliverable < 0)
                {
                    // No descriptor in the window: everything but a possible partial signature at the end is data.
                    deliverable = _reader._eof ? available : Math.Max(0, available - (DescriptorProbeLength - 1));
                }

                if (deliverable > 0)
                {
                    var count = Math.Min(deliverable, destination.Length);
                    window[..count].CopyTo(destination.Span);
                    _reader._start += count;
                    _consumed += count;
                    return count;
                }

                if (_reader._eof && !needsMoreData)
                {
                    throw new InvalidDataException("The ZIP stream ends inside an entry (no data descriptor found).");
                }

                // More bytes are needed to decide; at the end of the stream the next round knows that and decides.
                await _reader.FillAsync(cancellationToken);
            }
        }

        /// <summary>
        ///     True when <paramref name="candidate" /> (starting at the signature) is the data descriptor of an entry
        ///     that has delivered <paramref name="consumed" /> bytes: CRC, then compressed and uncompressed size, both
        ///     equal to <paramref name="consumed" /> for a stored entry, in the 32-bit or the Zip64 layout.
        /// </summary>
        private static bool IsDescriptor(ReadOnlySpan<byte> candidate, long consumed, out int length)
        {
            var size32 = BinaryPrimitives.ReadUInt32LittleEndian(candidate[8..]);
            var uncompressed32 = BinaryPrimitives.ReadUInt32LittleEndian(candidate[12..]);
            if (consumed <= uint.MaxValue && size32 == consumed && uncompressed32 == consumed)
            {
                length = 16;
                return true;
            }

            var size64 = BinaryPrimitives.ReadUInt64LittleEndian(candidate[8..]);
            var uncompressed64 = BinaryPrimitives.ReadUInt64LittleEndian(candidate[16..]);
            if ((long)size64 == consumed && (long)uncompressed64 == consumed)
            {
                length = 24;
                return true;
            }

            length = 0;
            return false;
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
}

/// <summary>An entry of a <see cref="ForwardZipReader" />; <see cref="Data" /> is valid until the next entry is read.</summary>
/// <param name="Name">The entry name.</param>
/// <param name="Data">The stored bytes of the entry.</param>
/// <param name="IsStored">False for a compressed entry: it can be skipped, its data cannot be read.</param>
internal sealed record ForwardZipEntry(string Name, Stream Data, bool IsStored);
