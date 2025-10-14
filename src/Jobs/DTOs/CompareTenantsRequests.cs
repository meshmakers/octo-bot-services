using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Meshmakers.Octo.Backend.Jobs.DTOs;

/// <summary>
/// Request model for comparing two live tenants
/// </summary>
public class CompareLiveTenantsRequest
{
    /// <summary>
    /// Source tenant identifier
    /// </summary>
    [Required]
    [JsonPropertyName("sourceTenantId")]
    public string SourceTenantId { get; set; } = null!;

    /// <summary>
    /// Target tenant identifier
    /// </summary>
    [Required]
    [JsonPropertyName("targetTenantId")]
    public string TargetTenantId { get; set; } = null!;

    /// <summary>
    /// Optional comparison options
    /// </summary>
    [JsonPropertyName("options")]
    public TenantComparisonOptionsDto? Options { get; set; }
}

/// <summary>
/// Request model for comparing a live tenant with a backup archive
/// </summary>
public class CompareTenantWithBackupRequest
{
    /// <summary>
    /// Live tenant identifier
    /// </summary>
    [Required]
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = null!;

    /// <summary>
    /// Backup file (.tar.gz)
    /// </summary>
    [Required]
    [JsonPropertyName("backupFile")]
    public IFormFile BackupFile { get; set; } = null!;

    /// <summary>
    /// Optional comparison options
    /// </summary>
    [JsonPropertyName("options")]
    public TenantComparisonOptionsDto? Options { get; set; }
}

/// <summary>
/// Request model for comparing two backup archives
/// </summary>
public class CompareBackupsRequest
{
    /// <summary>
    /// Source backup file (.tar.gz)
    /// </summary>
    [Required]
    [JsonPropertyName("sourceBackupFile")]
    public IFormFile SourceBackupFile { get; set; } = null!;

    /// <summary>
    /// Target backup file (.tar.gz)
    /// </summary>
    [Required]
    [JsonPropertyName("targetBackupFile")]
    public IFormFile TargetBackupFile { get; set; } = null!;

    /// <summary>
    /// Optional comparison options
    /// </summary>
    [JsonPropertyName("options")]
    public TenantComparisonOptionsDto? Options { get; set; }
}

/// <summary>
/// Request model for comparing a backup archive with a live tenant
/// </summary>
public class CompareBackupWithLiveTenantRequest
{
    /// <summary>
    /// Backup file (.tar.gz)
    /// </summary>
    [Required]
    [JsonPropertyName("backupFile")]
    public IFormFile BackupFile { get; set; } = null!;

    /// <summary>
    /// Live tenant identifier
    /// </summary>
    [Required]
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = null!;

    /// <summary>
    /// Optional comparison options
    /// </summary>
    [JsonPropertyName("options")]
    public TenantComparisonOptionsDto? Options { get; set; }
}

/// <summary>
/// Data transfer object for tenant comparison options
/// </summary>
public class TenantComparisonOptionsDto
{
    /// <summary>
    /// Comparison areas to include (metadata, models, entities, associations)
    /// Default is All
    /// </summary>
    [JsonPropertyName("areas")]
    public string Areas { get; set; } = "All";

    /// <summary>
    /// Maximum number of entities to compare per type (null = no limit)
    /// </summary>
    [JsonPropertyName("maxEntitiesPerType")]
    public int? MaxEntitiesPerType { get; set; }

    /// <summary>
    /// Include detailed property differences in entity comparison
    /// </summary>
    [JsonPropertyName("includePropertyDifferences")]
    public bool IncludePropertyDifferences { get; set; } = true;

    /// <summary>
    /// Include association differences in comparison
    /// </summary>
    [JsonPropertyName("includeAssociationDifferences")]
    public bool IncludeAssociationDifferences { get; set; } = true;
}