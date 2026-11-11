namespace Meshmakers.Octo.Backend.Jobs.Services;

/// <summary>
///     The tus uploads of the bot (restore and archive data import), kept in the platform artifact store so that every
///     replica serves every request of an upload and every replica can run the consuming job (AB#8736).
/// </summary>
/// <remarks>
///     <para>
///         An upload lives in the category <c>restore-staging</c> of its tenant, the area of the artifact store for
///         uploaded files (app-side retention <c>Bot:ArtifactRetentionHours</c>, lifecycle backstop one day):
///         <c>&lt;id&gt;.tus-info</c> (upload length and tus metadata, plain) and one object per received
///         <c>PATCH</c> body, <c>&lt;id&gt;.tus-part.&lt;offset&gt;.&lt;length&gt;.&lt;attempt&gt;[.octoenc]</c> (encrypted
///         when a key ring is configured; the attempt id is new for every request, so two requests never share a key).
///         The offset of an upload is the end of the contiguous run of parts from byte 0, so it is the same on every
///         replica and needs no lock. A part is stored at the offset its request was validated against; two requests
///         for the same offset (a client retry) store overlapping parts with the same bytes, which count once.
///     </para>
///     <para>
///         A completed upload stays as it is until the consuming job (on any replica) reads it: the archive data import
///         copies the parts straight into its scratch file, the restore assembles them into
///         <c>restore-staging/&lt;tenant&gt;/&lt;id&gt;[.octoenc]</c>, the staged file it reads since AB#5561. Nothing
///         is assembled inside a request. The job deletes the upload afterwards, after a success as after a failure.
///     </para>
/// </remarks>
public interface ISharedUploadStore
{
    /// <summary>
    ///     Creates an upload of <paramref name="uploadLength" /> bytes and returns its id (32 lower-case hex digits).
    /// </summary>
    /// <param name="tenantId">The tenant the upload belongs to.</param>
    /// <param name="uploadLength">The announced length in bytes.</param>
    /// <param name="metadata">The raw tus <c>Upload-Metadata</c> header, if any.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<string> CreateAsync(string tenantId, long uploadLength, string? metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     The upload <paramref name="uploadId" /> of <paramref name="tenantId" />, or <c>null</c> when it does not
    ///     exist (never created, deleted, assembled, or the id is not one this store creates).
    /// </summary>
    Task<SharedUpload?> GetAsync(string tenantId, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The number of bytes of the upload received so far; 0 when it does not exist.
    /// </summary>
    Task<long> GetOffsetAsync(string tenantId, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Appends the bytes of <paramref name="data" /> (one tus <c>PATCH</c> body) at <paramref name="offset" /> and
    ///     returns how many were stored. When the client drops the connection (an exception, or an end of stream with
    ///     <paramref name="cancellationToken" /> cancelled), the bytes received until then are kept (tus resumes from
    ///     the offset).
    /// </summary>
    /// <param name="tenantId">The tenant the upload belongs to.</param>
    /// <param name="uploadId">The upload.</param>
    /// <param name="offset">
    ///     The offset of the request (<c>Upload-Offset</c>, validated against <see cref="GetOffsetAsync" />), not the
    ///     offset at the time of the write: a concurrent request on another replica may have advanced it.
    /// </param>
    /// <param name="data">The request body.</param>
    /// <param name="cancellationToken">The request token.</param>
    /// <exception cref="SharedUploadException">
    ///     The upload does not exist, <paramref name="offset" /> is beyond the bytes received, or
    ///     <paramref name="data" /> holds more bytes than the upload has left from <paramref name="offset" />.
    /// </exception>
    Task<long> AppendAsync(string tenantId, string uploadId, long offset, Stream data,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes the upload (its description and every part); a staged file assembled from it is not touched.
    /// </summary>
    Task DeleteAsync(string tenantId, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     True when the upload exists and all of its bytes were received.
    /// </summary>
    Task<bool> IsCompleteAsync(string tenantId, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the staged file <c>restore-staging/&lt;tenant&gt;/&lt;id&gt;[.octoenc]</c> with its stored size, or
    ///     <c>null</c>. Never resolves to the description or a part of an upload.
    /// </summary>
    Task<(string FileName, long Size)?> FindStagedAsync(string tenantId, string uploadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes the plaintext of a complete upload to <paramref name="destination" />, part by part, without staging
    ///     it. 🔴 Copies the whole file: call it from a job, not from a request.
    /// </summary>
    /// <returns><c>false</c> (nothing written) when the upload does not exist or is not complete.</returns>
    /// <exception cref="InvalidDataException">A part is missing or does not hold the bytes its name announces.</exception>
    Task<bool> TryCopyCompleteUploadAsync(string tenantId, string uploadId, Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the staged file of the upload (see <see cref="FindStagedAsync" />). When it is not staged yet but the
    ///     upload is complete, assembles it first and deletes the parts. 🔴 Copies the whole file: call it from a job,
    ///     not from a request (a request could outlive the ingress timeout, and two concurrent assemblies of the same
    ///     upload would remove each other's result).
    /// </summary>
    /// <returns><c>null</c> when there is neither a staged file nor a complete upload of that id.</returns>
    Task<(string FileName, long Size)?> EnsureStagedAsync(string tenantId, string uploadId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     A tus upload in the shared store.
/// </summary>
/// <param name="UploadId">The id.</param>
/// <param name="UploadLength">The announced length in bytes.</param>
/// <param name="Metadata">The raw tus <c>Upload-Metadata</c> header, empty when there was none.</param>
public sealed record SharedUpload(string UploadId, long UploadLength, string Metadata);

/// <summary>
///     A request the shared upload store refuses (unknown upload, more data than announced).
/// </summary>
public sealed class SharedUploadException(string message) : Exception(message);
