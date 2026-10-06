using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <inheritdoc />
public sealed class BotArtifactStorage : IBotArtifactStorage
{
    /// <summary>
    ///     Service tag of the file protection metrics.
    /// </summary>
    internal const string ServiceName = "bot-services";

    // Header layout of OCTOENC1 (docs/secret-sweep-dump-storage.md): magic(8) version(1) kidLength(1) kid(n)
    // createdAt(8) chunkSize(4) baseNonce(12) wrapNonce(12) wrappedKey(32) wrapTag(16) = 94 + n bytes.
    private const int MagicLength = 8;
    private const int FixedHeaderLength = 94;
    private const int TagLength = 16;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes(SecretFileFormat.Magic);

    /// <summary>
    ///     Categories whose encrypted artifacts the bot wrote itself and which therefore pin a key id in the ring
    ///     (key-id retention, <c>DumpKeyMissing</c>). <see cref="ArtifactCategories.RestoreStaging" /> is excluded on
    ///     purpose: tenant users upload those files, an uploaded <c>.octoenc</c> may carry any key id, and such a
    ///     file must neither raise an instance-wide warning nor appear in <c>requiredKeyIds</c>.
    /// </summary>
    private static readonly string[] KeyRetentionCategories =
        [ArtifactCategories.Presweep, ArtifactCategories.TenantDumps];

    private readonly ConcurrentDictionary<string, (DateTimeOffset CreatedAt, long Size, SecretFileHeader Header)>
        _headerCache = new(StringComparer.Ordinal);

    /// <summary>
    ///     How long the result of <see cref="GetEncryptedArtifactHeadersAsync" /> is reused. The secrets status (any
    ///     tenant user, polled by editors) calls it; without a snapshot every call would list all categories of the
    ///     whole store. Writes and deletes through this instance invalidate it.
    /// </summary>
    internal static readonly TimeSpan HeaderSnapshotLifetime = TimeSpan.FromMinutes(1);

    private readonly ArtifactKeyBuilder _keys;
    private readonly ILogger<BotArtifactStorage> _logger;
    private readonly IArtifactStore? _presweepStore;
    private readonly ISecretFileProtector _protector;
    private readonly IArtifactStore _store;
    private volatile HeaderSnapshot? _headerSnapshot;

    /// <summary>
    ///     Constructor.
    /// </summary>
    /// <param name="store">The configured artifact store.</param>
    /// <param name="keys">Key layout with the instance prefix.</param>
    /// <param name="protector">The engine's file protector (instance key ring).</param>
    /// <param name="scratchDirectory">Local scratch directory for the dump tools.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="presweepStore">
    ///     Separate store for <see cref="ArtifactCategories.Presweep" /> (the backwards-compatible
    ///     <c>Bot:SecretSweep:BackupStoragePath</c> alias on the file system); <c>null</c> = <paramref name="store" />.
    /// </param>
    public BotArtifactStorage(IArtifactStore store, ArtifactKeyBuilder keys, ISecretFileProtector protector,
        string scratchDirectory, ILogger<BotArtifactStorage> logger, IArtifactStore? presweepStore = null)
    {
        _store = store;
        _keys = keys;
        _protector = protector;
        _logger = logger;
        _presweepStore = presweepStore;
        ScratchDirectory = Path.GetFullPath(scratchDirectory);
    }

    /// <inheritdoc />
    public string ScratchDirectory { get; }

    /// <inheritdoc />
    public bool CanEncrypt => _protector.IsConfigured;

    /// <inheritdoc />
    public string CreateScratchFilePath(string suffix)
    {
        if (suffix.Contains(Path.DirectorySeparatorChar) || suffix.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The suffix must not contain a directory separator.", nameof(suffix));
        }

        OwnerOnlyFiles.CreateDirectory(ScratchDirectory);
        return Path.Combine(ScratchDirectory, $"{Guid.NewGuid():N}{suffix}");
    }

