using System.Globalization;
using Meshmakers.Octo.Backend.Jobs.Services;
using tusdotnet.Interfaces;
using tusdotnet.Models;

namespace Meshmakers.Octo.Backend.BotServices.Services;

/// <summary>
///     The tus store of one tenant over the <see cref="ISharedUploadStore" /> (AB#8736). It replaces the pod-local
///     <c>TusDiskStore</c>: with more than one bot replica and no ingress affinity, the creation, the offset
///     <c>HEAD</c> and every <c>PATCH</c> of an upload may each reach another replica, and so may the job start.
/// </summary>
/// <remarks>
///     Supports the tus core protocol, creation and termination. Clients do not use deferred length, checksums,
///     concatenation or expiration (Studio's tus-js-client and the SDK's <c>TusUploader</c>), so they are not offered.
///     tusdotnet's file lock is per process; parts of two concurrent requests to two replicas cannot corrupt the
///     upload: each part is stored at the <c>Upload-Offset</c> tusdotnet validated for its request (tusdotnet answers
///     that offset plus the bytes written), and overlapping parts hold the same bytes (see
///     <see cref="ISharedUploadStore" />).
/// </remarks>
/// <param name="uploads">The shared upload store.</param>
/// <param name="tenantId">The tenant of the request.</param>
/// <param name="request">The tus request; its <c>Upload-Offset</c> header is the offset of a <c>PATCH</c>.</param>
internal sealed class ArtifactTusStore(ISharedUploadStore uploads, string tenantId, HttpRequest request)
    : ITusStore, ITusCreationStore, ITusTerminationStore
{
    /// <inheritdoc />
    public async Task<long> AppendDataAsync(string fileId, Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await uploads.AppendAsync(tenantId, fileId, GetRequestOffset(), stream, cancellationToken);
        }
        catch (SharedUploadException e)
        {
            // tusdotnet answers a TusStoreException with 400 and its message.
            throw new TusStoreException(e.Message);
        }
    }

    /// <summary>
    ///     The <c>Upload-Offset</c> of a <c>PATCH</c>, which tusdotnet has checked against the stored offset before it
    ///     calls <see cref="AppendDataAsync" />. Only a creation with upload carries none: its file was created by the
    ///     same request, at offset 0.
    /// </summary>
    private long GetRequestOffset()
    {
        var header = request.Headers["Upload-Offset"].ToString();
        if (string.IsNullOrEmpty(header))
        {
            return 0;
        }

        return long.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : throw new TusStoreException($"The Upload-Offset header '{header}' is not a number.");
    }

    /// <inheritdoc />
    public async Task<bool> FileExistAsync(string fileId, CancellationToken cancellationToken)
    {
        return await uploads.GetAsync(tenantId, fileId, cancellationToken) != null;
    }

    /// <inheritdoc />
    public async Task<long?> GetUploadLengthAsync(string fileId, CancellationToken cancellationToken)
    {
        return (await uploads.GetAsync(tenantId, fileId, cancellationToken))?.UploadLength;
    }

    /// <inheritdoc />
    public Task<long> GetUploadOffsetAsync(string fileId, CancellationToken cancellationToken)
    {
        return uploads.GetOffsetAsync(tenantId, fileId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> CreateFileAsync(long uploadLength, string metadata, CancellationToken cancellationToken)
    {
        return uploads.CreateAsync(tenantId, uploadLength, metadata, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> GetUploadMetadataAsync(string fileId, CancellationToken cancellationToken)
    {
        var metadata = (await uploads.GetAsync(tenantId, fileId, cancellationToken))?.Metadata;
        return string.IsNullOrEmpty(metadata) ? null : metadata;
    }

    /// <inheritdoc />
    public Task DeleteFileAsync(string fileId, CancellationToken cancellationToken)
    {
        return uploads.DeleteAsync(tenantId, fileId, cancellationToken);
    }
}
