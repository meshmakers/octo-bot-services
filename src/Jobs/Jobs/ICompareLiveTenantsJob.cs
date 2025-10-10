using System.ComponentModel;
using Hangfire;
using Meshmakers.Octo.Backend.Jobs.DTOs;

namespace Meshmakers.Octo.Backend.Jobs.Jobs;

/// <summary>
/// Job for comparing two live tenants
/// </summary>
public interface ICompareLiveTenantsJob
{
    /// <summary>
    /// Compares two live tenants and generates a comparison report
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="sourceTenantId">The source tenant ID</param>
    /// <param name="targetTenantId">The target tenant ID</param>
    /// <param name="options">The TenantComparisonOptionsDto</param>
    /// <param name="cancellationToken">A cancellation token to abort the job</param>
    /// <returns>The cache key where the comparison report (JSON) is stored</returns>
    [DisplayName("Compare live tenants '{0}' and '{1}'")]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(60 * 10)] // Prevents concurrent execution
    Task<string?> Run(string tenantId, string sourceTenantId, string targetTenantId, TenantComparisonOptionsDto? options, IBotCancellationToken? cancellationToken);
}