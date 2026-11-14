using Meshmakers.Octo.Services.Methods.Operations;

namespace Meshmakers.Octo.Backend.BotServices.Methods;

/// <summary>
///     AB#8881: the setup of a tenant removes the method operation records an earlier tenant with the same id left behind
///     (accepted before the current tenant's creation, <see cref="ICkMethodOperationTenantGeneration" />): those a failed
///     purge on delete kept, and one a call admitted just before the delete wrote after the purge.
/// </summary>
/// <remarks>
///     Cleanup only, never the guarantee: reads already ignore such records (the store registration wraps the store in
///     <see cref="CkMethodTenantGenerationOperationStore" />). It does nothing while the generation is unknown (the asset
///     repository has not yet written the lifecycle record of a brand-new tenant; the next setup pass cleans up), and a
///     failure is logged, never thrown, so it never blocks the setup. A restore keeps the creation time, so the restore's
///     own operation record is never removed.
/// </remarks>
internal sealed class OperationRecordGenerationCleanup(
    ICkMethodOperationTenantGeneration generation,
    ICkMethodOperationTenantPurge purge,
    ILogger<OperationRecordGenerationCleanup> logger)
{
    /// <summary>Removes the earlier generations' records of <paramref name="tenantId" />; never throws.</summary>
    /// <returns>The number of records removed (0 when the generation is unknown or the cleanup failed).</returns>
    public async Task<long> RunAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            var start = await generation.GetStartUtcAsync(tenantId, cancellationToken);
            if (start is not { } createdUtc)
            {
                return 0;
            }

            var removed = await purge.DeleteTenantAcceptedBeforeAsync(tenantId, createdUtc, cancellationToken);
            if (removed > 0)
            {
                logger.LogInformation(
                    "Removed {Count} method operation record(s) of an earlier tenant with the id '{TenantId}'", removed,
                    tenantId);
            }

            return removed;
        }
        catch (Exception e) when (!(e is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(e,
                "Could not remove the method operation records of an earlier tenant with the id '{TenantId}'; reads ignore them and they expire with their retention",
                tenantId);
            return 0;
        }
    }
}
