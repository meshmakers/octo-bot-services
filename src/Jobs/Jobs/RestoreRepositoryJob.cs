using Meshmakers.Common.Shared.Services;
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
public class RestoreRepositoryJob(
    ILogger<RunFixupJob> logger,
    ISystemContext systemContext,
    IDistributedCacheService distributedCache,
    ICommandExecutionService commandExecutionService) : IRestoreRepositoryJob
{
    /// <inheritdoc />
    public async Task Run(string tenantId, string databaseName, string cacheKey,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                return;
            }

            // Check if the tenant exists and delete it if it does
            var tenantContext = await systemContext.TryFindTenantContextAsync(tenantId);
            if (tenantContext != null)
            {
                logger.LogInformation("Tenant \'{TenantId}\' already exists, deleting it before restore", tenantId);
                await DropTenantAsync(tenantId);
            }
            else
            {
                logger.LogInformation("Tenant \'{TenantId}\' does not exist, proceeding with restore", tenantId);
            }

            logger.LogInformation("Running restore command for \'{TenantId}\'", tenantId);
            var tempFile = await GetTempFileAsync(cacheKey);
            var r = await commandExecutionService.ExecuteMongoRestoreAsync(new MongoRestoreOptions
            {
                Database = databaseName,
                Archive = tempFile.Item1,
                Gzip = true,
                Drop = true
            }, TimeSpan.FromHours(1), cancellationToken?.ShutdownToken);

            if (!r.Success)
            {
                throw JobFailedException.CommandExecutionFailed(r, tenantId, "mongorestore");
            }

            logger.LogInformation("Restored database \'{DatabaseName}\' for tenant \'{TenantId}\'", databaseName,
                tenantId);
            await ClearCache(cacheKey);

            logger.LogInformation("Attaching tenant \'{TenantId}\' to database \'{DatabaseName}\'", tenantId,
                databaseName);
            using var session = await systemContext.GetAdminSessionAsync();
            session.StartTransaction();
            await systemContext.AttachChildTenantAsync(session, databaseName, tenantId);
            await session.CommitTransactionAsync();
            logger.LogInformation(
                "Tenant \'{TenantId}\' successfully restored and attached to database \'{DatabaseName}\'", tenantId,
                databaseName);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while restoring database for tenant \'{TenantId}\'", tenantId);
            throw;
        }
    }

    private async Task DropTenantAsync(string tenantId)
    {
        using var session = await systemContext.GetAdminSessionAsync();
        session.StartTransaction();
        await systemContext.DropChildTenantAsync(session, tenantId);
        await session.CommitTransactionAsync();
    }

    private async Task<Tuple<string, string>> GetTempFileAsync(string key)
    {
        var cacheStream = await distributedCache.GetCacheStreamByIdAsync(systemContext.TenantId, key);
        if (cacheStream == null)
        {
            throw JobFailedException.CacheStreamNotFound(systemContext.TenantId, key);
        }

        var tempFile = Path.ChangeExtension(Path.GetTempFileName(), "tar.gz");

        if (cacheStream.ContentType.ToLower() == MimeTypes.MimeTypeGzip)
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