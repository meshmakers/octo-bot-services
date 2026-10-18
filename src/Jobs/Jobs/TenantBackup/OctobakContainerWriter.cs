using System.IO.Compression;
using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.Services;

namespace Meshmakers.Octo.Backend.Jobs.Jobs.TenantBackup;

/// <summary>
///     Writes a format 2 <c>.octobak.zip</c> (AB#5967) onto a stream that cannot seek: the verbatim mongodump blob, the
///     files of the native archive copy copied byte for byte out of the staging area, and the manifest last. Nothing is
///     compressed (the staged files are gzip files already) and nothing is kept in memory but one copy buffer, so the
///     container goes straight to the artifact store without a local copy.
/// </summary>
internal static class OctobakContainerWriter
{
    /// <summary>
    ///     Writes the container to <paramref name="target" />. The order is: <c>mongo.tar.gz</c>, the generation maps,
    ///     the data slices, <c>manifest.json</c> (concept §6).
    /// </summary>
    /// <param name="target">The (non-seekable) stream the container is built on; left open.</param>
    /// <param name="mongoBlobPath">The local mongodump blob.</param>
    /// <param name="artifacts">Opens the staged files.</param>
    /// <param name="tenantId">The tenant of the staging run.</param>
    /// <param name="runId">The staging run.</param>
    /// <param name="stagedObjects">The objects of the run.</param>
    /// <param name="manifest">The manifest, written last.</param>
    /// <param name="manifestOptions">Serializer options of the manifest.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="JobFailedException">A staged object disappeared or changed its size while it was copied.</exception>
    public static async Task WriteAsync(Stream target, string mongoBlobPath, IBotArtifactStorage artifacts,
        string tenantId, string runId, IReadOnlyList<StagingObject> stagedObjects, BackupManifest manifest,
        JsonSerializerOptions manifestOptions, CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);

        var mongoEntry = zip.CreateEntry(BackupArchiveContainer.MongoBlobEntry, CompressionLevel.NoCompression);
        await using (var entryStream = mongoEntry.Open())
        await using (var blob = new FileStream(mongoBlobPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await blob.CopyToAsync(entryStream, cancellationToken);
        }

        foreach (var staged in OrderForContainer(stagedObjects))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = await artifacts.OpenStagingReadAsync(tenantId, runId, staged.RelativePath,
                                         cancellationToken)
                                     ?? throw new JobFailedException(
                                         $"The staged file '{staged.RelativePath}' of the archive copy disappeared " +
                                         "before it was packed.");
            var entry = zip.CreateEntry(BackupArchiveContainer.NativeEntryFor(staged.RelativePath),
                CompressionLevel.NoCompression);
            long copied;
            await using (var entryStream = entry.Open())
            {
                var counter = new CountingStream(entryStream);
                await source.CopyToAsync(counter, cancellationToken);
                copied = counter.BytesWritten;
            }

            if (copied != staged.Size)
            {
                throw new JobFailedException(
                    $"The staged file '{staged.RelativePath}' of the archive copy changed while it was packed " +
                    $"({copied} bytes copied, {staged.Size} listed).");
            }
        }

        var manifestEntry = zip.CreateEntry(BackupArchiveContainer.ManifestEntry, CompressionLevel.NoCompression);
        await using var manifestStream = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(manifestStream, manifest, manifestOptions, cancellationToken);
    }

    /// <summary>Generation maps first, then the data slices, each group in path order.</summary>
    internal static IEnumerable<StagingObject> OrderForContainer(IEnumerable<StagingObject> staged)
    {
        return staged
            .OrderBy(s => s.RelativePath.Contains("/genmap/", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(s => s.RelativePath, StringComparer.Ordinal);
    }

    /// <summary>Counts the bytes written through it.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesWritten { get; private set; }

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

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            BytesWritten += buffer.Length;
        }
    }
}