    /// <inheritdoc />
    public void DeleteScratchFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not delete scratch file '{FilePath}'", path);
        }
    }

    /// <inheritdoc />
    public int CleanupScratch(TimeSpan maxAge)
    {
        if (!Directory.Exists(ScratchDirectory))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - maxAge;
        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(ScratchDirectory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Could not delete stale scratch file '{FilePath}'", file);
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<StoredArtifact> StoreFileAsync(string category, string tenantId, string fileName,
        string localFilePath, ArtifactEncryption encryption, CancellationToken cancellationToken = default)
    {
        var encrypt = encryption switch
        {
            ArtifactEncryption.Required => CanEncrypt
                ? true
                : throw new SecretEncryptionNotConfiguredException(
                    $"Artifacts of category '{category}' must be encrypted, but the key ring has no active key " +
                    "(SecretEncryption:Keys / ActiveKeyId)."),
            ArtifactEncryption.IfConfigured => CanEncrypt,
            _ => false
        };

        var storedName = encrypt ? fileName + SecretFileFormat.FileExtension : fileName;
        var key = _keys.Build(category, tenantId, storedName);
        var tenant = ArtifactKeyBuilder.NormalizeTenantId(tenantId);
        var store = GetStore(category);
        var metadata = new ArtifactMetadata(encrypt ? ArtifactMetadata.DefaultContentType : ContentTypeOf(fileName),
            new Dictionary<string, string>
            {
                ["category"] = category,
                ["tenant"] = tenant,
                ["encrypted"] = encrypt ? "true" : "false"
            });

        try
        {
            await using var source = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (encrypt)
            {
                await PutEncryptedAsync(store, key, source, metadata,
                    new SecretFileContext(tenant, category, ServiceName), cancellationToken);
            }
            else
            {
                await store.PutAsync(key, source, metadata, cancellationToken);
            }
        }
        catch
        {
            // Never leave a partial artifact behind (the providers may have written part of it).
            await TryDeleteAsync(store, key);
            throw;
        }

        _headerSnapshot = null;
        var info = await store.GetInfoAsync(key, cancellationToken);
        _logger.LogInformation(
            "Stored artifact '{Category}/{TenantId}/{FileName}' ({Size} bytes, encrypted: {Encrypted}) in the " +
            "{Provider} artifact store", category, tenant, storedName, info?.Size, encrypt, store.ProviderName);
        return new StoredArtifact(category, tenant, storedName, info?.Size ?? new FileInfo(localFilePath).Length,
            encrypt);
    }

    /// <inheritdoc />
    public Task<ArtifactInfo?> GetInfoAsync(string category, string tenantId, string fileName,
        CancellationToken cancellationToken = default)
    {
        return GetStore(category).GetInfoAsync(_keys.Build(category, tenantId, fileName), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string category, string tenantId, string fileName,
        CancellationToken cancellationToken = default)
    {
        var key = _keys.Build(category, tenantId, fileName);
        _headerSnapshot = null;
        return GetStore(category).DeleteAsync(key, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtifactKeyParts>> DeleteOlderThanAsync(string category, TimeSpan maxAge,
        CancellationToken cancellationToken = default)
    {
        var deletedKeys = await GetStore(category)
            .DeleteOlderThanAsync(_keys.CategoryPrefix(category), maxAge, cancellationToken);
        if (deletedKeys.Count > 0)
        {
            _headerSnapshot = null;
        }

        var deleted = new List<ArtifactKeyParts>(deletedKeys.Count);
        foreach (var key in deletedKeys)
        {
            _headerCache.TryRemove(key, out _);
            if (_keys.TryParse(key, out var parts) && parts != null)
            {
                deleted.Add(parts);
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<(string FileName, long Size)?> FindRestoreStagingAsync(string tenantId, string uploadId,
        CancellationToken cancellationToken = default)
    {
        if (!ArtifactKey.IsValidSegment(uploadId) || !ArtifactKey.IsValidSegment(uploadId + SecretFileFormat.FileExtension))
        {
            return null;
        }

        foreach (var name in new[] { uploadId + SecretFileFormat.FileExtension, uploadId })
        {
            var info = await GetInfoAsync(ArtifactCategories.RestoreStaging, tenantId, name, cancellationToken);
            if (info != null)
            {
                return (name, info.Size);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<bool> TryWritePlainToFileAsync(string category, string tenantId, string fileName,
        string targetPath, CancellationToken cancellationToken = default)
    {
        var tenant = ArtifactKeyBuilder.NormalizeTenantId(tenantId);
        var input = await GetStore(category).OpenReadAsync(_keys.Build(category, tenantId, fileName), cancellationToken);
        if (input == null)
        {
            return false;
        }

        var context = new SecretFileContext(tenant, category, ServiceName);
        var innerPath = targetPath + ".inner";
        try
        {
            await using (input)
            await using (var output = OwnerOnlyFiles.CreateNew(targetPath))
            {
                await WritePlainAsync(input, output, context, cancellationToken);
            }

            // An uploaded file may itself be an OCTOENC1 file of this environment (inside an encrypted staging
            // artifact): unwrap it once more so the restore always gets the dump.
            if (await StartsWithMagicAsync(targetPath, cancellationToken))
            {
                await using (var nestedInput = new FileStream(targetPath, FileMode.Open, FileAccess.Read,
                                 FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var nestedOutput = OwnerOnlyFiles.CreateNew(innerPath))
                {
                    await _protector.UnprotectAsync(nestedInput, nestedOutput, context, cancellationToken);
                }

                File.Move(innerPath, targetPath, true);
            }

            return true;
        }
        catch
        {
            // UnprotectAsync writes every verified chunk before it reads the next: a failed decryption leaves a
            // plaintext prefix behind, which must never be restored.
            DeleteScratchFile(innerPath);
            DeleteScratchFile(targetPath);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryUnprotectLocalFileAsync(string tenantId, string sourcePath, string targetPath,
        CancellationToken cancellationToken = default)
    {
        if (!await StartsWithMagicAsync(sourcePath, cancellationToken))
        {
            return false;
        }

        var context = new SecretFileContext(ArtifactKeyBuilder.NormalizeTenantId(tenantId),
            ArtifactCategories.RestoreStaging, ServiceName);
        try
        {
            await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = OwnerOnlyFiles.CreateNew(targetPath);
            await _protector.UnprotectAsync(input, output, context, cancellationToken);
        }
        catch
        {
            // A failed decryption leaves a verified plaintext prefix behind, which must never be restored.
            DeleteScratchFile(targetPath);
            throw;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<ArtifactDownload?> OpenDownloadAsync(string category, string tenantId, string fileName,
        CancellationToken cancellationToken = default)
    {
        var store = GetStore(category);
        var key = _keys.Build(category, tenantId, fileName);
        var info = await store.GetInfoAsync(key, cancellationToken);
        if (info == null)
        {
            return null;
        }

        var content = await store.OpenReadAsync(key, cancellationToken);
        if (content == null)
        {
            return null;
        }

        try
        {
            var (prefix, header) = await ReadHeadAsync(content, _protector, cancellationToken);
            var named = fileName.EndsWith(SecretFileFormat.FileExtension, StringComparison.Ordinal);
            if (named && header == null)
            {
                throw new InvalidSecretFileException(
                    $"The artifact '{category}/{tenantId}/{fileName}' is not an encrypted secret file.");
            }

            var downloadName = named ? fileName[..^SecretFileFormat.FileExtension.Length] : fileName;
            var plainLength = header == null ? info.Size : ComputePlainLength(info.Size, header);
            return new ArtifactDownload(new PrefixedReadStream(prefix, content), header != null, plainLength,
                downloadName, header);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task CopyPlainAsync(ArtifactDownload download, Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (download.Encrypted)
        {
            await _protector.UnprotectAsync(download.Content, destination,
                new SecretFileContext(null, "download", ServiceName), cancellationToken);
        }
        else
        {
            await download.Content.CopyToAsync(destination, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EncryptedArtifactHeader>> GetEncryptedArtifactHeadersAsync(
        CancellationToken cancellationToken = default)
    {
        if (_headerSnapshot is { } snapshot &&
            Environment.TickCount64 - snapshot.Ticks < (long)HeaderSnapshotLifetime.TotalMilliseconds)
        {
            return snapshot.Headers;
        }

        var result = new List<EncryptedArtifactHeader>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in KeyRetentionCategories)
        {
            var store = GetStore(category);
            await foreach (var info in store.ListAsync(_keys.CategoryPrefix(category), cancellationToken))
            {
                if (!info.Key.EndsWith(SecretFileFormat.FileExtension, StringComparison.Ordinal) ||
                    !_keys.TryParse(info.Key, out var parts) || parts == null)
                {
                    continue;
                }

                seen.Add(info.Key);
                var header = await GetHeaderAsync(store, info, cancellationToken);
                if (header != null)
                {
                    result.Add(new EncryptedArtifactHeader(parts.Category, parts.TenantId, parts.FileName, header));
                }
            }
        }

        foreach (var stale in _headerCache.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _headerCache.TryRemove(stale, out _);
        }

        _headerSnapshot = new HeaderSnapshot(Environment.TickCount64, result);
        return result;
    }

    /// <inheritdoc />
    public async Task<SecretFileHeader?> ReadHeaderAsync(string category, string tenantId, string fileName,
        CancellationToken cancellationToken = default)
    {
        var store = GetStore(category);
        var info = await store.GetInfoAsync(_keys.Build(category, tenantId, fileName), cancellationToken);
        return info == null ? null : await GetHeaderAsync(store, info, cancellationToken);
    }

    /// <inheritdoc />
    public bool CanUnprotect(SecretFileHeader header)
    {
        return _protector.CanUnprotect(header);
    }

    /// <summary>
    ///     Length of the plaintext of an <c>OCTOENC1</c> file of <paramref name="storedSize" /> bytes: every chunk
    ///     but the last holds exactly the chunk size, the last one fewer (possibly 0), each followed by a 16-byte
    ///     tag. <c>null</c> when the size does not fit the format (the decryption then fails anyway).
    /// </summary>
    internal static long? ComputePlainLength(long storedSize, SecretFileHeader header)
    {
        var body = storedSize - header.HeaderLength;
        if (body < TagLength || header.ChunkSize <= 0)
        {
            return null;
        }

        var fullChunks = (body - TagLength) / ((long)header.ChunkSize + TagLength);
        var rest = body - TagLength - fullChunks * (header.ChunkSize + TagLength);
        return rest < header.ChunkSize ? fullChunks * header.ChunkSize + rest : null;
    }

    private IArtifactStore GetStore(string category)
    {
        return category == ArtifactCategories.Presweep && _presweepStore != null ? _presweepStore : _store;
    }

    private async Task WritePlainAsync(Stream input, Stream output, SecretFileContext context,
        CancellationToken cancellationToken)
    {
        var (prefix, header) = await ReadHeadAsync(input, _protector, cancellationToken);
        var content = new PrefixedReadStream(prefix, input);
        if (header != null)
        {
            await _protector.UnprotectAsync(content, output, context, cancellationToken);
        }
        else
        {
            await content.CopyToAsync(output, cancellationToken);
        }
    }

    private async Task PutEncryptedAsync(IArtifactStore store, string key, Stream plaintext,
        ArtifactMetadata metadata, SecretFileContext context, CancellationToken cancellationToken)
    {
        // Encrypt and upload concurrently through a bounded pipe: constant memory, no second scratch copy.
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 4 * 1024 * 1024,
            resumeWriterThreshold: 2 * 1024 * 1024));
        var protect = Task.Run(async () =>
        {
            try
            {
                await _protector.ProtectAsync(plaintext, pipe.Writer.AsStream(true), context, cancellationToken);
                await pipe.Writer.CompleteAsync();
            }
            catch (Exception e)
            {
                await pipe.Writer.CompleteAsync(e);
                throw;
            }
        }, CancellationToken.None);

        try
        {
            await store.PutAsync(key, pipe.Reader.AsStream(true), metadata, cancellationToken);
        }
        catch (Exception putError)
        {
            await pipe.Reader.CompleteAsync(putError);
            var protectError = await CaptureAsync(protect);
            if (protectError != null)
            {
                ExceptionDispatchInfo.Throw(protectError);
            }

            throw;
        }

        await pipe.Reader.CompleteAsync();
        await protect;
    }

    private async Task<SecretFileHeader?> GetHeaderAsync(IArtifactStore store, ArtifactInfo info,
        CancellationToken cancellationToken)
    {
        if (_headerCache.TryGetValue(info.Key, out var cached) && cached.CreatedAt == info.CreatedAt &&
            cached.Size == info.Size)
        {
            return cached.Header;
        }

        try
        {
            var content = await store.OpenReadAsync(info.Key, cancellationToken);
            if (content == null)
            {
                return null;
            }

            await using (content)
            {
                // The header only - never the body of a possibly multi-GB dump.
                var (_, header) = await ReadHeadAsync(content, _protector, cancellationToken);
                if (header == null)
                {
                    return null;
                }

                _headerCache[info.Key] = (info.CreatedAt, info.Size, header);
                return header;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not read the header of encrypted artifact '{Key}'", info.Key);
            return null;
        }
    }

    /// <summary>
    ///     Reads the first bytes of <paramref name="input" />: the complete <c>OCTOENC1</c> header when the magic
    ///     matches, otherwise up to the magic length. The bytes read are returned to be replayed in front of the
    ///     rest of the stream (<see cref="PrefixedReadStream" />).
    /// </summary>
    internal static async Task<(byte[] Prefix, SecretFileHeader? Header)> ReadHeadAsync(Stream input,
        ISecretFileProtector protector, CancellationToken cancellationToken)
    {
        var head = new byte[MagicLength + 2];
        var read = await input.ReadAtLeastAsync(head, head.Length, false, cancellationToken);
        if (read < MagicLength + 2 || !head.AsSpan(0, MagicLength).SequenceEqual(Magic))
        {
            return (head[..read], null);
        }

        var headerLength = FixedHeaderLength + head[MagicLength + 1];
        var prefix = new byte[headerLength];
        head.CopyTo(prefix, 0);
        var rest = await input.ReadAtLeastAsync(prefix.AsMemory(head.Length), headerLength - head.Length, false,
            cancellationToken);
        prefix = prefix[..(head.Length + rest)];
        using var headerStream = new MemoryStream(prefix, false);
        return (prefix, protector.ReadHeader(headerStream));
    }

    private static async Task<bool> StartsWithMagicAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous);
        var head = new byte[MagicLength];
        var read = await stream.ReadAtLeastAsync(head, MagicLength, false, cancellationToken);
        return read == MagicLength && head.AsSpan().SequenceEqual(Magic);
    }

    private static string ContentTypeOf(string fileName)
    {
        if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return "application/zip";
        }

        return fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? "application/gzip"
            : ArtifactMetadata.DefaultContentType;
    }

    private async Task TryDeleteAsync(IArtifactStore store, string key)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not delete the partial artifact '{Key}'", key);
        }
    }

    private sealed record HeaderSnapshot(long Ticks, IReadOnlyList<EncryptedArtifactHeader> Headers);

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }
}
