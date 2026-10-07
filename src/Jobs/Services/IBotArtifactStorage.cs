using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;

namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     The bot's view of the platform artifact store (AB#5561): pre-sweep dumps, tenant dumps and restore staging
///     files, encrypted with the instance key ring (<see cref="ISecretFileProtector" />, format <c>OCTOENC1</c>,
///     AB#5559) where that is required or possible, plus the local scratch directory the dump tools work in.
/// </summary>
/// <remarks>
///     <para>
///         Keys follow <c>&lt;instancePrefix&gt;/&lt;category&gt;/&lt;tenantId&gt;/&lt;file&gt;</c>
///         (<see cref="ArtifactKeyBuilder" />, <see cref="ArtifactCategories" />). Encrypted artifacts carry the
///         suffix <see cref="SecretFileFormat.FileExtension" />, which this service appends itself.
///     </para>
///     <para>
///         🔴 Decryption never hands a caller a partly verified plaintext without saying so: file targets are
///         deleted when <see cref="ISecretFileProtector.UnprotectAsync" /> throws, and stream targets
///         (<see cref="CopyPlainAsync" />) must be aborted by the caller on an exception.
///     </para>
/// </remarks>
public interface IBotArtifactStorage
{
    /// <summary>
    ///     Local scratch directory (owner-only on Unix) where mongodump writes and encrypted artifacts are decrypted
    ///     for mongorestore. Files there are deleted after use and by the hourly cleanup.
    /// </summary>
    string ScratchDirectory { get; }

    /// <summary>
    ///     True when the key ring has an active key, i.e. artifacts can be encrypted.
    /// </summary>
    bool CanEncrypt { get; }

    /// <summary>
    ///     Creates the scratch directory if needed and returns the path of a new, not yet existing scratch file
    ///     ending with <paramref name="suffix" />.
    /// </summary>
    /// <param name="suffix">File name suffix, e.g. <c>.presweep.tar.gz</c>.</param>
    string CreateScratchFilePath(string suffix);

    /// <summary>
    ///     Deletes a scratch file; never throws.
    /// </summary>
    void DeleteScratchFile(string? path);

    /// <summary>
    ///     Deletes scratch files older than <paramref name="maxAge" /> (left behind by a crash).
    /// </summary>
    /// <returns>The number of deleted files.</returns>
    int CleanupScratch(TimeSpan maxAge);

