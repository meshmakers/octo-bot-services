using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;
using Meshmakers.Octo.Backend.Jobs.Jobs.ArchiveData;
using Meshmakers.Octo.Backend.Jobs.Secrets;
using Meshmakers.Octo.Backend.Jobs.Services;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Services.ArtifactStorage;
using Meshmakers.Octo.Services.ArtifactStorage.Configuration;
using Meshmakers.Octo.Services.ArtifactStorage.FileSystem;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    /// <param name="artifactRetentionHours">
    ///     Hours to keep tenant dumps and staged restore uploads in the artifact store (AB#5561, default 24; the
    ///     store's lifecycle rule of 1 day is the backstop).
    /// </param>
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
        int secretBackupRetentionDays = 7,
        int artifactRetentionHours = 24)
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
        // AB#5559: restore of a run's pre-sweep dump (admin, SecretManagement, confirm).
        services.AddTransient<IRestorePreSweepDumpJob, RestorePreSweepDumpJob>();

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
                secretBackupRetentionDays,
                sp.GetService<ISecretSweepRunStore>(),
                null,
                sp.GetService<IBotArtifactStorage>(),
                artifactRetentionHours));

        // Secret sweep (AB#5539). ISecretMaintenanceService and ISecretAttributeProtector come from
        // AddRuntimeEngine().
        services.AddOptions<SecretSweepJobOptions>();
        services.AddSingleton<ISecretSweepReportStore>(_ => new HangfireSecretSweepReportStore());
        services.AddSingleton<ISecretSweepTenantLock>(_ => new HangfireSecretSweepTenantLock());
        // AB#5544: run history, dump management and the environment status of the secrets admin API.
        services.AddSingleton<ISecretSweepRunStore>(_ => new HangfireSecretSweepRunStore());
        services.AddTransient<ISecretSweepRunService, SecretSweepRunService>();
        services.AddTransient<ISecretEnvironmentStatusService, SecretEnvironmentStatusService>();
        services.AddTransient<ISecretSweepCoordinator, SecretSweepCoordinator>();
        services.AddTransient<ISecretSweepJob, SecretSweepJob>();
        services.AddSingleton<ISecretSweepJobInspector>(_ => new HangfireSecretSweepJobInspector());
        services.AddTransient<SecretSweepInterruptedRunRecovery>();

        return services;
    }

    /// <summary>
    ///     Default root of the file system artifact store when <c>ArtifactStorage:FileSystem:RootPath</c> is not
    ///     configured: <c>&lt;temp&gt;/octo-bot/artifacts</c> (local development; not durable in a container).
    /// </summary>
    public static string DefaultArtifactRootPath => Path.Combine(Path.GetTempPath(), "octo-bot", "artifacts");

    /// <summary>
    ///     Registers the platform artifact store (section <c>ArtifactStorage</c>, env <c>OCTO_ARTIFACTSTORAGE__*</c>,
    ///     AB#5561) and the bot's view of it, <see cref="IBotArtifactStorage" />.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration root.</param>
    /// <param name="scratchPath">Local scratch directory for mongodump / decryption (owner-only).</param>
    /// <remarks>
    ///     Root path precedence of the <c>FileSystem</c> provider:
    ///     <list type="number">
    ///         <item><c>ArtifactStorage:FileSystem:RootPath</c> when set - all categories.</item>
    ///         <item>
    ///             otherwise, when <c>Bot:SecretSweep:BackupStoragePath</c> is set explicitly (backwards-compatible
    ///             alias, e.g. the chart's PVC stop-gap), pre-sweep dumps are stored below that path and tenant dumps /
    ///             restore staging below <see cref="DefaultArtifactRootPath" />;
    ///         </item>
    ///         <item>otherwise <see cref="DefaultArtifactRootPath" /> for all categories.</item>
    ///     </list>
    ///     With <c>S3</c> / <c>AzureBlob</c> the store holds every category; <c>BackupStoragePath</c> is then only the
    ///     place where legacy (pre-AB#5561) local dumps expire.
    /// </remarks>
    public static IServiceCollection AddOctoBotArtifactStorage(this IServiceCollection services,
        IConfiguration configuration, string scratchPath)
    {
        var section = configuration.GetSection(ArtifactStorageOptions.SectionName);
        var rootPathConfigured = !string.IsNullOrWhiteSpace(section["FileSystem:RootPath"]);
        var backupStoragePath = configuration[$"{SecretSweepJobOptions.SectionName}:BackupStoragePath"];

        services.AddArtifactStorage(configuration, options =>
        {
            if (options.Provider == ArtifactStorageProvider.FileSystem &&
                string.IsNullOrWhiteSpace(options.FileSystem.RootPath))
            {
                options.FileSystem.RootPath = DefaultArtifactRootPath;
            }
        });

        services.AddSingleton<IBotArtifactStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<ArtifactStorageOptions>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var backupFiles = sp.GetService<IBackupFileStorageService>();

            IArtifactStore? presweepStore = null;
            if (options.Provider == ArtifactStorageProvider.FileSystem)
            {
                EnsureOutsideTemporaryDirectories(options.FileSystem.RootPath!, backupFiles);
                if (!rootPathConfigured && !string.IsNullOrWhiteSpace(backupStoragePath))
                {
                    EnsureOutsideTemporaryDirectories(backupStoragePath, backupFiles);
                    presweepStore = new FileSystemArtifactStore(backupStoragePath,
                        sp.GetService<TimeProvider>(), loggerFactory.CreateLogger<FileSystemArtifactStore>());
                }
            }

            return new BotArtifactStorage(sp.GetRequiredService<IArtifactStore>(),
                sp.GetRequiredService<ArtifactKeyBuilder>(), sp.GetRequiredService<ISecretFileProtector>(),
                scratchPath, loggerFactory.CreateLogger<BotArtifactStorage>(), presweepStore);
        });

        return services;
    }

    /// <summary>
    ///     The hourly cleanup deletes everything below the tus and dump directories after a few hours; a file system
    ///     artifact root there would lose 7-day pre-sweep dumps.
    /// </summary>
    private static void EnsureOutsideTemporaryDirectories(string root, IBackupFileStorageService? backupFiles)
    {
        if (backupFiles == null)
        {
            return;
        }

        foreach (var temporary in new[] { backupFiles.TusStoragePath, backupFiles.DumpStoragePath })
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var temporaryFull = Path.GetFullPath(temporary).TrimEnd(Path.DirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
            if (rootFull.StartsWith(temporaryFull, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The artifact storage root '{root}' must not lie inside the tus upload or dump directory " +
                    $"'{temporary}' (cleaned up after Bot:FileRetentionHours).");
            }
        }
    }

    /// <summary>
    ///     Adds the startup check that marks secret sweep runs left in <c>Running</c> by an ended process as
    ///     <c>Failed</c> ("Interrupted (service restart)", AB#5539). Register it in the host that runs the
    ///     Hangfire server, after <see cref="AddOctoJobs" />.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddOctoSecretSweepInterruptedRunRecovery(this IServiceCollection services)
    {
        services.AddHostedService<SecretSweepInterruptedRunRecoveryHostedService>();
        return services;
    }
}