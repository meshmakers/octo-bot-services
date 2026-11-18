using Meshmakers.Octo.Backend.Jobs.DumpRegistry;
using Meshmakers.Octo.Services.Methods.Operations;

namespace Meshmakers.Octo.Backend.BotServices.Services.DumpRegistry;

/// <summary>
///     The setup of a tenant removes the registry entries of an earlier tenant with the same id (dumped before the current
///     tenant was created). Cleanup only: reads already ignore them. Does nothing while the generation is unknown and
///     never throws.
/// </summary>
public sealed class DumpHashGenerationCleanup(
    ICkMethodOperationTenantGeneration generation,
    IDumpHashRegistry registry,
    ILogger<DumpHashGenerationCleanup> logger)
{
    /// <summary>Removes the earlier tenant's entries of <paramref name="tenantId" />.</summary>
    /// <returns>The number of removed entries.</returns>
    public async Task<long> RunAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            if (await generation.GetStartUtcAsync(tenantId, cancellationToken) is not { } tenantCreatedUtc)
            {
                return 0;
            }

            var removedCount = await registry.DeleteTenantDumpedBeforeAsync(tenantId, tenantCreatedUtc,
                cancellationToken);
            if (removedCount > 0)
            {
                logger.LogInformation(
                    "Removed {Count} dump registry entries of an earlier tenant with the id '{TenantId}'",
                    removedCount, tenantId);
            }

            return removedCount;
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception,
                "Could not remove the dump registry entries of an earlier tenant with the id '{TenantId}'; reads ignore them",
                tenantId);
            return 0;
        }
    }
}