    /// <summary>
    ///     Uploads <paramref name="localFilePath" /> to the store under
    ///     <c>&lt;category&gt;/&lt;tenantId&gt;/&lt;fileName&gt;</c> (plus <c>.octoenc</c> when encrypted).
    /// </summary>
    /// <param name="category">One of <see cref="ArtifactCategories" />.</param>
    /// <param name="tenantId">The tenant the artifact belongs to (lower-cased in the key).</param>
    /// <param name="fileName">The plain file name, without <c>.octoenc</c>.</param>
    /// <param name="localFilePath">The local file to upload; not deleted.</param>
    /// <param name="encryption">Whether the artifact is encrypted.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>What was stored.</returns>
    /// <exception cref="SecretEncryptionNotConfiguredException">
    ///     <paramref name="encryption" /> is <see cref="ArtifactEncryption.Required" /> and no active key exists.
    /// </exception>
    Task<StoredArtifact> StoreFileAsync(string category, string tenantId, string fileName, string localFilePath,
        ArtifactEncryption encryption, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Size and timestamp of an artifact, or <c>null</c> when it does not exist.
    /// </summary>
    Task<ArtifactInfo?> GetInfoAsync(string category, string tenantId, string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes an artifact.
    /// </summary>
    /// <returns><c>true</c> when it existed.</returns>
    Task<bool> DeleteAsync(string category, string tenantId, string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     App-side retention of one category (all tenants of this instance).
    /// </summary>
    /// <returns>The deleted artifacts.</returns>
    Task<IReadOnlyList<ArtifactKeyParts>> DeleteOlderThanAsync(string category, TimeSpan maxAge,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the stored file name of the restore staging upload <paramref name="uploadId" /> of
    ///     <paramref name="tenantId" /> (<c>&lt;uploadId&gt;.octoenc</c> or <c>&lt;uploadId&gt;</c>) and its size,
    ///     or <c>null</c> when the upload is not staged in the store.
    /// </summary>
    Task<(string FileName, long Size)?> FindRestoreStagingAsync(string tenantId, string uploadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes the plaintext of an artifact to <paramref name="targetPath" />: decrypted when it is an
    ///     <c>OCTOENC1</c> file (also an uploaded one inside an encrypted staging artifact), copied otherwise. On
    ///     any failure the target file is deleted and the exception propagates.
    /// </summary>
    /// <returns><c>false</c> when the artifact does not exist.</returns>
    /// <exception cref="InvalidSecretFileException">The artifact is tampered with or truncated.</exception>
    /// <exception cref="UnknownSecretKeyIdException">The artifact's key id is not in the key ring.</exception>
    Task<bool> TryWritePlainToFileAsync(string category, string tenantId, string fileName, string targetPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Decrypts a local file (an upload restored from the local tus directory because staging it into the store
    ///     failed) into <paramref name="targetPath" /> when it is an <c>OCTOENC1</c> file. On any failure the target
    ///     file is deleted and the exception propagates.
    /// </summary>
    /// <param name="tenantId">The tenant the file belongs to (metrics context).</param>
    /// <param name="sourcePath">The local file; never modified.</param>
    /// <param name="targetPath">The scratch file that receives the plaintext.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    ///     <c>true</c> when the file was encrypted and its plaintext is in <paramref name="targetPath" />;
    ///     <c>false</c> when it is not an <c>OCTOENC1</c> file (nothing is written, use the source as is).
    /// </returns>
    /// <exception cref="InvalidSecretFileException">The file is tampered with or truncated.</exception>
    /// <exception cref="UnknownSecretKeyIdException">The file's key id is not in the key ring.</exception>
    Task<bool> TryUnprotectLocalFileAsync(string tenantId, string sourcePath, string targetPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Opens an artifact for a download in plaintext form. The caller disposes the result.
    /// </summary>
    /// <returns><c>null</c> when the artifact does not exist.</returns>
    Task<ArtifactDownload?> OpenDownloadAsync(string category, string tenantId, string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Copies the plaintext of <paramref name="download" /> to <paramref name="destination" />, decrypting on
    ///     the fly. 🔴 When this throws, <paramref name="destination" /> holds a verified prefix only and the
    ///     caller must discard it (abort the HTTP response).
    /// </summary>
    Task CopyPlainAsync(ArtifactDownload download, Stream destination, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The header (key id, creation time) of every encrypted pre-sweep dump and tenant dump of this instance
    ///     (restore staging uploads are excluded: tenant users upload them with arbitrary key ids), read from the first
    ///     bytes of each artifact only (cached per key; the whole result is reused for up to a minute unless this
    ///     instance stored or deleted an artifact). Used for the key-id retention check (<c>DumpKeyMissing</c>).
    /// </summary>
    Task<IReadOnlyList<EncryptedArtifactHeader>> GetEncryptedArtifactHeadersAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The <c>OCTOENC1</c> header of one artifact (first bytes only), or <c>null</c> when the artifact does not
    ///     exist or is not encrypted.
    /// </summary>
    Task<SecretFileHeader?> ReadHeaderAsync(string category, string tenantId, string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     True when the key id of <paramref name="header" /> is in the key ring.
    /// </summary>
    bool CanUnprotect(SecretFileHeader header);
}

/// <summary>
///     Whether an artifact is encrypted with the key ring.
/// </summary>
public enum ArtifactEncryption
{
    /// <summary>Never encrypted.</summary>
    None = 0,

    /// <summary>Encrypted when the key ring has an active key, stored as is otherwise.</summary>
    IfConfigured = 1,

    /// <summary>Always encrypted; refused without an active key (pre-sweep dumps).</summary>
    Required = 2
}

/// <summary>
///     An artifact written by <see cref="IBotArtifactStorage.StoreFileAsync" />.
/// </summary>
/// <param name="Category">The category.</param>
/// <param name="TenantId">The lower-cased tenant id.</param>
/// <param name="FileName">The stored file name (with <c>.octoenc</c> when encrypted).</param>
/// <param name="Size">Stored size in bytes (ciphertext size when encrypted).</param>
/// <param name="Encrypted">Whether the artifact is an <c>OCTOENC1</c> file.</param>
public sealed record StoredArtifact(string Category, string TenantId, string FileName, long Size, bool Encrypted)
{
    /// <summary>
    ///     Prefix of a job result that names an artifact instead of a local file.
    /// </summary>
    public const string ResultReferencePrefix = "octo-artifact:";

    /// <summary>
    ///     The job result value that refers to this artifact: <c>octo-artifact:&lt;category&gt;/&lt;tenant&gt;/&lt;file&gt;</c>
    ///     (without the instance prefix, which is configuration).
    /// </summary>
    public string ToResultReference()
    {
        return $"{ResultReferencePrefix}{Category}/{TenantId}/{FileName}";
    }

    /// <summary>
    ///     Parses a job result written by <see cref="ToResultReference" />.
    /// </summary>
    public static bool TryParseResultReference(string? value, out ArtifactKeyParts? parts)
    {
        parts = null;
        if (value == null || !value.StartsWith(ResultReferencePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value[ResultReferencePrefix.Length..].Split('/');
        if (segments.Length != 3 || segments.Any(s => !ArtifactKey.IsValidSegment(s)))
        {
            return false;
        }

        parts = new ArtifactKeyParts(segments[0], segments[1], segments[2]);
        return true;
    }
}

/// <summary>
///     An artifact opened for download.
/// </summary>
public sealed class ArtifactDownload : IAsyncDisposable, IDisposable
{
    internal ArtifactDownload(Stream content, bool encrypted, long? plainLength, string downloadFileName,
        SecretFileHeader? header)
    {
        Content = content;
        Encrypted = encrypted;
        PlainLength = plainLength;
        DownloadFileName = downloadFileName;
        Header = header;
    }

    /// <summary>The raw stored content (positioned at its first byte).</summary>
    public Stream Content { get; }

    /// <summary>Whether the content is an <c>OCTOENC1</c> file.</summary>
    public bool Encrypted { get; }

    /// <summary>Length of the plaintext, when it can be computed from the stored size.</summary>
    public long? PlainLength { get; }

    /// <summary>File name offered to the client (without <c>.octoenc</c>).</summary>
    public string DownloadFileName { get; }

    /// <summary>The header of an encrypted artifact.</summary>
    public SecretFileHeader? Header { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        Content.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return Content.DisposeAsync();
    }
}

/// <summary>
///     Header of an encrypted artifact (no key needed to read it).
/// </summary>
/// <param name="Category">The category.</param>
/// <param name="TenantId">The lower-cased tenant id.</param>
/// <param name="FileName">The stored file name.</param>
/// <param name="Header">The <c>OCTOENC1</c> header.</param>
public sealed record EncryptedArtifactHeader(string Category, string TenantId, string FileName, SecretFileHeader Header);
