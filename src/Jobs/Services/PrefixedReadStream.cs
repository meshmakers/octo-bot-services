namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     A read-only, forward-only stream that replays bytes already read from <c>inner</c> (e.g. a sniffed file
///     header) in front of the rest of <c>inner</c>. Disposing it disposes <c>inner</c>.
/// </summary>
internal sealed class PrefixedReadStream(byte[] prefix, Stream inner) : Stream
{
    private int _prefixPosition;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (_prefixPosition < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
            prefix.AsSpan(_prefixPosition, n).CopyTo(buffer);
            _prefixPosition += n;
            return n;
        }

        return inner.Read(buffer);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixPosition < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
            prefix.AsMemory(_prefixPosition, n).CopyTo(buffer);
            _prefixPosition += n;
            return ValueTask.FromResult(n);
        }

        return inner.ReadAsync(buffer, cancellationToken);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        return inner.DisposeAsync();
    }
}
