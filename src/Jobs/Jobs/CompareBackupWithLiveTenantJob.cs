using Meshmakers.Octo.Backend.Jobs.DTOs;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that compares a backup archive with a live tenant
/// </summary>
#pragma warning disable CS9113 // Parameter is unread - will be used when Engine Task 03 is completed
public class CompareBackupWithLiveTenantJob : CompareTenantsJobBase, ICompareBackupWithLiveTenantJob
#pragma warning restore CS9113
{
    private readonly ILogger<CompareBackupWithLiveTenantJob> _logger;
    private readonly ISystemContext _systemContext;
    private readonly ITenantComparisonService _tenantComparisonService;

    /// <summary>
    /// Implements a job that compares a backup archive with a live tenant
    /// </summary>
    public CompareBackupWithLiveTenantJob(ILogger<CompareBackupWithLiveTenantJob> logger,
        ISystemContext systemContext,
        ITenantComparisonService tenantComparisonService,
        IDistributedCacheService distributedCache) : base(distributedCache, logger)
    {
        _logger = logger;
        _systemContext = systemContext;
        _tenantComparisonService = tenantComparisonService;
    }

    /// <inheritdoc />
    public async Task<string?> Run(string tenantId, string backupCacheKey, string liveTenantId,
        TenantComparisonOptionsDto? options,
        IBotCancellationToken? cancellationToken)
    {
        string? tempFile = null;

        try
        {
            if (!await _systemContext.IsSystemTenantExistingAsync())
            {
                return null;
            }

            _logger.LogInformation(
                "Starting comparison of backup with live tenant '{LiveTenantId}'",
                liveTenantId);

            // Get the backup file from cache
            tempFile = await GetTempFileFromCache(_systemContext.TenantId, backupCacheKey, "backup");

            var report = await _tenantComparisonService.CompareBackupWithTenantAsync(
                tempFile,
                liveTenantId,
                GetOptions(options),
                cancellationToken?.ShutdownToken ?? CancellationToken.None);

            // Serialize the report to JSON and cache it
            var cacheKey = await CacheReportToDistributedCache(_systemContext.TenantId, report);

            _logger.LogInformation(
                "Successfully completed comparison of backup with live tenant '{LiveTenantId}'",
                liveTenantId);

            return cacheKey;
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error while comparing backup with live tenant '{LiveTenantId}'",
                liveTenantId);
            throw;
        }
        finally
        {
            // Cleanup: Delete the temporary backup file and cache entry
            if (tempFile != null && File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary backup file: {TempFile}", tempFile);
                }
            }

            await ClearCache(_systemContext.TenantId, backupCacheKey);
        }
    }
}
