using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Secrets;
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
    /// <param name="secretBackupStoragePath">
    ///     The storage path for pre-sweep secret backups (AB#5539); <c>null</c> = <c>secret-backups</c> next to
    ///     <paramref name="dumpStoragePath" />.
    /// </param>
    /// <param name="secretBackupRetentionDays">Days to keep pre-sweep secret backups (decision 10: 7).</param>
    /// <returns></returns>
    /// <remarks>
    ///     The secret sweep reads <see cref="SecretSweepJobOptions" /> through <c>IOptions</c>; the host binds
    ///     them from <see cref="SecretSweepJobOptions.SectionName" />.
    /// </remarks>
    public static IServiceCollection AddOctoJobs(
        this IServiceCollection services,
        string tusStoragePath = "/data/tus-uploads",
        string dumpStoragePath = "/data/dumps",
        int fileRetentionHours = 4,
        string? secretBackupStoragePath = null,
        int secretBackupRetentionDays = 7)
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
                sp.GetRequiredService<ILogger<BackupFileStorageService>>(), secretBackupStoragePath));

        services.AddTransient<IAttributeValueAggregatorJob, AttributeValueAggregatorJob>();
        services.AddTransient<IRunFixupJob, RunFixupJob>();
        services.AddTransient<IRestoreRepositoryJob, RestoreRepositoryJob>();
        services.AddTransient<IDumpRepositoryJob, DumpRepositoryJob>();

        // Archive data export/import (AB#4230). The jobs access the tenant's CrateDB-backed
        // stream-data repository directly through ISystemContext (registered by the runtime engine /
        // CrateDb wiring in Program.cs), exactly like Dump/RestoreRepositoryJob access MongoDB
        // directly — no asset-repo HTTP hop, no forwarded operator token.
        services.AddTransient<IExportArchiveDataJob, ExportArchiveDataJob>();
        services.AddTransient<IImportArchiveDataJob, ImportArchiveDataJob>();
        services.AddTransient<ICleanupStaleFilesJob>(sp =>
            new CleanupStaleFilesJob(
                sp.GetRequiredService<ILogger<CleanupStaleFilesJob>>(),
                sp.GetRequiredService<IBackupFileStorageService>(),
                fileRetentionHours,
                secretBackupRetentionDays));

        // Secret sweep (AB#5539). ISecretMaintenanceService and ISecretAttributeProtector come from
        // AddRuntimeEngine().
        services.AddOptions<SecretSweepJobOptions>();
        services.AddSingleton<ISecretSweepReportStore>(_ => new HangfireSecretSweepReportStore());
        services.AddSingleton<ISecretSweepTenantLock>(_ => new HangfireSecretSweepTenantLock());
        services.AddTransient<ISecretSweepCoordinator, SecretSweepCoordinator>();
        services.AddTransient<ISecretSweepJob, SecretSweepJob>();

        return services;
    }
}