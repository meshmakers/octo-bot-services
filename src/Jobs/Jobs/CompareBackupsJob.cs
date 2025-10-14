using Meshmakers.Octo.Backend.Jobs.DTOs;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that compares two backup archives
/// </summary>
public class CompareBackupsJob : CompareTenantsJobBase,  ICompareBackupsJob
{
    private readonly ILogger<CompareBackupsJob> _logger;
    private readonly ISystemContext _systemContext;
    private readonly ITenantComparisonService _tenantComparisonService;

    /// <summary>
    /// Implements a job that compares two backup archives
    /// </summary>
    public CompareBackupsJob(ILogger<CompareBackupsJob> logger,
        ISystemContext systemContext,
        ITenantComparisonService tenantComparisonService,
        IDistributedCacheService distributedCache) : base(distributedCache, logger)
    {
        _logger = logger;
        _systemContext = systemContext;
        _tenantComparisonService = tenantComparisonService;
    }

    /// <inheritdoc />
    public async Task<string?> Run(string sourceBackupCacheKey, string targetBackupCacheKey,
        TenantComparisonOptionsDto? options,
        IBotCancellationToken? cancellationToken)
    {
        string? sourceTempFile = null;
        string? targetTempFile = null;

        try
        {
            if (!await _systemContext.IsSystemTenantExistingAsync())
            {
                return null;
            }

            _logger.LogInformation("Starting comparison of two backup archives");

            // Get both backup files from cache
            sourceTempFile = await GetTempFileFromCache(_systemContext.TenantId, sourceBackupCacheKey, "source");
            targetTempFile = await GetTempFileFromCache(_systemContext.TenantId, targetBackupCacheKey, "target");

            // Future implementation (uncomment when Engine method is available):
            var report = await _tenantComparisonService.CompareBackupsAsync(
                sourceTempFile,
                targetTempFile,
                GetOptions(options),
                cancellationToken?.ShutdownToken ?? CancellationToken.None);

            // Serialize the report to JSON and cache it
            var cacheKey = await CacheReportToDistributedCache(_systemContext.TenantId, report);

            _logger.LogInformation("Successfully completed comparison of two backup archives");

            return cacheKey;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while comparing two backup archives");
            throw;
        }
        finally
        {
            // Cleanup: Delete both temporary backup files and cache entries
            if (sourceTempFile != null && File.Exists(sourceTempFile))
            {
                try
                {
                    File.Delete(sourceTempFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete source backup file: {TempFile}", sourceTempFile);
                }
            }

            if (targetTempFile != null && File.Exists(targetTempFile))
            {
                try
                {
                    File.Delete(targetTempFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete target backup file: {TempFile}", targetTempFile);
                }
            }

            await ClearCache(_systemContext.TenantId, sourceBackupCacheKey);
            await ClearCache(_systemContext.TenantId, targetBackupCacheKey);
        }
    }
}