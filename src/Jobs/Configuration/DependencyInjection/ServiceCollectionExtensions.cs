using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
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
    /// <returns></returns>
    public static IServiceCollection AddOctoJobs(
        this IServiceCollection services,
        string tusStoragePath = "/data/tus-uploads",
        string dumpStoragePath = "/data/dumps",
        int fileRetentionHours = 4)
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
        services.AddTransient<ICleanupStaleFilesJob>(sp =>
            new CleanupStaleFilesJob(
                sp.GetRequiredService<ILogger<CleanupStaleFilesJob>>(),
                sp.GetRequiredService<IBackupFileStorageService>(),
                fileRetentionHours));

        return services;
    }
}