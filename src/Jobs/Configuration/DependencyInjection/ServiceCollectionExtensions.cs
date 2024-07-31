using Meshmakers.Octo.Backend.Jobs.Commands;

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
    public static IServiceCollection AddOctoCommands(
        this IServiceCollection services)
    {
        services.AddTransient<IExportRtModelByQueryCommand, ExportRtModelByQueryByQueryCommand>();
        services.AddTransient<IExportRtModelByDeepGraphCommand, ExportRtModelByDeepGraphCommand>();
        services.AddTransient<IImportCkModelCommand, ImportCkModelCommand>();
        services.AddTransient<IImportRtModelCommand, ImportRtModelCommand>();

        return services;
    }
}