using Meshmakers.Octo.ConstructionKit.Models.System.Bot.Generated.System.Bot.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Microsoft.Extensions.Logging;

namespace RepositoryUpdate.Services;

public class RepositoryFixupService(
    ILogger<RepositoryFixupService> logger,
    ISystemContext systemContext,
    IRepositoryOpsService repositoryOpsService) : IRepositoryFixupService
{
    public async Task FixupRepositoryAsync(string tenantId, CancellationToken? cancellationToken = null)
    {
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        if (tenantContext == null)
        {
            throw RepositoryUpdateException.TenantContextNotFound(tenantId);
        }

        // Perform the fixup operations
        var tenantRepository = tenantContext.GetTenantRepository();

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        logger.LogInformation("Starting repository fixup for tenant {TenantId}...", tenantId);
        var query = RtEntityQueryOptions.Create()
            .FieldEquals(nameof(RtFixup.Enabled), true)
            .FieldEquals(nameof(RtFixup.IsApplied), false)
            .SortOrder(nameof(RtFixup.Order), SortOrders.Ascending);
        var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync<RtFixup>(session, query);
        await session.CommitTransactionAsync();

        logger.LogInformation("Found {Count} fixups to apply for tenant {TenantId}", resultSet.TotalCount, tenantId);

        foreach (var rtFixup in resultSet.Items)
        {
            if (string.IsNullOrWhiteSpace(rtFixup.Script))
            {
                continue;
            }

            if (cancellationToken?.IsCancellationRequested == true)
            {
                logger.LogInformation("Fixup job for tenant {TenantId} was cancelled", tenantId);
                return;
            }

            logger.LogInformation("Applying fixup {FixupId} for tenant {TenantId}", rtFixup.RtId, tenantId);
            await ExecuteScriptAsync(rtFixup, tenantContext.DatabaseName, tenantRepository);
            logger.LogInformation("Fixup {FixupId} applied successfully for tenant {TenantId}", rtFixup.RtId, tenantId);
        }
    }

    private async Task ExecuteScriptAsync(RtFixup rtFixup, string databaseName, ITenantRepository tenantRepository,
        CancellationToken? cancellationToken = null)
    {
        IOctoSession session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();
        try
        {
            // Save Scripts property to the temporary file.
            var scriptFilePath = Path.ChangeExtension(Path.GetTempFileName(), "ts");
            try
            {
                await File.WriteAllTextAsync(scriptFilePath, rtFixup.Script);

                var commandResult =
                    await repositoryOpsService.ExecuteMongoShellScriptAsync(databaseName, scriptFilePath, cancellationToken);

                rtFixup.IsApplied = true;
                rtFixup.AppliedAt = DateTime.UtcNow;
                rtFixup.IsSuccess = commandResult.Success;
                rtFixup.Error = commandResult.Error;
                rtFixup.Output = commandResult.Output;

                await tenantRepository.UpdateOneRtEntityByIdAsync(session, rtFixup.RtId, rtFixup);
                await session.CommitTransactionAsync();
            }
            finally
            {
                if (File.Exists(scriptFilePath))
                {
                    File.Delete(scriptFilePath);
                }
            }
        }
        catch (Exception e)
        {
            throw RepositoryUpdateException.UpdateScriptFailed(rtFixup.RtId, e);
        }
    }
}