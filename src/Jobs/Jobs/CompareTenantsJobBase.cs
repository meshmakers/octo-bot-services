using System.Text.Json;
using Meshmakers.Octo.Backend.Jobs.DTOs;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison;
using Meshmakers.Octo.Sdk.ServiceClient;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Base class for tenant comparison jobs
/// </summary>
public abstract class CompareTenantsJobBase(
    IDistributedCacheService distributedCache,
    ILogger logger)
{
    /// <summary>
    /// Converts DTO options to internal options
    /// </summary>
    /// <param name="options"></param>
    /// <returns></returns>
    protected TenantComparisonOptions GetOptions(TenantComparisonOptionsDto? options)
    {
        if (options == null)
        {
            return new TenantComparisonOptions();
        }

        return new TenantComparisonOptions()
        {
            Areas = Enum.TryParse<ComparisonAreas>(options.Areas, out var areas) ? areas : ComparisonAreas.All,
            MaxEntitiesPerType = options.MaxEntitiesPerType,
            IncludePropertyDifferences = options.IncludePropertyDifferences,
            IncludeAssociationDifferences = options.IncludeAssociationDifferences
        };
    }

    /// <summary>
    /// Retrieves a temporary file from the distributed cache
    /// </summary>
    /// <param name="tenantId">The tenant ID for cache lookup</param>
    /// <param name="cacheKey">The cache key to retrieve</param>
    /// <param name="label">Optional label for logging purposes</param>
    /// <returns>Path to the temporary file</returns>
    protected async Task<string> GetTempFileFromCache(string tenantId, string cacheKey, string? label = null)
    {
        var cacheStream = await distributedCache.GetCacheStreamByIdAsync(tenantId, cacheKey);
        if (cacheStream == null)
        {
            throw JobFailedException.CacheStreamNotFound(tenantId, cacheKey);
        }

        var tempFile = Path.ChangeExtension(Path.GetTempFileName(), "tar.gz");

        if (cacheStream.ContentType.ToLower() == MimeTypes.MimeTypeGzip ||
            cacheStream.ContentType.ToLower() == MimeTypes.MimeTypeXGzip)
        {
            await using var streamWriter = new StreamWriter(tempFile);
            await cacheStream.Stream.CopyToAsync(streamWriter.BaseStream);

            if (!string.IsNullOrEmpty(label))
            {
                logger.LogInformation("Retrieved {Label} backup file from cache to: {TempFile}", label, tempFile);
            }

            return tempFile;
        }

        throw JobFailedException.ContentTypeNotSupported(cacheStream.ContentType);
    }

    /// <summary>
    /// Caches a comparison report to the distributed cache
    /// </summary>
    /// <param name="tenantId">The tenant ID for cache storage</param>
    /// <param name="report">The report object to cache</param>
    /// <returns>Cache key for the stored report</returns>
    protected async Task<string> CacheReportToDistributedCache(string tenantId, object report)
    {
        var jsonReport = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await using var memoryStream = new MemoryStream();
        await using var streamWriter = new StreamWriter(memoryStream);
        await streamWriter.WriteAsync(jsonReport);
        await streamWriter.FlushAsync();
        memoryStream.Position = 0;

        return await distributedCache.CreateStreamAsync(
            tenantId,
            memoryStream,
            MimeTypes.MimeTypeJson,
            "TenantComparisonReport.json",
            TimeSpan.FromHours(1));
    }

    /// <summary>
    /// Clears a cache entry
    /// </summary>
    /// <param name="tenantId">The tenant ID for cache lookup</param>
    /// <param name="cacheKey">The cache key to delete</param>
    protected async Task ClearCache(string tenantId, string cacheKey)
    {
        await distributedCache.DeleteCacheStreamAsync(tenantId, cacheKey);
    }
}