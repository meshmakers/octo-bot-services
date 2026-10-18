namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     The read side of a decrypting pipe (<see cref="IBotArtifactStorage.OpenPlainReadAsync" />): reads the verified
///     plaintext the producer task writes; an exception of the producer (a truncated or tampered artifact) surfaces from
///     the read that would have returned the next bytes. Disposing it stops the producer and waits for it.
/// </summary>
internal sealed class PlainReadStream(Stream inner, CancellationTokenSource cancellation, Task producer) : Stream
{
    private bool _disposed;

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
        return inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return inner.ReadAsync(buffer, offset, count, cancellationToken);
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

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await cancellation.CancelAsync();
        await inner.DisposeAsync();
        try
        {
            await producer;
        }
        catch (Exception)
        {
            // The producer reports its failure through the pipe; at disposal only the end matters.
        }

        cancellation.Dispose();
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
