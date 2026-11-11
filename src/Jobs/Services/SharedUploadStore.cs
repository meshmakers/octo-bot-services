using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <inheritdoc />
public sealed class SharedUploadStore(IBotArtifactStorage storage, ILogger<SharedUploadStore> logger)
    : ISharedUploadStore
{
    /// <summary>
    ///     The category the uploads live in: the area of the artifact store for uploaded files, with app-side
    ///     retention and a lifecycle backstop. The artifact store has no separate temporary area.
    /// </summary>
    internal const string Category = ArtifactCategories.RestoreStaging;

    internal const string InfoSuffix = ".tus-info";
    internal const string PartMarker = ".tus-part.";

    private const int BufferSize = 81920;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<string> CreateAsync(string tenantId, long uploadLength, string? metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(uploadLength);

        var uploadId = Guid.NewGuid().ToString("N");
        var document = JsonSerializer.SerializeToUtf8Bytes(new UploadDocument(uploadLength, metadata ?? string.Empty),
            JsonOptions);
        await using var content = new MemoryStream(document);
        await storage.StoreStreamAsync(Category, tenantId, uploadId + InfoSuffix, content, ArtifactEncryption.None,
            cancellationToken);
        return uploadId;
    }

    /// <inheritdoc />
    public async Task<SharedUpload?> GetAsync(string tenantId, string uploadId,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidUploadId(uploadId))
        {
            return null;
        }

        var stream = await storage.OpenStoredReadAsync(Category, tenantId, uploadId + InfoSuffix, cancellationToken);
        if (stream == null)
        {
            return null;
        }

        await using (stream)
        {
            var document = await JsonSerializer.DeserializeAsync<UploadDocument>(stream, JsonOptions,
                               cancellationToken)
                           ?? throw new InvalidDataException($"The description of upload '{uploadId}' is empty.");
            return new SharedUpload(uploadId, document.UploadLength, document.Metadata ?? string.Empty);
        }
    }

    /// <inheritdoc />
    public async Task<long> GetOffsetAsync(string tenantId, string uploadId,
        CancellationToken cancellationToken = default)
    {
        var upload = await GetAsync(tenantId, uploadId, cancellationToken);
        return upload == null ? 0 : await GetOffsetAsync(tenantId, upload, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> AppendAsync(string tenantId, string uploadId, long offset, Stream data,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var upload = await GetAsync(tenantId, uploadId, cancellationToken)
                     ?? throw new SharedUploadException($"Upload '{uploadId}' does not exist.");

        // The part goes to the offset the request was validated against, never to a newly read one: another replica
        // may have stored a request for the same offset meanwhile (a client retry), and a part at the advanced offset
        // would hold the bytes of the wrong range. At the validated offset the two parts overlap and hold the same
        // bytes of the file, which the chain takes once. An offset beyond the received run would leave a gap.
        var current = await GetOffsetAsync(tenantId, upload, cancellationToken);
        if (offset > current)
        {
            throw new SharedUploadException(
                $"Offset {offset} is beyond the {current} bytes upload '{uploadId}' has received.");
        }

        var remaining = upload.UploadLength - offset;
        if (remaining <= 0)
        {
            return 0;
        }

        // The body is received into a local scratch file first: the part name carries its length, and a dropped
        // connection must still leave the bytes that arrived. One body is bounded by the client part size (the
        // ingress refuses bodies above 100 MiB).
        var scratchPath = storage.CreateScratchFilePath(".tus-part");
        try
        {
            long received = 0;
            var disconnected = false;
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                await using var scratch = OwnerOnlyFiles.CreateNew(scratchPath);
                while (true)
                {
                    int read;
                    try
                    {
                        read = await data.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                    }
                    catch (Exception e) when (e is OperationCanceledException or IOException)
                    {
                        // The client went away (Kestrel reports it either way); keep what arrived.
                        disconnected = true;
                        break;
                    }

                    if (read == 0)
                    {
                        // tusdotnet's guarded stream turns a dropped connection into an end of stream with the
                        // request token cancelled, without an exception.
                        disconnected = cancellationToken.IsCancellationRequested;
                        break;
                    }

                    if (received + read > remaining)
                    {
                        throw new SharedUploadException(
                            $"The request holds more data than upload '{uploadId}' has left ({remaining} bytes " +
                            $"from offset {offset}, upload length {upload.UploadLength}).");
                    }

                    await scratch.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
                    received += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (received == 0)
            {
                return 0;
            }

            // After a dropped connection the request token is cancelled; storing the received bytes must not be.
            // Every attempt writes its own part: a failed write deletes only its own key, never the part of a
            // concurrent request for the same range.
            await storage.StoreFileAsync(Category, tenantId, PartName(uploadId, offset, received, NewAttemptId()),
                scratchPath, ArtifactEncryption.IfConfigured,
                disconnected || cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken);
            return received;
        }
        finally
        {
            storage.DeleteScratchFile(scratchPath);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string tenantId, string uploadId, CancellationToken cancellationToken = default)
    {
        if (!IsValidUploadId(uploadId))
        {
            return;
        }

        // The description first: from then on the upload no longer exists for any replica.
        await storage.DeleteAsync(Category, tenantId, uploadId + InfoSuffix, cancellationToken);

        // List first, delete after: deleting while a provider pages through a listing can skip entries.
        var parts = new List<string>();
        await foreach (var entry in storage.ListAsync(Category, tenantId, uploadId + PartMarker, cancellationToken))
        {
            parts.Add(entry.FileName);
        }

        foreach (var part in parts)
        {
            await storage.DeleteAsync(Category, tenantId, part, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<bool> IsCompleteAsync(string tenantId, string uploadId,
        CancellationToken cancellationToken = default)
    {
        var upload = await GetAsync(tenantId, uploadId, cancellationToken);
        return upload != null && await GetOffsetAsync(tenantId, upload, cancellationToken) >= upload.UploadLength;
    }

    /// <inheritdoc />
    public async Task<(string FileName, long Size)?> FindStagedAsync(string tenantId, string uploadId,
        CancellationToken cancellationToken = default)
    {
        // A staged file of an older upload may carry any valid name, but never the description or a part of an
        // upload in flight: a job would consume (and delete) it.
        if (string.IsNullOrEmpty(uploadId) || uploadId.Contains(".tus-", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return await storage.FindRestoreStagingAsync(tenantId, uploadId, cancellationToken);
        }
        catch (ArgumentException)
        {
            // A tenant id the store keys cannot carry.
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryCopyCompleteUploadAsync(string tenantId, string uploadId, Stream destination,
        CancellationToken cancellationToken = default)
    {
        var upload = await GetAsync(tenantId, uploadId, cancellationToken);
        if (upload == null)
        {
            return false;
        }

        var parts = await ListContiguousPartsAsync(tenantId, upload, cancellationToken);
        if (EndOf(parts) < upload.UploadLength)
        {
            return false;
        }

        await WriteContentAsync(tenantId, upload, parts, destination, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<(string FileName, long Size)?> EnsureStagedAsync(string tenantId, string uploadId,
        CancellationToken cancellationToken = default)
    {
        var staged = await FindStagedAsync(tenantId, uploadId, cancellationToken);
        if (staged != null)
        {
            return staged;
        }

        var upload = await GetAsync(tenantId, uploadId, cancellationToken);
        if (upload == null)
        {
            return null;
        }

        var parts = await ListContiguousPartsAsync(tenantId, upload, cancellationToken);
        if (EndOf(parts) < upload.UploadLength)
        {
            return null;
        }

        var stored = await storage.StoreFromWriterAsync(Category, tenantId, uploadId,
            (target, token) => WriteContentAsync(tenantId, upload, parts, target, token),
            ArtifactEncryption.IfConfigured, cancellationToken);
        logger.LogInformation("Assembled upload '{UploadId}' of tenant '{TenantId}' ({Length} bytes, {Parts} part(s)) " +
                              "into the staged file '{FileName}'", uploadId, tenantId, upload.UploadLength,
            parts.Count, stored.FileName);

        try
        {
            await DeleteAsync(tenantId, uploadId, cancellationToken);
        }
        catch (Exception e)
        {
            // The staged file is complete; leftover parts expire with the retention of the category.
            logger.LogWarning(e, "Could not delete the parts of the assembled upload '{UploadId}' of tenant " +
                                 "'{TenantId}'; they expire with the retention", uploadId, tenantId);
        }

        return (stored.FileName, stored.Size);
    }

    /// <summary>
    ///     True for the ids <see cref="CreateAsync" /> hands out (32 lower-case hex digits). Everything else is not an
    ///     upload of this store, which also keeps any caller-supplied id from shaping a key.
    /// </summary>
    internal static bool IsValidUploadId(string? uploadId)
    {
        return uploadId is { Length: 32 } && uploadId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    ///     The key of one stored part: <c>&lt;id&gt;.tus-part.&lt;offset&gt;.&lt;length&gt;.&lt;attempt&gt;</c>, the
    ///     attempt a 32 hex digit id of its own per request, so that two requests for the same range never share a key.
    /// </summary>
    internal static string PartName(string uploadId, long offset, long length, string attemptId)
    {
        if (!IsValidUploadId(attemptId))
        {
            throw new ArgumentException("The attempt id must be 32 lower-case hex digits.", nameof(attemptId));
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{uploadId}{PartMarker}{offset:D19}.{length:D19}.{attemptId}");
    }

    internal static string NewAttemptId()
    {
        return Guid.NewGuid().ToString("N");
    }

    internal static bool TryParsePart(string uploadId, string fileName, out UploadPart part)
    {
        part = default;
        var prefix = uploadId + PartMarker;
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = fileName[prefix.Length..];
        if (rest.EndsWith(SecretFileFormat.FileExtension, StringComparison.Ordinal))
        {
            rest = rest[..^SecretFileFormat.FileExtension.Length];
        }

        var segments = rest.Split('.');
        if (segments.Length != 3 ||
            !long.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            !long.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
            length <= 0 || !IsValidUploadId(segments[2]))
        {
            return false;
        }

        part = new UploadPart(fileName, offset, length);
        return true;
    }

    /// <summary>
    ///     The parts that make up the contiguous run from byte 0, each with the bytes to skip at its start. Two
    ///     replicas may store parts that overlap (a client retried a request while the first one was still running);
    ///     they hold the same bytes of the same file, so the longest part at an offset is taken and overlaps are
    ///     skipped.
    /// </summary>
    internal static List<(UploadPart Part, long Skip)> Chain(IEnumerable<UploadPart> parts, long uploadLength)
    {
        var ordered = parts.OrderBy(p => p.Offset).ThenByDescending(p => p.Length);
        var chain = new List<(UploadPart Part, long Skip)>();
        long covered = 0;
        foreach (var part in ordered)
        {
            if (part.Offset > covered || covered >= uploadLength)
            {
                break;
            }

            var end = part.Offset + part.Length;
            if (end <= covered)
            {
                continue;
            }

            chain.Add((part, covered - part.Offset));
            covered = end;
        }

        return chain;
    }

    private async Task<long> GetOffsetAsync(string tenantId, SharedUpload upload, CancellationToken cancellationToken)
    {
        return Math.Min(EndOf(await ListContiguousPartsAsync(tenantId, upload, cancellationToken)),
            upload.UploadLength);
    }

    private async Task<List<(UploadPart Part, long Skip)>> ListContiguousPartsAsync(string tenantId,
        SharedUpload upload, CancellationToken cancellationToken)
    {
        var parts = new List<UploadPart>();
        await foreach (var entry in storage.ListAsync(Category, tenantId, upload.UploadId + PartMarker,
                           cancellationToken))
        {
            if (TryParsePart(upload.UploadId, entry.FileName, out var part))
            {
                parts.Add(part);
            }
        }

        return Chain(parts, upload.UploadLength);
    }

    private static long EndOf(List<(UploadPart Part, long Skip)> chain)
    {
        return chain.Count == 0 ? 0 : chain[^1].Part.Offset + chain[^1].Part.Length;
    }

    private async Task WriteContentAsync(string tenantId, SharedUpload upload,
        List<(UploadPart Part, long Skip)> chain, Stream target, CancellationToken cancellationToken)
    {
        long written = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (var (part, skip) in chain)
            {
                var take = Math.Min(part.Length - skip, upload.UploadLength - written);
                var input = await storage.OpenStoredReadAsync(Category, tenantId, part.FileName, cancellationToken)
                            ?? throw new InvalidDataException(
                                $"Part '{part.FileName}' of upload '{upload.UploadId}' disappeared (expired?).");
                await using (input)
                {
                    // Every byte of the part is read, also the skipped and the surplus ones: an encrypted part is
                    // only verified to its end once its last chunk is read.
                    var position = 0L;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)) > 0)
                    {
                        var from = Math.Max(0, skip - position);
                        var to = Math.Min(read, skip + take - position);
                        if (to > from)
                        {
                            await target.WriteAsync(buffer.AsMemory((int)from, (int)(to - from)), cancellationToken);
                        }

                        position += read;
                    }

                    if (position != part.Length)
                    {
                        throw new InvalidDataException(
                            $"Part '{part.FileName}' of upload '{upload.UploadId}' holds {position} bytes, not " +
                            $"{part.Length}.");
                    }
                }

                written += take;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (written != upload.UploadLength)
        {
            throw new InvalidDataException(
                $"Upload '{upload.UploadId}' is not complete: {written} of {upload.UploadLength} bytes.");
        }
    }

    internal readonly record struct UploadPart(string FileName, long Offset, long Length);

    private sealed record UploadDocument(long UploadLength, string? Metadata);
}
