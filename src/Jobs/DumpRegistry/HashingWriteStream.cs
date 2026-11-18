using System.Security.Cryptography;

namespace Meshmakers.Octo.Backend.Jobs.DumpRegistry;

internal sealed class HashingWriteStream : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytesWritten;

    public long BytesWritten => _bytesWritten;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _bytesWritten;

    public override long Position
    {
        get => _bytesWritten;
        set => throw new NotSupportedException();
    }

    public string HashHex() => Convert.ToHexStringLower(_hash.GetCurrentHash());

    public override void Write(byte[] buffer, int offset, int count)
    {
        _hash.AppendData(buffer, offset, count);
        _bytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        _bytesWritten += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
