using RepositoryUpdate.Services;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Extensions for dependency injection's service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds repository update services to the service collection
    /// </summary>
    /// <param name="services"></param>
    public static IServiceCollection AddRepositoryUpdate(this IServiceCollection services)
    {
        services.AddTransient<IRepositoryFixupService, RepositoryFixupService>();

        return services;
    }
}