using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Secrets;

/// <inheritdoc />
public class SecretSweepRunService(
    ISecretSweepRunStore runStore,
    IBackupFileStorageService backupFileStorage,
    ILogger<SecretSweepRunService> logger,
    TimeProvider? timeProvider = null) : ISecretSweepRunService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretSweepRunDto>> GetRunsAsync(string tenantId, int limit)
    {
        var runs = await runStore.GetRunsAsync(tenantId, limit);
        foreach (var dump in runs.Select(r => r.Dump).OfType<SecretSweepDumpDto>())
        {
            RefreshFileState(tenantId, dump);
        }

        return runs;
    }

    /// <inheritdoc />
    public async Task<SecretSweepDumpDeleteResultDto> DeleteDumpAsync(string tenantId, string runId,
        string? deletedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return SecretSweepDumpDeleteResultDto.NotFound;
        }

        // Decided and recorded under the store's per-tenant lock, so two concurrent deletes answer 204 once
        // and 409 once.
        var result = SecretSweepDumpDeleteResultDto.NotFound;
        string? fileName = null;
        var deletedAt = _time.GetUtcNow().UtcDateTime;
        await runStore.UpdateAsync(tenantId, runId, run =>
        {
            if (run.Dump == null)
            {
                result = SecretSweepDumpDeleteResultDto.NotFound;
                return false;
            }

            if (run.Dump.DeletedAt != null)
            {
                result = SecretSweepDumpDeleteResultDto.AlreadyDeleted;
                return false;
            }

            fileName = run.Dump.FileName;
            run.Dump.DeletedAt = deletedAt;
            run.Dump.DeletedBy = deletedBy;
            run.Dump.Exists = false;
            result = SecretSweepDumpDeleteResultDto.Deleted;
            return true;
        });

        if (result == SecretSweepDumpDeleteResultDto.Deleted && fileName != null)
        {
            var path = backupFileStorage.GetSecretBackupFilePath(tenantId, fileName);
            await backupFileStorage.DeleteFileAsync(path);
            if (File.Exists(path))
            {
                // Recorded as deleted already; the hourly cleanup removes the file at the latest on expiry.
                logger.LogError("Pre-sweep secret backup '{FileName}' of tenant '{TenantId}' (run '{RunId}') was " +
                                "marked deleted but the file could not be removed", fileName, tenantId, runId);
            }
            else
            {
                logger.LogInformation("Pre-sweep secret backup '{FileName}' of tenant '{TenantId}' (run '{RunId}') " +
                                      "deleted early by '{DeletedBy}'", fileName, tenantId, runId,
                    deletedBy ?? "<unknown>");
            }
        }

        return result;
    }

    private void RefreshFileState(string tenantId, SecretSweepDumpDto dump)
    {
        try
        {
            var file = new FileInfo(backupFileStorage.GetSecretBackupFilePath(tenantId, dump.FileName));
            dump.Exists = dump.DeletedAt == null && file.Exists;
            if (file.Exists)
            {
                dump.SizeBytes = file.Length;
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            dump.Exists = false;
        }
    }
}
