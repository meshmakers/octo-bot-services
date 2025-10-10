using Meshmakers.Common.Shared.Services;
using Meshmakers.Octo.Backend.Jobs.Commands;
using Meshmakers.Octo.Backend.Jobs.Jobs;

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
    /// <returns></returns>
    public static IServiceCollection AddOctoJobs(
        this IServiceCollection services)
    {
        services.AddTransient<IExportRtModelByQueryCommand, ExportRtModelByQueryByQueryCommand>();
        services.AddTransient<IExportRtModelByDeepGraphCommand, ExportRtModelByDeepGraphCommand>();
        services.AddTransient<IImportCkModelCommand, ImportCkModelCommand>();
        services.AddTransient<ICompressionService, CompressionService>();

        services.AddRepositoryUpdate();
        services.AddTransient<IImportModelJob, ImportModelJob>();
        services.AddTransient<IExportModelJob, ExportModelJob>();
        services.AddTransient<IServiceHookJob, ServiceHookJob>();
        services.AddTransient<IAttributeValueAggregatorJob, AttributeValueAggregatorJob>();
        services.AddTransient<IRunFixupJob, RunFixupJob>();
        services.AddTransient<IRestoreRepositoryJob, RestoreRepositoryJob>();
        services.AddTransient<IDumpRepositoryJob, DumpRepositoryJob>();
        services.AddTransient<ICompareLiveTenantsJob, CompareLiveTenantsJob>();
        services.AddTransient<ICompareLiveTenantWithBackupJob, CompareLiveTenantWithBackupJob>();
        services.AddTransient<ICompareBackupsJob, CompareBackupsJob>();

        return services;
    }
}