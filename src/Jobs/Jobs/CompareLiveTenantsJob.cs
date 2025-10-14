using Meshmakers.Octo.Backend.Jobs.DTOs;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that compares two live tenants
/// </summary>
public class CompareLiveTenantsJob : CompareTenantsJobBase, ICompareLiveTenantsJob
{
    private readonly ILogger<CompareLiveTenantsJob> _logger;
    private readonly ISystemContext _systemContext;
    private readonly ITenantComparisonService _tenantComparisonService;

    /// <summary>
    /// Implements a job that compares two live tenants
    /// </summary>
    public CompareLiveTenantsJob(ILogger<CompareLiveTenantsJob> logger,
        ISystemContext systemContext,
        ITenantComparisonService tenantComparisonService,
        IDistributedCacheService distributedCache) : base(distributedCache, logger)
    {
        _logger = logger;
        _systemContext = systemContext;
        _tenantComparisonService = tenantComparisonService;
    }

    /// <inheritdoc />
    public async Task<string?> Run(string sourceTenantId, string targetTenantId,
        TenantComparisonOptionsDto? options,
        IBotCancellationToken? cancellationToken)
    {
        try
        {
            if (!await _systemContext.IsSystemTenantExistingAsync())
            {
                return null;
            }

            _logger.LogInformation(
                "Starting comparison of live tenant '{SourceTenantId}' with live tenant '{TargetTenantId}'",
                sourceTenantId, targetTenantId);

            // Perform the comparison
            var report = await _tenantComparisonService.CompareTenantAsync(
                sourceTenantId,
                targetTenantId,
                GetOptions(options),
                cancellationToken?.ShutdownToken ?? CancellationToken.None);

            // Serialize the report to JSON and cache it
            var cacheKey = await CacheReportToDistributedCache(_systemContext.TenantId, report);

            _logger.LogInformation(
                "Successfully completed comparison of tenants '{SourceTenantId}' and '{TargetTenantId}'",
                sourceTenantId, targetTenantId);

            return cacheKey;
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error while comparing tenants '{SourceTenantId}' and '{TargetTenantId}'",
                sourceTenantId, targetTenantId);
            throw;
        }
    }
}