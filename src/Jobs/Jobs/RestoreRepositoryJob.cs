using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that runs fixup tasks for a tenant.
/// </summary>
public class RestoreRepositoryJob(
    ILogger<RunFixupJob> logger,
    ISystemContext systemContext,
    IDistributedCacheService distributedCache) : IRestoreRepositoryJob
{
    /// <inheritdoc />
    public async Task Run(string tenantId, string databaseName, string cacheKey,
        string? oldDatabaseName,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                return;
            }

            logger.LogInformation("Running restore command for \'{TenantId}\'", tenantId);
            var tempFile = await GetTempFileAsync(cacheKey);


            var r = await systemContext.RestoreTenantAsync(tenantId, databaseName, tempFile.Item1, oldDatabaseName,
                true, true,
                TimeSpan.FromHours(1), cancellationToken?.ShutdownToken ?? CancellationToken.None);

            if (!r.Success)
            {
                throw JobFailedException.CommandExecutionFailed(r, tenantId, "mongorestore");
            }

            logger.LogInformation("Restored database \'{DatabaseName}\' for tenant \'{TenantId}\'", databaseName,
                tenantId);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while restoring database for tenant \'{TenantId}\'", tenantId);
            throw;
        }
        finally
        {
            await ClearCache(cacheKey);
        }
    }

    private async Task<Tuple<string, string>> GetTempFileAsync(string key)
    {
        var cacheStream = await distributedCache.GetCacheStreamByIdAsync(systemContext.TenantId, key);
        if (cacheStream == null)
        {
            throw JobFailedException.CacheStreamNotFound(systemContext.TenantId, key);
        }

        var tempFile = Path.ChangeExtension(Path.GetTempFileName(), "tar.gz");

        if (cacheStream.ContentType.ToLower() == MimeTypes.MimeTypeGzip ||
            cacheStream.ContentType.ToLower() == MimeTypes.MimeTypeXGzip)
        {
            var contentType = cacheStream.ContentType;
            await using var streamWriter = new StreamWriter(tempFile);
            await cacheStream.Stream.CopyToAsync(streamWriter.BaseStream);
            return new Tuple<string, string>(tempFile, contentType);
        }

        throw JobFailedException.ContentTypeNotSupported(cacheStream.ContentType);
    }

    private async Task ClearCache(string key)
    {
        await distributedCache.DeleteCacheStreamAsync(systemContext.TenantId, key);
    }
}