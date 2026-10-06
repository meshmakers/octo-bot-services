using System.Net.Mime;
using Meshmakers.Octo.Backend.Jobs.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Meshmakers.Octo.Backend.BotServices.Controllers;

/// <summary>
///     Streams an artifact from the artifact store to the client in plaintext form (AB#5561): an encrypted
///     (<c>OCTOENC1</c>) artifact is decrypted chunk by chunk while it is sent, so the client receives exactly the
///     file it received before the artifact store existed.
/// </summary>
/// <remarks>
///     🔴 The decryption verifies each chunk before it is written, but earlier chunks are already on the wire when a
///     later one fails (tampering, truncation). The response then must not look complete: the connection is
///     aborted, and with the announced <c>Content-Length</c> (or chunked encoding without a terminating chunk) the
///     client sees a failed transfer instead of a short, apparently valid file.
/// </remarks>
internal sealed class ArtifactDownloadResult(
    ArtifactDownload download,
    IBotArtifactStorage artifactStorage,
    string contentType,
    ILogger logger) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var httpContext = context.HttpContext;
        await using (download)
        {
            var response = httpContext.Response;
            response.ContentType = contentType;
            response.ContentLength = download.PlainLength;
            response.Headers[HeaderNames.ContentDisposition] = new ContentDisposition
            {
                FileName = download.DownloadFileName,
                DispositionType = DispositionTypeNames.Attachment
            }.ToString();

            try
            {
                await artifactStorage.CopyPlainAsync(download, response.Body, httpContext.RequestAborted);
            }
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                // The client went away.
            }
            catch (Exception e)
            {
                logger.LogError(e, "Download of artifact '{FileName}' failed; aborting the response",
                    download.DownloadFileName);
                if (!response.HasStarted)
                {
                    response.ContentLength = null;
                    response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                }

                httpContext.Abort();
            }
        }
    }
}
