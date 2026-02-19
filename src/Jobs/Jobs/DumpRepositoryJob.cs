using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;
using RepositoryUpdate;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that dumps a tenant database to a backup file on disk.
/// </summary>
public class DumpRepositoryJob(
    ILogger<DumpRepositoryJob> logger,
    ISystemContext systemContext,
    IBackupFileStorageService backupFileStorage) : IDumpRepositoryJob
{
    /// <inheritdoc />
    public async Task<string?> Run(string tenantId, IBotCancellationToken? cancellationToken)
    {
        try
        {
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                return null;
            }

            var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

            if (tenantContext == null)
            {
                throw RepositoryUpdateException.TenantContextNotFound(tenantId);
            }

            var fileName = backupFileStorage.GenerateDumpFileName(tenantId);
            var filePath = backupFileStorage.GetDumpFilePath(tenantId, fileName);

            // Ensure tenant subdirectory exists
            var directory = Path.GetDirectoryName(filePath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            logger.LogInformation("Running dump repository command for '{TenantId}' to '{FilePath}'", tenantId,
                filePath);

            var r = await systemContext.BackupTenantAsync(tenantId, filePath);

            if (r.Success)
            {
                logger.LogInformation("Dump completed for tenant '{TenantId}' at '{FilePath}'", tenantId, filePath);
                return filePath;
            }

            throw JobFailedException.CommandExecutionFailed(r, tenantId, "mongodump");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while dumping repository database for tenant '{TenantId}'", tenantId);
            throw;
        }
    }
}