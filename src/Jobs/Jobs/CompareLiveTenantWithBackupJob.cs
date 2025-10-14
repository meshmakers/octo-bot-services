using Meshmakers.Octo.Backend.Jobs.DTOs;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Implements a job that compares a live tenant with a backup archive
/// </summary>
#pragma warning disable CS9113 // Parameter is unread - will be used when Engine Task 03 is completed
public class CompareLiveTenantWithBackupJob : CompareTenantsJobBase,  ICompareLiveTenantWithBackupJob
#pragma warning restore CS9113
{
    private readonly ILogger<CompareLiveTenantWithBackupJob> _logger;
    private readonly ISystemContext _systemContext;
    private readonly ITenantComparisonService _tenantComparisonService;

    /// <summary>
    /// Implements a job that compares a live tenant with a backup archive
    /// </summary>
    public CompareLiveTenantWithBackupJob(ILogger<CompareLiveTenantWithBackupJob> logger,
        ISystemContext systemContext,
        ITenantComparisonService tenantComparisonService,
        IDistributedCacheService distributedCache) : base(distributedCache, logger)
    {
        _logger = logger;
        _systemContext = systemContext;
        _tenantComparisonService = tenantComparisonService;
    }

    /// <inheritdoc />
    public async Task<string?> Run(string liveTenantId, string backupCacheKey,
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
                "Starting comparison of live tenant '{LiveTenantId}' with backup",
                liveTenantId);

            // Get the backup file from cache
            tempFile = await GetTempFileFromCache(_systemContext.TenantId, backupCacheKey, "backup");

            var report = await _tenantComparisonService.CompareTenantWithBackupAsync(
                liveTenantId,
                tempFile,
                GetOptions(options),
                cancellationToken?.ShutdownToken ?? CancellationToken.None);

            // Serialize the report to JSON and cache it
            var cacheKey = await CacheReportToDistributedCache(_systemContext.TenantId, report);

            _logger.LogInformation(
                "Successfully completed comparison of live tenant '{LiveTenantId}' with backup",
                liveTenantId);

            return cacheKey;
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                "Error while comparing live tenant '{LiveTenantId}' with backup",
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