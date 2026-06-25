using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Services;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Extension methods for <see cref="IServiceCollection" />.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds the Octo commands to the service collection.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="tusStoragePath">The storage path for tus uploads.</param>
    /// <param name="dumpStoragePath">The storage path for database dumps.</param>
    /// <param name="fileRetentionHours">The number of hours to retain temporary files.</param>
    /// <param name="assetServiceUrl">Base URL of the Asset Repository service (StreamData endpoints for AB#4230 archive data jobs).</param>
    /// <returns></returns>
    public static IServiceCollection AddOctoJobs(
        this IServiceCollection services,
        string tusStoragePath = "/data/tus-uploads",
        string dumpStoragePath = "/data/dumps",
        int fileRetentionHours = 4,
        string? assetServiceUrl = null)
    {
        services.AddTransient<IExportRtModelByQueryCommand, ExportRtModelByQueryByQueryCommand>();
        services.AddTransient<IExportRtModelByDeepGraphCommand, ExportRtModelByDeepGraphCommand>();
        services.AddTransient<IImportCkModelCommand, ImportCkModelCommand>();
        services.AddTransient<ICompressionService, CompressionService>();

        services.AddRepositoryUpdate();
        services.AddTransient<IImportModelJob, ImportModelJob>();
        services.AddTransient<IExportModelJob, ExportModelJob>();

        services.AddSingleton<IBackupFileStorageService>(sp =>
            new BackupFileStorageService(tusStoragePath, dumpStoragePath,
                sp.GetRequiredService<ILogger<BackupFileStorageService>>()));

        services.AddTransient<IAttributeValueAggregatorJob, AttributeValueAggregatorJob>();
        services.AddTransient<IRunFixupJob, RunFixupJob>();
        services.AddTransient<IRestoreRepositoryJob, RestoreRepositoryJob>();
        services.AddTransient<IDumpRepositoryJob, DumpRepositoryJob>();

        // Archive data export/import (AB#4230). The factory builds a per-job StreamData client
        // against the asset-repo endpoint, authenticated with the operator's forwarded bearer token.
        services.AddSingleton<IArchiveDataClientFactory>(_ => new ArchiveDataClientFactory(assetServiceUrl ?? string.Empty));
        services.AddTransient<IExportArchiveDataJob, ExportArchiveDataJob>();
        services.AddTransient<IImportArchiveDataJob, ImportArchiveDataJob>();
        services.AddTransient<ICleanupStaleFilesJob>(sp =>
            new CleanupStaleFilesJob(
                sp.GetRequiredService<ILogger<CleanupStaleFilesJob>>(),
                sp.GetRequiredService<IBackupFileStorageService>(),
                fileRetentionHours));

        return services;
    }
}