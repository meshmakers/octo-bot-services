using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that restores a tenant database from a backup file.
/// </summary>
public class RestoreRepositoryJob(
    ILogger<RestoreRepositoryJob> logger,
    ISystemContext systemContext,
    IBackupFileStorageService backupFileStorage) : IRestoreRepositoryJob
{
    /// <inheritdoc />
    public async Task Run(string tenantId, string databaseName, string cacheKey,
        string? oldDatabaseName,
        IBotCancellationToken? cancellationToken)
    {
        // cacheKey is used as the tus file ID (or legacy cache key)
        var filePath = backupFileStorage.GetTusUploadFilePath(cacheKey);

        try
        {
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                return;
            }

            if (!File.Exists(filePath))
            {
                throw new JobFailedException(
                    $"Backup file not found at '{filePath}' for tus file ID '{cacheKey}'.");
            }

            logger.LogInformation("Running restore command for '{TenantId}' from file '{FilePath}'", tenantId,
                filePath);

            var r = await systemContext.RestoreTenantAsync(tenantId, databaseName, filePath, oldDatabaseName,
                true, true,
                TimeSpan.FromHours(1), cancellationToken?.ShutdownToken ?? CancellationToken.None);

            if (!r.Success)
            {
                throw JobFailedException.CommandExecutionFailed(r, tenantId, "mongorestore");
            }

            logger.LogInformation("Restored database '{DatabaseName}' for tenant '{TenantId}'", databaseName,
                tenantId);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while restoring database for tenant '{TenantId}'", tenantId);
            throw;
        }
        finally
        {
            await backupFileStorage.DeleteFileAsync(filePath);
        }
    }
}