using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Microsoft.Extensions.Logging;
using RepositoryUpdate.Services;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that runs fixup tasks for a tenant.
/// </summary>
public class RunFixupJob(
    ILogger<RunFixupJob> logger,
    ISystemContext systemContext,
    IRepositoryFixupService repositoryFixupService) : IRunFixupJob
{
    /// <inheritdoc />
    public async Task Run(string tenantId, IBotCancellationToken? cancellationToken)
    {
        try
        {
            if (!await systemContext.IsSystemTenantExistingAsync())
            {
                return;
            }

            logger.LogInformation("Running fixup job for tenant \'{TenantId}\'", tenantId);
            await repositoryFixupService.FixupRepositoryAsync(tenantId, cancellationToken?.ShutdownToken ?? null);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error while running fixup job for tenant \'{TenantId}\'", tenantId);
            throw;
        }
    }
}