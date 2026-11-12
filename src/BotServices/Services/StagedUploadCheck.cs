using Meshmakers.Octo.Backend.Jobs.Services;

namespace Meshmakers.Octo.Backend.BotServices.Services;

/// <summary>What is wrong with an upload a restore or archive import should read (AB#8447).</summary>
public enum StagedUploadProblem
{
    /// <summary>The upload is usable.</summary>
    None,

    /// <summary>No upload with this id exists for the tenant (also: it belongs to another tenant).</summary>
    NotFound,

    /// <summary>The tus upload exists but is not complete.</summary>
    Incomplete,

    /// <summary>The upload is empty (0 bytes).</summary>
    Empty
}

/// <summary>
///     Checks that an upload is usable for a tenant and not empty, without copying it (AB#8736: the job does that, on
///     whichever replica runs it): a staged file in the artifact store (<c>restore-staging/&lt;tenant&gt;/&lt;id&gt;</c>,
///     AB#5561), else a complete tus upload in the shared store, else a file on the local disk received before AB#8736.
///     The store key carries the tenant like the local directory does, so a foreign id is <see cref="StagedUploadProblem.NotFound" />
///     either way (AB#5060: "no such upload" and "another tenant's upload" are not told apart). Shared by the REST job
///     endpoints and the async methods of AB#8447.
/// </summary>
public static class StagedUploadCheck
{
    /// <summary>Checks the upload <paramref name="tusFileId" /> of <paramref name="tenantId" />.</summary>
    public static async Task<StagedUploadProblem> CheckAsync(ISharedUploadStore sharedUploads,
        IBackupFileStorageService backupFileStorage, string tenantId, string tusFileId, CancellationToken ct)
    {
        var staged = await sharedUploads.FindStagedAsync(tenantId, tusFileId, ct);
        long size;
        if (staged != null)
        {
            size = staged.Value.Size;
        }
        else if (await sharedUploads.GetAsync(tenantId, tusFileId, ct) is { } upload)
        {
            if (!await sharedUploads.IsCompleteAsync(tenantId, tusFileId, ct))
            {
                return StagedUploadProblem.Incomplete;
            }

            size = upload.UploadLength;
        }
        else
        {
            string filePath;
            try
            {
                filePath = backupFileStorage.GetTusUploadFilePath(tenantId, tusFileId);
            }
            catch (ArgumentException)
            {
                return StagedUploadProblem.NotFound;
            }

            if (!File.Exists(filePath))
            {
                return StagedUploadProblem.NotFound;
            }

            size = new FileInfo(filePath).Length;
        }

        return size == 0 ? StagedUploadProblem.Empty : StagedUploadProblem.None;
    }
}
