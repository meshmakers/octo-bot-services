using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;
using RepositoryUpdate.Models;
using RepositoryUpdate.Services;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that runs fixup tasks for a tenant.
/// </summary>
public class DumpRepositoryJob(
    ILogger<RunFixupJob> logger,
    ISystemContext systemContext,
    IDistributedCacheService distributedCache,
    ICommandExecutionService commandExecutionService) : IDumpRepositoryJob
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

            var filePath = Path.ChangeExtension(Path.GetTempFileName(), "tar.gz");

            logger.LogInformation("Running dump repository command for \'{TenantId}\'", tenantId);
            var r = await commandExecutionService.ExecuteMongoDumpAsync(new MongoDumpOptions
            {
                Database = tenantContext.DatabaseName,
                Archive = filePath,
                Gzip = true
            }, cancellationToken?.ShutdownToken);

            if (r.Success)
            {
                var key = await CacheFileToDistributedCache(tenantId, filePath);
                return key;
            }

            throw JobFailedException.CommandExecutionFailed(r, tenantId, "mongodump");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while dump repository  database for tenant \'{TenantId}\'", tenantId);
            throw;
        }
    }

    private async Task<string> CacheFileToDistributedCache(string tenantId, string tempFile)
    {
        using var streamReader = new StreamReader(tempFile);

        return await distributedCache.CreateStreamAsync(tenantId, streamReader.BaseStream, MimeTypes.MimeTypeGzip, "Dump.tar.gz",
            TimeSpan.FromHours(1));
    }
}